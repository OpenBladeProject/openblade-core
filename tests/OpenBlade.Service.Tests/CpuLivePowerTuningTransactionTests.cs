using System.Buffers.Binary;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLivePowerTuningTransactionTests
{
    [Fact]
    public async Task RepeatedEditsAndRestartRestoreFirstExactFractionalUnequalOriginal()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var original = backend.Pair;
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        Assert.Equal(CpuLivePowerOutcome.Verified, (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default)).Outcome);
        var id = store.Journal!.TransactionId;
        var originalContext = store.Journal.Context;
        backend.Context = backend.Context with { Identity = backend.Context.Identity with { BiosVersion = "2.03" } };
        Assert.Equal(CpuLivePowerOutcome.Verified, (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89), new(CpuLivePowerLimit.Slow, 79)], default)).Outcome);
        Assert.Equal(original, store.Journal!.OriginalBaseline.Decode()); Assert.Equal(id, store.Journal.TransactionId);
        Assert.Equal(90000u, store.Journal.OperationBaseline.Fast.Milliwatts);
        backend.Context = backend.Context with { Identity = backend.Context.Identity with { BiosVersion = "Unknown" } };
        Assert.Equal(CpuLivePowerOutcome.Verified, (await new CpuLivePowerTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(originalContext, store.Journal.Context);
        Assert.Equal(original, backend.Pair); Assert.Equal(CpuLivePowerOwnershipState.Restored, store.Journal!.State);
        Assert.Equal(new[] { 100123u, 80765u }, backend.Sent.Skip(3).Select(request => request.Command.Milliwatts));
        Assert.All(backend.Sent, request => Assert.Equal(original, request.Original));
        Assert.Equal(2, store.Journal.Steps.Count); Assert.Equal(2, store.Journal.CompletedSteps);
    }

    [Fact]
    public async Task IntermediateReturnToFullOriginalDoesNotLoseRemainingStep()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store) { Pair = PairFixture(85000, 80000) };
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 84)], default);
        var original = store.Journal!.OriginalBaseline;
        var result = await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 85), new(CpuLivePowerLimit.Slow, 79)], default);
        Assert.Equal(CpuLivePowerOutcome.Verified, result.Outcome); Assert.Equal(3, backend.Sent.Count);
        Assert.Contains(store.History, journal => journal.State == CpuLivePowerOwnershipState.OwnedVerified
            && journal.CompletedSteps == 1 && journal.Steps.Count == 2 && journal.Expected == original);
        Assert.Equal(CpuLivePowerOutcome.Verified, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Equal(original.Decode(), backend.Pair);
    }

    [Fact]
    public async Task NoOpUsesObservationWithoutSetterOrChangingExistingOwnership()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        Assert.Equal(CpuLivePowerOutcome.Unchanged, (await transaction.ApplySelectiveAsync([], default)).Outcome);
        Assert.Null(store.Journal); Assert.Empty(backend.Sent); Assert.Empty(backend.Admissions);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var journal = store.Journal; int writes = store.Writes, admits = backend.Admissions.Count;
        var result = await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        Assert.Equal(CpuLivePowerOutcome.Unchanged, result.Outcome); Assert.Equal(CpuLivePowerRecovery.RestoreReady, result.Recovery);
        Assert.Same(journal, store.Journal); Assert.Equal(writes, store.Writes); Assert.Equal(admits, backend.Admissions.Count);
        Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalAdmissionForPreviouslyChangedLimitCannotBeReplacedByImmediateReverse(bool unselected)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var journal = store.Journal; int writes = store.Writes;
        backend.Admit = (command, purpose) => purpose != CpuLivePowerCommandPurpose.Restore || command.Milliwatts != 100123;
        var result = await transaction.ApplySelectiveAsync([new(unselected ? CpuLivePowerLimit.Slow : CpuLivePowerLimit.Fast, 79)], default);
        Assert.Equal(CpuLivePowerBlockReason.AdmissionDenied, result.Reason); Assert.Same(journal, store.Journal);
        Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
        if (!unselected) { Assert.Contains(backend.Admissions, a => a.Command.Milliwatts == 90000 && a.Purpose == CpuLivePowerCommandPurpose.Restore); }
    }

    [Fact]
    public async Task OriginalAdmissionDoesNotSubstituteForExactImmediateReverse()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var journal = store.Journal;
        backend.Admit = (command, purpose) => purpose != CpuLivePowerCommandPurpose.Restore || command.Milliwatts != 90000;
        Assert.Equal(CpuLivePowerBlockReason.AdmissionDenied, (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89)], default)).Reason);
        Assert.Same(journal, store.Journal); Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("capability")]
    [InlineData("cap")]
    [InlineData("original-range")]
    [InlineData("step")]
    public async Task CandidateMetadataNeverSkipsIndependentAdmissionOrOriginalRange(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        if (fault == "range") { backend.Range = null; }
        if (fault == "capability") { backend.Capability = null; }
        if (fault == "cap") { backend.Capability = CpuLivePowerCapability.TryDecode(0); }
        if (fault == "original-range") { backend.Range = new(0x50001, 0, 100000, 1); }
        if (fault == "step") { backend.Range = new(0x50001, 0, 125000, 2); }
        var result = await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        Assert.Equal(CpuLivePowerBlockReason.AdmissionDenied, result.Reason); Assert.Null(store.Journal); Assert.Empty(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryRemainingOriginalIsReadmittedAndRangeCheckedAtStepBoundary(bool rangeChanges)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.AfterSend = () =>
        {
            // After fast is changed, its original remains necessary while slow
            // is next. Keep post-write verification metadata valid, revoke on
            // the next actual before observation.
            backend.OnObserve = () => { if (backend.Reads >= 4 && rangeChanges) { backend.Range = new(0x50001, 0, 100000, 1); } };
        };
        backend.Admit = (command, purpose) => backend.Sent.Count == 0 || rangeChanges
            || purpose != CpuLivePowerCommandPurpose.Restore || command.Milliwatts != 100123;
        var result = await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync(
            [new(CpuLivePowerLimit.Fast, 90), new(CpuLivePowerLimit.Slow, 79)], default);
        Assert.Equal(CpuLivePowerBlockReason.AdmissionDenied, result.Reason); Assert.Single(backend.Sent);
        Assert.Equal(1, store.Journal!.CompletedSteps); Assert.Equal(CpuLivePowerOwnershipState.OwnedVerified, store.Journal.State);
    }

    [Fact]
    public async Task PartialRestorationCanBeReplacedWithoutLosingFirstOriginal()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var original = backend.Pair;
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90), new(CpuLivePowerLimit.Slow, 79)], default);
        backend.Admit = (_, _) => backend.Sent.Count < 3;
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await transaction.RestoreAsync(default)).Outcome);
        Assert.True(store.Journal!.Restoring); Assert.Equal(1, store.Journal.CompletedSteps);
        backend.Admit = (_, _) => true;
        await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89)], default);
        Assert.Equal(original, store.Journal!.OriginalBaseline.Decode());
        Assert.Equal(CpuLivePowerOutcome.Verified, (await new CpuLivePowerTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(original, backend.Pair);
    }

    [Theory]
    [InlineData("stapm")]
    [InlineData("unselected")]
    [InlineData("boot")]
    public async Task FreshDriftBlocksReplacementAndRestoreWithoutAdoptingIt(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default); var original = store.Journal!.OriginalBaseline;
        if (fault == "stapm") { backend.Pair = backend.Pair with { StapmRawBits = Bits(59000) }; }
        if (fault == "unselected") { backend.Pair = backend.Pair with { Slow = PairFixture(90000, 79000).Slow }; }
        if (fault == "boot") { backend.Context = backend.Context with { BootSequence = backend.Context.BootSequence with { Sequence = 43 } }; }
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89)], default)).Outcome);
        Assert.Equal(CpuLivePowerOwnershipState.Blocked, store.Journal!.State); Assert.Equal(original, store.Journal.OriginalBaseline);
        int reads = backend.Reads;
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await new CpuLivePowerTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedDriftRemainsBlockedAfterValuesReturnAndRestart(bool contextDrift)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var expected = backend.Pair; var context = backend.Context;
        var original = store.Journal!.OriginalBaseline;
        if (contextDrift) { backend.Context = context with { BootSequence = context.BootSequence with { Sequence = 43 } }; }
        else { backend.Pair = expected with { Slow = PairFixture(90000, 79000).Slow }; }
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await transaction.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(CpuLivePowerOwnershipState.Blocked, store.Journal!.State);
        Assert.Equal(original, store.Journal.OriginalBaseline);
        backend.Pair = expected; backend.Context = context;
        int reads = backend.Reads, writes = store.Writes;
        var restarted = new CpuLivePowerTuningTransaction(backend, store);
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await restarted.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89)], default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await restarted.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task UnavailableObservationPreservesExistingOwnership()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var journal = store.Journal; int writes = store.Writes;
        backend.Unavailable = true;
        Assert.Equal(CpuLivePowerBlockReason.ObservationUnavailable,
            (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Slow, 79)], default)).Reason);
        Assert.Equal(CpuLivePowerBlockReason.ObservationUnavailable, (await transaction.RestoreAsync(default)).Reason);
        Assert.Same(journal, store.Journal); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
        backend.Unavailable = false; backend.Context = ContextFixture();
        Assert.Equal(CpuLivePowerOutcome.Verified, (await transaction.RestoreAsync(default)).Outcome);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("restore")]
    [InlineData("ticket")]
    [InlineData("observe")]
    public async Task ChangedNonNullInvalidContextDurablyBlocksBeforeSubmission(string operation)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        var original = store.Journal!.OriginalBaseline;
        CpuLivePowerTuningTransaction.PreparedOperation? ticket = null;
        if (operation == "ticket")
        { ticket = (await transaction.PrepareApplyAsync([new(CpuLivePowerLimit.Slow, 79)], default)).Operation; }
        backend.Context = backend.Context with { Backend = backend.Context.Backend with { DriverSha256 = "unknown" } };
        CpuLivePowerTransactionResult result = operation switch
        {
            "prepare" => (await transaction.PrepareApplyAsync([new(CpuLivePowerLimit.Slow, 79)], default)).Result,
            "restore" => (await transaction.PrepareRestoreAsync(default)).Result,
            "observe" => await transaction.ObserveOwnershipAsync(default),
            _ => await transaction.ValidatePreparedAsync(ticket!, default),
        };
        Assert.Equal(CpuLivePowerBlockReason.ContextMismatch, result.Reason);
        Assert.Equal(CpuLivePowerOwnershipState.Blocked, store.Journal!.State);
        Assert.Equal(original, store.Journal.OriginalBaseline); Assert.Single(backend.Sent);
        backend.Context = ContextFixture();
        int reads = backend.Reads, writes = store.Writes;
        var restarted = new CpuLivePowerTuningTransaction(backend, store);
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Blocked,
            (await restarted.ApplySelectiveAsync([new(CpuLivePowerLimit.Slow, 79)], default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Blocked, (await restarted.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedPrepareOrPendingCasSendsNothing(int write)
    {
        var store = new MemoryStore { FailWrite = write }; var backend = new FakeBackend(store);
        Assert.Equal(CpuLivePowerBlockReason.JournalUnavailable,
            (await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default)).Reason);
        Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task FailedReplacementPendingCasLeavesEarlierOwnershipRestorableAcrossRestart()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var original = backend.Pair;
        var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        store.FailWrite = store.Writes + 2;
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Slow, 79)], default);
        Assert.Equal(CpuLivePowerOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(original, store.Journal.OriginalBaseline.Decode()); Assert.Equal(90000u, store.Journal.Expected.Fast.Milliwatts);
        Assert.Equal(CpuLivePowerOutcome.Verified, (await new CpuLivePowerTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        Assert.Equal(original, backend.Pair); Assert.Equal(2, backend.Sent.Count);
    }

    [Fact]
    public async Task FailedRestorationPreparationPreservesLatestOperation()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store); var transaction = new CpuLivePowerTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default); var journal = store.Journal;
        store.FailWrite = store.Writes + 1;
        Assert.Equal(CpuLivePowerBlockReason.JournalUnavailable, (await transaction.RestoreAsync(default)).Reason);
        Assert.Same(journal, store.Journal); Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTerminalCleanNotSubmittedCanClearPendingAndFailedClearKeepsIt(bool failClear)
    {
        var store = new MemoryStore { FailWrite = failClear ? 3 : null }; var backend = new FakeBackend(store);
        backend.OnSend = _ => Task.FromResult(CpuLivePowerSubmission.NotSubmitted);
        var result = await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        Assert.Equal(failClear ? CpuLivePowerOutcome.Uncertain : CpuLivePowerOutcome.Blocked, result.Outcome);
        Assert.Equal(failClear ? CpuLivePowerOwnershipState.PossibleSubmission : CpuLivePowerOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(PairFixture(), backend.Pair);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("exception")]
    [InlineData("readback")]
    [InlineData("progress-cas")]
    [InlineData("stapm")]
    public async Task AmbiguityRemainsPendingAcrossRestartWithoutGetterOrReplay(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        if (fault == "unknown") { backend.OnSend = _ => Task.FromResult(CpuLivePowerSubmission.Unknown); }
        if (fault == "exception") { backend.OnSend = _ => throw new IOException(); }
        if (fault == "readback") { backend.OnSend = _ => Task.FromResult(CpuLivePowerSubmission.Acknowledged); }
        if (fault == "progress-cas") { store.FailWrite = 3; }
        if (fault == "stapm") { backend.AfterSend = () => backend.Pair = backend.Pair with { StapmRawBits = Bits(59000) }; }
        var result = await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default);
        Assert.Equal(CpuLivePowerOutcome.Uncertain, result.Outcome); Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, store.Journal!.State);
        int reads = backend.Reads, writes = store.Writes;
        var restarted = new CpuLivePowerTuningTransaction(backend, store);
        Assert.Equal(CpuLivePowerOutcome.Uncertain, (await restarted.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 89)], default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Uncertain, (await restarted.ObserveOwnershipAsync(default)).Outcome);
        Assert.Equal(CpuLivePowerOutcome.Uncertain, (await restarted.RestoreAsync(default)).Outcome);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutSenderCannotAdvanceOnLateCompletion(bool timeout)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CpuLivePowerSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnSend = _ => { entered.SetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        var transaction = new CpuLivePowerTuningTransaction(backend, store, timeout ? TimeSpan.FromMilliseconds(100) : null);
        var pending = transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout) { cancellation.Cancel(); }
        Assert.Equal(CpuLivePowerOutcome.Uncertain, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        int reads = backend.Reads, writes = store.Writes;
        Assert.Equal(CpuLivePowerOutcome.Uncertain, (await new CpuLivePowerTuningTransaction(backend, store).RestoreAsync(default)).Outcome);
        release.SetResult(CpuLivePowerSubmission.Acknowledged); await release.Task;
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, store.Journal!.State);
        Assert.Equal(reads, backend.Reads); Assert.Equal(writes, store.Writes); Assert.Single(backend.Sent);
    }

    [Fact]
    public async Task CancelledLateJournalWriterIsAwaitedAndCannotTriggerSend()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeWrite = async index => { if (index == 2) { entered.SetResult(); await release.Task; } };
        using var cancellation = new CancellationTokenSource();
        var pending = new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.False(pending.IsCompleted); Assert.Empty(backend.Sent);
        release.SetResult(); Assert.Equal(CpuLivePowerOutcome.Uncertain, (await pending).Outcome);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, store.Journal!.State); Assert.Empty(backend.Sent);
    }

    [Fact]
    public async Task RevisionConflictBeforePendingMarkerPreventsSend()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        backend.Admit = (_, _) => { if (store.Journal is not null) { store.Hash = "foreign"; } return true; };
        Assert.Equal(CpuLivePowerBlockReason.JournalUnavailable,
            (await new CpuLivePowerTuningTransaction(backend, store).ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default)).Reason);
        Assert.Empty(backend.Sent); Assert.Equal(CpuLivePowerOwnershipState.Prepared, store.Journal!.State);
    }

    internal sealed class MemoryStore : ICpuLivePowerOwnershipJournalStore
    {
        internal CpuLivePowerOwnershipJournal? Journal;
        internal string? Hash;
        internal int Writes;
        internal int? FailWrite;
        internal Func<int, Task>? BeforeWrite;
        internal readonly List<CpuLivePowerOwnershipJournal> History = [];
        public Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token) => Task.FromResult(new CpuLivePowerJournalLoadResult(Journal, false, Hash));
        public async Task<string?> TryWriteAsync(string? expectedSha256, CpuLivePowerOwnershipJournal journal, CancellationToken token)
        {
            Assert.True(journal.IsWellFormed); int index = ++Writes;
            if (BeforeWrite is not null) { await BeforeWrite(index); }
            if (index == FailWrite || expectedSha256 != Hash) { return null; }
            Journal = journal; Hash = index.ToString(); History.Add(journal); return Hash;
        }
    }

    internal sealed class FakeBackend(MemoryStore store) : ICpuLivePowerBackend
    {
        internal CpuLivePowerContext Context = ContextFixture();
        internal CpuLivePowerPair Pair = PairFixture();
        internal CpuLivePowerFusedCap? Cap = CpuLivePowerFusedCap.TryDecode(4, 7u << 28);
        internal CpuLivePowerCapability? Capability = CpuLivePowerCapability.TryDecode(2);
        internal CpuLivePowerTrustedRange? Range = new(0x50001, 0, 125000, 1);
        internal int Reads;
        internal bool Unavailable;
        internal Action? OnObserve, AfterSend;
        internal Func<CpuLivePowerCommand, CpuLivePowerCommandPurpose, bool> Admit = (_, _) => true;
        internal Func<CpuLivePowerNativeRequest, Task<CpuLivePowerSubmission>>? OnSend;
        internal readonly List<CpuLivePowerNativeRequest> Sent = [];
        internal readonly List<(CpuLivePowerCommand Command, CpuLivePowerCommandPurpose Purpose)> Admissions = [];
        public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token)
        { Reads++; OnObserve?.Invoke(); return Task.FromResult(new CpuLivePowerBackendObservation(Context, Unavailable ? null : Raw(Pair), Cap, Capability, Range)); }
        public Task<bool> CheckWriteAdmissionAsync(CpuLivePowerContext context, CpuLivePowerCommand command,
            CpuLivePowerCommandPurpose purpose, CpuLivePowerPair expectedBefore, CpuLivePowerPair original, CancellationToken token)
        { Admissions.Add((command, purpose)); return Task.FromResult(Context.MatchesHardwareContext(context) && Admit(command, purpose)); }
        public Task<CpuLivePowerSubmission> SendAsync(CpuLivePowerContext context, CpuLivePowerNativeRequest request, CancellationToken token)
        {
            Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, store.Journal!.State);
            Assert.True(Context.MatchesHardwareContext(context)); Assert.Equal(Pair, request.Before);
            Assert.Equal(store.Journal.OriginalBaseline.Decode(), request.Original);
            Assert.Equal(request.Before.With(request.Command), request.After);
            Assert.Contains((request.Command, request.Purpose), Admissions);
            Sent.Add(request);
            if (OnSend is not null) { return OnSend(request); }
            Pair = request.After; AfterSend?.Invoke(); return Task.FromResult(CpuLivePowerSubmission.Acknowledged);
        }
    }

    internal static CpuLivePowerContext ContextFixture()
    {
        var context = CpuLiveCurveTuningTransactionTests.ContextFixture();
        return new(context.Identity, context.EnabledCoreMask, context.PmVersion, context.PmTableLength, context.Backend, context.BootSequence);
    }
    internal static uint Bits(uint value) => BitConverter.SingleToUInt32Bits((float)value * 0.001f);
    internal static CpuLivePowerPair PairFixture(uint fast = 100123, uint slow = 80765)
    {
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(fast, out var f)); Assert.True(CpuLivePowerValue.TryFromMilliwatts(slow, out var s));
        return new(f, s, Bits(60000));
    }
    internal static CpuPmRawSnapshot Raw(CpuLivePowerPair pair)
    {
        var reply = new byte[0xD54];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, 0xD50);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), pair.StapmRawBits);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), pair.Fast.RawBits);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(20), pair.Slow.RawBits);
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), DateTimeOffset.UtcNow, 123, out var raw));
        return raw!;
    }
}
