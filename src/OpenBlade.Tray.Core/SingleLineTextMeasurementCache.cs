namespace OpenBlade.Tray;

public sealed class SingleLineTextMeasurementCache
{
    private const double HeightTolerance = 0.5;
    private double cachedTextScaleFactor = double.NaN;
    private double cachedHeight = double.NaN;

    public double GetOrMeasure(double textScaleFactor, Func<double> measure)
    {
        if (!double.IsFinite(textScaleFactor) || textScaleFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textScaleFactor),
                textScaleFactor,
                "The text scale factor must be finite and positive.");
        }

        ArgumentNullException.ThrowIfNull(measure);
        if (textScaleFactor.Equals(cachedTextScaleFactor))
        {
            return cachedHeight;
        }

        double measuredHeight = measure();
        if (!double.IsFinite(measuredHeight) || measuredHeight <= 0)
        {
            throw new InvalidOperationException(
                "The measured single-line text height must be finite and positive.");
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
