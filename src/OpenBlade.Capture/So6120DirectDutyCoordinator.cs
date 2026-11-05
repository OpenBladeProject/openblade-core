using OpenBlade.Contracts;

namespace OpenBlade.Capture;

internal sealed record So6120DutyControls(byte Selector, byte Manual, byte Override,
    byte Staged1, byte Staged2, byte Cycle, byte PolicyCommand);

internal sealed record So6120DutySnapshot(So6120DutyControls Controls,
    byte Actual1, byte Actual2, byte Tach1, byte Tach2, byte PolicyStatus, byte ActiveState);

// Same monotonic clock for this operation; a snapshot is an ordered set of fresh
// A0 reads, NOT an atomic snapshot. Sequence/time record observation provenance.
internal sealed record So6120DutyObservation(DirectDutyOwner Owner, long Sequence,
    long StartedTimestamp, long CompletedTimestamp, So6120DutySnapshot Snapshot);

internal enum So6120DutyIoDisposition { Confirmed, DefinitelyUnsubmitted, Unresolved }
internal sealed record So6120DutyReadResult(So6120DutyIoDisposition Disposition, So6120DutyObservation? Observation);
internal sealed record So6120DutyWriteResult(So6120DutyIoDisposition Disposition, So6120MailboxResult? Transaction);
internal sealed record So6120DutyMutation(So6120MailboxRegister Register, byte Value, So6120DutyControls Expected);

[Flags]
internal enum So6120DutyOwnedFields { None = 0, Staged1 = 1, Staged2 = 2, Override = 4, Selector = 8 }

internal sealed record So6120DutyCheckpoint(DirectDutyOwner Owner, long Generation,
    PerformancePreset Profile, So6120DutyControls Baseline, So6120DutyControls Expected,
    So6120DutyOwnedFields Owned, So6120DutyObservation Observation);

internal enum So6120DutyFailure
{
    None, AlreadyUsed, InvalidRequest, AdmissionRefused, BaselineRejected,
    StateChanged, ObservationRejected, NotSubmitted, CommandUnresolved,
    JournalUnresolved, Cancelled, PwmNotObserved, BoundaryUnresolved,
}

internal sealed record So6120DutyResult(So6120DutyFailure Failure,
    So6120DutyCheckpoint? CleanupCheckpoint, bool DirectEntryConfirmed,
    bool NormalModeObserved, bool CleanupConfirmed, bool CommandsFenced,
    bool DurableFenceConfirmed, bool PermissionPublicationPossible,
    bool PermissionEnabledConfirmed, bool PermissionRestoredConfirmed,
    IReadOnlyList<So6120DutyObservation> Observations,
    IReadOnlyList<So6120DutyWriteResult> Writes)
{
    public bool Success => Failure == So6120DutyFailure.None;
    public bool PermissionExposureMayRemain => PermissionPublicationPossible && !PermissionRestoredConfirmed;
}

// No implementation is supplied. The outer owner must implement durable claims,
// journal generations, exact-device/power/session/caller admission and the
// reviewed treatment of residual contention. These methods do not promise
// exclusion of firmware writers or atomic snapshot-to-publication stability.
internal interface ISo6120DirectDutyBoundary
{
    // False guarantees no claim/submission. Exceptions leave the claim unknown.
    bool ClaimEntry(DirectDutyOwner owner, PerformancePreset capturedProfile);
    bool ClaimExit(So6120DutyCheckpoint checkpoint);
    So6120DutyReadResult ReadSnapshot(CancellationToken cancellationToken);
    bool RecordIntent(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyObservation observation);
    bool AdmitWrite(DirectDutyOwner owner, So6120DutyMutation mutation);
    // Resolve a recorded intent only with durable evidence that its A1 was not
    // submitted. Needed for cancellation/refusal before publication and explicit
    // DefinitelyUnsubmitted results; failure leaves the journal unresolved.
    bool RecordNotSubmitted(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyFailure reason);
    // Expected controls are freshly observed BEFORE starting the A1 core.
    // Bind that observation to this request. The core publication callback may
    // check metadata admission, never issue nested A0 reads over staged A1.
    // Use the one-byte core and a separate fresh A0 confirmation; retain its
    // ledger. DefinitelyUnsubmitted needs durable no-submission evidence, not
    // merely a local counter. An unresolved A0 also stops all further commands.
    So6120DutyWriteResult WriteAndConfirm(So6120DutyMutation mutation,
        So6120DutyObservation before, CancellationToken cancellationToken);
    bool RecordCheckpoint(So6120DutyCheckpoint checkpoint);
    // Called after the in-process fence is set. Failure/exception does not mean
    // a durable fence exists. This operation performs no hardware I/O.
    bool RecordFence(DirectDutyOwner owner, So6120DutyFailure reason);
}

