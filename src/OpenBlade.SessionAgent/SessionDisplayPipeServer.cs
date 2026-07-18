using System.IO.Pipes;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionDisplayPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionAgentPipeOwner owner;
    private readonly SessionDisplayRequestDispatcher dispatcher;
    private NamedPipeServerStream? currentPipe;
    private int disposed;

    public SessionDisplayPipeServer(
        SessionAgentPipeOwner owner,
        SessionDisplayRequestDispatcher dispatcher)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        currentPipe = owner.TakeInitialPipe();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream connected = currentPipe
                ?? throw new ObjectDisposedException(nameof(SessionDisplayPipeServer));
            await connected.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Preserve ownership without a gap while the accepted request is handled.
            NamedPipeServerStream replacement = owner.CreateReplacementPipe();
            currentPipe = replacement;
            try
            {
                await ServeConnectionAsync(connected, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await connected.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task ServeConnectionAsync(
        Stream connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SessionDisplayRequest request;
        try
        {
            request = await SessionDisplayIpcCodec.ReadRequestAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // A malformed frame has no trustworthy correlation ID. Close only
            // this connection rather than synthesizing a misleading response.
            return;
        }

        SessionDisplayResponse response = await dispatcher.DispatchAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(FrameWriteTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        await SessionDisplayIpcCodec.WriteResponseAsync(
            connection,
            response,
            linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        NamedPipeServerStream? pipe = Interlocked.Exchange(ref currentPipe, null);
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        await owner.DisposeAsync().ConfigureAwait(false);
    }
}
