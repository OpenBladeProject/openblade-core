using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.Tray;

internal sealed class SessionGpuAppAdapter
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(25);
    private readonly string pipeName;

    internal SessionGpuAppAdapter()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        pipeName = SessionAgentPipeEndpoint.CreateGpu(userSid, process.SessionId).PipeName;
    }

    internal Task<SessionGpuResponse> QueryAsync(CancellationToken cancellationToken) =>
        SendAsync(
            requestId => new QueryGpuAppsRequest(
                SessionGpuIpc.ProtocolVersion,
                requestId),
            cancellationToken);

    internal Task<SessionGpuResponse> CloseAsync(
        IReadOnlyList<SessionGpuApp> apps,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apps);
        return SendAsync(
            requestId => new CloseGpuAppsRequest(
                SessionGpuIpc.ProtocolVersion,
                requestId,
                force,
                apps.Select(app => new SessionGpuAppIdentity(
                    app.ProcessId,
                    app.StartTimeUtcTicks,
                    app.Name)).ToArray()),
            cancellationToken);
    }

    private async Task<SessionGpuResponse> SendAsync(
        Func<Guid, SessionGpuRequest> createRequest,
        CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using (var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken))
        {
            connectionDeadline.CancelAfter(ConnectionTimeout);
            await pipe.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
        }

        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        operationDeadline.CancelAfter(OperationTimeout);
        SessionGpuRequest request = createRequest(requestId);
        await SessionGpuIpcCodec.WriteRequestAsync(
            pipe,
            request,
            operationDeadline.Token).ConfigureAwait(false);
        SessionGpuResponse response = await SessionGpuIpcCodec.ReadResponseAsync(
            pipe,
            operationDeadline.Token).ConfigureAwait(false);
        if (response.RequestId != requestId)
        {
            throw new InvalidDataException(
                "Session-agent GPU response ID does not match the request.");
        }
        return response;
    }
}
