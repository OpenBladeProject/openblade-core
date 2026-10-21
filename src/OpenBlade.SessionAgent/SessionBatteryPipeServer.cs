using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionBatteryPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(20);

    private readonly SessionAgentPipeServer<SessionBatteryRequest, SessionBatteryResponse> server;

    public SessionBatteryPipeServer(
        SessionAgentPipeOwner owner,
        SessionBatteryRequestDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        server = new SessionAgentPipeServer<SessionBatteryRequest, SessionBatteryResponse>(
            owner,
            SessionBatteryIpcCodec.ReadRequestAsync,
            SessionBatteryIpcCodec.WriteResponseAsync,
            dispatcher.DispatchAsync,
            new SessionAgentPipeServerPolicy<SessionBatteryRequest, SessionBatteryResponse>(
                FrameTimeout,
                FrameTimeout,
                static _ => DispatchTimeout,
                static (request, _) => new SessionBatteryResponse(
                    SessionBatteryIpc.ProtocolVersion,
                    request.RequestId,
                    SessionBatteryOperationStatus.Failed,
                    "The Session Agent could not read the Windows battery report.",
                    null)),
            timeProvider);
    }

    public Task RunAsync(CancellationToken cancellationToken) => server.RunAsync(cancellationToken);

    public Task ServeConnectionAsync(Stream connection, CancellationToken cancellationToken) =>
        server.ServeConnectionAsync(connection, cancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
