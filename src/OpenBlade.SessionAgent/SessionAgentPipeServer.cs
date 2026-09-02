using System.Diagnostics;
using System.IO.Pipes;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Shared connection handling for the Session Agent named pipes. Each
/// protocol supplies its codec, dispatcher, and deadline policy; the accept
/// loop, replacement-pipe ownership, bounded frame and dispatch deadlines,
/// and per-connection error containment are implemented once so a pipe fix
/// cannot land on one server and miss the other.
/// </summary>
internal sealed class SessionAgentPipeServer<TRequest, TResponse> : IAsyncDisposable
    where TRequest : class
{
    private readonly SessionAgentPipeOwner owner;
    private readonly Func<Stream, CancellationToken, Task<TRequest>> readRequest;
    private readonly Func<Stream, TResponse, CancellationToken, Task> writeResponse;
    private readonly Func<TRequest, CancellationToken, Task<TResponse>> dispatch;
    private readonly SessionAgentPipeServerPolicy<TRequest, TResponse> policy;
    private readonly TimeProvider timeProvider;
    private NamedPipeServerStream? currentPipe;
    private int disposed;

    internal SessionAgentPipeServer(
        SessionAgentPipeOwner owner,
        Func<Stream, CancellationToken, Task<TRequest>> readRequest,
        Func<Stream, TResponse, CancellationToken, Task> writeResponse,
        Func<TRequest, CancellationToken, Task<TResponse>> dispatch,
        SessionAgentPipeServerPolicy<TRequest, TResponse> policy,
        TimeProvider? timeProvider)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.readRequest = readRequest ?? throw new ArgumentNullException(nameof(readRequest));
        this.writeResponse = writeResponse
            ?? throw new ArgumentNullException(nameof(writeResponse));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        currentPipe = owner.TakeInitialPipe();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream connected = currentPipe
                ?? throw new ObjectDisposedException(GetType().Name);
            SessionAgentPipeAcceptResult acceptResult =
                await SessionAgentPipeAccept.AcceptAsync(
                        connected.WaitForConnectionAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (acceptResult == SessionAgentPipeAcceptResult.Canceled)
            {
                return;
            }

            // Preserve ownership without a gap while the accepted request is handled.
            currentPipe = owner.CreateReplacementPipe();
            if (acceptResult == SessionAgentPipeAcceptResult.PipeClosed)
            {
                await connected.DisposeAsync().ConfigureAwait(false);
                continue;
            }
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
        TRequest request;
        using (var readDeadline = new CancellationTokenSource(policy.FrameReadTimeout, timeProvider))
        using (CancellationTokenSource readToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            readDeadline.Token))
        {
            try
            {
                request = await readRequest(connection, readToken.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException)
            {
                // A malformed or truncated frame has no trustworthy
                // correlation ID. Close only this connection.
                return;
            }
            catch (OperationCanceledException) when (
                readDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // A stalled frame ends only this connection.
                return;
            }
        }

        TResponse response;
        using (var dispatchDeadline = new CancellationTokenSource(
            policy.DispatchTimeout(request),
            timeProvider))
        using (CancellationTokenSource dispatchToken =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                dispatchDeadline.Token))
        {
            try
            {
                response = await dispatch(request, dispatchToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                dispatchDeadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // A stalled native operation ends this request without
                // terminating the session agent or blocking later clients.
                return;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && policy.DispatchFailure is { } failureResponse)
            {
                Trace.TraceError("Session pipe dispatch failed: {0}", exception);
                response = failureResponse(request, exception);
            }
        }

        using var writeDeadline = new CancellationTokenSource(policy.FrameWriteTimeout, timeProvider);
        using CancellationTokenSource writeToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            writeDeadline.Token);
        try
        {
            await writeResponse(connection, response, writeToken.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // The client can close after submitting a valid request but before
            // reading the reply. That ends only this connection, not the agent.
        }
        catch (OperationCanceledException) when (
            writeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A client that stops reading must not hold the server or
            // terminate the other session-agent features.
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

/// <summary>
/// The per-protocol choices the shared pipe server makes explicit: frame
/// deadlines, the dispatch deadline for a given request, and whether an
/// unexpected dispatcher exception is answered with a typed failure response
/// (<see langword="null"/> lets it propagate, for dispatchers that already
/// map every failure themselves).
/// </summary>
internal sealed record SessionAgentPipeServerPolicy<TRequest, TResponse>(
    TimeSpan FrameReadTimeout,
    TimeSpan FrameWriteTimeout,
    Func<TRequest, TimeSpan> DispatchTimeout,
    Func<TRequest, Exception, TResponse>? DispatchFailure);
