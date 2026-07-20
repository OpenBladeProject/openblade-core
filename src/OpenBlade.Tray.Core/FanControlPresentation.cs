using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record FanModeOption(FanMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class FanCurveEditorPoint
{
    public FanCurveEditorPoint(FanCurvePoint value)
    {
        TemperatureCelsius = value.TemperatureCelsius;
        SpeedRpm = value.SpeedRpm;
    }

    public int TemperatureCelsius { get; }

    public string TemperatureLabel => $"{TemperatureCelsius}°C";

    public int SpeedRpm { get; set; }

    public string SpeedLabel => $"{SpeedRpm:N0} RPM";

    public FanCurvePoint ToValue() => new(TemperatureCelsius, SpeedRpm);
}

public static class FanModePresentation
{
    private static readonly FanMode[] Order =
    [
        FanMode.Automatic,
        FanMode.Fixed,
        FanMode.ManualCurve,
    ];

    public static string Label(FanMode value) => value switch
    {
        FanMode.Automatic => "Automatic",
        FanMode.Fixed => "Fixed speed",
        FanMode.ManualCurve => "Manual fan curve",
        FanMode.Maximum => "Maximum",
        _ => value.ToString(),
    };

    public static IReadOnlyList<FanModeOption> CreateOptions(
        IReadOnlySet<FanMode> admitted,
        bool automaticOnly = false)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        return Order
            .Where(value => admitted.Contains(value)
                && (!automaticOnly || value == FanMode.Automatic))
            .Select(value => new FanModeOption(value, Label(value)))
            .ToArray();
    }
}
