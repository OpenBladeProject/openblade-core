namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsFunctionRowOwnershipTrackerTests
{
    [Fact]
    public void FailedConstructionRemainsOwnedUntilCleanupIsConfirmed()
    {
        var tracker = new WindowsFunctionRowOwnershipTracker();
        Assert.True(tracker.HasNoOutstandingSources);
        WindowsFunctionRowOwnershipTracker.Registration source = tracker.Begin();
        Assert.False(tracker.HasNoOutstandingSources);
        Assert.False(source.IsActive);
        Assert.False(source.IsCleanupConfirmed);

        source.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.True(source.IsCleanupConfirmed);
        Assert.True(tracker.HasNoOutstandingSources);
        source.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.True(tracker.HasNoOutstandingSources);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void UncertainCleanupCannotBeForgottenByLaterSources(bool unhooked, bool held)
    {
        var tracker = new WindowsFunctionRowOwnershipTracker();
        WindowsFunctionRowOwnershipTracker.Registration failed = tracker.Begin();
        failed.ConfirmActive();
        Assert.True(failed.IsActive);
        failed.Complete(unhooked, held);
        Assert.False(failed.IsActive);
        Assert.False(failed.IsCleanupConfirmed);

        WindowsFunctionRowOwnershipTracker.Registration later = tracker.Begin();
        later.ConfirmActive();
        Assert.False(later.CanTransferOwnership(sourceIsActiveAndIdle: true));
        later.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.False(later.CanTransferOwnership(sourceIsActiveAndIdle: false));
        failed.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.False(tracker.HasNoOutstandingSources);
    }

    [Fact]
    public void OneCleanSourceDoesNotReleaseAnotherStartingSource()
    {
        var tracker = new WindowsFunctionRowOwnershipTracker();
        WindowsFunctionRowOwnershipTracker.Registration first = tracker.Begin();
        WindowsFunctionRowOwnershipTracker.Registration second = tracker.Begin();
        first.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.False(first.CanTransferOwnership(sourceIsActiveAndIdle: false));
        Assert.False(tracker.HasNoOutstandingSources);
        second.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.True(second.CanTransferOwnership(sourceIsActiveAndIdle: false));
        Assert.True(tracker.HasNoOutstandingSources);
    }

    [Fact]
    public void ActiveTransferRequiresAnIdleExclusiveSource()
    {
        var tracker = new WindowsFunctionRowOwnershipTracker();
        WindowsFunctionRowOwnershipTracker.Registration source = tracker.Begin();
        Assert.False(source.CanTransferOwnership(sourceIsActiveAndIdle: true));
        source.ConfirmActive();
        Assert.False(source.CanTransferOwnership(sourceIsActiveAndIdle: false));
        Assert.True(source.CanTransferOwnership(sourceIsActiveAndIdle: true));
        WindowsFunctionRowOwnershipTracker.Registration other = tracker.Begin();
        Assert.False(source.CanTransferOwnership(sourceIsActiveAndIdle: true));
        other.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.True(source.CanTransferOwnership(sourceIsActiveAndIdle: true));
        source.Complete(hookCleanupConfirmed: true, hasActiveKeys: false);
        Assert.True(source.CanTransferOwnership(sourceIsActiveAndIdle: false));
    }
}
