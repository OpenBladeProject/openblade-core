using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveCurveTuningTransactionTests
{
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
        Assert.Equal(original, store.Journal.Baseline);
        Assert.All(backend.Values, value => Assert.Equal(-2, value.Offset));
        var restored = await new CpuLiveCurveTuningTransaction(backend, store).RestoreAsync(CancellationToken.None);
        Assert.Equal(CpuLiveCurveOutcome.Verified, restored.Outcome);
        Assert.Equal(CpuLiveCurveRecovery.None, restored.Recovery);
        Assert.Equal(original, backend.Values);
        Assert.Equal(original.Select(core => core.PhysicalCoreId).Reverse(), backend.Sent.Skip(12).Select(command => command.PhysicalCoreId));
        Assert.Equal(original.Select(core => core.Offset).Reverse(), backend.Sent.Skip(12).Select(command => command.Offset));
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal.State);
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
        Assert.All(store.Journal.Baseline, value => Assert.Equal(-1, value.Offset));
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

    [Theory]
    [InlineData("boot")]
    [InlineData("unknown-boot")]
    [InlineData("backend")]
    [InlineData("bios")]
    [InlineData("unselected-core")]
    public async Task ChangedContextOrVectorBlocksRestoreAcrossRestart(string fault)
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        await new CpuLiveCurveTuningTransaction(backend, store).ApplySelectiveAsync([new(0, 0)], default);
        if (fault == "boot") { backend.Context = backend.Context with { BootId = Guid.NewGuid() }; }
        if (fault == "unknown-boot") { backend.Context = backend.Context with { BootId = Guid.Empty }; }
        if (fault == "backend") { backend.Context = backend.Context with { Backend = backend.Context.Backend with { DriverSha256 = "other" } }; }
        if (fault == "bios") { backend.Context = backend.Context with { Identity = backend.Context.Identity with { BiosVersion = "2.03" } }; }
        if (fault == "unselected-core") { backend.Values[11] = new(15, -9); }
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        int writes = store.Writes;
        Assert.Equal(CpuLiveCurveRecovery.Blocked, (await transaction.ObserveOwnershipAsync(default)).Recovery);
        Assert.Equal(writes, store.Writes); // reconciliation is read-only
        Assert.Equal(CpuLiveCurveOutcome.Blocked, (await transaction.RestoreAsync(default)).Outcome);
        Assert.Single(backend.Sent); Assert.Equal(CpuLiveCurveOwnershipState.Blocked, store.Journal!.State);
    }

    [Fact]
    public async Task OwnedEditsAreExplicitlyBlockedWithoutReplacingOriginalBaseline()
    {
        var store = new MemoryStore(); var backend = new FakeBackend(store);
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        await transaction.ApplySelectiveAsync([new(0, 0)], default);
        var original = store.Journal;
        Assert.Equal(CpuLiveCurveBlockReason.ExistingOwnership,
            (await transaction.ApplyAllCoreAsync(new(true, -2), default)).Reason);
        Assert.Same(original, store.Journal); Assert.Single(backend.Sent);
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
        internal int Reads;
        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token)
        { Reads++; return Task.FromResult(new CpuLiveCurveBackendObservation(Context, new(0xFF55, 0x005D000B, 0xD50, Values.ToArray()))); }
        public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token)
        { Admissions.Add((command, purpose)); return Task.FromResult(context == Context && Admit(command, purpose)); }
        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token)
        {
            Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, store.Journal!.State);
            Assert.Equal(Context, context); Assert.Equal(Values, expectedBefore.CoreOffsets);
            Assert.Contains((command, purpose), Admissions);
            Sent.Add(command); return OnSend?.Invoke(command) ?? Apply(command);
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
            Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"]), Guid.NewGuid());
}
