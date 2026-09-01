namespace OpenBlade.Tray;

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
