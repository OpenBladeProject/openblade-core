using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionDisplayPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QueryDispatchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MutationDispatchTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionAgentPipeServer<SessionDisplayRequest, SessionDisplayResponse> server;

    public SessionDisplayPipeServer(
        SessionAgentPipeOwner owner,
        SessionDisplayRequestDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        server = new SessionAgentPipeServer<SessionDisplayRequest, SessionDisplayResponse>(
            owner,
            SessionDisplayIpcCodec.ReadRequestAsync,
            SessionDisplayIpcCodec.WriteResponseAsync,
            dispatcher.DispatchAsync,
            new SessionAgentPipeServerPolicy<SessionDisplayRequest, SessionDisplayResponse>(
                FrameReadTimeout,
                FrameWriteTimeout,
                // Queries answer from cached display state; mutations drive
                // native display reconfiguration and may legitimately take longer.
                static request => request is QueryDisplayRequest
                    ? QueryDispatchTimeout
                    : MutationDispatchTimeout,
                // The display dispatcher maps every failure to a typed
                // response itself, so an escaping exception is a defect worth
                // surfacing rather than masking.
                DispatchFailure: null),
            timeProvider);
    }

    public Task RunAsync(CancellationToken cancellationToken) => server.RunAsync(cancellationToken);

    public Task ServeConnectionAsync(Stream connection, CancellationToken cancellationToken) =>
        server.ServeConnectionAsync(connection, cancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
