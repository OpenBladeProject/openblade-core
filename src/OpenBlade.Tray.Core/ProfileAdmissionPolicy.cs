using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Admits profile edits against current capabilities while preserving unchanged
/// settings. USB-C thermal restrictions still apply to unchanged profiles.
/// </summary>
internal static class ProfileAdmissionPolicy
{
    internal static bool Supports(
        ProfileSet draft,
        ProfileSet current,
        DeviceCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return false;
        }

        return ProfileSupportedOrUnchanged(
                draft.PluggedIn,
                current.PluggedIn,
                capabilities,
                PowerSource.PluggedIn)
            && ProfileSupportedOrUnchanged(
                draft.Battery,
                current.Battery,
                capabilities,
                PowerSource.Battery)
            && ProfileSupportedOrUnchanged(
                draft.UsbC,
                current.UsbC,
                capabilities,
                PowerSource.UsbC)
            && (draft.BatteryPolicy == current.BatteryPolicy
                || (HasBatteryCapability(capabilities)
                    && capabilities.ChargeLimits.Contains(
                        draft.BatteryPolicy.ChargeLimitPercent)));
    }

    private static bool ProfileSupportedOrUnchanged(
        PowerProfile draft,
        PowerProfile current,
        DeviceCapabilities capabilities,
        PowerSource powerSource) =>
        Enum.IsDefined(draft.Performance)
        && (capabilities.PerformancePresets.Contains(draft.Performance)
            || draft.Performance == current.Performance)
        && (PerformancePresetPresentation.IsAvailable(powerSource, draft.Performance)
            || (powerSource != PowerSource.UsbC
                && draft.Performance == current.Performance))
        && Enum.IsDefined(draft.Fans)
        && (capabilities.FanModes.Contains(draft.Fans)
            || draft.Fans == current.Fans)
        && (FanModeAvailable(powerSource, draft.Fans)
            || (powerSource != PowerSource.UsbC
                && draft.Fans == current.Fans))
        && (draft.Performance != PerformancePreset.Custom
            || CustomPerformanceSettings.IsWellFormed(draft.CustomPerformance))
        && (draft.CustomPerformance is null
            || CustomPerformanceSettings.IsWellFormed(draft.CustomPerformance))
        && (ThermalCombinationSupported(draft.Performance, draft.Fans, capabilities)
            || (draft.Fans == current.Fans
                && !capabilities.FanModes.Contains(draft.Fans)))
        && (LightingSupported(draft.Lighting, capabilities)
            || draft.Lighting == current.Lighting);

    private static bool ThermalCombinationSupported(
        PerformancePreset performance,
        FanMode fans,
        DeviceCapabilities capabilities) =>
        fans == FanMode.Automatic
        || ((capabilities.SupportsDirectManualFanTransitions
                || performance == PerformancePreset.Balanced)
            && fans is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum);

    internal static bool FanModeAvailable(PowerSource powerSource, FanMode fans) =>
        powerSource switch
        {
            PowerSource.PluggedIn => fans is FanMode.Automatic
                or FanMode.Fixed
                or FanMode.ManualCurve
                or FanMode.Maximum,
            PowerSource.Battery or PowerSource.UsbC => fans == FanMode.Automatic,
            PowerSource.ExternalUnknown => false,
            _ => false,
        };

    internal static bool LightingSupported(
        LightingSettings lighting,
        DeviceCapabilities capabilities)
    {
        if (!Enum.IsDefined(lighting.Effect)
            || !capabilities.LightingEffects.Contains(lighting.Effect)
            || !LightingSettingsValidation.IsWellFormed(lighting))
        {
            return false;
        }

        return true;
    }

    internal static bool HasBatteryCapability(DeviceCapabilities? capabilities) =>
        capabilities is not null
        && capabilities.ChargeLimits.Count is > 0 and <= 51
        && capabilities.ChargeLimits.Any(value => value < 100)
        && capabilities.ChargeLimits.All(
            value => value is >= 50 and <= 100);
}
