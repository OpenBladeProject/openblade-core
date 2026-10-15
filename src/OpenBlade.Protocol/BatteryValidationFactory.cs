using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record AdmittedServiceModel(
    ExpectedHostIdentity HostIdentity,
    HidHandleIdentity HandleIdentity);

public static class BatteryValidationFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TemporaryFullChargeValidationFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Rz090528BrightnessFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(30);

    internal static IProductionBatteryPortFactory
        CreateWindowsProductionBatteryForService() =>
        WindowsHostIdentitySource.IsExactRz090528WindowsHost()
            ? CreateWindowsRz090528ProductionBattery()
            : ProductionBatteryFactory.CreateWindows();

    internal static IProductionBatteryPortFactory
        CreateWindowsProductionBatteryForPadlessHyperBoostLifecycleValidation()
    {
        PadlessHyperBoostLifecycleTarget? target = PadlessHyperBoostLifecycleTarget.ForCurrentWindowsHost();
        RequireLifecycleReadiness(target);
        return target!.Model == "RZ09-0528"
            ? CreateWindowsRz090528ProductionBattery(compatibilityPolicy: null, allowPadlessLifecycleValidation: true)
            : ProductionBatteryFactory.CreateWindowsForPadlessHyperBoostLifecycleValidation();
    }

    internal static void RequireLifecycleReadiness(PadlessHyperBoostLifecycleTarget? target)
    {
        if (target is null || !target.Readiness.LifecycleValidationReady)
        {
            throw new UnvalidatedCommandException(
                "Developer lifecycle validation remains blocked until this exact model's non-lifecycle evidence is reviewed.");
        }
    }

    internal static IProductionBatteryPortFactory
        CreateWindowsProductionBatteryForService(
            FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return WindowsHostIdentitySource.IsRz090528WindowsHost(
                allowUntestedFirmware: true)
            ? CreateWindowsRz090528ProductionBattery(compatibilityPolicy)
            : ProductionBatteryFactory.CreateWindows(compatibilityPolicy);
    }

    internal static IProductionBatteryPortFactory
        CreateWindowsRz090528ProductionBattery() =>
        CreateWindowsRz090528ProductionBattery(compatibilityPolicy: null);

    private static IProductionBatteryPortFactory
        CreateWindowsRz090528ProductionBattery(
            FirmwareCompatibilityPolicy? compatibilityPolicy,
            bool allowPadlessLifecycleValidation = false)
    {
        if (allowPadlessLifecycleValidation)
        {
            PadlessHyperBoostLifecycleTarget? selected = PadlessHyperBoostLifecycleTarget.ForCurrentWindowsHost();
            if (selected?.Model != "RZ09-0528")
            {
                throw new UnvalidatedCommandException("The exact RZ09-0528 lifecycle target is unavailable.");
            }
            RequireLifecycleReadiness(selected);
        }
        var target = new DeviceIdentity(
            Model: "RZ09-0528",
            VendorId: 0x1532,
            ProductId: 0x02C6,
            BiosVersion: "2.02");
        DeviceCommandCatalog catalog = DeviceCommandCatalog.Create(
            target,
            [
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBalancedFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBalancedSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceSilentFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x05, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceSilentSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x05, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBoostFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x02, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBoostSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x02, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceCustomFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x04, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceCustomSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x04, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                // On battery, Synapse on this model offers Battery Saver (mode 3)
                // and Balanced (mode 6); both pairs were captured from Synapse on 02C6.
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBatterySaverFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x03, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBatterySaverSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x03, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBatteryBalancedFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x06, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.PerformanceBatteryBalancedSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x06, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomCpuLowAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x01, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomCpuMediumAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x01, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomCpuHighAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x01, 0x02],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomGpuLowAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x02, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomGpuMediumAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x02, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.CustomGpuHighAction,
                    CommandClass: 0x0D,
                    CommandId: 0x07,
                    Payload: [0x01, 0x02, 0x02],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.FanMaximumStateFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x01, 0x00, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.FanMaximumStateSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x02,
                    Payload: [0x01, 0x02, 0x00, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.FanMaximumSpeedFirstAction,
                    CommandClass: 0x0D,
                    CommandId: 0x01,
                    Payload: [0x01, 0x01, 0x32],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.FanMaximumSpeedSecondAction,
                    CommandClass: 0x0D,
                    CommandId: 0x01,
                    Payload: [0x01, 0x02, 0x32],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit50Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xB2],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit55Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xB7],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit60Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xBC],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit65Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xC1],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit70Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xC6],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit75Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xCB],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.Limit80Action,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0xD0],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.ProtectionOffAction,
                    CommandClass: 0x07,
                    CommandId: 0x12,
                    Payload: [0x50],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.StartupAnimationOnAction,
                    CommandClass: 0x0F,
                    CommandId: 0x18,
                    Payload: [0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x00, 0x00],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.StartupAnimationOffAction,
                    CommandClass: 0x0F,
                    CommandId: 0x18,
                    Payload: [0x00, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x00, 0x01],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.FunctionKeysPrimaryAction,
                    CommandClass: 0x02,
                    CommandId: 0x06,
                    Payload: [0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x00, 0x00],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.MultimediaKeysPrimaryAction,
                    CommandClass: 0x02,
                    CommandId: 0x06,
                    Payload: [0x00, 0x01],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x00, 0x01],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.GamingModeOnAction,
                    CommandClass: 0x00,
                    CommandId: 0x08,
                    Payload: [0x01, 0x00, 0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x32, 0x0A, 0x00, 0x00],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.GamingModeOffAction,
                    CommandClass: 0x00,
                    CommandId: 0x08,
                    Payload: [0x00, 0x00, 0x00, 0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: false,
                    Restored: true)
                {
                    OneShotOnly = true,
                    ExpectedResponsePayload = [0x32, 0x0A, 0x00, 0x00],
                },
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.TemporaryEnableAction,
                    CommandClass: 0x07,
                    CommandId: 0x0F,
                    Payload: [0x04],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
                new CapturedCommandFixture(
                    target,
                    ProductionBatteryPort.TemporaryCancelAction,
                    CommandClass: 0x07,
                    CommandId: 0x0F,
                    Payload: [0x00],
                    Captured: true,
                    Decoded: true,
                    Applied: true,
                    ReadBack: true,
                    Restored: true),
            ]);
        ProductionBatteryReadEvidence readEvidence =
            CreateRz090528ProductionBatteryReadEvidence();
        ReadOnlyMemory<byte> hyperBoostFixture = HyperBoostFixtureSource.ReadRz090528()
            ?? throw new UnvalidatedCommandException("The exact RZ09-0528 HyperBoost fixture is absent.");
        DeviceCommandCatalog hyperBoostCatalog = CapturedFixtureFile.LoadCatalog(
            hyperBoostFixture, TrustedFixtureHashRegistry.Rz090528HyperBoostProduction, target);
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            allowPadlessLifecycleValidation
                ? WindowsHostIdentitySource.Rz090528Identity with { FirmwareAdmission = FirmwareAdmissionScope.ExactVersions }
                : WindowsHostIdentitySource.Rz090528Identity,
            compatibilityPolicy);
        ControllerExclusivityPolicy exclusivity = allowPadlessLifecycleValidation
            ? ControllerExclusivityPolicy.Strict : ControllerExclusivityPolicy.Advisory;
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            exclusivity,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        return new ProductionBatteryFactory(
            catalog,
            readEvidence,
            static () => new ValidationRunContext(),
            settleDelay => new ValidationActorFactory(
                opener,
                new OverlappedFeatureOperation(native),
                guard,
                TimeProvider.System,
                DefaultExchangeTimeout,
                exclusivity,
                settleDelay),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            PendingDisposalRegistry.Shared,
            new FanSpeedRange(2000, 5000, 100),
            new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            new CustomPerformanceCapabilities(
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
            new CpuPowerLimitCapabilities(
                new DeviceNumericRange(45, 95, 1),
                DeviceFeatureAccess.ObservedOnly),
            new CpuVoltageOptimizerCapabilities(
                new DeviceNumericRange(-30, -1, 1),
                DeviceFeatureAccess.ObservedOnly)
            {
                SupportsEnableDisable = true,
            },
            new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite)
            {
                SupportsEnableDisable = true,
            },
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
            AdapterPowerProtocol.SynapseAdapterStates,
            Rz090528CommandSessionGate.Shared,
            hyperBoostAccess: DeviceFeatureAccess.ReadWrite,
            // Production admits pad-less writes only through the exact 2025 admission manifest;
            // the developer lifecycle composition keeps its separate validation-only path.
            hyperBoostWithoutCoolingPadAccess: allowPadlessLifecycleValidation
                ? DeviceFeatureAccess.ObservedOnly
                : Rz090528HyperBoostWithoutCoolingPadAdmissionSource.ReadGates().WriteAdmitted
                    ? DeviceFeatureAccess.ReadWrite
                    : null,
            // Direct mode-7 entry and exact restoration were confirmed from Silent, Balanced and
            // Performance with Automatic fans on the exact 02C6 laptop.
            hyperBoostBaseModes: new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite)
            {
                Custom = DeviceFeatureAccess.ReadWrite,
            },
            hyperBoostCatalog: hyperBoostCatalog,
            hyperBoostEligibility: new WindowsHyperBoostEligibilitySource(native),
            allowPadlessHyperBoostLifecycleValidation: allowPadlessLifecycleValidation,
            padlessHyperBoostLifecycleBaseMode: PerformancePreset.Performance);
    }

    internal static ProductionBatteryReadEvidence
        CreateRz090528ProductionBatteryReadEvidence() =>
        new(
            new HashSet<ProductionBatteryQueryCapability>
            {
                ProductionBatteryQueryCapability.TemporaryFullCharge,
                ProductionBatteryQueryCapability.AdapterPower,
                ProductionBatteryQueryCapability.McuVersion,
                ProductionBatteryQueryCapability.StartupAnimation,
                ProductionBatteryQueryCapability.FunctionKeyPrimary,
                ProductionBatteryQueryCapability.GamingMode,
            });

    internal static IProductionLightingPortFactory
        CreateWindowsProductionLightingForService() =>
        WindowsHostIdentitySource.IsExactRz090528WindowsHost()
            ? CreateWindowsRz090528ProductionLighting()
            : ProductionLightingFactory.CreateWindows();

    internal static IProductionLightingPortFactory
        CreateWindowsProductionLightingForService(
            FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return WindowsHostIdentitySource.IsRz090528WindowsHost(
                allowUntestedFirmware: true)
            ? CreateWindowsRz090528ProductionLighting(compatibilityPolicy)
            : ProductionLightingFactory.CreateWindows(compatibilityPolicy);
    }

    internal static IProductionLightingPortFactory
        CreateWindowsRz090528ProductionLighting() =>
        CreateWindowsRz090528ProductionLighting(compatibilityPolicy: null);

    private static IProductionLightingPortFactory
        CreateWindowsRz090528ProductionLighting(
            FirmwareCompatibilityPolicy? compatibilityPolicy)
        => CreateWindowsRz090528BrightnessLighting(
            staticEffectAdmitted: true,
            spectrumEffectAdmitted: true,
            allSynapseEffectsAdmitted: true,
            compatibilityPolicy: compatibilityPolicy);

    internal static IProductionLightingPortFactory
        CreateWindowsRz090528BrightnessLighting()
        => CreateWindowsRz090528BrightnessLighting(
            staticEffectAdmitted: false,
            spectrumEffectAdmitted: false,
            allSynapseEffectsAdmitted: false,
            compatibilityPolicy: null);

    internal static IProductionLightingPortFactory
        CreateWindowsRz090528LightingValidation()
        => CreateWindowsRz090528BrightnessLighting(
            staticEffectAdmitted: true,
            spectrumEffectAdmitted: true,
            allSynapseEffectsAdmitted: true,
            compatibilityPolicy: null);

    private static IProductionLightingPortFactory
        CreateWindowsRz090528BrightnessLighting(
            bool staticEffectAdmitted,
            bool spectrumEffectAdmitted,
            bool allSynapseEffectsAdmitted,
            FirmwareCompatibilityPolicy? compatibilityPolicy)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity,
            compatibilityPolicy);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            ControllerExclusivityPolicy.Advisory,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        var actorFactory = new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            DefaultExchangeTimeout,
            ControllerExclusivityPolicy.Advisory,
            Rz090528BrightnessFeatureReportSettleDelay);
        return new Rz090528BrightnessLightingFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            staticEffectAdmitted,
            spectrumEffectAdmitted,
            allSynapseEffectsAdmitted);
    }

    public static IBatteryValidationRunFactory CreateWindows()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindows(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static Rz090528BatteryValidationFactory
        CreateWindowsRz090528BatteryValidation()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            ControllerExclusivityPolicy.Advisory,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        var actorFactory = new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            DefaultExchangeTimeout,
            ControllerExclusivityPolicy.Advisory,
            BatteryFeatureReportSettleDelay);
        return new Rz090528BatteryValidationFactory(
            static () => new ValidationRunContext(),
            actorFactory);
    }

    public static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsQuerySequence(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    public static ITemporaryFullChargeQueryRunFactory CreateWindowsTemporaryFullChargeQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsTemporaryFullChargeQuery(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static ITemporaryFullChargeQueryRunFactory
        CreateWindowsRz090528TemporaryFullChargeQuery()
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory);
        return new TemporaryFullChargeQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultCleanupDrainTimeout);
    }

    public static IFanRpmQueryRunFactory CreateWindowsFanRpmQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsFanRpmQuery(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static IFanRpmQueryRunFactory CreateWindowsRz090528FanRpmQuery()
        => CreateWindowsRz090528FanRpmQuery(compatibilityPolicy: null);

    private static IFanRpmQueryRunFactory CreateWindowsRz090528FanRpmQuery(
        FirmwareCompatibilityPolicy? compatibilityPolicy)
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory,
                compatibilityPolicy);
        return new FanRpmQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static IFanRpmQueryRunFactory CreateWindowsFanRpmForService() =>
        WindowsHostIdentitySource.IsExactRz090528WindowsHost()
            ? CreateWindowsRz090528FanRpmQuery()
            : CreateWindowsRz090581FanRpmForService(compatibilityPolicy: null);

    internal static IFanRpmQueryRunFactory CreateWindowsFanRpmForService(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        if (WindowsHostIdentitySource.IsRz090528WindowsHost(
                allowUntestedFirmware: true))
        {
            return CreateWindowsRz090528FanRpmQuery(compatibilityPolicy);
        }

        return CreateWindowsRz090581FanRpmForService(compatibilityPolicy);
    }

    internal static IFanRpmQueryRunFactory CreateWindowsRz090581FanRpmForService(
        FirmwareCompatibilityPolicy? compatibilityPolicy)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsFanRpmQuery(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            compatibilityPolicy,
            Rz090581FeatureCommandGate.Shared);
    }

    public static ILightingBrightnessQueryRunner CreateWindowsLightingBrightnessQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsLightingBrightnessQuery(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static ILightingBrightnessQueryRunner
        CreateWindowsRz090528LightingBrightnessQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var platform = new RuntimeWindowsPlatformEvidenceSource(registry);
        var processSource = new RunningProcessSource();
        var serviceSource = new WindowsServiceSource();
        var hardwareAccessGuard = new WindowsHardwareAccessGuard(
            processSource,
            serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            platform,
            WindowsHostIdentitySource.Rz090528Identity);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            ControllerExclusivityPolicy.Advisory,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        var actorFactory = new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            hardwareAccessGuard,
            TimeProvider.System,
            DefaultExchangeTimeout,
            ControllerExclusivityPolicy.Advisory,
            BatteryFeatureReportSettleDelay);
        return new LightingBrightnessQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    public static IPerformanceFanDiscriminatorQueryRunner
        CreateWindowsPerformanceFanDiscriminatorQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsPerformanceFanDiscriminatorQuery(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static IPerformanceFanDiscriminatorQueryRunner
        CreateWindowsRz090528PerformanceFanDiscriminatorQuery()
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory);
        return new PerformanceFanDiscriminatorQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static IPerformanceFanDiscriminatorQueryRunner
        CreateWindowsRz090528PowerSourceQuery()
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory);
        return new PerformanceFanDiscriminatorQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            powerSourceOnly: true);
    }

    internal static IPerformanceFanDiscriminatorQueryRunner
        CreateWindowsRz090581Bios400HyperBoostObservationQuery()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            ControllerExclusivityPolicy.Advisory);
        return new PerformanceFanDiscriminatorQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            hyperBoostObservationOnly: true);
    }


    public static IPerformanceFanSetterValidationRunner
        CreateWindowsPerformanceFanSetterValidation()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsPerformanceFanSetterValidation(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }


    internal static IPerformanceFanSetterValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidation() =>
            CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
                sustainedPlan: null,
                thermalTelemetrySource: null,
                externalWorkloadSource: null,
                adapterTransitionPlan: null,
                powerSourceBoundaryPlan: null);

    internal static IPerformanceFanSetterValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostSustainedValidation(
            ReviewedHyperBoostThermalPlan sustainedPlan,
            IHyperBoostThermalTelemetrySource thermalTelemetrySource) =>
            CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
                sustainedPlan
                    ?? throw new ArgumentNullException(nameof(sustainedPlan)),
                thermalTelemetrySource
                    ?? throw new ArgumentNullException(nameof(thermalTelemetrySource)),
                externalWorkloadSource: null,
                adapterTransitionPlan: null,
                powerSourceBoundaryPlan: null);

    internal static IPerformanceFanSetterValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostReviewedLoadValidation(
            ReviewedHyperBoostThermalPlan sustainedPlan,
            IHyperBoostThermalTelemetrySource thermalTelemetrySource,
            IHyperBoostExternalWorkloadSource externalWorkloadSource) =>
            CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
                sustainedPlan
                    ?? throw new ArgumentNullException(nameof(sustainedPlan)),
                thermalTelemetrySource
                    ?? throw new ArgumentNullException(nameof(thermalTelemetrySource)),
                externalWorkloadSource
                    ?? throw new ArgumentNullException(nameof(externalWorkloadSource)),
                adapterTransitionPlan: null,
                powerSourceBoundaryPlan: null);

    internal static IHyperBoostAdapterTransitionValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostAdapterTransitionValidation(
            ReviewedHyperBoostAdapterTransitionPlan adapterTransitionPlan) =>
            (IHyperBoostAdapterTransitionValidationRunner)
                CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
                    sustainedPlan: null,
                    thermalTelemetrySource: null,
                    externalWorkloadSource: null,
                    adapterTransitionPlan
                        ?? throw new ArgumentNullException(nameof(adapterTransitionPlan)),
                    powerSourceBoundaryPlan: null);

    internal static IHyperBoostPowerSourceBoundaryValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostPowerSourceBoundaryValidation(
            ReviewedHyperBoostPowerSourceBoundaryPlan powerSourceBoundaryPlan) =>
            (IHyperBoostPowerSourceBoundaryValidationRunner)
                CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
                    sustainedPlan: null,
                    thermalTelemetrySource: null,
                    externalWorkloadSource: null,
                    adapterTransitionPlan: null,
                    powerSourceBoundaryPlan
                        ?? throw new ArgumentNullException(
                            nameof(powerSourceBoundaryPlan)));

    private static IPerformanceFanSetterValidationRunner
        CreateWindowsRz090581Bios400PadAbsentHyperBoostSetterValidationCore(
            ReviewedHyperBoostThermalPlan? sustainedPlan,
            IHyperBoostThermalTelemetrySource? thermalTelemetrySource,
            IHyperBoostExternalWorkloadSource? externalWorkloadSource,
            ReviewedHyperBoostAdapterTransitionPlan? adapterTransitionPlan,
            ReviewedHyperBoostPowerSourceBoundaryPlan? powerSourceBoundaryPlan)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new ExpectedHostIdentity(
                "Razer",
                "Blade 16 - RZ09-0581",
                "RZ09-05819EN4",
                Array.AsReadOnly(new[] { "4.00" })));
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            ControllerExclusivityPolicy.Strict,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02E0,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: RazerReport.ReportLength + 1));
        var actorFactory = new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            DefaultExchangeTimeout,
            ControllerExclusivityPolicy.Strict,
            BatteryFeatureReportSettleDelay);
        var safetyGate = new PadAbsentHyperBoostSafetyGate(
            new WindowsCoolingPadPhysicalPresenceSource(native),
            DefaultExchangeTimeout);
        return new PerformanceFanSetterValidationRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            safetyGate: safetyGate,
            sustainedPlan: sustainedPlan,
            thermalTelemetrySource: thermalTelemetrySource,
            externalWorkloadSource: externalWorkloadSource,
            adapterTransitionPlan: adapterTransitionPlan,
            powerSourceBoundaryPlan: powerSourceBoundaryPlan);
    }

    public static IProtectionOffValidationRunFactory CreateWindowsProtectionOffValidation()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsProtectionOffValidation(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    public static ITemporaryFullChargeValidationRunFactory
        CreateWindowsTemporaryFullChargeValidation()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsTemporaryFullChargeValidation(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    internal static ITemporaryFullChargeValidationRunFactory
        CreateWindowsRz090528TemporaryFullChargeValidation()
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory);
        return new TemporaryFullChargeValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            TimeProvider.System,
            DefaultCleanupDrainTimeout);
    }

    public static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequenceForService()
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        return CreateWindowsQuerySequenceForService(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            native,
            new OverlappedFeatureOperation(native),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout);
    }

    public static BatteryValidationProbeResult ProbeWindows()
    {
        var registry = new WindowsRegistryValueSource();
        return ProbeWindows(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            new WindowsHidNative());
    }

    public static BatteryValidationProbeResult ProbeWindowsForService()
    {
        var registry = new WindowsRegistryValueSource();
        return ProbeWindowsForService(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            new WindowsHidNative());
    }

    internal static BatteryValidationProbeResult ProbeWindowsForService(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        var registry = new WindowsRegistryValueSource();
        return ProbeWindowsForService(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            new RunningProcessSource(),
            new WindowsServiceSource(),
            new WindowsHidNative(),
            compatibilityPolicy);
    }

    internal static BatteryValidationProbeResult ProbeWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native) =>
        ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Strict);

    // Every model the service admits. The service verifies the one whose BIOS
    // manufacturer and product name match, or the first when none does.
    // Diagnostics also use it to tell an unadmitted laptop from an admitted one.
    internal static IReadOnlyList<AdmittedServiceModel> AdmittedServiceModels { get; } =
    [
        new(
            WindowsHostIdentitySource.ProductionIdentity,
            WindowsRazerHidEvidenceEvaluator.ProductionExactIdentity),
        new(
            WindowsHostIdentitySource.Rz090528Identity,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91)),
    ];

    internal static BatteryValidationProbeResult ProbeWindowsForService(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null)
    {
        AdmittedServiceModel model = SelectServiceModel(registry);
        return ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Advisory,
            model.HostIdentity,
            model.HandleIdentity,
            compatibilityPolicy);
    }

    // Choosing the model is not admission: the chosen model's full check
    // re-reads and verifies every identity value and the exact HID device.
    private static AdmittedServiceModel SelectServiceModel(IRegistryValueSource registry)
    {
        if (WindowsHostIdentitySource.ReadBiosModelName(registry) is { } bios)
        {
            foreach (AdmittedServiceModel model in AdmittedServiceModels)
            {
                if (string.Equals(
                        model.HostIdentity.Manufacturer,
                        bios.Manufacturer,
                        StringComparison.Ordinal)
                    && string.Equals(
                        model.HostIdentity.ProductName,
                        bios.ProductName,
                        StringComparison.Ordinal))
                {
                    return model;
                }
            }
        }

        return AdmittedServiceModels[0];
    }

    private static BatteryValidationProbeResult ProbeWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy,
        ExpectedHostIdentity? expectedHostIdentity = null,
        HidHandleIdentity? exactHandleIdentity = null,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);

        var guard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = expectedHostIdentity is { } expected
            ? new WindowsHostIdentitySource(
                registry,
                platform,
                expected,
                compatibilityPolicy)
            : compatibilityPolicy is null
                ? new WindowsHostIdentitySource(registry, platform)
                : new WindowsHostIdentitySource(
                    registry,
                    platform,
                    compatibilityPolicy);
        var evaluator = exactHandleIdentity is { } exact
            ? new WindowsRazerHidEvidenceEvaluator(
                hostIdentity,
                guard,
                native,
                exclusivityPolicy,
                exact)
            : new WindowsRazerHidEvidenceEvaluator(
                hostIdentity,
                guard,
                native,
                exclusivityPolicy);
        using HidEvidenceEvaluation evaluation = evaluator.Evaluate();
        return evaluation.Result;
    }

    internal static IBatteryValidationRunFactory CreateWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(featureIo);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);

        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            ControllerExclusivityPolicy.Strict);
        var actorFactory = new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict,
            BatteryFeatureReportSettleDelay);

        return new BatteryValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout) =>
        CreateWindowsQuerySequence(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            ControllerExclusivityPolicy.Strict);

    internal static ITemporaryFullChargeQueryRunFactory CreateWindowsTemporaryFullChargeQuery(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Advisory);
        return new TemporaryFullChargeQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IFanRpmQueryRunFactory CreateWindowsFanRpmQuery(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null,
        SemaphoreSlim? sharedCommandGate = null)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Advisory,
            compatibilityPolicy: compatibilityPolicy);
        return new FanRpmQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            PendingDisposalRegistry.Shared,
            sharedCommandGate);
    }

    internal static ILightingBrightnessQueryRunner CreateWindowsLightingBrightnessQuery(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Advisory);
        return new LightingBrightnessQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout);
    }

    internal static IPerformanceFanDiscriminatorQueryRunner
        CreateWindowsPerformanceFanDiscriminatorQuery(
            IRegistryValueSource registry,
            IWindowsPlatformEvidenceSource platform,
            IRunningProcessSource processSource,
            IWindowsServiceSource serviceSource,
            IHidCandidateNative native,
            IHidFeatureIo featureIo,
            TimeProvider timeProvider,
            TimeSpan exchangeTimeout,
            TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Advisory);
        return new PerformanceFanDiscriminatorQueryRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout);
    }

    internal static IPerformanceFanSetterValidationRunner
        CreateWindowsPerformanceFanSetterValidation(
            IRegistryValueSource registry,
            IWindowsPlatformEvidenceSource platform,
            IRunningProcessSource processSource,
            IWindowsServiceSource serviceSource,
            IHidCandidateNative native,
            IHidFeatureIo featureIo,
            TimeProvider timeProvider,
            TimeSpan exchangeTimeout,
            TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Advisory);
        return new PerformanceFanSetterValidationRunner(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout);
    }

    internal static IProtectionOffValidationRunFactory CreateWindowsProtectionOffValidation(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict);
        return new ProtectionOffValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static ITemporaryFullChargeValidationRunFactory
        CreateWindowsTemporaryFullChargeValidation(
            IRegistryValueSource registry,
            IWindowsPlatformEvidenceSource platform,
            IRunningProcessSource processSource,
            IWindowsServiceSource serviceSource,
            IHidCandidateNative native,
            IHidFeatureIo featureIo,
            TimeProvider timeProvider,
            TimeSpan exchangeTimeout,
            TimeSpan cleanupDrainTimeout)
    {
        ValidationActorFactory actorFactory = CreateActorFactory(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            ControllerExclusivityPolicy.Strict,
            featureReportSettleDelay: TemporaryFullChargeValidationFeatureReportSettleDelay);
        return new TemporaryFullChargeValidationRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    internal static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequenceForService(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout) =>
        CreateWindowsQuerySequence(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            featureIo,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            ControllerExclusivityPolicy.Advisory);

    private static IBatteryQuerySequenceRunFactory CreateWindowsQuerySequence(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        ControllerExclusivityPolicy exclusivityPolicy)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(featureIo);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);

        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            exclusivityPolicy);
        var actorFactory = new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            exclusivityPolicy,
            BatteryFeatureReportSettleDelay);

        return new BatteryQuerySequenceRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            cleanupDrainTimeout);
    }

    private static ValidationActorFactory CreateActorFactory(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        IHidFeatureIo featureIo,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        ControllerExclusivityPolicy exclusivityPolicy,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null,
        TimeSpan? featureReportSettleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(featureIo);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);

        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = compatibilityPolicy is null
            ? new WindowsHostIdentitySource(registry, platform)
            : new WindowsHostIdentitySource(
                registry,
                platform,
                compatibilityPolicy);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            exclusivityPolicy);
        return new ValidationActorFactory(
            opener,
            featureIo,
            hardwareAccessGuard,
            timeProvider,
            exchangeTimeout,
            exclusivityPolicy,
            featureReportSettleDelay ?? BatteryFeatureReportSettleDelay);
    }

    private static ValidationActorFactory
        CreateWindowsRz090528TemporaryFullChargeActorFactory(
            ControllerExclusivityPolicy exclusivityPolicy,
            FirmwareCompatibilityPolicy? compatibilityPolicy = null)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var guard = new WindowsHardwareAccessGuard(
            new RunningProcessSource(),
            new WindowsServiceSource());
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry),
            WindowsHostIdentitySource.Rz090528Identity,
            compatibilityPolicy);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            guard,
            native,
            exclusivityPolicy,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
        return new ValidationActorFactory(
            opener,
            new OverlappedFeatureOperation(native),
            guard,
            TimeProvider.System,
            DefaultExchangeTimeout,
            exclusivityPolicy,
            TemporaryFullChargeValidationFeatureReportSettleDelay);
    }

    internal static ICoolingPadPhysicalPresenceSource
        CreateWindowsCoolingPadPhysicalPresenceSource() =>
        new WindowsCoolingPadPhysicalPresenceSource(new WindowsHidNative());
}