// One operation object is shared by entry and exit, including every checkpoint.
// It prevents stale-checkpoint reuse in this process; the boundary still owns
// durable fencing across instances/restarts. It is not a hardware ownership token.
internal sealed class So6120DirectDutyOperation(DirectDutyOwner owner, ISo6120DirectDutyBoundary boundary)
{
    private readonly object sync = new();
    private bool entryStarted, entryFinished, exitStarted, fenced;
    private long observationSequence, observationTimestamp;
    internal DirectDutyOwner Owner { get; } = owner ?? throw new ArgumentNullException(nameof(owner));
    internal ISo6120DirectDutyBoundary Boundary { get; } = boundary ?? throw new ArgumentNullException(nameof(boundary));
    internal So6120DutyCheckpoint? Latest { get; private set; }
    internal bool DurableFenceConfirmed { get; private set; }
    internal bool Fenced { get { lock (sync) { return fenced; } } }
    internal bool DirectEntryConfirmed { get; set; }
    internal bool PermissionPublicationPossible { get; set; }
    internal bool PermissionEnabledConfirmed { get; set; }
    internal bool PermissionRestoredConfirmed { get; set; }

    internal bool BeginEntry()
    {
        lock (sync)
        {
            if (entryStarted || fenced) { return false; }
            entryStarted = true;
            return true;
        }
    }
    internal void FinishEntry() { lock (sync) { entryFinished = true; } }
    internal bool BeginExit(So6120DutyCheckpoint checkpoint)
    {
        lock (sync)
        {
            if (!entryFinished || exitStarted || fenced || checkpoint is null || !ReferenceEquals(Latest, checkpoint)) { return false; }
            exitStarted = true;
            return true;
        }
    }
    // Includes accepted observations after the last durable checkpoint (for
    // example PWM observation attempts). Exit must not restart an older clock.
    internal bool AcceptObservation(So6120DutyObservation observation)
    {
        lock (sync)
        {
            if (fenced || observation.Owner != Owner || observation.Sequence <= observationSequence
                || observation.StartedTimestamp < observationTimestamp
                || observation.CompletedTimestamp < observation.StartedTimestamp
                || observation.Snapshot is null || observation.Snapshot.Controls is null)
            {
                return false;
            }
            observationSequence = observation.Sequence;
            observationTimestamp = observation.CompletedTimestamp;
            return true;
        }
    }
    internal void Commit(So6120DutyCheckpoint checkpoint) { Latest = checkpoint; }
    internal void Fence(So6120DutyFailure reason)
    {
        lock (sync)
        {
            if (fenced) { return; }
            fenced = true;
        }
        try { DurableFenceConfirmed = Boundary.RecordFence(Owner, reason); }
        catch (Exception) { DurableFenceConfirmed = false; }
    }
}

