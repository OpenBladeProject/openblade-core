using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private static bool StatusMetadataEqual(
        ServiceStatusSnapshot left,
        ServiceStatusSnapshot right) =>
        left.DeviceVerified == right.DeviceVerified
        && left.UnsupportedLaptop == right.UnsupportedLaptop
        && left.ControlAccess == right.ControlAccess
        && left.ObservationReason == right.ObservationReason
        && left.WritesBlocked == right.WritesBlocked
        && left.HyperBoostRestorePending == right.HyperBoostRestorePending
        && CpuTuningEqual(left.CpuTuning, right.CpuTuning)
        && CoolingPadEqual(left.CoolingPad, right.CoolingPad)
        && ThermalConvergenceEqual(left.ThermalConvergence, right.ThermalConvergence)
        && left.PotentialControllers.SequenceEqual(
            right.PotentialControllers,
            StringComparer.Ordinal)
        && ((left.HyperBoostBlockingControllers is null
                && right.HyperBoostBlockingControllers is null)
            || (left.HyperBoostBlockingControllers is not null
                && right.HyperBoostBlockingControllers is not null
                && left.HyperBoostBlockingControllers.SequenceEqual(
                    right.HyperBoostBlockingControllers,
                    StringComparer.Ordinal)))
        && string.Equals(
            left.ControllerDetectionWarning,
            right.ControllerDetectionWarning,
            StringComparison.Ordinal)
        && left.ExternalChanges.SequenceEqual(right.ExternalChanges)
        && left.UnconfirmedWrites.SequenceEqual(right.UnconfirmedWrites)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal)
        && CapabilitiesEqual(left.Capabilities, right.Capabilities)
        && left.ConfirmedState == right.ConfirmedState
        && left.Profiles == right.Profiles
        && left.DesiredState == right.DesiredState
        && left.ActivePowerSource == right.ActivePowerSource
        && left.ActiveApplicationProfileId == right.ActiveApplicationProfileId
        && left.EffectiveLaptopBehavior == right.EffectiveLaptopBehavior
        && left.PerformanceOverride == right.PerformanceOverride
        && left.FanOverride == right.FanOverride
        && left.FanControlOverride == right.FanControlOverride
        && left.BatterySaverFanStopStatus == right.BatterySaverFanStopStatus
        && left.FanStopStatus == right.FanStopStatus
        && left.LightingOverride == right.LightingOverride
        && left.BatteryPolicyOverride == right.BatteryPolicyOverride
        && left.BatteryProtectionOverride == right.BatteryProtectionOverride
        && left.PadlessHyperBoostEligibility == right.PadlessHyperBoostEligibility
        && left.FullChargeOverride == right.FullChargeOverride
        && left.LightingRendererOwnership == right.LightingRendererOwnership
        && left.StartupAnimationEnabled == right.StartupAnimationEnabled
        && left.FunctionKeyPrimary == right.FunctionKeyPrimary
        && left.GamingModeEnabled == right.GamingModeEnabled
        && left.PerformanceShortcutPreview == right.PerformanceShortcutPreview
        && left.KeyboardDriverModeActive == right.KeyboardDriverModeActive
        && left.AlwaysOverrideExternalLightingChanges
            == right.AlwaysOverrideExternalLightingChanges
        && left.LightingRefreshRate == right.LightingRefreshRate
        && left.AllowUntestedFirmware == right.AllowUntestedFirmware
        && left.IsExperimentalDevice == right.IsExperimentalDevice
        && left.ExperimentalWritesPaused == right.ExperimentalWritesPaused
        && string.Equals(left.ExperimentalSupportSource, right.ExperimentalSupportSource, StringComparison.Ordinal)
        && left.FirmwareCompatibility == right.FirmwareCompatibility
        && left.DisplayBrightnessHidAvailability
            == right.DisplayBrightnessHidAvailability
        && left.KeyboardLightingControlMode == right.KeyboardLightingControlMode
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && left.BatteryHealth == right.BatteryHealth
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private static bool CoolingPadEqual(CoolingPadStatusSnapshot left, CoolingPadStatusSnapshot right) =>
        ReferenceEquals(left, right)
        // IPC recreates curve lists. Compare the remaining record fields normally
        // and the two ordered point sequences by value.
        || ((left with { Requested = right.Requested }) == right
            && (left.Requested with { CpuCurve = right.Requested.CpuCurve, GpuCurve = right.Requested.GpuCurve }) == right.Requested
            && left.Requested.CpuCurve.SequenceEqual(right.Requested.CpuCurve)
            && left.Requested.GpuCurve.SequenceEqual(right.Requested.GpuCurve));

    private static bool CpuTuningEqual(CpuTuningStatus? left, CpuTuningStatus? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.Firmware == right.Firmware && left.Live == right.Live
            && left.PendingBoot == right.PendingBoot && left.RestorePending == right.RestorePending
            && left.FirmwareRecovery == right.FirmwareRecovery && left.LiveRecovery == right.LiveRecovery
            && SavedCpuEqual(left.SavedConfiguration, right.SavedConfiguration)
            && LiveCpuEqual(left.LiveState, right.LiveState));

    private static bool SavedCpuEqual(CpuSavedConfiguration? left, CpuSavedConfiguration? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null
            && left.PowerLimitMilliwatts == right.PowerLimitMilliwatts
            && left.Policy == right.Policy && left.PowerMode == right.PowerMode
            && (ReferenceEquals(left.CurveOptimizer, right.CurveOptimizer)
                || (left.CurveOptimizer is { } l && right.CurveOptimizer is { } r
                    && l.Mode == r.Mode && l.AllCoreValue == r.AllCoreValue
                    && l.CoreValues.SequenceEqual(r.CoreValues))));

    private static bool LiveCpuEqual(CpuLiveState? left, CpuLiveState? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null
            && left.PowerLimits == right.PowerLimits && left.CoreValues.SequenceEqual(right.CoreValues));

    private static bool ThermalConvergenceEqual(
        ThermalConvergenceStatus? left,
        ThermalConvergenceStatus? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.Policy == right.Policy
            && left.Disclosure == right.Disclosure
            && left.State == right.State
            && left.Reason == right.Reason
            && left.ExplicitReapplyRequired == right.ExplicitReapplyRequired
            && left.AffectedComponents.SequenceEqual(right.AffectedComponents));

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
            && left.HyperBoostAccess == right.HyperBoostAccess
            && left.HyperBoostWithoutCoolingPadAccess
                == right.HyperBoostWithoutCoolingPadAccess
            && left.HyperBoostBaseModes == right.HyperBoostBaseModes
            && left.SpecialKeyBindingsAccess == right.SpecialKeyBindingsAccess
            && left.WritableSpecialKeySlots == right.WritableSpecialKeySlots
            && left.LampArrayLightingAccess == right.LampArrayLightingAccess
            && left.ThxAudio == right.ThxAudio
            && left.ObservedPowerSourceBehaviors.SequenceEqual(
                right.ObservedPowerSourceBehaviors)
            && left.FanModes.SetEquals(right.FanModes)
            && left.FanSpeedRange == right.FanSpeedRange
            && left.FanSpeedRangesByPerformancePreset.Count
                == right.FanSpeedRangesByPerformancePreset.Count
            && left.FanSpeedRangesByPerformancePreset.All(entry =>
                right.FanSpeedRangesByPerformancePreset.TryGetValue(
                    entry.Key,
                    out FanSpeedRange? rightRange)
                && entry.Value == rightRange)
            && left.SupportsBatterySaverFanStop == right.SupportsBatterySaverFanStop
            && left.HasFanStopTrialEvidence == right.HasFanStopTrialEvidence
            && left.HasFanStopSourceEvidence == right.HasFanStopSourceEvidence
            && left.SupportsFanStop == right.SupportsFanStop
            && left.SupportsDirectManualFanTransitions
                == right.SupportsDirectManualFanTransitions
            && left.ReportsPerformanceWithManualFans == right.ReportsPerformanceWithManualFans
            && left.SupportsManualFansOnLimitedPower == right.SupportsManualFansOnLimitedPower
            && left.LightingEffects.SetEquals(right.LightingEffects)
            && left.ObservedLightingEffects.SetEquals(right.ObservedLightingEffects)
            && left.SupportedLightingRefreshRates.SetEquals(
                right.SupportedLightingRefreshRates)
            && left.LogoLightingModes.SetEquals(right.LogoLightingModes)
            && left.OneShotLaptopControls.SetEquals(right.OneShotLaptopControls)
            && left.HostManagedLaptopControls.SetEquals(right.HostManagedLaptopControls)
            && left.ChargeLimits.SetEquals(right.ChargeLimits)
            && left.SupportsKeyboardBrightness == right.SupportsKeyboardBrightness
            && left.SupportsIndependentLightingControls == right.SupportsIndependentLightingControls
            && left.SupportsBreathingColorSelection == right.SupportsBreathingColorSelection
            && (left.SupportedBreathingColorModes is null
                ? right.SupportedBreathingColorModes is null
                : right.SupportedBreathingColorModes is not null
                    && left.SupportedBreathingColorModes.SetEquals(right.SupportedBreathingColorModes))
            && left.SupportsUniformLightingFrames == right.SupportsUniformLightingFrames
            && left.SupportsWaveDirectionSelection == right.SupportsWaveDirectionSelection
            && left.SupportsPlaybackMuteIndicator
                == right.SupportsPlaybackMuteIndicator
            && left.Blade18KeyboardLayoutId == right.Blade18KeyboardLayoutId
            && string.Equals(
                left.KeyboardLightingLayoutId,
                right.KeyboardLightingLayoutId,
                StringComparison.Ordinal)
            && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
            && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge);

    private static bool CustomPerformanceCapabilitiesEqual(
        CustomPerformanceCapabilities? left,
        CustomPerformanceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.SupportsMaximumFans == right.SupportsMaximumFans
            && left.AllowedCombinations.SetEquals(right.AllowedCombinations));
}
