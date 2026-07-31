using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class FirmwareSmokeAssessmentTests
{
    [Fact]
    public void HealthyBaselineHasNoFailures()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(
            report.Checks,
            check => check.Outcome == FirmwareSmokeOutcome.Failed);
    }

    [Fact]
    public void HealthyRz090528BaselineUsesItsExactCapabilityGate()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.9.0.0") with
        {
            TargetModel = RazerBladeModelCatalog.Rz090528ModelNumber,
            Firmware = new FirmwareIdentity("2.02", "1.09", "1.9.0.0", "Unavailable"),
            Capabilities = CreateSnapshot("1.9.0.0").Capabilities with
            {
                PerformancePresets = ["Balanced", "Custom", "Performance", "Silent"],
                LightingEffects = ["Off", "Spectrum", "Static"],
                ObservedLightingEffects =
                [
                    "Ripple",
                    "Starlight",
                    "Tidal",
                    "Wave",
                    "Wheel",
                ],
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite.ToString(),
                WritableSpecialKeySlots =
                    (SpecialKeySlotSet.M3
                    | SpecialKeySlotSet.M4
                    | SpecialKeySlotSet.M5).ToString(),
                LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly.ToString(),
                OneShotLaptopControls =
                    ["FunctionKeyPrimary", "GamingMode", "StartupAnimation"],
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
                ],
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.True(report.Succeeded);
        Assert.DoesNotContain(
            report.Checks,
            check => check.Outcome == FirmwareSmokeOutcome.Failed);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.special-keys").Outcome);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "capabilities.special-key-slots").Outcome);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.lamp-array").Outcome);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "capabilities.cpu-voltage-optimizer").Outcome);
    }

    [Fact]
    public void PostUpdateAcceptsEqualCapabilityValuesFromDifferentCollections()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = CopyCapabilities(baseline.Capabilities),
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.True(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRequiresMcuVersionToChange()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            snapshot,
            snapshot,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "firmware.mcu").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsDifferentSupportedTargetModel()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            TargetModel = RazerBladeModelCatalog.Rz090528ModelNumber,
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "snapshot.target").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsLostCapability()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = baseline.Capabilities with
            {
                LightingEffects = baseline.Capabilities.LightingEffects
                    .Where(effect => effect != "Static")
                    .ToArray(),
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsLostObservedLightingCapability()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0") with
        {
            Capabilities = CreateSnapshot("1.5.0.0").Capabilities with
            {
                ObservedLightingEffects = ["Spectrum", "Static"],
            },
        };
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = baseline.Capabilities with
            {
                ObservedLightingEffects = ["Static"],
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsChangedSpecialKeyBindingAccess()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = baseline.Capabilities with
            {
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ObservedOnly.ToString(),
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsChangedWritableSpecialKeySlots()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = baseline.Capabilities with
            {
                WritableSpecialKeySlots =
                    (SpecialKeySlotSet.M3 | SpecialKeySlotSet.M4).ToString(),
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsChangedLampArrayAccess()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = baseline.Capabilities with
            {
                LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly.ToString(),
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsBaselineThatDidNotPassItsHealthGate()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("1.5.0.0") with
        {
            DeviceVerified = false,
        };
        FirmwareSmokeSnapshot current = CreateSnapshot("1.6.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            requireMcuChange: true);

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "baseline.integrity").Outcome);
    }

    [Fact]
    public async Task SnapshotRoundTripsThroughStableJson()
    {
        FirmwareSmokeSnapshot expected = CreateSnapshot("1.5.0.0") with
        {
            Capabilities = CreateSnapshot("1.5.0.0").Capabilities with
            {
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
                    DeviceFeatureAccess.ObservedOnly),
                ObservedPowerSourceBehaviors =
                [
                    new(
                        PowerSource.UsbC,
                        PerformancePreset.Balanced,
                        FanMode.Automatic,
                        FixedFanControlAvailable: false,
                        PerformanceModeAvailable: false,
                        CustomModeAvailable: false,
                        InadequatePowerWarningVisible: true),
                ],
            },
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "OpenBlade.FirmwareSmoke.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        try
        {
            await FirmwareSmokeFiles.WriteJsonAsync(path, expected, CancellationToken.None);

            FirmwareSmokeSnapshot actual =
                await FirmwareSmokeFiles.ReadJsonAsync<FirmwareSmokeSnapshot>(
                    path,
                    CancellationToken.None);

            Assert.Equal(expected.Firmware, actual.Firmware);
            Assert.Equal(
                expected.Capabilities.PerformancePresets,
                actual.Capabilities.PerformancePresets);
            Assert.Equal(
                expected.Capabilities.CpuPowerLimit,
                actual.Capabilities.CpuPowerLimit);
            Assert.Equal(
                expected.Capabilities.CpuVoltageOptimizer,
                actual.Capabilities.CpuVoltageOptimizer);
            Assert.Equal(
                expected.Capabilities.GpuOverclock,
                actual.Capabilities.GpuOverclock);
            Assert.Equal(
                expected.Capabilities.ObservedPowerSourceBehaviors,
                actual.Capabilities.ObservedPowerSourceBehaviors);
            Assert.Equal(expected.Controls, actual.Controls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static FirmwareSmokeSnapshot CreateSnapshot(string mcu) => new(
        FirmwareSmokeSnapshotCollector.CurrentSchemaVersion,
        DateTimeOffset.Parse("2026-07-24T20:00:00Z"),
        ObservationAttempts: 1,
        "RZ09-0581",
        "0.1.0+test",
        DeviceVerified: true,
        WritesBlocked: false,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        Message: "Verified device state observed.",
        new FirmwareIdentity("3.01", "1.04", mcu, "98.3.5c.40.3d"),
        new FirmwareSmokeCapabilities(
            [
                "Balanced",
                "BatteryBalanced",
                "BatterySaver",
                "Custom",
                "Performance",
                "Silent",
            ],
            ["Automatic", "Fixed", "ManualCurve", "Maximum"],
            [
                "AmbientAwareness",
                "Breathing",
                "Off",
                "Reactive",
                "Ripple",
                "Spectrum",
                "Starlight",
                "Static",
                "Tidal",
                "Wave",
                "Wheel",
            ],
            [],
            SpecialKeyBindingsAccess: DeviceFeatureAccess.ReadWrite.ToString(),
            WritableSpecialKeySlots: SpecialKeySlotSet.All.ToString(),
            LampArrayLightingAccess: null,
            [50, 55, 60, 65, 70, 75, 80],
            SupportsBatteryProtectionOff: true,
            SupportsTemporaryFullCharge: true,
            ["Breathing", "Off", "Static"],
            [
                "FunctionKeyPrimary",
                "GamingMode",
                "MicrophoneMuteIndicator",
                "StartupAnimation",
            ],
            CustomPerformance: null,
            CpuPowerLimit: null,
            CpuVoltageOptimizer: null,
            GpuOverclock: null,
            ObservedPowerSourceBehaviors: []),
        new FirmwareSmokeControlState(
            "Balanced",
            "Automatic",
            BatteryProtectionEnabled: true,
            ChargeLimitPercent: 80,
            TemporaryFullCharge: false,
            LogoLighting: "Static",
            DesiredLightingEffect: "Spectrum",
            LightingRendererOwnership: "Spectrum",
            StartupAnimationEnabled: true,
            FunctionKeyPrimary: "MultimediaKeys",
            GamingModeEnabled: false,
            KeyboardDriverModeActive: true,
            ActivePowerSource: "PluggedIn"),
        new FirmwareSmokeLiveReadings(
            BatteryPowerFlowMilliwatts: 0,
            Fan1Rpm: 0,
            Fan2Rpm: 0,
            CpuTemperatureCelsius: 48,
            GpuTemperatureCelsius: null),
        new FirmwareSmokeRuntime(
            ServiceProcessCount: 1,
            SessionAgentProcessCount: 1,
            TrayProcessCount: 1,
            [
                new InstalledComponent(
                    "OpenBlade.Service.exe",
                    "0.1.0+test",
                    100),
                new InstalledComponent(
                    "OpenBlade.SessionAgent.exe",
                    "0.1.0+test",
                    100),
                new InstalledComponent(
                    "OpenBlade.Tray.exe",
                    "0.1.0+test",
                    100),
            ]));

    private static FirmwareSmokeCapabilities CopyCapabilities(
        FirmwareSmokeCapabilities value) => value with
        {
            PerformancePresets = value.PerformancePresets.ToArray(),
            FanModes = value.FanModes.ToArray(),
            LightingEffects = value.LightingEffects.ToArray(),
            ObservedLightingEffects = value.ObservedLightingEffects.ToArray(),
            ChargeLimits = value.ChargeLimits.ToArray(),
            LogoLightingModes = value.LogoLightingModes.ToArray(),
            OneShotLaptopControls = value.OneShotLaptopControls.ToArray(),
        };
}
