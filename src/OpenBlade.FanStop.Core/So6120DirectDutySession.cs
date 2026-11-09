using OpenBlade.Contracts;

namespace OpenBlade.FanStop;

// Supplied evidence is unauthenticated. This comparison boundary does not grant
// hardware authority, consent, firmware exclusion, or driver-handle admission.
// Challenge acceptance and hardware-test consent are bound as separate fields;
// equal or different caller-supplied UUIDs do not establish authorization.
internal sealed record So6120SessionBinding(So6120JournalIdentity Identity, uint ConsoleSession,
    So6120CallerChallengeContext Challenge, So6120OpenBladeHandoff Handoff);
internal sealed record So6120SessionEvidence(So6120JournalIdentity Identity, So6120SupervisedMetadata Metadata,
    So6120CallerChallengeContext Challenge, So6120OpenBladeHandoff Handoff);
internal interface ISo6120SessionEvidenceSource
{
    // Must collect fresh metadata starting inside this call. No mailbox I/O.
    So6120SessionEvidence Capture();
}
internal sealed record So6120SessionOptions(TimeSpan TransactionTimeout, TimeSpan SnapshotTimeout, int MaxPolls = 128)
{
    // Layer-local protocol/observation bounds. The fixed trial separately
    // supplies resolved entry-work and Auto-phase cutoffs, not thermal guarantees.
    // SnapshotTimeout is checked between/after transfers, so one in-flight
    // transaction may overrun it. A native stall has no software time bound.
    public static So6120SessionOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
}

// One serialized, one-shot, injected developer session. Ownership here covers
// these objects and the agreed journal path, not other processes or firmware.
// The fixed attended CLI supplies the live platform. A blocked native call holds serialization;
// software deadlines cannot interrupt it or promise physical thermal recovery.
internal sealed partial class So6120DirectDutySession : IDisposable
{
    private readonly object sync = new();
    private readonly So6120SessionBinding binding;
    private readonly ISo6120MailboxBytePort port;
    private readonly IDisposable portLifetime;
    private readonly ISo6120SessionEvidenceSource source;
    private readonly TimeProvider clock;
    private readonly So6120SessionOptions options;
    private readonly So6120DirectDutyJournal journal;
    private readonly ISo6120SessionAdmissionPolicy admissionPolicy;
    private readonly So6120DirectDutyOperation operation;
    private readonly Boundary boundary;
    private bool running, disposed, blocked, entered, restored;
    private bool transferActive;
    private int commandsPossiblySubmitted, commandsCompleted, mutationsPossiblySubmitted;
    private bool nativeCommandAmbiguity;

    // Positive in-memory command evidence survives storage/admission failures.
    // A missing checkpoint or a faulted journal is never itself release proof.
    internal So6120SessionCommandEvidence CommandEvidence
    {
        get
        {
            lock (sync)
            {
                return new(commandsPossiblySubmitted, commandsCompleted,
                    mutationsPossiblySubmitted, nativeCommandAmbiguity, transferActive);
            }
        }
    }
    private So6120DutyFailure deferredFence;
    private long observationSequence;
    private So6120DutyObservation? latestObservation;
    private So6120DutyResult? entry;

    public static So6120DirectDutySession Acquire() => throw new NotSupportedException(
        "Unscoped acquisition is not admitted; use the fixed attended Windows trial.");

