namespace OpenBlade.Tray;

public readonly record struct TrayDashboardControlPlacement(
    TrayDashboardControlKind Kind,
    int Row,
    int Column,
    int ColumnSpan);

public sealed record TrayDashboardLayout(
    IReadOnlyList<TrayDashboardControlPlacement> Controls,
    int RowCount,
    TrayDashboardControlKind? ExpandedControl);

public static class TrayDashboardLayoutPolicy
{
    private const int BaseHeightDip = 82;
    private const int ControlRowHeightDip = 106;
    private const int ExpandedControlHeightDip = 84;
    private const int MinimumHeightDip = 188;
    private const int MaximumHeightDip = 640;

    public static TrayDashboardLayout Resolve(
        IEnumerable<TrayDashboardControlPreference> controls,
        TrayDashboardControlKind? expandedControl = null)
    {
        ArgumentNullException.ThrowIfNull(controls);

        TrayDashboardControlPreference[] visibleControls = controls
            .Where(control => control.IsVisible)
            .ToArray();
        TrayDashboardControlKind? effectiveExpandedControl = expandedControl is { } selected
            && visibleControls.Any(control => control.Kind == selected)
                ? selected
                : null;
        var placements = new List<TrayDashboardControlPlacement>();
        int row = 0;
        int column = 0;
        foreach (TrayDashboardControlPreference control in visibleControls.Where(control =>
                     control.Kind != effectiveExpandedControl))
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
        return new TrayDashboardLayout(
            placements,
            rowCount,
            effectiveExpandedControl);
    }

    public static int CalculatePreferredHeight(TrayDashboardLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int expandedHeight = layout.ExpandedControl is not null
            ? ExpandedControlHeightDip
            : 0;
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
