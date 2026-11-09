using OpenBlade.Contracts;

namespace OpenBlade.Testing;

// Frozen from 9e53ed92bbe3985e1ea899ed6c1124f6f6dd020e.
// Bodies: ProfileAutomationState.cs 446-470, 546-569; OverrideResolver.cs 7-34.
// Only static parameters replace instance fields. No native or production resolver calls.
internal static class ManagedAutomationPolicyBaseline
{
    // ProfileAutomationState.cs:490-508 at 8a6e49d7bcffc52c84e8d447711f9192385e9da6.
    internal static bool IsPerformanceCompatibleWithSource(
        PerformancePreset performance,
        PowerSource source) => source switch
        {
            PowerSource.PluggedIn => performance is
                PerformancePreset.Silent
                or PerformancePreset.Balanced
                or PerformancePreset.Performance
                or PerformancePreset.Turbo
                or PerformancePreset.MaximumPower
                or PerformancePreset.Gaming
                or PerformancePreset.Creator
                or PerformancePreset.Custom,
            PowerSource.Battery => performance is
                PerformancePreset.BatterySaver
                or PerformancePreset.BatteryBalanced,
            PowerSource.UsbC => performance is PerformancePreset.Balanced,
            _ => false,
        };

    internal static ResolvedSetting<LightingSettings> ResolveLighting(
        PowerProfile profile,
        ConfirmedDeviceState confirmed,
        bool fromApplication,
        PowerSource powerSource,
        ManualOverride<LightingSettings>? temporaryLighting,
        ManualOverride<LightingSettings>? persistentLighting)
    {
        if (powerSource == PowerSource.ExternalUnknown)
        {
            return confirmed.Lighting is { } observed
                ? new ResolvedSetting<LightingSettings>(
                    observed,
                    SettingValueSource.ConfirmedFirmwareState)
                : new ResolvedSetting<LightingSettings>(
                    profile.Lighting,
                    SettingValueSource.PowerProfile);
        }

        return Resolve(
            profile.Lighting,
            confirmed.Lighting ?? profile.Lighting,
            temporaryLighting,
            persistentLighting,
            fromApplication
                ? SettingValueSource.ApplicationProfile
                : SettingValueSource.PowerProfile);
    }

    internal static ResolvedSetting<PerformancePreset> ResolvePerformance(
        PerformancePreset profileValue,
        PerformanceOverride? temporaryOverride,
        PerformanceOverride? persistentOverride,
        SettingValueSource profileSource)
    {
        if (persistentOverride is not null)
        {
            return new ResolvedSetting<PerformancePreset>(
                persistentOverride.Value,
                SettingValueSource.PersistentOverride);
        }

        if (temporaryOverride is not null)
        {
            return new ResolvedSetting<PerformancePreset>(
                temporaryOverride.Value,
                SettingValueSource.TemporaryOverride);
        }

        return new ResolvedSetting<PerformancePreset>(
            profileValue,
            profileSource);
    }

    private static ResolvedSetting<T> Resolve<T>(
        T? profileValue,
        T confirmedValue,
        ManualOverride<T>? temporaryOverride,
        ManualOverride<T>? persistentOverride,
        SettingValueSource profileSource = SettingValueSource.PowerProfile)
    {
        if (persistentOverride is not null)
        {
            return new ResolvedSetting<T>(
                persistentOverride.Value,
                SettingValueSource.PersistentOverride);
        }

        if (temporaryOverride is not null)
        {
            return new ResolvedSetting<T>(
                temporaryOverride.Value,
                SettingValueSource.TemporaryOverride);
        }

        if (profileValue is not null)
        {
            return new ResolvedSetting<T>(profileValue, profileSource);
        }

        return new ResolvedSetting<T>(confirmedValue, SettingValueSource.ConfirmedFirmwareState);
    }
}