// Offline typed sequencing only. No native construction, CLI or live binding.
// EC2394 schedules event14 for writable A1; CD6C->9008 returns on idle0A at
// 9010->907E. Idle is checked before writes and finally, not proof of drained
// callbacks. Same-profile reload can restore saved Manual at165EE..165F2;
// unexpected Manual is drift, never an instruction to issue a repair write.
// Bank0 read-only06 maps802E3; bit0 gates FE at15E4A..15E54. Active+matching
// PWM is observed state, not proof that FE executed or of a mechanical effect.
// F0.6 also holds a policy contributor at1101A..11022: exposure begins at a
// possible permission publication, before FE, and ends on confirmed restoration.
// Synchronous native stalls cannot be cancelled here. An Unresolved return
// fences all commands; no timeout supplies permission for an alternate writer.
internal sealed class So6120DirectDutyCoordinator(So6120DirectDutyOperation operation)
{
    private readonly So6120DirectDutyOperation operation = operation ?? throw new ArgumentNullException(nameof(operation));

    public So6120DutyResult Enter(PerformancePreset capturedProfile, byte requested1, byte requested2,
        int maxPwmObservations = 8, CancellationToken cancellationToken = default)
    {
        var flow = new So6120DutyWorkflow(operation, cancellationToken);
        if (!operation.BeginEntry()) { return flow.Result(So6120DutyFailure.AlreadyUsed); }
        try
        {
            if (!So6120DutyWorkflow.TrySelector(capturedProfile, out byte selector)
                || maxPwmObservations is < 1 or > 64 || !So6120DutyWorkflow.ValidOwner(operation.Owner))
            {
                return flow.Result(So6120DutyFailure.InvalidRequest);
            }
            flow.CheckCancellation();
            if (!operation.Boundary.ClaimEntry(operation.Owner, capturedProfile))
            {
                return flow.Result(So6120DutyFailure.AdmissionRefused);
            }
            So6120DutyObservation baseline = flow.Observe();
            So6120DutyControls controls = baseline.Snapshot.Controls;
            // Auto is this experiment's scope, not a firmware prerequisite.
            // 0A must be idle: mapped A1 schedules the separate event14 handler.
            // Reject an existing bit6 owner and sensor-suppressing bit5.
            if (controls.Selector != selector || controls.Manual != 0 || controls.PolicyCommand != 0
                || (controls.Override & 0x60) != 0 || controls.Cycle == 0
                || requested1 > controls.Cycle || requested2 > controls.Cycle
                || baseline.Snapshot.Actual1 > controls.Cycle || baseline.Snapshot.Actual2 > controls.Cycle
                || (baseline.Snapshot.ActiveState & 1) == 0)
            {
                return flow.Result(So6120DutyFailure.BaselineRejected);
            }
            flow.Initialize(capturedProfile, baseline);
            flow.Change(So6120MailboxRegister.StagedDuty1, requested1, So6120DutyOwnedFields.Staged1);
            flow.Change(So6120MailboxRegister.StagedDuty2, requested2, So6120DutyOwnedFields.Staged2);
            flow.Change(So6120MailboxRegister.Override, (byte)(controls.Override | 0x40), So6120DutyOwnedFields.Override);
            flow.Change(So6120MailboxRegister.Selector, 0xFE, So6120DutyOwnedFields.Selector);
            for (int attempt = 0; attempt < maxPwmObservations; attempt++)
            {
                So6120DutyObservation observation = flow.ObserveExpected();
                if (observation.Snapshot.Actual1 == requested1 && observation.Snapshot.Actual2 == requested2)
                {
                    return flow.Result(So6120DutyFailure.None);
                }
            }
            // Every command is resolved; a confirmed direct-entry checkpoint can
            // still be explicitly handed back. No automatic cleanup or retry.
            return flow.Result(So6120DutyFailure.PwmNotObserved);
        }
        catch (So6120DutyStopped stopped) { return flow.Result(stopped.Failure); }
        catch (Exception) { return flow.Unresolved(So6120DutyFailure.BoundaryUnresolved); }
        finally { operation.FinishEntry(); }
    }
}

internal sealed class So6120DirectDutyAutoExit(So6120DirectDutyOperation operation)
{
    private readonly So6120DirectDutyOperation operation = operation ?? throw new ArgumentNullException(nameof(operation));

