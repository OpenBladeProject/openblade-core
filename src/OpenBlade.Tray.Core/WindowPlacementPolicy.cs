namespace OpenBlade.Tray;

/// <summary>
/// A pixel rectangle whose position uses the same coordinate space as its source work area.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class WindowPlacementPolicy
{
    public static PixelRect EnforceMinimumSize(
        PixelRect current,
        double minimumWidthDip,
        double minimumHeightDip,
        int dpi)
    {
        ValidateDip(minimumWidthDip, nameof(minimumWidthDip));
        ValidateDip(minimumHeightDip, nameof(minimumHeightDip));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(current.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(current.Height);

        return current with
        {
            Width = Math.Max(current.Width, ScaleToPixels(minimumWidthDip, dpi)),
            Height = Math.Max(current.Height, ScaleToPixels(minimumHeightDip, dpi)),
        };
    }

    public static PixelRect Center(
        double widthDip,
        double heightDip,
        int dpi,
        PixelRect workArea)
    {
        ValidateDip(widthDip, nameof(widthDip));
        ValidateDip(heightDip, nameof(heightDip));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workArea.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workArea.Height);

        int scaledWidth = ScaleToPixels(widthDip, dpi);
        int scaledHeight = ScaleToPixels(heightDip, dpi);
        int width = Math.Min(scaledWidth, workArea.Width);
        int height = Math.Min(scaledHeight, workArea.Height);

        return checked(new PixelRect(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));
    }

    private static int ScaleToPixels(double dips, int dpi)
    {
        double scaled = dips * dpi / 96d;
        if (!double.IsFinite(scaled))
        {
            throw new OverflowException("The scaled window dimension is not finite.");
        }

        int rounded = checked((int)Math.Round(scaled, MidpointRounding.AwayFromZero));
        return Math.Max(1, rounded);
    }

    private static void ValidateDip(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
