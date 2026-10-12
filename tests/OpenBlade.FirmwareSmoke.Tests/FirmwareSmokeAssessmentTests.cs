using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class FirmwareSmokeAssessmentTests
{
    [Fact]
    public void CompareUnchangedInstalledGeneration_AcceptsGreenExactSnapshot()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report =
            FirmwareSmokeAssessment.CompareUnchangedInstalledGeneration(
                snapshot,
                snapshot);

        Assert.True(report.Succeeded);
    }

    [Fact]
    public void CompareUnchangedInstalledGeneration_AcceptsDisabledLightingRenderer()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0") with
        {
            Controls = CreateSnapshot("1.5.0.0").Controls with
            {
                DesiredLightingEffect = "Off",
                LightingRendererOwnership = "None",
            },
        };

        FirmwareSmokeReport report =
            FirmwareSmokeAssessment.CompareUnchangedInstalledGeneration(
                snapshot,
                snapshot);

        Assert.True(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "state.lighting-renderer").Outcome);
    }

    [Fact]
    public void CompareUnchangedInstalledGeneration_RejectsMissingActiveRenderer()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0") with
        {
            Controls = CreateSnapshot("1.5.0.0").Controls with
            {
                DesiredLightingEffect = "Static",
                LightingRendererOwnership = "None",
            },
        };

        FirmwareSmokeReport report =
            FirmwareSmokeAssessment.CompareUnchangedInstalledGeneration(
                snapshot,
                snapshot);

        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                report.Checks,
                check => check.Id == "state.lighting-renderer").Outcome);
    }

    [Fact]
    public void HealthyBaselineHasNoFailures()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.Contains("AudioMeter", snapshot.Capabilities.LightingEffects);
        Assert.Contains("Sequence", snapshot.Capabilities.LightingEffects);
        Assert.True(report.Succeeded);
        Assert.DoesNotContain(
            report.Checks,
            check => check.Outcome == FirmwareSmokeOutcome.Failed);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.lighting").Outcome);
        string inventory = Assert.Single(
            report.Checks,
            check => check.Id == "firmware.inventory.available").Actual;
        Assert.Contains(
            "VBIOS=98.3.5c.40.3d (GPU=NVIDIA GeForce RTX 5090 Laptop GPU)",
            inventory);
        Assert.Contains("Thunderbolt=Unavailable (ObservedOnly)", inventory);
        Assert.Contains("Touchpad=Unavailable (ObservedOnly)", inventory);
    }

    [Fact]
    public void Rz090581DirectHyperBoostBaseline_RejectsMissingCustomAdmission()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.6.0.0");
        snapshot = snapshot with
        {
            Capabilities = snapshot.Capabilities with
            {
                HyperBoostBaseModes = snapshot.Capabilities.HyperBoostBaseModes! with { Custom = null },
            },
        };
        FirmwareSmokeCheck check = Assert.Single(FirmwareSmokeAssessment.ValidateBaseline(snapshot).Checks,
            item => item.Id == "capabilities.hyper-boost-base-modes");
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
    }
    [Fact]
    public void Rz090581Bios400RequiresReadWriteHyperBoostCapability()
    {
        FirmwareSmokeSnapshot admitted = CreateSnapshot("1.6.0.0") with
        {
            Firmware = new FirmwareIdentity("4.00", "1.04", "1.6.0.0", "98.3.5c.40.3d"),
            Capabilities = CreateSnapshot("1.6.0.0").Capabilities with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite.ToString(),
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite)
                {
                    Custom = DeviceFeatureAccess.ReadWrite,
                },
            },
        };

        FirmwareSmokeReport admittedReport =
            FirmwareSmokeAssessment.ValidateBaseline(admitted);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                admittedReport.Checks,
                check => check.Id == "capabilities.hyper-boost").Outcome);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                admittedReport.Checks,
                check => check.Id == "capabilities.hyper-boost-base-modes").Outcome);

        FirmwareSmokeReport missingReport = FirmwareSmokeAssessment.ValidateBaseline(
            admitted with
            {
                Capabilities = admitted.Capabilities with { HyperBoostAccess = null },
            });
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                missingReport.Checks,
                check => check.Id == "capabilities.hyper-boost").Outcome);

        FirmwareSmokeReport missingBaseModesReport =
            FirmwareSmokeAssessment.ValidateBaseline(
                admitted with
                {
                    Capabilities = admitted.Capabilities with
                    {
                        HyperBoostBaseModes = null,
                    },
                });
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                missingBaseModesReport.Checks,
                check => check.Id == "capabilities.hyper-boost-base-modes").Outcome);
    }

    [Fact]
    public void Rz090581RequiresReadWritePadlessHyperBoostAfterLifecycleAdmission()
    {
        FirmwareSmokeSnapshot admitted = CreateSnapshot("1.6.0.0") with
        {
            Capabilities = CreateSnapshot("1.6.0.0").Capabilities with
            {
                HyperBoostWithoutCoolingPadAccess =
                    DeviceFeatureAccess.ReadWrite.ToString(),
            },
        };

        FirmwareSmokeReport admittedReport =
            FirmwareSmokeAssessment.ValidateBaseline(admitted);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                admittedReport.Checks,
                check => check.Id
                    == "capabilities.hyper-boost-without-cooling-pad").Outcome);

        FirmwareSmokeReport observedOnlyReport =
            FirmwareSmokeAssessment.ValidateBaseline(
                admitted with
                {
                    Capabilities = admitted.Capabilities with
                    {
                        HyperBoostWithoutCoolingPadAccess =
                            DeviceFeatureAccess.ObservedOnly.ToString(),
                    },
                });
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                observedOnlyReport.Checks,
                check => check.Id
                    == "capabilities.hyper-boost-without-cooling-pad").Outcome);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ObservedOnly", true)]
    [InlineData("ReadWrite", true)]
    [InlineData("ReadOnly", false)]
    public void Rz090528PadlessAccessAllowsOnlyAdmittedReadWriteOrValidationObservedOnly(
        string? access, bool passes)
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.6.0.0") with
        {
            TargetModel = "RZ09-0528",
        };
        snapshot = snapshot with
        {
            Capabilities = snapshot.Capabilities with { HyperBoostWithoutCoolingPadAccess = access },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);
        Assert.Equal(
            passes ? FirmwareSmokeOutcome.Passed : FirmwareSmokeOutcome.Failed,
            Assert.Single(
                report.Checks,
                check => check.Id == "capabilities.hyper-boost-without-cooling-pad").Outcome);
    }
    [Fact]
    public void Rz090581FirmwareUsesModelScopedHyperBoostCapability()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "capabilities.hyper-boost").Outcome);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "capabilities.hyper-boost-base-modes").Outcome);
    }

    [Fact]
    public async Task HealthyRz090528BaselineRoundTripsAndUsesItsExactCapabilityGate()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.9.0.0") with
        {
            TargetModel = RazerBladeModelCatalog.Rz090528ModelNumber,
            Firmware = new FirmwareIdentity("2.02", "1.09", "1.9.0.0", "Unavailable"),
            Capabilities = CreateSnapshot("1.9.0.0").Capabilities with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite.ToString(),
                HyperBoostWithoutCoolingPadAccess = DeviceFeatureAccess.ReadWrite.ToString(),
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite)
                { Custom = DeviceFeatureAccess.ReadWrite },
                LightingEffects =
                [
                    "AmbientAwareness",
                    "AudioMeter",
                    "Breathing",
                    "Fire",
                    "Off",
                    "Reactive",
                    "Ripple",
                    "Sequence",
                    "Spectrum",
                    "Starlight",
                    "Static",
                    "Tidal",
                    "Wave",
                    "Wheel",
                ],
                ObservedLightingEffects = [],
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite.ToString(),
                WritableSpecialKeySlots = SpecialKeySlotSet.All.ToString(),
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
                    DeviceFeatureAccess.ReadWrite)
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

        string directory = Path.Combine(
            Path.GetTempPath(),
            "OpenBlade.FirmwareSmoke.Tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        FirmwareSmokeSnapshot expected = snapshot;
        try
        {
            await FirmwareSmokeFiles.WriteJsonAsync(path, expected, CancellationToken.None);
            snapshot = await FirmwareSmokeFiles.ReadJsonAsync<FirmwareSmokeSnapshot>(
                path,
                CancellationToken.None);

            Assert.Equal(expected.SchemaVersion, snapshot.SchemaVersion);
            Assert.Equal(expected.TargetModel, snapshot.TargetModel);
            Assert.Equal(expected.Controls, snapshot.Controls);
            Assert.NotNull(snapshot.Capabilities.CustomPerformance);
            Assert.Equal(7, snapshot.Capabilities.CustomPerformance.AllowedCombinations.Count);
            Assert.True(expected.Capabilities.CustomPerformance!.AllowedCombinations.SetEquals(
                snapshot.Capabilities.CustomPerformance.AllowedCombinations));
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(expected, FirmwareSmokeFiles.JsonOptions),
                System.Text.Json.JsonSerializer.Serialize(snapshot, FirmwareSmokeFiles.JsonOptions));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.Equal(DeviceFeatureAccess.ReadWrite.ToString(), snapshot.Capabilities.HyperBoostAccess);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.hyper-boost").Outcome);
        foreach (DeviceFeatureAccess unsupportedAccess in Enum.GetValues<DeviceFeatureAccess>()
            .Where(access => access != DeviceFeatureAccess.ReadWrite))
        {
            FirmwareSmokeReport unsupportedHyperBoostReport = FirmwareSmokeAssessment.ValidateBaseline(
                snapshot with
                {
                    Capabilities = snapshot.Capabilities with
                    {
                        HyperBoostAccess = unsupportedAccess.ToString(),
                    },
                });
            Assert.False(unsupportedHyperBoostReport.Succeeded);
            Assert.Equal(
                FirmwareSmokeOutcome.Failed,
                Assert.Single(
                    unsupportedHyperBoostReport.Checks,
                    check => check.Id == "capabilities.hyper-boost").Outcome);
        }

        Assert.Contains("Sequence", snapshot.Capabilities.LightingEffects);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.lighting").Outcome);
        Assert.True(report.Succeeded);

        FirmwareSmokeReport missingSequenceReport = FirmwareSmokeAssessment.ValidateBaseline(
            snapshot with
            {
                Capabilities = snapshot.Capabilities with
                {
                    LightingEffects = snapshot.Capabilities.LightingEffects
                        .Where(effect => effect != "Sequence")
                        .ToArray(),
                },
            });
        Assert.False(missingSequenceReport.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                missingSequenceReport.Checks,
                check => check.Id == "capabilities.lighting").Outcome);
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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

        Assert.True(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(report.Checks, check => check.Id == "capabilities.unchanged").Outcome);
    }

    [Fact]
    public void PostUpdateRequiresEveryDeclaredFirmwareComponentToChange()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            snapshot,
            snapshot,
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "firmware.mcu").Outcome);
    }

    [Fact]
    public void PostUpdateRejectsAnEmptyExpectedChangeSet()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        Assert.Throws<ArgumentException>(() =>
            FirmwareSmokeAssessment.ComparePostUpdate(
                snapshot,
                snapshot,
                new HashSet<FirmwareComponent>()));
    }

    [Fact]
    public void RehearsalRequiresEverySelectedComponentToBeReadable()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("Unavailable");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.CompareRehearsal(
            snapshot,
            snapshot,
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

        FirmwareSmokeCheck check = Assert.Single(
            report.Checks,
            item => item.Id == "firmware.mcu");
        Assert.False(report.Succeeded);
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.DoesNotContain("Different from Unavailable", check.Expected);
        Assert.Contains("readable", check.Expected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RehearsalAllowsAnUnrelatedComponentToBeUnavailable()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("Unavailable");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.CompareRehearsal(
            snapshot,
            snapshot,
            new HashSet<FirmwareComponent> { FirmwareComponent.Bios });

        Assert.True(report.Succeeded);
        Assert.Equal("pre-update rehearsal", report.Phase);
    }

    [Fact]
    public void RehearsalRejectsAnyFirmwareChange()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("mcu-before");
        FirmwareSmokeSnapshot current = baseline with
        {
            Firmware = baseline.Firmware with { Ec = "ec-after" },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.CompareRehearsal(
            baseline,
            current,
            new HashSet<FirmwareComponent> { FirmwareComponent.Bios });

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "firmware.ec").Outcome);
    }

    [Fact]
    public void RehearsalRejectsAnEmptyComponentSet()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("1.5.0.0");

        Assert.Throws<ArgumentException>(() =>
            FirmwareSmokeAssessment.CompareRehearsal(
                snapshot,
                snapshot,
                new HashSet<FirmwareComponent>()));
    }

    [Theory]
    [InlineData(FirmwareComponent.Bios)]
    [InlineData(FirmwareComponent.Ec)]
    [InlineData(FirmwareComponent.Mcu)]
    [InlineData(FirmwareComponent.Vbios)]
    public void PostUpdateAcceptsADeclaredFirmwareComponentChange(
        FirmwareComponent component)
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("mcu-before");
        FirmwareSmokeSnapshot current = baseline with
        {
            Firmware = ChangeFirmwareVersion(baseline.Firmware, component, "after"),
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            new HashSet<FirmwareComponent> { component });

        Assert.True(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == $"firmware.{component.ToString().ToLowerInvariant()}")
                .Outcome);
        if (component == FirmwareComponent.Vbios)
        {
            Assert.Equal(
                FirmwareSmokeOutcome.Passed,
                Assert.Single(
                    report.Checks,
                    check => check.Id == "firmware.vbios-gpu").Outcome);
        }
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("NVIDIA GeForce RTX 5080 Laptop GPU")]
    public void PostUpdateRejectsUnreadableOrChangedVbiosGpuIdentity(
        string currentGpuName)
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("mcu-before");
        FirmwareSmokeSnapshot current = baseline with
        {
            Firmware = baseline.Firmware with
            {
                Vbios = "vbios-after",
                VbiosGpuName = currentGpuName,
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            new HashSet<FirmwareComponent> { FirmwareComponent.Vbios });

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(
                report.Checks,
                check => check.Id == "firmware.vbios-gpu").Outcome);
    }

    [Fact]
    public void PostUpdateAcceptsMultipleDeclaredFirmwareChanges()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("mcu-before");
        FirmwareSmokeSnapshot current = baseline with
        {
            Firmware = baseline.Firmware with
            {
                Bios = "bios-after",
                Ec = "ec-after",
            },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            new HashSet<FirmwareComponent>
            {
                FirmwareComponent.Bios,
                FirmwareComponent.Ec,
            });

        Assert.True(report.Succeeded);
    }

    [Fact]
    public void PostUpdateRejectsAnUndeclaredFirmwareChange()
    {
        FirmwareSmokeSnapshot baseline = CreateSnapshot("mcu-before");
        FirmwareSmokeSnapshot current = baseline with
        {
            Firmware = baseline.Firmware with { Bios = "bios-after" },
        };

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ComparePostUpdate(
            baseline,
            current,
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

        Assert.False(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            Assert.Single(report.Checks, check => check.Id == "firmware.bios").Outcome);
    }

    [Fact]
    public void BaselineDoesNotRequireAnUnrelatedFirmwareComponent()
    {
        FirmwareSmokeSnapshot snapshot = CreateSnapshot("Unavailable");

        FirmwareSmokeReport report = FirmwareSmokeAssessment.ValidateBaseline(snapshot);

        Assert.True(report.Succeeded);
        Assert.Equal(
            FirmwareSmokeOutcome.Passed,
            Assert.Single(
                report.Checks,
                check => check.Id == "firmware.inventory.available").Outcome);
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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
            new HashSet<FirmwareComponent> { FirmwareComponent.Mcu });

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
                expected.Capabilities.HyperBoostAccess,
                actual.Capabilities.HyperBoostAccess);
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
        UnconfirmedWrites: [],
        Message: "Verified device state observed.",
        new FirmwareIdentity(
            "3.01",
            "1.04",
            mcu,
            "98.3.5c.40.3d",
            ThunderboltAccess: DeviceFeatureAccess.ObservedOnly.ToString(),
            TouchpadAccess: DeviceFeatureAccess.ObservedOnly.ToString(),
            VbiosGpuName: "NVIDIA GeForce RTX 5090 Laptop GPU"),
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
                "AudioMeter",
                "Breathing",
                "Off",
                "Reactive",
                "Ripple",
                "Sequence",
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
            ObservedPowerSourceBehaviors: [],
            HyperBoostAccess: DeviceFeatureAccess.ReadWrite.ToString(),
            HyperBoostWithoutCoolingPadAccess:
                DeviceFeatureAccess.ReadWrite.ToString())
        {
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite)
            {
                Custom = DeviceFeatureAccess.ReadWrite,
            },
        },
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

    private static FirmwareIdentity ChangeFirmwareVersion(
        FirmwareIdentity firmware,
        FirmwareComponent component,
        string value) =>
        component switch
        {
            FirmwareComponent.Bios => firmware with { Bios = value },
            FirmwareComponent.Ec => firmware with { Ec = value },
            FirmwareComponent.Mcu => firmware with { Mcu = value },
            FirmwareComponent.Vbios => firmware with { Vbios = value },
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };

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
