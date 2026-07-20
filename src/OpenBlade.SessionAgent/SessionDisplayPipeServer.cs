using System.IO.Pipes;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionDisplayPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionAgentPipeOwner owner;
    private readonly SessionDisplayRequestDispatcher dispatcher;
    private readonly TimeProvider timeProvider;
    private NamedPipeServerStream? currentPipe;
    private int disposed;

    public SessionDisplayPipeServer(
        SessionAgentPipeOwner owner,
        SessionDisplayRequestDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
            request = await ReadRequestAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or TimeoutException)
        {
            // A malformed, truncated, or stalled frame has no trustworthy
            // correlation ID. Close only this connection.
            return;
        }

        SessionDisplayResponse response = await dispatcher.DispatchAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(FrameWriteTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        await SessionDisplayIpcCodec.WriteResponseAsync(
            connection,
            response,
            linked.Token).ConfigureAwait(false);
    }

    private async Task<SessionDisplayRequest> ReadRequestAsync(
        Stream connection,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(FrameReadTimeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            return await SessionDisplayIpcCodec.ReadRequestAsync(
                connection,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Display IPC frame read exceeded two seconds.", exception);
        }
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
