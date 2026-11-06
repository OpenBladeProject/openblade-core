using System.Threading.Channels;

namespace OpenBlade.Capture;

internal enum So6120GuardianStopReason { None, ExplicitAuto, CallerDisconnected, LeaseExpired, OperationLimit }

internal sealed record So6120GuardianOptions(TimeSpan CallerLease, TimeSpan OperationLimit)
{
    public static So6120GuardianOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
}

// The factory transfers the prepared session to the guardian. Until it returns,
// it must release its session on preparation failure. Only injected fake ports
// compose this prototype; there is no CLI, native acquisition, or IPC admission.
internal sealed record So6120GuardianTrial(So6120DirectDutySession Session,
    So6120SilentTrialPlan Plan, So6120SilentTrialApproval Approval);

internal sealed record So6120GuardianStatus(long Sequence, string Phase, So6120GuardianStopReason StopReason,
    bool OwnerRunning, bool LastReportedPermissionPublicationPossible, bool LastReportedPermissionRestoredConfirmed);

internal sealed record So6120GuardianReceipt(So6120SilentTrialResult? Trial, So6120GuardianStopReason StopReason,
    bool AutoConfirmed, bool CleanupConfirmed, bool JournalCompleted, bool ReleaseCompleted, string? Failure)
{
    // Cancellation makes Trial.Success false even after successful handback.
    // A completed journal alone says nothing about subsequent port release.
    public bool Resolved => AutoConfirmed && CleanupConfirmed && JournalCompleted && ReleaseCompleted
        && Trial is { CommandsFenced: false } && Failure is null;
}

/// <summary>
/// Offline owner-lifetime prototype. All session commands run on one worker;
/// caller loss only requests its existing confirmed-boundary Auto path. A lease
/// is a cutoff for new trial work, never a native-call or cooling deadline.
/// </summary>
internal sealed class So6120TrialGuardian
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly So6120GuardianOptions options;
    private readonly CancellationTokenSource stop = new();
    private readonly ITimer watchdog;
    private readonly long started;
    private long renewed;
    private bool finishing;
    private Task cancellation = Task.CompletedTask;
    private So6120GuardianStatus status = new(0, "Preparing", So6120GuardianStopReason.None, true, false, false);
    private readonly Channel<So6120GuardianStatus> statuses = Channel.CreateBounded<So6120GuardianStatus>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
            SingleWriter = false,
            SingleReader = false,
        });

    private So6120TrialGuardian(Func<Action<So6120SilentTrialProgress>, So6120GuardianTrial> prepare,
        TimeProvider clock, So6120GuardianOptions options)
    {
        this.clock = clock;
        this.options = options;
        started = renewed = clock.GetTimestamp();
        watchdog = clock.CreateTimer(_ => CheckDeadline(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        statuses.Writer.TryWrite(status);
        watchdog.Change(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        Completion = Task.Run(() => RunAsync(prepare));
    }

    public static So6120TrialGuardian Start(Func<Action<So6120SilentTrialProgress>, So6120GuardianTrial> prepare,
        TimeProvider? clock = null, So6120GuardianOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        options ??= So6120GuardianOptions.Default;
        if (options.CallerLease < TimeSpan.FromMilliseconds(100) || options.CallerLease > TimeSpan.FromSeconds(30)
            || options.OperationLimit < TimeSpan.FromMilliseconds(100) || options.OperationLimit > TimeSpan.FromSeconds(45))
        { throw new ArgumentOutOfRangeException(nameof(options)); }
        return new(prepare, clock ?? TimeProvider.System, options);
    }

    public Task<So6120GuardianReceipt> Completion { get; }
    public ChannelReader<So6120GuardianStatus> Statuses => statuses.Reader;
    public So6120GuardianStatus Snapshot { get { lock (sync) { return status; } } }

    public void RequestAuto() => RequestStop(So6120GuardianStopReason.ExplicitAuto);
    public void CallerDisconnected() => RequestStop(So6120GuardianStopReason.CallerDisconnected);

    public bool Heartbeat()
    {
        lock (sync)
        {
            long now = clock.GetTimestamp();
            CheckDeadlineLocked(now);
            if (finishing || status.StopReason != So6120GuardianStopReason.None) { return false; }
            renewed = now;
            return true;
        }
    }

    internal void CheckDeadline()
    {
        lock (sync) { CheckDeadlineLocked(clock.GetTimestamp()); }
    }

    private void CheckDeadlineLocked(long now)
    {
        if (finishing || status.StopReason != So6120GuardianStopReason.None) { return; }
        if (clock.GetElapsedTime(started, now) >= options.OperationLimit)
        { LatchStopLocked(So6120GuardianStopReason.OperationLimit); }
        else if (clock.GetElapsedTime(renewed, now) >= options.CallerLease)
        { LatchStopLocked(So6120GuardianStopReason.LeaseExpired); }
    }

    private void RequestStop(So6120GuardianStopReason reason)
    {
        lock (sync)
        {
            if (!finishing && status.StopReason == So6120GuardianStopReason.None) { LatchStopLocked(reason); }
        }
    }

    private void LatchStopLocked(So6120GuardianStopReason reason)
    {
        // No caller callbacks or caller token are registered. Still avoid running
        // even framework delay cancellation continuations on the liveness path.
        cancellation = stop.CancelAsync();
        PublishLocked(status with { StopReason = reason });
    }

    private void ReportProgress(So6120SilentTrialProgress progress)
    {
        lock (sync)
        {
            CheckDeadlineLocked(clock.GetTimestamp());
            PublishLocked(status with
            {
                Phase = progress.Phase,
                LastReportedPermissionPublicationPossible = progress.PermissionPublicationPossible,
                LastReportedPermissionRestoredConfirmed = progress.PermissionRestoredConfirmed,
            });
        }
    }

    private void PublishLocked(So6120GuardianStatus next)
    {
        status = next with { Sequence = status.Sequence + 1 };
        statuses.Writer.TryWrite(status);
    }

    private async Task<So6120GuardianReceipt> RunAsync(
        Func<Action<So6120SilentTrialProgress>, So6120GuardianTrial> prepare)
    {
        So6120GuardianTrial? owned = null;
        So6120SilentTrialResult? result = null;
        string? failure = null;
        bool released = false;
        try
        {
            owned = prepare(ReportProgress);
            CheckDeadline();
            result = owned.Session.ExecuteApproved(owned.Plan, owned.Approval, stop.Token);
        }
        catch (Exception ex) { failure = $"Owner: {ex.GetType().Name}: {ex.Message}"; }
        finally
        {
            // ExecuteApproved has returned: never close a port while its owner
            // is in a pending invocation, and never issue commands from Dispose.
            if (owned is not null)
            {
                lock (sync) { PublishLocked(status with { Phase = "Releasing" }); }
                try { owned.Session.Dispose(); released = true; }
                catch (Exception ex) { failure = $"{failure} Release: {ex.GetType().Name}: {ex.Message}".Trim(); }
            }
            lock (sync) { finishing = true; }
            await watchdog.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
            stop.Dispose();
        }

        var receipt = new So6120GuardianReceipt(result, Snapshot.StopReason,
            result?.Restoration?.NormalModeObserved == true,
            result?.Restoration?.CleanupConfirmed == true,
            result?.Restoration?.Success == true, released, failure);
        lock (sync)
        {
            PublishLocked(status with { Phase = receipt.Resolved ? "Complete" : "Unresolved", OwnerRunning = false });
            statuses.Writer.TryComplete();
        }
        return receipt;
    }
}
