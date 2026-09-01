using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GpuAppCleanupVisibilityTrackerTests
{
    [Fact]
    public void ClosableAppShowsImmediately()
    {
        var tracker = new GpuAppCleanupVisibilityTracker();

        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: true));
    }

    [Fact]
    public void ThreeConsecutiveSuccessfulEmptyScansHideVisibleAction()
    {
        var tracker = new GpuAppCleanupVisibilityTracker();
        tracker.ObserveSuccessfulScan(hasClosableApps: true);

        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: false));
        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: false));
        Assert.False(tracker.ObserveSuccessfulScan(hasClosableApps: false));
    }

    [Fact]
    public void SuccessfulPresenceBreaksAnEmptySequence()
    {
        var tracker = new GpuAppCleanupVisibilityTracker();
        tracker.ObserveSuccessfulScan(hasClosableApps: true);
        tracker.ObserveSuccessfulScan(hasClosableApps: false);
        tracker.ObserveSuccessfulScan(hasClosableApps: false);

        tracker.ObserveSuccessfulScan(hasClosableApps: true);

        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: false));
        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: false));
    }

    [Fact]
    public void ThreeConsecutiveFailuresExpireRetainedVisibility()
    {
        var tracker = new GpuAppCleanupVisibilityTracker();
        tracker.ObserveSuccessfulScan(hasClosableApps: true);

        Assert.True(tracker.ObserveFailedScan());
        Assert.True(tracker.ObserveFailedScan());
        Assert.False(tracker.ObserveFailedScan());
    }

    [Fact]
    public void EmptyAndFailedScansShareOneBoundedStaleBudget()
    {
        var tracker = new GpuAppCleanupVisibilityTracker();
        tracker.ObserveSuccessfulScan(hasClosableApps: true);

        Assert.True(tracker.ObserveSuccessfulScan(hasClosableApps: false));
        Assert.True(tracker.ObserveFailedScan());
        Assert.False(tracker.ObserveSuccessfulScan(hasClosableApps: false));
    }
}
