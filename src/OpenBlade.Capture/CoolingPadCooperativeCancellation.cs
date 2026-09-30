using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// A developer idle wait, armed only after all four HID exchanges have completed.
// Canceling this token proves cooperative cancellation between operations; it
// makes no assertion about in-flight HID cancellation or abrupt process loss.
internal sealed class CoolingPadCooperativeCancellation : IDisposable
{
    private readonly CancellationTokenSource work = new();
    private readonly Task waiting;
    private readonly PendingDisposalRegistry retained;
    private Task? cancellation;
    private int requested;
    private int disposed;

    internal CoolingPadCooperativeCancellation(PendingDisposalRegistry? retained = null)
    {
        this.retained = retained ?? PendingDisposalRegistry.Shared;
        waiting = Task.Delay(Timeout.InfiniteTimeSpan, work.Token);
    }
    internal bool Armed => !waiting.IsCompleted && !work.IsCancellationRequested;
    internal CancellationToken WorkToken => work.Token;

    internal async Task ObserveAsync(CancellationToken controlToken, TimeSpan? timeout = null)
    {
        controlToken.ThrowIfCancellationRequested();
        if (!Armed || Interlocked.Exchange(ref requested, 1) != 0)
        { throw new InvalidOperationException("Work cancellation is not armed or was already requested."); }
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        cancellation = work.CancelAsync();
        await cancellation.WaitAsync(limit, controlToken).ConfigureAwait(false);
        try
        {
            await waiting.WaitAsync(limit, controlToken).ConfigureAwait(false);
            throw new InvalidOperationException("Work cancellation was not observed.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == work.Token
            && work.IsCancellationRequested && !controlToken.IsCancellationRequested)
        {
            // Receipt is eligible only after this actual canceled idle wait unwinds.
        }
        controlToken.ThrowIfCancellationRequested();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        retained.Retain(FinishAsync());
    }
    private async Task FinishAsync()
    {
        try
        {
            await (cancellation ?? work.CancelAsync()).ConfigureAwait(false);
            try { await waiting.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        finally { work.Dispose(); }
    }
}