    // A confirmed pre-FE checkpoint restores only the changed prefix. An owned
    // FE checkpoint first restores its captured normal selector. This API has
    // no arbitrary destination parameter and performs no manual/gate/bank writes.
    public So6120DutyResult Restore(So6120DutyCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var flow = new So6120DutyWorkflow(operation, cancellationToken);
        if (!operation.BeginExit(checkpoint)) { return flow.Result(So6120DutyFailure.AlreadyUsed); }
        try
        {
            flow.CheckCancellation();
            if (!operation.Boundary.ClaimExit(checkpoint)) { return flow.Result(So6120DutyFailure.AdmissionRefused); }
            flow.Resume(checkpoint);
            flow.ObserveExpected();
            if ((checkpoint.Owned & So6120DutyOwnedFields.Selector) != 0)
            {
                flow.Change(So6120MailboxRegister.Selector, checkpoint.Baseline.Selector,
                    So6120DutyOwnedFields.Selector, restoring: true);
            }
            flow.ObserveExpected();
            flow.NormalModeObserved = true; // Observed state, not a settled-policy or cooling guarantee.
            flow.Change(So6120MailboxRegister.Override, checkpoint.Baseline.Override,
                So6120DutyOwnedFields.Override, restoring: true);
            // Saved staging bytes may exceed the current cycle. After confirmed
            // non-FE Auto they are inactive data and must be restored exactly.
            flow.Change(So6120MailboxRegister.StagedDuty1, checkpoint.Baseline.Staged1,
                So6120DutyOwnedFields.Staged1, restoring: true);
            flow.Change(So6120MailboxRegister.StagedDuty2, checkpoint.Baseline.Staged2,
                So6120DutyOwnedFields.Staged2, restoring: true);
            flow.ObserveExpected();
            flow.CleanupConfirmed = true;
            return flow.Result(So6120DutyFailure.None);
        }
        catch (So6120DutyStopped stopped) { return flow.Result(stopped.Failure); }
        catch (Exception) { return flow.Unresolved(So6120DutyFailure.BoundaryUnresolved); }
    }
}

internal sealed class So6120DutyStopped(So6120DutyFailure failure) : Exception
{
    public So6120DutyFailure Failure { get; } = failure;
}

internal sealed class So6120DutyWorkflow(So6120DirectDutyOperation operation, CancellationToken cancellationToken)
{
    private readonly List<So6120DutyObservation> observations = [];
    private readonly List<So6120DutyWriteResult> writes = [];
    private So6120DutyCheckpoint? checkpoint;
    public bool NormalModeObserved { get; set; }
    public bool CleanupConfirmed { get; set; }

    internal static bool ValidOwner(DirectDutyOwner owner) => owner.OperationId != Guid.Empty
        && owner.ControllerSession != Guid.Empty && !string.IsNullOrWhiteSpace(owner.DeviceIdentity)
        && !string.IsNullOrWhiteSpace(owner.FirmwareEvidence);

    // Exact normal selector definitions: ThermalModeProtocol.TryDecodeState and
    // docs/so6120-direct-pwm-handback.md. Custom4 needs retained-state support;
    // HyperBoost7 and unnamed selector1 are not admitted by this coordinator.
    internal static bool TrySelector(PerformancePreset profile, out byte selector)
    {
        selector = profile switch
        {
            PerformancePreset.Balanced => 0,
            PerformancePreset.Performance => 2,
            PerformancePreset.BatterySaver => 3,
            PerformancePreset.Silent => 5,
            PerformancePreset.BatteryBalanced => 6,
            _ => 0xFF,
        };
        return selector != 0xFF;
    }

    internal void CheckCancellation()
    {
        if (cancellationToken.IsCancellationRequested) { throw new So6120DutyStopped(So6120DutyFailure.Cancelled); }
    }

