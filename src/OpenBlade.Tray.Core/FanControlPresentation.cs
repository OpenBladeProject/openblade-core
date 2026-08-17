using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
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

    public string TemperatureLabel => TrayText.Format("{0}°C", TemperatureCelsius);

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

    public string SpeedLabel => TrayText.Format("{0:N0} RPM", SpeedRpm);

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

    private static readonly ConcurrentDictionary<string, FanModeOption[]> OptionsByCulture =
        new(StringComparer.OrdinalIgnoreCase);

    private static FanModeOption[] Options => OptionsByCulture.GetOrAdd(
        CultureInfo.CurrentUICulture.Name,
        _ => Order
            .Select(value => new FanModeOption(value, Label(value)))
            .ToArray());

    public static string Label(FanMode value) => value switch
    {
        FanMode.Automatic => TrayText.Get("Automatic"),
        FanMode.Fixed => TrayText.Get("Fixed speed"),
        FanMode.ManualCurve => TrayText.Get("Manual fan curve"),
        FanMode.Maximum => TrayText.Get("Maximum"),
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
