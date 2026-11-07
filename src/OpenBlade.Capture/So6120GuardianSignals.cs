namespace OpenBlade.Capture;

// Only liveness and stop requests cross the caller boundary. Approval and all
// hardware operands stay in the attended owner. This latch also covers the
// interval before the prepared session is transferred to the guardian worker
// and remains the sole caller lease when attached to it.
internal sealed class So6120GuardianSignals : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lease;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource firstHeartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer timer;
    private long renewed;
    private bool disposed;
    private Task cancellation = Task.CompletedTask;
    private So6120GuardianStopReason reason;
    private So6120TrialGuardian? guardian;

    public So6120GuardianSignals(TimeProvider? clock = null, TimeSpan? lease = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.lease = lease ?? TimeSpan.FromSeconds(5);
        if (this.lease < TimeSpan.FromMilliseconds(100) || this.lease > TimeSpan.FromSeconds(30))
        { throw new ArgumentOutOfRangeException(nameof(lease)); }
        renewed = this.clock.GetTimestamp();
        timer = this.clock.CreateTimer(_ => CheckDeadline(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
    }

    public CancellationToken StopToken => stop.Token;
    public Task FirstHeartbeat => firstHeartbeat.Task;
    public So6120GuardianStopReason StopReason { get { lock (sync) { return reason; } } }

    public bool Receive(byte command)
    {
        lock (sync)
        {
            if (disposed) { return false; }
            long now = clock.GetTimestamp();
            CheckDeadlineLocked(now);
            if (reason != So6120GuardianStopReason.None) { return false; }
            if (command == 1)
            {
                renewed = now;
                firstHeartbeat.TrySetResult();
                return true;
            }
            StopLocked(command == 2 ? So6120GuardianStopReason.ExplicitAuto : So6120GuardianStopReason.CallerDisconnected);
            return false;
        }
    }

    public void CallerDisconnected()
    {
        lock (sync) { if (!disposed) { StopLocked(So6120GuardianStopReason.CallerDisconnected); } }
    }

    internal void Attach(So6120TrialGuardian owner)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (guardian is not null) { throw new InvalidOperationException("Caller signals already belong to an owner."); }
            CheckDeadlineLocked(clock.GetTimestamp());
            guardian = owner;
            if (reason != So6120GuardianStopReason.None) { owner.RequestStop(reason); }
        }
    }

    internal void CheckDeadline()
    {
        lock (sync) { if (!disposed) { CheckDeadlineLocked(clock.GetTimestamp()); } }
    }

    private void CheckDeadlineLocked(long now)
    {
        if (clock.GetElapsedTime(renewed, now) >= lease) { StopLocked(So6120GuardianStopReason.LeaseExpired); }
    }

    private void StopLocked(So6120GuardianStopReason cause)
    {
        if (reason != So6120GuardianStopReason.None) { return; }
        reason = cause;
        // Only the owner's internal cancellation links register on this token.
        // Their callbacks must not block the control reader or watchdog.
        cancellation = stop.CancelAsync();
        guardian?.RequestStop(cause);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) { return; } disposed = true; guardian = null; }
        await timer.DisposeAsync().ConfigureAwait(false);
        try { await cancellation.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
}
