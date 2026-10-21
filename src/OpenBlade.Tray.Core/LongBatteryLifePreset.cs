using System.Globalization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record LongBatteryLifePresetPlan(
    PowerProfile Original,
    PowerProfile Candidate)
{
    public bool HasChanges => Candidate != Original;
}

public static class LongBatteryLifePreset
{
    public const byte TargetBrightnessPercent = 35;

    private static readonly PerformancePreset[] PerformancePreference =
    [
        PerformancePreset.BatterySaver,
        PerformancePreset.BatteryBalanced,
    ];

    public static LongBatteryLifePresetPlan Preview(
        PowerProfile batteryProfile,
        DeviceCapabilities capabilities,
        DisplaySettingsSnapshot? display)
    {
        ArgumentNullException.ThrowIfNull(batteryProfile);
        ArgumentNullException.ThrowIfNull(capabilities);

        PerformancePreset performance = PerformancePreference.FirstOrDefault(
            capabilities.PerformancePresets.Contains,
            batteryProfile.Performance);
        FanMode fans = capabilities.FanModes.Contains(FanMode.Automatic)
            ? FanMode.Automatic
            : batteryProfile.Fans;
        LightingSettings lighting = capabilities.LightingEffects.Contains(LightingEffect.Off)
            ? LightingSettingsFactory.Off()
            : batteryProfile.Lighting;
        DisplayProfileSettings? displaySettings = PreviewDisplay(batteryProfile.Display, display);

        PowerProfile candidate = batteryProfile with
        {
            Performance = performance,
            Fans = fans,
            Lighting = lighting,
            Display = displaySettings,
        };
        if (fans == FanMode.Automatic && batteryProfile.FanControl is not null)
        {
            candidate = candidate with
            {
                FanControl = new FanControlSettings(
                    FanMode.Automatic,
                    batteryProfile.FanControl.FixedSpeedRpm,
                    batteryProfile.FanControl.ManualCurve),
            };
        }

        return new LongBatteryLifePresetPlan(batteryProfile, candidate);
    }

    private static DisplayProfileSettings? PreviewDisplay(
        DisplayProfileSettings? configured,
        DisplaySettingsSnapshot? display)
    {
        if (display is not { IsAvailable: true })
        {
            return configured;
        }

        string? refreshKey = display.RefreshRateOptions
            .Where(option => !option.IsDynamic)
            .Select(option => (Option: option, Hertz: ParseFixedHertz(option.Key)))
            .Where(value => value.Hertz.HasValue)
            .OrderBy(value => value.Hertz)
            .ThenBy(value => value.Option.Key, StringComparer.Ordinal)
            .Select(value => value.Option.Key)
            .FirstOrDefault();
        byte? brightness = display.SupportedBrightnessLevels
            .Where(level => level is > 0 and <= 100)
            .Distinct()
            .OrderBy(level => Math.Abs(level - TargetBrightnessPercent))
            .ThenBy(level => level)
            .Select(level => (byte?)level)
            .FirstOrDefault();

        if (refreshKey is null && brightness is null)
        {
            return configured;
        }

        DisplayProfileSettings basis = configured ?? DisplayProfileSettings.Unconfigured();
        return basis with
        {
            RefreshRateKey = refreshKey ?? basis.RefreshRateKey,
            BrightnessPercent = brightness ?? basis.BrightnessPercent,
        };
    }

    private static int? ParseFixedHertz(string key)
    {
        const string prefix = "fixed-";
        return key.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                key.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int hertz)
            && hertz > 0
                ? hertz
                : null;
    }
}
