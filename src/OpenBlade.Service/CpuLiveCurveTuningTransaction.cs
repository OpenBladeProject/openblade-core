using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record CpuLiveCurveBackendObservation(CpuLiveCurveContext? Context, Rz090528CpuLiveObservation? Observation);
internal enum CpuLiveCurveSubmission { NotSubmitted, Acknowledged, Unknown }
internal interface ICpuLiveCurveBackend
{
    // Always fresh, complete and from a trusted context/boot identity source.
    Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token);
    Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic) => ObserveAsync(token);
    Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token);
    // A future native implementation must recheck context, admission and the
    // entire expected-before vector under its same-thread shared mutex lease.
    // Acknowledged means firmware response 1, not merely IOCTL success.
    // NotSubmitted guarantees this call is terminal and never invoked a setter.
    // Return the actual terminal task even after token cancellation. The owner
    // bounds its wait separately and may settle only this exact call's journal.
    Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token);
    Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? peer, CancellationToken token) =>
        peer is null ? SendAsync(context, command, purpose, expectedBefore, token)
            : Task.FromResult(CpuLiveCurveSubmission.NotSubmitted);

}

internal enum CpuLiveCurveOutcome { Verified, Unchanged, Blocked, Uncertain }
internal enum CpuLiveCurveRecovery { None, RestoreReady, Blocked }
internal enum CpuLiveCurveBlockReason
{
    None, InvalidJournal, PendingSubmission, ContextMismatch, ObservationUnavailable,
    StateChanged, InvalidTarget, AdmissionDenied, JournalUnavailable,
}
internal sealed record CpuLiveCurveTransactionResult(CpuLiveCurveOutcome Outcome,
    CpuLiveCurveRecovery Recovery, CpuLiveCurveBlockReason Reason, CpuLiveCurveSnapshot? Observation);

// Each owned edit replaces only the bounded operation; the original baseline
// remains immutable until every owned core is verified back at its original.
internal sealed class CpuLiveCurveTuningTransaction
{
    private readonly ICpuLiveCurveBackend backend;
    private readonly ICpuLiveCurveOwnershipJournalStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan operationTimeout;
    private PreparedOperation? currentPrepared;
    private PendingSubmission? pendingSubmission;

    private sealed record PendingSubmission(Task<CpuLiveCurveSubmission> Completion,
        CpuLiveCurveOwnershipJournal Prior, string PendingHash, PreparedOperation? Operation);

