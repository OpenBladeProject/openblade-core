namespace OpenBlade.Tray;

public readonly record struct TrayDashboardControlPlacement(
    TrayDashboardControlKind Kind,
    int Row,
    int Column,
    int ColumnSpan);

public sealed record TrayDashboardLayout(
    IReadOnlyList<TrayDashboardControlPlacement> Controls,
    int RowCount,
    int VisibleRowCount,
    bool HasOverflow);

public static class TrayDashboardLayoutPolicy
{
    private const int PreferredHeightDip = 388;
    private const int ColumnCount = 3;
    private const int MaximumVisibleRows = 2;

    public static TrayDashboardLayout Resolve(
        IEnumerable<TrayDashboardControlPreference> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);

        TrayDashboardControlPreference[] visibleControls = controls
            .Where(control => control.IsVisible)
            .ToArray();
        var placements = new List<TrayDashboardControlPlacement>();
        for (int index = 0; index < visibleControls.Length; index++)
        {
            placements.Add(new TrayDashboardControlPlacement(
                visibleControls[index].Kind,
                index / ColumnCount,
                index % ColumnCount,
                1));
        }

        int rowCount = checked((visibleControls.Length + ColumnCount - 1) / ColumnCount);
        int visibleRowCount = Math.Min(rowCount, MaximumVisibleRows);
        return new TrayDashboardLayout(
            placements,
            rowCount,
            visibleRowCount,
            rowCount > MaximumVisibleRows);
    }

    public static int CalculatePreferredHeight() => PreferredHeightDip;
}

public static class TrayDashboardInteractionPolicy
{
    public static readonly TimeSpan DeactivationReopenSuppression =
        TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan ForegroundActivationRetryWindow =
        TimeSpan.FromMilliseconds(250);

    public static bool ShouldSuppressReopen(TimeSpan elapsedSinceDeactivation) =>
        elapsedSinceDeactivation >= TimeSpan.Zero
        && elapsedSinceDeactivation <= DeactivationReopenSuppression;

    public static bool ShouldRetryForegroundActivation(TimeSpan elapsed) =>
        elapsed >= TimeSpan.Zero
        && elapsed <= ForegroundActivationRetryWindow;

    public static bool IsShellTrayWindowClass(string? className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    public static int ResolveNextIndex(int optionCount, int currentIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(optionCount);
        if (optionCount == 0)
        {
            return -1;
        }

        return currentIndex >= 0 && currentIndex < optionCount
            ? (currentIndex + 1) % optionCount
            : 0;
    }
}