    // On successful argument validation ownership of the disposable port passes
    // here, including journal-creation failure. Caller must not retain another
    // user of that port. No claim is made that an injected port is authentic.
    internal static So6120DirectDutySession CreateInjected(string runRoot, So6120SessionBinding binding,
        ISo6120MailboxBytePort port, ISo6120SessionEvidenceSource source, TimeProvider clock,
        So6120SessionOptions? options = null, Action<So6120JournalWriteStage>? storageFault = null,
        ISo6120SessionAdmissionPolicy? admissionPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(binding.Identity);
        ArgumentNullException.ThrowIfNull(binding.Challenge);
        ArgumentNullException.ThrowIfNull(binding.Handoff);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        if (port is not IDisposable lifetime) { throw new ArgumentException("The transferred port must have an owned disposable lifetime.", nameof(port)); }
        options ??= So6120SessionOptions.Default;
        if (options.TransactionTimeout <= TimeSpan.Zero || options.TransactionTimeout > TimeSpan.FromSeconds(5)
            || options.SnapshotTimeout < options.TransactionTimeout || options.SnapshotTimeout > TimeSpan.FromMinutes(1)
            || options.MaxPolls is < 1 or > 1000 || binding.ConsoleSession is 0 or uint.MaxValue
            // This transport supplies independent readback for two fans only.
            || binding.Identity.RequestedDuty3.HasValue
            || !MatchesStamp(binding.Challenge.Stamp, binding, clock) || !MatchesStamp(binding.Handoff.Stamp, binding, clock))
        { throw new ArgumentException("Invalid session bounds or binding."); }
        try
        {
            return new(binding, port, lifetime, source, clock, options,
                So6120DirectDutyJournal.Create(runRoot, binding.Identity, storageFault),
                admissionPolicy ?? So6120SupervisedSessionAdmissionPolicy.Instance);
        }
        catch { lifetime.Dispose(); throw; }
    }

    private So6120DirectDutySession(So6120SessionBinding binding, ISo6120MailboxBytePort port,
        IDisposable lifetime, ISo6120SessionEvidenceSource source, TimeProvider clock, So6120SessionOptions options,
        So6120DirectDutyJournal journal, ISo6120SessionAdmissionPolicy admissionPolicy)
    {
        this.binding = binding; this.port = port; portLifetime = lifetime;
        this.source = source; this.clock = clock; this.options = options; this.journal = journal;
        this.admissionPolicy = admissionPolicy;
        boundary = new(this);
        operation = new(binding.Identity.Owner, boundary);
    }

