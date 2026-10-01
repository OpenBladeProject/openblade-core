using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record CpuLivePowerBackendObservation(CpuLivePowerContext? Context, CpuPmRawSnapshot? Observation,
    CpuLivePowerFusedCap? FusedCap, CpuLivePowerCapability? Capability, CpuLivePowerTrustedRange? Range);
internal enum CpuLivePowerSubmission { Unknown, NotSubmitted, Acknowledged }
internal interface ICpuLivePowerBackend
{
    // Independently fresh context/continuity and requested-state interpretation,
    // not raw PM shape or equality. A no-op setter is never an observation.
    Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token);
    Task<bool> CheckWriteAdmissionAsync(CpuLivePowerContext context, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token);
    // Recheck fresh context, range, the entire before pair and independent write
    // proof under the native lease. Acknowledged requires firmware response 1.
    // NotSubmitted guarantees terminal clean cleanup and no setter invocation.
    // Implementations must retain/exclude an actual worker across cancellation
    // and backend replacement, including observation/admission callbacks.
    Task<CpuLivePowerSubmission> SendAsync(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token);
}

internal enum CpuLivePowerOutcome { Verified, Unchanged, Blocked, Uncertain }
internal enum CpuLivePowerRecovery { None, RestoreReady, Blocked }
internal enum CpuLivePowerBlockReason
{
    None, InvalidJournal, PendingSubmission, ContextMismatch, ObservationUnavailable,
    StateChanged, InvalidTarget, AdmissionDenied, JournalUnavailable,
}
internal sealed record CpuLivePowerTransactionResult(CpuLivePowerOutcome Outcome, CpuLivePowerRecovery Recovery,
    CpuLivePowerBlockReason Reason, CpuLivePowerPair? Observation);

// Default composition supplies no concrete native provider. This transaction
// consumes independently trusted admission; metadata, equality and planning grant no proof.
internal sealed class CpuLivePowerTuningTransaction
{
    private readonly ICpuLivePowerBackend backend;
    private readonly ICpuLivePowerOwnershipJournalStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan operationTimeout;
    private bool persistenceUncertain;
    private PreparedOperation? currentPrepared;

    // The ticket binds an exact journal revision; it grants no native authority.
    internal sealed class PreparedOperation(CpuLivePowerContext context, CpuLivePowerPair expected,
        CpuLivePowerOwnershipJournal? journal, string? hash, bool execute, CpuLivePowerWriteScope scope)
    {
        internal CpuLivePowerContext Context { get; } = context;
        internal CpuLivePowerPair Expected { get; private set; } = expected;
        internal CpuLivePowerOwnershipJournal? Journal { get; private set; } = journal;
        internal string? Hash { get; private set; } = hash;
        internal CpuLivePowerWriteScope Scope { get; } = scope;
        internal bool NeedsExecution { get; set; } = execute;
        internal bool Started { get; set; }
        internal bool Closed { get; private set; }
        internal void Close() => Closed = true;
        internal void Update(CpuLivePowerOwnershipJournal value, string next)
        { Journal = value; Hash = next; Expected = value.Expected.Decode()!; }
    }
    internal sealed record Preparation(CpuLivePowerTransactionResult Result, PreparedOperation? Operation);

    internal async Task<Preparation> PrepareApplyAsync(IReadOnlyList<CpuLivePowerTarget>? targets, CancellationToken token)
    {
        PreparedOperation? operation = null;
        var result = await ApplyCoreAsync(targets, token, value => operation = value).ConfigureAwait(false);
        return new(result, operation);
    }

    internal async Task<Preparation> PrepareRestoreAsync(CancellationToken token)
    {
        PreparedOperation? operation = null;
        var result = await RestoreCoreAsync(token, value => operation = value).ConfigureAwait(false);
        return new(result, operation);
    }

