using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public sealed class ProductionBatteryFactory : IProductionBatteryPortFactory
{
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(30);

    private readonly DeviceCommandCatalog catalog;
    private readonly ProductionBatteryReadEvidence readEvidence;
    private readonly Func<ValidationRunContext> createContext;
    private readonly Func<TimeSpan, IValidationActorFactory> createActorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly FanSpeedRange fanSpeedRange;
    private readonly IReadOnlySet<FanMode>? admittedFanModes;
    private readonly CustomPerformanceCapabilities? customPerformanceCapabilities;
    private readonly CpuPowerLimitCapabilities? cpuPowerLimitCapabilities;
    private readonly CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities;
    private readonly GpuOverclockCapabilities? gpuOverclockCapabilities;
    private readonly IReadOnlyList<PowerSourceBehaviorObservation> observedPowerSourceBehaviors;
    private readonly IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates;
    private readonly SemaphoreSlim? sharedCommandGate;

    internal ProductionBatteryFactory(
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        Func<ValidationRunContext> createContext,
        Func<TimeSpan, IValidationActorFactory> createActorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals,
        FanSpeedRange? fanSpeedRange = null,
        IReadOnlySet<FanMode>? admittedFanModes = null,
        CustomPerformanceCapabilities? customPerformanceCapabilities = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null,
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates = null,
        SemaphoreSlim? sharedCommandGate = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.readEvidence = readEvidence ?? throw new ArgumentNullException(nameof(readEvidence));
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.createActorFactory = createActorFactory
            ?? throw new ArgumentNullException(nameof(createActorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.pendingDisposals = pendingDisposals
            ?? throw new ArgumentNullException(nameof(pendingDisposals));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.fanSpeedRange = fanSpeedRange ?? FanSpeedRange.Standard;
        if (!this.fanSpeedRange.IsWellFormed)
        {
            throw new ArgumentException(
                "The production fan-speed range is malformed.",
                nameof(fanSpeedRange));
        }

        this.admittedFanModes = admittedFanModes is null
            ? null
            : new HashSet<FanMode>(admittedFanModes);
        this.customPerformanceCapabilities = customPerformanceCapabilities;
        this.cpuPowerLimitCapabilities = cpuPowerLimitCapabilities;
        this.cpuVoltageOptimizerCapabilities = cpuVoltageOptimizerCapabilities;
        this.gpuOverclockCapabilities = gpuOverclockCapabilities;
        this.observedPowerSourceBehaviors = observedPowerSourceBehaviors is null
            ? []
            : [.. observedPowerSourceBehaviors];
        this.admittedAdapterPowerStates = admittedAdapterPowerStates is null
            ? null
            : new HashSet<AdapterPowerState>(admittedAdapterPowerStates);
        this.sharedCommandGate = sharedCommandGate;
        Capabilities = ProductionBatteryPort.CreateCapabilities(
            catalog,
            readEvidence,
            this.fanSpeedRange,
            this.admittedFanModes,
            this.customPerformanceCapabilities,
            this.cpuPowerLimitCapabilities,
            this.cpuVoltageOptimizerCapabilities,
            this.gpuOverclockCapabilities,
            this.observedPowerSourceBehaviors);
    }

    public DeviceCapabilities Capabilities { get; }

    internal IReadOnlySet<AdapterPowerState>? AdmittedAdapterPowerStates =>
        admittedAdapterPowerStates;

    public static IProductionBatteryPortFactory CreateWindows()
        => CreateWindowsCore(compatibilityPolicy: null);

    internal static IProductionBatteryPortFactory CreateWindows(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindowsCore(compatibilityPolicy);
    }

    private static IProductionBatteryPortFactory CreateWindowsCore(
        FirmwareCompatibilityPolicy? compatibilityPolicy)
    {
        ReadOnlyMemory<byte>? fixtureBytes = ProductionFixtureSource.Read();
        if (fixtureBytes is null)
        {
            throw new UnvalidatedCommandException(
                "The embedded production battery fixture is absent.");
        }

        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(
            fixtureBytes.Value,
            TrustedFixtureHashRegistry.Production);
        return CreateWindows(
            catalog,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy,
            admitGpuOverclock: true);
    }

    public static IProductionBatteryPortFactory CreateWindowsReadOnly() =>
        CreateWindows(
            DeviceCommandCatalog.Empty,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy: null,
            admitGpuOverclock: false);

    internal static IProductionBatteryPortFactory CreateWindowsReadOnly(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindows(
            DeviceCommandCatalog.Empty,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy,
            admitGpuOverclock: false);
    }

    public async Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            IValidationActorFactory actorFactory =
                createActorFactory(BatteryFeatureReportSettleDelay)
                ?? throw new InvalidOperationException();
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Failure is not null || opened.Value is null)
        {
            if (opened.Value is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    opened.Value,
                    timeProvider,
                    cleanupDrainTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }

            return Failure(opened.Failure ?? HidSessionFailureKind.NativeIoFailure);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                opened.Value,
                timeProvider,
                cleanupDrainTimeout,
                pendingDisposals).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        return new(
            new ProductionBatteryPort(
                opened.Value,
                catalog,
                readEvidence,
                context,
                exchangeTimeout,
                fanSpeedRange,
                admittedFanModes,
                admittedAdapterPowerStates,
                customPerformanceCapabilities,
                cpuPowerLimitCapabilities,
                cpuVoltageOptimizerCapabilities,
                gpuOverclockCapabilities,
                observedPowerSourceBehaviors,
                sharedCommandGate),
            null);
    }

    private static IProductionBatteryPortFactory CreateWindows(
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        FirmwareCompatibilityPolicy? compatibilityPolicy,
        bool admitGpuOverclock)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var processSource = new RunningProcessSource();
        var serviceSource = new WindowsServiceSource();
        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = compatibilityPolicy is null
            ? new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry))
            : new WindowsHostIdentitySource(
                registry,
                new RuntimeWindowsPlatformEvidenceSource(registry),
                compatibilityPolicy);
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            ControllerExclusivityPolicy.Advisory);
        GpuOverclockCapabilities? gpuOverclockCapabilities = admitGpuOverclock
            ? GetRz090581GpuOverclockCapabilities(hostIdentity.ReadExactIdentity())
            : null;
        return new ProductionBatteryFactory(
            catalog,
            readEvidence,
            static () => new ValidationRunContext(),
            settleDelay => new ValidationActorFactory(
                opener,
                new OverlappedFeatureOperation(native),
                hardwareAccessGuard,
                TimeProvider.System,
                DefaultExchangeTimeout,
                ControllerExclusivityPolicy.Advisory,
                settleDelay),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            PendingDisposalRegistry.Shared,
            gpuOverclockCapabilities: gpuOverclockCapabilities);
    }

    internal static GpuOverclockCapabilities? GetRz090581GpuOverclockCapabilities(
        HostIdentityReadResult exactIdentity) => exactIdentity is
        {
            Evidence: not null,
            Failure: null,
            FirmwareCompatibility: HostFirmwareCompatibility.Tested
                or HostFirmwareCompatibility.UntestedAllowed,
        }
            // These are the Synapse-visible policy-envelope bounds observed on
            // the exact RZ09-0581. GPU tuning is performed through NVIDIA's API,
            // so admission is independent of BIOS version after exact-device
            // identity succeeds. The independently queried driver range remains
            // a second per-write gate; bounded reversible validation establishes
            // the 5 MHz UI step inside this envelope, but not every endpoint.
            ? new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite)
            {
                SupportsEnableDisable = true,
            }
            : null;

    private static HidSessionResult<IProductionBatteryPort> Failure(
        HidSessionFailureKind failure) => new(null, failure);
}