    // Journal-only, called under the controller's live gate. Never wait for an
    // unfinished sender, start a native read, or settle from global last-result
    // state. A failed CAS is final and leaves the ownership record blocked.
    internal async Task<bool> TrySettlePendingSubmissionAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (pendingSubmission is not { Completion.IsCompleted: true } pending) { return false; }
            pendingSubmission = null;
            if (pending.Completion.Status != TaskStatus.RanToCompletion
                || pending.Completion.Result != CpuLiveCurveSubmission.NotSubmitted)
            { _ = pending.Completion.Exception; return false; }
            return await SettleNotSubmittedAsync(pending.Prior, pending.PendingHash, pending.Operation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> SettleNotSubmittedAsync(CpuLiveCurveOwnershipJournal prior, string pendingHash,
        PreparedOperation? operation)
    {
        // With no owned changes, this closes the obligation without claiming a
        // fresh observation or a physical restore. Otherwise retain prior ownership.
        var settled = prior.CompletedSteps == 0 && !prior.Restoring
            && prior.Expected.SequenceEqual(prior.OriginalBaseline)
            && prior.OperationBaseline.SequenceEqual(prior.OriginalBaseline)
            ? prior.ArmRestoration() : prior;
        string? hash = await WriteAsync(pendingHash, settled, CancellationToken.None).ConfigureAwait(false);
        if (hash is null) { return false; }
        operation?.Update(settled, hash);
        return true;
    }

    // A serial, single-use journal ticket, not native write authority.
    internal sealed class PreparedOperation(CpuLiveCurveContext context, CpuLiveCurveSnapshot expected,
        CpuLiveCurveOwnershipJournal? journal, string? hash, bool execute)
    {
        internal CpuLiveCurveContext Context { get; } = context;
        internal CpuLiveCurveSnapshot Expected { get; private set; } = expected;
        internal CpuLiveCurveOwnershipJournal? Journal { get; private set; } = journal;
        internal string? Hash { get; private set; } = hash;
        internal bool NeedsExecution { get; set; } = execute;
        internal bool Started { get; set; }
        internal bool Closed { get; private set; }
        internal CpuLiveCurvePeerState PeerState => new(Expected,
            Journal is { State: not CpuLiveCurveOwnershipState.Restored } journal
                ? CpuLiveCurveOwnershipJournal.Snapshot(journal.OriginalBaseline) : null);
        internal void Close() => Closed = true;
        internal void Update(CpuLiveCurveOwnershipJournal value, string next)
        { Journal = value; Hash = next; Expected = CpuLiveCurveOwnershipJournal.Snapshot(value.Expected)!; }
    }
    internal sealed record Preparation(CpuLiveCurveTransactionResult Result, PreparedOperation? Operation);

    internal async Task<Preparation> PrepareApplyAsync(CpuCurveOptimizerSettings? settings, CancellationToken token)
    {
        PreparedOperation? operation = null;
        var result = await ApplyAsync(observation => settings is null ? [] : settings.IsWellFormed
            ? observation.CoreOffsets.Select(core => new CpuLiveCurveTarget(core.PhysicalCoreId, settings.EffectiveValue)).ToArray()
            : null, token, value => operation = value).ConfigureAwait(false);
        return new(result, operation);
    }

    internal async Task<Preparation> PrepareRestoreAsync(CancellationToken token)
    {
        PreparedOperation? operation = null;
        var result = await RestoreCoreAsync(token, value => operation = value).ConfigureAwait(false);
        return new(result, operation);
    }

    internal CpuLiveCurveTuningTransaction(ICpuLiveCurveBackend backend, ICpuLiveCurveOwnershipJournalStore store,
        TimeSpan? operationTimeout = null)
    {
        this.backend = backend; this.store = store;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromSeconds(10))
        { throw new ArgumentOutOfRangeException(nameof(operationTimeout)); }
    }

    internal Task<CpuLiveCurveTransactionResult> ApplyAllCoreAsync(CpuCurveOptimizerSettings? settings, CancellationToken token) =>
        ApplyAsync(observation => settings is null ? [] : settings.IsWellFormed
            ? observation.CoreOffsets.Select(core => new CpuLiveCurveTarget(core.PhysicalCoreId, settings.EffectiveValue)).ToArray()
            : null, token);

    internal Task<CpuLiveCurveTransactionResult> ApplySelectiveAsync(IReadOnlyList<CpuLiveCurveTarget>? targets, CancellationToken token)
    {
        if (targets is { Count: > 12 }) { return Task.FromResult(Blocked(CpuLiveCurveBlockReason.InvalidTarget)); }
        var copy = targets?.ToArray() ?? [];
        return ApplyAsync(_ => copy, token);
    }

