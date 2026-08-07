namespace OpenBlade.Tray;

public readonly record struct TrayDashboardPoint(int X, int Y);

public readonly record struct TrayDashboardSize(int Width, int Height);

public readonly record struct TrayDashboardRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);
}

public static class TrayDashboardPlacement
{
    public static TrayDashboardRect Resolve(
        TrayDashboardPoint anchor,
        TrayDashboardRect workArea,
        TrayDashboardSize requestedSizeDip,
        int dpi,
        int marginDip = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marginDip);
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

        int workAreaMiddle = checked(workArea.Y + (workArea.Height / 2));
        int y = anchor.Y >= workAreaMiddle
            ? checked(workArea.Bottom - verticalMargin - height)
            : checked(workArea.Y + verticalMargin);

        return new TrayDashboardRect(x, y, width, height);
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
}

public static class TrayIconActivationPolicy
{
    public static TrayIconActivation Resolve(TrayIconPointerButton button) =>
        button == TrayIconPointerButton.Left
            ? TrayIconActivation.ShowDashboard
            : TrayIconActivation.None;
}
