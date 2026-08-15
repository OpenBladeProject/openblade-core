namespace OpenBlade.Tray;

/// <summary>
/// Caches one single-line height for text blocks that share the same typography.
/// Use a separate instance when font, style, language, or other line-height inputs differ.
/// </summary>
public sealed class SingleLineTextMeasurementCache
{
    private const double HeightTolerance = 0.5;
    private double cachedTextScaleFactor = double.NaN;
    private double cachedHeight = double.NaN;

    /// <summary>
    /// Returns the cached height or measures it for a new text scale factor.
    /// Invalid inputs return <see cref="double.NaN"/> without changing the cache.
    /// </summary>
    public double GetOrMeasure(double textScaleFactor, Func<double> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (!double.IsFinite(textScaleFactor) || textScaleFactor <= 0)
        {
            return double.NaN;
        }

        if (textScaleFactor.Equals(cachedTextScaleFactor))
        {
            return cachedHeight;
        }

        double measuredHeight = measure();
        if (!double.IsFinite(measuredHeight) || measuredHeight <= 0)
        {
            return double.NaN;
        }

        cachedTextScaleFactor = textScaleFactor;
        cachedHeight = measuredHeight;
        return measuredHeight;
    }

    public static bool IsWrapped(double actualHeight, double singleLineHeight) =>
        double.IsFinite(actualHeight)
        && double.IsFinite(singleLineHeight)
        && actualHeight > singleLineHeight + HeightTolerance;
}