    private async Task<CpuLiveCurveTransactionResult> ApplyAsync(
        Func<Rz090528CpuLiveObservation, IReadOnlyList<CpuLiveCurveTarget>?> getTargets, CancellationToken token,
        Action<PreparedOperation>? prepared = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            currentPrepared = null;
            var loaded = await LoadAsync(token).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLiveCurveBlockReason.InvalidJournal); }
            if (loaded.Journal is { State: CpuLiveCurveOwnershipState.PossibleSubmission or CpuLiveCurveOwnershipState.Blocked })
            { return await ObserveCoreAsync(loaded, token).ConfigureAwait(false); }
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            var owned = loaded.Journal is { State: not CpuLiveCurveOwnershipState.Restored } ? loaded.Journal : null;
            if (owned is not null && observed?.Context is { } currentContext && !owned.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(owned, loaded.Sha256!, CpuLiveCurveBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out var baseline)) { return Blocked(CpuLiveCurveBlockReason.ObservationUnavailable); }
            if (owned is not null && !Matches(owned, observed, owned.Expected, out baseline))
            { return await BlockJournalAsync(owned, loaded.Sha256!, MismatchReason(owned, observed), baseline).ConfigureAwait(false); }
            var context = observed!.Context!;
            var frozen = new Rz090528CpuLiveObservation(CpuLiveCurveSnapshot.SupportedMask,
                CpuLiveCurveSnapshot.SupportedPmVersion, CpuLiveCurveSnapshot.SupportedPmLength, baseline!.CoreOffsets);
            var targets = getTargets(frozen);
            if (targets is null) { return Blocked(CpuLiveCurveBlockReason.InvalidTarget, baseline); }
            var shape = Rz090528CpuLiveCurvePlan.CreateSelective(frozen, targets, null);
            if (shape.Status == CpuLiveCurvePlanStatus.Unchanged)
            {
                if (prepared is not null)
                { prepared(currentPrepared = new(context, baseline!, loaded.Journal, loaded.Sha256, false)); }
                return new(CpuLiveCurveOutcome.Unchanged, owned is null ? CpuLiveCurveRecovery.None : CpuLiveCurveRecovery.RestoreReady,
                    CpuLiveCurveBlockReason.None, baseline);
            }
            if (shape.Status != CpuLiveCurvePlanStatus.AdmissionDenied) { return Blocked(CpuLiveCurveBlockReason.InvalidTarget, baseline); }

            var admitted = new HashSet<(CpuLiveCurveCommand, CpuLiveCurveCommandPurpose)>();
            foreach (var target in targets)
            {
                short prior = baseline!.CoreOffsets.Single(core => core.PhysicalCoreId == target.PhysicalCoreId).Offset;
                if (prior == target.Offset) { continue; }
                _ = CpuLiveCurveCommand.TryCreate(target.PhysicalCoreId, target.Offset, CpuLiveCurveSnapshot.SupportedMask, out var forward);
                _ = CpuLiveCurveCommand.TryCreate(target.PhysicalCoreId, prior, CpuLiveCurveSnapshot.SupportedMask, out var reverse);
                if (!await AdmitAsync(context, forward, CpuLiveCurveCommandPurpose.Forward, token).ConfigureAwait(false)
                    || !await AdmitAsync(context, reverse, CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false))
                { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, baseline); }
                admitted.Add((forward, CpuLiveCurveCommandPurpose.Forward));
                admitted.Add((reverse, CpuLiveCurveCommandPurpose.Restore));
            }
            var plan = Rz090528CpuLiveCurvePlan.CreateSelective(frozen, targets,
                (command, purpose) => admitted.Contains((command, purpose))).Plan;
            if (plan is null) { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, baseline); }
            // The immediate reverse is not necessarily the original restore.
            // Admit originals for prior owned cores and every newly changed
            // core, including a core whose target happens to be its original.
            var originals = owned?.OriginalBaseline ?? baseline!.CoreOffsets;
            foreach (var original in originals)
            {
                if (baseline!.CoreOffsets.Single(core => core.PhysicalCoreId == original.PhysicalCoreId).Offset == original.Offset
                    && !plan.Steps.Any(step => step.Command.PhysicalCoreId == original.PhysicalCoreId)) { continue; }
                _ = CpuLiveCurveCommand.TryCreate(original.PhysicalCoreId, original.Offset,
                    CpuLiveCurveSnapshot.SupportedMask, out var restore);
                if (!await AdmitAsync(context, restore, CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false))
                { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, baseline); }
            }
            var journal = CpuLiveCurveOwnershipJournal.Prepare(context, plan, owned);
            string? hash = await WriteAsync(loaded.Sha256, journal, token).ConfigureAwait(false);
            if (hash is null) { return Blocked(CpuLiveCurveBlockReason.JournalUnavailable, baseline); }
            if (prepared is not null)
            {
                prepared(currentPrepared = new(context, baseline!, journal, hash, true));
                return new(CpuLiveCurveOutcome.Unchanged, CpuLiveCurveRecovery.RestoreReady, CpuLiveCurveBlockReason.None, baseline);
            }
            return await ExecuteAsync(journal, hash, restoring: false, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal Task<CpuLiveCurveTransactionResult> RestoreAsync(CancellationToken token) => RestoreCoreAsync(token);

    private async Task<CpuLiveCurveTransactionResult> RestoreCoreAsync(CancellationToken token,
        Action<PreparedOperation>? prepared = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            currentPrepared = null;
            var loaded = await LoadAsync(token).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLiveCurveBlockReason.InvalidJournal); }
            if (loaded.Journal is not { State: CpuLiveCurveOwnershipState.Prepared or CpuLiveCurveOwnershipState.OwnedVerified } journal)
            { return await ObserveCoreAsync(loaded, token).ConfigureAwait(false); }
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            if (observed?.Context is { } currentContext && !journal.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(journal, loaded.Sha256!, CpuLiveCurveBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out var snapshot)) { return Blocked(CpuLiveCurveBlockReason.ObservationUnavailable); }
            if (!Matches(journal, observed, journal.Expected, out snapshot))
            { return await BlockJournalAsync(journal, loaded.Sha256!, MismatchReason(journal, observed), snapshot).ConfigureAwait(false); }
            var restoration = journal.ArmRestoration();
            foreach (var step in restoration.Steps.Reverse())
            {
                if (!await AdmitAsync(journal.Context, step.Command(true), CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false))
                { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot); }
            }
            // Persist the complete original restoration before any setter.
            // Prepared may already own changes made by earlier operations.
            string? hash = await WriteAsync(loaded.Sha256, restoration, token).ConfigureAwait(false);
            if (hash is null) { return Blocked(CpuLiveCurveBlockReason.JournalUnavailable, snapshot); }
            if (prepared is not null)
            {
                prepared(currentPrepared = new(journal.Context, snapshot!, restoration, hash,
                    restoration.State != CpuLiveCurveOwnershipState.Restored));
                return new(CpuLiveCurveOutcome.Unchanged, restoration.State == CpuLiveCurveOwnershipState.Restored
                    ? CpuLiveCurveRecovery.None : CpuLiveCurveRecovery.RestoreReady, CpuLiveCurveBlockReason.None, snapshot);
            }
            if (restoration.State == CpuLiveCurveOwnershipState.Restored)
            { return new(CpuLiveCurveOutcome.Verified, CpuLiveCurveRecovery.None, CpuLiveCurveBlockReason.None, snapshot); }
            return await ExecuteAsync(restoration, hash, restoring: true, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuLiveCurveTransactionResult> ObserveOwnershipAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ObserveCoreAsync(await LoadAsync(token).ConfigureAwait(false), token, diagnostic).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CpuLiveCurveTransactionResult> ObserveCoreAsync(CpuLiveCurveJournalLoadResult loaded,
        CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        if (loaded.Invalid) { return Blocked(CpuLiveCurveBlockReason.InvalidJournal); }
        // A pending sender might still run. Do not start another native getter
        // through this interface merely to make an uncertain result look fresh.
        if (loaded.Journal is { State: CpuLiveCurveOwnershipState.PossibleSubmission }) { return Uncertain(); }
        if (loaded.Journal is { State: CpuLiveCurveOwnershipState.Blocked }) { return Blocked(CpuLiveCurveBlockReason.StateChanged); }
        var observed = await ObserveAsync(token, diagnostic).ConfigureAwait(false);
        if (loaded.Journal is { State: not CpuLiveCurveOwnershipState.Restored } owned
            && observed?.Context is { } currentContext && !owned.Context.MatchesHardwareContext(currentContext))
        { return await BlockJournalAsync(owned, loaded.Sha256!, CpuLiveCurveBlockReason.ContextMismatch, null).ConfigureAwait(false); }
        if (!Valid(observed, out var snapshot)) { return Blocked(CpuLiveCurveBlockReason.ObservationUnavailable); }
        if (loaded.Journal is null or { State: CpuLiveCurveOwnershipState.Restored })
        { return new(CpuLiveCurveOutcome.Unchanged, CpuLiveCurveRecovery.None, CpuLiveCurveBlockReason.None, snapshot); }
        if (!Matches(loaded.Journal, observed, loaded.Journal.Expected, out snapshot))
        { return await BlockJournalAsync(loaded.Journal, loaded.Sha256!, MismatchReason(loaded.Journal, observed), snapshot).ConfigureAwait(false); }
        return new(CpuLiveCurveOutcome.Blocked, CpuLiveCurveRecovery.RestoreReady, CpuLiveCurveBlockReason.None, snapshot);
    }

    internal async Task<CpuLiveCurveTransactionResult> ValidatePreparedAsync(PreparedOperation operation, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CpuLiveCurveTransactionResult> ValidatePreparedCoreAsync(PreparedOperation operation, CancellationToken token)
    {
        if (!ReferenceEquals(currentPrepared, operation) || operation.Closed)
        { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied); }
        var loaded = await LoadAsync(token).ConfigureAwait(false);
        if (loaded.Invalid) { return Blocked(CpuLiveCurveBlockReason.InvalidJournal); }
        if (loaded.Journal is { State: CpuLiveCurveOwnershipState.PossibleSubmission }) { return Uncertain(); }
        if (loaded.Sha256 != operation.Hash || loaded.Journal is { State: CpuLiveCurveOwnershipState.Blocked })
        { return Blocked(CpuLiveCurveBlockReason.StateChanged); }
        var observed = await ObserveAsync(token).ConfigureAwait(false);
        if (observed?.Context is { } currentContext && !operation.Context.MatchesHardwareContext(currentContext))
        {
            return loaded.Journal is { State: not CpuLiveCurveOwnershipState.Restored } owned
                ? await BlockJournalAsync(owned, loaded.Sha256!, CpuLiveCurveBlockReason.ContextMismatch, null).ConfigureAwait(false)
                : Blocked(CpuLiveCurveBlockReason.ContextMismatch);
        }
        if (!Valid(observed, out var snapshot)) { return Blocked(CpuLiveCurveBlockReason.ObservationUnavailable); }
        if (!operation.Context.MatchesHardwareContext(observed!.Context) || !operation.Expected.CoreOffsets.SequenceEqual(snapshot!.CoreOffsets))
        {
            var reason = !operation.Context.MatchesHardwareContext(observed.Context) ? CpuLiveCurveBlockReason.ContextMismatch : CpuLiveCurveBlockReason.StateChanged;
            return loaded.Journal is { State: not CpuLiveCurveOwnershipState.Restored } owned
                ? await BlockJournalAsync(owned, loaded.Sha256!, reason, snapshot).ConfigureAwait(false) : Blocked(reason, snapshot);
        }
        if (!await AdmitRemainingAsync(operation.Journal, operation.NeedsExecution, token).ConfigureAwait(false))
        { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot); }
        return new(CpuLiveCurveOutcome.Unchanged, loaded.Journal is null or { State: CpuLiveCurveOwnershipState.Restored }
            ? CpuLiveCurveRecovery.None : CpuLiveCurveRecovery.RestoreReady, CpuLiveCurveBlockReason.None, snapshot);
    }

    internal async Task<CpuLiveCurveTransactionResult> ExecutePreparedAsync(PreparedOperation operation,
        Func<CancellationToken, Task<bool>>? beforeStep, CancellationToken token, CpuLivePowerPeerState? peer = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (operation.Started) { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied); }
            operation.Started = true;
            var validation = await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false);
            if (validation.Outcome != CpuLiveCurveOutcome.Unchanged || !operation.NeedsExecution) { return validation; }
            return await ExecuteAsync(operation.Journal!, operation.Hash!, operation.Journal!.Restoring,
                token, operation, beforeStep, peer).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Persist confirmed loss of an active coupled operation's peer. This does
    // not establish ownership for an unchanged family with no journal.
    internal async Task<CpuLiveCurveTransactionResult> RejectPreparedAsync(PreparedOperation operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(currentPrepared, operation) || operation.Closed)
            { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied); }
            var loaded = await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLiveCurveBlockReason.InvalidJournal); }
            if (loaded.Journal is { State: CpuLiveCurveOwnershipState.PossibleSubmission }) { return Uncertain(); }
            if (loaded.Journal is null or { State: CpuLiveCurveOwnershipState.Blocked })
            { return Blocked(CpuLiveCurveBlockReason.StateChanged); }
            if (loaded.Journal.Steps.Count == 0) { return Blocked(CpuLiveCurveBlockReason.StateChanged); }
            if (loaded.Sha256 != operation.Hash) { return Blocked(CpuLiveCurveBlockReason.JournalUnavailable); }
            return await BlockJournalAsync(loaded.Journal, loaded.Sha256!, CpuLiveCurveBlockReason.StateChanged,
                operation.Expected).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> AdmitRemainingAsync(CpuLiveCurveOwnershipJournal? journal, bool execute, CancellationToken token)
    {
        if (journal is null or { State: CpuLiveCurveOwnershipState.Restored }) { return !token.IsCancellationRequested; }
        CpuLiveCurveStoredStep[] remaining = !execute ? [] : journal.Restoring ? journal.Steps.Take(journal.CompletedSteps).ToArray()
            : journal.Steps.Skip(journal.CompletedSteps).ToArray();
        foreach (var step in remaining)
        {
            if (!await AdmitAsync(journal.Context, step.Command(journal.Restoring), journal.Restoring
                ? CpuLiveCurveCommandPurpose.Restore : CpuLiveCurveCommandPurpose.Forward, token).ConfigureAwait(false)
                || !journal.Restoring && !await AdmitAsync(journal.Context, step.Command(true), CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false))
            { return false; }
        }
        foreach (var original in journal.OriginalBaseline)
        {
            if (journal.Expected.Single(core => core.PhysicalCoreId == original.PhysicalCoreId).Offset == original.Offset
                && !remaining.Any(step => step.PhysicalCoreId == original.PhysicalCoreId)) { continue; }
            _ = CpuLiveCurveCommand.TryCreate(original.PhysicalCoreId, original.Offset, CpuLiveCurveSnapshot.SupportedMask, out var command);
            if (!await AdmitAsync(journal.Context, command, CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false)) { return false; }
        }
        return !token.IsCancellationRequested;
    }

    private async Task<CpuLiveCurveTransactionResult> ExecuteAsync(CpuLiveCurveOwnershipJournal journal, string hash,
        bool restoring, CancellationToken token, PreparedOperation? operation = null,
        Func<CancellationToken, Task<bool>>? beforeStep = null, CpuLivePowerPeerState? peer = null)
    {
        CpuLiveCurveSnapshot? snapshot = null;
        while (restoring ? journal.CompletedSteps > 0 : journal.CompletedSteps < journal.Steps.Count)
        {
            if (beforeStep is not null && !await beforeStep(token).ConfigureAwait(false))
            { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot); }
            if (operation is not null)
            {
                var validation = await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false);
                if (validation.Outcome != CpuLiveCurveOutcome.Unchanged) { return validation; }
            }
            int index = restoring ? journal.CompletedSteps - 1 : journal.CompletedSteps;
            var step = journal.Steps[index];
            var purpose = restoring ? CpuLiveCurveCommandPurpose.Restore : CpuLiveCurveCommandPurpose.Forward;
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            if (observed?.Context is { } currentContext && !journal.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(journal, hash, CpuLiveCurveBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out snapshot)) { return Blocked(CpuLiveCurveBlockReason.ObservationUnavailable); }
            if (!Matches(journal, observed, journal.Expected, out snapshot))
            { return await BlockJournalAsync(journal, hash, MismatchReason(journal, observed), snapshot).ConfigureAwait(false); }
            if (!await AdmitAsync(journal.Context, step.Command(restoring), purpose, token).ConfigureAwait(false)
                || !restoring && !await AdmitAsync(journal.Context, step.Command(true), CpuLiveCurveCommandPurpose.Restore, token).ConfigureAwait(false))
            { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot); }
            if (!await AdmitRemainingAsync(journal, execute: true, token).ConfigureAwait(false))
            { return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot); }
            var pending = journal.Progress(CpuLiveCurveOwnershipState.PossibleSubmission, journal.CompletedSteps, restoring);
            string? pendingHash = await WriteAsync(hash, pending, token).ConfigureAwait(false);
            if (pendingHash is null) { return Blocked(CpuLiveCurveBlockReason.JournalUnavailable, snapshot); }
            operation?.Update(pending, pendingHash);
            CpuLiveCurveSubmission submission;
            Task<CpuLiveCurveSubmission>? actualSubmission = null;
            try
            {
                token.ThrowIfCancellationRequested();
                submission = await BoundedAsync(t => actualSubmission = backend.SendAsync(journal.Context, step.Command(restoring),
                    purpose, snapshot!, peer, t), token).ConfigureAwait(false);
            }
            catch
            {
                if (actualSubmission is not null)
                { pendingSubmission = new(actualSubmission, journal, pendingHash, operation); }
                return Uncertain(snapshot);
            }
            if (submission == CpuLiveCurveSubmission.NotSubmitted)
            {
                if (!await SettleNotSubmittedAsync(journal, pendingHash, operation).ConfigureAwait(false)) { return Uncertain(snapshot); }
                return Blocked(CpuLiveCurveBlockReason.AdmissionDenied, snapshot);
            }
            if (submission != CpuLiveCurveSubmission.Acknowledged || token.IsCancellationRequested) { return Uncertain(snapshot); }
            int completed = journal.CompletedSteps + (restoring ? -1 : 1);
            observed = await ObserveAsync(token).ConfigureAwait(false);
            if (!Matches(journal, observed, journal.ExpectedAt(completed).CoreOffsets, out snapshot)
                || token.IsCancellationRequested) { return Uncertain(snapshot); }
            bool finished = restoring ? completed == 0 : completed == journal.Steps.Count;
            var verified = journal.Progress(finished && snapshot!.CoreOffsets.SequenceEqual(journal.OriginalBaseline)
                ? CpuLiveCurveOwnershipState.Restored
                : CpuLiveCurveOwnershipState.OwnedVerified, completed, restoring);
            string? verifiedHash = await WriteAsync(pendingHash, verified, token).ConfigureAwait(false);
            if (verifiedHash is null) { return Uncertain(snapshot); }
            journal = verified; hash = verifiedHash;
            operation?.Update(journal, hash);
        }
        if (operation is not null) { operation.NeedsExecution = false; }
        return new(CpuLiveCurveOutcome.Verified, journal.State == CpuLiveCurveOwnershipState.Restored
            ? CpuLiveCurveRecovery.None : CpuLiveCurveRecovery.RestoreReady,
            CpuLiveCurveBlockReason.None, snapshot);
    }

    private async Task<CpuLiveCurveJournalLoadResult> LoadAsync(CancellationToken token)
    {
        try
        {
            var loaded = await store.LoadAsync(token).ConfigureAwait(false);
            return loaded.Invalid || loaded.Journal is { IsWellFormed: false }
                || loaded.Journal is not null && loaded.Sha256 is null || loaded.Journal is null && loaded.Sha256 is not null
                ? new(null, true, null) : loaded;
        }
        catch { return new(null, true, null); }
    }
    private async Task<string?> WriteAsync(string? hash, CpuLiveCurveOwnershipJournal journal, CancellationToken token)
    {
        // Never detach a timed-out journal writer: a late persistence completion
        // must not race a later CAS or silently advance ownership in background.
        string? next;
        try { next = await store.TryWriteAsync(hash, journal, token).ConfigureAwait(false); }
        catch { next = null; }
        return next;
    }
    private async Task<CpuLiveCurveTransactionResult> BlockJournalAsync(CpuLiveCurveOwnershipJournal journal,
        string hash, CpuLiveCurveBlockReason reason, CpuLiveCurveSnapshot? snapshot)
    {
        string? blockedHash = await WriteAsync(hash, journal.Progress(CpuLiveCurveOwnershipState.Blocked,
            journal.CompletedSteps, journal.Restoring), CancellationToken.None).ConfigureAwait(false);
        return Blocked(blockedHash is null ? CpuLiveCurveBlockReason.JournalUnavailable : reason, snapshot);
    }
    private async Task<CpuLiveCurveBackendObservation?> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic = null)
    {
        using var stage = diagnostic?.BeginStage("curve backend observation");
        try { return await BoundedAsync(t => backend.ObserveAsync(t, diagnostic), token, diagnostic).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { diagnostic?.RecordObservationCancellation(token.IsCancellationRequested); return null; }
        catch (Exception exception)
        { diagnostic?.Check(false, "curve observation exception " + CpuQueryDiagnostic.FromException(CpuQueryStage.Approval, exception).Failure); return null; }
    }
    private async Task<bool> AdmitAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
        CpuLiveCurveCommandPurpose purpose, CancellationToken token)
    {
        try { return await BoundedAsync(t => backend.CheckWriteAdmissionAsync(context, command, purpose, t), token).ConfigureAwait(false); }
        catch { return false; }
    }
    private async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token,
        CpuSavedAdmissionDiagnostic? observationDiagnostic = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // The observation's synchronous admission prefix can still be running
        // when its deadline fires. Capture its stage then, before unwinding it.
        using var registration = observationDiagnostic is null ? default : timeout.Token.Register(() =>
            observationDiagnostic.RecordObservationCancellation(token.IsCancellationRequested));
        timeout.CancelAfter(operationTimeout);
        Task<T>? task = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            task = operation(timeout.Token);
            return await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            if (task is not null)
            {
                _ = task.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            throw;
        }
    }

    private static bool Valid(CpuLiveCurveBackendObservation? observed, out CpuLiveCurveSnapshot? snapshot)
    {
        snapshot = CpuLiveCurveSnapshot.TryCreate(observed?.Observation);
        return observed?.Context is { IsWellFormed: true } && snapshot is not null;
    }
    private static bool Matches(CpuLiveCurveOwnershipJournal journal, CpuLiveCurveBackendObservation? observed,
        IReadOnlyList<CpuLiveCoreOffset> expected, out CpuLiveCurveSnapshot? snapshot) =>
        Valid(observed, out snapshot) && journal.Context.MatchesHardwareContext(observed!.Context) && snapshot!.CoreOffsets.SequenceEqual(expected);
    private static CpuLiveCurveBlockReason MismatchReason(CpuLiveCurveOwnershipJournal journal, CpuLiveCurveBackendObservation? observed) =>
        !journal.Context.MatchesHardwareContext(observed?.Context) ? CpuLiveCurveBlockReason.ContextMismatch : CpuLiveCurveBlockReason.StateChanged;
    private static CpuLiveCurveTransactionResult Blocked(CpuLiveCurveBlockReason reason, CpuLiveCurveSnapshot? snapshot = null) =>
        new(CpuLiveCurveOutcome.Blocked, CpuLiveCurveRecovery.Blocked, reason, snapshot);
    private static CpuLiveCurveTransactionResult Uncertain(CpuLiveCurveSnapshot? snapshot = null) =>
        new(CpuLiveCurveOutcome.Uncertain, CpuLiveCurveRecovery.Blocked, CpuLiveCurveBlockReason.PendingSubmission, snapshot);
}
