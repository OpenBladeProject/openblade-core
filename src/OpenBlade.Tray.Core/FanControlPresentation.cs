using System.ComponentModel;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record FanModeOption(FanMode Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class FanCurveEditorPoint : INotifyPropertyChanged
{
    private int speedRpm;

    public FanCurveEditorPoint(FanCurvePoint value)
    {
        TemperatureCelsius = value.TemperatureCelsius;
        speedRpm = value.SpeedRpm;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int TemperatureCelsius { get; }

    public string TemperatureLabel => $"{TemperatureCelsius}°C";

    public int SpeedRpm
    {
        get => speedRpm;
        set
        {
            int normalized = FanControlSettings.QuantizeSpeed(value);
            if (speedRpm == normalized)
            {
                return;
            }

            speedRpm = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedRpm)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedLabel)));
        }
    }

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

    private static readonly FanModeOption[] Options = Order
        .Select(value => new FanModeOption(value, Label(value)))
        .ToArray();

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
        return Options
            .Where(option => admitted.Contains(option.Value)
                && (!automaticOnly || option.Value == FanMode.Automatic))
            .ToArray();
    }
}
