using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public sealed class ServiceClient : IServiceClient
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly Func<CancellationToken, ValueTask<Stream>> connectAsync;
    private readonly TimeProvider timeProvider;

    public ServiceClient()
        : this(OpenSynapseIpc.PipeName)
    {
    }

    public ServiceClient(string pipeName, TimeProvider? timeProvider = null)
        : this(
            token => ConnectNamedPipeAsync(
                string.IsNullOrWhiteSpace(pipeName)
                    ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
                    : pipeName,
                token),
            timeProvider ?? TimeProvider.System)
    {
    }

    public ServiceClient(
        Func<CancellationToken, ValueTask<Stream>> connectAsync,
        TimeProvider timeProvider)
    {
        this.connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
    {
        var request = new GetStatusRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid());
        ServiceResponse response = await SendOnceAsync(request, token);
        return response.Status
            ?? throw new InvalidDataException("Service status response is missing its status body.");
    }

    public Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token) => SendMutationAsync(
            new UpdateProfilesRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), profiles),
            token);

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) => SendMutationAsync(
            new SetPerformanceOverrideRequest(
                OpenSynapseIpc.ProtocolVersion,
                Guid.NewGuid(),
                value),
            token);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => SendMutationAsync(
            new SetFanOverrideRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => SendMutationAsync(
            new SetLightingOverrideRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token) => SendMutationAsync(
            new SetBatteryPolicyRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetFullChargeOverrideRequest(
                OpenSynapseIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public async IAsyncEnumerable<ServiceStatusSnapshot> WatchStatusAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        TimeSpan reconnectDelay = InitialReconnectDelay;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Stream? connection = null;
            ServiceStatusSnapshot? initial = null;
            try
            {
                connection = await connectAsync(token);
                var request = new SubscribeStatusRequest(
                    OpenSynapseIpc.ProtocolVersion,
                    Guid.NewGuid());
                await IpcMessageCodec.WriteRequestAsync(connection, request, token);
                ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(
                    connection,
                    token);
                ServiceResponse response = ValidateResponse(frame, request);
                initial = response.Status
                    ?? throw new InvalidDataException(
                        "Service subscription response is missing its status body.");
            }
            catch (IOException) when (!token.IsCancellationRequested)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }

                await Task.Delay(reconnectDelay, timeProvider, token);
                reconnectDelay = NextDelay(reconnectDelay);
                continue;
            }

            await using (connection)
            {
                reconnectDelay = InitialReconnectDelay;
                yield return initial;

                bool disconnected = false;
                while (!disconnected)
                {
                    ServiceServerFrame? frame = null;
                    try
                    {
                        frame = await IpcMessageCodec.ReadServerFrameAsync(connection, token);
                    }
                    catch (IOException) when (!token.IsCancellationRequested)
                    {
                        disconnected = true;
                    }

                    if (disconnected)
                    {
                        break;
                    }

                    if (frame is not ServiceStatusEvent statusEvent)
                    {
                        throw new InvalidDataException(
                            "Service returned an unexpected subscription frame.");
                    }

                    yield return statusEvent.Status;
                }
            }

            await Task.Delay(reconnectDelay, timeProvider, token);
            reconnectDelay = NextDelay(reconnectDelay);
        }
    }

    private async Task<ControlOperationResult> SendMutationAsync(
        ServiceRequest request,
        CancellationToken token)
    {
        ServiceResponse response = await SendOnceAsync(request, token);
        return response.Result
            ?? throw new InvalidDataException("Service mutation response is missing its result body.");
    }

    private async Task<ServiceResponse> SendOnceAsync(
        ServiceRequest request,
        CancellationToken token)
    {
        await using Stream connection = await connectAsync(token);
        await IpcMessageCodec.WriteRequestAsync(connection, request, token);
        ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(connection, token);
        return ValidateResponse(frame, request);
    }

    private static ServiceResponse ValidateResponse(
        ServiceServerFrame frame,
        ServiceRequest request)
    {
        if (frame is not ServiceResponse response)
        {
            throw new InvalidDataException("Service returned an unexpected server frame.");
        }

        if (response.Version != OpenSynapseIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Service response version does not match the client.");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("Service response request ID does not match the request.");
        }

        if (!response.Success)
        {
            throw new InvalidDataException(response.Error ?? "Service rejected the request.");
        }

        return response;
    }

    private static TimeSpan NextDelay(TimeSpan current) => TimeSpan.FromMilliseconds(
        Math.Min(current.TotalMilliseconds * 2, MaximumReconnectDelay.TotalMilliseconds));

    private static async ValueTask<Stream> ConnectNamedPipeAsync(
        string pipeName,
        CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(token);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
