using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed class ServiceClient : IServiceClient
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly Func<CancellationToken, ValueTask<Stream>> connectAsync;
    private readonly TimeProvider timeProvider;

    public ServiceClient()
        : this(OpenBladeIpc.PipeName)
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
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        ServiceResponse response = await SendOnceAsync(request, token);
        return response.Status
            ?? throw new InvalidDataException("Service status response is missing its status body.");
    }

    public async Task<ServiceStatusSnapshot> ObserveStateAsync(
        CancellationToken cancellationToken)
    {
        var request = new ObserveStateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid());
        ServiceResponse response = await SendOnceAsync(request, cancellationToken);
        return response.Status
            ?? throw new InvalidDataException(
                "Service observation response is missing its status body.");
    }

    public async Task<DiagnosticArchive> ExportDiagnosticsAsync(CancellationToken token)
    {
        var request = new ExportDiagnosticsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid());
        ServiceResponse response = await SendOnceAsync(request, token);
        return response.Archive
            ?? throw new InvalidDataException(
                "Service diagnostic response is missing its archive body.");
    }

    public Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token) => SendMutationAsync(
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles),
            token);

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) => SendMutationAsync(
            new SetPerformanceOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                value),
            token);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => SendMutationAsync(
            new SetFanOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => SendMutationAsync(
            new SetLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token) => SendMutationAsync(
            new SetBatteryPolicyRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), value),
            token);

    public Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetBatteryProtectionRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token) => SendMutationAsync(
            new SetFullChargeOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                enabled),
            token);

    public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        TimeSpan reconnectDelay = InitialReconnectDelay;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            SubscriptionOpenResult openResult = await OpenSubscriptionAsync(token);
            if (openResult.TransportFailed)
            {
                yield return new ServiceWatchUpdate.Reconnecting();
                await Task.Delay(reconnectDelay, timeProvider, token);
                reconnectDelay = NextDelay(reconnectDelay);
                continue;
            }

            await using (Stream connection = openResult.Connection!)
            {
                reconnectDelay = InitialReconnectDelay;
                yield return new ServiceWatchUpdate.Connected(openResult.InitialStatus!);

                while (true)
                {
                    SubscriptionReadResult readResult = await ReadSubscriptionFrameAsync(
                        connection,
                        token);
                    if (readResult.TransportFailed)
                    {
                        break;
                    }

                    if (readResult.Frame is not ServiceStatusEvent statusEvent)
                    {
                        throw new InvalidDataException(
                            "Service returned an unexpected subscription frame.");
                    }

                    if (statusEvent.Version != OpenBladeIpc.ProtocolVersion)
                    {
                        throw new InvalidDataException(
                            "Service status event version does not match the client.");
                    }

                    yield return new ServiceWatchUpdate.Connected(statusEvent.Status);
                }
            }

            yield return new ServiceWatchUpdate.Reconnecting();
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

        if (response.Version != OpenBladeIpc.ProtocolVersion)
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

    private async Task<SubscriptionOpenResult> OpenSubscriptionAsync(
        CancellationToken token)
    {
        Stream? connection = null;
        try
        {
            connection = await connectAsync(token);
            var request = new SubscribeStatusRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid());
            await IpcMessageCodec.WriteRequestAsync(connection, request, token);
            ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(
                connection,
                token);
            ServiceResponse response = ValidateResponse(frame, request);
            ServiceStatusSnapshot initial = response.Status
                ?? throw new InvalidDataException(
                    "Service subscription response is missing its status body.");
            return new SubscriptionOpenResult(connection, initial, false);
        }
        catch (Exception exception) when (
            !token.IsCancellationRequested
            && IsRetryableTransportFailure(exception))
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            return new SubscriptionOpenResult(null, null, true);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            throw;
        }
    }

    private static async Task<SubscriptionReadResult> ReadSubscriptionFrameAsync(
        Stream connection,
        CancellationToken token)
    {
        try
        {
            return new SubscriptionReadResult(
                await IpcMessageCodec.ReadServerFrameAsync(connection, token),
                false);
        }
        catch (Exception exception) when (
            !token.IsCancellationRequested
            && IsRetryableTransportFailure(exception))
        {
            return new SubscriptionReadResult(null, true);
        }
    }

    private static bool IsRetryableTransportFailure(Exception exception) =>
        exception is IOException && exception is not InvalidDataException;

    private sealed record SubscriptionOpenResult(
        Stream? Connection,
        ServiceStatusSnapshot? InitialStatus,
        bool TransportFailed);

    private sealed record SubscriptionReadResult(
        ServiceServerFrame? Frame,
        bool TransportFailed);

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
