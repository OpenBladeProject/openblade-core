using OpenBlade.Contracts;

namespace OpenBlade.Capture;

internal static class Rz090528ProductionAdmissionCapabilityGate
{
    private static readonly IReadOnlySet<PerformancePreset> PerformancePresets =
        new HashSet<PerformancePreset>
        {
            PerformancePreset.Balanced,
            PerformancePreset.Silent,
            PerformancePreset.Performance,
            PerformancePreset.Custom,
        };

    private static readonly IReadOnlySet<CustomPerformanceCombination>
        CustomPerformanceCombinations = new HashSet<CustomPerformanceCombination>
        {
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
        };

    private static readonly IReadOnlySet<FanMode> FanModes =
        new HashSet<FanMode>
        {
            FanMode.Automatic,
            FanMode.Fixed,
            FanMode.ManualCurve,
            FanMode.Maximum,
        };

    private static readonly IReadOnlySet<int> ChargeLimits =
        new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 };

    private static readonly IReadOnlySet<OneShotLaptopControl> LaptopControls =
        new HashSet<OneShotLaptopControl>
        {
            OneShotLaptopControl.StartupAnimation,
            OneShotLaptopControl.FunctionKeyPrimary,
            OneShotLaptopControl.GamingMode,
        };

    private static readonly IReadOnlySet<LightingEffect> ObservedLightingEffects =
        new HashSet<LightingEffect>
        {
            LightingEffect.Spectrum,
            LightingEffect.Starlight,
            LightingEffect.Ripple,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
        };

    private static readonly PowerSourceBehaviorObservation[] PowerSourceBehaviors =
    [
        new(
            PowerSource.Battery,
            PerformancePreset.BatterySaver,
            FanMode.Automatic,
            FixedFanControlAvailable: false,
            PerformanceModeAvailable: false,
            CustomModeAvailable: false,
            InadequatePowerWarningVisible: false),
        new(
            PowerSource.UsbC,
            PerformancePreset.Balanced,
            FanMode.Automatic,
            FixedFanControlAvailable: false,
            PerformanceModeAvailable: false,
            CustomModeAvailable: false,
            InadequatePowerWarningVisible: true),
    ];

    internal static bool Matches(
        DeviceCapabilities battery,
        DeviceCapabilities lighting) =>
        battery.PerformancePresets.SetEquals(PerformancePresets)
        && battery.CustomPerformance?.AllowedCombinations.SetEquals(
            CustomPerformanceCombinations) == true
        && battery.CpuPowerLimit is
        {
            Watts:
            {
                Minimum: 45,
                Maximum: 95,
                Step: 1,
            },
            Access: DeviceFeatureAccess.ObservedOnly,
        }
        && battery.CpuVoltageOptimizer is
        {
            Value:
            {
                Minimum: -30,
                Maximum: -1,
                Step: 1,
            },
            SupportsEnableDisable: true,
            Access: DeviceFeatureAccess.ObservedOnly,
        }
        && battery.GpuOverclock is
        {
            CoreOffsetMegahertz:
            {
                Minimum: -1000,
                Maximum: 400,
                Step: null,
            },
            MemoryOffsetMegahertz:
            {
                Minimum: -1000,
                Maximum: 3000,
                Step: null,
            },
            SupportsEnableDisable: true,
            Access: DeviceFeatureAccess.ObservedOnly,
        }
        && battery.ObservedPowerSourceBehaviors.SequenceEqual(PowerSourceBehaviors)
        && battery.FanModes.SetEquals(FanModes)
        && battery.FanSpeedRange == new FanSpeedRange(2000, 5000, 100)
        && battery.ChargeLimits.SetEquals(ChargeLimits)
        && battery.SupportsBatteryProtectionOff
        && battery.SupportsTemporaryFullCharge
        && battery.OneShotLaptopControls.SetEquals(LaptopControls)
        && lighting.LightingEffects.SetEquals(
        [
            LightingEffect.Off,
            LightingEffect.Static,
        ])
        && lighting.ObservedLightingEffects.SetEquals(ObservedLightingEffects)
        && lighting.SpecialKeyBindingsAccess == DeviceFeatureAccess.ReadWrite
        && lighting.WritableSpecialKeySlots == (
            SpecialKeySlotSet.M3
            | SpecialKeySlotSet.M4
            | SpecialKeySlotSet.M5)
        && lighting.LampArrayLightingAccess == DeviceFeatureAccess.ObservedOnly
        && lighting.LogoLightingModes.SetEquals(Enum.GetValues<LogoLightingMode>())
        && lighting.SupportsKeyboardBrightness
        && !lighting.SupportsPlaybackMuteIndicator;
}
