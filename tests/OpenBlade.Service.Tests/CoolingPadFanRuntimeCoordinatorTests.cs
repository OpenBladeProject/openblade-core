using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadFanRuntimeCoordinatorTests
{
    [Fact]
    public async Task UnavailableCoordinator_RemainsObservedOnlyAndPerformsNoWrite()
    {
        ICoolingPadFanRuntimeCoordinator coordinator =
            UnavailableCoolingPadFanRuntimeCoordinator.Instance;

        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "test demand",
            CancellationToken.None);
        await coordinator.HandbackAsync("test handback", CancellationToken.None);

        Assert.Equal(DeviceFeatureAccess.ObservedOnly, coordinator.Snapshot.Access);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedDemand);
        Assert.Null(coordinator.Snapshot.AppliedTarget);
        Assert.True(coordinator.Snapshot.IsWellFormed);
    }

    [Fact]
    public async Task Reconcile_AppliesComputedTargetAndDeduplicatesQuantizedTarget()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        var first = new CoolingDemandLevel(6_875);
        var sameTarget = new CoolingDemandLevel(6_880);

        await coordinator.ReconcileAsync(first, "first demand", CancellationToken.None);
        await coordinator.ReconcileAsync(
            sameTarget,
            "same quantized target",
            CancellationToken.None);

        CoolingPadFanTarget expected = CoolingPadFanTarget.FromDemand(first);
        Assert.Equal([expected], backend.AppliedTargets);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, coordinator.Snapshot.Access);
        Assert.Equal(CoolingPadControlOwnershipState.Owned, coordinator.Snapshot.Ownership);
        Assert.Equal(sameTarget, coordinator.Snapshot.AppliedDemand);
        Assert.Equal(expected, coordinator.Snapshot.AppliedTarget);
        Assert.Equal("same quantized target", coordinator.Snapshot.Reason);
        Assert.True(coordinator.Snapshot.IsWellFormed);
    }

    [Fact]
    public async Task Reconcile_NullDemandHandsBackAndDoesNotOwnDesiredIntent()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        var savedIntent = new CoolingPadFanIntent(
            CoolingPadFanMode.SmartCurve,
            CoolingPadFanIntent.Default.FixedDemand,
            CoolingPadSmartCurve.Default);
        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "smart curve demand",
            CancellationToken.None);

        await coordinator.ReconcileAsync(
            demand: null,
            reason: "automatic handback",
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, backend.HandbackCount);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedDemand);
        Assert.Equal(CoolingPadFanMode.SmartCurve, savedIntent.Mode);
        Assert.Same(CoolingPadSmartCurve.Default, savedIntent.SmartCurve);
    }

    [Fact]
    public async Task LifecycleHandback_UsesNarrowBackendCleanupPath()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "apply",
            CancellationToken.None);

        await coordinator.HandbackForLifecycleAsync(
            "low power",
            CancellationToken.None);

        Assert.Equal(0, backend.HandbackCount);
        Assert.Equal(1, backend.LifecycleHandbackCount);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task IndeterminateApply_LatchesAndNeverRetriesBackend()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "unknown"),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);

        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "apply",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ReconcileAsync(
            new CoolingDemandLevel(7_000),
            "retry",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandbackAsync(
            "retry handback",
            CancellationToken.None));

        Assert.Equal(CoolingPadControlOwnershipState.Indeterminate, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedDemand);
        Assert.Single(backend.AppliedTargets);
        Assert.Equal(0, backend.HandbackCount);
    }

    [Fact]
    public async Task KnownNotAppliedApply_IsLatchedUntilIntentChanges()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "blocked"),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        var blockedDemand = new CoolingDemandLevel(6_000);

        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.ReconcileAsync(
            blockedDemand,
            "first attempt",
            CancellationToken.None));
        await coordinator.ReconcileAsync(
            blockedDemand,
            "maintenance tick",
            CancellationToken.None);

        Assert.Single(backend.AppliedTargets);
        Assert.Contains("not retried", coordinator.Snapshot.Reason, StringComparison.Ordinal);

        backend.ApplyFailure = null;
        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(7_000),
            "changed intent",
            CancellationToken.None);

        Assert.Equal(2, backend.AppliedTargets.Count);
        Assert.Equal(CoolingPadControlOwnershipState.Owned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task PreSubmissionExhaustion_RetriesOnceOnLaterTickThenSucceeds()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = ExhaustedFailure(),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        var demand = new CoolingDemandLevel(6_000);

        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.ReconcileAsync(
            demand,
            "exhausted session",
            CancellationToken.None));
        backend.ApplyFailure = null;

        await coordinator.ReconcileAsync(
            demand,
            "replacement session",
            CancellationToken.None);

        Assert.Equal(2, backend.AppliedTargets.Count);
        Assert.Equal(CoolingPadControlOwnershipState.Owned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task RepeatedPreSubmissionExhaustion_HardLatchesAfterOneLaterRetry()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = ExhaustedFailure(),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        var demand = new CoolingDemandLevel(6_000);

        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.ReconcileAsync(
            demand,
            "first exhausted session",
            CancellationToken.None));
        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.ReconcileAsync(
            demand,
            "replacement also exhausted",
            CancellationToken.None));
        await coordinator.ReconcileAsync(
            demand,
            "later maintenance tick",
            CancellationToken.None);

        Assert.Equal(2, backend.AppliedTargets.Count);
        Assert.Contains("not retried", coordinator.Snapshot.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownNotAppliedHandback_IsLatchedButLifecycleCanRearmOnce()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "apply",
            CancellationToken.None);
        backend.HandbackFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "blocked");

        await Assert.ThrowsAsync<BladeWriteException>(() => coordinator.HandbackAsync(
            "first handback",
            CancellationToken.None));
        await coordinator.HandbackAsync(
            "maintenance handback",
            CancellationToken.None);

        Assert.Equal(1, backend.HandbackCount);
        backend.HandbackFailure = null;
        await coordinator.HandbackForLifecycleAsync(
            "low-power handback",
            CancellationToken.None);

        Assert.Equal(1, backend.LifecycleHandbackCount);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task InvalidateConnection_ChangesOwnedToIndeterminateWithoutBackendWrite()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);
        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_000),
            "apply",
            CancellationToken.None);

        await coordinator.InvalidateConnectionAsync(
            "exact cooling-pad identity was lost",
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ReconcileAsync(
            new CoolingDemandLevel(7_000),
            "retry",
            CancellationToken.None));

        Assert.Equal(CoolingPadControlOwnershipState.Indeterminate, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedDemand);
        Assert.Null(coordinator.Snapshot.AppliedTarget);
        Assert.Single(backend.AppliedTargets);
        Assert.Equal(0, backend.HandbackCount);
    }

    [Fact]
    public async Task InvalidateConnection_LeavesUnownedStateWithoutBackendWrite()
    {
        var backend = new FakeBackend();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(backend);

        await coordinator.InvalidateConnectionAsync(
            "pad absent",
            CancellationToken.None);

        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedDemand);
        Assert.Empty(backend.AppliedTargets);
        Assert.Equal(0, backend.HandbackCount);
    }

    [Fact]
    public void ProductionFactory_RejectsClosedEvidenceOrImplementationGates()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            AdmittedCoolingPadFanRuntimeCoordinator.CreateForProduction(
                new FakeBackend(),
                UnavailableCoolingPadFanOwnershipJournalStore.Instance));

        Assert.Contains("not admitted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_RecordsPendingOwnedHandbackAndClear()
    {
        var backend = new FakeBackend();
        var journal = new RecordingJournalStore();
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(
            backend,
            journal,
            TimeProvider.System);

        await coordinator.ReconcileAsync(
            new CoolingDemandLevel(6_875),
            "apply",
            CancellationToken.None);
        await coordinator.HandbackAsync("handback", CancellationToken.None);

        Assert.Equal(
            [
                CoolingPadFanOwnershipJournalState.AcquirePending,
                CoolingPadFanOwnershipJournalState.OwnedConfirmed,
                CoolingPadFanOwnershipJournalState.HandbackPending,
            ],
            journal.Writes.Select(entry => entry.State).ToArray());
        Assert.Equal(1, journal.ClearCalls);
        Assert.Null(journal.Journal);
    }

    [Fact]
    public async Task UnexpectedExit_ConfirmedOwnershipReleasesExactlyOnce()
    {
        var backend = new FakeBackend();
        var journal = new RecordingJournalStore
        {
            Journal = new CoolingPadFanOwnershipJournal(
                CoolingPadFanOwnershipJournalState.OwnedConfirmed,
                new CoolingPadFanTarget(2_200),
                DateTimeOffset.UtcNow),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(
            backend,
            journal,
            TimeProvider.System);

        CoolingPadFanCrashRecoveryResult result =
            await coordinator.RecoverAfterUnexpectedExitAsync(default);

        Assert.Equal(CoolingPadFanCrashRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, backend.HandbackCount);
        Assert.Equal(1, journal.ClearCalls);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task Apply_FinalJournalFailureRetainsPendingRecoveryState()
    {
        var backend = new FakeBackend();
        var journal = new RecordingJournalStore
        {
            FailOnState = CoolingPadFanOwnershipJournalState.OwnedConfirmed,
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(
            backend,
            journal,
            TimeProvider.System);

        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ReconcileAsync(
                new CoolingDemandLevel(6_875),
                "apply",
                CancellationToken.None));

        Assert.Equal("journal write failed", failure.Message);
        Assert.Single(backend.AppliedTargets);
        Assert.Equal(
            CoolingPadFanOwnershipJournalState.AcquirePending,
            journal.Journal?.State);
        Assert.Equal(
            CoolingPadControlOwnershipState.Owned,
            coordinator.Snapshot.Ownership);
        Assert.Contains(
            "last confirmed target",
            coordinator.Snapshot.Reason,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)CoolingPadFanOwnershipJournalState.AcquirePending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.UpdatePending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.HandbackPending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.Indeterminate)]
    public async Task UnexpectedExit_NonConfirmedJournalBlocksWithoutWrite(
        int stateValue)
    {
        var backend = new FakeBackend();
        var journal = new RecordingJournalStore
        {
            Journal = new CoolingPadFanOwnershipJournal(
                (CoolingPadFanOwnershipJournalState)stateValue,
                new CoolingPadFanTarget(2_200),
                DateTimeOffset.UtcNow),
        };
        var coordinator = AdmittedCoolingPadFanRuntimeCoordinator.CreateForTesting(
            backend,
            journal,
            TimeProvider.System);

        CoolingPadFanCrashRecoveryResult result =
            await coordinator.RecoverAfterUnexpectedExitAsync(default);

        Assert.Equal(CoolingPadFanCrashRecoveryStatus.Blocked, result.Status);
        Assert.Equal(0, backend.HandbackCount);
        Assert.Equal(CoolingPadControlOwnershipState.Indeterminate, coordinator.Snapshot.Ownership);
    }

    private sealed class FakeBackend : ICoolingPadFanSemanticBackend
    {
        internal List<CoolingPadFanTarget> AppliedTargets { get; } = [];

        internal int HandbackCount { get; private set; }

        internal int LifecycleHandbackCount { get; private set; }

        internal Exception? ApplyFailure { get; set; }

        internal Exception? HandbackFailure { get; set; }

        public Task AcquireTargetAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
            => ApplyAsync(target, cancellationToken);

        public Task UpdateTargetAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
            => ApplyAsync(target, cancellationToken);

        private Task ApplyAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppliedTargets.Add(target);
            return ApplyFailure is null
                ? Task.CompletedTask
                : Task.FromException(ApplyFailure);
        }

        public Task HandbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HandbackCount++;
            return HandbackFailure is null
                ? Task.CompletedTask
                : Task.FromException(HandbackFailure);
        }

        public Task HandbackForLifecycleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LifecycleHandbackCount++;
            return HandbackFailure is null
                ? Task.CompletedTask
                : Task.FromException(HandbackFailure);
        }
    }

    private static BladeWriteException ExhaustedFailure() => new(
        WriteSubmissionState.KnownNotApplied,
        "transaction space exhausted")
    {
        FailureKind = HidSessionFailureKind.Exhausted,
    };

    private sealed class RecordingJournalStore
        : ICoolingPadFanOwnershipJournalStore
    {
        internal CoolingPadFanOwnershipJournal? Journal { get; set; }

        internal bool Invalid { get; set; }

        internal CoolingPadFanOwnershipJournalState? FailOnState { get; set; }

        internal List<CoolingPadFanOwnershipJournal> Writes { get; } = [];

        internal int ClearCalls { get; private set; }

        public Task<CoolingPadFanOwnershipJournalLoadResult> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CoolingPadFanOwnershipJournalLoadResult(
                Journal,
                Invalid));
        }

        public Task WriteAsync(
            CoolingPadFanOwnershipJournal journal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journal.State == FailOnState)
            {
                return Task.FromException(new IOException("journal write failed"));
            }

            Journal = journal;
            Writes.Add(journal);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal = null;
            ClearCalls++;
            return Task.CompletedTask;
        }
    }
}
