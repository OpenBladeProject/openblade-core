using System.Buffers.Binary;

namespace OpenBlade.Capture;

internal enum CpuPmListenerResult { ChannelClosed, Denied, BudgetExpired, Canceled, Failed, AlreadyUsed }

// The listener owns the transport and drains pending I/O before closing it. It
// does not own the supervisor's process/driver/payload pins or perform cleanup.
internal interface ICpuPmAuthorityListenerTransport : IDisposable
{
    Stream Stream { get; }
    Task WaitForConnectionAsync(CancellationToken token);
    ICpuPmAuthorityChannel BindRetainedChild(CpuPmAuthorityPeer expectedChild);
}

internal sealed class CpuPmAuthorityListenerSession : IAsyncDisposable
{
    private readonly Guid run, lifetime;
    private readonly ICpuPmAuthorityListenerTransport transport;
    private readonly ICpuPmAuthorityClock clock;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int used, disposed;
    internal CpuPmAuthorityListenerSession(Guid run, Guid lifetime, ICpuPmAuthorityListenerTransport transport, ICpuPmAuthorityClock clock)
    {
        if (run == Guid.Empty || lifetime == Guid.Empty) { throw new ArgumentException("Run and lifetime required."); }
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        this.run = run; this.lifetime = lifetime; this.transport = transport; this.clock = clock;
    }
    internal async Task<CpuPmListenerResult> ServeOnceAsync(CpuPmAuthorityPeer child, ICpuPmOwnedDriverProofSource? proof, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref used, 1, 0) != 0) { return CpuPmListenerResult.AlreadyUsed; }
        ICpuPmAuthorityChannel? channel = null;
        CpuPmParentAuthoritySession? session = null;
        try
        {
            if (!child.IsValid) { return CpuPmListenerResult.Denied; }
            using var lifetimeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            lifetimeDeadline.CancelAfter(CpuPmAuthorityProtocol.SessionBudget);
            TimeSpan started = clock.Now;
            lifetimeDeadline.Token.ThrowIfCancellationRequested();
            await AwaitAndDrainAsync(transport.WaitForConnectionAsync(lifetimeDeadline.Token), lifetimeDeadline.Token).ConfigureAwait(false);
            lifetimeDeadline.Token.ThrowIfCancellationRequested();
            if (clock.Now < started || clock.Now - started >= CpuPmAuthorityProtocol.SessionBudget) { return CpuPmListenerResult.BudgetExpired; }
            channel = transport.BindRetainedChild(child);
            lifetimeDeadline.Token.ThrowIfCancellationRequested();
            session = new CpuPmParentAuthoritySession(run, lifetime, child, channel, proof, clock);
            using var revoke = lifetimeDeadline.Token.Register(session.Revoke);
            return await RunLoopAsync(transport.Stream, session, clock, started, lifetimeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return CpuPmListenerResult.Canceled; }
        catch (Exception) { return CpuPmListenerResult.Failed; }
        finally
        {
            session?.Revoke();
            try { channel?.Dispose(); }
            finally
            {
                try { transport.Dispose(); }
                finally { drained.TrySetResult(); }
            }
        }
    }

    internal static async Task<CpuPmListenerResult> RunLoopAsync(Stream stream, CpuPmParentAuthoritySession session,
        ICpuPmAuthorityClock clock, TimeSpan started, CancellationToken lifetimeToken)
    {
        TimeSpan previous = started;
        try
        {
            while (true)
            {
                lifetimeToken.ThrowIfCancellationRequested();
                if (!SessionClockValid()) { return CpuPmListenerResult.BudgetExpired; }
                byte[] prefix = new byte[4];
                // Idle waiting is governed by the session, not the two-second
                // request timer. F08 may hold the child without traffic for 40s.
                int read = await AwaitAndDrainAsync(stream.ReadAsync(prefix.AsMemory(0, 1), lifetimeToken).AsTask(), lifetimeToken).ConfigureAwait(false);
                if (read == 0) { return CpuPmListenerResult.ChannelClosed; }
                if (!SessionClockValid()) { return CpuPmListenerResult.BudgetExpired; }
                TimeSpan requestStarted = clock.Now;
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                requestDeadline.CancelAfter(CpuPmAuthorityProtocol.RequestBudget);
                using var revoke = requestDeadline.Token.Register(session.Revoke);
                CancellationToken token = requestDeadline.Token;
                await AwaitAndDrainAsync(stream.ReadExactlyAsync(prefix.AsMemory(1, 3), token).AsTask(), token).ConfigureAwait(false);
                if (!RequestClockValid()) { return CpuPmListenerResult.BudgetExpired; }
                int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is <= 0 or > CpuPmAuthorityProtocol.MaximumFrameBytes) { return CpuPmListenerResult.Denied; }
                byte[] body = new byte[length];
                await AwaitAndDrainAsync(stream.ReadExactlyAsync(body, token).AsTask(), token).ConfigureAwait(false);
                if (!RequestClockValid()) { return CpuPmListenerResult.BudgetExpired; }
                byte[]? response = session.Handle(body, token);
                if (response is null) { return CpuPmListenerResult.Denied; }
                if (!RequestClockValid()) { return CpuPmListenerResult.BudgetExpired; }
                await AwaitAndDrainAsync(WindowsCpuPmAuthorityChannel.WriteFrameAsync(stream, response, token), token).ConfigureAwait(false);
                if (!RequestClockValid()) { return CpuPmListenerResult.BudgetExpired; }

                bool RequestClockValid() => !token.IsCancellationRequested && SessionClockValid()
                    && clock.Now - requestStarted < CpuPmAuthorityProtocol.RequestBudget;
            }
        }
        catch (OperationCanceledException) { return CpuPmListenerResult.Canceled; }
        catch (Exception) { return CpuPmListenerResult.Failed; }
        finally { session.Revoke(); }

        bool SessionClockValid()
        {
            TimeSpan now = clock.Now;
            if (now < previous || now - started >= CpuPmAuthorityProtocol.SessionBudget) { return false; }
            previous = now; return true;
        }
    }

    private static async Task AwaitAndDrainAsync(Task operation, CancellationToken token)
    {
        try { await operation.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation requests pipe I/O cancellation; it does not prove
            // completion. Retain the stream and buffers until this SAME task ends.
            try { await operation.ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
    private static async Task<T> AwaitAndDrainAsync<T>(Task<T> operation, CancellationToken token)
    {
        await AwaitAndDrainAsync((Task)operation, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await operation.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await drained.Task.ConfigureAwait(false); return; }
        stop.Cancel();
        if (Interlocked.CompareExchange(ref used, 1, 0) == 0)
        {
            try { transport.Dispose(); } finally { drained.TrySetResult(); }
        }
        await drained.Task.ConfigureAwait(false);
        stop.Dispose();
    }
}