    internal CpuLivePowerTuningTransaction(ICpuLivePowerBackend backend, ICpuLivePowerOwnershipJournalStore store,
        TimeSpan? operationTimeout = null)
    {
        this.backend = backend; this.store = store;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromSeconds(10))
        { throw new ArgumentOutOfRangeException(nameof(operationTimeout)); }
    }

    internal Task<CpuLivePowerTransactionResult> ApplySelectiveAsync(IReadOnlyList<CpuLivePowerTarget>? targets, CancellationToken token) =>
        ApplyCoreAsync(targets, token);

    private async Task<CpuLivePowerTransactionResult> ApplyCoreAsync(IReadOnlyList<CpuLivePowerTarget>? targets, CancellationToken token,
        Action<PreparedOperation>? prepared = null)
    {
        if (targets is { Count: > 2 }) { return Blocked(CpuLivePowerBlockReason.InvalidTarget); }
        var copy = targets?.ToArray() ?? [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            currentPrepared = null;
            if (persistenceUncertain) { return Uncertain(); }
            var loaded = await LoadAsync(token).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLivePowerBlockReason.InvalidJournal); }
            if (loaded.Journal is { State: CpuLivePowerOwnershipState.PossibleSubmission or CpuLivePowerOwnershipState.Blocked })
            { return await ObserveCoreAsync(loaded, token).ConfigureAwait(false); }
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            var owned = loaded.Journal is { State: not CpuLivePowerOwnershipState.Restored } ? loaded.Journal : null;
            if (owned is not null && observed?.Context is { } currentContext && !owned.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(owned, loaded.Sha256!, CpuLivePowerBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out var baseline)) { return Blocked(CpuLivePowerBlockReason.ObservationUnavailable); }
            if (owned is not null && !Matches(owned, observed, owned.Expected.Decode()!, out baseline))
            { return await BlockJournalAsync(owned, loaded.Sha256!, MismatchReason(owned, observed), baseline).ConfigureAwait(false); }
            var shape = Rz090528CpuLivePowerPlan.CreateSelective(observed!.Observation, copy,
                observed.FusedCap, observed.Capability, observed.Range, null);
            if (shape.Status == CpuLivePowerPlanStatus.Unchanged)
            {
                if (prepared is not null)
                { prepared(currentPrepared = new(observed.Context!, baseline!, loaded.Journal, loaded.Sha256, false, new())); }
                return new(CpuLivePowerOutcome.Unchanged, owned is null ? CpuLivePowerRecovery.None : CpuLivePowerRecovery.RestoreReady,
                    CpuLivePowerBlockReason.None, baseline);
            }
            if (shape.Status != CpuLivePowerPlanStatus.AdmissionDenied)
            {
                return Blocked(shape.Status == CpuLivePowerPlanStatus.InvalidTarget ? CpuLivePowerBlockReason.InvalidTarget
                    : CpuLivePowerBlockReason.AdmissionDenied, baseline);
            }

            var original = owned?.OriginalBaseline.Decode() ?? baseline!;
            var scope = new CpuLivePowerWriteScope(); var before = baseline!;
            var admitted = new HashSet<(CpuLivePowerCommand, CpuLivePowerCommandPurpose)>();
            foreach (var target in copy.OrderBy(value => value.Limit))
            {
                uint next = (uint)target.WholeWatts * 1000;
                if (before.Get(target.Limit).Milliwatts == next) { continue; }
                _ = CpuLivePowerCommand.TryCreate(target.Limit, next, out var command);
                var request = new CpuLivePowerNativeRequest(scope, command, CpuLivePowerCommandPurpose.Forward, before, original);
                if (!await AdmitOperationAsync(observed, request, token).ConfigureAwait(false))
                { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, baseline); }
                admitted.Add((command, CpuLivePowerCommandPurpose.Forward));
                admitted.Add((request.ImmediateReverse, CpuLivePowerCommandPurpose.Restore));
                before = request.After;
            }
            var plan = Rz090528CpuLivePowerPlan.CreateSelective(observed.Observation, copy,
                observed.FusedCap, observed.Capability, observed.Range, (command, purpose) => admitted.Contains((command, purpose))).Plan;
            if (plan is null || token.IsCancellationRequested) { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, baseline); }
            var journal = CpuLivePowerOwnershipJournal.Prepare(observed.Context!, plan, owned);
            string? hash = await WriteAsync(loaded.Sha256, journal, token).ConfigureAwait(false);
            if (hash is null) { return Blocked(CpuLivePowerBlockReason.JournalUnavailable, baseline); }
            if (prepared is not null)
            {
                prepared(currentPrepared = new(observed.Context!, baseline!, journal, hash, true, scope));
                return new(CpuLivePowerOutcome.Unchanged, CpuLivePowerRecovery.RestoreReady, CpuLivePowerBlockReason.None, baseline);
            }
            return await ExecuteAsync(journal, hash, scope, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal Task<CpuLivePowerTransactionResult> RestoreAsync(CancellationToken token) => RestoreCoreAsync(token);

    private async Task<CpuLivePowerTransactionResult> RestoreCoreAsync(CancellationToken token,
        Action<PreparedOperation>? prepared = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            currentPrepared = null;
            if (persistenceUncertain) { return Uncertain(); }
            var loaded = await LoadAsync(token).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLivePowerBlockReason.InvalidJournal); }
            if (loaded.Journal is not { State: CpuLivePowerOwnershipState.Prepared or CpuLivePowerOwnershipState.OwnedVerified } journal)
            { return await ObserveCoreAsync(loaded, token).ConfigureAwait(false); }
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            if (observed?.Context is { } currentContext && !journal.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(journal, loaded.Sha256!, CpuLivePowerBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out var baseline)) { return Blocked(CpuLivePowerBlockReason.ObservationUnavailable); }
            if (!Matches(journal, observed, journal.Expected.Decode()!, out baseline))
            { return await BlockJournalAsync(journal, loaded.Sha256!, MismatchReason(journal, observed), baseline).ConfigureAwait(false); }
            var restoration = journal.ArmRestoration(); var scope = new CpuLivePowerWriteScope();
            for (int index = 0; index < restoration.Steps.Count; index++)
            {
                if (!await AdmitOperationAsync(observed!, Request(restoration, index, scope), token).ConfigureAwait(false))
                { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, baseline); }
            }
            if (token.IsCancellationRequested) { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, baseline); }
            string? hash = await WriteAsync(loaded.Sha256, restoration, token).ConfigureAwait(false);
            if (hash is null) { return Blocked(CpuLivePowerBlockReason.JournalUnavailable, baseline); }
            if (prepared is not null)
            {
                prepared(currentPrepared = new(journal.Context, baseline!, restoration, hash,
                    restoration.State != CpuLivePowerOwnershipState.Restored, scope));
                return new(CpuLivePowerOutcome.Unchanged, restoration.State == CpuLivePowerOwnershipState.Restored
                    ? CpuLivePowerRecovery.None : CpuLivePowerRecovery.RestoreReady, CpuLivePowerBlockReason.None, baseline);
            }
            if (restoration.State == CpuLivePowerOwnershipState.Restored)
            { return new(CpuLivePowerOutcome.Verified, CpuLivePowerRecovery.None, CpuLivePowerBlockReason.None, baseline); }
            return await ExecuteAsync(restoration, hash, scope, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuLivePowerTransactionResult> ObserveOwnershipAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return persistenceUncertain ? Uncertain() : await ObserveCoreAsync(await LoadAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CpuLivePowerTransactionResult> ObserveCoreAsync(CpuLivePowerJournalLoadResult loaded, CancellationToken token)
    {
        if (loaded.Invalid) { return Blocked(CpuLivePowerBlockReason.InvalidJournal); }
        // A late actual worker may still run: never use another getter or setter
        // to infer that an uncertain submission completed or can be replayed.
        if (loaded.Journal is { State: CpuLivePowerOwnershipState.PossibleSubmission }) { return Uncertain(); }
        if (loaded.Journal is { State: CpuLivePowerOwnershipState.Blocked }) { return Blocked(CpuLivePowerBlockReason.StateChanged); }
        var observed = await ObserveAsync(token).ConfigureAwait(false);
        if (loaded.Journal is { State: not CpuLivePowerOwnershipState.Restored } owned
            && observed?.Context is { } currentContext && !owned.Context.MatchesHardwareContext(currentContext))
        { return await BlockJournalAsync(owned, loaded.Sha256!, CpuLivePowerBlockReason.ContextMismatch, null).ConfigureAwait(false); }
        if (!Valid(observed, out var pair)) { return Blocked(CpuLivePowerBlockReason.ObservationUnavailable); }
        if (loaded.Journal is null or { State: CpuLivePowerOwnershipState.Restored })
        { return new(CpuLivePowerOutcome.Unchanged, CpuLivePowerRecovery.None, CpuLivePowerBlockReason.None, pair); }
        if (!Matches(loaded.Journal, observed, loaded.Journal.Expected.Decode()!, out pair))
        { return await BlockJournalAsync(loaded.Journal, loaded.Sha256!, MismatchReason(loaded.Journal, observed), pair).ConfigureAwait(false); }
        return new(CpuLivePowerOutcome.Blocked, CpuLivePowerRecovery.RestoreReady, CpuLivePowerBlockReason.None, pair);
    }

    internal async Task<CpuLivePowerTransactionResult> ValidatePreparedAsync(PreparedOperation operation, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CpuLivePowerTransactionResult> ValidatePreparedCoreAsync(PreparedOperation operation, CancellationToken token)
    {
        if (persistenceUncertain) { return Uncertain(); }
        if (!ReferenceEquals(currentPrepared, operation) || operation.Closed)
        { return Blocked(CpuLivePowerBlockReason.AdmissionDenied); }
        var loaded = await LoadAsync(token).ConfigureAwait(false);
        if (loaded.Invalid) { return Blocked(CpuLivePowerBlockReason.InvalidJournal); }
        if (loaded.Journal is { State: CpuLivePowerOwnershipState.PossibleSubmission }) { return Uncertain(); }
        if (loaded.Sha256 != operation.Hash || loaded.Journal is { State: CpuLivePowerOwnershipState.Blocked })
        { return Blocked(CpuLivePowerBlockReason.StateChanged); }
        var observed = await ObserveAsync(token).ConfigureAwait(false);
        if (observed?.Context is { } currentContext && !operation.Context.MatchesHardwareContext(currentContext))
        {
            return loaded.Journal is { State: not CpuLivePowerOwnershipState.Restored } owned
                ? await BlockJournalAsync(owned, loaded.Sha256!, CpuLivePowerBlockReason.ContextMismatch, null).ConfigureAwait(false)
                : Blocked(CpuLivePowerBlockReason.ContextMismatch);
        }
        if (!Valid(observed, out var pair)) { return Blocked(CpuLivePowerBlockReason.ObservationUnavailable); }
        if (!operation.Context.MatchesHardwareContext(observed!.Context) || operation.Expected != pair)
        {
            var reason = !operation.Context.MatchesHardwareContext(observed.Context) ? CpuLivePowerBlockReason.ContextMismatch : CpuLivePowerBlockReason.StateChanged;
            return loaded.Journal is { State: not CpuLivePowerOwnershipState.Restored } owned
                ? await BlockJournalAsync(owned, loaded.Sha256!, reason, pair).ConfigureAwait(false) : Blocked(reason, pair);
        }
        if (operation.Journal is { State: not CpuLivePowerOwnershipState.Restored } journal)
        {
            var remaining = operation.NeedsExecution ? journal : journal.ArmRestoration();
            for (int index = remaining.CompletedSteps; index < remaining.Steps.Count; index++)
            {
                if (!await AdmitOperationAsync(observed, Request(remaining, index, operation.Scope), token).ConfigureAwait(false))
                { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, pair); }
            }
        }
        if (token.IsCancellationRequested) { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, pair); }
        return new(CpuLivePowerOutcome.Unchanged, loaded.Journal is null or { State: CpuLivePowerOwnershipState.Restored }
            ? CpuLivePowerRecovery.None : CpuLivePowerRecovery.RestoreReady, CpuLivePowerBlockReason.None, pair);
    }

    internal async Task<CpuLivePowerTransactionResult> ExecutePreparedAsync(PreparedOperation operation,
        Func<CancellationToken, Task<bool>>? beforeStep, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (operation.Started) { return Blocked(CpuLivePowerBlockReason.AdmissionDenied); }
            operation.Started = true;
            var validation = await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false);
            if (validation.Outcome != CpuLivePowerOutcome.Unchanged || !operation.NeedsExecution) { return validation; }
            return await ExecuteAsync(operation.Journal!, operation.Hash!, operation.Scope, token, operation, beforeStep).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuLivePowerTransactionResult> RejectPreparedAsync(PreparedOperation operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(currentPrepared, operation) || operation.Closed)
            { return Blocked(CpuLivePowerBlockReason.AdmissionDenied); }
            if (persistenceUncertain) { return Uncertain(); }
            var loaded = await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (loaded.Invalid) { return Blocked(CpuLivePowerBlockReason.InvalidJournal); }
            if (loaded.Journal is { State: CpuLivePowerOwnershipState.PossibleSubmission }) { return Uncertain(); }
            if (loaded.Journal is null or { State: CpuLivePowerOwnershipState.Blocked })
            { return Blocked(CpuLivePowerBlockReason.StateChanged); }
            if (loaded.Journal.Steps.Count == 0) { return Blocked(CpuLivePowerBlockReason.StateChanged); }
            if (loaded.Sha256 != operation.Hash) { return Blocked(CpuLivePowerBlockReason.JournalUnavailable); }
            return await BlockJournalAsync(loaded.Journal, loaded.Sha256!, CpuLivePowerBlockReason.StateChanged,
                operation.Expected).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<CpuLivePowerTransactionResult> ExecuteAsync(CpuLivePowerOwnershipJournal journal, string hash,
        CpuLivePowerWriteScope scope, CancellationToken token, PreparedOperation? operation = null,
        Func<CancellationToken, Task<bool>>? beforeStep = null)
    {
        CpuLivePowerPair? pair = null;
        while (journal.CompletedSteps < journal.Steps.Count)
        {
            if (beforeStep is not null && !await beforeStep(token).ConfigureAwait(false))
            { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, pair); }
            if (operation is not null)
            {
                var validation = await ValidatePreparedCoreAsync(operation, token).ConfigureAwait(false);
                if (validation.Outcome != CpuLivePowerOutcome.Unchanged) { return validation; }
            }
            var request = Request(journal, journal.CompletedSteps, scope);
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            if (observed?.Context is { } currentContext && !journal.Context.MatchesHardwareContext(currentContext))
            { return await BlockJournalAsync(journal, hash, CpuLivePowerBlockReason.ContextMismatch, null).ConfigureAwait(false); }
            if (!Valid(observed, out pair)) { return Blocked(CpuLivePowerBlockReason.ObservationUnavailable); }
            if (!Matches(journal, observed, request.Before, out pair))
            { return await BlockJournalAsync(journal, hash, MismatchReason(journal, observed), pair).ConfigureAwait(false); }
            if (!await AdmitOperationAsync(observed!, request, token).ConfigureAwait(false) || token.IsCancellationRequested)
            { return Blocked(CpuLivePowerBlockReason.AdmissionDenied, pair); }
            var pending = journal.Progress(CpuLivePowerOwnershipState.PossibleSubmission, journal.CompletedSteps);
            string? pendingHash = await WriteAsync(hash, pending, token).ConfigureAwait(false);
            if (pendingHash is null) { return Blocked(CpuLivePowerBlockReason.JournalUnavailable, pair); }
            operation?.Update(pending, pendingHash);
            CpuLivePowerSubmission submission;
            try
            {
                token.ThrowIfCancellationRequested();
                submission = await BoundedAsync(t => backend.SendAsync(journal.Context, request, t), token).ConfigureAwait(false);
            }
            catch { return Uncertain(pair); }
            if (submission == CpuLivePowerSubmission.NotSubmitted && !token.IsCancellationRequested)
            {
                string? reverted = await WriteAsync(pendingHash, journal, CancellationToken.None).ConfigureAwait(false);
                if (reverted is null) { return Uncertain(pair); }
                operation?.Update(journal, reverted);
                return Blocked(CpuLivePowerBlockReason.AdmissionDenied, pair);
            }
            if (submission != CpuLivePowerSubmission.Acknowledged || token.IsCancellationRequested) { return Uncertain(pair); }
            observed = await ObserveAsync(token).ConfigureAwait(false);
            if (!Matches(journal, observed, request.After, out pair) || !RangeAllows(observed!, request)
                || token.IsCancellationRequested) { return Uncertain(pair); }
            int completed = journal.CompletedSteps + 1;
            var verified = journal.Progress(completed == journal.Steps.Count && pair == request.Original
                ? CpuLivePowerOwnershipState.Restored : CpuLivePowerOwnershipState.OwnedVerified, completed);
            string? verifiedHash = await WriteAsync(pendingHash, verified, token).ConfigureAwait(false);
            if (verifiedHash is null) { return Uncertain(pair); }
            journal = verified; hash = verifiedHash;
            operation?.Update(journal, hash);
        }
        if (operation is not null) { operation.NeedsExecution = false; }
        return new(CpuLivePowerOutcome.Verified, journal.State == CpuLivePowerOwnershipState.Restored
            ? CpuLivePowerRecovery.None : CpuLivePowerRecovery.RestoreReady, CpuLivePowerBlockReason.None, pair);
    }

    private static CpuLivePowerNativeRequest Request(CpuLivePowerOwnershipJournal journal, int index, CpuLivePowerWriteScope scope) =>
        new(scope, journal.Steps[index].Command(), journal.Restoring ? CpuLivePowerCommandPurpose.Restore : CpuLivePowerCommandPurpose.Forward,
            journal.ExpectedAt(index), journal.OriginalBaseline.Decode()!);

    private async Task<bool> AdmitOperationAsync(CpuLivePowerBackendObservation observed, CpuLivePowerNativeRequest request, CancellationToken token)
    {
        if (!RangeAllows(observed, request)) { return false; }
        if (!await AdmitAsync(observed.Context!, request, request.Command, request.Purpose, token).ConfigureAwait(false)
            || request.Purpose == CpuLivePowerCommandPurpose.Forward
                && !await AdmitAsync(observed.Context!, request, request.ImmediateReverse, CpuLivePowerCommandPurpose.Restore, token).ConfigureAwait(false))
        { return false; }
        foreach (var original in request.OriginalRestores)
        {
            if (!await AdmitAsync(observed.Context!, request, original, CpuLivePowerCommandPurpose.Restore, token).ConfigureAwait(false)) { return false; }
        }
        return !token.IsCancellationRequested;
    }
    private static bool RangeAllows(CpuLivePowerBackendObservation observed, CpuLivePowerNativeRequest request) =>
        observed.FusedCap is { } cap && observed.Capability is { } capability && observed.Range is { } range
        && range.AllowsOperation(request, capability.RawValue, cap);
    private async Task<bool> AdmitAsync(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CpuLivePowerCommand command,
        CpuLivePowerCommandPurpose purpose, CancellationToken token)
    {
        try { return await BoundedAsync(t => backend.CheckWriteAdmissionAsync(context, command, purpose, request.Before, request.Original, t), token).ConfigureAwait(false); }
        catch { return false; }
    }
    private async Task<CpuLivePowerBackendObservation?> ObserveAsync(CancellationToken token)
    {
        try { return await BoundedAsync(backend.ObserveAsync, token).ConfigureAwait(false); }
        catch { return null; }
    }
    private async Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token)
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
    private async Task<string?> WriteAsync(string? hash, CpuLivePowerOwnershipJournal journal, CancellationToken token)
    {
        // Await persistence even if cancellation fires; never detach a writer
        // that could later race CAS or advance ownership without this caller.
        string? next;
        try { next = await store.TryWriteAsync(hash, journal, token).ConfigureAwait(false); }
        catch { next = null; }
        if (next is null) { persistenceUncertain = true; }
        return next;
    }
    private async Task<CpuLivePowerTransactionResult> BlockJournalAsync(CpuLivePowerOwnershipJournal journal, string hash,
        CpuLivePowerBlockReason reason, CpuLivePowerPair? pair)
    {
        string? blocked = await WriteAsync(hash, journal.Progress(CpuLivePowerOwnershipState.Blocked, journal.CompletedSteps), CancellationToken.None).ConfigureAwait(false);
        return Blocked(blocked is null ? CpuLivePowerBlockReason.JournalUnavailable : reason, pair);
    }
    private async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(operationTimeout); Task<T>? task = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested(); task = operation(timeout.Token);
            var result = await task.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested(); return result;
        }
        finally
        {
            // Retain the CTS until the actual callback finishes. Late completion
            // only releases it/observes faults; it never mutates the journal.
            if (task is { IsCompleted: false })
            {
                _ = task.ContinueWith(completed => { _ = completed.Exception; timeout.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else { if (task is not null) { _ = task.Exception; } timeout.Dispose(); }
        }
    }
    private static bool Valid(CpuLivePowerBackendObservation? observed, out CpuLivePowerPair? pair)
    {
        pair = CpuLivePowerSnapshot.TryCreate(observed?.Observation)?.Pair;
        return observed?.Context is { IsWellFormed: true } && pair is not null;
    }
    private static bool Matches(CpuLivePowerOwnershipJournal journal, CpuLivePowerBackendObservation? observed,
        CpuLivePowerPair expected, out CpuLivePowerPair? pair) => Valid(observed, out pair) && journal.Context.MatchesHardwareContext(observed!.Context) && pair == expected;
    private static CpuLivePowerBlockReason MismatchReason(CpuLivePowerOwnershipJournal journal, CpuLivePowerBackendObservation? observed) =>
        !journal.Context.MatchesHardwareContext(observed?.Context) ? CpuLivePowerBlockReason.ContextMismatch : CpuLivePowerBlockReason.StateChanged;
    private static CpuLivePowerTransactionResult Blocked(CpuLivePowerBlockReason reason, CpuLivePowerPair? pair = null) =>
        new(CpuLivePowerOutcome.Blocked, CpuLivePowerRecovery.Blocked, reason, pair);
    private static CpuLivePowerTransactionResult Uncertain(CpuLivePowerPair? pair = null) =>
        new(CpuLivePowerOutcome.Uncertain, CpuLivePowerRecovery.Blocked, CpuLivePowerBlockReason.PendingSubmission, pair);
}
