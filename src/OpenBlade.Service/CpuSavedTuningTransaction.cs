using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal enum CpuSavedTuningCommandKind { CurveOptimizer, PowerLimit, ResetPowerLimit }

internal sealed record CpuSavedTuningCommand(CpuSavedTuningCommandKind Kind, int Value)
{
    internal bool IsWellFormed => Kind switch
    {
        CpuSavedTuningCommandKind.CurveOptimizer => Value is >= -30 and <= 0,
        CpuSavedTuningCommandKind.PowerLimit => Value is >= 45 and <= 95,
        CpuSavedTuningCommandKind.ResetPowerLimit => Value == 0,
        _ => false,
    };

    internal byte[] Encode()
    {
        if (!IsWellFormed) { throw new InvalidOperationException("Invalid saved CPU command."); }
        if (Kind == CpuSavedTuningCommandKind.CurveOptimizer)
        {
            return Rz090528CpuFirmwareCodec.EncodeCurveOptimizer(Value != 0, Value == 0 ? -1 : Value);
        }
        if (Kind == CpuSavedTuningCommandKind.PowerLimit)
        {
            return Rz090528CpuFirmwareCodec.EncodePowerLimit(Value);
        }
        // Static reset candidate only; the backend must independently admit
        // this restoration route before even the forward power command runs.
        byte[] command = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(command, Rz090528CpuFirmwareCodec.PowerLimitFunction);
        return command;
    }
}

internal enum CpuSavedTuningSubmission { NotSubmitted, Acknowledged, Unknown }

internal interface ICpuSavedTuningBackend
{
    // Fresh exact identity, provider version, competing-controller and feature
    // admission are required for every command, including reverse commands.
    Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token);
    Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token);
    Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token);
}

internal enum CpuSavedTuningOutcome { Verified, Unchanged, Blocked, Uncertain }
internal enum CpuSavedTuningBlockReason
{
    None, InvalidJournal, PendingSubmission, UnrestorableBaseline, AdmissionDenied,
    StateChanged, JournalUnavailable, IdentityMismatch,
}

internal sealed record CpuSavedTuningTransactionResult(
    CpuSavedTuningOutcome Outcome,
    SavedCpuFirmwareReadResult? Observation,
    CpuTuningPendingBoot PendingBoot,
    bool RestorePending,
    CpuSavedTuningBlockReason Reason = CpuSavedTuningBlockReason.None)
{
    internal CpuTuningRecoveryStatus Recovery { get; init; } = new(CpuTuningRecoveryState.Unknown, CpuTuningFeatures.None);
}

internal static class CpuSavedTuningPlan
{
    internal static bool IsExactIdentity(DeviceIdentity? identity) => identity is
    { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6, BiosVersion: "2.02" };

    internal static CpuSavedTuningStep[]? Create(byte[] baseline, CpuTuningSettings settings)
    {
        if (baseline.Length != Rz090528CpuFirmwareCodec.SavedConfigurationLength || !settings.IsWellFormed)
        {
            return null;
        }
        var steps = new List<CpuSavedTuningStep>();
        byte[] current = (byte[])baseline.Clone();
        if (settings.PowerLimitWatts is { } watts
            && !Add(new(CpuSavedTuningCommandKind.PowerLimit, watts))) { return null; }
        if (settings.CurveOptimizer is { } curve
            && !Add(new(CpuSavedTuningCommandKind.CurveOptimizer, curve.EffectiveValue))) { return null; }
        return steps.ToArray();

        bool Add(CpuSavedTuningCommand command)
        {
            byte[] after = Simulate(current, command);
            if (after.AsSpan().SequenceEqual(current)) { return true; }
            CpuSavedTuningCommand? reverse = Reverse(current, command.Kind);
            if (reverse is null || !Simulate(after, reverse).AsSpan().SequenceEqual(current)) { return false; }
            steps.Add(new(command, reverse, current, after));
            current = after;
            return true;
        }
    }

