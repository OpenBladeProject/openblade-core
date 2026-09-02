using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionGpuPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(20);

    private readonly SessionAgentPipeServer<SessionGpuRequest, SessionGpuResponse> server;

    public SessionGpuPipeServer(
        SessionAgentPipeOwner owner,
        SessionGpuRequestDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        server = new SessionAgentPipeServer<SessionGpuRequest, SessionGpuResponse>(
            owner,
            SessionGpuIpcCodec.ReadRequestAsync,
            SessionGpuIpcCodec.WriteResponseAsync,
            dispatcher.DispatchAsync,
            new SessionAgentPipeServerPolicy<SessionGpuRequest, SessionGpuResponse>(
                FrameTimeout,
                FrameTimeout,
                static _ => DispatchTimeout,
                // GPU app scans and process actions touch third-party
                // processes; an unexpected failure must still answer the
                // caller so the Tray can report it instead of timing out.
                static (request, _) => new SessionGpuResponse(
                    SessionGpuIpc.ProtocolVersion,
                    request.RequestId,
                    SessionGpuOperationStatus.Failed,
                    "The Session Agent could not complete the GPU app operation.",
                    [],
                    [])),
            timeProvider);
    }

    public Task RunAsync(CancellationToken cancellationToken) => server.RunAsync(cancellationToken);

    public Task ServeConnectionAsync(Stream connection, CancellationToken cancellationToken) =>
        server.ServeConnectionAsync(connection, cancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