    public So6120DutyResult Enter(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            BeginInvocation();
            try
            {
                if (ownedStopHooks is not null) { throw new InvalidOperationException("Use the owned Stop entry point."); }
                if (entered || !binding.Identity.RequestedDuty1.HasValue || !binding.Identity.RequestedDuty2.HasValue)
                { throw new InvalidOperationException("Fixed entry requires an unused fixed-target identity."); }
                entered = true;
                entry = new So6120DirectDutyCoordinator(operation).Enter(binding.Identity.CapturedProfile,
                    binding.Identity.RequestedDuty1.Value, binding.Identity.RequestedDuty2.Value, cancellationToken: cancellationToken);
                return entry;
            }
            finally { running = false; }
        }
    }

    public So6120DutyResult Restore(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            BeginInvocation();
            try
            {
                if (ownedStopHooks is not null) { throw new InvalidOperationException("Use the owned Auto entry point."); }
                return RestoreWorkflow(cancellationToken);
            }
            finally { running = false; }
        }
    }

    private So6120DutyResult RestoreWorkflow(CancellationToken cancellationToken)
    {
        if (restored || entry?.CleanupCheckpoint is null || operation.Latest is not { } checkpoint)
        { throw new InvalidOperationException("No unused in-memory cleanup checkpoint."); }
        restored = true;
        var result = new So6120DirectDutyAutoExit(operation).Restore(checkpoint, cancellationToken);
        if (result.Success)
        {
            try { Store(() => journal.Complete(binding.Identity.Owner, operation.Latest!.Generation, latestObservation!.Sequence)); }
            catch
            {
                result = result with
                {
                    Failure = So6120DutyFailure.JournalUnresolved,
                    CleanupCheckpoint = null,
                    CommandsFenced = true,
                    DurableFenceConfirmed = operation.DurableFenceConfirmed,
                };
            }
        }
        return result;
    }

    private void BeginInvocation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running) { throw new InvalidOperationException("Reentrant session access is not admitted."); }
        if (blocked) { throw new InvalidOperationException("The session is fenced; no commands are admitted."); }
        running = true;
    }

    private static bool MatchesStamp(So6120AdmissionStamp stamp, So6120SessionBinding expected, TimeProvider clock) =>
        stamp.RunId == expected.Identity.Owner.OperationId && stamp.ConsoleSession == expected.ConsoleSession
        && ReferenceEquals(stamp.Clock, clock);

    private So6120SessionEvidence FreshAdmission()
    {
        EnsureCommands();
        try
        {
            long started = clock.GetTimestamp();
            So6120SessionEvidence evidence = source.Capture();
            var metadata = evidence.Metadata;
            var challenge = evidence.Challenge;
            var handoff = evidence.Handoff;
            if (evidence.Identity != binding.Identity || !MatchesStamp(metadata.Stamp, binding, clock)
                || metadata.StartedTimestamp < started || !MatchesStamp(challenge.Stamp, binding, clock)
                || !MatchesStamp(handoff.Stamp, binding, clock)
                || challenge.DecisionId != binding.Challenge.DecisionId || challenge.EvidenceSha256 != binding.Challenge.EvidenceSha256
                || challenge.ReviewedScope != binding.Challenge.ReviewedScope || challenge.ObservedScope != binding.Challenge.ObservedScope
                || challenge.AcceptedResiduals != binding.Challenge.AcceptedResiduals
                || challenge.ResidualsRequiringAcceptance != binding.Challenge.ResidualsRequiringAcceptance
                || handoff.Origin != binding.Handoff.Origin || handoff.OriginalExit != binding.Handoff.OriginalExit)
            { throw new InvalidOperationException("Session evidence binding changed."); }
            Reassess(evidence);
            return evidence;
        }
        catch { Fence(So6120DutyFailure.AdmissionRefused); throw; }
    }

    // Repeat the freshness check after durable I/O; a successful flush may have
    // consumed the metadata age or transaction budget. The core checks its own
    // single A1+confirmation deadline immediately after the callback returns.
    private void Reassess(So6120SessionEvidence evidence)
    {
        if (admissionPolicy.Assess(clock, evidence.Metadata, evidence.Challenge, evidence.Handoff)
            != So6120AdmissionAssessment.InputsMatched)
        { Fence(So6120DutyFailure.AdmissionRefused); throw new InvalidOperationException("Fresh supervised admission refused."); }
    }

    private void EnsureCommands()
    {
        if (disposed || blocked || !running) { throw new InvalidOperationException("No active admitted session invocation."); }
    }
    private void Store(Action append)
    {
        try { append(); }
        catch { Fence(So6120DutyFailure.JournalUnresolved); throw; }
    }
    private void Fence(So6120DutyFailure reason)
    {
        blocked = true;
        // Stop commands immediately, but retain the core's actual ledger before
        // a durable fence makes all later result records inadmissible. This is
        // not permission to invoke another core or complete a pending command.
        if (transferActive)
        {
            if (deferredFence == So6120DutyFailure.None) { deferredFence = reason; }
            return;
        }
        operation.Fence(reason);
    }

    private So6120MailboxResult Transfer(So6120MailboxRegister register, byte? value, CancellationToken cancellationToken)
    {
        EnsureCommands();
        if (!SessionTransactionAllowed())
        { return new(So6120MailboxFailure.Deadline, null, 0, 0, false, []); }
        Store(() => journal.BeginTransfer(new(Guid.NewGuid(), register, value)));
        long? priorPermissionStarted = permissionStarted;
        TimeSpan transferTimeout = options.TransactionTimeout;
        if (trialExecuting || ownedStopHooks is not null) { cancellationToken = CancellationToken.None; }
        softPublicationRefused = false;
        transferActive = true;
        try
        {
            var result = value is byte written
                ? new So6120HostMailboxTransaction(port, boundary, clock, transferTimeout, options.MaxPolls)
                    .WriteAndConfirm(register, written, cancellationToken)
                : new So6120HostMailboxTransaction(port, boundary, clock, transferTimeout, options.MaxPolls,
                    allowBoardIdentityReads: expectedBoardSsid.HasValue)
                    .Read(register, cancellationToken);
            commandsPossiblySubmitted += result.CommandsPossiblySubmitted;
            commandsCompleted += result.CommandsCompleted;
            if (value.HasValue) { mutationsPossiblySubmitted += result.CommandsPossiblySubmitted; }
            nativeCommandAmbiguity |= result.CommandsPossiblySubmitted != result.CommandsCompleted
                || result.Operations.Any(row => row.ExceptionType is not null || row.Result is not { } native
                    || native.Effect == So6120PortEffect.MayHavePerformed
                    || native.Succeeded && (native.Effect != So6120PortEffect.Performed
                        || row.Operation == So6120PortOperation.ReadData && native.BytesReturned != 1)
                    || !native.Succeeded && native.Effect != So6120PortEffect.NotPerformed);
            bool clean = !blocked && !result.Success && (KnownUnsubmitted(result)
                || softPublicationRefused && result.Failure == So6120MailboxFailure.AdmissionRefused
                    && result.CommandsPossiblySubmitted == 0 && result.CommandsCompleted == 0 && !result.WriteHandlerCompleted);
            if (clean) { permissionStarted = priorPermissionStarted; }
            if (!journal.Faulted)
            {
                try
                {
                    if (clean) { Store(() => journal.RecordTransferNotSubmitted(result)); }
                    else { Store(() => journal.RecordTransferResult(result)); }
                }
                catch { /* Store fenced; preserve the real ledger in memory. */ }
            }
            if (!result.Success && !clean) { Fence(So6120DutyFailure.CommandUnresolved); }
            return result;
        }
        catch
        {
            // Unexpected escape from the transaction boundary cannot establish
            // whether its native command completed.
            nativeCommandAmbiguity = true;
            throw;
        }
        finally
        {
            transferActive = false;
            if (blocked) { operation.Fence(deferredFence == So6120DutyFailure.None ? So6120DutyFailure.JournalUnresolved : deferredFence); }
        }
    }

    private static bool KnownUnsubmitted(So6120MailboxResult result) =>
        result.Failure is So6120MailboxFailure.Cancelled or So6120MailboxFailure.Deadline or So6120MailboxFailure.PortFailure
        && result.CommandsPossiblySubmitted == 0 && result.CommandsCompleted == 0 && !result.WriteHandlerCompleted
        && result.Operations.All(row => row.ExceptionType is null && row.Result is { } native
            && (native.Succeeded && native.Effect == So6120PortEffect.Performed
                && (row.Operation != So6120PortOperation.ReadData || native.BytesReturned == 1)
                || !native.Succeeded && native.Effect == So6120PortEffect.NotPerformed));

    private So6120DutyReadResult ReadSnapshot(CancellationToken cancellationToken)
    {
        EnsureCommands();
        // An aggregate timeout after resolved A0s supplies no snapshot, but
        // does not make those reads ambiguous. The coordinator still fences a
        // missing mandatory post-A1 observation; pre-mutation/dwell can stop
        // and attempt separately admitted Auto from the confirmed checkpoint.
        long started = clock.GetTimestamp();
        if (expectedBoardSsid is ushort expected)
        {
            // The journal names the intended source contract. These separately
            // completed, journaled A0 reads establish its reported EC family
            // before any baseline or fresh pre-write snapshot can be accepted.
            // Existing supervised capture has no family-query authority.
            var low = Transfer(So6120MailboxRegister.BoardSsidLow, null, cancellationToken);
            if (blocked || !low.Success)
            { return new(blocked ? So6120DutyIoDisposition.Unresolved : So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
            if (clock.GetElapsedTime(started) >= options.SnapshotTimeout)
            { return new(So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
            var high = Transfer(So6120MailboxRegister.BoardSsidHigh, null, cancellationToken);
            if (blocked || !high.Success)
            { return new(blocked ? So6120DutyIoDisposition.Unresolved : So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
            ushort observed = (ushort)(low.Value!.Value | high.Value!.Value << 8);
            if (observed != expected)
            {
                BoardIdentityRefusal = $"Reported EC family mismatch (expected SSID={expected:X4}; observed SSID={observed:X4}).";
                // A changed family after any setting publication invalidates
                // ownership, including a confirmed pre-FE staging prefix.
                if (mutationsPossiblySubmitted != 0)
                {
                    Fence(So6120DutyFailure.StateChanged);
                    return new(So6120DutyIoDisposition.Unresolved, null);
                }
                return new(So6120DutyIoDisposition.DefinitelyUnsubmitted, null);
            }
        }
        var values = new byte[SnapshotOrder.Length];
        for (int i = 0; i < SnapshotOrder.Length; i++)
        {
            if (clock.GetElapsedTime(started) >= options.SnapshotTimeout)
            { return new(So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
            var result = Transfer(SnapshotOrder[i], null, cancellationToken);
            if (blocked || !result.Success)
            { return new(blocked ? So6120DutyIoDisposition.Unresolved : So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
            values[i] = result.Value!.Value;
        }
        if (clock.GetElapsedTime(started) >= options.SnapshotTimeout)
        { return new(So6120DutyIoDisposition.DefinitelyUnsubmitted, null); }
        // Fixed sequential A0 order, one clock interval. No atomic snapshot or
        // field-stability/exclusion claim, even when every read is confirmed.
        var controls = new So6120DutyControls(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);
        var observation = new So6120DutyObservation(binding.Identity.Owner, ++observationSequence, started, clock.GetTimestamp(),
            new(controls, values[7], values[8], values[9], values[10], values[11], values[12]));
        Store(() => journal.RecordObservation(observation));
        latestObservation = observation;
        return new(So6120DutyIoDisposition.Confirmed, observation);
    }
    private static readonly So6120MailboxRegister[] SnapshotOrder =
    [
        So6120MailboxRegister.Selector, So6120MailboxRegister.Manual, So6120MailboxRegister.Override,
        So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2, So6120MailboxRegister.Cycle,
        So6120MailboxRegister.PolicyCommand, So6120MailboxRegister.ActualDuty1, So6120MailboxRegister.ActualDuty2,
        So6120MailboxRegister.Tachometer1, So6120MailboxRegister.Tachometer2, So6120MailboxRegister.PolicyStatus,
        So6120MailboxRegister.ActiveState,
    ];

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) { return; }
            if (running) { throw new InvalidOperationException("Cannot dispose reentrantly during a session invocation."); }
            disposed = true;
            blocked = true;
            // No commands, automatic cleanup, retry, or invented completion.
            try { portLifetime.Dispose(); }
            finally { journal.Dispose(); }
        }
    }

    private sealed class Boundary(So6120DirectDutySession session) : ISo6120DirectDutyBoundary, ISo6120MailboxPrerequisites
    {
        public bool ClaimEntry(DirectDutyOwner owner, PerformancePreset capturedProfile)
        {
            session.FreshAdmission();
            session.Store(() => session.journal.ClaimEntry(owner, capturedProfile));
            return true;
        }
        public bool ClaimExit(So6120DutyCheckpoint checkpoint)
        {
            session.FreshAdmission();
            session.Store(() => session.journal.ClaimExit(checkpoint));
            return true;
        }
        public So6120DutyReadResult ReadSnapshot(CancellationToken cancellationToken) => session.ReadSnapshot(cancellationToken);
        public bool RecordIntent(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyObservation observation)
        {
            session.EnsureCommands();
            session.Store(() => session.journal.RecordIntent(owner, mutation, observation));
            return true;
        }
        public bool AdmitWrite(DirectDutyOwner owner, So6120DutyMutation mutation)
        {
            if (!session.SessionEntryAllowed(refresh: false, checkThermal: false)) { return false; }
            session.FreshAdmission();
            return owner == session.binding.Identity.Owner && mutation.Expected == session.latestObservation?.Snapshot.Controls;
        }
        public So6120DutyWriteResult WriteAndConfirm(So6120DutyMutation mutation, So6120DutyObservation before,
            CancellationToken cancellationToken)
        {
            session.EnsureCommands();
            if (!ReferenceEquals(before, session.latestObservation) || before.Snapshot.Controls != mutation.Expected)
            { session.Fence(So6120DutyFailure.StateChanged); return new(So6120DutyIoDisposition.Unresolved, null); }
            if (session.ownedStopHooks is not null && !session.ownedStopRestoring &&
                !session.SessionEntryAllowed(refresh: true))
            {
                return new(So6120DutyIoDisposition.DefinitelyUnsubmitted,
                    new(So6120MailboxFailure.AdmissionRefused, null, 0, 0, false, []));
            }
            if (session.trialExecuting && !session.trialRestoring)
            {
                var plan = session.preparedPlan!;
                if (session.trialStopping ? !plan.AdmitsStopWrite(mutation.Register, before.Snapshot)
                    : !plan.AdmitsLivePwm(before.Snapshot))
                {
                    // PWM/tach drift alone does not invalidate confirmed
                    // mapped-control ownership. Stop before A1 and hand back.
                    session.trialStop = session.trialStopping
                        ? So6120SilentTrialStop.SpinNotObserved : So6120SilentTrialStop.PwmNotObserved;
                    return new(So6120DutyIoDisposition.DefinitelyUnsubmitted,
                        new(So6120MailboxFailure.AdmissionRefused, null, 0, 0, false, []));
                }
                if (!session.EntryAllowed(refresh: true))
                { return new(So6120DutyIoDisposition.DefinitelyUnsubmitted, new(So6120MailboxFailure.AdmissionRefused, null, 0, 0, false, [])); }
            }
            var result = session.Transfer(mutation.Register, mutation.Value, cancellationToken);
            return new(session.blocked ? So6120DutyIoDisposition.Unresolved
                : result.Success ? So6120DutyIoDisposition.Confirmed : So6120DutyIoDisposition.DefinitelyUnsubmitted, result);
        }
        public bool RecordNotSubmitted(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyFailure reason)
        {
            session.EnsureCommands();
            session.Store(() => session.journal.RecordNotSubmitted(owner, mutation, reason));
            return true;
        }
        public bool RecordCheckpoint(So6120DutyCheckpoint checkpoint)
        {
            session.EnsureCommands();
            session.Store(() => session.journal.RecordCheckpoint(checkpoint));
            if (session.trialRestoring && session.permissionStarted.HasValue && session.permissionRestored is null
                && checkpoint.Expected.Override == checkpoint.Baseline.Override)
            { session.permissionRestored = session.clock.GetTimestamp(); }
            return true;
        }
        public bool RecordFence(DirectDutyOwner owner, So6120DutyFailure reason)
        {
            session.blocked = true;
            try { session.journal.RecordFence(owner, reason); return true; }
            catch { return false; }
        }
        public bool Prepare(So6120MailboxRequest request)
        {
            if (request.WriteValue.HasValue && !session.SessionEntryAllowed(refresh: false))
            { session.softPublicationRefused = true; return false; }
            var evidence = session.FreshAdmission();
            session.Store(() => session.journal.PrepareRequest(request));
            session.Reassess(evidence);
            return true;
        }
        public bool AdmitPublication(So6120MailboxRequest request)
        {
            if (request.WriteValue.HasValue && !session.SessionEntryAllowed(refresh: false))
            { session.softPublicationRefused = true; return false; }
            var evidence = session.FreshAdmission();
            session.Store(() => session.journal.RecordPublication(request.Id));
            session.Reassess(evidence);
            if (request.WriteValue.HasValue && !session.SessionEntryAllowed(refresh: false))
            { session.softPublicationRefused = true; return false; }
            if (session.trialExecuting && !session.trialRestoring && request.Register == So6120MailboxRegister.Override
                && request.WriteValue is byte flags && (flags & 0x40) != 0 && session.permissionStarted is null)
            { session.permissionStarted = session.clock.GetTimestamp(); }
            return true;
        }
    }
}
