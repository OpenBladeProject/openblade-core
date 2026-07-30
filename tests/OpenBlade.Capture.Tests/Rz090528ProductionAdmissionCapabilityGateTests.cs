using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528ProductionAdmissionCapabilityGateTests
{
    [Fact]
    public void Matches_RequiresEveryExactAdmittedAndObservedCapability()
    {
        (DeviceCapabilities battery, DeviceCapabilities lighting) = ExactCapabilities();

        Assert.True(Rz090528ProductionAdmissionCapabilityGate.Matches(battery, lighting));
    }

    [Fact]
    public void Matches_RejectsDroppedModelSpecificDiscoveries()
    {
        (DeviceCapabilities battery, DeviceCapabilities lighting) = ExactCapabilities();
        var cases = new (DeviceCapabilities Battery, DeviceCapabilities Lighting)[]
        {
            (battery with { CpuPowerLimit = null }, lighting),
            (battery with { CpuVoltageOptimizer = null }, lighting),
            (
                battery with
                {
                    CpuVoltageOptimizer = battery.CpuVoltageOptimizer! with
                    {
                        SupportsEnableDisable = false,
                    },
                },
                lighting),
            (battery with { GpuOverclock = null }, lighting),
            (
                battery with
                {
                    GpuOverclock = battery.GpuOverclock! with
                    {
                        SupportsEnableDisable = false,
                    },
                },
                lighting),
            (battery with { ObservedPowerSourceBehaviors = [] }, lighting),
            (battery, lighting with { SpecialKeyBindingsAccess = null }),
            (
                battery,
                lighting with
                {
                    WritableSpecialKeySlots =
                        SpecialKeySlotSet.M3 | SpecialKeySlotSet.M4,
                }),
            (battery, lighting with { LampArrayLightingAccess = null }),
            (
                battery,
                lighting with
                {
                    ObservedLightingEffects =
                        new HashSet<LightingEffect> { LightingEffect.Static },
                }),
        };

        Assert.All(
            cases,
            value => Assert.False(
                Rz090528ProductionAdmissionCapabilityGate.Matches(
                    value.Battery,
                    value.Lighting)));
    }

    [Fact]
    public void Matches_RejectsOlderModelCombinationAssumptions()
    {
        (DeviceCapabilities battery, DeviceCapabilities lighting) = ExactCapabilities();
        battery = battery with
        {
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium),
                }),
        };

        Assert.False(
            Rz090528ProductionAdmissionCapabilityGate.Matches(battery, lighting));
    }

    private static (DeviceCapabilities Battery, DeviceCapabilities Lighting)
        ExactCapabilities()
    {
        var battery = new DeviceCapabilities(
            new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            },
            new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            new HashSet<LightingEffect>(),
            new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
            SupportsBatteryProtectionOff: true,
            SupportsTemporaryFullCharge: true)
        {
            FanSpeedRange = new FanSpeedRange(2000, 5000, 100),
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
                }),
            CpuPowerLimit = new CpuPowerLimitCapabilities(
                new DeviceNumericRange(45, 95, 1),
                DeviceFeatureAccess.ObservedOnly),
            CpuVoltageOptimizer = new CpuVoltageOptimizerCapabilities(
                new DeviceNumericRange(-30, -1, 1),
                DeviceFeatureAccess.ObservedOnly)
            {
                SupportsEnableDisable = true,
            },
            GpuOverclock = new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ObservedOnly)
            {
                SupportsEnableDisable = true,
            },
            ObservedPowerSourceBehaviors =
            [
                new PowerSourceBehaviorObservation(
                    PowerSource.Battery,
                    PerformancePreset.BatterySaver,
                    FanMode.Automatic,
                    FixedFanControlAvailable: false,
                    PerformanceModeAvailable: false,
                    CustomModeAvailable: false,
                    InadequatePowerWarningVisible: false),
                new PowerSourceBehaviorObservation(
                    PowerSource.UsbC,
                    PerformancePreset.Balanced,
                    FanMode.Automatic,
                    FixedFanControlAvailable: false,
                    PerformanceModeAvailable: false,
                    CustomModeAvailable: false,
                    InadequatePowerWarningVisible: true),
            ],
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            },
        };
        var lighting = new DeviceCapabilities(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect> { LightingEffect.Off },
            new HashSet<int>(),
            SupportsBatteryProtectionOff: false,
            SupportsTemporaryFullCharge: false)
        {
            SupportsKeyboardBrightness = true,
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
            WritableSpecialKeySlots =
                SpecialKeySlotSet.M3
                | SpecialKeySlotSet.M4
                | SpecialKeySlotSet.M5,
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
            ObservedLightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Spectrum,
                LightingEffect.Static,
                LightingEffect.Starlight,
                LightingEffect.Ripple,
                LightingEffect.Tidal,
                LightingEffect.Wave,
                LightingEffect.Wheel,
            },
            LogoLightingModes = new HashSet<LogoLightingMode>(
                Enum.GetValues<LogoLightingMode>()),
        };
        return (battery, lighting);
    }
}
