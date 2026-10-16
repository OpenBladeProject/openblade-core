using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionAudioPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoverDispatchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadDispatchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationDispatchTimeout = TimeSpan.FromSeconds(15);

    private readonly SessionAgentPipeServer<SessionAudioRequest, SessionAudioResponse> server;

    public SessionAudioPipeServer(
        SessionAgentPipeOwner owner,
        SessionAudioRequestDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        server = new SessionAgentPipeServer<SessionAudioRequest, SessionAudioResponse>(
            owner,
            SessionAudioIpcCodec.ReadRequestAsync,
            SessionAudioIpcCodec.WriteResponseAsync,
            dispatcher.DispatchAsync,
            new SessionAgentPipeServerPolicy<SessionAudioRequest, SessionAudioResponse>(
                FrameReadTimeout,
                FrameWriteTimeout,
                static request => request switch
                {
                    DiscoverThxAudioRequest => DiscoverDispatchTimeout,
                    ReadThxAudioRequest or ConnectThxAudioRequest => ReadDispatchTimeout,
                    _ => MutationDispatchTimeout,
                },
                DispatchFailure: null),
            timeProvider);
    }

    public Task RunAsync(CancellationToken cancellationToken) => server.RunAsync(cancellationToken);

    public Task ServeConnectionAsync(Stream connection, CancellationToken cancellationToken) =>
        server.ServeConnectionAsync(connection, cancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
