using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GpuAppCleanupVisibilityTrackerTests
{
    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 30)]
    public void ObservationIntervalReflectsCleanupCardVisibility(
        bool cleanupCardVisible,
        int expectedSeconds) => Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            GpuAppObservationPolicy.GetInterval(cleanupCardVisible));

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, false, false)]
    public void ObservationRequiresLaptopOrPinnedPowerModeDashboard(
        bool windowVisible,
        bool laptopPageVisible,
        bool homePageVisible,
        bool powerModeCardPinned,
        bool expected) => Assert.Equal(
            expected,
            GpuAppObservationPolicy.ShouldObserve(
                windowVisible,
                laptopPageVisible,
                homePageVisible,
                powerModeCardPinned));

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