    private static CpuSavedTuningCommand? Reverse(byte[] before, CpuSavedTuningCommandKind kind)
    {
        Rz090528SavedCpuConfiguration saved = Rz090528CpuFirmwareCodec.DecodeSaved(before);
        if (kind == CpuSavedTuningCommandKind.CurveOptimizer)
        {
            // Positive, per-core and unknown modes are retained in the full
            // baseline but have no admitted exact typed restoration route.
            return before[0xC0] == 1 && before[0xC1] == 1
                && saved.AllCoreCurveOptimizer is >= -30 and <= 0
                    ? new(CpuSavedTuningCommandKind.CurveOptimizer, saved.AllCoreCurveOptimizer.Value)
                    : null;
        }
        if (saved.HasCustomPowerLimit && saved.PowerLimitMilliwatts is >= 45000 and <= 95000
            && saved.PowerLimitMilliwatts % 1000 == 0)
        {
            return new(CpuSavedTuningCommandKind.PowerLimit, checked((int)saved.PowerLimitMilliwatts / 1000));
        }
        return saved.PolicyMode == 2 && saved.PowerLimitMode == 0xFF && saved.PowerLimitFields.All(value => value == 0)
            ? new(CpuSavedTuningCommandKind.ResetPowerLimit, 0)
            : null;
    }

    internal static byte[] Simulate(byte[] before, CpuSavedTuningCommand command)
    {
        if (before.Length != Rz090528CpuFirmwareCodec.SavedConfigurationLength || !command.IsWellFormed)
        {
            throw new ArgumentException("Invalid saved CPU transition.");
        }
        byte[] after = (byte[])before.Clone();
        after[0x94] = 2;
        if (command.Kind == CpuSavedTuningCommandKind.CurveOptimizer)
        {
            after[0xC0] = 1;
            after[0xC1] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(after.AsSpan(0xC2), checked((ushort)-command.Value));
        }
        else
        {
            after[0x95] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(0x96), checked((uint)command.Value * 1000));
        }
        // BIOS 2.02 AodSmm callback 0x4A78: only +0x96 is changed by
        // 0x50001. Common completion checks all five fields, including CO.
        if (Rz090528CpuFirmwareCodec.DecodeSaved(after).PowerLimitFields.All(value => value == 0))
        {
            after[0x95] = 0xFF;
        }
        return after;
    }
}

