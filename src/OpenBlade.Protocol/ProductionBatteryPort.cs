using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ProductionBatteryPort :
    IProductionBatteryPort,
    IPadlessHyperBoostLifecycleValidationPort
{
    internal const string Limit50Action = "battery-limit-50";
    internal const string Limit55Action = "battery-limit-55";
    internal const string Limit60Action = "battery-limit-60";
    internal const string Limit65Action = "battery-limit-65";
    internal const string Limit70Action = "battery-limit-70";
    internal const string Limit75Action = "battery-limit-75";
    internal const string Limit80Action = "battery-limit-80";
    internal const string ProtectionOffAction = "battery-protection-off";
    internal const string TemporaryEnableAction = "charge-to-100-enable";
    internal const string TemporaryCancelAction = "charge-to-100-cancel";
    internal const string PerformanceBalancedFirstAction =
        "performance-balanced-channel-1";
    internal const string PerformanceBalancedSecondAction =
        "performance-balanced-channel-2";
    internal const string PerformanceSilentFirstAction =
        "performance-silent-channel-1";
    internal const string PerformanceSilentSecondAction =
        "performance-silent-channel-2";
    internal const string PerformanceBoostFirstAction =
        "performance-performance-channel-1";
    internal const string PerformanceBoostSecondAction =
        "performance-performance-channel-2";
    internal const string PerformanceBatterySaverFirstAction =
        "performance-battery-saver-channel-1";
    internal const string PerformanceBatterySaverSecondAction =
        "performance-battery-saver-channel-2";
    internal const string PerformanceBatteryBalancedFirstAction =
        "performance-battery-balanced-channel-1";
    internal const string PerformanceBatteryBalancedSecondAction =
        "performance-battery-balanced-channel-2";
    internal const string HyperBoostEnableFirstAction =
        "hyperboost-enable-channel-1";
    internal const string HyperBoostEnableSecondAction =
        "hyperboost-enable-channel-2";
    internal const string HyperBoostRestoreFirstAction =
        "hyperboost-restore-balanced-channel-1";
    internal const string HyperBoostRestoreSecondAction =
        "hyperboost-restore-balanced-channel-2";
    internal const string PerformanceCustomFirstAction =
        "performance-custom-channel-1";
    internal const string PerformanceCustomSecondAction =
        "performance-custom-channel-2";
    internal const string CustomCpuLowAction = "custom-performance-cpu-low";
    internal const string CustomCpuMediumAction = "custom-performance-cpu-medium";
    internal const string CustomCpuHighAction = "custom-performance-cpu-high";
    internal const string CustomGpuLowAction = "custom-performance-gpu-low";
    internal const string CustomGpuMediumAction = "custom-performance-gpu-medium";
    internal const string CustomGpuHighAction = "custom-performance-gpu-high";
    internal const string FanMaximumStateFirstAction =
        "fan-maximum-5400-state-channel-1";
    internal const string FanMaximumStateSecondAction =
        "fan-maximum-5400-state-channel-2";
    internal const string FanMaximumSpeedFirstAction =
        "fan-maximum-5400-speed-channel-1";
    internal const string FanMaximumSpeedSecondAction =
        "fan-maximum-5400-speed-channel-2";
    internal const string StartupAnimationOnAction = "startup-animation-on";
    internal const string StartupAnimationOffAction = "startup-animation-off";
    internal const string FunctionKeysPrimaryAction = "function-key-primary-function";
    internal const string MultimediaKeysPrimaryAction = "function-key-primary-multimedia";
    internal const string GamingModeOnAction = "gaming-mode-on";
    internal const string GamingModeOffAction = "gaming-mode-off";
    internal const string MicrophoneMuteIndicatorOnAction =
        "microphone-mute-indicator-on";
    internal const string MicrophoneMuteIndicatorOffAction =
        "microphone-mute-indicator-off";

    private readonly IRazerCommandSession session;
    private readonly DeviceCommandCatalog catalog;
    private readonly DeviceCommandCatalog hyperBoostCatalog;
    private readonly ProductionBatteryReadEvidence readEvidence;
    private readonly ValidationRunContext context;
    private readonly TimeSpan exchangeTimeout;
    private readonly BatteryCommandClient batteryCommands;
    private readonly TemporaryFullChargeCommandClient temporaryCommands;
    private readonly AdapterPowerCommandClient adapterPowerCommands;
    private readonly McuVersionCommandClient mcuVersionCommands;
    private readonly Rz090581CapabilityDiagnosticsCommandClient
        rz090581CapabilityDiagnosticsCommands;
    private readonly StartupAnimationCommandClient startupAnimationCommands;
    private readonly FunctionKeyPrimaryCommandClient functionKeyPrimaryCommands;
    private readonly GamingModeCommandClient gamingModeCommands;
    private readonly FanSpeedRange fanSpeedRange;
    private readonly IReadOnlySet<FanMode>? admittedFanModes;
    private readonly IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates;
    private readonly IHyperBoostEligibilitySource? hyperBoostEligibility;
    private readonly bool allowPadlessHyperBoostLifecycleValidation;
    private readonly PerformancePreset padlessHyperBoostLifecycleBaseMode;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object lifecycleSync = new();
    private readonly IAsyncDisposable ownedSession;
    private bool closing;
    private bool disposed;
    private bool poisoned;
    private Task? disposalTask;

    internal ProductionBatteryPort(
        IRazerCommandSession session,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout,
        FanSpeedRange? fanSpeedRange = null,
        IReadOnlySet<FanMode>? admittedFanModes = null,
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates = null,
        CustomPerformanceCapabilities? customPerformanceCapabilities = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null,
        DeviceFeatureAccess? hyperBoostAccess = null,
        DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess = null,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes = null,
        DeviceCommandCatalog? hyperBoostCatalog = null,
        IHyperBoostEligibilitySource? hyperBoostEligibility = null,
        bool allowPadlessHyperBoostLifecycleValidation = false,
        PerformancePreset padlessHyperBoostLifecycleBaseMode = PerformancePreset.Balanced)
        : this(
            session,
            session,
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
            hyperBoostAccess,
            hyperBoostWithoutCoolingPadAccess,
            hyperBoostBaseModes,
            hyperBoostCatalog,
            hyperBoostEligibility,
            allowPadlessHyperBoostLifecycleValidation,
            padlessHyperBoostLifecycleBaseMode)
    {
    }

    internal ProductionBatteryPort(
        ValidationActor actor,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout,
        FanSpeedRange? fanSpeedRange = null,
        IReadOnlySet<FanMode>? admittedFanModes = null,
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates = null,
        CustomPerformanceCapabilities? customPerformanceCapabilities = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null,
        SemaphoreSlim? sharedCommandGate = null,
        DeviceFeatureAccess? hyperBoostAccess = null,
        DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess = null,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes = null,
        DeviceCommandCatalog? hyperBoostCatalog = null,
        IHyperBoostEligibilitySource? hyperBoostEligibility = null,
        bool allowPadlessHyperBoostLifecycleValidation = false,
        PerformancePreset padlessHyperBoostLifecycleBaseMode = PerformancePreset.Balanced)
        : this(
            sharedCommandGate is null
                ? actor.Session
                : new Rz090528CommandSessionGate(actor.Session, sharedCommandGate),
            actor,
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
            hyperBoostAccess,
            hyperBoostWithoutCoolingPadAccess,
            hyperBoostBaseModes,
            hyperBoostCatalog,
            hyperBoostEligibility,
            allowPadlessHyperBoostLifecycleValidation,
            padlessHyperBoostLifecycleBaseMode)
    {
    }

    private ProductionBatteryPort(
        IRazerCommandSession session,
        IAsyncDisposable ownedSession,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout,
        FanSpeedRange? fanSpeedRange,
        IReadOnlySet<FanMode>? admittedFanModes,
        IReadOnlySet<AdapterPowerState>? admittedAdapterPowerStates,
        CustomPerformanceCapabilities? customPerformanceCapabilities,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities,
        GpuOverclockCapabilities? gpuOverclockCapabilities,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors,
        DeviceFeatureAccess? hyperBoostAccess,
        DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes,
        DeviceCommandCatalog? hyperBoostCatalog,
        IHyperBoostEligibilitySource? hyperBoostEligibility,
        bool allowPadlessHyperBoostLifecycleValidation,
        PerformancePreset padlessHyperBoostLifecycleBaseMode)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.ownedSession = ownedSession ?? throw new ArgumentNullException(nameof(ownedSession));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.hyperBoostCatalog = hyperBoostAccess == DeviceFeatureAccess.ReadWrite
            ? hyperBoostCatalog ?? DeviceCommandCatalog.Empty
            : DeviceCommandCatalog.Empty;
        this.readEvidence = readEvidence ?? throw new ArgumentNullException(nameof(readEvidence));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
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
        this.admittedAdapterPowerStates = admittedAdapterPowerStates is null
            ? null
            : new HashSet<AdapterPowerState>(admittedAdapterPowerStates);
        this.hyperBoostEligibility = hyperBoostEligibility;
        this.allowPadlessHyperBoostLifecycleValidation =
            allowPadlessHyperBoostLifecycleValidation;
        this.padlessHyperBoostLifecycleBaseMode = padlessHyperBoostLifecycleBaseMode;
        if (hyperBoostBaseModes is not null
            && (hyperBoostAccess is null
                || !Enum.IsDefined(hyperBoostBaseModes.Silent)
                || !Enum.IsDefined(hyperBoostBaseModes.Balanced)
                || !Enum.IsDefined(hyperBoostBaseModes.Performance)
                || hyperBoostBaseModes.Silent > hyperBoostAccess
                || hyperBoostBaseModes.Balanced > hyperBoostAccess
                || hyperBoostBaseModes.Performance > hyperBoostAccess
                || hyperBoostBaseModes.Custom is { } custom
                    && (!Enum.IsDefined(custom) || custom > hyperBoostAccess)))
        {
            throw new ArgumentException(
                "HyperBoost base-mode access must be defined and no broader than overall HyperBoost access.",
                nameof(hyperBoostBaseModes));
        }
        if (hyperBoostWithoutCoolingPadAccess is not null
            && (hyperBoostAccess is null
                || hyperBoostWithoutCoolingPadAccess > hyperBoostAccess
                || hyperBoostWithoutCoolingPadAccess is not
                    (DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite)
                || hyperBoostWithoutCoolingPadAccess == DeviceFeatureAccess.ReadWrite
                    && hyperBoostBaseModes?.Balanced != DeviceFeatureAccess.ReadWrite
                    && hyperBoostBaseModes?.Performance != DeviceFeatureAccess.ReadWrite))
        {
            throw new UnvalidatedCommandException(
                "HyperBoost without the cooling pad requires exact bounded evidence, a writable direct base mode, and access no broader than overall HyperBoost.");
        }
        if (allowPadlessHyperBoostLifecycleValidation
            && (hyperBoostAccess != DeviceFeatureAccess.ReadWrite
                || hyperBoostWithoutCoolingPadAccess is not
                    (DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite)
                || padlessHyperBoostLifecycleBaseMode is not (PerformancePreset.Balanced or PerformancePreset.Performance)
                || hyperBoostBaseModes?.AccessFor(padlessHyperBoostLifecycleBaseMode)
                    != DeviceFeatureAccess.ReadWrite
                || this.hyperBoostEligibility is null
                || !HasHyperBoostCommandSet(this.hyperBoostCatalog, catalog, hyperBoostBaseModes)))
        {
            throw new UnvalidatedCommandException(
                "Developer pad-less lifecycle validation requires the exact admitted HyperBoost pair, selected baseline restoration, pad-less evidence, and an independent eligibility gate.");
        }
        if (hyperBoostAccess == DeviceFeatureAccess.ReadWrite
            && (!HasHyperBoostCommandSet(this.hyperBoostCatalog, catalog, hyperBoostBaseModes)
                || this.hyperBoostEligibility is null))
        {
            throw new UnvalidatedCommandException(
                "Writable HyperBoost requires the exact reversible command set and cooling-pad eligibility gate.");
        }
        if (hyperBoostBaseModes?.Silent == DeviceFeatureAccess.ReadWrite
            && !HasPair(catalog, PerformanceSilentFirstAction, PerformanceSilentSecondAction)
            || hyperBoostBaseModes?.Performance == DeviceFeatureAccess.ReadWrite
                && !HasPair(catalog, PerformanceBoostFirstAction, PerformanceBoostSecondAction))
        {
            throw new UnvalidatedCommandException(
                "A writable direct HyperBoost base mode requires its exact restoration pair.");
        }

        batteryCommands = new BatteryCommandClient(session, exchangeTimeout);
        temporaryCommands = new TemporaryFullChargeCommandClient(session, exchangeTimeout);
        adapterPowerCommands = new AdapterPowerCommandClient(session, exchangeTimeout);
        mcuVersionCommands = new McuVersionCommandClient(session, exchangeTimeout);
        rz090581CapabilityDiagnosticsCommands =
            new Rz090581CapabilityDiagnosticsCommandClient(session, exchangeTimeout);
        startupAnimationCommands = new StartupAnimationCommandClient(
            session,
            exchangeTimeout);
        functionKeyPrimaryCommands = new FunctionKeyPrimaryCommandClient(
            session,
            exchangeTimeout);
        gamingModeCommands = new GamingModeCommandClient(session, exchangeTimeout);
        Capabilities = CreateCapabilities(
            catalog,
            readEvidence,
            this.fanSpeedRange,
            this.admittedFanModes,
            customPerformanceCapabilities,
            cpuPowerLimitCapabilities,
            cpuVoltageOptimizerCapabilities,
            gpuOverclockCapabilities,
            observedPowerSourceBehaviors,
            hyperBoostAccess,
            hyperBoostWithoutCoolingPadAccess,
            hyperBoostBaseModes);
    }

    public DeviceCapabilities Capabilities { get; }

    public bool SupportsTemporaryFullChargeQuery =>
        readEvidence.SupportsTemporaryFullChargeQuery;

    public bool SupportsMcuVersionQuery => readEvidence.SupportsMcuVersionQuery;

    public bool SupportsStartupAnimationQuery =>
        readEvidence.SupportsStartupAnimationQuery;

    public bool SupportsFunctionKeyPrimaryQuery =>
        readEvidence.SupportsFunctionKeyPrimaryQuery;

    public bool SupportsGamingModeQuery => readEvidence.SupportsGamingModeQuery;

    public bool SupportsRz090581CapabilityDiagnosticsQuery =>
        readEvidence.SupportsRz090581CapabilityDiagnosticsQuery;

    public async Task<ConfirmedBatteryState> QueryProtectionAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<BatteryValidationSetting> result =
                await batteryCommands.QueryAsync(transactionId, cancellationToken)
                    .ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(
                    result.Failure.Value,
                    result.Diagnostic,
                    cancellationToken,
                    DeviceStateQuery.BatteryProtection);
            }

            return MapProtectionState(result.Value);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsTemporaryFullChargeQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<bool> result = await temporaryCommands.QueryAsync(
                transactionId,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(
                    result.Failure.Value,
                    result.Diagnostic,
                    cancellationToken,
                    DeviceStateQuery.TemporaryFullCharge);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            return await QueryPerformanceFanStateCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<AdapterPowerState> QueryAdapterPowerStateAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsAdapterPowerQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return AdapterPowerState.Unknown;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<AdapterPowerState> result =
                await adapterPowerCommands.QueryAsync(transactionId, cancellationToken)
                    .ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            if (result.Value != AdapterPowerState.Unknown
                && admittedAdapterPowerStates is not null
                && !admittedAdapterPowerStates.Contains(result.Value))
            {
                return AdapterPowerState.Unknown;
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<string?> QueryMcuVersionAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsMcuVersionQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<string?> result = await mcuVersionCommands.QueryAsync(
                transactionId,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Rz090581CapabilityDiagnostics?>
        QueryRz090581CapabilityDiagnosticsAsync(
            CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsRz090581CapabilityDiagnosticsQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            HidSessionResult<Rz090581CapabilityDiagnostics?> result =
                await rz090581CapabilityDiagnosticsCommands.QueryAsync(
                    () => TransactionIdAssignment.Valid(AllocateTransactionForRead()),
                    cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> QueryStartupAnimationAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsStartupAnimationQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<bool> result = await startupAnimationCommands.QueryAsync(
                transactionId,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<FunctionKeyPrimaryMode?> QueryFunctionKeyPrimaryAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsFunctionKeyPrimaryQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<FunctionKeyPrimaryMode> result =
                await functionKeyPrimaryCommands.QueryAsync(
                    transactionId,
                    cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> QueryGamingModeAsync(CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsGamingModeQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<bool> result = await gamingModeCommands.QueryAsync(
                transactionId,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken)
    {
        string? action = ChargeLimitAction(percent);
        if (action is null)
        {
            return Task.FromException(Unavailable(
                $"Charge limit {percent}% is not admitted for production use."));
        }

        return ExecuteSetterAsync(action, cancellationToken);
    }

    public Task SetPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? actions = value switch
        {
            PerformancePreset.Balanced =>
                [PerformanceBalancedFirstAction, PerformanceBalancedSecondAction],
            PerformancePreset.Silent =>
                [PerformanceSilentFirstAction, PerformanceSilentSecondAction],
            PerformancePreset.Performance =>
                [PerformanceBoostFirstAction, PerformanceBoostSecondAction],
            PerformancePreset.BatterySaver =>
                [PerformanceBatterySaverFirstAction, PerformanceBatterySaverSecondAction],
            PerformancePreset.BatteryBalanced =>
                [PerformanceBatteryBalancedFirstAction, PerformanceBatteryBalancedSecondAction],
            _ => null,
        };
        return actions is null
            ? Task.FromException(Unavailable(
                $"Performance preset '{value}' is not admitted for production use."))
            : ExecuteThermalSettersAsync(
                actions,
                state => state.Performance == value && state.Fans == FanMode.Automatic,
                state => state.Performance is not null && state.Fans == FanMode.Automatic,
                "Performance changes require a confirmed Automatic thermal state.",
                applyMaximumTiming: false,
                verifyFinalState: value is PerformancePreset.BatterySaver
                    or PerformancePreset.BatteryBalanced,
                cancellationToken,
                hyperBoostExitActions:
                [
                    HyperBoostRestoreFirstAction,
                    HyperBoostRestoreSecondAction,
                ],
                hyperBoostExitCatalog: hyperBoostCatalog);
    }

    public Task SetHyperBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, PerformancePreset.Balanced, cancellationToken);

    public Task SetHyperBoostAsync(
        bool enabled,
        PerformancePreset baseMode,
        CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
            enabled,
            baseMode,
            allowPadlessLifecycleValidation: false,
            cancellationToken);

    public Task SetHyperBoostAsync(
        bool enabled,
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
            enabled,
            baseMode,
            allowPadlessLifecycleValidation: false,
            cancellationToken,
            requiresCoolingPad
                ? CoolingPadConnectionState.PresentExact
                : CoolingPadConnectionState.Absent);

    Task IPadlessHyperBoostLifecycleValidationPort
        .EnablePadlessHyperBoostLifecycleValidationAsync(
            CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
                enabled: true,
                padlessHyperBoostLifecycleBaseMode,
                allowPadlessLifecycleValidation: true,
                cancellationToken);

    private Task SetHyperBoostCoreAsync(
        bool enabled,
        PerformancePreset baseMode,
        bool allowPadlessLifecycleValidation,
        CancellationToken cancellationToken,
        CoolingPadConnectionState? journaledPadState = null)
    {
        bool lifecycleValidation = enabled
            && allowPadlessLifecycleValidation
            && this.allowPadlessHyperBoostLifecycleValidation;
        if (Capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
            || allowPadlessLifecycleValidation && !lifecycleValidation)
        {
            return Task.FromException(Unavailable(
                "HyperBoost control is not admitted for this exact device and firmware."));
        }

        DeviceFeatureAccess? baseAccess = Capabilities.HyperBoostBaseModes
            ?.AccessFor(baseMode)
            ?? (baseMode == PerformancePreset.Balanced
                ? Capabilities.HyperBoostAccess
                : null);
        if (baseAccess != DeviceFeatureAccess.ReadWrite)
        {
            return Task.FromException(Unavailable(
                $"Direct HyperBoost from '{baseMode}' is not admitted for this exact device and firmware."));
        }

        (IReadOnlyList<string> actions, DeviceCommandCatalog commandCatalog) = enabled
            ? ([HyperBoostEnableFirstAction, HyperBoostEnableSecondAction], hyperBoostCatalog)
            : RestoreHyperBoostTarget(baseMode);
        // The journaled pad path, or else the first eligibility observation, locks the pad
        // state for the whole transition. Where both a pad-present and a pad-less path are
        // admitted, no command may proceed under the other path.
        CoolingPadConnectionState? entryPadState = enabled ? journaledPadState : null;
        return ExecuteThermalSettersAsync(
            actions,
            state => state.HyperBoostActive == enabled
                && state.Fans == FanMode.Automatic
                && (enabled || state.Performance == baseMode),
            enabled
                ? state => !state.HyperBoostActive
                    && state.Performance == baseMode
                    && state.Fans == FanMode.Automatic
                : state => state.HyperBoostActive && state.Fans == FanMode.Automatic,
            enabled
                ? $"HyperBoost requires confirmed {baseMode} performance with admitted direct-transition evidence and Automatic fans."
                : $"Only a confirmed HyperBoost state can be restored directly to {baseMode}.",
            applyMaximumTiming: false,
            verifyFinalState: true,
            cancellationToken,
            commandCatalog: commandCatalog,
            validateEligibility: enabled
                ? async token => entryPadState = await ValidateHyperBoostEligibilityAsync(
                    baseMode,
                    lifecycleValidation,
                    entryPadState,
                    token).ConfigureAwait(false)
                : null,
            // Wherever a pad-present path exists, the pad state can change which path applies.
            revalidateEligibilityPerCommand: enabled
                && HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(Capabilities) is not null,
            requiredPadStatePerCommand: () => entryPadState
                ?? (lifecycleValidation
                    ? CoolingPadConnectionState.Absent : CoolingPadConnectionState.PresentExact));
    }

    private (IReadOnlyList<string> Actions, DeviceCommandCatalog Catalog)
        RestoreHyperBoostTarget(PerformancePreset baseMode) => baseMode switch
        {
            PerformancePreset.Silent =>
                ([PerformanceSilentFirstAction, PerformanceSilentSecondAction], catalog),
            PerformancePreset.Performance =>
                ([PerformanceBoostFirstAction, PerformanceBoostSecondAction], catalog),
            // The Custom mode pair; the service then re-applies the saved CPU/GPU levels.
            PerformancePreset.Custom =>
                ([PerformanceCustomFirstAction, PerformanceCustomSecondAction], catalog),
            // Models without a dedicated HyperBoost restore pair restore Balanced directly.
            _ when !HasPair(hyperBoostCatalog, HyperBoostRestoreFirstAction, HyperBoostRestoreSecondAction) =>
                ([PerformanceBalancedFirstAction, PerformanceBalancedSecondAction], catalog),
            _ =>
                ([HyperBoostRestoreFirstAction, HyperBoostRestoreSecondAction], hyperBoostCatalog),
        };

    public Task SetCustomPerformanceAsync(
        CustomPerformanceSettings value,
        CancellationToken cancellationToken)
    {
        if (!CustomPerformanceSettings.IsWellFormed(value)
            || Capabilities.CustomPerformance?.Supports(value) == false)
        {
            return Task.FromException(KnownNotApplied(
                "The custom performance CPU/GPU combination is invalid."));
        }

        string? cpuAction = CustomPerformanceAction(
            CustomPerformanceDomain.Cpu,
            value.Cpu);
        string? gpuAction = CustomPerformanceAction(
            CustomPerformanceDomain.Gpu,
            value.Gpu);
        if (cpuAction is null || gpuAction is null)
        {
            return Task.FromException(Unavailable(
                "The custom performance CPU/GPU combination is not admitted."));
        }

        return ExecuteThermalSettersAsync(
            [
                PerformanceCustomFirstAction,
                PerformanceCustomSecondAction,
                cpuAction,
                gpuAction,
            ],
            state => state.Performance == PerformancePreset.Custom
                && state.Fans == FanMode.Automatic
                && state.CustomPerformance == value,
            state => !state.HyperBoostActive
                && state.Fans == FanMode.Automatic,
            "Custom performance requires confirmed Automatic fans.",
            applyMaximumTiming: false,
            verifyFinalState: true,
            cancellationToken,
            hyperBoostExitActions:
            [
                HyperBoostRestoreFirstAction,
                HyperBoostRestoreSecondAction,
            ],
            hyperBoostExitCatalog: hyperBoostCatalog);
    }

    public Task SetFanModeAsync(
        FanMode value,
        CancellationToken cancellationToken)
    {
        if (admittedFanModes is not null && !admittedFanModes.Contains(value))
        {
            return Task.FromException(Unavailable(
                $"Fan mode '{value}' is not admitted for production use."));
        }

        IReadOnlyList<string>? actions = value switch
        {
            // The verified command that exits fixed maximum on this device is
            // the balanced automatic thermal state.
            FanMode.Automatic =>
                [PerformanceBalancedFirstAction, PerformanceBalancedSecondAction],
            FanMode.Maximum =>
                [
                    FanMaximumStateFirstAction,
                    FanMaximumStateSecondAction,
                    FanMaximumSpeedFirstAction,
                    FanMaximumSpeedSecondAction,
                ],
            _ => null,
        };
        return actions is null
            ? Task.FromException(Unavailable(
                $"Fan mode '{value}' is not admitted for production use."))
            : ExecuteThermalSettersAsync(
                actions,
                state => state.Fans == value && !state.HyperBoostActive,
                value == FanMode.Maximum
                    ? state => state.Performance == PerformancePreset.Balanced
                        && state.Fans == FanMode.Automatic
                    : state => !state.HyperBoostActive,
                value == FanMode.Maximum
                    ? "Maximum fan mode requires confirmed Balanced performance and Automatic fans."
                    : "Automatic fan mode could not be safely applied.",
                applyMaximumTiming: value == FanMode.Maximum,
                verifyFinalState: false,
                cancellationToken);
    }

    public Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (admittedFanModes is not null && !admittedFanModes.Contains(value.Mode))
        {
            return Task.FromException(Unavailable(
                $"Fan mode '{value.Mode}' is not admitted for production use."));
        }

        return value.Mode switch
        {
            FanMode.Automatic => SetFanModeAsync(FanMode.Automatic, cancellationToken),
            FanMode.Maximum => SetFanTargetsWithStateAsync(
                fanSpeedRange.MaximumRpm,
                fanSpeedRange.MaximumRpm,
                cancellationToken),
            FanMode.Fixed => SetFanTargetsWithStateAsync(
                value.FixedSpeedRpm,
                value.FixedSpeedRpm,
                cancellationToken),
            // Enter manual mode at maximum. The service-owned curve loop lowers
            // the targets only after obtaining fresh temperature readings.
            FanMode.ManualCurve => SetFanTargetsWithStateAsync(
                fanSpeedRange.MaximumRpm,
                fanSpeedRange.MaximumRpm,
                cancellationToken),
            _ => Task.FromException(Unavailable(
                $"Fan mode '{value.Mode}' is not admitted for production use.")),
        };
    }

    public Task SetFanTargetsAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken)
    {
        ValidateFanSpeed(fan1Rpm, nameof(fan1Rpm));
        ValidateFanSpeed(fan2Rpm, nameof(fan2Rpm));
        return ExecuteDynamicFanCommandsAsync(
            [
                CreateFanSpeedCommand(ThermalChannel.First, fan1Rpm),
                CreateFanSpeedCommand(ThermalChannel.Second, fan2Rpm),
            ],
            requireAutomaticEntry: false,
            cancellationToken);
    }

    private Task SetFanTargetsWithStateAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken)
    {
        ValidateFanSpeed(fan1Rpm, nameof(fan1Rpm));
        ValidateFanSpeed(fan2Rpm, nameof(fan2Rpm));
        return ExecuteDynamicFanCommandsAsync(
            [
                RequiredCommand(FanMaximumStateFirstAction),
                RequiredCommand(FanMaximumStateSecondAction),
                CreateFanSpeedCommand(ThermalChannel.First, fan1Rpm),
                CreateFanSpeedCommand(ThermalChannel.Second, fan2Rpm),
            ],
            requireAutomaticEntry: true,
            cancellationToken);
    }

    private RazerCommandDefinition RequiredCommand(string action)
    {
        if (!catalog.TryGet(action, out RazerCommandDefinition? command) || command is null)
        {
            throw Unavailable(
                $"Thermal action '{action}' is not admitted for production use.");
        }

        return command;
    }

    private RazerCommandDefinition CreateFanSpeedCommand(
        ThermalChannel channel,
        int speedRpm)
    {
        string templateAction = channel == ThermalChannel.First
            ? FanMaximumSpeedFirstAction
            : FanMaximumSpeedSecondAction;
        RazerCommandDefinition template = RequiredCommand(templateAction);
        byte[] payload = [0x01, (byte)channel, checked((byte)(speedRpm / 100))];
        return new RazerCommandDefinition(
            template.CommandClass,
            template.CommandId,
            payload,
            payload);
    }

    private void ValidateFanSpeed(int speedRpm, string parameterName)
    {
        if (!fanSpeedRange.Supports(speedRpm))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                speedRpm,
                $"Fan speed is outside the admitted {fanSpeedRange.MinimumRpm:N0}-"
                + $"{fanSpeedRange.MaximumRpm:N0} RPM range or is not a "
                + $"{fanSpeedRange.StepRpm:N0} RPM step.");
        }
    }

    private async Task ExecuteDynamicFanCommandsAsync(
        IReadOnlyList<RazerCommandDefinition> commands,
        bool requireAutomaticEntry,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw KnownNotApplied("The fan write was canceled before submission.");
        }

        await EnterForWriteAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            EnsureUsableForWrite();
            ProductionPerformanceFanState current;
            try
            {
                current = await QueryPerformanceFanStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    BladeWriteFailurePhase.ThermalPreWriteRead,
                    "The adjustable fan pre-write read failed before submission.",
                    exception);
            }
            bool manual = current.Fans == FanMode.Maximum;
            if (requireAutomaticEntry)
            {
                if (!manual
                    && (current.Performance != PerformancePreset.Balanced
                        || current.Fans != FanMode.Automatic))
                {
                    throw KnownNotApplied(
                        "Fixed and manual fan control require confirmed Balanced performance and Automatic fans.");
                }
            }
            else if (!manual)
            {
                throw KnownNotApplied(
                    "Manual fan targets were not written because firmware is no longer in manual fan mode.");
            }

            int firstCommand = manual && requireAutomaticEntry ? 2 : 0;
            for (int index = firstCommand; index < commands.Count; index++)
            {
                RazerCommandDefinition command = commands[index];
                cancellationToken.ThrowIfCancellationRequested();
                submitted = true;
                HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
                    new RazerCommandSpec(
                        command.CommandClass,
                        command.CommandId,
                        command.Payload.ToArray(),
                        command.Payload.Length,
                        payload => payload.Span.SequenceEqual(command.Payload.AsSpan())),
                    AllocateTransaction,
                    exchangeTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (result.Failure is not null
                    || !result.Value.Span.SequenceEqual(command.Payload.AsSpan()))
                {
                    poisoned = true;
                    bool manualEntryCommand = requireAutomaticEntry && index < 2;
                    string stage = manualEntryCommand ? "manual entry" : "fan target";
                    int stageIndex = manualEntryCommand
                        ? index + 1
                        : requireAutomaticEntry
                            ? index - 1
                            : index + 1;
                    string failure = result.Failure?.ToString()
                        ?? "UnexpectedAcknowledgement";
                    BladeWriteFailurePhase failurePhase = manualEntryCommand
                        ? index == 0
                            ? BladeWriteFailurePhase
                                .AdjustableFanFirstManualEntrySubmission
                            : BladeWriteFailurePhase
                                .AdjustableFanSecondManualEntrySubmission
                        : BladeWriteFailurePhase.Unspecified;
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        failurePhase,
                        "The adjustable fan write outcome is indeterminate after "
                        + $"{stage} channel {stageIndex} of 2 ({failure}).");
                }

                if (index < commands.Count - 1)
                {
                    await Task.Delay(
                        ThermalWriteTiming.MaximumInterCommandDelay(index),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (submitted)
            {
                poisoned = true;
                throw Indeterminate(
                    "The adjustable fan write was canceled after submission became possible.");
            }

            throw KnownNotApplied("The fan write was canceled before submission.");
        }
        catch (Exception exception)
        {
            if (submitted)
            {
                poisoned = true;
            }

            throw new BladeWriteException(
                submitted
                    ? WriteSubmissionState.Indeterminate
                    : WriteSubmissionState.KnownNotApplied,
                submitted
                    ? "The adjustable fan write outcome is indeterminate."
                    : "The adjustable fan write was not applied.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task SetProtectionOffAsync(CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsTemporaryFullChargeQuery)
        {
            return Task.FromException(Unavailable(
                "Battery protection-off is unavailable without admitted temporary-state readback."));
        }

        return ExecuteSetterAsync(ProtectionOffAction, cancellationToken);
    }

    public Task SetTemporaryFullChargeAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!Capabilities.SupportsTemporaryFullCharge)
        {
            return Task.FromException(Unavailable(
                "Temporary full charge requires admitted enable, cancel, and readback commands."));
        }

        return ExecuteSetterAsync(
            enabled ? TemporaryEnableAction : TemporaryCancelAction,
            cancellationToken);
    }

    public Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed())
        {
            return Task.FromException(KnownNotApplied(
                "The one-shot laptop control request is invalid."));
        }

        string? action = value.Control switch
        {
            OneShotLaptopControl.StartupAnimation => value.Enabled is true
                ? StartupAnimationOnAction
                : StartupAnimationOffAction,
            OneShotLaptopControl.FunctionKeyPrimary => value.FunctionKeyMode switch
            {
                FunctionKeyPrimaryMode.FunctionKeys => FunctionKeysPrimaryAction,
                FunctionKeyPrimaryMode.MultimediaKeys => MultimediaKeysPrimaryAction,
                _ => null,
            },
            OneShotLaptopControl.GamingMode => value.Enabled is true
                ? GamingModeOnAction
                : GamingModeOffAction,
            OneShotLaptopControl.MicrophoneMuteIndicator => value.Enabled is true
                ? MicrophoneMuteIndicatorOnAction
                : MicrophoneMuteIndicatorOffAction,
            _ => null,
        };
        if (action is null
            || !catalog.TryGet(action, out RazerCommandDefinition? command)
            || command is null
            || !command.OneShotOnly)
        {
            return Task.FromException(Unavailable(
                $"One-shot laptop control '{value.Control}' is not admitted for production use."));
        }

        return ExecuteSetterAsync(action, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleSync)
        {
            if (disposalTask is null)
            {
                closing = true;
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ownedSession.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycleSync)
            {
                disposed = true;
            }

            operationGate.Release();
        }
    }

    internal static DeviceCapabilities CreateCapabilities(
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        FanSpeedRange? fanSpeedRange = null,
        IReadOnlySet<FanMode>? admittedFanModes = null,
        CustomPerformanceCapabilities? customPerformanceCapabilities = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null,
        DeviceFeatureAccess? hyperBoostAccess = null,
        DeviceFeatureAccess? hyperBoostWithoutCoolingPadAccess = null,
        HyperBoostBaseModeCapabilities? hyperBoostBaseModes = null)
    {
        var limits = new HashSet<int>();
        foreach ((int percent, string action) in ChargeLimitActions)
        {
            if (catalog.TryGet(action, out _))
            {
                limits.Add(percent);
            }
        }

        bool protectionOff = readEvidence.SupportsTemporaryFullChargeQuery
            && catalog.TryGet(ProtectionOffAction, out _);
        bool temporaryFullCharge = readEvidence.SupportsTemporaryFullChargeQuery
            && catalog.TryGet(TemporaryEnableAction, out _)
            && catalog.TryGet(TemporaryCancelAction, out _);

        var performance = new HashSet<PerformancePreset>();
        if (HasPair(catalog, PerformanceBalancedFirstAction, PerformanceBalancedSecondAction))
        {
            performance.Add(PerformancePreset.Balanced);
        }

        if (HasPair(catalog, PerformanceSilentFirstAction, PerformanceSilentSecondAction))
        {
            performance.Add(PerformancePreset.Silent);
        }

        if (HasPair(catalog, PerformanceBoostFirstAction, PerformanceBoostSecondAction))
        {
            performance.Add(PerformancePreset.Performance);
        }

        if (HasPair(
                catalog,
                PerformanceBatterySaverFirstAction,
                PerformanceBatterySaverSecondAction))
        {
            performance.Add(PerformancePreset.BatterySaver);
        }

        if (HasPair(
                catalog,
                PerformanceBatteryBalancedFirstAction,
                PerformanceBatteryBalancedSecondAction))
        {
            performance.Add(PerformancePreset.BatteryBalanced);
        }

        if (HasPair(catalog, PerformanceCustomFirstAction, PerformanceCustomSecondAction)
            && HasAll(
                catalog,
                CustomCpuLowAction,
                CustomCpuMediumAction,
                CustomCpuHighAction,
                CustomGpuLowAction,
                CustomGpuMediumAction,
                CustomGpuHighAction))
        {
            performance.Add(PerformancePreset.Custom);
        }

        var fans = new HashSet<FanMode>();
        if (performance.Contains(PerformancePreset.Balanced))
        {
            fans.Add(FanMode.Automatic);
            if (HasAll(
                    catalog,
                    FanMaximumStateFirstAction,
                    FanMaximumStateSecondAction,
                    FanMaximumSpeedFirstAction,
                    FanMaximumSpeedSecondAction))
            {
                fans.Add(FanMode.Maximum);
                // Multiple captured fixed targets (2,000, 2,100, 3,700 and
                // 5,400 RPM) establish the target-byte formula and bounds.
                // Synapse uses the same admitted state/speed commands for its
                // host-rendered manual curves.
                fans.Add(FanMode.Fixed);
                fans.Add(FanMode.ManualCurve);
            }
        }

        if (admittedFanModes is not null)
        {
            fans.IntersectWith(admittedFanModes);
        }

        FanSpeedRange? admittedFanSpeedRange =
            fans.Any(mode => mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum)
                ? fanSpeedRange ?? FanSpeedRange.Standard
                : null;

        var oneShotControls = new HashSet<OneShotLaptopControl>();
        if (HasOneShotPair(catalog, StartupAnimationOnAction, StartupAnimationOffAction))
        {
            oneShotControls.Add(OneShotLaptopControl.StartupAnimation);
        }
        if (HasOneShotPair(catalog, FunctionKeysPrimaryAction, MultimediaKeysPrimaryAction))
        {
            oneShotControls.Add(OneShotLaptopControl.FunctionKeyPrimary);
        }
        if (HasOneShotPair(catalog, GamingModeOnAction, GamingModeOffAction))
        {
            oneShotControls.Add(OneShotLaptopControl.GamingMode);
        }
        if (HasOneShotPair(
                catalog,
                MicrophoneMuteIndicatorOnAction,
                MicrophoneMuteIndicatorOffAction))
        {
            oneShotControls.Add(OneShotLaptopControl.MicrophoneMuteIndicator);
        }

        return new DeviceCapabilities(
            performance,
            fans,
            new HashSet<LightingEffect>(),
            limits,
            protectionOff,
            SupportsTemporaryFullCharge: temporaryFullCharge)
        {
            OneShotLaptopControls = oneShotControls,
            FanSpeedRange = admittedFanSpeedRange,
            CustomPerformance = performance.Contains(PerformancePreset.Custom)
                ? customPerformanceCapabilities
                : null,
            CpuPowerLimit = cpuPowerLimitCapabilities,
            CpuVoltageOptimizer = cpuVoltageOptimizerCapabilities,
            GpuOverclock = gpuOverclockCapabilities,
            HyperBoostAccess = hyperBoostAccess,
            HyperBoostWithoutCoolingPadAccess = hyperBoostWithoutCoolingPadAccess,
            HyperBoostBaseModes = hyperBoostBaseModes,
            ObservedPowerSourceBehaviors = observedPowerSourceBehaviors is null
                ? []
                : [.. observedPowerSourceBehaviors],
        };
    }

    private static bool HasPair(
        DeviceCommandCatalog catalog,
        string firstAction,
        string secondAction) =>
        catalog.TryGet(firstAction, out _)
        && catalog.TryGet(secondAction, out _);

    internal static bool HasHyperBoostCommandSet(DeviceCommandCatalog catalog) =>
        HasPair(catalog, HyperBoostEnableFirstAction, HyperBoostEnableSecondAction)
        && HasPair(catalog, HyperBoostRestoreFirstAction, HyperBoostRestoreSecondAction);

    internal static bool HasHyperBoostCommandSet(
        DeviceCommandCatalog hyperBoostCatalog,
        DeviceCommandCatalog restorationCatalog,
        HyperBoostBaseModeCapabilities? baseModes)
    {
        if (baseModes is null)
        {
            return HasHyperBoostCommandSet(hyperBoostCatalog);
        }
        PerformancePreset[] writable = new[]
        {
            PerformancePreset.Silent, PerformancePreset.Balanced, PerformancePreset.Performance,
            PerformancePreset.Custom,
        }.Where(mode => baseModes.AccessFor(mode) == DeviceFeatureAccess.ReadWrite).ToArray();
        return HasPair(hyperBoostCatalog, HyperBoostEnableFirstAction, HyperBoostEnableSecondAction)
            && writable.Length > 0
            && writable.All(mode => mode == PerformancePreset.Balanced
                ? HasPair(hyperBoostCatalog, HyperBoostRestoreFirstAction, HyperBoostRestoreSecondAction)
                    || HasPerformancePair(restorationCatalog, mode)
                : HasPerformancePair(restorationCatalog, mode));
    }

    internal static bool HasPerformancePair(
        DeviceCommandCatalog catalog,
        PerformancePreset preset) => preset switch
        {
            PerformancePreset.Silent =>
                HasPair(catalog, PerformanceSilentFirstAction, PerformanceSilentSecondAction),
            PerformancePreset.Balanced =>
                HasPair(catalog, PerformanceBalancedFirstAction, PerformanceBalancedSecondAction),
            PerformancePreset.Performance =>
                HasPair(catalog, PerformanceBoostFirstAction, PerformanceBoostSecondAction),
            PerformancePreset.Custom =>
                HasPair(catalog, PerformanceCustomFirstAction, PerformanceCustomSecondAction),
            _ => false,
        };

    private static bool HasOneShotPair(
        DeviceCommandCatalog catalog,
        string firstAction,
        string secondAction) =>
        catalog.TryGet(firstAction, out RazerCommandDefinition? first)
        && first is { OneShotOnly: true }
        && catalog.TryGet(secondAction, out RazerCommandDefinition? second)
        && second is { OneShotOnly: true };

    private static bool HasAll(
        DeviceCommandCatalog catalog,
        params string[] actions) =>
        actions.All(action => catalog.TryGet(action, out _));

    private static readonly (int Percent, string Action)[] ChargeLimitActions =
    [
        (50, Limit50Action),
        (55, Limit55Action),
        (60, Limit60Action),
        (65, Limit65Action),
        (70, Limit70Action),
        (75, Limit75Action),
        (80, Limit80Action),
    ];

    private static string? ChargeLimitAction(int percent)
    {
        foreach ((int admittedPercent, string action) in ChargeLimitActions)
        {
            if (admittedPercent == percent)
            {
                return action;
            }
        }

        return null;
    }

    private static string? CustomPerformanceAction(
        CustomPerformanceDomain domain,
        CustomPerformanceLevel level) => (domain, level) switch
        {
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.Low) =>
                CustomCpuLowAction,
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.Medium) =>
                CustomCpuMediumAction,
            (CustomPerformanceDomain.Cpu, CustomPerformanceLevel.High) =>
                CustomCpuHighAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.Low) =>
                CustomGpuLowAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.Medium) =>
                CustomGpuMediumAction,
            (CustomPerformanceDomain.Gpu, CustomPerformanceLevel.High) =>
                CustomGpuHighAction,
            _ => null,
        };

    private async Task ExecuteSetterAsync(
        string action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw KnownNotApplied("The battery write was canceled before submission.");
        }

        if (!catalog.TryGet(action, out RazerCommandDefinition? command) || command is null)
        {
            throw Unavailable($"Battery action '{action}' is not admitted for production use.");
        }

        await EnterForWriteAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureUsableForWrite();
            HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
                new RazerCommandSpec(
                    command.CommandClass,
                    command.CommandId,
                    command.Payload.ToArray(),
                    command.ExpectedResponsePayload.Length,
                    payload => payload.Span.SequenceEqual(
                        command.ExpectedResponsePayload.AsSpan())),
                AllocateTransaction,
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);

            if (result.Failure is not null)
            {
                BladeWriteException failure = MapWriteFailure(
                    result.Failure.Value,
                    result.Diagnostic);
                if (failure.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    poisoned = true;
                }

                throw failure;
            }

            if (!result.Value.Span.SequenceEqual(command.ExpectedResponsePayload.AsSpan()))
            {
                poisoned = true;
                throw Indeterminate(
                    "The acknowledged payload did not match the admitted command response.");
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            poisoned = true;
            throw Indeterminate("The battery write was canceled after submission became possible.");
        }
        catch (Exception exception)
        {
            poisoned = true;
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The battery write outcome is indeterminate.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task ExecuteThermalSettersAsync(
        IReadOnlyList<string> actions,
        Func<ProductionPerformanceFanState, bool> alreadyMatches,
        Func<ProductionPerformanceFanState, bool> precondition,
        string preconditionFailure,
        bool applyMaximumTiming,
        bool verifyFinalState,
        CancellationToken cancellationToken,
        DeviceCommandCatalog? commandCatalog = null,
        Func<CancellationToken, Task>? validateEligibility = null,
        IReadOnlyList<string>? hyperBoostExitActions = null,
        DeviceCommandCatalog? hyperBoostExitCatalog = null,
        bool revalidateEligibilityPerCommand = false,
        Func<CoolingPadConnectionState>? requiredPadStatePerCommand = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw KnownNotApplied("The thermal write was canceled before submission.");
        }

        DeviceCommandCatalog admittedCatalog = commandCatalog ?? catalog;
        var commands = new RazerCommandDefinition[actions.Count];
        for (int index = 0; index < actions.Count; index++)
        {
            string action = actions[index];
            if (!admittedCatalog.TryGet(action, out RazerCommandDefinition? command)
                || command is null)
            {
                throw Unavailable(
                    $"Thermal action '{action}' is not admitted for production use.");
            }

            commands[index] = command;
        }

        await EnterForWriteAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            EnsureUsableForWrite();
            ThermalModeState currentMode =
                await QueryThermalModeStateCoreAsync(cancellationToken).ConfigureAwait(false);
            ProductionPerformanceFanState current = await MapThermalStateAsync(
                currentMode,
                cancellationToken).ConfigureAwait(false);
            bool exitedHyperBoost = false;
            if (current.HyperBoostActive && hyperBoostExitActions is not null)
            {
                if (hyperBoostExitCatalog is null)
                {
                    throw KnownNotApplied(
                        "HyperBoost cannot be exited without the exact admitted restoration pair.");
                }

                var exitCommands = new RazerCommandDefinition[hyperBoostExitActions.Count];
                for (int index = 0; index < hyperBoostExitActions.Count; index++)
                {
                    string action = hyperBoostExitActions[index];
                    if (!hyperBoostExitCatalog.TryGet(
                            action,
                            out RazerCommandDefinition? command)
                        || command is null)
                    {
                        throw KnownNotApplied(
                            "HyperBoost cannot be exited without the exact admitted restoration pair.");
                    }

                    exitCommands[index] = command;
                }

                foreach (RazerCommandDefinition command in exitCommands)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    submitted = true;
                    HidSessionResult<ReadOnlyMemory<byte>> result =
                        await ExecuteThermalCommandAsync(command, cancellationToken)
                            .ConfigureAwait(false);
                    if (result.Failure is not null
                        || !result.Value.Span.SequenceEqual(command.Payload.AsSpan()))
                    {
                        poisoned = true;
                        throw Indeterminate(
                            "The HyperBoost restoration outcome is indeterminate.");
                    }
                }

                current = await QueryPerformanceFanStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (current.HyperBoostActive
                    || current.Performance != PerformancePreset.Balanced
                    || current.Fans != FanMode.Automatic)
                {
                    poisoned = true;
                    throw Indeterminate(
                        "HyperBoost restoration did not confirm Balanced performance and Automatic fans.");
                }

                exitedHyperBoost = true;
            }

            if (alreadyMatches(current))
            {
                return;
            }

            if (!precondition(current))
            {
                throw KnownNotApplied(preconditionFailure);
            }

            if (validateEligibility is not null && !revalidateEligibilityPerCommand)
            {
                await validateEligibility(cancellationToken).ConfigureAwait(false);
            }

            for (int index = 0; index < commands.Length; index++)
            {
                RazerCommandDefinition command = commands[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (revalidateEligibilityPerCommand && validateEligibility is not null)
                {
                    await validateEligibility(cancellationToken).ConfigureAwait(false);
                }
                submitted = true;
                HidSessionResult<ReadOnlyMemory<byte>> result =
                    await ExecuteThermalCommandAsync(command, cancellationToken,
                        revalidateEligibilityPerCommand
                            ? () => hyperBoostEligibility?.ObserveCoolingPad()
                                == (requiredPadStatePerCommand?.Invoke()
                                    ?? CoolingPadConnectionState.PresentExact)
                            : null)
                        .ConfigureAwait(false);

                if (result.Failure is not null
                    || !result.Value.Span.SequenceEqual(command.Payload.AsSpan()))
                {
                    poisoned = true;
                    throw Indeterminate(
                        "The multi-command thermal write outcome is indeterminate.");
                }

                if (applyMaximumTiming && index < commands.Length - 1)
                {
                    await Task.Delay(
                        ThermalWriteTiming.MaximumInterCommandDelay(index),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (verifyFinalState || exitedHyperBoost)
            {
                ProductionPerformanceFanState final;
                try
                {
                    final = await QueryPerformanceFanStateCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    poisoned = true;
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        BladeWriteFailurePhase.ThermalFinalConfirmationRead,
                        "The thermal commands were acknowledged, but final state readback failed.",
                        exception);
                }

                if (!alreadyMatches(final))
                {
                    poisoned = true;
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        BladeWriteFailurePhase.ThermalFinalConfirmationRead,
                        "The acknowledged thermal commands did not match the final state readback.");
                }
            }
        }
        catch (BladeWriteException exception)
        {
            if (!submitted || exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                throw;
            }

            poisoned = true;
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The multi-command thermal write outcome is indeterminate.",
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (submitted)
            {
                poisoned = true;
                throw Indeterminate(
                    "The thermal write was canceled after submission became possible.");
            }

            throw KnownNotApplied("The thermal write was canceled before submission.");
        }
        catch (Exception exception)
        {
            if (submitted)
            {
                poisoned = true;
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The multi-command thermal write outcome is indeterminate.",
                    exception);
            }

            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                BladeWriteFailurePhase.ThermalPreWriteRead,
                "The thermal pre-write read failed before submission.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteThermalCommandAsync(
        RazerCommandDefinition command,
        CancellationToken cancellationToken,
        Func<bool>? submissionAllowed = null) => session.ExecuteAsync(
            new RazerCommandSpec(
                command.CommandClass,
                command.CommandId,
                command.Payload.ToArray(),
                command.Payload.Length,
                payload => payload.Span.SequenceEqual(command.Payload.AsSpan()))
            {
                SubmissionAllowed = submissionAllowed,
            },
            AllocateTransaction,
            exchangeTimeout,
            cancellationToken);

    private async Task<CoolingPadConnectionState> ValidateHyperBoostEligibilityAsync(
        PerformancePreset baseMode,
        bool allowPadlessLifecycleValidation,
        CoolingPadConnectionState? lockedPadState,
        CancellationToken cancellationToken)
    {
        CoolingPadConnectionState padState;
        try
        {
            padState = hyperBoostEligibility?.ObserveCoolingPad()
                ?? CoolingPadConnectionState.ObservationFailed;
        }
        catch
        {
            padState = CoolingPadConnectionState.ObservationFailed;
        }

        bool exactPerformanceLifecycle = allowPadlessLifecycleValidation
            && this.allowPadlessHyperBoostLifecycleValidation
            && padlessHyperBoostLifecycleBaseMode == PerformancePreset.Performance;
        bool padPresentEligibility = !exactPerformanceLifecycle && padState == CoolingPadConnectionState.PresentExact;
        if (lockedPadState is { } locked && padState != locked)
        {
            throw KnownNotApplied(
                "The cooling-pad state changed during the HyperBoost transition.");
        }
        bool padAbsentEligibility = padState == CoolingPadConnectionState.Absent
            && (HyperBoostCapabilityPolicy.IsDirectEntryBase(Capabilities, coolingPadPresent: false, baseMode)
                || allowPadlessLifecycleValidation
                    && this.allowPadlessHyperBoostLifecycleValidation
                    && baseMode == padlessHyperBoostLifecycleBaseMode
                    && Capabilities.HyperBoostWithoutCoolingPadAccess
                        == DeviceFeatureAccess.ObservedOnly);
        if (!padPresentEligibility && !padAbsentEligibility)
        {
            throw KnownNotApplied(
                "HyperBoost requires either the exact Razer Laptop Cooling Pad or separately admitted pad-absent evidence.");
        }

        byte transactionId = AllocateTransactionForRead();
        HidSessionResult<AdapterPowerState> adapter =
            await adapterPowerCommands.QueryAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
        if (adapter.Failure is not null)
        {
            HidSessionFailureKind failure = adapter.Failure.Value;
            try
            {
                ThrowReadFailure(failure, adapter.Diagnostic, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                IsTransientHyperBoostAdapterReadFailure(failure))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
                    "The HyperBoost adapter preflight read failed before any HyperBoost command was submitted.",
                    exception);
            }
        }

        bool adapterEligible = padAbsentEligibility
            ? HyperBoostValidationPower.IsAdmittedFullPower(adapter.Value)
                && admittedAdapterPowerStates?.Contains(adapter.Value) == true
            : adapter.Value == AdapterPowerState.FullPower280W;
        if (!adapterEligible)
        {
            throw KnownNotApplied(
                "HyperBoost requires a confirmed admitted full-power adapter.");
        }
        return padState;
    }

    private static bool IsTransientHyperBoostAdapterReadFailure(
        HidSessionFailureKind failure) => failure is
        HidSessionFailureKind.Timeout or
        HidSessionFailureKind.NativeIoFailure;

    private async Task<ProductionPerformanceFanState> QueryPerformanceFanStateCoreAsync(
        CancellationToken cancellationToken)
    {
        ThermalModeState state = await QueryThermalModeStateCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        return await MapThermalStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ThermalModeState> QueryThermalModeStateCoreAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> first = await QueryThermalChannelAsync(
            ThermalChannel.First,
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await QueryThermalChannelAsync(
            ThermalChannel.Second,
            cancellationToken).ConfigureAwait(false);

        if (!ThermalModeProtocol.TryDecodeMatchingPair(
                first.Span,
                second.Span,
                out ThermalModeState state))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Thermal channels did not report one recognized matching state.",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = DeviceStateQuery.ThermalChannelAgreement,
            };
        }

        return state;
    }

    private static ProductionPerformanceFanState MapThermalState(
        ThermalModeState state) => state switch
        {
            ThermalModeState.BalancedAutomatic => new(
                PerformancePreset.Balanced,
                FanMode.Automatic),
            ThermalModeState.SilentAutomatic => new(
                PerformancePreset.Silent,
                FanMode.Automatic),
            ThermalModeState.PerformanceAutomatic => new(
                PerformancePreset.Performance,
                FanMode.Automatic),
            ThermalModeState.BatterySaverAutomatic => new(
                PerformancePreset.BatterySaver,
                FanMode.Automatic),
            ThermalModeState.BatteryBalancedAutomatic => new(
                PerformancePreset.BatteryBalanced,
                FanMode.Automatic),
            ThermalModeState.FixedMaximum => new(null, FanMode.Maximum),
            ThermalModeState.HyperBoostObserved => new(null, FanMode.Automatic)
            {
                HyperBoostActive = true,
            },
            _ => throw new RazerProtocolException(
                "Thermal query returned an undefined state.",
                HidProtocolValidationFailure.PayloadValidator),
        };

    private async Task<ProductionPerformanceFanState> MapThermalStateAsync(
        ThermalModeState state,
        CancellationToken cancellationToken)
    {
        if (state != ThermalModeState.CustomAutomatic)
        {
            return MapThermalState(state);
        }

        CustomPerformanceLevel cpu = await QueryCustomPerformanceLevelAsync(
            CustomPerformanceDomain.Cpu,
            cancellationToken).ConfigureAwait(false);
        CustomPerformanceLevel gpu = await QueryCustomPerformanceLevelAsync(
            CustomPerformanceDomain.Gpu,
            cancellationToken).ConfigureAwait(false);
        var custom = new CustomPerformanceSettings(cpu, gpu);
        if (Capabilities.CustomPerformance?.Supports(custom) == false)
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Custom performance returned a combination not admitted for this exact device.",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = DeviceStateQuery.CustomPerformanceCombination,
            };
        }

        return new ProductionPerformanceFanState(
            PerformancePreset.Custom,
            FanMode.Automatic)
        {
            CustomPerformance = custom,
        };
    }

    private async Task<CustomPerformanceLevel> QueryCustomPerformanceLevelAsync(
        CustomPerformanceDomain domain,
        CancellationToken cancellationToken)
    {
        DeviceStateQuery query = domain == CustomPerformanceDomain.Cpu
            ? DeviceStateQuery.CustomCpuPerformance
            : DeviceStateQuery.CustomGpuPerformance;
        var command = new RazerCommandSpec(
            CustomPerformanceProtocol.CommandClass,
            CustomPerformanceProtocol.QueryCommandId,
            CustomPerformanceProtocol.CreateQueryPayload(domain),
            3,
            payload => CustomPerformanceProtocol.TryDecodeQuery(
                payload.Span,
                domain,
                out _));
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            AllocateTransaction,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken, query);
        }

        if (!CustomPerformanceProtocol.TryDecodeQuery(
                result.Value.Span,
                domain,
                out CustomPerformanceLevel level))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Custom performance query failed payload validation.",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = query,
            };
        }

        return level;
    }

    private async Task<ReadOnlyMemory<byte>> QueryThermalChannelAsync(
        ThermalChannel channel,
        CancellationToken cancellationToken)
    {
        DeviceStateQuery query = channel == ThermalChannel.First
            ? DeviceStateQuery.ThermalChannelOne
            : DeviceStateQuery.ThermalChannelTwo;
        var command = new RazerCommandSpec(
            ThermalModeProtocol.CommandClass,
            ThermalModeProtocol.QueryCommandId,
            ThermalModeProtocol.CreateQueryPayload(channel),
            4,
            payload => ThermalModeProtocol.TryDecodeQuery(
                payload.Span,
                channel,
                out _));
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            AllocateTransaction,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken, query);
        }

        if (!ThermalModeProtocol.TryDecodeQuery(result.Value.Span, channel, out _))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Thermal query failed payload validation.",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = query,
            };
        }

        return result.Value;
    }

    private TransactionIdAssignment AllocateTransaction() =>
        context.TryAllocateTransaction(out byte transactionId)
            ? TransactionIdAssignment.Valid(transactionId)
            : TransactionIdAssignment.Exhausted();

    private byte AllocateTransactionForRead()
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            throw new IOException("Battery query transaction identifiers are exhausted.");
        }

        return transactionId;
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        Task waitForOperation;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
            waitForOperation = operationGate.WaitAsync(cancellationToken);
        }

        await waitForOperation.ConfigureAwait(false);
        try
        {
            ThrowIfClosingOrDisposed();
        }
        catch
        {
            operationGate.Release();
            throw;
        }
    }

    private async Task EnterForWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw KnownNotApplied("The battery write was canceled before submission.");
        }
        catch (ObjectDisposedException)
        {
            throw KnownNotApplied("The battery write was blocked because the session is closing.");
        }
    }

    private void EnsureUsable()
    {
        ThrowIfClosingOrDisposed();
        if (poisoned || session.Disposition == RazerCommandSessionDisposition.RecoveryRequired)
        {
            throw new IOException("The battery control session requires a fresh verified reopen.");
        }
    }

    private void EnsureUsableForWrite()
    {
        try
        {
            EnsureUsable();
        }
        catch (ObjectDisposedException)
        {
            throw Indeterminate("The battery control session was disposed during the operation.");
        }
        catch (IOException)
        {
            throw Indeterminate("The battery control session is poisoned and must be reopened.");
        }
    }

    private void ThrowIfClosingOrDisposed()
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
        }
    }

    private void ThrowReadFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        CancellationToken cancellationToken,
        DeviceStateQuery? query = null)
    {
        if (failure is not (
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled))
        {
            poisoned = true;
        }

        switch (failure)
        {
            case HidSessionFailureKind.Canceled when cancellationToken.IsCancellationRequested:
            case HidSessionFailureKind.QueuedCanceled when cancellationToken.IsCancellationRequested:
                throw new OperationCanceledException(cancellationToken);
            case HidSessionFailureKind.Timeout:
                throw new TimeoutException("Battery state query timed out.");
            case HidSessionFailureKind.ProtocolFailure:
                throw new RazerProtocolException(
                    "Battery state query failed protocol validation.",
                    diagnostic?.ProtocolValidationFailure
                        ?? HidProtocolValidationFailure.PayloadValidator)
                {
                    StateQuery = query,
                };
            default:
                throw new IOException("Battery state query failed closed.");
        }
    }

    private BladeWriteException MapWriteFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic)
    {
        bool knownNotApplied = failure is
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled
            or HidSessionFailureKind.Exhausted
            || (failure == HidSessionFailureKind.Canceled
                && diagnostic is null
                && session.Disposition == RazerCommandSessionDisposition.Healthy)
            || (failure == HidSessionFailureKind.ProtocolFailure
                && diagnostic?.ProtocolValidationFailure == HidProtocolValidationFailure.Status);

        return new BladeWriteException(
            knownNotApplied
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate,
            knownNotApplied
                ? "The battery write was definitively not applied."
                : "The battery write outcome is indeterminate.");
    }

    private static BladeWriteException KnownNotApplied(string message) => new(
        WriteSubmissionState.KnownNotApplied,
        message);

    private static CapabilityUnavailableException Unavailable(string message) => new(message);

    private static BladeWriteException Indeterminate(string message) => new(
        WriteSubmissionState.Indeterminate,
        message);

    private static ConfirmedBatteryState MapProtectionState(
        BatteryValidationSetting setting) => setting switch
        {
            BatteryValidationSetting.ProtectionOff => new(false, null, null),
            BatteryValidationSetting.Limit50 => new(true, 50, null),
            BatteryValidationSetting.Limit55 => new(true, 55, null),
            BatteryValidationSetting.Limit60 => new(true, 60, null),
            BatteryValidationSetting.Limit65 => new(true, 65, null),
            BatteryValidationSetting.Limit70 => new(true, 70, null),
            BatteryValidationSetting.Limit75 => new(true, 75, null),
            BatteryValidationSetting.Limit80 => new(true, 80, null),
            _ => throw new RazerProtocolException(
                "Battery protection query returned an undefined setting.",
                HidProtocolValidationFailure.PayloadValidator),
        };
}
