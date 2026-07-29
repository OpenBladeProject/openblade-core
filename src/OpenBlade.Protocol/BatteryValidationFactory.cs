using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

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
        CreateWindowsRz090528ProductionBattery()
    {
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
                ControllerExclusivityPolicy.Advisory,
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
                DeviceFeatureAccess.ObservedOnly),
            new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ObservedOnly),
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
            new HashSet<AdapterPowerState>
            {
                AdapterPowerState.FullPower280W,
            });
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
            ? CreateWindowsRz090528BrightnessLighting()
            : ProductionLightingFactory.CreateWindows();

    internal static IProductionLightingPortFactory
        CreateWindowsRz090528BrightnessLighting()
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
            Rz090528BrightnessFeatureReportSettleDelay);
        return new Rz090528BrightnessLightingFactory(
            static () => new ValidationRunContext(),
            actorFactory);
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
    {
        ValidationActorFactory actorFactory =
            CreateWindowsRz090528TemporaryFullChargeActorFactory(
                ControllerExclusivityPolicy.Advisory);
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
            : CreateWindowsFanRpmQuery();

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
            new ExpectedHostIdentity(
                "Razer",
                "Blade 16 - RZ09-0528",
                "RZ09-05289EN4",
                "2.02"));
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

    internal static BatteryValidationProbeResult ProbeWindowsForService(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native)
    {
        BatteryValidationProbeResult production = ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Advisory);
        if (production.Classification
            != BatteryValidationProbeClassification.HostIdentityBlocked)
        {
            return production;
        }

        return ProbeWindows(
            registry,
            platform,
            processSource,
            serviceSource,
            native,
            ControllerExclusivityPolicy.Advisory,
            WindowsHostIdentitySource.Rz090528Identity,
            new HidHandleIdentity(
                VendorId: 0x1532,
                ProductId: 0x02C6,
                UsagePage: 0x0001,
                Usage: 0x0002,
                FeatureReportByteLength: 91));
    }

    private static BatteryValidationProbeResult ProbeWindows(
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform,
        IRunningProcessSource processSource,
        IWindowsServiceSource serviceSource,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy,
        ExpectedHostIdentity? expectedHostIdentity = null,
        HidHandleIdentity? exactHandleIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(processSource);
        ArgumentNullException.ThrowIfNull(serviceSource);
        ArgumentNullException.ThrowIfNull(native);

        var guard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = expectedHostIdentity is { } expected
            ? new WindowsHostIdentitySource(registry, platform, expected)
            : new WindowsHostIdentitySource(registry, platform);
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
        return new FanRpmQueryRunFactory(
            static () => new ValidationRunContext(),
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout);
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
        var hostIdentity = new WindowsHostIdentitySource(registry, platform);
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
            ControllerExclusivityPolicy exclusivityPolicy)
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
}
