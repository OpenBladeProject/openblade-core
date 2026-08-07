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
        TrayDashboardSize requestedSize,
        int margin = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (requestedSize.Width <= 0 || requestedSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSize));
        }

        int availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        int availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        int width = Math.Min(requestedSize.Width, availableWidth);
        int height = Math.Min(requestedSize.Height, availableHeight);
        int minimumX = checked(workArea.X + margin);
        int maximumX = checked(workArea.Right - margin - width);
        int desiredX = checked(anchor.X - (width / 2));
        int x = Math.Clamp(desiredX, minimumX, maximumX);

        int workAreaMiddle = checked(workArea.Y + (workArea.Height / 2));
        int y = anchor.Y >= workAreaMiddle
            ? checked(workArea.Bottom - margin - height)
            : checked(workArea.Y + margin);

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
