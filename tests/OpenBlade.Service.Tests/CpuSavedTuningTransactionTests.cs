using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuSavedTuningTransactionTests
{
    private static readonly DeviceIdentity Identity = new("RZ09-0528", 0x1532, 0x02C6, "2.02");
    private static readonly CpuTuningSettings Curve = new(null, new(true, -12));

    [Fact]
    public async Task SourceAdmittedEndpointEditsKeepOriginalPairAndRestoreAcrossTransactions()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] original = (byte[])backend.Bytes.Clone();
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, new(45, new(false, -30)), default)).Outcome);
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity with { BiosVersion = "2.03" }, new(95, new(true, -30)), default)).Outcome);
        Assert.Equal(original, store.Journal!.BaselineCurrent);
        Assert.Equal(original, store.Journal.BaselineLast);
        Assert.True(CpuSavedSemanticPolicy.MatchesPlan(original, original, store.Journal.Settings, store.Journal.Steps));
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await new CpuSavedTuningTransaction(backend, store)
            .RestoreAsync(Identity with { BiosVersion = "Unknown" }, default)).Outcome);
        Assert.Equal(Identity, store.Journal.Identity);
        Assert.Equal(original, backend.Bytes);
        Assert.Equal(6, backend.Sent.Count);
        Assert.Equal(new(CpuSavedTuningCommandKind.CurveOptimizer, -1), backend.Sent[^2]);
        Assert.Equal(new(CpuSavedTuningCommandKind.ResetPowerLimit, 0), backend.Sent[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOriginalOrPendingTransitionBlocksRecreatedTransactionBeforeAnyBackendRead(bool pendingEdit)
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        await new CpuSavedTuningTransaction(backend, store).ApplyAsync(Identity, Curve, default);
        if (pendingEdit)
        {
            backend.AfterWrite = () => throw new TimeoutException();
            Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await new CpuSavedTuningTransaction(backend, store)
                .ApplyAsync(Identity, new(null, new(true, -13)), default)).Outcome);
        }
        CpuSavedTuningStep corrupted = pendingEdit ? store.Journal!.PendingUpdateStep! : store.Journal!.Steps[0];
        corrupted.After[799] ^= 1;
        int reads = backend.Reads;
        int sends = backend.Sent.Count;
        var recreated = new CpuSavedTuningTransaction(backend, store);
        Assert.Equal(CpuSavedTuningBlockReason.InvalidJournal, (await recreated.RestoreAsync(Identity, default)).Reason);
        Assert.Equal(CpuSavedTuningBlockReason.InvalidJournal, (await recreated.ApplyAsync(Identity, Curve, default)).Reason);
        Assert.Equal(reads, backend.Reads);
        Assert.Equal(sends, backend.Sent.Count);
    }

    [Fact]
    public async Task CompleteReversePlanIsAdmittedBeforeFirstRestoreCommand()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, new(75, new(true, -12)), CancellationToken.None);
        backend.DeniedKind = CpuSavedTuningCommandKind.ResetPowerLimit;
        CpuSavedTuningTransactionResult result = await transaction.RestoreAsync(Identity, CancellationToken.None);
        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.Equal(CpuSavedTuningBlockReason.AdmissionDenied, result.Reason);
        Assert.Equal(2, backend.Sent.Count);
        Assert.Equal(2, store.Journal!.CompletedSteps);
    }

    [Fact]
    public async Task RecoveryFeaturesFollowCompletedCommandsRatherThanUnsubmittedIntent()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        backend.AfterWrite = () => backend.DeniedKind = CpuSavedTuningCommandKind.CurveOptimizer;
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, new(75, new(true, -12)), CancellationToken.None);
        var partial = await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.PowerLimit), partial.Recovery);
        backend.MirrorMismatch = true;
        var drift = await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.PowerLimit), drift.Recovery);
        var mismatch = await transaction.ObserveOwnershipAsync(Identity with { Model = "RZ09-9999" }, CancellationToken.None);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.None), mismatch.Recovery);
    }

    [Fact]
    public async Task PreparedCleanupOwnsNoFeaturesAndMatchingObservationOwnsNothing()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store) { Submission = CpuSavedTuningSubmission.NotSubmitted };
        var transaction = new CpuSavedTuningTransaction(backend, store);
        Assert.Equal(new(CpuTuningRecoveryState.None, CpuTuningFeatures.None),
            (await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None)).Recovery);
        await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);
        Assert.Equal(CpuTuningOwnershipState.Prepared, store.Journal!.State);
        var prepared = await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None);
        Assert.True(prepared.RestorePending);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.None), prepared.Recovery);
        await transaction.RestoreAsync(Identity, CancellationToken.None);
        Assert.Equal(new(CpuTuningRecoveryState.None, CpuTuningFeatures.None),
            (await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None)).Recovery);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task VerifiedWriteDurablyOwnsBothFullImagesAndExplicitRestoreReturnsEveryByte()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);

        CpuSavedTuningTransactionResult applied = await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Verified, applied.Outcome);
        Assert.Equal(CpuTuningPendingBoot.Requested, applied.PendingBoot);
        Assert.True(applied.RestorePending);
        Assert.Equal(baseline, store.Journal!.BaselineCurrent);
        Assert.Equal(baseline, store.Journal.BaselineLast);
        Assert.Equal(Curve, store.Journal.Settings);
        Assert.Equal(CpuTuningOwnershipState.OwnedVerified, store.Journal.State);
        Assert.Equal(-12, Rz090528CpuFirmwareCodec.DecodeSaved(backend.Bytes).AllCoreCurveOptimizer);

        // A new process can restore a confirmed journal after fresh readback.
        CpuSavedTuningTransactionResult restored = await new CpuSavedTuningTransaction(backend, store)
            .RestoreAsync(Identity, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Verified, restored.Outcome);
        Assert.False(restored.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Unknown, restored.PendingBoot);
        Assert.Equal(baseline, backend.Bytes);
        Assert.Equal(CpuTuningOwnershipState.Restored, store.Journal.State);
        Assert.Equal(new[] { -12, -1 }, backend.Sent.Select(command => command.Value));
    }

    [Fact]
    public async Task CombinedPlanPreservesAllPowerSiblingsAndRestoresInReverseOrder()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        backend.Bytes[0x95] = 2;
        for (int index = 0; index < 5; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(backend.Bytes.AsSpan(0x96 + index * 4), checked((uint)(65000 + index * 17)));
        }
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.ApplyAsync(Identity,
            new(75, new(true, -12)), CancellationToken.None)).Outcome);
        Assert.Equal(baseline.AsSpan(0x9A, 16).ToArray(), backend.Bytes.AsSpan(0x9A, 16).ToArray());
        Assert.Equal(75000u, BinaryPrimitives.ReadUInt32LittleEndian(backend.Bytes.AsSpan(0x96)));
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(baseline, backend.Bytes);
        Assert.Equal(new[]
        {
            new CpuSavedTuningCommand(CpuSavedTuningCommandKind.PowerLimit, 75),
            new CpuSavedTuningCommand(CpuSavedTuningCommandKind.CurveOptimizer, -12),
            new CpuSavedTuningCommand(CpuSavedTuningCommandKind.CurveOptimizer, -1),
            new CpuSavedTuningCommand(CpuSavedTuningCommandKind.PowerLimit, 65),
        }, backend.Sent);
    }

    [Fact]
    public async Task UnadmittedResetRouteBlocksForwardPowerBeforeAnyJournalOrSubmission()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store) { DeniedKind = CpuSavedTuningCommandKind.ResetPowerLimit };

        CpuSavedTuningTransactionResult result = await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, new(75, null), CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.Empty(backend.Sent);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task CanonicalZeroPowerBaselineHasGatedExactResetPlan()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);

        await transaction.ApplyAsync(Identity, new(75, null), CancellationToken.None);
        Assert.Equal(new CpuSavedTuningCommand(CpuSavedTuningCommandKind.ResetPowerLimit, 0), store.Journal!.Steps[0].RestoreCommand);
        await transaction.RestoreAsync(Identity, CancellationToken.None);

        Assert.Equal(baseline, backend.Bytes);
        Assert.Equal(new byte[] { 1, 0, 5, 0, 0, 0, 0, 0 }, backend.Sent[1].Encode());
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 31)]
    [InlineData(0, 1, 1)]
    public async Task BaselineWithoutExactCurveRestoreRouteIsNeverOverwritten(byte mode, byte sign, ushort magnitude)
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        backend.Bytes[0xC0] = mode;
        backend.Bytes[0xC1] = sign;
        BinaryPrimitives.WriteUInt16LittleEndian(backend.Bytes.AsSpan(0xC2), magnitude);
        byte[] baseline = (byte[])backend.Bytes.Clone();

        CpuSavedTuningTransactionResult result = await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, Curve, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.Equal(baseline, backend.Bytes);
        Assert.Null(store.Journal);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task UnknownAcknowledgementStopsCombinedPlanAndCannotBecomeOwnedAfterRestart()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store) { Submission = CpuSavedTuningSubmission.Unknown };
        var transaction = new CpuSavedTuningTransaction(backend, store);

        CpuSavedTuningTransactionResult result = await transaction.ApplyAsync(Identity, new(75, new(true, -12)), CancellationToken.None);
        Assert.Equal(CpuSavedTuningOutcome.Uncertain, result.Outcome);
        Assert.Equal(CpuTuningOwnershipState.PossibleSubmission, store.Journal!.State);
        Assert.Single(backend.Sent);
        int reads = backend.Reads;

        var restarted = new CpuSavedTuningTransaction(backend, store);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.PowerLimit),
            (await restarted.ObserveOwnershipAsync(Identity, CancellationToken.None)).Recovery);
        Assert.Equal(CpuTuningPendingBoot.Unknown, (await restarted.ObserveOwnershipAsync(Identity, CancellationToken.None)).PendingBoot);
        Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await restarted.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await restarted.ApplyAsync(Identity, Curve, CancellationToken.None)).Outcome);
        Assert.Single(backend.Sent);
        Assert.True(backend.Reads > reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrExceptionAfterSubmissionKeepsDurableUncertainty(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MemoryStore();
        var backend = new FakeBackend(store)
        {
            AfterWrite = cancel ? cancellation.Cancel : () => throw new TimeoutException(),
        };

        CpuSavedTuningTransactionResult result = await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, new(75, new(true, -12)), cancellation.Token);

        Assert.Equal(CpuSavedTuningOutcome.Uncertain, result.Outcome);
        Assert.True(result.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Unknown, result.PendingBoot);
        Assert.Equal(CpuTuningOwnershipState.PossibleSubmission, store.Journal!.State);
        Assert.Single(backend.Sent);
        Assert.True(backend.Reads >= 3);
    }

    [Fact]
    public async Task FailedPreSubmissionJournalWriteNeverCallsSender()
    {
        var store = new MemoryStore { FailState = CpuTuningOwnershipState.PossibleSubmission };
        var backend = new FakeBackend(store);

        CpuSavedTuningTransactionResult result = await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, Curve, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.Empty(backend.Sent);
        Assert.Equal(CpuTuningOwnershipState.Prepared, store.Journal!.State);
        await new CpuSavedTuningTransaction(backend, store).ApplyAsync(Identity, Curve, CancellationToken.None);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task FailedVerifiedJournalWriteDoesNotAllowRestoreEvenWhenReadbackMatches()
    {
        var store = new MemoryStore { FailState = CpuTuningOwnershipState.OwnedVerified };
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await transaction.ApplyAsync(Identity, Curve, CancellationToken.None)).Outcome);
        Assert.Equal(CpuTuningOwnershipState.PossibleSubmission, store.Journal!.State);
        await transaction.RestoreAsync(Identity, CancellationToken.None);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task RepeatedEditsRetainOriginalBaselineAndBoundedRestorePlan()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);
        Guid originalId = store.Journal!.TransactionId;
        for (int value = -13; value >= -20; value--)
        {
            Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.ApplyAsync(Identity,
                new(75, new(true, value)), CancellationToken.None)).Outcome);
            Assert.Equal(baseline, store.Journal.BaselineCurrent);
            Assert.Equal(originalId, store.Journal.TransactionId);
            Assert.Equal(2, store.Journal.Steps.Length);
        }
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(baseline, backend.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousEditPreservesOriginalBaselineAndBlocksFurtherChanges(bool addPower)
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);
        backend.AfterWrite = () => throw new TimeoutException();

        Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await transaction.ApplyAsync(Identity,
            addPower ? new(75, null) : new(null, new(true, -13)), CancellationToken.None)).Outcome);
        Assert.NotNull(store.Journal!.PendingUpdateStep);
        Assert.Equal(baseline, store.Journal.BaselineCurrent);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.CurveOptimizer
            | (addPower ? CpuTuningFeatures.PowerLimit : CpuTuningFeatures.None)),
            (await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None)).Recovery);
        await new CpuSavedTuningTransaction(backend, store).RestoreAsync(Identity, CancellationToken.None);
        Assert.Equal(2, backend.Sent.Count);
    }

    [Fact]
    public async Task ExplicitRestoreCanDiscardPreparedUnsubmittedTransactionAfterExactRead()
    {
        var store = new MemoryStore { FailState = CpuTuningOwnershipState.PossibleSubmission };
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(CpuTuningOwnershipState.Restored, store.Journal!.State);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task DefinitiveNonSubmissionCanBeExplicitlyReleasedWithoutAReverseCommand()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store) { Submission = CpuSavedTuningSubmission.NotSubmitted };
        byte[] baseline = (byte[])backend.Bytes.Clone();
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await transaction.ApplyAsync(Identity, Curve, CancellationToken.None)).Outcome);
        Assert.Equal(CpuTuningOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(baseline, backend.Bytes);
        Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignDriftOrMirrorMismatchBlocksExplicitRestore(bool mirrorMismatch)
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, CancellationToken.None);
        if (mirrorMismatch) { backend.MirrorMismatch = true; }
        else { backend.Bytes[799] ^= 1; }

        CpuSavedTuningTransactionResult result = await transaction.RestoreAsync(Identity, CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.True(result.RestorePending);
        Assert.Single(backend.Sent);
        Assert.Equal(CpuTuningPendingBoot.Unknown, (await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None)).PendingBoot);
    }

    [Fact]
    public async Task AcknowledgedUnexpectedChangeNeverTriggersReverseCommand()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        backend.AfterWrite = () => backend.Bytes[799] ^= 1;
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.Equal(CpuSavedTuningOutcome.Uncertain, (await transaction.ApplyAsync(Identity, Curve, CancellationToken.None)).Outcome);
        await transaction.RestoreAsync(Identity, CancellationToken.None);
        Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData("observe", false)]
    [InlineData("observe", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public async Task ConfirmedOwnershipLossCannotBeReacquiredAfterValuesReturn(string operation, bool mirrorMismatch)
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, default);
        byte[] expected = (byte[])backend.Bytes.Clone();
        byte[] original = (byte[])store.Journal!.BaselineCurrent.Clone();
        if (mirrorMismatch) { backend.MirrorMismatch = true; }
        else { backend.Bytes[799] ^= 1; }
        var result = operation switch
        {
            "observe" => await transaction.ObserveOwnershipAsync(Identity, default),
            "update" => await transaction.ApplyAsync(Identity, new(null, new(true, -15)), default),
            _ => await transaction.RestoreAsync(Identity, default),
        };
        Assert.Equal(CpuSavedTuningOutcome.Blocked, result.Outcome);
        Assert.Equal(CpuTuningOwnershipState.Blocked, store.Journal.State);
        Assert.Equal(original, store.Journal.BaselineCurrent);
        Assert.Equal(original, store.Journal.BaselineLast);
        expected.CopyTo(backend.Bytes, 0); backend.MirrorMismatch = false;
        int reads = backend.Reads;
        var restarted = new CpuSavedTuningTransaction(backend, store);
        Assert.Equal(CpuTuningRecoveryState.Blocked, (await restarted.ObserveOwnershipAsync(Identity, default)).Recovery.State);
        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await restarted.RestoreAsync(Identity, default)).Outcome);
        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await restarted.ApplyAsync(Identity, Curve, default)).Outcome);
        Assert.Equal(reads, backend.Reads);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task TransientUnavailableReadPreservesConfirmedJournalAndLaterRestore()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, default);
        var owned = store.Journal;
        backend.Unavailable = true;
        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await transaction.RestoreAsync(Identity, default)).Outcome);
        Assert.Same(owned, store.Journal);
        backend.Unavailable = false;
        Assert.Equal(CpuSavedTuningOutcome.Verified,
            (await new CpuSavedTuningTransaction(backend, store).RestoreAsync(Identity, default)).Outcome);
        Assert.Equal(Baseline(), backend.Bytes);
    }

    [Fact]
    public async Task ConfirmedIdentityChangePermanentlyLosesOriginalOwnership()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, default);
        Assert.Equal(CpuSavedTuningBlockReason.IdentityMismatch,
            (await transaction.ObserveOwnershipAsync(Identity with { Model = "RZ09-9999" }, default)).Reason);
        Assert.Equal(CpuTuningOwnershipState.Blocked, store.Journal!.State);
        Assert.Equal(CpuSavedTuningOutcome.Blocked,
            (await new CpuSavedTuningTransaction(backend, store).RestoreAsync(Identity, default)).Outcome);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task FailedOwnershipLossPersistenceBlocksTheRetainedTransaction()
    {
        var store = new MemoryStore { FailState = CpuTuningOwnershipState.Blocked };
        var backend = new FakeBackend(store); var transaction = new CpuSavedTuningTransaction(backend, store);
        await transaction.ApplyAsync(Identity, Curve, default);
        backend.Bytes[799] ^= 1;
        Assert.Equal(CpuSavedTuningBlockReason.JournalUnavailable,
            (await transaction.ObserveOwnershipAsync(Identity, default)).Reason);
        backend.Bytes[799] ^= 1;
        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await transaction.RestoreAsync(Identity, default)).Outcome);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task PartialAcknowledgedPlanCanBeExplicitlyRestoredAfterFreshGateDenial()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        byte[] baseline = (byte[])backend.Bytes.Clone();
        backend.AfterWrite = () => backend.DeniedKind = CpuSavedTuningCommandKind.CurveOptimizer;
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.Equal(CpuSavedTuningOutcome.Blocked, (await transaction.ApplyAsync(Identity, new(75, new(true, -12)), CancellationToken.None)).Outcome);
        Assert.Equal(1, store.Journal!.CompletedSteps);
        Assert.Single(backend.Sent);
        Assert.Equal(CpuSavedTuningOutcome.Verified, (await transaction.RestoreAsync(Identity, CancellationToken.None)).Outcome);
        Assert.Equal(baseline, backend.Bytes);
    }

    [Fact]
    public async Task MatchingInitialConfigurationDoesNotGrantOwnership()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);

        CpuSavedTuningTransactionResult result = await new CpuSavedTuningTransaction(backend, store)
            .ApplyAsync(Identity, new(null, new(true, -1)), CancellationToken.None);

        Assert.Equal(CpuSavedTuningOutcome.Unchanged, result.Outcome);
        Assert.Equal(CpuTuningPendingBoot.Unknown, result.PendingBoot);
        Assert.False(result.RestorePending);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task InvalidJournalBlocksWithoutReadingOrWritingFirmware()
    {
        var store = new MemoryStore { Invalid = true };
        var backend = new FakeBackend(store);
        var transaction = new CpuSavedTuningTransaction(backend, store);

        Assert.True((await transaction.ApplyAsync(Identity, Curve, CancellationToken.None)).RestorePending);
        Assert.True((await transaction.RestoreAsync(Identity, CancellationToken.None)).RestorePending);
        Assert.Equal(new(CpuTuningRecoveryState.Blocked, CpuTuningFeatures.None),
            (await transaction.ObserveOwnershipAsync(Identity, CancellationToken.None)).Recovery);
        Assert.Equal(0, backend.Reads);
        Assert.Empty(backend.Sent);
    }

    internal static byte[] Baseline()
    {
        byte[] bytes = new byte[800];
        bytes[0x94] = 2;
        bytes[0x95] = 0xFF;
        bytes[0xC0] = 1;
        bytes[0xC1] = 1;
        bytes[0xC2] = 1;
        // Inactive physical core slots, positive signs/magnitudes and unrelated
        // fields remain byte-for-byte intact through all-core writes/restores.
        for (int core = 0; core < 16; core++)
        {
            bytes[0xC4 + core] = (byte)(core % 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x104 + core * 2), (ushort)(400 + core));
        }
        bytes[799] = 0xA5;
        return bytes;
    }

    private sealed class MemoryStore : ICpuTuningOwnershipJournalStore
    {
        private int revision;
        internal CpuTuningOwnershipJournal? Journal { get; private set; }
        internal CpuTuningOwnershipState? FailState { get; init; }
        internal bool Invalid { get; init; }
        public Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token) =>
            Task.FromResult(new CpuTuningOwnershipJournalLoadResult(Journal, Invalid, revision == 0 ? null : revision.ToString()));
        public Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(journal.IsWellFormed);
            Assert.Equal(revision == 0 ? null : revision.ToString(), expectedSha256);
            if (journal.State == FailState) { return Task.FromResult<string?>(null); }
            Journal = journal;
            return Task.FromResult<string?>((++revision).ToString());
        }
    }

    private sealed class FakeBackend(MemoryStore store) : ICpuSavedTuningBackend
    {
        internal byte[] Bytes { get; } = Baseline();
        internal int Reads { get; private set; }
        internal List<CpuSavedTuningCommand> Sent { get; } = [];
        internal CpuSavedTuningCommandKind? DeniedKind { get; set; }
        internal CpuSavedTuningSubmission Submission { get; init; } = CpuSavedTuningSubmission.Acknowledged;
        internal Action? AfterWrite { get; set; }
        internal bool MirrorMismatch { get; set; }
        internal bool Unavailable { get; set; }
        public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token) =>
            Task.FromResult(CpuHardwareIdentity.Same(identity, Identity) && command.Kind != DeniedKind);
        public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            if (Unavailable) { return Task.FromResult(new SavedCpuFirmwareReadResult(SavedCpuFirmwareReadStatus.Unavailable, null, null, 5, 5)); }
            byte[] last = (byte[])Bytes.Clone();
            if (MirrorMismatch) { last[799] ^= 1; }
            return Task.FromResult(new SavedCpuFirmwareReadResult(
                MirrorMismatch ? SavedCpuFirmwareReadStatus.MirrorMismatch : SavedCpuFirmwareReadStatus.Observed,
                Rz090528CpuFirmwareCodec.DecodeSaved(Bytes), Rz090528CpuFirmwareCodec.DecodeSaved(last), 0, 0));
        }
        public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token)
        {
            Assert.Equal(CpuTuningOwnershipState.PossibleSubmission, store.Journal!.State);
            Assert.Equal(800, store.Journal.BaselineCurrent.Length);
            Assert.Equal(800, store.Journal.BaselineLast.Length);
            Sent.Add(command);
            if (Submission == CpuSavedTuningSubmission.NotSubmitted) { return Task.FromResult(Submission); }
            // Independent fake of the observed firmware callback.
            Bytes[0x94] = 2;
            if (command.Kind == CpuSavedTuningCommandKind.CurveOptimizer)
            {
                Bytes[0xC0] = 1;
                Bytes[0xC1] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(0xC2), (ushort)-command.Value);
            }
            else
            {
                Bytes[0x95] = 2;
                BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(0x96), (uint)command.Value * 1000);
            }
            if (Bytes.AsSpan(0x96, 20).IndexOfAnyExcept((byte)0) < 0) { Bytes[0x95] = 0xFF; }
            AfterWrite?.Invoke();
            return Task.FromResult(Submission);
        }
    }
}
