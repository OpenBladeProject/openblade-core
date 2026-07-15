namespace OpenBlade.Tray;

public static class IntegerEditorValue
{
    public static double ToDouble(int value) => value;

    public static int ToInt32(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Editor values must be finite.");
        }

        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }
}
