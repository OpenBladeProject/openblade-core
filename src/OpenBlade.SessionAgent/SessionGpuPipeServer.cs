using System.Diagnostics;
using System.IO.Pipes;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionGpuPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(20);
    private readonly SessionAgentPipeOwner owner;
    private readonly SessionGpuRequestDispatcher dispatcher;
    private readonly TimeProvider timeProvider;
    private NamedPipeServerStream? currentPipe;
    private int disposed;

    public SessionGpuPipeServer(
        SessionAgentPipeOwner owner,
        SessionGpuRequestDispatcher dispatcher,
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
                ?? throw new ObjectDisposedException(nameof(SessionGpuPipeServer));
            if (!await SessionAgentPipeOwner.WaitForConnectionAsync(
                    connected,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            currentPipe = owner.CreateReplacementPipe();
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

    public async Task ServeConnectionAsync(Stream connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SessionGpuRequest request;
        using (var readDeadline = new CancellationTokenSource(FrameTimeout, timeProvider))
        using (CancellationTokenSource readToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readDeadline.Token))
        {
            try
            {
                request = await SessionGpuIpcCodec.ReadRequestAsync(
                    connection,
                    readToken.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                    or IOException
                    or OperationCanceledException)
            {
                return;
            }
        }

        SessionGpuResponse response;
        using (var dispatchDeadline = new CancellationTokenSource(DispatchTimeout, timeProvider))
        using (CancellationTokenSource dispatchToken =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                dispatchDeadline.Token))
        {
            try
            {
                response = await dispatcher.DispatchAsync(
                    request,
                    dispatchToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Session GPU dispatch failed: {exception}");
                response = new SessionGpuResponse(
                    SessionGpuIpc.ProtocolVersion,
                    request.RequestId,
                    SessionGpuOperationStatus.Failed,
                    "The Session Agent could not complete the GPU app operation.",
                    [],
                    []);
            }
        }

        using var writeDeadline = new CancellationTokenSource(FrameTimeout, timeProvider);
        using CancellationTokenSource writeToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            writeDeadline.Token);
        try
        {
            await SessionGpuIpcCodec.WriteResponseAsync(
                connection,
                response,
                writeToken.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or OperationCanceledException)
        {
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