internal sealed class CpuSavedTuningTransaction(ICpuSavedTuningBackend backend, ICpuTuningOwnershipJournalStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<CpuSavedTuningTransactionResult> ApplyAsync(
        DeviceIdentity identity, CpuTuningSettings settings, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CpuTuningOwnershipJournalLoadResult loaded = await LoadJournalAsync(token).ConfigureAwait(false);
            if (!loaded.Invalid && loaded.Journal is { State: CpuTuningOwnershipState.OwnedVerified } owned
                && owned.Identity == identity && loaded.Sha256 is not null
                && settings is { IsWellFormed: true, IsConfigured: true })
            {
                return await UpdateOwnedAsync(owned, loaded.Sha256, settings, token).ConfigureAwait(false);
            }
            if (loaded.Invalid || loaded.Journal is { State: not CpuTuningOwnershipState.Restored })
            {
                return await ObserveCoreAsync(identity, loaded, token).ConfigureAwait(false);
            }
            if (!CpuSavedTuningPlan.IsExactIdentity(identity) || settings is not { IsWellFormed: true, IsConfigured: true })
            {
                return Blocked(null, false);
            }
            SavedCpuFirmwareReadResult observation = await backend.ReadAsync(identity, token).ConfigureAwait(false);
            if (!TryGetPair(observation, out byte[]? baseline)) { return Blocked(observation, false); }
            CpuSavedTuningStep[]? steps = CpuSavedTuningPlan.Create(baseline!, settings);
            if (steps is null) { return Blocked(observation, false, CpuSavedTuningBlockReason.UnrestorableBaseline); }
            if (steps.Length == 0)
            {
                // Matching settings do not establish ownership.
                return new(CpuSavedTuningOutcome.Unchanged, observation, CpuTuningPendingBoot.Unknown, false);
            }
            // Reject the complete plan before any write if one reverse route
            // is unadmitted, including the separately gated zero-PPT reset.
            foreach (CpuSavedTuningStep step in steps)
            {
                if (!await backend.CheckWriteAdmissionAsync(identity, step.Command, token).ConfigureAwait(false)
                    || !await backend.CheckWriteAdmissionAsync(identity, step.RestoreCommand, token).ConfigureAwait(false))
                {
                    return Blocked(observation, false, CpuSavedTuningBlockReason.AdmissionDenied);
                }
            }
            var journal = new CpuTuningOwnershipJournal(1, Guid.NewGuid(), identity, settings,
                baseline!, observation.Last!.CopyBytes(), steps, CpuTuningOwnershipState.Prepared,
                0, false, DateTimeOffset.UtcNow);
            string? hash = await store.TryWriteAsync(loaded.Sha256, journal, token).ConfigureAwait(false);
            if (hash is null) { return Blocked(observation, true, CpuSavedTuningBlockReason.JournalUnavailable); }
            return await ExecuteAsync(journal, hash, restoring: false, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuSavedTuningTransactionResult> RestoreAsync(DeviceIdentity identity, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CpuTuningOwnershipJournalLoadResult loaded = await LoadJournalAsync(token).ConfigureAwait(false);
            if (!loaded.Invalid && loaded.Journal is { State: CpuTuningOwnershipState.Prepared } prepared
                && prepared.Identity == identity && loaded.Sha256 is not null)
            {
                SavedCpuFirmwareReadResult observation = await backend.ReadAsync(identity, token).ConfigureAwait(false);
                if (!Matches(observation, prepared.BaselineCurrent)) { return Blocked(observation, true, CpuSavedTuningBlockReason.StateChanged); }
                string? hash = await store.TryWriteAsync(loaded.Sha256, prepared with
                {
                    State = CpuTuningOwnershipState.Restored,
                    Restoring = true,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                }, token).ConfigureAwait(false);
                return hash is null ? Blocked(observation, true, CpuSavedTuningBlockReason.JournalUnavailable)
                    : new(CpuSavedTuningOutcome.Verified, observation, CpuTuningPendingBoot.Unknown, false);
            }
            if (loaded.Invalid || loaded.Journal is not { State: CpuTuningOwnershipState.OwnedVerified } journal
                || journal.Identity != identity || loaded.Sha256 is null)
            {
                return await ObserveCoreAsync(identity, loaded, token).ConfigureAwait(false);
            }
            // Admit the entire exact reverse plan before the first submission.
            // A curve command must not partially restore a plan whose later
            // power-limit reverse route has lost admission.
            foreach (CpuSavedTuningStep step in journal.Steps.Take(journal.CompletedSteps).Reverse())
            {
                if (!await backend.CheckWriteAdmissionAsync(identity, step.RestoreCommand, token).ConfigureAwait(false))
                {
                    return Blocked(null, true, CpuSavedTuningBlockReason.AdmissionDenied);
                }
            }
            return await ExecuteAsync(journal, loaded.Sha256, restoring: true, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuSavedTuningTransactionResult> ObserveOwnershipAsync(DeviceIdentity identity, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ObserveCoreAsync(identity, await LoadJournalAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<CpuSavedTuningTransactionResult> ObserveOwnershipAsync(
        DeviceIdentity identity, SavedCpuFirmwareReadResult freshObservation, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ObserveCoreAsync(identity, await LoadJournalAsync(token).ConfigureAwait(false), token, freshObservation).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<CpuTuningOwnershipJournalLoadResult> LoadJournalAsync(CancellationToken token)
    {
        CpuTuningOwnershipJournalLoadResult loaded = await store.LoadAsync(token).ConfigureAwait(false);
        return loaded.Journal is { IsWellFormed: false } || loaded.Journal is not null && loaded.Sha256 is null
            ? new(null, true, null) : loaded;
    }

    private async Task<CpuSavedTuningTransactionResult> ObserveCoreAsync(
        DeviceIdentity identity, CpuTuningOwnershipJournalLoadResult loaded, CancellationToken token,
        SavedCpuFirmwareReadResult? freshObservation = null)
    {
        if (loaded.Invalid)
        {
            return Blocked(null, true, CpuSavedTuningBlockReason.InvalidJournal) with
            { Recovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.None) };
        }
        CpuTuningOwnershipJournal? journal = loaded.Journal;
        bool pending = journal is { State: not CpuTuningOwnershipState.Restored };
        if (!CpuSavedTuningPlan.IsExactIdentity(identity) || journal is not null && journal.Identity != identity)
        {
            return Blocked(null, pending, CpuSavedTuningBlockReason.IdentityMismatch) with
            { Recovery = new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.None) };
        }
        SavedCpuFirmwareReadResult observation = freshObservation ?? await backend.ReadAsync(identity, token).ConfigureAwait(false);
        bool owned = journal is { State: CpuTuningOwnershipState.OwnedVerified }
            && Matches(observation, journal.ExpectedCurrent);
        CpuTuningFeatures features = CpuTuningFeatures.None;
        if (journal is not null)
        {
            foreach (CpuSavedTuningStep step in journal.Steps.Take(journal.CompletedSteps))
            { features |= Feature(step.RestoreCommand); }
            if (journal.State == CpuTuningOwnershipState.PossibleSubmission)
            {
                CpuSavedTuningCommand possible = journal.PendingUpdateStep?.Command
                    ?? (journal.Restoring ? journal.Steps[journal.CompletedSteps - 1].RestoreCommand
                        : journal.Steps[journal.CompletedSteps].Command);
                features |= Feature(possible);
            }
        }
        CpuTuningRecoveryState recovery = journal is null or { State: CpuTuningOwnershipState.Restored }
            ? CpuTuningRecoveryState.None
            : owned || journal.State == CpuTuningOwnershipState.Prepared && Matches(observation, journal.BaselineCurrent)
                ? CpuTuningRecoveryState.RestoreReady : CpuTuningRecoveryState.Blocked;
        // Read-only reconciliation can identify drift, but cannot prove that
        // a timed-out provider has finished or turn ambiguity into authority.
        return new(journal?.State == CpuTuningOwnershipState.PossibleSubmission
                ? CpuSavedTuningOutcome.Uncertain : CpuSavedTuningOutcome.Blocked,
            observation, owned ? CpuTuningPendingBoot.Requested : CpuTuningPendingBoot.Unknown, pending,
            journal?.State == CpuTuningOwnershipState.PossibleSubmission
                ? CpuSavedTuningBlockReason.PendingSubmission : CpuSavedTuningBlockReason.StateChanged)
        { Recovery = new(recovery, features) };
    }

    private static CpuTuningFeatures Feature(CpuSavedTuningCommand command) =>
        command.Kind == CpuSavedTuningCommandKind.CurveOptimizer
            ? CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.PowerLimit;

    private async Task<CpuSavedTuningTransactionResult> ExecuteAsync(
        CpuTuningOwnershipJournal journal, string hash, bool restoring, CancellationToken token)
    {
        SavedCpuFirmwareReadResult? observation = null;
        while (restoring ? journal.CompletedSteps > 0 : journal.CompletedSteps < journal.Steps.Length)
        {
            token.ThrowIfCancellationRequested();
            int index = restoring ? journal.CompletedSteps - 1 : journal.CompletedSteps;
            CpuSavedTuningStep step = journal.Steps[index];
            CpuSavedTuningCommand command = restoring ? step.RestoreCommand : step.Command;
            byte[] before = restoring ? step.After : step.Before;
            byte[] after = restoring ? step.Before : step.After;
            observation = await backend.ReadAsync(journal.Identity, token).ConfigureAwait(false);
            if (!Matches(observation, before))
            {
                return Blocked(observation, true, CpuSavedTuningBlockReason.StateChanged);
            }
            if (!await backend.CheckWriteAdmissionAsync(journal.Identity, command, token).ConfigureAwait(false)
                || !restoring && !await backend.CheckWriteAdmissionAsync(journal.Identity, step.RestoreCommand, token).ConfigureAwait(false))
            {
                return Blocked(observation, true, CpuSavedTuningBlockReason.AdmissionDenied);
            }
            var submitted = journal with
            {
                State = CpuTuningOwnershipState.PossibleSubmission,
                Restoring = restoring,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            string? submittedHash = await store.TryWriteAsync(hash, submitted, token).ConfigureAwait(false);
            if (submittedHash is null) { return Blocked(observation, true, CpuSavedTuningBlockReason.JournalUnavailable); }

            CpuSavedTuningSubmission submission;
            try
            {
                token.ThrowIfCancellationRequested();
                submission = await backend.SendAsync(journal.Identity, command, token).ConfigureAwait(false);
            }
            catch
            {
                // Exceptions, timeout and cancellation after the durable marker
                // are ambiguous. Never retry, roll back or await an unbounded
                // task here; the backend owns a bounded native operation.
                return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false);
            }
            if (submission == CpuSavedTuningSubmission.NotSubmitted)
            {
                // Sender explicitly guarantees the provider was never invoked.
                string? reset = await store.TryWriteAsync(submittedHash, journal, CancellationToken.None).ConfigureAwait(false);
                return reset is null ? await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false)
                    : Blocked(observation, true, CpuSavedTuningBlockReason.AdmissionDenied);
            }
            if (submission != CpuSavedTuningSubmission.Acknowledged || token.IsCancellationRequested)
            {
                return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false);
            }
            try
            {
                observation = await backend.ReadAsync(journal.Identity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            if (!Matches(observation, after))
            {
                return new(CpuSavedTuningOutcome.Uncertain, observation, CpuTuningPendingBoot.Unknown, true);
            }
            int completed = journal.CompletedSteps + (restoring ? -1 : 1);
            journal = journal with
            {
                State = completed == 0 ? CpuTuningOwnershipState.Restored : CpuTuningOwnershipState.OwnedVerified,
                CompletedSteps = completed,
                Restoring = restoring,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            string? verifiedHash;
            try { verifiedHash = await store.TryWriteAsync(submittedHash, journal, token).ConfigureAwait(false); }
            catch { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            if (verifiedHash is null) { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            hash = verifiedHash;
        }
        return new(CpuSavedTuningOutcome.Verified, observation,
            restoring ? CpuTuningPendingBoot.Unknown : CpuTuningPendingBoot.Requested, !restoring);
    }

    private async Task<CpuSavedTuningTransactionResult> UpdateOwnedAsync(
        CpuTuningOwnershipJournal journal, string hash, CpuTuningSettings requested, CancellationToken token)
    {
        SavedCpuFirmwareReadResult observation = await backend.ReadAsync(journal.Identity, token).ConfigureAwait(false);
        if (!Matches(observation, journal.ExpectedCurrent)) { return Blocked(observation, true, CpuSavedTuningBlockReason.StateChanged); }
        CpuSavedTuningStep[]? changes = CpuSavedTuningPlan.Create(journal.ExpectedCurrent, requested);
        if (changes is null) { return Blocked(observation, true, CpuSavedTuningBlockReason.UnrestorableBaseline); }
        if (changes.Length == 0)
        {
            return new(CpuSavedTuningOutcome.Unchanged, observation, CpuTuningPendingBoot.Requested, true);
        }
        foreach (CpuSavedTuningStep step in changes.Concat(journal.Steps.Take(journal.CompletedSteps)))
        {
            if (!await backend.CheckWriteAdmissionAsync(journal.Identity, step.Command, token).ConfigureAwait(false)
                || !await backend.CheckWriteAdmissionAsync(journal.Identity, step.RestoreCommand, token).ConfigureAwait(false))
            {
                return Blocked(observation, true, CpuSavedTuningBlockReason.AdmissionDenied);
            }
        }
        foreach (CpuSavedTuningStep change in changes)
        {
            // Retain the first baseline while replacing the bounded current
            // restore plan. A pending edit retains both the old confirmed plan
            // and its exact new transition, never an unbounded command history.
            CpuTuningSettings currentSettings = new(
                journal.Steps.Take(journal.CompletedSteps).Any(step => step.Command.Kind == CpuSavedTuningCommandKind.PowerLimit)
                    ? journal.Settings.PowerLimitWatts : null,
                journal.Steps.Take(journal.CompletedSteps).Any(step => step.Command.Kind == CpuSavedTuningCommandKind.CurveOptimizer)
                    ? journal.Settings.CurveOptimizer : null);
            CpuTuningSettings updatedSettings = change.Command.Kind == CpuSavedTuningCommandKind.PowerLimit
                ? currentSettings with { PowerLimitWatts = requested.PowerLimitWatts }
                : currentSettings with { CurveOptimizer = requested.CurveOptimizer };
            CpuSavedTuningStep[]? updatedPlan = CpuSavedTuningPlan.Create(journal.BaselineCurrent, updatedSettings);
            if (updatedPlan is null
                || !(updatedPlan.Length == 0 ? journal.BaselineCurrent : updatedPlan[^1].After).AsSpan().SequenceEqual(change.After))
            {
                return Blocked(observation, true, CpuSavedTuningBlockReason.UnrestorableBaseline);
            }
            observation = await backend.ReadAsync(journal.Identity, token).ConfigureAwait(false);
            if (!Matches(observation, change.Before)) { return Blocked(observation, true, CpuSavedTuningBlockReason.StateChanged); }
            if (!await backend.CheckWriteAdmissionAsync(journal.Identity, change.Command, token).ConfigureAwait(false)
                || !await backend.CheckWriteAdmissionAsync(journal.Identity, change.RestoreCommand, token).ConfigureAwait(false))
            {
                return Blocked(observation, true, CpuSavedTuningBlockReason.AdmissionDenied);
            }
            var submitted = journal with
            {
                State = CpuTuningOwnershipState.PossibleSubmission,
                Restoring = false,
                PendingUpdateSettings = updatedSettings,
                PendingUpdateStep = change,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            string? submittedHash = await store.TryWriteAsync(hash, submitted, token).ConfigureAwait(false);
            if (submittedHash is null) { return Blocked(observation, true, CpuSavedTuningBlockReason.JournalUnavailable); }
            CpuSavedTuningSubmission submission;
            try
            {
                token.ThrowIfCancellationRequested();
                submission = await backend.SendAsync(journal.Identity, change.Command, token).ConfigureAwait(false);
            }
            catch { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            if (submission == CpuSavedTuningSubmission.NotSubmitted)
            {
                string? reset = await store.TryWriteAsync(submittedHash, journal, CancellationToken.None).ConfigureAwait(false);
                return reset is null ? await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false)
                    : Blocked(observation, true, CpuSavedTuningBlockReason.AdmissionDenied);
            }
            if (submission != CpuSavedTuningSubmission.Acknowledged || token.IsCancellationRequested)
            {
                return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false);
            }
            try
            {
                observation = await backend.ReadAsync(journal.Identity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            if (!Matches(observation, change.After))
            {
                return new(CpuSavedTuningOutcome.Uncertain, observation, CpuTuningPendingBoot.Unknown, true, CpuSavedTuningBlockReason.StateChanged);
            }
            journal = journal with
            {
                Settings = updatedSettings,
                Steps = updatedPlan,
                CompletedSteps = updatedPlan.Length,
                State = updatedPlan.Length == 0 ? CpuTuningOwnershipState.Restored : CpuTuningOwnershipState.OwnedVerified,
                Restoring = updatedPlan.Length == 0,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            string? verifiedHash;
            try { verifiedHash = await store.TryWriteAsync(submittedHash, journal, token).ConfigureAwait(false); }
            catch { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            if (verifiedHash is null) { return await ReconcileUncertainAsync(journal.Identity).ConfigureAwait(false); }
            hash = verifiedHash;
        }
        bool pending = journal.State != CpuTuningOwnershipState.Restored;
        return new(CpuSavedTuningOutcome.Verified, observation,
            pending ? CpuTuningPendingBoot.Requested : CpuTuningPendingBoot.Unknown, pending);
    }

    private async Task<CpuSavedTuningTransactionResult> ReconcileUncertainAsync(DeviceIdentity identity)
    {
        SavedCpuFirmwareReadResult? observation = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<SavedCpuFirmwareReadResult>? read = null;
        try
        {
            read = backend.ReadAsync(identity, timeout.Token);
            observation = await read.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // A provider may ignore cancellation. Preserve the durable marker
            // and observe a detached read's eventual exception without waiting.
            if (read is not null)
            {
                _ = read.ContinueWith(static completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return new(CpuSavedTuningOutcome.Uncertain, observation, CpuTuningPendingBoot.Unknown, true, CpuSavedTuningBlockReason.PendingSubmission);
    }

    private static CpuSavedTuningTransactionResult Blocked(SavedCpuFirmwareReadResult? observation, bool pending,
        CpuSavedTuningBlockReason reason = CpuSavedTuningBlockReason.StateChanged) =>
        new(CpuSavedTuningOutcome.Blocked, observation, CpuTuningPendingBoot.Unknown, pending, reason);

    private static bool TryGetPair(SavedCpuFirmwareReadResult observation, out byte[]? current)
    {
        current = observation.Current?.CopyBytes();
        return observation is { Status: SavedCpuFirmwareReadStatus.Observed, CurrentError: 0, LastError: 0, Last: not null }
            && current is { Length: Rz090528CpuFirmwareCodec.SavedConfigurationLength }
            && current.AsSpan().SequenceEqual(observation.Last.CopyBytes());
    }

    private static bool Matches(SavedCpuFirmwareReadResult observation, byte[] expected) =>
        TryGetPair(observation, out byte[]? current) && current!.AsSpan().SequenceEqual(expected);
}
