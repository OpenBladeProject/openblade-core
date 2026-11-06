using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Capture;

internal enum So6120TrialTargetPolicy { PlusFour, HalfCycle, HalfCycleThenStop }

internal sealed record So6120SilentTrialPlan(Guid PlanId, So6120DutyObservation Baseline,
    byte RequestedDuty1, byte RequestedDuty2, long PreparedTimestamp)
{
    // Omit the legacy default from JSON so existing v2 approvals keep their hash
    // and retain the original requirement for exact live baseline PWM.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowBoundedPwmDrift { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public So6120TrialTargetPolicy TargetPolicy { get; init; }

    [JsonIgnore]
    public string PlanSha256 => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    internal bool IncludesStop => TargetPolicy == So6120TrialTargetPolicy.HalfCycleThenStop;
    internal bool RequiresPostIntentObservation => AllowBoundedPwmDrift || TargetPolicy != So6120TrialTargetPolicy.PlusFour;

    internal bool AdmitsLivePwm(So6120DutySnapshot snapshot) => TargetPolicy switch
    {
        So6120TrialTargetPolicy.PlusFour when AllowBoundedPwmDrift =>
            RequestedDuty1 - snapshot.Actual1 is >= 1 and <= 8 && RequestedDuty2 - snapshot.Actual2 is >= 1 and <= 8,
        So6120TrialTargetPolicy.PlusFour =>
            snapshot.Actual1 == Baseline.Snapshot.Actual1 && snapshot.Actual2 == Baseline.Snapshot.Actual2,
        So6120TrialTargetPolicy.HalfCycle or So6120TrialTargetPolicy.HalfCycleThenStop when !AllowBoundedPwmDrift =>
            RequestedDuty1 > snapshot.Actual1 && RequestedDuty2 > snapshot.Actual2,
        _ => false,
    };

    internal static (int First, int Second) DeriveTargets(So6120DutySnapshot snapshot, So6120TrialTargetPolicy policy) => policy switch
    {
        So6120TrialTargetPolicy.PlusFour => (snapshot.Actual1 + 4, snapshot.Actual2 + 4),
        So6120TrialTargetPolicy.HalfCycle or So6120TrialTargetPolicy.HalfCycleThenStop =>
            (snapshot.Controls.Cycle / 2, snapshot.Controls.Cycle / 2),
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static bool SupportsProfile(PerformancePreset profile) =>
        profile is PerformancePreset.Silent or PerformancePreset.Balanced;

    // The approved baseline already encodes the captured profile in its selector.
    // Keep that as the hashed value so existing Silent journals remain readable.
    internal bool Valid(PerformancePreset profile) => SupportsProfile(profile)
        && So6120DutyWorkflow.TrySelector(profile, out byte selector)
        && PlanId != Guid.Empty && Baseline is not null && Baseline.Snapshot is { } snapshot
        && snapshot.Controls is { Manual: 0, PolicyCommand: 0, Cycle: > 0 } controls && controls.Selector == selector
        && (controls.Override & 0x60) == 0 && (snapshot.ActiveState & 1) != 0
        && RequestedDuty1 <= controls.Cycle && RequestedDuty2 <= controls.Cycle
        && (TargetPolicy == So6120TrialTargetPolicy.PlusFour
            || TargetPolicy is So6120TrialTargetPolicy.HalfCycle or So6120TrialTargetPolicy.HalfCycleThenStop
                && profile == PerformancePreset.Balanced && !AllowBoundedPwmDrift)
        && DeriveTargets(snapshot, TargetPolicy) == (RequestedDuty1, RequestedDuty2)
        && AdmitsLivePwm(snapshot)
        && PreparedTimestamp == Baseline.CompletedTimestamp;

    internal So6120DutyControls HalfCycleControls() => Baseline.Snapshot.Controls with
    {
        Selector = 0xFE,
        Override = (byte)(Baseline.Snapshot.Controls.Override | 0x40),
        Staged1 = RequestedDuty1,
        Staged2 = RequestedDuty2,
    };

    internal bool AdmitsStopWrite(So6120MailboxRegister register, So6120DutySnapshot snapshot) => IncludesStop
        && snapshot.Actual2 == RequestedDuty2 && snapshot.Tach2 > 0
        && (register switch
        {
            So6120MailboxRegister.StagedDuty1 => snapshot.Actual1 == RequestedDuty1 && snapshot.Tach1 > 0,
            So6120MailboxRegister.StagedDuty2 => snapshot.Actual1 <= RequestedDuty1, // Fan 1 can already be slowing or stopped.
            _ => false,
        });
}
internal sealed record So6120SilentTrialApproval(Guid PlanId, string PlanSha256, Guid ConsentId, string ConsentSha256)
{
    internal bool Matches(So6120SilentTrialPlan plan) => PlanId == plan.PlanId && ConsentId != Guid.Empty
        && ConsentSha256 is { Length: 64 } && ConsentSha256.All(Uri.IsHexDigit)
        && string.Equals(PlanSha256, plan.PlanSha256, StringComparison.OrdinalIgnoreCase);
}
internal sealed record So6120SilentTrialHooks(Func<bool> RefreshEntryAdmission, Func<bool> CachedEntryAdmission,
    Action<So6120SilentTrialProgress>? Progress = null);
internal sealed record So6120SilentTrialProgress(string Phase, So6120DutyObservation? Observation,
    bool PermissionPublicationPossible, bool PermissionRestoredConfirmed);
internal enum So6120SilentTrialStop
{
    None, Cancelled, EntryBudget, ThermalRefused, ProgressFailed, EntryFailed, ObservationFailed,
    RestorationFailed, PwmNotObserved, AutoBudget, SpinNotObserved, StopStageFailed, ZeroTachNotObserved,
}
internal sealed record So6120TrialStopStage(So6120DutyResult Work, So6120DutyObservation? ZeroPwmObservation,
    bool ZeroTachObserved, long StartedTimestamp, long? CoastStartedTimestamp, long CompletedTimestamp);
internal sealed record So6120SilentTrialResult(So6120SilentTrialPlan Plan, So6120DutyResult? Entry,
    So6120DutyObservation? ObservationAfterDwell, So6120DutyResult? Restoration, So6120SilentTrialStop StopReason,
    bool CancelRequested, bool? ThermalAfterRestoration, long? PermissionStartedTimestamp, long? PermissionRestoredTimestamp,
    bool CommandsFenced, bool DurableFenceConfirmed)
{
    public long ExecutionStartedTimestamp { get; init; }
    public long? EntryCompletedTimestamp { get; init; }
    public long? AutoStartedTimestamp { get; init; }
    public long? AutoCompletedTimestamp { get; init; }
    public long ExecutionCompletedTimestamp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public So6120TrialStopStage? StopStage { get; init; }
    public bool NoSettingWritePublished => !CommandsFenced && (Entry is null || Entry.Writes.All(write =>
        write.Disposition == So6120DutyIoDisposition.DefinitelyUnsubmitted
        && write.Transaction is { CommandsPossiblySubmitted: 0 }));
    public bool Success => !CancelRequested && !CommandsFenced && StopReason == So6120SilentTrialStop.None
        && Entry?.Success == true && Restoration?.Success == true && (!Plan.IncludesStop || StopStage?.ZeroTachObserved == true);
}

internal sealed partial class So6120DirectDutySession
{
    private So6120SilentTrialPlan? preparedPlan;
    private So6120SilentTrialHooks? trialHooks;
    private CancellationToken trialCancellation;
    private bool preparing, trialExecuting, trialStopping, trialRestoring, softPublicationRefused;
    private long trialStarted;
    private long? permissionStarted, permissionRestored, entryCompleted, autoStarted, autoCompleted;
    private So6120SilentTrialStop trialStop;
    // Resolved entry-work cutoffs, NOT physical exposure/recovery guarantees.
    // Finish the active transaction and mandatory checkpoint observation before
    // declining further entry work. Auto has its own phase; it may exceed either
    // entry cutoff. No software deadline bounds a synchronous native stall.
    private static readonly TimeSpan EntryLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan EntryPermissionLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApprovalAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TrialDwell = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CoastLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CoastCadence = TimeSpan.FromMilliseconds(500);
    private const int MaxCoastReads = 4;

    public So6120SilentTrialPlan PrepareSilentIncrease(So6120SilentTrialHooks hooks, CancellationToken cancellationToken = default) =>
        PrepareIncrease(PerformancePreset.Silent, So6120TrialTargetPolicy.PlusFour, hooks, cancellationToken);

    public So6120SilentTrialPlan PrepareBalancedIncrease(So6120SilentTrialHooks hooks, CancellationToken cancellationToken = default) =>
        PrepareIncrease(PerformancePreset.Balanced, So6120TrialTargetPolicy.PlusFour, hooks, cancellationToken);

    public So6120SilentTrialPlan PrepareBalancedHalfCycle(So6120SilentTrialHooks hooks, CancellationToken cancellationToken = default) =>
        PrepareIncrease(PerformancePreset.Balanced, So6120TrialTargetPolicy.HalfCycle, hooks, cancellationToken);

    public So6120SilentTrialPlan PrepareBalancedStop(So6120SilentTrialHooks hooks, CancellationToken cancellationToken = default) =>
        PrepareIncrease(PerformancePreset.Balanced, So6120TrialTargetPolicy.HalfCycleThenStop, hooks, cancellationToken);

    private So6120SilentTrialPlan PrepareIncrease(PerformancePreset expectedProfile,
        So6120TrialTargetPolicy targetPolicy, So6120SilentTrialHooks hooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(hooks.RefreshEntryAdmission);
        ArgumentNullException.ThrowIfNull(hooks.CachedEntryAdmission);
        lock (sync)
        {
            BeginInvocation();
            try
            {
                if (preparing || entered || binding.Identity.RequestedDuty1.HasValue || binding.Identity.RequestedDuty2.HasValue
                    || binding.Identity.CapturedProfile != expectedProfile)
                { throw new InvalidOperationException($"Preparation requires an unused unplanned {expectedProfile} identity."); }
                preparing = true;
                trialHooks = hooks;
                cancellationToken.ThrowIfCancellationRequested();
                if (!RefreshThermal()) { throw new InvalidOperationException("Baseline thermal admission refused."); }
                FreshAdmission();
                Store(() => journal.ClaimPreparation(binding.Identity.Owner));
                var read = ReadSnapshot(cancellationToken);
                if (read.Disposition != So6120DutyIoDisposition.Confirmed || read.Observation is not { } baseline)
                { throw new InvalidOperationException("Baseline observation unavailable."); }
                var (first, second) = So6120SilentTrialPlan.DeriveTargets(baseline.Snapshot, targetPolicy);
                if (first > byte.MaxValue || second > byte.MaxValue)
                { throw new InvalidOperationException("No fixed-increase headroom."); }
                var plan = new So6120SilentTrialPlan(Guid.NewGuid(), baseline,
                    (byte)first, (byte)second, baseline.CompletedTimestamp)
                {
                    AllowBoundedPwmDrift = targetPolicy == So6120TrialTargetPolicy.PlusFour,
                    TargetPolicy = targetPolicy,
                };
                if (!plan.Valid(expectedProfile))
                { throw new InvalidOperationException($"{expectedProfile}/Auto baseline or fixed target headroom refused ({targetPolicy})."); }
                preparedPlan = plan;
                ReportTrial("Prepared", baseline);
                if (trialStop != So6120SilentTrialStop.None) { throw new InvalidOperationException("Preparation reporting failed."); }
                return plan;
            }
            finally { running = false; }
        }
    }

    // This approval covers the fixed stages and their explicit Auto exit.
    // No loaded plan is accepted; a matching digest is not consent authentication.
    public So6120SilentTrialResult ExecuteApproved(So6120SilentTrialPlan plan, So6120SilentTrialApproval approval,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            BeginInvocation();
            So6120DutyObservation? afterDwell = null;
            So6120DutyResult? exit = null;
            So6120TrialStopStage? stopStage = null;
            bool? thermal = null;
            try
            {
                if (entered || !ReferenceEquals(plan, preparedPlan) || approval is null || !approval.Matches(plan)
                    || clock.GetElapsedTime(plan.PreparedTimestamp) > ApprovalAge || trialHooks is null)
                { throw new InvalidOperationException("Exact in-memory plan approval missing, expired, or already used."); }
                entered = true;
                trialExecuting = true;
                trialStarted = clock.GetTimestamp();
                trialCancellation = cancellationToken;
                Store(() => journal.ApprovePlan(plan, approval));
                ReportTrial("Entering", latestObservation);
                if (EntryAllowed(refresh: true))
                {
                    // The coordinator reobserves the approved controls and checks
                    // the live increase against the same fixed approved targets.
                    entry = new So6120DirectDutyCoordinator(operation).EnterPrepared(binding.Identity.CapturedProfile, plan);
                    entryCompleted = clock.GetTimestamp();
                    ReportTrial("EntryFinished", latestObservation);
                    if (entry.Success && plan.IncludesStop)
                    {
                        if (EntryAllowed(refresh: false, checkThermal: false))
                        {
                            stopStage = ExecuteStopStage(plan);
                            ReportTrial("StopFinished", latestObservation);
                        }
                    }
                    else if (entry.Success && EntryAllowed(refresh: false, checkThermal: false))
                    {
                        try
                        {
                            Task.Delay(TrialDwell, clock, cancellationToken).GetAwaiter().GetResult();
                            var read = EntryAllowed(refresh: false, checkThermal: false)
                                ? ReadSnapshot(CancellationToken.None)
                                : new So6120DutyReadResult(So6120DutyIoDisposition.DefinitelyUnsubmitted, null);
                            if (read.Disposition == So6120DutyIoDisposition.DefinitelyUnsubmitted)
                            { if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.ObservationFailed; } }
                            else if (read.Disposition != So6120DutyIoDisposition.Confirmed || read.Observation is not { } observation
                                || observation.Snapshot.Controls != operation.Latest!.Expected || (observation.Snapshot.ActiveState & 1) == 0)
                            { Fence(So6120DutyFailure.StateChanged); trialStop = So6120SilentTrialStop.ObservationFailed; }
                            else
                            {
                                afterDwell = observation;
                                if (observation.Snapshot.Actual1 != plan.RequestedDuty1 || observation.Snapshot.Actual2 != plan.RequestedDuty2)
                                { trialStop = So6120SilentTrialStop.PwmNotObserved; }
                            }
                            ReportTrial("DwellFinished", afterDwell);
                        }
                        catch (OperationCanceledException) { trialStop = So6120SilentTrialStop.Cancelled; }
                        catch { trialStop = So6120SilentTrialStop.ObservationFailed; }
                    }
                    else if (!entry.Success && trialStop == So6120SilentTrialStop.None)
                    { trialStop = entry.Failure == So6120DutyFailure.PwmNotObserved ? So6120SilentTrialStop.PwmNotObserved : So6120SilentTrialStop.EntryFailed; }
                    if (!blocked && entry.CleanupCheckpoint is not null)
                    {
                        trialRestoring = true;
                        autoStarted = clock.GetTimestamp();
                        ReportTrial("Restoring", latestObservation);
                        // User cancellation applies to further trial work. This
                        // separately approved handback has its own 60s phase and
                        // fresh hardware admission, never a cancelled token.
                        exit = RestoreWorkflow(CancellationToken.None);
                        autoCompleted = clock.GetTimestamp();
                        ReportTrial("RestorationFinished", latestObservation);
                        if (!exit.Success) { if (trialStop != So6120SilentTrialStop.AutoBudget) { trialStop = So6120SilentTrialStop.RestorationFailed; } }
                        else
                        {
                            // Optional diagnostic refresh only AFTER confirmed
                            // Auto. A slow sensor query must not delay handback.
                            thermal = RefreshThermal() && CachedThermal();
                        }
                    }
                }
                if (trialStop == So6120SilentTrialStop.None && cancellationToken.IsCancellationRequested)
                { trialStop = So6120SilentTrialStop.Cancelled; }
                return new(plan, entry, afterDwell, exit, trialStop, cancellationToken.IsCancellationRequested,
                    thermal, permissionStarted, permissionRestored, operation.Fenced, operation.DurableFenceConfirmed)
                {
                    ExecutionStartedTimestamp = trialStarted,
                    EntryCompletedTimestamp = entryCompleted,
                    AutoStartedTimestamp = autoStarted,
                    AutoCompletedTimestamp = autoCompleted,
                    ExecutionCompletedTimestamp = clock.GetTimestamp(),
                    StopStage = stopStage,
                };
            }
            catch (Exception) when (trialExecuting)
            {
                // Retain reached milestones even if admission or storage
                // failure interrupts orchestration. No speculative handback.
                if (!blocked) { Fence(So6120DutyFailure.BoundaryUnresolved); }
                if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.ObservationFailed; }
                return new(plan, entry, afterDwell, exit, trialStop, cancellationToken.IsCancellationRequested,
                    thermal, permissionStarted, permissionRestored, operation.Fenced, operation.DurableFenceConfirmed)
                {
                    ExecutionStartedTimestamp = trialStarted,
                    EntryCompletedTimestamp = entryCompleted,
                    AutoStartedTimestamp = autoStarted,
                    AutoCompletedTimestamp = autoCompleted,
                    ExecutionCompletedTimestamp = clock.GetTimestamp(),
                    StopStage = stopStage,
                };
            }
            finally { trialExecuting = false; running = false; }
        }
    }

    private So6120TrialStopStage ExecuteStopStage(So6120SilentTrialPlan plan)
    {
        long started = clock.GetTimestamp();
        long? coastStarted = null;
        So6120DutyObservation? zeroPwm = null;
        int consecutiveZeroTach = 0;
        // Cancellation stops new work through EntryAllowed. A submitted A1
        // must still finish its independent confirmation and full checkpoint.
        var flow = new So6120DutyWorkflow(operation, CancellationToken.None);
        So6120DutyCheckpoint checkpoint = operation.Latest!;
        flow.Resume(checkpoint);
        try
        {
            So6120DutyObservation spin = flow.ObserveExpected();
            if (checkpoint.Expected != plan.HalfCycleControls()
                || !plan.AdmitsStopWrite(So6120MailboxRegister.StagedDuty1, spin.Snapshot))
            {
                trialStop = So6120SilentTrialStop.SpinNotObserved;
                return Result(flow.Result(So6120DutyFailure.PwmNotObserved));
            }
            if (!EntryAllowed(refresh: false, checkThermal: false))
            { return Result(flow.Result(So6120DutyFailure.AdmissionRefused)); }
            Store(() => journal.ClaimStop(checkpoint, spin));
            trialStopping = true;
            flow.Change(So6120MailboxRegister.StagedDuty1, 0, So6120DutyOwnedFields.Staged1);
            if (!EntryAllowed(refresh: false, checkThermal: false))
            { return Result(flow.Result(So6120DutyFailure.AdmissionRefused)); }
            flow.Change(So6120MailboxRegister.StagedDuty2, 0, So6120DutyOwnedFields.Staged2);

            coastStarted = clock.GetTimestamp();
            CountCoastSample(operation.Latest!.Observation);
            for (int i = 0; i < MaxCoastReads && consecutiveZeroTach < 2 && CanStartCoastRead(); i++)
            {
                Task.Delay(CoastCadence, clock, trialCancellation).GetAwaiter().GetResult();
                if (!CanStartCoastRead()) { break; }
                CountCoastSample(flow.ObserveExpected());
            }
            if (consecutiveZeroTach >= 2) { return Result(flow.Result(So6120DutyFailure.None)); }
            if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.ZeroTachNotObserved; }
            return Result(flow.Result(So6120DutyFailure.PwmNotObserved));
        }
        catch (OperationCanceledException)
        {
            trialStop = So6120SilentTrialStop.Cancelled;
            return Result(flow.Result(So6120DutyFailure.Cancelled));
        }
        catch (So6120DutyStopped stopped)
        {
            if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.StopStageFailed; }
            return Result(flow.Result(stopped.Failure));
        }
        catch (Exception)
        {
            if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.StopStageFailed; }
            return Result(flow.Unresolved(So6120DutyFailure.BoundaryUnresolved));
        }

        bool CanStartCoastRead() => EntryAllowed(refresh: false, checkThermal: false)
            && clock.GetElapsedTime(coastStarted!.Value) < CoastLimit;

        void CountCoastSample(So6120DutyObservation observation)
        {
            var snapshot = observation.Snapshot;
            bool actualZero = snapshot.Actual1 == 0 && snapshot.Actual2 == 0;
            if (actualZero) { zeroPwm ??= observation; }
            consecutiveZeroTach = actualZero && snapshot.Tach1 == 0 && snapshot.Tach2 == 0 ? consecutiveZeroTach + 1 : 0;
        }

        So6120TrialStopStage Result(So6120DutyResult work) =>
            new(work, zeroPwm, consecutiveZeroTach >= 2, started, coastStarted, clock.GetTimestamp());
    }

    private bool RefreshThermal()
    {
        try { return trialHooks!.RefreshEntryAdmission(); }
        catch { return false; }
    }
    private bool CachedThermal()
    {
        try { return trialHooks!.CachedEntryAdmission(); }
        catch { return false; }
    }
    private bool EntryAllowed(bool refresh, bool checkThermal = true)
    {
        if (!trialExecuting || trialRestoring) { return true; }
        if (trialStop != So6120SilentTrialStop.None) { return false; }
        if (trialCancellation.IsCancellationRequested) { trialStop = So6120SilentTrialStop.Cancelled; return false; }
        if (clock.GetElapsedTime(trialStarted) >= EntryLimit
            || permissionStarted is long started && clock.GetElapsedTime(started) >= EntryPermissionLimit)
        { trialStop = So6120SilentTrialStop.EntryBudget; return false; }
        if (checkThermal && ((refresh && !RefreshThermal()) || !CachedThermal()))
        { trialStop = So6120SilentTrialStop.ThermalRefused; return false; }
        return true;
    }
    // Checked only before a NEW logical transaction, never inside its A1 and
    // separate A0 confirmation. Full transaction timeout remains available; the
    // phase can overrun by that transaction (or indefinitely on native stall).
    private bool AutoTransactionAllowed()
    {
        if (!trialExecuting || !trialRestoring || autoStarted is not long started
            || clock.GetElapsedTime(started) < AutoLimit) { return true; }
        trialStop = So6120SilentTrialStop.AutoBudget;
        return false;
    }
    private void ReportTrial(string phase, So6120DutyObservation? observation)
    {
        try { trialHooks?.Progress?.Invoke(new(phase, observation, operation.PermissionPublicationPossible, operation.PermissionRestoredConfirmed)); }
        catch { if (trialStop == So6120SilentTrialStop.None) { trialStop = So6120SilentTrialStop.ProgressFailed; } }
    }
}
