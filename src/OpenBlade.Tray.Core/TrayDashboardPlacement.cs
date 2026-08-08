namespace OpenBlade.Tray;

public readonly record struct TrayDashboardPoint(int X, int Y);

public readonly record struct TrayDashboardSize(int Width, int Height);

public readonly record struct TrayDashboardRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);
}

public readonly record struct TrayDashboardRevealGeometry(int StagingY);

public static class TrayDashboardPlacement
{
    /// <summary>
    /// Resolves a physical-pixel popup rectangle within a physical-pixel work area.
    /// The requested size and margin are device-independent pixels and are scaled
    /// using the target monitor's DPI.
    /// </summary>
    public static TrayDashboardRect Resolve(
        TrayDashboardPoint anchor,
        TrayDashboardRect workArea,
        TrayDashboardSize requestedSizeDip,
        int dpi,
        int marginDip = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(marginDip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (requestedSizeDip.Width <= 0 || requestedSizeDip.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSizeDip));
        }

        int margin = WindowPlacementPolicy.ScaleToPixels(marginDip, dpi);
        int horizontalMargin = Math.Min(margin, Math.Max(0, (workArea.Width - 1) / 2));
        int verticalMargin = Math.Min(margin, Math.Max(0, (workArea.Height - 1) / 2));
        int availableWidth = checked(workArea.Width - (horizontalMargin * 2));
        int availableHeight = checked(workArea.Height - (verticalMargin * 2));
        int requestedWidth = WindowPlacementPolicy.ScaleToPixels(requestedSizeDip.Width, dpi);
        int requestedHeight = WindowPlacementPolicy.ScaleToPixels(requestedSizeDip.Height, dpi);
        int width = Math.Min(requestedWidth, availableWidth);
        int height = Math.Min(requestedHeight, availableHeight);
        int minimumX = checked(workArea.X + horizontalMargin);
        int maximumX = Math.Max(
            minimumX,
            checked(workArea.Right - horizontalMargin - width));
        int desiredX = checked(anchor.X - (width / 2));
        int x = Math.Clamp(desiredX, minimumX, maximumX);

        int y = UsesBottomEdge(anchor, workArea)
            ? checked(workArea.Bottom - verticalMargin - height)
            : checked(workArea.Y + verticalMargin);

        return new TrayDashboardRect(x, y, width, height);
    }

    /// <summary>
    /// Resolves a physical-pixel off-work-area staging coordinate from the same
    /// top-or-bottom edge decision used by <see cref="Resolve"/>.
    /// </summary>
    public static TrayDashboardRevealGeometry ResolveRevealGeometry(
        TrayDashboardPoint anchor,
        TrayDashboardRect workArea,
        TrayDashboardRect placement)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (placement.Width <= 0 || placement.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        if (UsesBottomEdge(anchor, workArea))
        {
            return new TrayDashboardRevealGeometry(workArea.Bottom);
        }

        return new TrayDashboardRevealGeometry(
            checked(workArea.Y - placement.Height));
    }

    private static bool UsesBottomEdge(
        TrayDashboardPoint anchor,
        TrayDashboardRect workArea)
    {
        int workAreaMiddle = checked(workArea.Y + (workArea.Height / 2));
        return anchor.Y >= workAreaMiddle;
    }
}

public enum TrayIconPointerButton
{
    Other,
    Left,
    Right,
    Middle,
}

public enum TrayIconActivation
{
    None,
    ShowDashboard,
    OpenMainWindow,
}

public static class TrayIconActivationPolicy
{
    public static TrayIconActivation Resolve(TrayIconPointerButton button) =>
        button == TrayIconPointerButton.Left
            ? TrayIconActivation.ShowDashboard
            : TrayIconActivation.None;

    public static TrayIconActivation ResolveDoubleClick(TrayIconPointerButton button) =>
        button == TrayIconPointerButton.Left
            ? TrayIconActivation.OpenMainWindow
            : TrayIconActivation.None;

    public static bool ShouldSuppressTrailingClick(
        TimeSpan elapsedSinceDoubleClick,
        TimeSpan systemDoubleClickTime) =>
        systemDoubleClickTime > TimeSpan.Zero
        && elapsedSinceDoubleClick >= TimeSpan.Zero
        && elapsedSinceDoubleClick <= systemDoubleClickTime;
}
