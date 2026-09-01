namespace OpenBlade.Tray;

public static class GpuAppObservationPolicy
{
    public static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(30);

    public static TimeSpan GetInterval(bool cleanupCardVisible) =>
        cleanupCardVisible ? VisibleInterval : HiddenInterval;

    public static bool ShouldObserve(
        bool windowVisible,
        bool laptopPageVisible,
        bool homePageVisible,
        bool powerModeCardPinned) => windowVisible
        && (laptopPageVisible || (homePageVisible && powerModeCardPinned));
}

public sealed class GpuAppCleanupVisibilityTracker
{
    private const int UnconfirmedScansBeforeHide = 3;

    private int unconfirmedScans;

    public bool IsVisible { get; private set; }

    public bool ObserveSuccessfulScan(bool hasClosableApps)
    {
        if (hasClosableApps)
        {
            unconfirmedScans = 0;
            IsVisible = true;
            return IsVisible;
        }

        return ObserveUnconfirmedScan();
    }

    public bool ObserveFailedScan() => ObserveUnconfirmedScan();

    private bool ObserveUnconfirmedScan()
    {
        unconfirmedScans++;
        if (unconfirmedScans >= UnconfirmedScansBeforeHide)
        {
            IsVisible = false;
        }

        return IsVisible;
    }
}