    internal So6120DutyObservation Observe()
    {
        CheckCancellation();
        if (operation.Fenced) { throw new So6120DutyStopped(So6120DutyFailure.CommandUnresolved); }
        So6120DutyReadResult result;
        try { result = operation.Boundary.ReadSnapshot(cancellationToken); }
        catch (Exception) { throw StopUnresolved(So6120DutyFailure.CommandUnresolved); }
        if (result.Disposition == So6120DutyIoDisposition.DefinitelyUnsubmitted)
        {
            throw new So6120DutyStopped(So6120DutyFailure.NotSubmitted);
        }
        if (result.Disposition != So6120DutyIoDisposition.Confirmed || result.Observation is not { } observation)
        {
            throw StopUnresolved(So6120DutyFailure.CommandUnresolved);
        }
        observations.Add(observation);
        if (!operation.AcceptObservation(observation))
        {
            throw StopUnresolved(So6120DutyFailure.ObservationRejected);
        }
        return observation;
    }

    internal So6120DutyObservation ObserveExpected()
    {
        So6120DutyObservation observation = Observe();
        if (observation.Snapshot.Controls != checkpoint!.Expected || (observation.Snapshot.ActiveState & 1) == 0)
        {
            throw StopUnresolved(So6120DutyFailure.StateChanged);
        }
        return observation;
    }

    internal void Initialize(PerformancePreset profile, So6120DutyObservation baseline)
    {
        Commit(new(operation.Owner, 1, profile, baseline.Snapshot.Controls,
            baseline.Snapshot.Controls, So6120DutyOwnedFields.None, baseline));
    }
    internal void Resume(So6120DutyCheckpoint prior) { checkpoint = prior; }

