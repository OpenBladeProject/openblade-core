using System.Collections.Concurrent;
using System.ComponentModel;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record FanModeOption(FanMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One fixed-temperature point in the shared fan-curve editor. Laptop and
/// cooling-pad curves use the same point and template with their own speed bounds.
/// </summary>
public sealed class FanCurveEditorPoint : INotifyPropertyChanged
{
    private int speedRpm;

    public FanCurveEditorPoint(FanCurvePoint value, FanSpeedRange? range = null)
    {
        FanSpeedRange speedRange = range is { IsWellFormed: true } ? range : FanSpeedRange.Standard;
        MinimumRpm = speedRange.MinimumRpm;
        MaximumRpm = speedRange.MaximumRpm;
        StepRpm = speedRange.StepRpm;
        TemperatureCelsius = value.TemperatureCelsius;
        speedRpm = value.SpeedRpm;
    }

    private FanCurveEditorPoint(int temperatureCelsius, int speed, int minimum, int maximum, int step)
    {
        MinimumRpm = minimum;
        MaximumRpm = maximum;
        StepRpm = step;
        TemperatureCelsius = temperatureCelsius;
        speedRpm = Quantize(speed);
    }

    /// <summary>A cooling-pad point; the pad accepts 500 to 3200 RPM in 50 RPM steps.</summary>
    public static FanCurveEditorPoint ForCoolingPad(CoolingPadCurvePoint value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value.TemperatureCelsius, value.Rpm,
            CoolingPadSettings.MinimumRpm, CoolingPadSettings.MaximumRpm, CoolingPadSettings.RpmStep);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int TemperatureCelsius { get; }

    public int MinimumRpm { get; }

    public int MaximumRpm { get; }

    public int StepRpm { get; }

    public string TemperatureLabel => TrayText.Format("{0}°C", TemperatureCelsius);

    public int SpeedRpm
    {
        get => speedRpm;
        set
        {
            int normalized = Quantize(value);
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

    public CoolingPadCurvePoint ToCoolingPadValue() => new(TemperatureCelsius, SpeedRpm);

    private int Quantize(int value) => Math.Clamp(
        (int)Math.Round(value / (double)StepRpm, MidpointRounding.AwayFromZero) * StepRpm,
        MinimumRpm,
        MaximumRpm);
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
        TrayLocalization.ResourceCulture.Name,
        static cultureName =>
        {
            using IDisposable temporary =
                TrayLocalization.BeginTemporaryLanguageOverride(cultureName);
            return Order
                .Select(value => new FanModeOption(value, Label(value)))
                .ToArray();
        });

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
