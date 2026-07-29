using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private static bool StatusMetadataEqual(
        ServiceStatusSnapshot left,
        ServiceStatusSnapshot right) =>
        left.DeviceVerified == right.DeviceVerified
        && left.WritesBlocked == right.WritesBlocked
        && left.PotentialControllers.SequenceEqual(
            right.PotentialControllers,
            StringComparer.Ordinal)
        && string.Equals(
            left.ControllerDetectionWarning,
            right.ControllerDetectionWarning,
            StringComparison.Ordinal)
        && left.ExternalChanges.SequenceEqual(right.ExternalChanges)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal)
        && CapabilitiesEqual(left.Capabilities, right.Capabilities)
        && left.ConfirmedState == right.ConfirmedState
        && left.Profiles == right.Profiles
        && left.DesiredState == right.DesiredState
        && left.ActivePowerSource == right.ActivePowerSource
        && left.PerformanceOverride == right.PerformanceOverride
        && left.FanOverride == right.FanOverride
        && left.FanControlOverride == right.FanControlOverride
        && left.LightingOverride == right.LightingOverride
        && left.FullChargeOverride == right.FullChargeOverride
        && left.LightingRendererOwnership == right.LightingRendererOwnership
        && left.StartupAnimationEnabled == right.StartupAnimationEnabled
        && left.FunctionKeyPrimary == right.FunctionKeyPrimary
        && left.GamingModeEnabled == right.GamingModeEnabled
        && left.PerformanceShortcutPreview == right.PerformanceShortcutPreview
        && left.KeyboardDriverModeActive == right.KeyboardDriverModeActive
        && left.AlwaysOverrideExternalLightingChanges
            == right.AlwaysOverrideExternalLightingChanges
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && left.BatteryHealth == right.BatteryHealth
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private static bool CapabilitiesEqual(
        DeviceCapabilities? left,
        DeviceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.PerformancePresets.SetEquals(right.PerformancePresets)
            && CustomPerformanceCapabilitiesEqual(
                left.CustomPerformance,
                right.CustomPerformance)
            && left.CpuPowerLimit == right.CpuPowerLimit
            && left.CpuVoltageOptimizer == right.CpuVoltageOptimizer
            && left.GpuOverclock == right.GpuOverclock
            && left.SpecialKeyBindingsAccess == right.SpecialKeyBindingsAccess
            && left.LampArrayLightingAccess == right.LampArrayLightingAccess
            && left.ObservedPowerSourceBehaviors.SequenceEqual(
                right.ObservedPowerSourceBehaviors)
            && left.FanModes.SetEquals(right.FanModes)
            && left.FanSpeedRange == right.FanSpeedRange
            && left.LightingEffects.SetEquals(right.LightingEffects)
            && left.ObservedLightingEffects.SetEquals(right.ObservedLightingEffects)
            && left.LogoLightingModes.SetEquals(right.LogoLightingModes)
            && left.OneShotLaptopControls.SetEquals(right.OneShotLaptopControls)
            && left.ChargeLimits.SetEquals(right.ChargeLimits)
            && left.SupportsKeyboardBrightness == right.SupportsKeyboardBrightness
            && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
            && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge);

    private static bool CustomPerformanceCapabilitiesEqual(
        CustomPerformanceCapabilities? left,
        CustomPerformanceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.AllowedCombinations.SetEquals(right.AllowedCombinations));
}
