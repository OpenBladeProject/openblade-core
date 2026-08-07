namespace OpenBlade.Tray;

public readonly record struct TrayDashboardControlPlacement(
    TrayDashboardControlKind Kind,
    int Row,
    int Column,
    int ColumnSpan);

public sealed record TrayDashboardLayout(
    IReadOnlyList<TrayDashboardControlPlacement> Controls,
    int RowCount);

public static class TrayDashboardLayoutPolicy
{
    private const int BaseHeightDip = 118;
    private const int ControlRowHeightDip = 122;
    private const int ExpandedControlHeightDip = 118;
    private const int MinimumHeightDip = 230;
    private const int MaximumHeightDip = 720;

    public static TrayDashboardLayout Resolve(
        IEnumerable<TrayDashboardControlPreference> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);

        var placements = new List<TrayDashboardControlPlacement>();
        int row = 0;
        int column = 0;
        foreach (TrayDashboardControlPreference control in controls.Where(control =>
                     control.IsVisible))
        {
            bool wide = control.Size == TrayDashboardControlSize.Wide;
            if (wide && column != 0)
            {
                row++;
                column = 0;
            }

            placements.Add(new TrayDashboardControlPlacement(
                control.Kind,
                row,
                column,
                wide ? 2 : 1));

            if (wide || column == 1)
            {
                row++;
                column = 0;
            }
            else
            {
                column = 1;
            }
        }

        int rowCount = checked(row + (column == 0 ? 0 : 1));
        return new TrayDashboardLayout(placements, rowCount);
    }

    public static int CalculatePreferredHeight(
        TrayDashboardLayout layout,
        bool hasExpandedControl)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int expandedHeight = hasExpandedControl ? ExpandedControlHeightDip : 0;
        return Math.Clamp(
            checked(BaseHeightDip + (layout.RowCount * ControlRowHeightDip) + expandedHeight),
            MinimumHeightDip,
            MaximumHeightDip);
    }
}

public static class TrayDashboardInteractionPolicy
{
    public static readonly TimeSpan DeactivationReopenSuppression =
        TimeSpan.FromMilliseconds(250);

    public static bool ShouldSuppressReopen(TimeSpan elapsedSinceDeactivation) =>
        elapsedSinceDeactivation >= TimeSpan.Zero
        && elapsedSinceDeactivation <= DeactivationReopenSuppression;
}
