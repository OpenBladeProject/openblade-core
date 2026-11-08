using OpenBlade.Contracts;

namespace OpenBlade.Testing;

// Frozen from predecessor 69f66ba673c0523c0521b09b286a7d09f8137488.
// Test-only independent oracle: never delegate to product/native policy.
internal static class ManagedProfilePolicyBaseline
{
    internal static PowerProfile Apply(
        PowerProfile baseline,
        ApplicationPowerProfileOverride? value)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (value is null)
        {
            return baseline;
        }

        FanMode fans = value.Fans ?? value.FanControl?.Mode ?? baseline.Fans;
        DisplayProfileSettings baselineDisplay = baseline.Display
            ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings display = ApplyDisplay(baselineDisplay, value.Display);
        LaptopBehaviorProfileSettings behavior = baseline.EffectiveLaptopBehavior;
        if (value.GamingModeEnabled.HasValue || value.GamingModeKeyPolicy is not null)
        {
            behavior = behavior with
            {
                GamingModeEnabled = value.GamingModeEnabled ?? behavior.GamingModeEnabled,
                GamingModeKeyPolicy = value.GamingModeKeyPolicy ?? behavior.GamingModeKeyPolicy,
            };
        }

        return baseline with
        {
            Performance = value.Performance ?? baseline.Performance,
            CustomPerformance = value.CustomPerformance ?? baseline.CustomPerformance,
            GpuClockTuning = value.GpuClockTuning ?? baseline.GpuClockTuning,
            Fans = fans,
            FanControl = value.FanControl
                ?? (value.Fans.HasValue
                    ? FanControlSettings.FromLegacy(fans)
                    : baseline.FanControl),
            Lighting = value.Lighting ?? baseline.Lighting,
            Display = display,
            LaptopBehavior = behavior,
        };
    }

    internal static DisplayProfileSettings ApplyDisplay(
        DisplayProfileSettings baseline,
        DisplayProfileSettings? value) => value is null
            ? baseline
            : new DisplayProfileSettings(
                value.RefreshRateKey ?? baseline.RefreshRateKey,
                value.HdrEnabled ?? baseline.HdrEnabled,
                value.ColorProfileKey ?? baseline.ColorProfileKey)
            {
                BrightnessPercent = value.BrightnessPercent ?? baseline.BrightnessPercent,
            };
}
