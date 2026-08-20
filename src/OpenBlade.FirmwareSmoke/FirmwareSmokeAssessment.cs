using OpenBlade.Contracts;

namespace OpenBlade.FirmwareSmoke;

public static class FirmwareSmokeAssessment
{
    private static readonly string[] ExpectedPerformancePresets =
    [
        "Balanced",
        "BatteryBalanced",
        "BatterySaver",
        "Custom",
        "Performance",
        "Silent",
    ];

    private static readonly string[] ExpectedFanModes =
    [
        "Automatic",
        "Fixed",
        "ManualCurve",
        "Maximum",
    ];

    private static readonly string[] ExpectedLightingEffects =
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
    ];

    private static readonly int[] ExpectedChargeLimits = [50, 55, 60, 65, 70, 75, 80];

    private static readonly string[] ExpectedLogoModes = ["Breathing", "Off", "Static"];

    private static readonly string[] ExpectedRz090528LightingEffects =
    [
        "AmbientAwareness",
        "AudioMeter",
        "Breathing",
        "Fire",
        "Off",
        "Reactive",
        "Ripple",
        "Spectrum",
        "Starlight",
        "Static",
        "Tidal",
        "Wave",
        "Wheel",
    ];

    private static readonly string[] ExpectedLaptopControls =
    [
        "FunctionKeyPrimary",
        "GamingMode",
        "MicrophoneMuteIndicator",
        "StartupAnimation",
    ];

    private static readonly FirmwareSmokeCapabilityExpectation Rz090581Expectation = new(
        ExpectedPerformancePresets,
        ExpectedFanModes,
        ExpectedLightingEffects,
        ExpectedChargeLimits,
        ExpectedLogoModes,
        ExpectedLaptopControls,
        DeviceFeatureAccess.ReadWrite.ToString(),
        SpecialKeySlotSet.All.ToString(),
        "Unavailable");

    private static readonly FirmwareSmokeCapabilityExpectation Rz090528Expectation = new(
        ["Balanced", "Custom", "Performance", "Silent"],
        ExpectedFanModes,
        ExpectedRz090528LightingEffects,
        ExpectedChargeLimits,
        ExpectedLogoModes,
        ["FunctionKeyPrimary", "GamingMode", "StartupAnimation"],
        DeviceFeatureAccess.ReadWrite.ToString(),
        (SpecialKeySlotSet.M3 | SpecialKeySlotSet.M4 | SpecialKeySlotSet.M5).ToString(),
        DeviceFeatureAccess.ObservedOnly.ToString());

    private static readonly IReadOnlySet<CustomPerformanceCombination>
        ExpectedRz090528CustomPerformance = new HashSet<CustomPerformanceCombination>
        {
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
        };

    private static readonly CpuPowerLimitCapabilities ExpectedRz090528CpuPowerLimit =
        new(new DeviceNumericRange(45, 95, 1), DeviceFeatureAccess.ObservedOnly);

    private static readonly CpuVoltageOptimizerCapabilities
        ExpectedRz090528CpuVoltageOptimizer = new(
            new DeviceNumericRange(-30, -1, 1),
            DeviceFeatureAccess.ObservedOnly)
        {
            SupportsEnableDisable = true,
        };

    private static readonly GpuOverclockCapabilities ExpectedRz090528GpuOverclock = new(
        new DeviceNumericRange(-1000, 400, null),
        new DeviceNumericRange(-1000, 3000, null),
        DeviceFeatureAccess.ObservedOnly)
    {
        SupportsEnableDisable = true,
    };

    private static readonly IReadOnlyList<PowerSourceBehaviorObservation>
        ExpectedRz090528PowerSourceBehaviors =
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

    public static IReadOnlyList<string> ManualChecks { get; } =
    [
        "Confirm keyboard Off and brightness; on models with admitted effects, also confirm Static and one animation.",
        "Confirm keyboard brightness decreases and increases with the physical function keys.",
        "Confirm media, display-brightness, Project, and Print Screen function-row actions.",
        "Confirm the performance key or Fn+P changes mode and shows the expected notification.",
        "If special-key access is ReadWrite, confirm every advertised writable slot and ordinary-key pass-through for every unavailable slot; if ObservedOnly, confirm the UI keeps bindings inactive.",
        "If MicrophoneMuteIndicator is admitted, confirm microphone mute changes Windows state and its keyboard indicator.",
        "Unplug AC, test the Battery profile, then test USB-C and return to the full-power adapter.",
        "Sleep and resume once; confirm lighting, Driver mode, profile state, and readings recover.",
    ];

    public static FirmwareSmokeReport ValidateBaseline(FirmwareSmokeSnapshot snapshot)
    {
        var checks = CommonHealthChecks(snapshot);
        checks.Add(Check(
            "firmware.inventory.available",
            "Firmware inventory includes a readable component",
            FirmwareVersions(snapshot.Firmware).Any(version => !IsUnavailable(version.Value)),
            "At least one concrete firmware version",
            FirmwareInventory(snapshot.Firmware)));
        return new FirmwareSmokeReport(
            "baseline",
            DateTimeOffset.UtcNow,
            checks,
            ManualChecks);
    }

    public static FirmwareSmokeReport ComparePostUpdate(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current,
        IReadOnlySet<FirmwareComponent> expectedChangedComponents)
    {
        RequireComponents(expectedChangedComponents, nameof(expectedChangedComponents));
        return CompareSnapshots(
            baseline,
            current,
            expectedChangedComponents,
            new HashSet<FirmwareComponent>(),
            "post-update");
    }

    internal static FirmwareSmokeReport CompareUnchangedInstalledGeneration(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current) =>
        CompareSnapshots(
            baseline,
            current,
            new HashSet<FirmwareComponent>(),
            new HashSet<FirmwareComponent>(),
            "installed-generation");

    public static FirmwareSmokeReport CompareRehearsal(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current,
        IReadOnlySet<FirmwareComponent> componentsUnderTest)
    {
        RequireComponents(componentsUnderTest, nameof(componentsUnderTest));
        return CompareSnapshots(
            baseline,
            current,
            new HashSet<FirmwareComponent>(),
            componentsUnderTest,
            "pre-update rehearsal");
    }

    private static FirmwareSmokeReport CompareSnapshots(
        FirmwareSmokeSnapshot baseline,
        FirmwareSmokeSnapshot current,
        IReadOnlySet<FirmwareComponent> expectedChangedComponents,
        IReadOnlySet<FirmwareComponent> requiredReadableComponents,
        string phase)
    {
        var checks = CommonHealthChecks(current);
        FirmwareSmokeReport baselineValidation = ValidateBaseline(baseline);
        string[] baselineFailures = baselineValidation.Checks
            .Where(check => check.Outcome == FirmwareSmokeOutcome.Failed)
            .Select(check => check.Id)
            .ToArray();
        checks.Add(Check(
            "baseline.integrity",
            "Baseline passed its original read-only health gate",
            baselineFailures.Length == 0,
            "No failed baseline checks",
            baselineFailures.Length == 0
                ? "Passed"
                : string.Join(", ", baselineFailures)));
        checks.Add(Check(
            "snapshot.schema",
            "Baseline schema is supported",
            baseline.SchemaVersion == FirmwareSmokeSnapshotCollector.CurrentSchemaVersion,
            FirmwareSmokeSnapshotCollector.CurrentSchemaVersion.ToString(),
            baseline.SchemaVersion.ToString()));
        checks.Add(Check(
            "snapshot.target",
            "Baseline and current snapshots target the same laptop model",
            string.Equals(
                baseline.TargetModel,
                current.TargetModel,
                StringComparison.OrdinalIgnoreCase),
            baseline.TargetModel,
            current.TargetModel));
        checks.Add(Check(
            "product.service-version",
            "Service build did not change during the firmware update",
            string.Equals(
                baseline.ServiceVersion,
                current.ServiceVersion,
                StringComparison.Ordinal),
            baseline.ServiceVersion ?? "Unavailable",
            current.ServiceVersion ?? "Unavailable"));
        AddFirmwareVersionChecks(
            checks,
            baseline.Firmware,
            current.Firmware,
            expectedChangedComponents,
            requiredReadableComponents);
        checks.Add(Check(
            "capabilities.unchanged",
            "Advertised capabilities match the baseline",
            CapabilitiesEqual(baseline.Capabilities, current.Capabilities),
            Summarize(baseline.Capabilities),
            Summarize(current.Capabilities)));
        checks.Add(Check(
            "state.performance",
            "Performance readback matches the baseline",
            baseline.Controls.Performance == current.Controls.Performance,
            baseline.Controls.Performance ?? "Unavailable",
            current.Controls.Performance ?? "Unavailable"));
        checks.Add(Check(
            "state.fans",
            "Fan-mode readback matches the baseline",
            baseline.Controls.Fans == current.Controls.Fans,
            baseline.Controls.Fans ?? "Unavailable",
            current.Controls.Fans ?? "Unavailable"));
        checks.Add(Check(
            "state.battery",
            "Battery protection and charge-limit readback match the baseline",
            baseline.Controls.BatteryProtectionEnabled
                == current.Controls.BatteryProtectionEnabled
                && baseline.Controls.ChargeLimitPercent
                == current.Controls.ChargeLimitPercent
                && baseline.Controls.TemporaryFullCharge
                == current.Controls.TemporaryFullCharge,
            BatteryState(baseline),
            BatteryState(current)));
        checks.Add(Check(
            "state.logo",
            "Lid-logo readback matches the baseline",
            baseline.Controls.LogoLighting == current.Controls.LogoLighting,
            baseline.Controls.LogoLighting ?? "Unavailable",
            current.Controls.LogoLighting ?? "Unavailable"));
        checks.Add(Check(
            "state.laptop-controls",
            "Startup, function-key, and Gaming-mode readbacks match the baseline",
            baseline.Controls.StartupAnimationEnabled
                == current.Controls.StartupAnimationEnabled
                && baseline.Controls.FunctionKeyPrimary
                == current.Controls.FunctionKeyPrimary
                && baseline.Controls.GamingModeEnabled
                == current.Controls.GamingModeEnabled,
            LaptopControlState(baseline),
            LaptopControlState(current)));
        checks.Add(Check(
            "state.driver-mode",
            "Keyboard Driver mode is confirmed",
            current.Controls.KeyboardDriverModeActive == true,
            "True",
            Value(current.Controls.KeyboardDriverModeActive)));
        checks.Add(Check(
            "state.lighting-renderer",
            "Lighting renderer state matches the desired effect and is not faulted",
            string.Equals(
                current.Controls.DesiredLightingEffect,
                "Off",
                StringComparison.Ordinal)
                ? current.Controls.LightingRendererOwnership == "None"
                : current.Controls.LightingRendererOwnership is not "None" and not "Faulted",
            "None for Off; otherwise an active, non-faulted renderer",
            $"{current.Controls.DesiredLightingEffect ?? "Unavailable"}/" +
                current.Controls.LightingRendererOwnership));
        checks.Add(Check(
            "runtime.components",
            "Installed OpenBlade component versions match the baseline",
            baseline.Runtime.InstalledComponents.SequenceEqual(
                current.Runtime.InstalledComponents),
            Components(baseline),
            Components(current)));

        return new FirmwareSmokeReport(
            phase,
            DateTimeOffset.UtcNow,
            checks,
            ManualChecks);
    }

    private static void AddFirmwareVersionChecks(
        ICollection<FirmwareSmokeCheck> checks,
        FirmwareIdentity baseline,
        FirmwareIdentity current,
        IReadOnlySet<FirmwareComponent> expectedChangedComponents,
        IReadOnlySet<FirmwareComponent> requiredReadableComponents)
    {
        IReadOnlyDictionary<FirmwareComponent, string> baselineVersions =
            FirmwareVersions(baseline);
        IReadOnlyDictionary<FirmwareComponent, string> currentVersions =
            FirmwareVersions(current);
        foreach (FirmwareComponent component in Enum.GetValues<FirmwareComponent>())
        {
            string name = FirmwareComponentName(component);
            string baselineVersion = baselineVersions[component];
            string currentVersion = currentVersions[component];
            bool expectedToChange = expectedChangedComponents.Contains(component);
            bool requiresReadable = requiredReadableComponents.Contains(component);
            bool baselineReadable = !IsUnavailable(baselineVersion);
            bool currentReadable = !IsUnavailable(currentVersion);
            bool passed = expectedToChange
                ? baselineReadable
                    && currentReadable
                    && !string.Equals(
                        baselineVersion,
                        currentVersion,
                        StringComparison.Ordinal)
                : (!requiresReadable || baselineReadable && currentReadable)
                    && string.Equals(
                        baselineVersion,
                        currentVersion,
                        StringComparison.Ordinal);
            checks.Add(Check(
                $"firmware.{component.ToString().ToLowerInvariant()}",
                expectedToChange
                    ? $"{name} version changed and remains readable"
                    : requiresReadable
                        ? $"{name} version remains readable and unchanged"
                    : $"{name} version is unchanged",
                passed,
                expectedToChange
                    ? baselineReadable
                        ? $"A readable version different from {baselineVersion}"
                        : "A readable baseline and a different readable current version"
                    : requiresReadable && !baselineReadable
                        ? "The same readable version in both snapshots"
                    : baselineVersion,
                currentVersion));
        }

        if (expectedChangedComponents.Contains(FirmwareComponent.Vbios)
            || requiredReadableComponents.Contains(FirmwareComponent.Vbios))
        {
            bool baselineGpuReadable = !IsUnavailable(baseline.VbiosGpuName);
            bool currentGpuReadable = !IsUnavailable(current.VbiosGpuName);
            checks.Add(Check(
                "firmware.vbios-gpu",
                "VBIOS GPU identity remains readable and unchanged",
                baselineGpuReadable
                    && currentGpuReadable
                    && string.Equals(
                        baseline.VbiosGpuName,
                        current.VbiosGpuName,
                        StringComparison.Ordinal),
                baselineGpuReadable
                    ? baseline.VbiosGpuName
                    : "The same readable GPU identity in both snapshots",
                current.VbiosGpuName));
        }
    }

    private static void RequireComponents(
        IReadOnlySet<FirmwareComponent> components,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
        {
            throw new ArgumentException(
                "At least one firmware component is required.",
                parameterName);
        }
    }

    private static IReadOnlyDictionary<FirmwareComponent, string> FirmwareVersions(
        FirmwareIdentity firmware) =>
        new Dictionary<FirmwareComponent, string>
        {
            [FirmwareComponent.Bios] = firmware.Bios,
            [FirmwareComponent.Ec] = firmware.Ec,
            [FirmwareComponent.Mcu] = firmware.Mcu,
            [FirmwareComponent.Vbios] = firmware.Vbios,
        };

    private static string FirmwareInventory(FirmwareIdentity firmware) =>
        string.Join(
            "; ",
            FirmwareVersions(firmware)
                .Select(version => version.Key == FirmwareComponent.Vbios
                    ? $"VBIOS={version.Value} (GPU={firmware.VbiosGpuName})"
                    : $"{FirmwareComponentName(version.Key)}={version.Value}")
                .Concat(
                [
                    $"Thunderbolt={firmware.Thunderbolt} ({firmware.ThunderboltAccess ?? "Unavailable"})",
                    $"Touchpad={firmware.Touchpad} ({firmware.TouchpadAccess ?? "Unavailable"})",
                ]));

    private static string FirmwareComponentName(FirmwareComponent component) =>
        component switch
        {
            FirmwareComponent.Bios => "BIOS",
            FirmwareComponent.Ec => "EC",
            FirmwareComponent.Mcu => "MCU",
            FirmwareComponent.Vbios => "VBIOS",
            _ => component.ToString(),
        };

    private static List<FirmwareSmokeCheck> CommonHealthChecks(
        FirmwareSmokeSnapshot snapshot)
    {
        FirmwareSmokeCapabilityExpectation expected = ExpectedCapabilities(
            snapshot.TargetModel);
        string expectedHyperBoostAccess = ExpectedHyperBoostAccess(
            snapshot.TargetModel,
            snapshot.Firmware.Bios);
        string expectedHyperBoostWithoutCoolingPadAccess =
            ExpectedHyperBoostWithoutCoolingPadAccess(snapshot.TargetModel);
        HyperBoostBaseModeCapabilities? expectedHyperBoostBaseModes =
            ExpectedHyperBoostBaseModes(snapshot.TargetModel, snapshot.Firmware.Bios);
        var checks = new List<FirmwareSmokeCheck>
        {
            Check(
                "target.model",
                "Snapshot targets the supported laptop",
                RazerBladeModelCatalog.Find(snapshot.TargetModel) is not null,
                "RZ09-0581 or RZ09-0528",
                snapshot.TargetModel),
            Check(
                "device.verified",
                "Exact device verification succeeded",
                snapshot.DeviceVerified,
                "True",
                snapshot.DeviceVerified.ToString()),
            Check(
                "device.writes",
                "Production writes are not blocked",
                !snapshot.WritesBlocked,
                "False",
                snapshot.WritesBlocked.ToString()),
            Check(
                "device.controllers",
                "No competing Razer controller is detected",
                snapshot.PotentialControllers.Count == 0,
                "None",
                List(snapshot.PotentialControllers)),
            Check(
                "device.controller-detection",
                "Controller detection completed without warning",
                string.IsNullOrWhiteSpace(snapshot.ControllerDetectionWarning),
                "None",
                snapshot.ControllerDetectionWarning ?? "None"),
            Check(
                "device.external-changes",
                "No unexplained external control changes are present",
                snapshot.ExternalChanges.Count == 0,
                "None",
                List(snapshot.ExternalChanges)),
            Check(
                "device.unconfirmed-writes",
                "No hardware writes are awaiting confirmation",
                snapshot.UnconfirmedWrites.Count == 0,
                "None",
                List(snapshot.UnconfirmedWrites)),
            Check(
                "power.source",
                "Laptop is on the full-power AC adapter",
                snapshot.Controls.ActivePowerSource == "PluggedIn",
                "PluggedIn",
                snapshot.Controls.ActivePowerSource),
            Check(
                "capabilities.performance",
                "All approved performance presets are admitted",
                snapshot.Capabilities.PerformancePresets.SequenceEqual(
                    expected.PerformancePresets),
                string.Join(", ", expected.PerformancePresets),
                List(snapshot.Capabilities.PerformancePresets)),
            Check(
                "capabilities.fans",
                "All approved fan modes are admitted",
                snapshot.Capabilities.FanModes.SequenceEqual(expected.FanModes),
                string.Join(", ", expected.FanModes),
                List(snapshot.Capabilities.FanModes)),
            Check(
                "capabilities.lighting",
                "All approved lighting effects are admitted",
                snapshot.Capabilities.LightingEffects.SequenceEqual(
                    expected.LightingEffects),
                string.Join(", ", expected.LightingEffects),
                List(snapshot.Capabilities.LightingEffects)),
            Check(
                "capabilities.charge-limits",
                "All approved charge limits are admitted",
                snapshot.Capabilities.ChargeLimits.SequenceEqual(expected.ChargeLimits),
                string.Join(", ", expected.ChargeLimits),
                List(snapshot.Capabilities.ChargeLimits)),
            Check(
                "capabilities.battery",
                "Battery protection-off and temporary-full-charge are admitted",
                snapshot.Capabilities.SupportsBatteryProtectionOff
                    && snapshot.Capabilities.SupportsTemporaryFullCharge,
                "Both supported",
                $"ProtectionOff={snapshot.Capabilities.SupportsBatteryProtectionOff}; " +
                $"TemporaryFullCharge={snapshot.Capabilities.SupportsTemporaryFullCharge}"),
            Check(
                "capabilities.logo",
                "All approved lid-logo modes are admitted",
                snapshot.Capabilities.LogoLightingModes.SequenceEqual(expected.LogoLightingModes),
                string.Join(", ", expected.LogoLightingModes),
                List(snapshot.Capabilities.LogoLightingModes)),
            Check(
                "capabilities.laptop-controls",
                "All approved laptop controls are admitted",
                snapshot.Capabilities.OneShotLaptopControls.SequenceEqual(
                    expected.OneShotLaptopControls),
                string.Join(", ", expected.OneShotLaptopControls),
                List(snapshot.Capabilities.OneShotLaptopControls)),
            Check(
                "capabilities.special-keys",
                "Special-key access matches the exact model",
                string.Equals(
                    snapshot.Capabilities.SpecialKeyBindingsAccess,
                    expected.SpecialKeyBindingsAccess,
                    StringComparison.Ordinal),
                expected.SpecialKeyBindingsAccess,
                snapshot.Capabilities.SpecialKeyBindingsAccess ?? "Unavailable"),
            Check(
                "capabilities.special-key-slots",
                "Writable special-key slots match the exact model",
                string.Equals(
                    snapshot.Capabilities.WritableSpecialKeySlots,
                    expected.WritableSpecialKeySlots,
                    StringComparison.Ordinal),
                expected.WritableSpecialKeySlots,
                snapshot.Capabilities.WritableSpecialKeySlots ?? "Unavailable"),
            Check(
                "capabilities.lamp-array",
                "Windows LampArray access matches the exact model",
                string.Equals(
                    snapshot.Capabilities.LampArrayLightingAccess ?? "Unavailable",
                    expected.LampArrayLightingAccess,
                    StringComparison.Ordinal),
                expected.LampArrayLightingAccess,
                snapshot.Capabilities.LampArrayLightingAccess ?? "Unavailable"),
            Check(
                "capabilities.hyper-boost",
                "HyperBoost access matches captured model and firmware evidence",
                string.Equals(
                    snapshot.Capabilities.HyperBoostAccess ?? "Unavailable",
                    expectedHyperBoostAccess,
                    StringComparison.Ordinal),
                expectedHyperBoostAccess,
                snapshot.Capabilities.HyperBoostAccess ?? "Unavailable"),
            Check(
                "capabilities.hyper-boost-without-cooling-pad",
                "Pad-less HyperBoost access matches reviewed evidence",
                string.Equals(
                    snapshot.Capabilities.HyperBoostWithoutCoolingPadAccess
                        ?? "Unavailable",
                    expectedHyperBoostWithoutCoolingPadAccess,
                    StringComparison.Ordinal),
                expectedHyperBoostWithoutCoolingPadAccess,
                snapshot.Capabilities.HyperBoostWithoutCoolingPadAccess
                    ?? "Unavailable"),
            Check(
                "capabilities.hyper-boost-base-modes",
                "HyperBoost base-mode access matches direct-transition evidence",
                snapshot.Capabilities.HyperBoostBaseModes == expectedHyperBoostBaseModes,
                HyperBoostBaseModeSummary(expectedHyperBoostBaseModes),
                HyperBoostBaseModeSummary(snapshot.Capabilities.HyperBoostBaseModes)),
            Check(
                "readback.performance",
                "Performance readback is available",
                snapshot.Controls.Performance is not null,
                "Available",
                snapshot.Controls.Performance ?? "Unavailable"),
            Check(
                "readback.fans",
                "Fan-mode readback is available",
                snapshot.Controls.Fans is not null,
                "Available",
                snapshot.Controls.Fans ?? "Unavailable"),
            Check(
                "readback.battery",
                "Battery readback is complete",
                snapshot.Controls.BatteryProtectionEnabled.HasValue
                    && snapshot.Controls.TemporaryFullCharge.HasValue
                    && (snapshot.Controls.BatteryProtectionEnabled == false
                        || snapshot.Controls.ChargeLimitPercent.HasValue),
                "Complete",
                BatteryState(snapshot)),
            Check(
                "readback.logo",
                "Lid-logo readback is available",
                snapshot.Controls.LogoLighting is not null,
                "Available",
                snapshot.Controls.LogoLighting ?? "Unavailable"),
            Check(
                "readback.laptop-controls",
                "Startup, function-key, Gaming, and Driver-mode readbacks are available",
                snapshot.Controls.StartupAnimationEnabled.HasValue
                    && snapshot.Controls.FunctionKeyPrimary is not null
                    && snapshot.Controls.GamingModeEnabled.HasValue
                    && snapshot.Controls.KeyboardDriverModeActive.HasValue,
                "All available",
                LaptopControlState(snapshot) +
                $"; Driver={Value(snapshot.Controls.KeyboardDriverModeActive)}"),
            Check(
                "telemetry.battery-power",
                "Windows battery-power reading is available",
                snapshot.LiveReadings.BatteryPowerFlowMilliwatts.HasValue,
                "Available",
                Value(snapshot.LiveReadings.BatteryPowerFlowMilliwatts)),
            Check(
                "telemetry.fans",
                "Both firmware fan-speed readings are available",
                snapshot.LiveReadings.Fan1Rpm.HasValue
                    && snapshot.LiveReadings.Fan2Rpm.HasValue,
                "Fan 1 and Fan 2 available",
                $"Fan1={Value(snapshot.LiveReadings.Fan1Rpm)}; " +
                $"Fan2={Value(snapshot.LiveReadings.Fan2Rpm)}"),
            Check(
                "telemetry.cpu-temperature",
                "Firmware CPU temperature is available",
                snapshot.LiveReadings.CpuTemperatureCelsius.HasValue,
                "Available",
                Value(snapshot.LiveReadings.CpuTemperatureCelsius)),
            ProcessCheck(
                "runtime.service",
                "One OpenBlade Service process is running",
                snapshot.Runtime.ServiceProcessCount),
            ProcessCheck(
                "runtime.session-agent",
                "One OpenBlade Session Agent process is running",
                snapshot.Runtime.SessionAgentProcessCount),
        };

        if (RazerBladeModelCatalog.Find(snapshot.TargetModel)?.ModelNumber
            == RazerBladeModelCatalog.Rz090528ModelNumber)
        {
            checks.Add(Check(
                "capabilities.custom-performance",
                "Exact-model Custom performance combinations are retained",
                snapshot.Capabilities.CustomPerformance?.AllowedCombinations.SetEquals(
                    ExpectedRz090528CustomPerformance) == true,
                $"{ExpectedRz090528CustomPerformance.Count} exact combinations",
                snapshot.Capabilities.CustomPerformance is { } custom
                    ? $"{custom.AllowedCombinations.Count} combinations"
                    : "Unavailable"));
            checks.Add(Check(
                "capabilities.cpu-power-limit",
                "Observed CPU power-limit range is retained",
                snapshot.Capabilities.CpuPowerLimit == ExpectedRz090528CpuPowerLimit,
                ExpectedRz090528CpuPowerLimit.ToString(),
                snapshot.Capabilities.CpuPowerLimit?.ToString() ?? "Unavailable"));
            checks.Add(Check(
                "capabilities.cpu-voltage-optimizer",
                "Observed CPU voltage-optimizer range is retained",
                snapshot.Capabilities.CpuVoltageOptimizer
                    == ExpectedRz090528CpuVoltageOptimizer,
                ExpectedRz090528CpuVoltageOptimizer.ToString(),
                snapshot.Capabilities.CpuVoltageOptimizer?.ToString() ?? "Unavailable"));
            checks.Add(Check(
                "capabilities.gpu-overclock",
                "Observed GPU overclock policy is retained",
                snapshot.Capabilities.GpuOverclock == ExpectedRz090528GpuOverclock,
                ExpectedRz090528GpuOverclock.ToString(),
                snapshot.Capabilities.GpuOverclock?.ToString() ?? "Unavailable"));
            checks.Add(Check(
                "capabilities.power-source-behaviors",
                "Observed battery and USB-C restrictions are retained",
                snapshot.Capabilities.ObservedPowerSourceBehaviors.SequenceEqual(
                    ExpectedRz090528PowerSourceBehaviors),
                $"{ExpectedRz090528PowerSourceBehaviors.Count} exact observations",
                $"{snapshot.Capabilities.ObservedPowerSourceBehaviors.Count} observations"));
        }

        checks.Add(new FirmwareSmokeCheck(
            "runtime.tray",
            "One OpenBlade Tray process is preferred",
            snapshot.Runtime.TrayProcessCount == 1
                ? FirmwareSmokeOutcome.Passed
                : FirmwareSmokeOutcome.Warning,
            "1",
            snapshot.Runtime.TrayProcessCount.ToString()));
        checks.Add(new FirmwareSmokeCheck(
            "telemetry.gpu-temperature",
            "GPU temperature is optional while the NVIDIA GPU is asleep",
            snapshot.LiveReadings.GpuTemperatureCelsius.HasValue
                ? FirmwareSmokeOutcome.Passed
                : FirmwareSmokeOutcome.Warning,
            "Available when the GPU is active",
            Value(snapshot.LiveReadings.GpuTemperatureCelsius)));
        return checks;
    }

    private static FirmwareSmokeCheck ProcessCheck(
        string id,
        string description,
        int actual) =>
        Check(id, description, actual == 1, "1", actual.ToString());

    private static FirmwareSmokeCheck Check(
        string id,
        string description,
        bool passed,
        string expected,
        string actual) =>
        new(
            id,
            description,
            passed ? FirmwareSmokeOutcome.Passed : FirmwareSmokeOutcome.Failed,
            expected,
            actual);

    private static bool IsUnavailable(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);

    private static string BatteryState(FirmwareSmokeSnapshot snapshot) =>
        $"Enabled={Value(snapshot.Controls.BatteryProtectionEnabled)}; " +
        $"Limit={Value(snapshot.Controls.ChargeLimitPercent)}; " +
        $"TemporaryFullCharge={Value(snapshot.Controls.TemporaryFullCharge)}";

    private static string LaptopControlState(FirmwareSmokeSnapshot snapshot) =>
        $"Startup={Value(snapshot.Controls.StartupAnimationEnabled)}; " +
        $"Function={snapshot.Controls.FunctionKeyPrimary ?? "Unavailable"}; " +
        $"Gaming={Value(snapshot.Controls.GamingModeEnabled)}";

    private static string Components(FirmwareSmokeSnapshot snapshot) =>
        string.Join(
            "; ",
            snapshot.Runtime.InstalledComponents.Select(component =>
                $"{component.Name}={component.ProductVersion ?? "Unavailable"}"));

    private static string Summarize(FirmwareSmokeCapabilities capabilities) =>
        $"Performance={capabilities.PerformancePresets.Count}; " +
        $"Fans={capabilities.FanModes.Count}; " +
        $"Lighting={capabilities.LightingEffects.Count}; " +
        $"ObservedLighting={capabilities.ObservedLightingEffects.Count}; " +
        $"SpecialKeys={capabilities.SpecialKeyBindingsAccess ?? "Unavailable"}; " +
        $"WritableSpecialKeySlots={capabilities.WritableSpecialKeySlots ?? "Unavailable"}; " +
        $"LampArray={capabilities.LampArrayLightingAccess ?? "Unavailable"}; " +
        $"HyperBoost={capabilities.HyperBoostAccess ?? "Unavailable"}; " +
        $"HyperBoostWithoutCoolingPad=" +
        $"{capabilities.HyperBoostWithoutCoolingPadAccess ?? "Unavailable"}; " +
        $"HyperBoostBases={HyperBoostBaseModeSummary(capabilities.HyperBoostBaseModes)}; " +
        $"ChargeLimits={capabilities.ChargeLimits.Count}; " +
        $"Logo={capabilities.LogoLightingModes.Count}; " +
        $"Laptop={capabilities.OneShotLaptopControls.Count}; " +
        $"CustomPerformance={capabilities.CustomPerformance?.AllowedCombinations.Count ?? 0}; " +
        $"CpuPowerLimit={capabilities.CpuPowerLimit?.Access.ToString() ?? "Unavailable"}; " +
        $"CpuVoltageOptimizer=" +
        $"{capabilities.CpuVoltageOptimizer?.Access.ToString() ?? "Unavailable"}; " +
        $"GpuOverclock={capabilities.GpuOverclock?.Access.ToString() ?? "Unavailable"}; " +
        $"PowerSourceBehaviors={capabilities.ObservedPowerSourceBehaviors.Count}";

    private static bool CapabilitiesEqual(
        FirmwareSmokeCapabilities left,
        FirmwareSmokeCapabilities right) =>
        left.PerformancePresets.SequenceEqual(right.PerformancePresets)
        && left.FanModes.SequenceEqual(right.FanModes)
        && left.LightingEffects.SequenceEqual(right.LightingEffects)
        && left.ObservedLightingEffects.SequenceEqual(right.ObservedLightingEffects)
        && string.Equals(
            left.SpecialKeyBindingsAccess,
            right.SpecialKeyBindingsAccess,
            StringComparison.Ordinal)
        && string.Equals(
            left.WritableSpecialKeySlots,
            right.WritableSpecialKeySlots,
            StringComparison.Ordinal)
        && string.Equals(
            left.LampArrayLightingAccess,
            right.LampArrayLightingAccess,
            StringComparison.Ordinal)
        && string.Equals(
            left.HyperBoostAccess,
            right.HyperBoostAccess,
            StringComparison.Ordinal)
        && string.Equals(
            left.HyperBoostWithoutCoolingPadAccess,
            right.HyperBoostWithoutCoolingPadAccess,
            StringComparison.Ordinal)
        && left.HyperBoostBaseModes == right.HyperBoostBaseModes
        && left.ChargeLimits.SequenceEqual(right.ChargeLimits)
        && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
        && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge
        && left.LogoLightingModes.SequenceEqual(right.LogoLightingModes)
        && left.OneShotLaptopControls.SequenceEqual(right.OneShotLaptopControls)
        && CustomPerformanceEqual(left.CustomPerformance, right.CustomPerformance)
        && left.CpuPowerLimit == right.CpuPowerLimit
        && left.CpuVoltageOptimizer == right.CpuVoltageOptimizer
        && left.GpuOverclock == right.GpuOverclock
        && left.ObservedPowerSourceBehaviors.SequenceEqual(
            right.ObservedPowerSourceBehaviors);

    private static bool CustomPerformanceEqual(
        CustomPerformanceCapabilities? left,
        CustomPerformanceCapabilities? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } leftValue, { } rightValue) =>
                leftValue.AllowedCombinations.SetEquals(rightValue.AllowedCombinations),
            _ => false,
        };

    private static FirmwareSmokeCapabilityExpectation ExpectedCapabilities(
        string modelNumber) =>
        RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber switch
        {
            RazerBladeModelCatalog.OpenBladeTargetModelNumber => Rz090581Expectation,
            RazerBladeModelCatalog.Rz090528ModelNumber => Rz090528Expectation,
            _ => new(
                [],
                [],
                [],
                [],
                [],
                [],
                "Unavailable",
                SpecialKeySlotSet.None.ToString(),
                "Unavailable"),
        };

    private static string ExpectedHyperBoostAccess(
        string modelNumber,
        string biosVersion) => RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber switch
        {
            RazerBladeModelCatalog.OpenBladeTargetModelNumber =>
                DeviceFeatureAccess.ReadWrite.ToString(),
            RazerBladeModelCatalog.OpenBladeTargetModelNumber or
            RazerBladeModelCatalog.Rz090528ModelNumber =>
                DeviceFeatureAccess.ObservedOnly.ToString(),
            _ => "Unavailable",
        };

    private static string ExpectedHyperBoostWithoutCoolingPadAccess(
        string modelNumber) => RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber
            == RazerBladeModelCatalog.OpenBladeTargetModelNumber
                ? DeviceFeatureAccess.ObservedOnly.ToString()
                : "Unavailable";

    private static HyperBoostBaseModeCapabilities? ExpectedHyperBoostBaseModes(
        string modelNumber,
        string biosVersion) =>
        RazerBladeModelCatalog.Find(modelNumber)?.ModelNumber
            == RazerBladeModelCatalog.OpenBladeTargetModelNumber
                ? new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ObservedOnly,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ObservedOnly)
                : null;

    private static string HyperBoostBaseModeSummary(
        HyperBoostBaseModeCapabilities? capabilities) => capabilities is null
            ? "Unavailable"
            : $"Silent={capabilities.Silent},Balanced={capabilities.Balanced},Performance={capabilities.Performance}";

    private static string List<T>(IReadOnlyList<T> values) =>
        values.Count == 0 ? "None" : string.Join(", ", values);

    private static string Value<T>(T? value)
        where T : struct =>
        value?.ToString() ?? "Unavailable";

    private sealed record FirmwareSmokeCapabilityExpectation(
        IReadOnlyList<string> PerformancePresets,
        IReadOnlyList<string> FanModes,
        IReadOnlyList<string> LightingEffects,
        IReadOnlyList<int> ChargeLimits,
        IReadOnlyList<string> LogoLightingModes,
        IReadOnlyList<string> OneShotLaptopControls,
        string SpecialKeyBindingsAccess,
        string WritableSpecialKeySlots,
        string LampArrayLightingAccess);
}
