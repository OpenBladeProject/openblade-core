using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public sealed class ProductionBatteryFactory : IProductionBatteryPortFactory
{
    private static readonly DeviceIdentity HyperBoostTarget = new(
        TargetBladeDevice.Model,
        TargetBladeDevice.VendorId,
        TargetBladeDevice.ProductId,
        "4.00");
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryFeatureReportSettleDelay =
        TimeSpan.FromMilliseconds(30);

    private readonly DeviceCommandCatalog catalog;
    private readonly DeviceCommandCatalog hyperBoostCatalog;
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
    private readonly DeviceFeatureAccess? hyperBoostAccess;
    private readonly DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess;
    private readonly HyperBoostBaseModeCapabilities? hyperBoostBaseModes;
    private readonly IHyperBoostEligibilitySource? hyperBoostEligibility;
    private readonly bool allowPadlessHyperBoostLifecycleValidation;
    private readonly PerformancePreset padlessHyperBoostLifecycleBaseMode;

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
        SemaphoreSlim? sharedCommandGate = null,
        DeviceFeatureAccess? hyperBoostAccess = null,
        DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess = null,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes = null,
        DeviceCommandCatalog? hyperBoostCatalog = null,
        IHyperBoostEligibilitySource? hyperBoostEligibility = null,
        bool allowPadlessHyperBoostLifecycleValidation = false,
        PerformancePreset padlessHyperBoostLifecycleBaseMode = PerformancePreset.Balanced)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.hyperBoostCatalog = hyperBoostCatalog ?? DeviceCommandCatalog.Empty;
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
        this.hyperBoostAccess = hyperBoostAccess;
        this.hyperBoostWithoutCoolingPadAccess = hyperBoostWithoutCoolingPadAccess;
        this.hyperBoostBaseModes = hyperBoostBaseModes;
        this.hyperBoostEligibility = hyperBoostEligibility;
        this.allowPadlessHyperBoostLifecycleValidation =
            allowPadlessHyperBoostLifecycleValidation;
        this.padlessHyperBoostLifecycleBaseMode = padlessHyperBoostLifecycleBaseMode;
        ValidateHyperBoostBaseModes(
            this.hyperBoostAccess,
            this.hyperBoostBaseModes,
            nameof(hyperBoostBaseModes));
        if (this.hyperBoostWithoutCoolingPadAccess is not null
            && (this.hyperBoostAccess is null
                || this.hyperBoostWithoutCoolingPadAccess > this.hyperBoostAccess
                || this.hyperBoostWithoutCoolingPadAccess is not
                    (DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite)
                || this.hyperBoostWithoutCoolingPadAccess == DeviceFeatureAccess.ReadWrite
                    && this.hyperBoostBaseModes?.Balanced
                        != DeviceFeatureAccess.ReadWrite))
        {
            throw new UnvalidatedCommandException(
                "HyperBoost without the cooling pad requires exact bounded evidence, writable Balanced admission, and access no broader than overall HyperBoost.");
        }
        if (this.hyperBoostAccess == DeviceFeatureAccess.ReadWrite
            && (!ProductionBatteryPort.HasHyperBoostCommandSet(
                    this.hyperBoostCatalog, catalog, this.hyperBoostBaseModes)
                || this.hyperBoostEligibility is null))
        {
            throw new UnvalidatedCommandException(
                "Writable HyperBoost requires reviewed exact-model fixtures and a cooling-pad eligibility gate.");
        }
        if (this.allowPadlessHyperBoostLifecycleValidation
            && (this.hyperBoostAccess != DeviceFeatureAccess.ReadWrite
                || this.hyperBoostWithoutCoolingPadAccess is not
                    (DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite)
                || padlessHyperBoostLifecycleBaseMode is not (PerformancePreset.Balanced or PerformancePreset.Performance)
                || this.hyperBoostBaseModes?.AccessFor(padlessHyperBoostLifecycleBaseMode)
                    != DeviceFeatureAccess.ReadWrite
                || !ProductionBatteryPort.HasHyperBoostCommandSet(
                    this.hyperBoostCatalog, catalog, this.hyperBoostBaseModes)
                || this.hyperBoostEligibility is null))
        {
            throw new UnvalidatedCommandException(
                "Developer pad-less lifecycle validation requires exact pad-less evidence and the admitted HyperBoost command set.");
        }
        if (this.hyperBoostBaseModes?.Silent == DeviceFeatureAccess.ReadWrite
            && !ProductionBatteryPort.HasPerformancePair(
                catalog,
                PerformancePreset.Silent)
            || this.hyperBoostBaseModes?.Performance == DeviceFeatureAccess.ReadWrite
                && !ProductionBatteryPort.HasPerformancePair(
                    catalog,
                    PerformancePreset.Performance))
        {
            throw new UnvalidatedCommandException(
                "A writable direct HyperBoost base mode requires its exact restoration pair.");
        }
        Capabilities = ProductionBatteryPort.CreateCapabilities(
            catalog,
            readEvidence,
            this.fanSpeedRange,
            this.admittedFanModes,
            this.customPerformanceCapabilities,
            this.cpuPowerLimitCapabilities,
            this.cpuVoltageOptimizerCapabilities,
            this.gpuOverclockCapabilities,
            this.observedPowerSourceBehaviors,
            this.hyperBoostAccess,
            this.hyperBoostWithoutCoolingPadAccess,
            this.hyperBoostBaseModes);
    }

    public DeviceCapabilities Capabilities { get; }

    private static void ValidateHyperBoostBaseModes(
        DeviceFeatureAccess? overallAccess,
        HyperBoostBaseModeCapabilities? baseModes,
        string parameterName)
    {
        if (baseModes is null)
        {
            return;
        }

        if (overallAccess is null
            || !Enum.IsDefined(baseModes.Silent)
            || !Enum.IsDefined(baseModes.Balanced)
            || !Enum.IsDefined(baseModes.Performance)
            || baseModes.Silent > overallAccess
            || baseModes.Balanced > overallAccess
            || baseModes.Performance > overallAccess)
        {
            throw new ArgumentException(
                "HyperBoost base-mode access must be defined and no broader than overall HyperBoost access.",
                parameterName);
        }
    }

    internal IReadOnlySet<AdapterPowerState>? AdmittedAdapterPowerStates =>
        admittedAdapterPowerStates;

    public static IProductionBatteryPortFactory CreateWindows()
        => CreateWindowsCore(
            compatibilityPolicy: null,
            allowPadlessHyperBoostLifecycleValidation: false);

    internal static IProductionBatteryPortFactory
        CreateWindowsForPadlessHyperBoostLifecycleValidation() =>
        CreateWindowsCore(
            compatibilityPolicy: null,
            allowPadlessHyperBoostLifecycleValidation: true);

    internal static IProductionBatteryPortFactory CreateWindows(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindowsCore(
            compatibilityPolicy,
            allowPadlessHyperBoostLifecycleValidation: false);
    }

    private static IProductionBatteryPortFactory CreateWindowsCore(
        FirmwareCompatibilityPolicy? compatibilityPolicy,
        bool allowPadlessHyperBoostLifecycleValidation,
        ControllerExclusivityPolicy exclusivityPolicy =
            ControllerExclusivityPolicy.Advisory)
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
        ReadOnlyMemory<byte>? hyperBoostFixtureBytes = HyperBoostFixtureSource.Read();
        if (hyperBoostFixtureBytes is null)
        {
            throw new UnvalidatedCommandException(
                "The embedded HyperBoost production fixture is absent.");
        }

        DeviceCommandCatalog hyperBoostCatalog = CapturedFixtureFile.LoadCatalog(
            hyperBoostFixtureBytes.Value,
            TrustedFixtureHashRegistry.HyperBoostProduction,
            HyperBoostTarget);
        return CreateWindows(
            catalog,
            hyperBoostCatalog,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy,
            admitGpuOverclock: true,
            allowPadlessHyperBoostLifecycleValidation,
            exclusivityPolicy);
    }

    public static IProductionBatteryPortFactory CreateWindowsReadOnly() =>
        CreateWindows(
            DeviceCommandCatalog.Empty,
            DeviceCommandCatalog.Empty,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy: null,
            admitGpuOverclock: false,
            allowPadlessHyperBoostLifecycleValidation: false);

    internal static IProductionBatteryPortFactory CreateWindowsReadOnly(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateWindows(
            DeviceCommandCatalog.Empty,
            DeviceCommandCatalog.Empty,
            ProductionBatteryReadEvidence.Production,
            compatibilityPolicy,
            admitGpuOverclock: false,
            allowPadlessHyperBoostLifecycleValidation: false);
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
                sharedCommandGate,
                hyperBoostAccess,
                hyperBoostWithoutCoolingPadAccess,
                hyperBoostBaseModes,
                hyperBoostCatalog,
                hyperBoostEligibility,
                allowPadlessHyperBoostLifecycleValidation,
                padlessHyperBoostLifecycleBaseMode),
            null);
    }

    private static IProductionBatteryPortFactory CreateWindows(
        DeviceCommandCatalog catalog,
        DeviceCommandCatalog hyperBoostCatalog,
        ProductionBatteryReadEvidence readEvidence,
        FirmwareCompatibilityPolicy? compatibilityPolicy,
        bool admitGpuOverclock,
        bool allowPadlessHyperBoostLifecycleValidation,
        ControllerExclusivityPolicy exclusivityPolicy =
            ControllerExclusivityPolicy.Advisory)
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
            exclusivityPolicy);
        HostIdentityReadResult exactIdentity = hostIdentity.ReadExactIdentity();
        ProductionBatteryReadEvidence admittedReadEvidence =
            readEvidence == ProductionBatteryReadEvidence.Production
                ? GetRz090581ProductionBatteryReadEvidence(exactIdentity)
                : readEvidence;
        GpuOverclockCapabilities? gpuOverclockCapabilities = admitGpuOverclock
            ? GetRz090581GpuOverclockCapabilities(exactIdentity)
            : null;
        (
            DeviceFeatureAccess? hyperBoostAccess,
            DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess,
            HyperBoostBaseModeCapabilities? hyperBoostBaseModes,
            DeviceCommandCatalog admittedHyperBoostCatalog) =
            GetRz090581HyperBoostFactoryComposition(
                exactIdentity,
                hyperBoostCatalog,
                HyperBoostWithoutCoolingPadEvidenceGates.Current.WriteAdmitted);
        if (allowPadlessHyperBoostLifecycleValidation
            && !HyperBoostWithoutCoolingPadEvidenceGates.Current
                .LifecycleValidationReady)
        {
            throw new UnvalidatedCommandException(
                "Developer pad-less lifecycle validation remains blocked until every non-lifecycle evidence gate is reviewed.");
        }
        IHyperBoostEligibilitySource? hyperBoostEligibility =
            hyperBoostAccess == DeviceFeatureAccess.ReadWrite
                ? new WindowsHyperBoostEligibilitySource(native)
                : null;
        IReadOnlySet<AdapterPowerState> admittedAdapterPowerStates =
            GetRz090581AdmittedAdapterPowerStates(exactIdentity);
        return new ProductionBatteryFactory(
            catalog,
            admittedReadEvidence,
            static () => new ValidationRunContext(),
            settleDelay => new ValidationActorFactory(
                opener,
                new OverlappedFeatureOperation(native),
                hardwareAccessGuard,
                TimeProvider.System,
                DefaultExchangeTimeout,
                exclusivityPolicy,
                settleDelay),
            TimeProvider.System,
            DefaultExchangeTimeout,
            DefaultCleanupDrainTimeout,
            PendingDisposalRegistry.Shared,
            admittedAdapterPowerStates: admittedAdapterPowerStates,
            gpuOverclockCapabilities: gpuOverclockCapabilities,
            hyperBoostAccess: hyperBoostAccess,
            hyperBoostWithoutCoolingPadAccess: hyperBoostWithoutCoolingPadAccess,
            hyperBoostBaseModes: hyperBoostBaseModes,
            hyperBoostCatalog: admittedHyperBoostCatalog,
            hyperBoostEligibility: hyperBoostEligibility,
            allowPadlessHyperBoostLifecycleValidation:
                allowPadlessHyperBoostLifecycleValidation);
    }

    internal static ProductionBatteryReadEvidence
        GetRz090581ProductionBatteryReadEvidence(
            HostIdentityReadResult exactIdentity) =>
        IsExactModelScopedRz090581Identity(exactIdentity)
            ? ProductionBatteryReadEvidence.Rz090581ModelScoped
            : ProductionBatteryReadEvidence.Production;

    internal static GpuOverclockCapabilities? GetRz090581GpuOverclockCapabilities(
        HostIdentityReadResult exactIdentity) => exactIdentity is
        {
            Evidence: not null,
            Failure: null,
            FirmwareCompatibility: HostFirmwareCompatibility.Tested
                or HostFirmwareCompatibility.UntestedAllowed
                or HostFirmwareCompatibility.ModelScoped,
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

    internal static DeviceFeatureAccess? GetRz090581HyperBoostAccess(
        HostIdentityReadResult exactIdentity,
        bool reversibleWritesAdmitted = false) => exactIdentity is
        {
            Evidence:
            {
                Manufacturer: "Razer",
                ProductName: "Blade 16 - RZ09-0581",
                Sku: "RZ09-05819EN4",
            } evidence,
            Failure: null,
        }
            ? exactIdentity.FirmwareCompatibility is
                HostFirmwareCompatibility.Tested
                or HostFirmwareCompatibility.UntestedAllowed
                or HostFirmwareCompatibility.ModelScoped
                    ? reversibleWritesAdmitted
                        ? DeviceFeatureAccess.ReadWrite
                        : DeviceFeatureAccess.ReadOnly
                    : DeviceFeatureAccess.ObservedOnly
            : null;

    internal static (
        DeviceFeatureAccess? Access,
        DeviceFeatureAccess? WithoutCoolingPadAccess,
        HyperBoostBaseModeCapabilities? BaseModes)
        GetRz090581HyperBoostCapabilities(
            HostIdentityReadResult exactIdentity,
            bool reversibleWritesAdmitted,
            bool padlessWritesAdmitted)
    {
        DeviceFeatureAccess? access = GetRz090581HyperBoostAccess(
            exactIdentity,
            reversibleWritesAdmitted);
        DeviceFeatureAccess? withoutCoolingPadAccess =
            GetRz090581HyperBoostWithoutCoolingPadAccess(
                exactIdentity,
                reversibleWritesAdmitted && padlessWritesAdmitted);
        HyperBoostBaseModeCapabilities? baseModes =
            GetRz090581HyperBoostBaseModeCapabilities(exactIdentity);
        if (access is not { } maximumAccess || baseModes is null)
        {
            return (access, withoutCoolingPadAccess, null);
        }

        return (
            access,
            withoutCoolingPadAccess,
            new HyperBoostBaseModeCapabilities(
                BoundAccess(baseModes.Silent, maximumAccess),
                BoundAccess(baseModes.Balanced, maximumAccess),
                BoundAccess(baseModes.Performance, maximumAccess)));

        static DeviceFeatureAccess BoundAccess(
            DeviceFeatureAccess candidate,
            DeviceFeatureAccess maximum) => candidate > maximum
                ? maximum
                : candidate;
    }

    internal static (
        DeviceFeatureAccess? Access,
        DeviceFeatureAccess? WithoutCoolingPadAccess,
        HyperBoostBaseModeCapabilities? BaseModes,
        DeviceCommandCatalog AdmittedCatalog)
        GetRz090581HyperBoostFactoryComposition(
            HostIdentityReadResult exactIdentity,
            DeviceCommandCatalog hyperBoostCatalog,
            bool padlessWritesAdmitted)
    {
        ArgumentNullException.ThrowIfNull(hyperBoostCatalog);
        bool reversibleWritesAdmitted =
            ProductionBatteryPort.HasHyperBoostCommandSet(hyperBoostCatalog);
        (
            DeviceFeatureAccess? access,
            DeviceFeatureAccess? withoutCoolingPadAccess,
            HyperBoostBaseModeCapabilities? baseModes) =
            GetRz090581HyperBoostCapabilities(
                exactIdentity,
                reversibleWritesAdmitted,
                reversibleWritesAdmitted && padlessWritesAdmitted);
        return (
            access,
            withoutCoolingPadAccess,
            baseModes,
            access == DeviceFeatureAccess.ReadWrite
                ? hyperBoostCatalog
                : DeviceCommandCatalog.Empty);
    }

    internal static DeviceFeatureAccess? GetRz090581HyperBoostWithoutCoolingPadAccess(
        HostIdentityReadResult exactIdentity,
        bool writesAdmitted = false) => IsExactModelScopedRz090581Identity(exactIdentity)
            ? writesAdmitted
                ? DeviceFeatureAccess.ReadWrite
                : DeviceFeatureAccess.ObservedOnly
            : null;

    internal static HyperBoostBaseModeCapabilities?
        GetRz090581HyperBoostBaseModeCapabilities(
            HostIdentityReadResult exactIdentity) => exactIdentity is
            {
                Evidence:
                {
                    Manufacturer: "Razer",
                    ProductName: "Blade 16 - RZ09-0581",
                    Sku: "RZ09-05819EN4",
                },
                Failure: null,
                FirmwareCompatibility: HostFirmwareCompatibility.Tested
                    or HostFirmwareCompatibility.UntestedAllowed
                    or HostFirmwareCompatibility.ModelScoped,
            }
            ? HyperBoostBaseModeAdmissionSource.ReadCapabilities()
            : null;

    internal static IReadOnlySet<AdapterPowerState>
        GetRz090581AdmittedAdapterPowerStates(
            HostIdentityReadResult exactIdentity,
            Func<bool>? isSlimQAdmissionApproved = null)
    {
        var states = new HashSet<AdapterPowerState>
        {
            AdapterPowerState.UsbCPower100W,
            AdapterPowerState.FullPower280W,
        };

        if (!IsExactModelScopedRz090581Identity(exactIdentity))
        {
            return states;
        }

        states.Add(AdapterPowerState.UsbCPower45W);
        states.Add(AdapterPowerState.UsbCPower60W);
        states.Add(AdapterPowerState.UsbCPower65W);
        states.Add(AdapterPowerState.UsbCPower80W);
        states.Add(AdapterPowerState.UsbCPower90W);
        try
        {
            if ((isSlimQAdmissionApproved ?? SlimQ330WAdapterAdmissionSource.IsAdmitted)())
            {
                states.Add(AdapterPowerState.FullPower330W);
            }
        }
        catch (UnvalidatedCommandException)
        {
            // Adapter evidence is optional for the read-only factory fallback.
            // A missing or untrusted manifest removes only the 330 W mapping.
        }

        return states;
    }

    private static bool IsExactModelScopedRz090581Identity(
        HostIdentityReadResult exactIdentity) => exactIdentity is
        {
            Evidence:
            {
                Manufacturer: "Razer",
                ProductName: "Blade 16 - RZ09-0581",
                Sku: "RZ09-05819EN4",
                BiosVersion: { Length: > 0 },
            },
            Failure: null,
            FirmwareCompatibility: HostFirmwareCompatibility.Tested
                or HostFirmwareCompatibility.ModelScoped
                or HostFirmwareCompatibility.UntestedAllowed,
        };

    private static HidSessionResult<IProductionBatteryPort> Failure(
        HidSessionFailureKind failure) => new(null, failure);
}
