using OpenBlade.Contracts;

namespace OpenBlade.FanStop;

// Trusted owner callbacks inspect OS/session/thermal state without mailbox I/O.
// Entry and continuation have separate scope, including power-downgrade rules.
internal sealed record So6120OwnedStopHooks(Func<bool> RefreshEntryAdmission, Func<bool> CachedEntryAdmission,
    Func<bool> ContinueStopAdmission);

internal enum So6120StopCoastResult { Stopped, AutomaticRequired, Unresolved }

internal sealed partial class So6120DirectDutySession
{
    private So6120OwnedStopHooks? ownedStopHooks;
    private CancellationToken ownedStopCancellation;
    private long ownedStopEntryStarted;
    private long? ownedStopAutoStarted;
    private bool ownedStopRestoring;
    private ushort? expectedBoardSsid;
    internal string? BoardIdentityRefusal { get; private set; }
    private static readonly TimeSpan OwnedStopEntryLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OwnedStopAutoLimit = TimeSpan.FromSeconds(60);

    internal static So6120DirectDutySession CreateForOwnedStop(string runRoot, So6120SessionBinding binding,
        ISo6120MailboxBytePort port, ISo6120SessionEvidenceSource source, TimeProvider clock,
        ISo6120SessionAdmissionPolicy admissionPolicy, So6120OwnedStopHooks hooks,
        So6120SessionOptions? options = null, Action<So6120JournalWriteStage>? storageFault = null,
        ushort? expectedBoardSsid = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(admissionPolicy);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(hooks.RefreshEntryAdmission);
        ArgumentNullException.ThrowIfNull(hooks.CachedEntryAdmission);
        ArgumentNullException.ThrowIfNull(hooks.ContinueStopAdmission);
        if (binding.Identity is not { CapturedProfile: PerformancePreset.Balanced, RequestedDuty1: 0, RequestedDuty2: 0 })
        {
            throw new ArgumentException("Owned Stop requires an explicit Balanced zero-duty identity.", nameof(binding));
        }

        var session = CreateInjected(runRoot, binding, port, source, clock, options, storageFault, admissionPolicy);
        session.ownedStopHooks = hooks;
        session.expectedBoardSsid = expectedBoardSsid;
        return session;
    }

    internal So6120DutyResult EnterStopped(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            RequireOwnedStop();
            cancellationToken.ThrowIfCancellationRequested();
            BeginInvocation();
            try
            {
                if (entered) { throw new InvalidOperationException("Owned Stop entry has already been attempted."); }
                entered = true;
                ownedStopCancellation = cancellationToken;
                ownedStopEntryStarted = clock.GetTimestamp();
                if (!SessionEntryAllowed(refresh: true))
                {
                    entry = new So6120DutyWorkflow(operation, CancellationToken.None).Result(So6120DutyFailure.AdmissionRefused);
                    return entry;
                }

                // Zero is the requested destination. The existing coordinator
                // skips unchanged staging bytes and publishes FE last.
                entry = new So6120DirectDutyCoordinator(operation).Enter(PerformancePreset.Balanced, 0, 0,
                    cancellationToken: CancellationToken.None);
                return entry;
            }
            finally { running = false; }
        }
    }

    internal bool ContinueStopAdmitted()
    {
        lock (sync)
        {
            RequireOwnedStop();
            if (disposed || blocked || restored || entry?.Success != true) { return false; }
            try { return ownedStopHooks!.ContinueStopAdmission(); }
            catch { return false; }
        }
    }

    // At most four complete observations while tachometers coast down. No
    // unbounded A0 polling; zero PWM alone is not a stopped-fan observation.
    internal So6120StopCoastResult ObserveStoppedCoast(CancellationToken cancellationToken = default,
        Action<TimeSpan, CancellationToken>? wait = null, Func<bool>? beforeObservation = null)
    {
        wait ??= static (delay, token) => token.WaitHandle.WaitOne(delay);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || !ContinueStopAdmitted())
            { return So6120StopCoastResult.AutomaticRequired; }
            wait(TimeSpan.FromSeconds(1), cancellationToken);
            if (cancellationToken.IsCancellationRequested) { return So6120StopCoastResult.AutomaticRequired; }
            // The owner refreshes typed power between resolved native operations.
            // A lower-power transition uses the mandatory observation below as
            // its independent ownership proof, rather than adding another read.
            if (beforeObservation?.Invoke() == false || !ContinueStopAdmitted())
            { return So6120StopCoastResult.AutomaticRequired; }
            var observed = VerifyOwnedStop();
            if (observed.Disposition != So6120DutyIoDisposition.Confirmed || observed.Observation is null)
            { return So6120StopCoastResult.Unresolved; }
            if (observed.Observation.Snapshot.AllTachStopped(operation.ThreeChannels))
            { return So6120StopCoastResult.Stopped; }
        }
        return So6120StopCoastResult.AutomaticRequired;
    }

    // An occasional power-transition verification. Sustained monitoring uses
    // ContinueStopAdmitted, which does not consume journal or mailbox capacity.
    internal So6120DutyReadResult VerifyOwnedStop(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            RequireOwnedStop();
            cancellationToken.ThrowIfCancellationRequested();
            BeginInvocation();
            try
            {
                if (entry?.Success != true || restored || operation.Latest is not { } checkpoint)
                {
                    throw new InvalidOperationException("No confirmed owned Stop is available to verify.");
                }

                var flow = new So6120DutyWorkflow(operation, CancellationToken.None);
                flow.Resume(checkpoint);
                try
                {
                    So6120DutyObservation observation = flow.ObserveExpected();
                    if (!observation.Snapshot.MatchesActualDuty(0, 0, operation.ThreeChannels ? (byte)0 : null) ||
                        (observation.Snapshot.ActiveState & 1) == 0)
                    {
                        Fence(So6120DutyFailure.StateChanged);
                        return new(So6120DutyIoDisposition.Unresolved, null);
                    }
                    return new(So6120DutyIoDisposition.Confirmed, observation);
                }
                catch
                {
                    Fence(So6120DutyFailure.CommandUnresolved);
                    return new(So6120DutyIoDisposition.Unresolved, null);
                }
            }
            finally { running = false; }
        }
    }

    internal So6120DutyResult ReturnToAutomatic()
    {
        lock (sync)
        {
            RequireOwnedStop();
            BeginInvocation();
            try
            {
                ownedStopRestoring = true;
                ownedStopAutoStarted = clock.GetTimestamp();
                return RestoreWorkflow(CancellationToken.None);
            }
            finally { running = false; }
        }
    }

    private void RequireOwnedStop()
    {
        if (ownedStopHooks is null) { throw new InvalidOperationException("This session was not created for owned Stop."); }
    }

    private bool SessionEntryAllowed(bool refresh, bool checkThermal = true)
    {
        if (ownedStopHooks is null) { return EntryAllowed(refresh, checkThermal); }
        if (ownedStopRestoring) { return true; }
        if (ownedStopCancellation.IsCancellationRequested ||
            clock.GetElapsedTime(ownedStopEntryStarted) >= OwnedStopEntryLimit) { return false; }
        if (!checkThermal) { return true; }
        try { return refresh ? ownedStopHooks.RefreshEntryAdmission() : ownedStopHooks.CachedEntryAdmission(); }
        catch { return false; }
    }

    private bool SessionTransactionAllowed() => ownedStopHooks is null
        ? AutoTransactionAllowed()
        : !ownedStopRestoring || ownedStopAutoStarted is long started && clock.GetElapsedTime(started) < OwnedStopAutoLimit;
}
