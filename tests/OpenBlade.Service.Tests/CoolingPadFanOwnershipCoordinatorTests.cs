using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadFanOwnershipCoordinatorTests
{
    [Fact]
    public void InitialSnapshot_IsUnownedWithoutAppliedTarget()
    {
        var coordinator = new CoolingPadFanOwnershipCoordinator(new FakeBackend());

        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Unowned,
                AppliedTarget: null),
            coordinator.Snapshot);
        Assert.True(coordinator.Snapshot.IsWellFormed);
    }

    [Fact]
    public async Task ApplyTarget_ClaimsOwnershipOnlyAfterBackendSuccessAndDeduplicates()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        var target = new CoolingPadFanTarget(2_200);

        bool submitted = await coordinator.ApplyTargetAsync(
            target,
            CancellationToken.None);
        bool repeated = await coordinator.ApplyTargetAsync(
            target,
            CancellationToken.None);
        var updatedTarget = new CoolingPadFanTarget(2_250);
        bool updated = await coordinator.ApplyTargetAsync(
            updatedTarget,
            CancellationToken.None);

        Assert.True(submitted);
        Assert.False(repeated);
        Assert.True(updated);
        Assert.Equal([target, updatedTarget], backend.AppliedTargets);
        Assert.Equal(1, backend.AcquireCount);
        Assert.Equal(1, backend.UpdateCount);
        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Owned,
                updatedTarget),
            coordinator.Snapshot);
    }

    [Fact]
    public async Task ApplyTarget_UnownedMatchingPlanStillSubmits()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        var plannedTarget = new CoolingPadFanTarget(2_200);

        await coordinator.ApplyTargetAsync(plannedTarget, CancellationToken.None);

        Assert.Equal([plannedTarget], backend.AppliedTargets);
        Assert.Equal(CoolingPadControlOwnershipState.Owned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task ApplyTarget_KnownNotAppliedPreservesPriorOwnershipAndAllowsRetry()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        var original = new CoolingPadFanTarget(2_200);
        var replacement = new CoolingPadFanTarget(2_250);
        await coordinator.ApplyTargetAsync(original, CancellationToken.None);
        backend.ApplyFailure = KnownNotApplied("rejected");

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => coordinator.ApplyTargetAsync(replacement, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Owned,
                original),
            coordinator.Snapshot);

        backend.ApplyFailure = null;
        Assert.True(await coordinator.ApplyTargetAsync(
            replacement,
            CancellationToken.None));
        Assert.Equal([original, replacement, replacement], backend.AppliedTargets);
    }

    [Fact]
    public async Task ApplyTarget_IndeterminateLatchesAndBlocksRetry()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = Indeterminate("unknown"),
        };
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        var target = new CoolingPadFanTarget(2_200);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => coordinator.ApplyTargetAsync(target, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApplyTargetAsync(target, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Indeterminate,
                AppliedTarget: null),
            coordinator.Snapshot);
        Assert.Single(backend.AppliedTargets);
    }

    [Fact]
    public async Task ApplyTarget_UnexpectedFailureIsMappedAndLatchedIndeterminate()
    {
        var backend = new FakeBackend
        {
            ApplyFailure = new IOException("transport failed"),
        };
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => coordinator.ApplyTargetAsync(
                new CoolingPadFanTarget(2_200),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(
            CoolingPadControlOwnershipState.Indeterminate,
            coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task ApplyTarget_PreCanceledDoesNotInvokeBackendOrLatch()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyTargetAsync(
                new CoolingPadFanTarget(2_200),
                cancellation.Token));

        Assert.Empty(backend.AppliedTargets);
        Assert.Equal(CoolingPadControlOwnershipState.Unowned, coordinator.Snapshot.Ownership);
    }

    [Fact]
    public async Task Handback_ClearsOwnershipOnlyAfterBackendSuccess()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        await coordinator.ApplyTargetAsync(
            new CoolingPadFanTarget(2_200),
            CancellationToken.None);

        bool submitted = await coordinator.HandbackAsync(CancellationToken.None);
        bool repeated = await coordinator.HandbackAsync(CancellationToken.None);

        Assert.True(submitted);
        Assert.False(repeated);
        Assert.Equal(1, backend.HandbackCount);
        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Unowned,
                AppliedTarget: null),
            coordinator.Snapshot);
    }

    [Fact]
    public async Task Handback_KnownNotAppliedPreservesOwnedTarget()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        var target = new CoolingPadFanTarget(2_200);
        await coordinator.ApplyTargetAsync(target, CancellationToken.None);
        backend.HandbackFailure = KnownNotApplied("rejected");

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => coordinator.HandbackAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(
            new CoolingPadFanOwnershipSnapshot(
                CoolingPadControlOwnershipState.Owned,
                target),
            coordinator.Snapshot);
        Assert.Equal(1, backend.HandbackCount);
    }

    [Fact]
    public async Task Handback_IndeterminateLatchesAndBlocksEveryLaterWrite()
    {
        var backend = new FakeBackend();
        var coordinator = new CoolingPadFanOwnershipCoordinator(backend);
        await coordinator.ApplyTargetAsync(
            new CoolingPadFanTarget(2_200),
            CancellationToken.None);
        backend.HandbackFailure = Indeterminate("unknown");

        await Assert.ThrowsAsync<BladeWriteException>(
            () => coordinator.HandbackAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.HandbackAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ApplyTargetAsync(
                new CoolingPadFanTarget(2_250),
                CancellationToken.None));

        Assert.Equal(CoolingPadControlOwnershipState.Indeterminate, coordinator.Snapshot.Ownership);
        Assert.Null(coordinator.Snapshot.AppliedTarget);
        Assert.Equal(1, backend.HandbackCount);
        Assert.Single(backend.AppliedTargets);
    }

    private static BladeWriteException KnownNotApplied(string message) => new(
        WriteSubmissionState.KnownNotApplied,
        message);

    private static BladeWriteException Indeterminate(string message) => new(
        WriteSubmissionState.Indeterminate,
        message);

    private sealed class FakeBackend : ICoolingPadFanSemanticBackend
    {
        internal List<CoolingPadFanTarget> AppliedTargets { get; } = [];

        internal int HandbackCount { get; private set; }

        internal int AcquireCount { get; private set; }

        internal int UpdateCount { get; private set; }

        internal Exception? ApplyFailure { get; set; }

        internal Exception? HandbackFailure { get; set; }

        public Task AcquireTargetAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            return ApplyAsync(target, cancellationToken);
        }

        public Task UpdateTargetAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
        {
            UpdateCount++;
            return ApplyAsync(target, cancellationToken);
        }

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
    }
}