    internal void Change(So6120MailboxRegister register, byte value, So6120DutyOwnedFields field, bool restoring = false)
    {
        So6120DutyCheckpoint current = checkpoint!;
        byte old = Get(current.Expected, register);
        if (old == value) { return; } // No write and no ownership for an unchanged staging field.
        if (restoring && (current.Owned & field) == 0) { throw StopUnresolved(So6120DutyFailure.StateChanged); }
        So6120DutyObservation before = ObserveExpected();
        var mutation = new So6120DutyMutation(register, value, current.Expected);
        try
        {
            if (!operation.Boundary.RecordIntent(operation.Owner, mutation, before))
            { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
        }
        catch (So6120DutyStopped) { throw; }
        catch (Exception) { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
        So6120DutyWriteResult result;
        bool invoked = false;
        bool enablesPermission = register == So6120MailboxRegister.Override && (old & 0x40) == 0 && (value & 0x40) != 0;
        try
        {
            CheckCancellation();
            bool admitted;
            try { admitted = operation.Boundary.AdmitWrite(operation.Owner, mutation); }
            catch (Exception) { throw StopUnresolved(So6120DutyFailure.BoundaryUnresolved); }
            if (!admitted) { throw new So6120DutyStopped(So6120DutyFailure.AdmissionRefused); }
            // Read mapped fields after metadata admission but BEFORE staging A1.
            // Residual contention remains: this observation is not an atomic claim.
            before = ObserveExpected();
            CheckCancellation();
            invoked = true;
            if (enablesPermission) { operation.PermissionPublicationPossible = true; }
            try { result = operation.Boundary.WriteAndConfirm(mutation, before, cancellationToken); }
            catch (Exception) { throw StopUnresolved(So6120DutyFailure.CommandUnresolved); }
        }
        catch (So6120DutyStopped stopped)
        {
            if (!invoked && !operation.Fenced) { ResolveUnsubmitted(mutation, stopped.Failure); }
            throw;
        }
        writes.Add(result);
        if (result.Disposition == So6120DutyIoDisposition.DefinitelyUnsubmitted
            && result.Transaction is { CommandsPossiblySubmitted: 0, WriteHandlerCompleted: false } unsubmitted
            && unsubmitted.Failure != So6120MailboxFailure.None)
        {
            ResolveUnsubmitted(mutation, So6120DutyFailure.NotSubmitted);
            if (enablesPermission) { operation.PermissionPublicationPossible = false; }
            throw new So6120DutyStopped(So6120DutyFailure.NotSubmitted);
        }
        if (result.Disposition != So6120DutyIoDisposition.Confirmed
            || result.Transaction is not { WriteConfirmed: true, CommandsCompleted: 2, CommandsPossiblySubmitted: 2 } transaction
            || transaction.Value != value)
        {
            throw StopUnresolved(So6120DutyFailure.CommandUnresolved);
        }
        So6120DutyControls next = Set(current.Expected, register, value);
        // Once A1 is acknowledged, interrupted/missing full-state confirmation
        // is unresolved even if its own byte readback matched.
        So6120DutyObservation after;
        try { after = Observe(); }
        catch (So6120DutyStopped) { throw StopUnresolved(So6120DutyFailure.CommandUnresolved); }
        if (after.Snapshot.Controls != next || (after.Snapshot.ActiveState & 1) == 0)
        { throw StopUnresolved(So6120DutyFailure.StateChanged); }
        if (enablesPermission) { operation.PermissionEnabledConfirmed = true; }
        if (restoring && register == So6120MailboxRegister.Override) { operation.PermissionRestoredConfirmed = true; }
        if (register == So6120MailboxRegister.Selector && value == 0xFE) { operation.DirectEntryConfirmed = true; }
        if (restoring && register == So6120MailboxRegister.Selector) { NormalModeObserved = true; }
        Commit(current with
        {
            Generation = current.Generation + 1,
            Expected = next,
            Owned = restoring ? current.Owned & ~field : current.Owned | field,
            Observation = after,
        });
    }

    private void ResolveUnsubmitted(So6120DutyMutation mutation, So6120DutyFailure reason)
    {
        try
        {
            if (!operation.Boundary.RecordNotSubmitted(operation.Owner, mutation, reason))
            { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
        }
        catch (So6120DutyStopped) { throw; }
        catch (Exception) { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
    }

    private void Commit(So6120DutyCheckpoint candidate)
    {
        try
        {
            if (!operation.Boundary.RecordCheckpoint(candidate))
            { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
        }
        catch (So6120DutyStopped) { throw; }
        catch (Exception) { throw StopUnresolved(So6120DutyFailure.JournalUnresolved); }
        checkpoint = candidate;
        operation.Commit(candidate);
    }

    private So6120DutyStopped StopUnresolved(So6120DutyFailure failure)
    {
        operation.Fence(failure);
        return new(failure);
    }
    internal So6120DutyResult Unresolved(So6120DutyFailure failure)
    {
        operation.Fence(failure);
        return Result(failure);
    }
    internal So6120DutyResult Result(So6120DutyFailure failure) =>
        new(failure, operation.Fenced ? null : checkpoint, operation.DirectEntryConfirmed,
            NormalModeObserved, CleanupConfirmed, operation.Fenced, operation.DurableFenceConfirmed,
            operation.PermissionPublicationPossible, operation.PermissionEnabledConfirmed, operation.PermissionRestoredConfirmed,
            observations.AsReadOnly(), writes.AsReadOnly());

    private static byte Get(So6120DutyControls state, So6120MailboxRegister register) => register switch
    {
        So6120MailboxRegister.StagedDuty1 => state.Staged1,
        So6120MailboxRegister.StagedDuty2 => state.Staged2,
        So6120MailboxRegister.Override => state.Override,
        So6120MailboxRegister.Selector => state.Selector,
        _ => throw new InvalidOperationException("Not a direct-duty coordinator field."),
    };
    private static So6120DutyControls Set(So6120DutyControls state, So6120MailboxRegister register, byte value) => register switch
    {
        So6120MailboxRegister.StagedDuty1 => state with { Staged1 = value },
        So6120MailboxRegister.StagedDuty2 => state with { Staged2 = value },
        So6120MailboxRegister.Override => state with { Override = value },
        So6120MailboxRegister.Selector => state with { Selector = value },
        _ => throw new InvalidOperationException("Not a direct-duty coordinator field."),
    };
}
