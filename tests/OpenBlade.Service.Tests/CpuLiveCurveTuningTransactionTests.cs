using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveCurveTuningTransactionTests
{
    [Fact]
    public async Task TimeoutCapturesSynchronousAdmissionBeforeItReturnsACompletedTask()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        backend.OnObserve = token =>
        {
            using var stage = diagnostic.BeginStage("power verified device probe");
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            // Cancellation signals the token's wait handle before its callbacks.
            // Keep this simulated synchronous probe active until the diagnostic
            // callback has actually run, as an unfinished native probe would be.
            Assert.True(SpinWait.SpinUntil(() => diagnostic.LiveObservationTimeoutContext is not null, TimeSpan.FromSeconds(5)));
            return Task.FromResult(new CpuLiveCurveBackendObservation(null, null));
        };
        var result = await new CpuLiveCurveTuningTransaction(backend, store, TimeSpan.FromMilliseconds(20))
            .ObserveOwnershipAsync(default, diagnostic);
        Assert.Equal(CpuLiveCurveBlockReason.ObservationUnavailable, result.Reason);
        Assert.StartsWith("; curve observation operation timeout; active stage power verified device probe; elapsed ",
            diagnostic.LiveObservationTimeoutContext);
        Assert.Empty(backend.Sent); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task ObservationTimeoutRetainsFirstFailureBeforeLateBackendCompletion()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var pending = new TaskCompletionSource<CpuLiveCurveBackendObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        IDisposable? stage = null;
        backend.OnObserve = _ => { stage = diagnostic.BeginStage("power adapter read sample 1 of 5 (open/exchange/dispose)"); return pending.Task; };
        var transaction = new CpuLiveCurveTuningTransaction(backend, store, TimeSpan.FromMilliseconds(20));
        var result = await transaction.ObserveOwnershipAsync(default, diagnostic).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CpuLiveCurveBlockReason.ObservationUnavailable, result.Reason);
        string first = Assert.IsType<string>(diagnostic.FirstFailure);
        Assert.StartsWith("curve observation operation timeout; active stage power adapter read sample 1 of 5", first);
        Assert.Matches(@"; elapsed \d+ ms$", first);
        Assert.False(pending.Task.IsCompleted); Assert.Equal(1, backend.Reads);
        diagnostic.Check(false, "late native rejection");
        pending.SetResult(new(null, null));
        await pending.Task;
        stage!.Dispose();
        Assert.Equal(first, diagnostic.FirstFailure);
        Assert.Equal(string.Empty, diagnostic.ActiveStageDetail());
        Assert.Empty(backend.Sent); Assert.Equal(0, store.Writes);
    }
    [Fact]
    public async Task MixedBaselineIsRestoredPerPhysicalCoreAfterRestart()
    {
        var store = new MemoryStore();
        var backend = new FakeBackend(store);
        backend.Values[0] = new(0, 12);
        backend.Values[1] = new(2, -45);
        var original = backend.Values.ToArray();
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        var applied = await transaction.ApplyAllCoreAsync(new(true, -2), CancellationToken.None);
        Assert.Equal(CpuLiveCurveOutcome.Verified, applied.Outcome);
        Assert.Equal(CpuLiveCurveRecovery.RestoreReady, applied.Recovery);
        Assert.Equal(12, store.Journal!.CompletedSteps);
        Assert.Equal(original, store.Journal.OriginalBaseline);
        Assert.All(backend.Values, value => Assert.Equal(-2, value.Offset));
        var originalContext = store.Journal.Context;
        backend.Context = backend.Context with { Identity = backend.Context.Identity with { BiosVersion = "Unknown" } };
        var restored = await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(CancellationToken.None);
        Assert.Equal(CpuLiveCurveOutcome.Verified, restored.Outcome);
        Assert.Equal(CpuLiveCurveRecovery.None, restored.Recovery);
        Assert.Equal(original, backend.Values);
        Assert.Equal(original.Select(core => core.PhysicalCoreId).Reverse(), backend.Sent.Skip(12).Select(command => command.PhysicalCoreId));
        Assert.Equal(original.Select(core => core.Offset).Reverse(), backend.Sent.Skip(12).Select(command => command.Offset));
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal.State);
        Assert.Equal(originalContext, store.Journal.Context);
    }

    [Fact]
    public async Task UnmanagedAndMatchingTargetsDoNotCreateOwnership()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Unchanged, (await transaction.ApplyAllCoreAsync(null, default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Unchanged, (await transaction.ApplyAllCoreAsync(new(true, -1), default)).Outcome);
        Assert.Null(store.Journal); Assert.Empty(backend.Sent); Assert.Empty(backend.Admissions);
    }

    [Fact]
    public async Task CompleteReverseAdmissionPrecedesAnyJournalOrSubmission()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Admit = (command, purpose) => purpose != CpuLiveCurveCommandPurpose.Restore || command.PhysicalCoreId != 15;
        var result = await new CpuLiveCurveTuningTransaction(backend, store).ApplyAllCoreAsync(new(true, -2), default);
        Assert.Equal(CpuLiveCurveBlockReason.AdmissionDenied, result.Reason);
        Assert.Null(store.Journal); Assert.Empty(backend.Sent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedPreparedOrSubmissionMarkerFlushSendsNothing(int failWrite)
    {
        var store = new MemoryStore { FailWrite = failWrite }; var backend = new FakeBackend(store);
        var result = await new CpuLiveCurveTuningTransaction(backend, store).ApplyAllCoreAsync(new(true, -2), default);
        Assert.Equal(CpuLiveCurveBlockReason.JournalUnavailable, result.Reason); Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task ConflictingJournalRevisionBeforeMarkerPreventsSubmission()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Admit = (_, _) =>
        {
            if (store.Journal is not null) { store.Hash = "foreign-revision"; }
            return true;
        };
        var result = await new CpuLiveCurveTuningTransaction(backend, store).ApplySelectiveAsync([new(0, 0)], default);
        Assert.Equal(CpuLiveCurveBlockReason.JournalUnavailable, result.Reason); Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task AllRemainingReverseCommandsAreAdmittedBeforeFirstRestore()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0), new(8, 0)], default);
        backend.Admit = (command, _) => command.PhysicalCoreId != 0;
        Assert.Equal(CpuLiveCurveBlockReason.AdmissionDenied, (await transaction.RestoreAsync(default)).Reason);
        Assert.Equal(2, backend.Sent.Count); Assert.Equal(2, store.Journal!.CompletedSteps);
    }

    [Fact]
    public async Task FailedVerifiedPersistenceLeavesPossibleSubmissionAndBlocksEveryFurtherWrite()
    {
        var store = new MemoryStore { FailWrite = 3 }; var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ApplyAllCoreAsync(new(true, -2), default)).Outcome);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
        int reads = backend.Reads;
        var restarted = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await restarted.ApplyAllCoreAsync(new(true, -3), default)).Outcome);
        Assert.Equal(CpuLiveCurveRecovery.Blocked, (await restarted.ObserveOwnershipAsync(default)).Recovery);
        Assert.Single(backend.Sent); Assert.Equal(reads, backend.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrThrowingSubmissionNeverRetriesOrRestores(bool throws)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.OnSend = _ => throws ? throw new IOException("private native detail") : Task.FromResult(CpuLiveCurveSubmission.Unknown);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ApplyAllCoreAsync(new(false, -1), default)).Outcome);
        Assert.Equal(CpuLiveCurveRecovery.Blocked, (await transaction.RestoreAsync(default)).Recovery);
        Assert.Single(backend.Sent); Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
    }

    [Fact]
    public async Task LateAcknowledgmentCannotAdvancePendingJournal()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var completion = new TaskCompletionSource<CpuLiveCurveSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        backend.OnSend = _ => { cancellation.Cancel(); return completion.Task; };
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain,
            (await transaction.ApplyAllCoreAsync(new(true, -2), cancellation.Token)).Outcome);
        completion.SetResult(CpuLiveCurveSubmission.Acknowledged);
        Assert.Equal(CpuLiveCurveRecovery.Blocked, (await transaction.ObserveOwnershipAsync(default)).Recovery);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Single(backend.Sent); Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
    }

    [Fact]
    public async Task NonCooperatingSenderTimesOutWithoutReplay()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var completion = new TaskCompletionSource<CpuLiveCurveSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnSend = _ => completion.Task;
        var transaction = new CpuLiveCurveTuningTransaction(backend, store, TimeSpan.FromMilliseconds(50));
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ApplySelectiveAsync([new(0, 0)], default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.RestoreAsync(default)).Outcome);
        completion.SetResult(CpuLiveCurveSubmission.Acknowledged);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task PreparedNotSubmittedCleanupUsesNoAdditionalSetter()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store)
        { OnSend = _ => Task.FromResult(CpuLiveCurveSubmission.NotSubmitted) };
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(8, 0)], default);
        Assert.Equal(CpuLiveCurveOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(CpuLiveCurveRecovery.RestoreReady, (await transaction.ObserveOwnershipAsync(default)).Recovery);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Single(backend.Sent); Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal.State);
    }

    [Fact]
    public async Task PartialVerifiedForwardCanRestoreOnlyChangedCoresInReverseOrder()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Admit = (command, purpose) => purpose == CpuLiveCurveCommandPurpose.Restore || backend.Sent.Count < 2;
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.ApplyAllCoreAsync(new(true, -2), default)).Outcome);
        Assert.Equal(2, store.Journal!.CompletedSteps);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Equal(new[] { 0, 2, 2, 0 }, backend.Sent.Select(command => command.PhysicalCoreId));
        Assert.All(backend.Values, value => Assert.Equal(-1, value.Offset));
    }

    [Fact]
    public async Task AmbiguousPartialReverseRetainsPendingStepAndRemainingOriginalValues()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0), new(8, 0)], default);
        backend.OnSend = command => backend.Sent.Count == 4 ? Task.FromResult(CpuLiveCurveSubmission.Unknown) : backend.Apply(command);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Equal(1, store.Journal!.CompletedSteps); Assert.True(store.Journal.Restoring);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal.State);
        Assert.All(store.Journal.OriginalBaseline, value => Assert.Equal(-1, value.Offset));
        await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(default);
        Assert.Equal(4, backend.Sent.Count);
    }

    [Fact]
    public async Task ChangeToUnselectedCoreDuringReadbackBlocksRestoration()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.AfterSend = () => backend.Values[11] = new(15, -9);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ApplySelectiveAsync([new(0, 0)], default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task ZeroLoaderSequenceSupportsVerifiedEditAndRestorationWithFakeBackend()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Sequence = 0 } };
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplySelectiveAsync([new(0, 0)], default)).Outcome);
        Assert.Equal(0u, store.Journal!.Context.BootSequence.Sequence);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal.State);
        Assert.Equal(2, backend.Sent.Count);
    }

    [Theory]
    [InlineData("missing-sequence")]
    [InlineData("unknown-source")]
    [InlineData("undefined-source")]
    public async Task UnknownLoaderSequenceCannotPrepareOrSubmitAnEdit(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var sequence = backend.Context.BootSequence;
        sequence = fault switch
        {
            "missing-sequence" => sequence with { Sequence = null },
            "unknown-source" => sequence with { Source = CpuLiveCurveBootSequenceSource.Unknown },
            _ => sequence with { Source = (CpuLiveCurveBootSequenceSource)999 },
        };
        backend.Context = backend.Context with { BootSequence = sequence };
        var result = await new CpuLiveCurveTuningTransaction(backend, store).ApplySelectiveAsync([new(0, 0)], default);
        Assert.Equal(CpuLiveCurveBlockReason.ObservationUnavailable, result.Reason);
        Assert.Equal(0, store.Writes); Assert.Null(store.Journal);
        Assert.Empty(backend.Admissions); Assert.Empty(backend.Sent);
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("unknown-boot")]
    [InlineData("unknown-boot-source")]
    [InlineData("undefined-boot-source")]
    [InlineData("backend")]
    [InlineData("model")]
    [InlineData("unselected-core")]
    public async Task ChangedContextOrVectorBlocksRestoreAcrossRestart(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        await new CpuLiveCurveTuningTransaction(backend, store).ApplySelectiveAsync([new(0, 0)], default);
        var originalContext = backend.Context; var expected = backend.Values.ToArray();
        if (fault == "boot") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Sequence = 43 } }; }
        if (fault == "unknown-boot") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Sequence = null } }; }
        if (fault == "unknown-boot-source") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Source = CpuLiveCurveBootSequenceSource.Unknown } }; }
        if (fault == "undefined-boot-source") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Source = (CpuLiveCurveBootSequenceSource)999 } }; }
        if (fault == "boot") { Assert.True(backend.Context.IsWellFormed); }
        if (fault == "backend") { backend.Context = backend.Context with { Backend = backend.Context.Backend with { DriverSha256 = "other" } }; }
        if (fault == "model") { backend.Context = backend.Context with { Identity = backend.Context.Identity with { Model = "RZ09-9999" } }; }
        if (fault == "unselected-core") { backend.Values[11] = new(15, -9); }
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        int writes = store.Writes;
        var observed = await transaction.ObserveOwnershipAsync(default);
        Assert.Equal(CpuLiveCurveRecovery.Blocked, observed.Recovery);
        Assert.Equal(fault == "unselected-core" ? CpuLiveCurveBlockReason.StateChanged : CpuLiveCurveBlockReason.ContextMismatch,
            observed.Reason);
        Assert.Equal(writes + 1, store.Writes);
        Assert.Equal(CpuLiveCurveOwnershipState.Blocked, store.Journal!.State);
        var result = await transaction.RestoreAsync(default);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, result.Outcome);
        Assert.Equal(CpuLiveCurveBlockReason.StateChanged, result.Reason);
        Assert.Single(backend.Sent); Assert.Equal(CpuLiveCurveOwnershipState.Blocked, store.Journal!.State);
        backend.Context = originalContext; backend.Values = expected;
        int reads = backend.Reads;
        var restarted = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.ApplySelectiveAsync([new(8, -2)], default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes + 1, store.Writes); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task RepeatedAllCoreAndSelectiveEditsRestoreOriginalVectorAcrossRestart()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Values[0] = new(0, short.MinValue); backend.Values[1] = new(2, short.MaxValue);
        var original = backend.Values.ToArray();
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplyAllCoreAsync(new(true, -2), default)).Outcome);
        var ownershipId = store.Journal!.TransactionId;
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplyAllCoreAsync(new(true, -3), default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplySelectiveAsync([new(0, -4), new(8, 0)], default)).Outcome);
        Assert.Equal(original, store.Journal!.OriginalBaseline);
        Assert.All(store.Journal.OperationBaseline, core => Assert.Equal(-3, core.Offset));
        Assert.Equal(ownershipId, store.Journal.TransactionId); Assert.Equal(2, store.Journal.Steps.Count);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(original, backend.Values);
        Assert.Equal(12, store.Journal.Steps.Count);
        Assert.Equal(original.Select(core => core.PhysicalCoreId).Reverse(), backend.Sent.Skip(26).Select(command => command.PhysicalCoreId));
    }

    [Fact]
    public async Task MatchingOwnedTargetLeavesJournalAndOwnershipIntactWithoutSetter()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        var journal = store.Journal; int writes = store.Writes; int admissions = backend.Admissions.Count;
        var result = await transaction.ApplySelectiveAsync([new(0, 0)], default);
        Assert.Equal(CpuLiveCurveOutcome.Unchanged, result.Outcome); Assert.Equal(CpuLiveCurveRecovery.RestoreReady, result.Recovery);
        Assert.Same(journal, store.Journal); Assert.Equal(writes, store.Writes);
        Assert.Equal(admissions, backend.Admissions.Count); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task NewPreparedOperationAfterRestartStillRestoresEarlierOwnedCores()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        store.FailWrite = store.Writes + 2; // Prepared is durable; submission marker fails.
        Assert.Equal(CpuLiveCurveBlockReason.JournalUnavailable, (await transaction.ApplySelectiveAsync([new(8, -2)], default)).Reason);
        Assert.Equal(CpuLiveCurveOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(0, store.Journal.CompletedSteps); Assert.Equal(0, store.Journal.OperationBaseline[0].Offset);
        Assert.Equal(-1, store.Journal.OriginalBaseline[0].Offset);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(new[] { 0, 0 }, backend.Sent.Select(command => command.PhysicalCoreId));
        Assert.All(backend.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Fact]
    public async Task PartialAcknowledgedOperationCanBeReplacedAndAllOwnedCoresRestored()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        backend.Admit = (_, purpose) => purpose == CpuLiveCurveCommandPurpose.Restore || backend.Sent.Count < 2;
        await transaction.ApplyAllCoreAsync(new(true, -2), default);
        Assert.Equal(2, store.Journal!.CompletedSteps);
        backend.Admit = (_, _) => true;
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplySelectiveAsync([new(8, -3)], default)).Outcome);
        Assert.Single(store.Journal!.Steps);
        await transaction.RestoreAsync(default);
        Assert.Equal(new[] { 0, 2, 8, 8, 2, 0 }, backend.Sent.Select(command => command.PhysicalCoreId));
        Assert.All(backend.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedEditRequiresImmediateAndOriginalReverseAdmission(bool priorCoreOutsideTargets)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Values[0] = new(0, 12);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, -2)], default);
        var journal = store.Journal; int writes = store.Writes;
        backend.Admit = (command, purpose) => purpose != CpuLiveCurveCommandPurpose.Restore || command.Offset != 12;
        var result = await transaction.ApplySelectiveAsync([new(priorCoreOutsideTargets ? 8 : 0, -3)], default);
        Assert.Equal(CpuLiveCurveBlockReason.AdmissionDenied, result.Reason);
        Assert.Same(journal, store.Journal); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
        if (!priorCoreOutsideTargets)
        { Assert.Contains(backend.Admissions, entry => entry.Item1.Offset == -2 && entry.Item2 == CpuLiveCurveCommandPurpose.Restore); }
    }

    [Fact]
    public async Task ReturningEveryOwnedCoreToOriginalResolvesOwnership()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0), new(8, -2)], default);
        var partial = await transaction.ApplySelectiveAsync([new(0, -1)], default);
        Assert.Equal(CpuLiveCurveRecovery.RestoreReady, partial.Recovery);
        var returned = await transaction.ApplySelectiveAsync([new(8, -1)], default);
        Assert.Equal(CpuLiveCurveOutcome.Verified, returned.Outcome); Assert.Equal(CpuLiveCurveRecovery.None, returned.Recovery);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal!.State);
        int sent = backend.Sent.Count;
        Assert.Equal(CpuLiveCurveRecovery.None, (await transaction.RestoreAsync(default)).Recovery);
        Assert.Equal(sent, backend.Sent.Count);
    }

    [Fact]
    public async Task RepeatedEditCannotSubstituteOriginalAdmissionForImmediateReverse()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, -2)], default);
        var journal = store.Journal;
        backend.Admit = (command, purpose) => purpose != CpuLiveCurveCommandPurpose.Restore || command.Offset != -2;
        Assert.Equal(CpuLiveCurveBlockReason.AdmissionDenied, (await transaction.ApplySelectiveAsync([new(0, -3)], default)).Reason);
        Assert.Same(journal, store.Journal); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task FailedRestorationArmingPreservesLatestOperationAndSendsNothing()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, -2)], default);
        await transaction.ApplySelectiveAsync([new(8, -3)], default);
        var journal = store.Journal;
        store.FailWrite = store.Writes + 1;
        Assert.Equal(CpuLiveCurveBlockReason.JournalUnavailable, (await transaction.RestoreAsync(default)).Reason);
        Assert.Same(journal, store.Journal); Assert.Equal(2, backend.Sent.Count);
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.RestoreAsync(default)).Outcome);
        Assert.All(backend.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Fact]
    public async Task PartiallyRestoredOwnershipCanBeEditedWithOriginalBaselineIntact()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, -2), new(8, -2)], default);
        backend.Admit = (_, _) => backend.Sent.Count < 3;
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.RestoreAsync(default)).Outcome);
        Assert.True(store.Journal!.Restoring); Assert.Equal(1, store.Journal.CompletedSteps);
        backend.Admit = (_, _) => true;
        Assert.Equal(CpuLiveCurveOutcome.Verified, (await transaction.ApplySelectiveAsync([new(2, -3)], default)).Outcome);
        Assert.All(store.Journal!.OriginalBaseline, core => Assert.Equal(-1, core.Offset));
        await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(default);
        Assert.Equal(new[] { 0, 8, 8, 2, 2, 0 }, backend.Sent.Select(command => command.PhysicalCoreId));
        Assert.All(backend.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("context")]
    [InlineData("unselected-core")]
    public async Task RepeatedEditNeverAdoptsDriftedContextOrVector(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        var original = store.Journal!.OriginalBaseline;
        if (fault == "boot") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Sequence = 43 } }; }
        if (fault == "context") { backend.Context = backend.Context with { Identity = backend.Context.Identity with { Model = "RZ09-9999" } }; }
        if (fault == "unselected-core") { backend.Values[11] = new(15, -9); }
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.ApplySelectiveAsync([new(8, -2)], default)).Outcome);
        Assert.Equal(original, store.Journal!.OriginalBaseline); Assert.Single(backend.Sent);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedDriftRemainsBlockedAfterValuesReturnAndRestart(bool contextDrift)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        var expected = backend.Values.ToArray(); var context = backend.Context;
        var original = store.Journal!.OriginalBaseline;
        if (contextDrift) { backend.Context = context with { BootSequence = context.BootSequence with { Sequence = 43 } }; }
        else { backend.Values[11] = new(15, -9); }
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOwnershipState.Blocked, store.Journal!.State);
        Assert.Equal(original, store.Journal.OriginalBaseline);
        backend.Values = expected; backend.Context = context;
        int reads = backend.Reads, writes = store.Writes;
        var restarted = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.ApplySelectiveAsync([new(8, -2)], default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await restarted.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task PossibleSubmissionInRepeatedEditPreservesOriginalAndNeverReplays()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        var original = store.Journal!.OriginalBaseline;
        store.FailWrite = store.Writes + 3;
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await transaction.ApplySelectiveAsync([new(8, -2)], default)).Outcome);
        int reads = backend.Reads;
        var restarted = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await restarted.ApplySelectiveAsync([new(8, -3)], default)).Outcome);
        Assert.Equal(CpuLiveCurveOutcome.Uncertain, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(2, backend.Sent.Count);
        Assert.Equal(original, store.Journal!.OriginalBaseline);
    }

    internal sealed class MemoryStore : ICpuLiveCurveOwnershipJournalStore
    {
        internal CpuLiveCurveOwnershipJournal? Journal;
        internal int Writes;
        internal int? FailWrite;
        internal string? Hash;
        public Task<CpuLiveCurveJournalLoadResult> LoadAsync(CancellationToken token) => Task.FromResult(new CpuLiveCurveJournalLoadResult(Journal, false, Hash));
        public Task<string?> TryWriteAsync(string? expectedSha256, CpuLiveCurveOwnershipJournal journal, CancellationToken token)
        {
            Assert.True(journal.IsWellFormed);
            if (++Writes == FailWrite || expectedSha256 != Hash) { return Task.FromResult<string?>(null); }
            Journal = journal; Hash = Writes.ToString(); return Task.FromResult<string?>(Hash);
        }
    }

    internal sealed class FakeBackend(MemoryStore store) : ICpuLiveCurveBackend
    {
        internal CpuLiveCurveContext Context = ContextFixture();
        internal CpuLiveCoreOffset[] Values = ValuesFixture();
        internal readonly List<CpuLiveCurveCommand> Sent = [];
        internal readonly List<(CpuLiveCurveCommand, CpuLiveCurveCommandPurpose)> Admissions = [];
        internal Func<CpuLiveCurveCommand, CpuLiveCurveCommandPurpose, bool> Admit = (_, _) => true;
        internal Func<CpuLiveCurveCommand, Task<CpuLiveCurveSubmission>>? OnSend;
        internal Action? AfterSend;
        internal Func<CpuLivePowerPeerState, bool>? CheckPeer;
        internal readonly List<CpuLivePowerPeerState> Peers = [];
        internal int Reads;
        internal Func<CancellationToken, Task<CpuLiveCurveBackendObservation>>? OnObserve;
        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token)
        { Reads++; return OnObserve?.Invoke(token) ?? Task.FromResult(new CpuLiveCurveBackendObservation(Context, new(0xFF55, 0x005D000B, 0xD50, Values.ToArray()))); }
        public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token)
        { Admissions.Add((command, purpose)); return Task.FromResult(Context.MatchesHardwareContext(context) && Admit(command, purpose)); }
        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token)
        {
            Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
            Assert.True(Context.MatchesHardwareContext(context)); Assert.Equal(Values, expectedBefore.CoreOffsets);
            Assert.Contains((command, purpose), Admissions);
            Sent.Add(command); return OnSend?.Invoke(command) ?? Apply(command);
        }
        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CpuLivePowerPeerState? peer, CancellationToken token)
        {
            if (peer is not null)
            {
                Peers.Add(peer);
                if (CheckPeer?.Invoke(peer) != true) { return Task.FromResult(CpuLiveCurveSubmission.NotSubmitted); }
            }
            return SendAsync(context, command, purpose, expectedBefore, token);
        }
        internal Task<CpuLiveCurveSubmission> Apply(CpuLiveCurveCommand command)
        {
            int index = Array.FindIndex(Values, core => core.PhysicalCoreId == command.PhysicalCoreId);
            Values[index] = new(command.PhysicalCoreId, command.Offset); AfterSend?.Invoke();
            return Task.FromResult(CpuLiveCurveSubmission.Acknowledged);
        }
    }

    internal static CpuLiveCoreOffset[] ValuesFixture() => new[] { 0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15 }
        .Select(id => new CpuLiveCoreOffset(id, -1)).ToArray();
    internal static CpuLiveCurveContext ContextFixture() => new(new("RZ09-0528", 0x1532, 0x02C6, "2.02"), 0xFF55, 0x005D000B, 0xD50,
        new(Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DriverPath],
            Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Platform.dll"],
            Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"]),
        new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 42));
}
