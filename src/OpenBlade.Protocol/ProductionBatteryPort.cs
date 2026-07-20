using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ProductionBatteryPort : IProductionBatteryPort
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

    private readonly IRazerCommandSession session;
    private readonly DeviceCommandCatalog catalog;
    private readonly ProductionBatteryReadEvidence readEvidence;
    private readonly ValidationRunContext context;
    private readonly TimeSpan exchangeTimeout;
    private readonly BatteryCommandClient batteryCommands;
    private readonly TemporaryFullChargeCommandClient temporaryCommands;
    private readonly AdapterPowerCommandClient adapterPowerCommands;
    private readonly McuVersionCommandClient mcuVersionCommands;
    private readonly StartupAnimationCommandClient startupAnimationCommands;
    private readonly FunctionKeyPrimaryCommandClient functionKeyPrimaryCommands;
    private readonly GamingModeCommandClient gamingModeCommands;
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
        TimeSpan exchangeTimeout)
        : this(session, session, catalog, readEvidence, context, exchangeTimeout)
    {
    }

    internal ProductionBatteryPort(
        ValidationActor actor,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
        : this(actor.Session, actor, catalog, readEvidence, context, exchangeTimeout)
    {
    }

    private ProductionBatteryPort(
        IRazerCommandSession session,
        IAsyncDisposable ownedSession,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.ownedSession = ownedSession ?? throw new ArgumentNullException(nameof(ownedSession));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.readEvidence = readEvidence ?? throw new ArgumentNullException(nameof(readEvidence));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
        batteryCommands = new BatteryCommandClient(session, exchangeTimeout);
        temporaryCommands = new TemporaryFullChargeCommandClient(session, exchangeTimeout);
        adapterPowerCommands = new AdapterPowerCommandClient(session, exchangeTimeout);
        mcuVersionCommands = new McuVersionCommandClient(session, exchangeTimeout);
        startupAnimationCommands = new StartupAnimationCommandClient(
            session,
            exchangeTimeout);
        functionKeyPrimaryCommands = new FunctionKeyPrimaryCommandClient(
            session,
            exchangeTimeout);
        gamingModeCommands = new GamingModeCommandClient(session, exchangeTimeout);
        Capabilities = CreateCapabilities(catalog, readEvidence);
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
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
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
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
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
            return Task.FromException(KnownNotApplied(
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
            ? Task.FromException(KnownNotApplied(
                $"Performance preset '{value}' is not admitted for production use."))
            : ExecuteThermalSettersAsync(
                actions,
                state => state.Performance == value && state.Fans == FanMode.Automatic,
                state => state.Performance is not null && state.Fans == FanMode.Automatic,
                "Performance changes require a confirmed Automatic thermal state.",
                applyMaximumTiming: false,
                verifyFinalState: value is PerformancePreset.BatterySaver
                    or PerformancePreset.BatteryBalanced,
                cancellationToken);
    }

    public Task SetCustomPerformanceAsync(
        CustomPerformanceSettings value,
        CancellationToken cancellationToken)
    {
        if (!CustomPerformanceSettings.IsWellFormed(value))
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
            return Task.FromException(KnownNotApplied(
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
            state => state.Fans == FanMode.Automatic,
            "Custom performance requires confirmed Automatic fans.",
            applyMaximumTiming: false,
            verifyFinalState: true,
            cancellationToken);
    }

    public Task SetFanModeAsync(
        FanMode value,
        CancellationToken cancellationToken)
    {
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
            ? Task.FromException(KnownNotApplied(
                $"Fan mode '{value}' is not admitted for production use."))
            : ExecuteThermalSettersAsync(
                actions,
                state => state.Fans == value,
                value == FanMode.Maximum
                    ? state => state.Performance == PerformancePreset.Balanced
                        && state.Fans == FanMode.Automatic
                    : _ => true,
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
        return value.Mode switch
        {
            FanMode.Automatic => SetFanModeAsync(FanMode.Automatic, cancellationToken),
            FanMode.Maximum => SetFanTargetsWithStateAsync(
                FanControlSettings.MaximumSpeedRpm,
                FanControlSettings.MaximumSpeedRpm,
                cancellationToken),
            FanMode.Fixed => SetFanTargetsWithStateAsync(
                value.FixedSpeedRpm,
                value.FixedSpeedRpm,
                cancellationToken),
            // Enter manual mode at maximum. The service-owned curve loop lowers
            // the targets only after obtaining fresh temperature readings.
            FanMode.ManualCurve => SetFanTargetsWithStateAsync(
                FanControlSettings.MaximumSpeedRpm,
                FanControlSettings.MaximumSpeedRpm,
                cancellationToken),
            _ => Task.FromException(KnownNotApplied(
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
            throw KnownNotApplied(
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

    private static void ValidateFanSpeed(int speedRpm, string parameterName)
    {
        if (speedRpm is < FanControlSettings.MinimumSpeedRpm
            or > FanControlSettings.MaximumSpeedRpm
            || speedRpm % FanControlSettings.SpeedStepRpm != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                speedRpm,
                "Fan speed is outside the captured 2,000-5,400 RPM range or is not a 100 RPM step.");
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
            ProductionPerformanceFanState current =
                await QueryPerformanceFanStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
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
                    throw Indeterminate(
                        "The adjustable fan write outcome is indeterminate.");
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
            return Task.FromException(KnownNotApplied(
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
            return Task.FromException(KnownNotApplied(
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
            _ => null,
        };
        if (action is null
            || !catalog.TryGet(action, out RazerCommandDefinition? command)
            || command is null
            || !command.OneShotOnly)
        {
            return Task.FromException(KnownNotApplied(
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
        ProductionBatteryReadEvidence readEvidence)
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

        return new DeviceCapabilities(
            performance,
            fans,
            new HashSet<LightingEffect>(),
            limits,
            protectionOff,
            SupportsTemporaryFullCharge: temporaryFullCharge)
        {
            OneShotLaptopControls = oneShotControls,
        };
    }

    private static bool HasPair(
        DeviceCommandCatalog catalog,
        string firstAction,
        string secondAction) =>
        catalog.TryGet(firstAction, out _)
        && catalog.TryGet(secondAction, out _);

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
            throw KnownNotApplied($"Battery action '{action}' is not admitted for production use.");
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
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw KnownNotApplied("The thermal write was canceled before submission.");
        }

        var commands = new RazerCommandDefinition[actions.Count];
        for (int index = 0; index < actions.Count; index++)
        {
            string action = actions[index];
            if (!catalog.TryGet(action, out RazerCommandDefinition? command)
                || command is null)
            {
                throw KnownNotApplied(
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
            if (alreadyMatches(current))
            {
                return;
            }

            if (!precondition(current))
            {
                throw KnownNotApplied(preconditionFailure);
            }

            for (int index = 0; index < commands.Length; index++)
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

            if (verifyFinalState)
            {
                ProductionPerformanceFanState final =
                    await QueryPerformanceFanStateCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (!alreadyMatches(final))
                {
                    poisoned = true;
                    throw Indeterminate(
                        "The final thermal state did not match the admitted target.");
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
            }

            throw new BladeWriteException(
                submitted
                    ? WriteSubmissionState.Indeterminate
                    : WriteSubmissionState.KnownNotApplied,
                submitted
                    ? "The multi-command thermal write outcome is indeterminate."
                    : "The thermal write was not applied.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

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
                HidProtocolValidationFailure.PayloadValidator);
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
        if ((cpu, gpu) is (CustomPerformanceLevel.High, CustomPerformanceLevel.High))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Custom performance returned the unsupported CPU High and GPU High combination.",
                HidProtocolValidationFailure.PayloadValidator);
        }

        return new ProductionPerformanceFanState(
            PerformancePreset.Custom,
            FanMode.Automatic)
        {
            CustomPerformance = new CustomPerformanceSettings(cpu, gpu),
        };
    }

    private async Task<CustomPerformanceLevel> QueryCustomPerformanceLevelAsync(
        CustomPerformanceDomain domain,
        CancellationToken cancellationToken)
    {
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
            ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
        }

        if (!CustomPerformanceProtocol.TryDecodeQuery(
                result.Value.Span,
                domain,
                out CustomPerformanceLevel level))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Custom performance query failed payload validation.",
                HidProtocolValidationFailure.PayloadValidator);
        }

        return level;
    }

    private async Task<ReadOnlyMemory<byte>> QueryThermalChannelAsync(
        ThermalChannel channel,
        CancellationToken cancellationToken)
    {
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
            ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
        }

        if (!ThermalModeProtocol.TryDecodeQuery(result.Value.Span, channel, out _))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Thermal query failed payload validation.",
                HidProtocolValidationFailure.PayloadValidator);
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
        CancellationToken cancellationToken)
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
                        ?? HidProtocolValidationFailure.PayloadValidator);
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
