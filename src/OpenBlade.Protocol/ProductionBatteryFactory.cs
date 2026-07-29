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
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null)
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

    public static IProductionBatteryPortFactory CreateWindows()
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
        return CreateWindows(catalog, ProductionBatteryReadEvidence.Production);
    }

    public static IProductionBatteryPortFactory CreateWindowsReadOnly() =>
        CreateWindows(DeviceCommandCatalog.Empty, ProductionBatteryReadEvidence.Production);

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
                admittedFanModes),
            null);
    }

    private static IProductionBatteryPortFactory CreateWindows(
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence)
    {
        var native = new WindowsHidNative();
        var registry = new WindowsRegistryValueSource();
        var processSource = new RunningProcessSource();
        var serviceSource = new WindowsServiceSource();
        var hardwareAccessGuard = new WindowsHardwareAccessGuard(processSource, serviceSource);
        var hostIdentity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry));
        var opener = new WindowsRazerHidSessionOpener(
            hostIdentity,
            hardwareAccessGuard,
            native,
            ControllerExclusivityPolicy.Advisory);
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
            PendingDisposalRegistry.Shared);
    }

    private static HidSessionResult<IProductionBatteryPort> Failure(
        HidSessionFailureKind failure) => new(null, failure);
}
