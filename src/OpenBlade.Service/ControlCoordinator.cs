using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// EffectiveDesired is the post-normalization, post-capability-filtered state
// used by the coordinator. Runtime reconciliation must not compare a later
// observation against optional intent that the coordinator dropped.
internal sealed record ScopedApplyOutcome(
    ControlResult Result,
    ConfirmedDeviceState? Readback,
    bool WriteAttempted,
    bool ReadbackMatched,
    DeviceCapabilities? Capabilities,
    bool DeviceAccessFailed,
    IReadOnlySet<DeviceSubsystem>? ConfirmedCoupledWrites = null,
    bool ConfirmationUnavailable = false,
    ConfirmedDeviceState? PreWriteState = null,
    DesiredDeviceState? EffectiveDesired = null,
    WriteSubmissionState? WriteSubmissionState = null,
    BladeWriteFailurePhase? WriteFailurePhase = null);

internal sealed record FullTransitionApplyOutcome(
    SubsystemApplyResults Results,
    ConfirmedDeviceState? Readback,
    IReadOnlySet<DeviceSubsystem> WriteAttempted,
    IReadOnlySet<DeviceSubsystem> ReadbackMatched,
    IReadOnlySet<DeviceSubsystem> ReadbackContradicted,
    DeviceCapabilities? Capabilities,
    bool DeviceAccessFailed,
    IReadOnlySet<DeviceSubsystem>? ConfirmationUnavailable = null,
    ConfirmedDeviceState? PreWriteState = null,
    DesiredDeviceState? EffectiveDesired = null);

internal sealed class IntentionalWriteCanceledException(
    IReadOnlySet<DeviceSubsystem> writeAttempted,
    CancellationToken cancellationToken,
    OperationCanceledException innerException)
    : OperationCanceledException(
        "An intentional hardware write was canceled before it could be confirmed.",
        innerException,
        cancellationToken)
{
    public IReadOnlySet<DeviceSubsystem> WriteAttempted { get; } = writeAttempted;
}

internal sealed class WriteAuthorityLostException : InvalidOperationException
{
    public WriteAuthorityLostException(
        string message = "The active Windows session changed before the recovery write.")
        : base(message)
    {
    }
}

public sealed class ControlCoordinator
{
    private readonly IBladeDevice device;
    private readonly Func<CancellationToken, Task<ConfirmedDeviceState>>?
        scopedStateReader;
    private readonly bool resolveIndeterminatePerformanceWrites;
    private readonly bool resolveIndeterminateAutomaticFanWrites;
    private readonly bool singleAttemptPerformanceWrites;
    private readonly bool singleAttemptFanWrites;
    private readonly ThermalTransitionStepRunner? thermalStepRunner;
    private readonly Func<string?>? performanceWriteBlockReason;
    private readonly Func<string?>? fanWriteBlockReason;

    public ControlCoordinator(IBladeDevice device)
        : this(device, scopedStateReader: null)
    {
    }

    internal ControlCoordinator(
        IBladeDevice device,
        Func<CancellationToken, Task<ConfirmedDeviceState>>? scopedStateReader,
        bool resolveIndeterminatePerformanceWrites = false,
        bool resolveIndeterminateAutomaticFanWrites = false,
        bool singleAttemptPerformanceWrites = false,
        bool singleAttemptFanWrites = false,
        ThermalTransitionStepRunner? thermalStepRunner = null,
        Func<string?>? performanceWriteBlockReason = null,
        Func<string?>? fanWriteBlockReason = null)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        this.scopedStateReader = scopedStateReader;
        this.resolveIndeterminatePerformanceWrites =
            resolveIndeterminatePerformanceWrites;
        this.resolveIndeterminateAutomaticFanWrites =
            resolveIndeterminateAutomaticFanWrites;
        this.singleAttemptPerformanceWrites = singleAttemptPerformanceWrites;
        this.singleAttemptFanWrites = singleAttemptFanWrites;
        this.thermalStepRunner = thermalStepRunner;
        this.performanceWriteBlockReason = performanceWriteBlockReason;
        this.fanWriteBlockReason = fanWriteBlockReason;
    }

    internal IBladeDevice ThermalDevice => device;

    public async Task<SubsystemApplyResults> ApplyDesiredStateAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken) =>
        (await ApplyDesiredStateOutcomeAsync(desired, cancellationToken)).Results;

    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) =>
        (await ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            value,
            value == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
            cancellationToken)).Result;

    public async Task<ControlResult> ApplyFanAsync(
        FanMode value,
        CancellationToken cancellationToken) =>
        (await ApplyFanOutcomeAsync(value, cancellationToken)).Result;

    public async Task<ControlResult> ApplyChargeLimitAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken) =>
        (await ApplyChargeLimitOutcomeAsync(value, cancellationToken)).Result;

    public async Task<ControlResult> ApplyBatteryProtectionAsync(
        bool enabled,
        BatteryPolicy storedPolicy,
        CancellationToken cancellationToken) =>
        (await ApplyBatteryProtectionOutcomeAsync(
            enabled,
            storedPolicy,
            cancellationToken)).Result;

    public async Task<ControlResult> ApplyTemporaryFullChargeAsync(
        bool enabled,
        CancellationToken cancellationToken) =>
        (await ApplyTemporaryFullChargeOutcomeAsync(enabled, cancellationToken)).Result;

    public async Task<ControlResult> ApplyLightingAsync(
        LightingSettings value,
        CancellationToken cancellationToken) =>
        (await ApplyLightingOutcomeAsync(value, cancellationToken)).Result;

    /// <summary>
    /// Applies a preset without treating omitted GPU tuning as an instruction
    /// to relinquish existing GPU clock-offset management.
    /// </summary>
    internal Task<ScopedApplyOutcome> ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) =>
        ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            value,
            value == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
            cancellationToken);

    /// <summary>
    /// Applies performance without treating omitted standalone GPU tuning as
    /// an instruction to relinquish management. Legacy offsets embedded in
    /// custom performance are still normalized and applied.
    /// </summary>
    internal Task<ScopedApplyOutcome> ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        CancellationToken cancellationToken) => ApplyPerformanceOutcomeAsync(
            value,
            customPerformance,
            gpuClockTuning: null,
            relinquishWhenUnconfigured: false,
            cancellationToken);

    /// <summary>
    /// Applies resolved profile or override GPU tuning intent. A null
    /// <paramref name="gpuClockTuning"/> explicitly relinquishes GPU
    /// clock-offset management before reading state.
    /// </summary>
    internal Task<ScopedApplyOutcome> ApplyPerformanceOutcomeAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning,
        CancellationToken cancellationToken) => ApplyPerformanceOutcomeAsync(
            value,
            customPerformance,
            gpuClockTuning,
            relinquishWhenUnconfigured: true,
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyPerformanceOutcomeAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return ApplyPerformanceOutcomeAsync(
            desired.Performance.Value,
            desired.CustomPerformance,
            desired.GpuClockTuning,
            relinquishWhenUnconfigured: true,
            cancellationToken,
            desired);
    }

    private async Task<ScopedApplyOutcome> ApplyPerformanceOutcomeAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning,
        bool relinquishWhenUnconfigured,
        CancellationToken cancellationToken,
        DesiredDeviceState? requestedDesired = null)
    {
        NormalizeLegacyGpuClockTuning(ref customPerformance, ref gpuClockTuning);
        GpuClockTuningSettings? effectiveGpuClockTuning = gpuClockTuning;
        bool PerformanceSupported(DeviceCapabilities capabilities)
        {
            effectiveGpuClockTuning = FilterUnsupportedGpuClockTuning(
                gpuClockTuning,
                capabilities);
            return capabilities.PerformancePresets.Contains(value)
                && (value != PerformancePreset.Custom
                    || capabilities.CustomPerformance?.Supports(customPerformance) != false);
        }

        ScopedApplyOutcome outcome = await ApplyScopedAsync(
            DeviceSubsystem.Performance,
            "Performance",
            IsDefined(value)
                && (value == PerformancePreset.Custom
                    ? CustomPerformanceSettings.IsWellFormed(customPerformance)
                    : customPerformance is null)
                && (gpuClockTuning is null
                    || GpuClockTuningSettings.IsWellFormed(gpuClockTuning)),
            PerformanceSupported,
            state => PerformanceMatches(
                state,
                value,
                customPerformance,
                effectiveGpuClockTuning),
            token => ApplyPerformanceAndGpuClockTuningAsync(
                value,
                customPerformance,
                effectiveGpuClockTuning,
                token),
            cancellationToken,
            state => DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                ? "Performance changes require Automatic fan mode."
                : null,
            stateAwareSetter: (state, token) =>
                CanApplyGpuClockOffsetsOnly(
                    state,
                    value,
                    customPerformance,
                    effectiveGpuClockTuning)
                    ? SetGpuClockOffsetsAsync(
                        effectiveGpuClockTuning!.EffectiveOffsets,
                        effectiveGpuClockTuning.Enabled,
                        token)
                    : ApplyPerformanceAndGpuClockTuningAsync(
                        value,
                        customPerformance,
                        effectiveGpuClockTuning,
                        token),
            beforeInitialStateRead: (_, token) =>
                relinquishWhenUnconfigured && effectiveGpuClockTuning is null
                    ? RelinquishGpuClockOffsetManagementAsync(token)
                    : Task.CompletedTask,
            resolveIndeterminateWriteByReadback:
                resolveIndeterminatePerformanceWrites,
            unchangedAfterIndeterminateWrite: PerformanceStateMatches,
            writeBlockReason: performanceWriteBlockReason,
            returnBlockedOnWriteAuthorityLoss:
                performanceWriteBlockReason is not null)
            .ConfigureAwait(false);
        return requestedDesired is null
            ? outcome
            : outcome with
            {
                EffectiveDesired = requestedDesired with
                {
                    CustomPerformance = customPerformance,
                    GpuClockTuning = effectiveGpuClockTuning,
                },
            };
    }

    internal Task<ScopedApplyOutcome> ApplyFanOutcomeAsync(
        FanMode value,
        CancellationToken cancellationToken) => ApplyFanControlOutcomeAsync(
            FanControlSettings.FromLegacy(value),
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyFanControlOutcomeAsync(
        FanControlSettings value,
        CancellationToken cancellationToken,
        Func<bool>? writeAllowed = null,
        Func<ConfirmedDeviceState, string?>? initialStateBlockReason = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        Func<ConfirmedDeviceState, string?>? manualEntryBlockReason =
            DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                ? state => IsConfirmedManualFanWriteBaseline(state)
                    ? null
                    : "Fixed and manual fan control require confirmed Balanced Automatic fans to enter manual mode, or an already-confirmed manual fan state."
                : null;
        Func<ConfirmedDeviceState, string?>? combinedBlockReason =
            initialStateBlockReason is null
                ? manualEntryBlockReason
                : state => initialStateBlockReason(state)
                    ?? manualEntryBlockReason?.Invoke(state);
        return ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            IsDefined(value.Mode),
            capabilities => capabilities.FanModes.Contains(value.Mode)
                && (value.Mode != FanMode.Fixed
                    || capabilities.FanSpeedRange?.Supports(value.FixedSpeedRpm) == true),
            state => FanModeMatches(state.Fans, value.Mode),
            token => SetFanControlAsync(value, token),
            cancellationToken,
            combinedBlockReason,
            writeAllowed: writeAllowed,
            acceptSetterCompletionWithoutReadback:
                DeviceSubsystemOrder.IsManualFanMode(value.Mode),
            setterCompletionEvidence: DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                ? state => DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                : null,
            // Firmware exposes only Automatic versus manual state. It cannot
            // read back a fixed target or distinguish Fixed, Curve and Maximum,
            // so every explicit manual-mode action must submit its RPM target.
            forceWrite: DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                || initialStateBlockReason is not null,
            resolveIndeterminateWriteByReadback:
                resolveIndeterminateAutomaticFanWrites
                    && value.Mode == FanMode.Automatic,
            unchangedAfterIndeterminateWrite: PerformanceStateMatches,
            writeBlockReason: fanWriteBlockReason,
            returnBlockedOnWriteAuthorityLoss: fanWriteBlockReason is not null);
    }

    internal Task<ScopedApplyOutcome> ApplyManualFanTargetContinuationOutcomeAsync(
        int targetRpm,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            valid: targetRpm > 0,
            capabilities => capabilities.FanSpeedRange?.Supports(targetRpm) == true,
            matches: _ => false,
            token => ContinueFanTargetsAfterManualEntryOnceAsync(
                targetRpm,
                targetRpm,
                token),
            cancellationToken,
            initialStateBlockReason: state =>
                state.HyperBoostActive == true
                    ? "The target-only continuation is blocked while fresh readback reports HyperBoost active."
                    : DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                        ? null
                        : "The target-only continuation requires both fan channels to remain in manual mode.",
            acceptSetterCompletionWithoutReadback: true,
            setterCompletionEvidence: state =>
                DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                && state.HyperBoostActive != true,
            forceWrite: true,
            writeBlockReason: fanWriteBlockReason,
            returnBlockedOnWriteAuthorityLoss: fanWriteBlockReason is not null);

    // Generic manual readback cannot identify exact target RPMs after a
    // possible submission, so this path never authorizes an exact replay.
    internal Task<ScopedApplyOutcome> ApplyFanTargetsOutcomeAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            valid: fan1Rpm > 0 && fan2Rpm > 0,
            capabilities => capabilities.FanModes.Contains(FanMode.ManualCurve)
                && capabilities.FanSpeedRange?.Supports(fan1Rpm) == true
                && capabilities.FanSpeedRange.Supports(fan2Rpm),
            matches: _ => false,
            token => SetFanTargetsOnceAsync(fan1Rpm, fan2Rpm, token),
            cancellationToken,
            initialStateBlockReason: state =>
                state.HyperBoostActive == true
                    ? "Manual fan-curve maintenance is blocked while fresh readback reports HyperBoost active."
                    : DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                        ? null
                        : "Manual fan-curve maintenance requires the confirmed fan state to remain manual.",
            acceptSetterCompletionWithoutReadback: true,
            setterCompletionEvidence: state =>
                DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                && state.HyperBoostActive != true,
            forceWrite: true,
            resolveIndeterminateWriteByReadback: false,
            reconcileUnconfirmableIndeterminateWrite: true,
            writeBlockReason: fanWriteBlockReason,
            returnBlockedOnWriteAuthorityLoss: fanWriteBlockReason is not null);

    internal async Task<ScopedApplyOutcome> ApplyFanControlTransitionOutcomeAsync(
        FanControlSettings previous,
        FanControlSettings requested,
        PerformancePreset performanceIntent,
        CustomPerformanceSettings? customPerformanceIntent,
        CancellationToken cancellationToken)
    {
        var writeAttempted = new HashSet<DeviceSubsystem>();
        var confirmedCoupledWrites = new HashSet<DeviceSubsystem>();
        try
        {
            ScopedApplyOutcome outcome = await ApplyFanControlTransitionCoreOutcomeAsync(
                previous,
                requested,
                performanceIntent,
                customPerformanceIntent,
                writeAttempted,
                confirmedCoupledWrites,
                cancellationToken).ConfigureAwait(false);
            return outcome with
            {
                ConfirmedCoupledWrites = new HashSet<DeviceSubsystem>(
                    confirmedCoupledWrites),
            };
        }
        catch (IntentionalWriteCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            writeAttempted.UnionWith(exception.WriteAttempted);
            throw new IntentionalWriteCanceledException(
                writeAttempted,
                cancellationToken,
                exception);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && writeAttempted.Count > 0)
        {
            throw new IntentionalWriteCanceledException(
                writeAttempted,
                cancellationToken,
                exception);
        }
    }

    private async Task<ScopedApplyOutcome> ApplyFanControlTransitionCoreOutcomeAsync(
        FanControlSettings previous,
        FanControlSettings requested,
        PerformancePreset performanceIntent,
        CustomPerformanceSettings? customPerformanceIntent,
        HashSet<DeviceSubsystem> writeAttempted,
        HashSet<DeviceSubsystem> confirmedCoupledWrites,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(requested);

        bool requestedManual = DeviceSubsystemOrder.IsManualFanMode(requested.Mode);
        bool previousManual = DeviceSubsystemOrder.IsManualFanMode(previous.Mode);
        bool requiresAutomaticBridge = requestedManual
            && (!previousManual || previous.Mode != requested.Mode);
        ScopedApplyOutcome? automaticBridge = null;
        if (requiresAutomaticBridge)
        {
            automaticBridge = await RunThermalStepAsync(
                DeviceSubsystem.Fans,
                ThermalTransitionStepPurpose.AutomaticBridge,
                (coordinator, token) => coordinator.ApplyFanControlOutcomeAsync(
                    FanControlSettings.FromLegacy(FanMode.Automatic),
                    token),
                cancellationToken).ConfigureAwait(false);
            RecordWriteAttempt(writeAttempted, DeviceSubsystem.Fans, automaticBridge);
            if (automaticBridge.Result.Status is not (
                    ControlResultStatus.Applied or ControlResultStatus.Unchanged))
            {
                return automaticBridge with
                {
                    Result = automaticBridge.Result with
                    {
                        Message = $"Could not leave {previous.Mode} before selecting {requested.Mode}. {automaticBridge.Result.Message}",
                    },
                };
            }
        }

        ScopedApplyOutcome? balancedBridge = null;
        if (requiresAutomaticBridge)
        {
            balancedBridge = await RunThermalStepAsync(
                DeviceSubsystem.Performance,
                ThermalTransitionStepPurpose.BalancedBridge,
                (coordinator, token) =>
                    coordinator.ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                        PerformancePreset.Balanced,
                        customPerformance: null,
                        token),
                cancellationToken).ConfigureAwait(false);
            RecordWriteAttempt(writeAttempted, DeviceSubsystem.Performance, balancedBridge);
            RecordConfirmedCoupledWrite(
                confirmedCoupledWrites,
                DeviceSubsystem.Performance,
                balancedBridge);
            if (balancedBridge.Result.Status is not (
                    ControlResultStatus.Applied or ControlResultStatus.Unchanged))
            {
                return balancedBridge with
                {
                    WriteAttempted = balancedBridge.WriteAttempted
                        || automaticBridge?.WriteAttempted == true,
                    DeviceAccessFailed = balancedBridge.DeviceAccessFailed
                        || automaticBridge?.DeviceAccessFailed == true,
                    Result = balancedBridge.Result with
                    {
                        Message = $"Could not establish Balanced Automatic fans before selecting {requested.Mode}. {balancedBridge.Result.Message}",
                    },
                };
            }
        }

        ScopedApplyOutcome fans = await RunThermalStepAsync(
            DeviceSubsystem.Fans,
            ThermalTransitionStepPurpose.Requested,
            (coordinator, token) => coordinator.ApplyFanControlOutcomeAsync(
                requested,
                token),
            cancellationToken).ConfigureAwait(false);
        if (thermalStepRunner is not null
            && fans.WriteSubmissionState == WriteSubmissionState.Indeterminate
            && IsAdjustableFanManualEntrySubmission(fans.WriteFailurePhase)
            && ResolveManualFanTarget(requested, fans.Capabilities) is int targetRpm)
        {
            ScopedApplyOutcome continuation = await thermalStepRunner
                .ApplyFreshContinuationAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.ManualTargetContinuation,
                    fans,
                    (coordinator, token) => coordinator
                        .ApplyManualFanTargetContinuationOutcomeAsync(
                            targetRpm,
                            token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (continuation.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged)
            {
                fans = continuation with
                {
                    Result = continuation.Result with
                    {
                        Message =
                            "Fans entered manual mode and the target-only continuation completed without replaying either entry command.",
                    },
                };
            }
            else
            {
                return fans with
                {
                    Result = fans.Result with
                    {
                        Message = $"{fans.Result.Message} Target-only continuation result: {continuation.Result.Message}",
                    },
                    ConfirmationUnavailable = true,
                };
            }
        }
        RecordWriteAttempt(writeAttempted, DeviceSubsystem.Fans, fans);
        if (fans.Result.Status is not (
                ControlResultStatus.Applied or ControlResultStatus.Unchanged))
        {
            if ((balancedBridge?.Result.Status == ControlResultStatus.Applied
                    || automaticBridge?.Result.Status == ControlResultStatus.Applied)
                && !fans.WriteAttempted
                && performanceIntent != PerformancePreset.Balanced)
            {
                ScopedApplyOutcome restored = await RunThermalStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.Restoration,
                    (coordinator, token) => coordinator
                        .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                            performanceIntent,
                            customPerformanceIntent,
                            token),
                    cancellationToken).ConfigureAwait(false);
                RecordWriteAttempt(writeAttempted, DeviceSubsystem.Performance, restored);
                RecordConfirmedCoupledWrite(
                    confirmedCoupledWrites,
                    DeviceSubsystem.Performance,
                    restored);
                string restoration = restored.Result.Status is
                        ControlResultStatus.Applied or ControlResultStatus.Unchanged
                    ? " The previous performance intent was restored."
                    : $" The previous performance intent could not be restored. {restored.Result.Message}";
                return fans with
                {
                    Readback = restored.Readback ?? fans.Readback,
                    WriteAttempted = fans.WriteAttempted
                        || balancedBridge?.WriteAttempted == true
                        || automaticBridge?.WriteAttempted == true
                        || restored.WriteAttempted,
                    DeviceAccessFailed = fans.DeviceAccessFailed || restored.DeviceAccessFailed,
                    Result = fans.Result with
                    {
                        Message = fans.Result.Message + restoration,
                    },
                };
            }

            return fans with
            {
                WriteAttempted = fans.WriteAttempted
                    || balancedBridge?.WriteAttempted == true
                    || automaticBridge?.WriteAttempted == true,
                DeviceAccessFailed = fans.DeviceAccessFailed
                    || balancedBridge?.DeviceAccessFailed == true
                    || automaticBridge?.DeviceAccessFailed == true,
            };
        }

        if (requested.Mode != FanMode.Automatic || !previousManual)
        {
            return fans;
        }

        ScopedApplyOutcome performance = await RunThermalStepAsync(
            DeviceSubsystem.Performance,
            ThermalTransitionStepPurpose.Restoration,
            (coordinator, token) => coordinator
                .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                    performanceIntent,
                    customPerformanceIntent,
                    token),
            cancellationToken).ConfigureAwait(false);
        RecordWriteAttempt(writeAttempted, DeviceSubsystem.Performance, performance);
        RecordConfirmedCoupledWrite(
            confirmedCoupledWrites,
            DeviceSubsystem.Performance,
            performance);
        if (performance.Result.Status is not (
                ControlResultStatus.Applied or ControlResultStatus.Unchanged))
        {
            return fans with
            {
                Result = performance.Result with
                {
                    Message = $"Fans returned to Automatic, but the saved {performanceIntent} performance intent was not restored. {performance.Result.Message}",
                },
                Readback = performance.Readback ?? fans.Readback,
                WriteAttempted = fans.WriteAttempted || performance.WriteAttempted,
                ReadbackMatched = fans.ReadbackMatched,
                Capabilities = performance.Capabilities ?? fans.Capabilities,
                DeviceAccessFailed = fans.DeviceAccessFailed || performance.DeviceAccessFailed,
            };
        }

        return fans with
        {
            Result = fans.Result with
            {
                Message = performanceIntent == PerformancePreset.Balanced
                    ? fans.Result.Message
                    : $"{fans.Result.Message} {performanceIntent} performance was restored.",
            },
            Readback = performance.Readback ?? fans.Readback,
            WriteAttempted = fans.WriteAttempted || performance.WriteAttempted,
            ReadbackMatched = fans.ReadbackMatched && performance.ReadbackMatched,
            Capabilities = performance.Capabilities ?? fans.Capabilities,
            DeviceAccessFailed = fans.DeviceAccessFailed || performance.DeviceAccessFailed,
        };
    }

    private static void RecordWriteAttempt(
        HashSet<DeviceSubsystem> writeAttempted,
        DeviceSubsystem subsystem,
        ScopedApplyOutcome outcome)
    {
        if (outcome.WriteAttempted)
        {
            writeAttempted.Add(subsystem);
        }
    }

    private static void RecordConfirmedCoupledWrite(
        HashSet<DeviceSubsystem> confirmedCoupledWrites,
        DeviceSubsystem subsystem,
        ScopedApplyOutcome outcome)
    {
        if (outcome.WriteAttempted && outcome.ReadbackMatched)
        {
            confirmedCoupledWrites.Add(subsystem);
        }
    }

    private Task<ScopedApplyOutcome> RunThermalStepAsync(
        DeviceSubsystem component,
        ThermalTransitionStepPurpose purpose,
        Func<ControlCoordinator, CancellationToken, Task<ScopedApplyOutcome>> operation,
        CancellationToken cancellationToken) => thermalStepRunner is null
            ? operation(this, cancellationToken)
            : thermalStepRunner.ApplyStepAsync(
                component,
                purpose,
                operation,
                cancellationToken);

    private static int? ResolveManualFanTarget(
        FanControlSettings requested,
        DeviceCapabilities? capabilities) => requested.Mode switch
        {
            FanMode.Fixed => requested.FixedSpeedRpm,
            FanMode.Maximum or FanMode.ManualCurve =>
                capabilities?.FanSpeedRange?.MaximumRpm,
            _ => null,
        };

    private static bool IsAdjustableFanManualEntrySubmission(
        BladeWriteFailurePhase? phase) => phase is
            BladeWriteFailurePhase.AdjustableFanFirstManualEntrySubmission or
            BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission;

    internal Task<ScopedApplyOutcome> ApplyBatteryOutcomeAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken) =>
        ApplyChargeLimitOutcomeAsync(value, cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyBatteryOutcomeAsync(
        DesiredBatteryState value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ApplyBatteryDesiredOutcomeAsync(value, cancellationToken);
    }

    internal Task<ScopedApplyOutcome> ApplyChargeLimitOutcomeAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ApplyBatteryOperationAsync(
            (capabilities, confirmed) => CreateChargeLimitPlan(
                value,
                capabilities,
                confirmed),
            cancellationToken);
    }

    internal Task<ScopedApplyOutcome> ApplyBatteryProtectionOutcomeAsync(
        bool enabled,
        BatteryPolicy storedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storedPolicy);
        return ApplyBatteryOperationAsync(
            (capabilities, confirmed) => CreateProtectionPlan(
                enabled,
                storedPolicy,
                capabilities,
                confirmed),
            cancellationToken);
    }

    internal Task<ScopedApplyOutcome> ApplyTemporaryFullChargeOutcomeAsync(
        bool enabled,
        CancellationToken cancellationToken) => ApplyBatteryOperationAsync(
            (capabilities, confirmed) => CreateTemporaryFullChargePlan(
                enabled,
                capabilities,
                confirmed),
            cancellationToken);

    private Task<ScopedApplyOutcome> ApplyBatteryDesiredOutcomeAsync(
        DesiredBatteryState desired,
        CancellationToken cancellationToken) => ApplyBatteryOperationAsync(
            (capabilities, confirmed) => CreateBatteryPlan(
                desired,
                capabilities,
                confirmed),
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyLightingOutcomeAsync(
        LightingSettings value,
        CancellationToken cancellationToken,
        bool forceWrite = false,
        Func<bool>? writeAllowed = null,
        Func<ConfirmedDeviceState, string?>? initialStateBlockReason = null) =>
        ApplyScopedAsync(
            DeviceSubsystem.Lighting,
            "Lighting",
            IsLightingValid(value),
            capabilities => LightingSupported(capabilities, value),
            state => LightingMatches(state, value),
            token => device.SetLightingAsync(value, token),
            cancellationToken,
            initialStateBlockReason,
            acceptSetterCompletionWithoutReadback: value.Effect != LightingEffect.Off,
            setterCompletionEvidence: state => LogoMatches(state, value),
            forceWrite: forceWrite,
            writeAllowed: writeAllowed,
            stateReader: device.ReadLightingStateAsync);

    internal async Task<FullTransitionApplyOutcome> ApplyDesiredStateOutcomeAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken,
        IReadOnlySet<DeviceSubsystem>? automaticWriteExclusions = null,
        Func<ConfirmedDeviceState, IReadOnlySet<DeviceSubsystem>>?
            automaticWriteExclusionResolver = null,
        Func<CancellationToken, Task<PowerTransitionDeviceStateReadResult>>?
            stateReader = null,
        Func<IBladeDevice, CancellationToken,
            Task<PowerTransitionDeviceStateReadResult>>? deviceStateReader = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var transition = new FullTransitionApplyState();
        DeviceCapabilities capabilities;
        ConfirmedDeviceState initial;
        bool lightingReadUnavailable = false;
        try
        {
            IBladeDevice initialDevice = thermalStepRunner?.CurrentDevice ?? device;
            capabilities = await initialDevice.ReadCapabilitiesAsync(cancellationToken);
            EnsureGpuClockOffsetManagementContract(capabilities);
            desired = DropUnsupportedGpuClockTuning(desired, capabilities);
            if (desired.GpuClockTuning is null)
            {
                // Null means OpenBlade no longer manages or reports NVIDIA
                // offsets; it does not mean reset them. A reset would require
                // touching NVML and could wake an idle dGPU. Driver-held
                // offsets may therefore remain applied until a later managed
                // profile applies new values or the driver reloads.
                await RelinquishGpuClockOffsetManagementAsync(cancellationToken);
            }

            PowerTransitionDeviceStateReadResult initialRead =
                deviceStateReader is not null
                    ? await deviceStateReader(initialDevice, cancellationToken)
                        .ConfigureAwait(false)
                    : stateReader is not null
                        ? await stateReader(cancellationToken).ConfigureAwait(false)
                        : new(
                            await initialDevice.ReadStateAsync(cancellationToken)
                                .ConfigureAwait(false),
                            LightingOffConfirmationUnavailable: false);
            initial = initialRead.State;
            lightingReadUnavailable =
                initialRead.LightingOffConfirmationUnavailable;
            cancellationToken.ThrowIfCancellationRequested();
            if (automaticWriteExclusionResolver is not null)
            {
                automaticWriteExclusions = automaticWriteExclusionResolver(initial);
            }
            if (initialRead.LightingOffConfirmationUnavailable)
            {
                automaticWriteExclusions = automaticWriteExclusions is null
                    ? new HashSet<DeviceSubsystem> { DeviceSubsystem.Lighting }
                    : new HashSet<DeviceSubsystem>(automaticWriteExclusions)
                    {
                        DeviceSubsystem.Lighting,
                    };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ControlResult failed = ReadFailure("Device state", exception);
            return new FullTransitionApplyOutcome(
                new SubsystemApplyResults(failed, failed, failed, failed),
                null,
                transition.Attempted,
                transition.Matched,
                transition.Contradicted,
                null,
                DeviceAccessFailed: true);
        }

        Dictionary<DeviceSubsystem, PendingSubsystem> states =
            CreatePendingSubsystems(desired, capabilities, initial);

        try
        {
            await ApplyPendingSubsystemWritesAsync(
                desired,
                capabilities,
                initial,
                states,
                automaticWriteExclusions,
                lightingReadUnavailable,
                transition,
                cancellationToken);

            if (transition.Attempted.Count == 0)
            {
                return CreateFullOutcome(
                    transition.Provisional,
                    initial,
                    transition.Attempted,
                    transition.Matched,
                    transition.Contradicted,
                    capabilities,
                    DeviceAccessFailed: false,
                    effectiveDesired: desired);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final;
            bool finalLightingReadUnavailable;
            try
            {
                IBladeDevice finalDevice = thermalStepRunner?.CurrentDevice ?? device;
                PowerTransitionDeviceStateReadResult finalRead =
                    deviceStateReader is not null
                        ? await deviceStateReader(finalDevice, cancellationToken)
                            .ConfigureAwait(false)
                        : stateReader is not null
                            ? await stateReader(cancellationToken).ConfigureAwait(false)
                            : new(
                                await finalDevice.ReadStateAsync(cancellationToken)
                                    .ConfigureAwait(false),
                                LightingOffConfirmationUnavailable: false);
                final = finalRead.State;
                finalLightingReadUnavailable =
                    finalRead.LightingOffConfirmationUnavailable;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                foreach (DeviceSubsystem subsystem in transition.Attempted)
                {
                    transition.Provisional[subsystem] =
                        Indeterminate(states[subsystem].Name, exception);
                }

                transition.Matched.Clear();
                HashSet<DeviceSubsystem> confirmationUnavailable =
                    transition.Completed
                        .Where(subsystem =>
                            !states[subsystem].AcceptSetterCompletionWithoutReadback)
                        .ToHashSet();
                confirmationUnavailable.UnionWith(
                    transition.ConfirmationUnavailable);

                return CreateFullOutcome(
                    transition.Provisional,
                    null,
                    transition.Attempted,
                    transition.Matched,
                    transition.Contradicted,
                    capabilities,
                    DeviceAccessFailed: true,
                    confirmationUnavailable: confirmationUnavailable,
                    preWriteState: initial,
                    effectiveDesired: desired);
            }

            IReadOnlySet<DeviceSubsystem> finalReadUnavailable =
                finalLightingReadUnavailable
                    ? new HashSet<DeviceSubsystem> { DeviceSubsystem.Lighting }
                    : new HashSet<DeviceSubsystem>();
            ReconcileFinalReadback(
                desired,
                states,
                final,
                finalReadUnavailable,
                transition);
            HashSet<DeviceSubsystem> attemptedWithoutConfirmation =
                transition.Attempted
                    .Where(subsystem =>
                        finalReadUnavailable.Contains(subsystem)
                        && !CanAcceptUnavailableFinalReadback(
                            subsystem,
                            states[subsystem],
                            final,
                            desired,
                            transition)
                        && !HasExplicitFinalReadbackContradiction(
                            subsystem,
                            final,
                            desired))
                    .ToHashSet();
            attemptedWithoutConfirmation.UnionWith(
                transition.ConfirmationUnavailable);

            return CreateFullOutcome(
                transition.Provisional,
                final,
                transition.Attempted,
                transition.Matched,
                transition.Contradicted,
                capabilities,
                transition.DeviceAccessFailed,
                confirmationUnavailable: attemptedWithoutConfirmation.Count > 0
                    ? attemptedWithoutConfirmation
                    : null,
                preWriteState: attemptedWithoutConfirmation.Count > 0
                    ? initial
                    : null,
                effectiveDesired: desired);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested
                && transition.Attempted.Count > 0)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem>(transition.Attempted),
                cancellationToken,
                exception);
        }
    }

    private Dictionary<DeviceSubsystem, PendingSubsystem> CreatePendingSubsystems(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities,
        ConfirmedDeviceState initial)
    {
        BatteryOperationPlan batteryPlan = CreateBatteryPlan(
            desired.Battery,
            capabilities,
            initial.Battery);
        bool thermalCombinationSupported = IsVerifiedThermalCombination(
            desired.Performance.Value,
            desired.Fans.Value);
        bool manualFansDesired = DeviceSubsystemOrder.IsManualFanMode(desired.Fans.Value);
        bool PerformanceStateMatches(ConfirmedDeviceState state) =>
            manualFansDesired && DeviceSubsystemOrder.IsManualFanMode(state.Fans)
            || PerformanceMatches(
                state,
                desired.Performance.Value,
                desired.CustomPerformance,
                desired.GpuClockTuning);
        return new Dictionary<DeviceSubsystem, PendingSubsystem>
        {
            [DeviceSubsystem.Performance] = new(
                "Performance",
                thermalCombinationSupported
                    && IsDefined(desired.Performance.Value)
                    && capabilities.PerformancePresets.Contains(desired.Performance.Value)
                    && GpuClockTuningSupported(capabilities, desired.GpuClockTuning),
                PerformanceStateMatches(initial),
                token => ApplyPerformanceAndGpuClockTuningAsync(
                    desired.Performance.Value,
                    desired.CustomPerformance,
                    desired.GpuClockTuning,
                    token),
                PerformanceStateMatches)
            {
                StateAwareSetter = (state, token) =>
                    CanApplyGpuClockOffsetsOnly(
                        state,
                        desired.Performance.Value,
                        desired.CustomPerformance,
                        desired.GpuClockTuning)
                        ? SetGpuClockOffsetsAsync(
                            desired.GpuClockTuning!.EffectiveOffsets,
                            desired.GpuClockTuning.Enabled,
                            token)
                        : ApplyPerformanceAndGpuClockTuningAsync(
                            desired.Performance.Value,
                            desired.CustomPerformance,
                            desired.GpuClockTuning,
                            token),
            },
            [DeviceSubsystem.Fans] = new(
                "Fans",
                thermalCombinationSupported
                    && IsDefined(desired.Fans.Value)
                    && capabilities.FanModes.Contains(desired.Fans.Value),
                FanModeMatches(initial.Fans, desired.Fans.Value),
                token => SetFanControlAsync(
                    desired.FanControl
                        ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                    token),
                state => FanModeMatches(state.Fans, desired.Fans.Value),
                AcceptSetterCompletionWithoutReadback:
                    desired.Fans.Value is FanMode.Fixed or FanMode.ManualCurve),
            [DeviceSubsystem.Battery] = new(
                batteryPlan.Name,
                batteryPlan.Supported,
                batteryPlan.InitiallyMatching,
                token => ApplyBatteryTransitionAsync(
                    desired.Battery,
                    capabilities,
                    initial.Battery,
                    token),
                state => BatteryMatches(desired.Battery, state.Battery)),
            [DeviceSubsystem.Lighting] = new(
                "Lighting",
                IsLightingValid(desired.Lighting.Value)
                    && LightingSupported(capabilities, desired.Lighting.Value),
                LightingMatches(initial, desired.Lighting.Value),
                token => device.SetLightingAsync(desired.Lighting.Value, token),
                state => LightingMatches(state, desired.Lighting.Value),
                AcceptSetterCompletionWithoutReadback:
                    desired.Lighting.Value.Effect != LightingEffect.Off),
        };
    }

    private async Task ApplyBatteryTransitionAsync(
        DesiredBatteryState desired,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? initial,
        CancellationToken cancellationToken)
    {
        BatteryOperationPlan plan = CreateBatteryPlan(desired, capabilities, initial);
        await plan.Setter(cancellationToken).ConfigureAwait(false);

        if (initial?.TemporaryFullCharge is not true
            || desired.TemporaryFullCharge)
        {
            return;
        }

        ConfirmedBatteryState? refreshed = (await device.ReadStateAsync(cancellationToken)
            .ConfigureAwait(false)).Battery;
        if (refreshed?.TemporaryFullCharge is not false)
        {
            return;
        }

        // Keep one intent bounded to the cancellation plus one confirmed follow-up.
        // Any remaining multi-step mismatch is retried by the next automation cycle.
        BatteryOperationPlan followUp = CreateBatteryPlan(
            desired,
            capabilities,
            refreshed);
        if (followUp.Supported && !followUp.InitiallyMatching)
        {
            await followUp.Setter(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyPendingSubsystemWritesAsync(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities,
        ConfirmedDeviceState initial,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> states,
        IReadOnlySet<DeviceSubsystem>? automaticWriteExclusions,
        bool lightingReadUnavailable,
        FullTransitionApplyState transition,
        CancellationToken cancellationToken)
    {
        bool stopFurtherWrites = false;
        string? stopReason = null;
        bool stopThermalWrites = false;
        string? thermalStopReason = null;
        IReadOnlyList<DeviceSubsystem> orderedSubsystems =
            DeviceSubsystemOrder.ForTransition(initial, desired);

        foreach (DeviceSubsystem subsystem in orderedSubsystems)
        {
            PendingSubsystem pending = states[subsystem];
            if (automaticWriteExclusions?.Contains(subsystem) == true)
            {
                transition.Provisional[subsystem] =
                    subsystem == DeviceSubsystem.Lighting && lightingReadUnavailable
                        ? AutomaticReadUnavailable(pending.Name)
                        : initial.HyperBoostActive
                            && IsThermalSubsystem(subsystem)
                            ? PreservedForHyperBoost(pending.Name)
                        : ExternalChangePreserved(pending.Name);
                continue;
            }

            if (!pending.Supported)
            {
                transition.Provisional[subsystem] = Unsupported(pending.Name);
                if (IsThermalSubsystem(subsystem) && !pending.InitiallyMatching)
                {
                    stopThermalWrites = true;
                    thermalStopReason = transition.Provisional[subsystem].Message;
                }
                continue;
            }

            if (pending.InitiallyMatching)
            {
                transition.Provisional[subsystem] = Unchanged(pending.Name);
                transition.Matched.Add(subsystem);
                continue;
            }

            if (stopFurtherWrites)
            {
                transition.Provisional[subsystem] = SubsequentWritesBlocked(stopReason!);
                continue;
            }

            if (stopThermalWrites && IsThermalSubsystem(subsystem))
            {
                transition.Provisional[subsystem] =
                    SubsequentWritesBlocked(thermalStopReason!);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsThermalSubsystem(subsystem) && thermalStepRunner is not null)
            {
                ScopedApplyOutcome thermalOutcome = await RunThermalStepAsync(
                        subsystem,
                        ThermalTransitionStepPurpose.Requested,
                        (coordinator, token) => coordinator
                            .ApplyFullTransitionThermalStepOnceAsync(
                                subsystem,
                                desired,
                                capabilities,
                                states,
                                initial,
                                token),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (subsystem == DeviceSubsystem.Fans
                    && thermalOutcome.WriteSubmissionState
                        == WriteSubmissionState.Indeterminate
                    && IsAdjustableFanManualEntrySubmission(
                        thermalOutcome.WriteFailurePhase)
                    && ResolveManualFanTarget(
                            desired.FanControl
                                ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                            capabilities) is int targetRpm)
                {
                    ScopedApplyOutcome continuation = await thermalStepRunner
                        .ApplyFreshContinuationAsync(
                            DeviceSubsystem.Fans,
                            ThermalTransitionStepPurpose.ManualTargetContinuation,
                            thermalOutcome,
                            (coordinator, token) => coordinator
                                .ApplyManualFanTargetContinuationOutcomeAsync(
                                    targetRpm,
                                    token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    thermalOutcome = continuation.Result.Status is
                            ControlResultStatus.Applied or
                            ControlResultStatus.Unchanged
                        ? continuation with
                        {
                            Result = continuation.Result with
                            {
                                Message =
                                    "Fans entered manual mode and the target-only continuation completed without replaying either entry command.",
                            },
                        }
                        : thermalOutcome with
                        {
                            Result = thermalOutcome.Result with
                            {
                                Message =
                                    $"{thermalOutcome.Result.Message} Target-only continuation result: {continuation.Result.Message}",
                            },
                            ConfirmationUnavailable = true,
                        };
                }
                ApplyFullTransitionThermalStepOutcome(
                    subsystem,
                    pending,
                    thermalOutcome,
                    transition,
                    ref stopFurtherWrites,
                    ref stopReason,
                    ref stopThermalWrites,
                    ref thermalStopReason);
                continue;
            }

            transition.Attempted.Add(subsystem);
            try
            {
                PendingSubsystem writePending = pending;
                if (thermalStepRunner is not null)
                {
                    ControlCoordinator currentCoordinator =
                        thermalStepRunner.CreateRecipeCoordinator();
                    writePending = currentCoordinator.CreatePendingSubsystems(
                        desired,
                        capabilities,
                        initial)[subsystem];
                }
                await (writePending.StateAwareSetter is null
                    ? writePending.Setter(cancellationToken)
                    : writePending.StateAwareSetter(initial, cancellationToken));
                transition.Completed.Add(subsystem);
                transition.Provisional[subsystem] = Indeterminate(pending.Name, null);
            }
            catch (CapabilityUnavailableException exception)
            {
                transition.Attempted.Remove(subsystem);
                transition.Provisional[subsystem] = CapabilityRefused(pending.Name, exception);
                if (IsThermalSubsystem(subsystem))
                {
                    stopThermalWrites = true;
                    thermalStopReason = transition.Provisional[subsystem].Message;
                }
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState is WriteSubmissionState.KnownNotApplied)
            {
                transition.Attempted.Remove(subsystem);
                transition.Provisional[subsystem] = WriteRejected(pending.Name, exception);
                if (IsThermalSubsystem(subsystem))
                {
                    stopThermalWrites = true;
                    thermalStopReason = transition.Provisional[subsystem].Message;
                }
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState is WriteSubmissionState.Indeterminate)
            {
                transition.Provisional[subsystem] = Indeterminate(pending.Name, exception);
                stopFurtherWrites = true;
                stopReason = transition.Provisional[subsystem].Message;
                transition.DeviceAccessFailed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                transition.Provisional[subsystem] = Indeterminate(pending.Name, exception);
                if (IsThermalSubsystem(subsystem))
                {
                    stopThermalWrites = true;
                    thermalStopReason = transition.Provisional[subsystem].Message;
                }
                if (IsTerminalDeviceFailure(exception))
                {
                    stopFurtherWrites = true;
                    stopReason = transition.Provisional[subsystem].Message;
                    transition.DeviceAccessFailed = true;
                }
            }
        }
    }

    private async Task<ScopedApplyOutcome> ApplyFullTransitionThermalStepOnceAsync(
        DeviceSubsystem subsystem,
        DesiredDeviceState desired,
        DeviceCapabilities capabilities,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> originalStates,
        ConfirmedDeviceState initial,
        CancellationToken cancellationToken)
    {
        PendingSubsystem original = originalStates[subsystem];
        string? blockReason = subsystem == DeviceSubsystem.Performance
            ? performanceWriteBlockReason?.Invoke()
            : fanWriteBlockReason?.Invoke();
        if (blockReason is not null)
        {
            return new ScopedApplyOutcome(
                new ControlResult(ControlResultStatus.Blocked, blockReason),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: false,
                WriteSubmissionState: WriteSubmissionState.KnownNotApplied);
        }

        try
        {
            Dictionary<DeviceSubsystem, PendingSubsystem> currentStates =
                CreatePendingSubsystems(
                    desired,
                    capabilities,
                    initial);
            PendingSubsystem pending = currentStates[subsystem];
            await (pending.StateAwareSetter is null
                ? pending.Setter(cancellationToken)
                : pending.StateAwareSetter(initial, cancellationToken))
                .ConfigureAwait(false);
            return new ScopedApplyOutcome(
                Applied(original.Name),
                initial,
                WriteAttempted: true,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: false);
        }
        catch (CapabilityUnavailableException exception)
        {
            return new ScopedApplyOutcome(
                CapabilityRefused(original.Name, exception),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: false,
                WriteSubmissionState: WriteSubmissionState.KnownNotApplied);
        }
        catch (BladeWriteException exception)
            when (exception.SubmissionState is WriteSubmissionState.KnownNotApplied)
        {
            return new ScopedApplyOutcome(
                WriteRejected(original.Name, exception),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: exception.FailurePhase
                    == BladeWriteFailurePhase.ThermalPreWriteRead,
                WriteSubmissionState: exception.SubmissionState,
                WriteFailurePhase: exception.FailurePhase);
        }
        catch (BladeWriteException exception)
            when (exception.SubmissionState is WriteSubmissionState.Indeterminate)
        {
            return new ScopedApplyOutcome(
                Indeterminate(original.Name, exception),
                null,
                WriteAttempted: true,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: true,
                WriteSubmissionState: exception.SubmissionState,
                WriteFailurePhase: exception.FailurePhase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ScopedApplyOutcome(
                Indeterminate(original.Name, exception),
                null,
                WriteAttempted: true,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: IsTerminalDeviceFailure(exception));
        }
    }

    private static void ApplyFullTransitionThermalStepOutcome(
        DeviceSubsystem subsystem,
        PendingSubsystem pending,
        ScopedApplyOutcome outcome,
        FullTransitionApplyState transition,
        ref bool stopFurtherWrites,
        ref string? stopReason,
        ref bool stopThermalWrites,
        ref string? thermalStopReason)
    {
        if (outcome.WriteAttempted)
        {
            transition.Attempted.Add(subsystem);
        }
        transition.DeviceAccessFailed |= outcome.DeviceAccessFailed;
        if (outcome.ConfirmationUnavailable)
        {
            transition.ConfirmationUnavailable.Add(subsystem);
        }

        if (outcome.Result.Status is
            ControlResultStatus.Applied or ControlResultStatus.Unchanged)
        {
            transition.Completed.Add(subsystem);
            transition.Provisional[subsystem] = Indeterminate(pending.Name, null);
            return;
        }

        transition.Provisional[subsystem] = outcome.Result;
        if (outcome.WriteAttempted
            || outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate)
        {
            stopFurtherWrites = true;
            stopReason = outcome.Result.Message;
            return;
        }

        stopThermalWrites = true;
        thermalStopReason = outcome.Result.Message;
    }

    private static void ReconcileFinalReadback(
        DesiredDeviceState desired,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> states,
        ConfirmedDeviceState final,
        IReadOnlySet<DeviceSubsystem> finalReadUnavailable,
        FullTransitionApplyState transition)
    {
        transition.Matched.Clear();
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
        {
            PendingSubsystem pending = states[subsystem];
            if (transition.ConfirmationUnavailable.Contains(subsystem))
            {
                transition.Provisional[subsystem] = Indeterminate(pending.Name, null);
                transition.Matched.Remove(subsystem);
                continue;
            }
            if (finalReadUnavailable.Contains(subsystem))
            {
                if (transition.Attempted.Contains(subsystem))
                {
                    transition.Provisional[subsystem] =
                        CanAcceptUnavailableFinalReadback(
                            subsystem,
                            pending,
                            final,
                            desired,
                            transition)
                            ? AppliedWithoutReadback(pending.Name)
                            : Indeterminate(
                                pending.Name,
                                new IOException(
                                    "The final lighting state could not be confirmed."));
                }
                else if (transition.Provisional[subsystem].Status
                    == ControlResultStatus.Unchanged)
                {
                    transition.Provisional[subsystem] =
                        AutomaticReadUnavailable(pending.Name);
                }
                transition.Matched.Remove(subsystem);
                continue;
            }
            if (transition.Attempted.Contains(subsystem))
            {
                if (pending.Matches(final))
                {
                    transition.Provisional[subsystem] = Applied(pending.Name);
                    transition.Matched.Add(subsystem);
                }
                else
                {
                    transition.Provisional[subsystem] =
                        HasAcceptedSetterCompletionWithoutReadback(
                            subsystem,
                            pending,
                            transition)
                            && (subsystem != DeviceSubsystem.Lighting
                                || LogoMatches(final, desired.Lighting.Value))
                            ? AppliedWithoutReadback(pending.Name)
                            : Indeterminate(pending.Name, null);
                }
            }
            else if (transition.Provisional[subsystem].Status == ControlResultStatus.Unchanged)
            {
                if (pending.Matches(final))
                {
                    transition.Matched.Add(subsystem);
                }
                else
                {
                    transition.Provisional[subsystem] = FinalReadbackChanged(pending.Name);
                    if (subsystem == DeviceSubsystem.Battery
                        && !desired.Battery.TemporaryFullCharge
                        && final.Battery is { ProtectionEnabled: false })
                    {
                        transition.Contradicted.Add(DeviceSubsystem.Battery);
                    }
                }
            }
        }
    }

    private static bool CanAcceptUnavailableFinalReadback(
        DeviceSubsystem subsystem,
        PendingSubsystem pending,
        ConfirmedDeviceState final,
        DesiredDeviceState desired,
        FullTransitionApplyState transition) =>
        HasAcceptedSetterCompletionWithoutReadback(
            subsystem,
            pending,
            transition)
        && (subsystem != DeviceSubsystem.Lighting
            || final.LogoLighting is null
            || LogoMatches(final, desired.Lighting.Value));

    private static bool HasAcceptedSetterCompletionWithoutReadback(
        DeviceSubsystem subsystem,
        PendingSubsystem pending,
        FullTransitionApplyState transition) =>
        transition.Completed.Contains(subsystem)
        && pending.AcceptSetterCompletionWithoutReadback;

    private static bool HasExplicitFinalReadbackContradiction(
        DeviceSubsystem subsystem,
        ConfirmedDeviceState final,
        DesiredDeviceState desired) =>
        subsystem == DeviceSubsystem.Lighting
        && final.LogoLighting is not null
        && !LogoMatches(final, desired.Lighting.Value);

    private async Task<ScopedApplyOutcome> ApplyBatteryOperationAsync(
        Func<DeviceCapabilities, ConfirmedBatteryState?, BatteryOperationPlan> createPlan,
        CancellationToken cancellationToken)
    {
        DeviceCapabilities? capabilities = null;
        bool submissionIndeterminate = false;
        bool setterCompleted = false;
        ConfirmedDeviceState? initial = null;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            EnsureGpuClockOffsetManagementContract(capabilities);
            cancellationToken.ThrowIfCancellationRequested();
            initial = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BatteryOperationPlan plan = createPlan(capabilities, initial.Battery);
            if (!plan.Supported)
            {
                return new ScopedApplyOutcome(
                    Unsupported(plan.Name),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            if (plan.InitiallyMatching)
            {
                return new ScopedApplyOutcome(
                    Unchanged(plan.Name),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: true,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await plan.Setter(cancellationToken);
                submissionIndeterminate = true;
                setterCompleted = true;
            }
            catch (CapabilityUnavailableException exception)
            {
                return new ScopedApplyOutcome(
                    CapabilityRefused(plan.Name, exception),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState is WriteSubmissionState.KnownNotApplied)
            {
                bool preWriteReadFailed = exception.FailurePhase
                    == BladeWriteFailurePhase.ThermalPreWriteRead;
                return new ScopedApplyOutcome(
                    WriteRejected(plan.Name, exception),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: preWriteReadFailed,
                    WriteSubmissionState: exception.SubmissionState,
                    WriteFailurePhase: exception.FailurePhase);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState is WriteSubmissionState.Indeterminate)
            {
                submissionIndeterminate = true;
                return new ScopedApplyOutcome(
                    Indeterminate(plan.Name, exception),
                    null,
                    WriteAttempted: true,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Matches(final.Battery))
            {
                return new ScopedApplyOutcome(
                    Applied(plan.Name),
                    final,
                    WriteAttempted: true,
                    ReadbackMatched: true,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            return new ScopedApplyOutcome(
                Indeterminate(plan.Name, null),
                final,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && submissionIndeterminate)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem> { DeviceSubsystem.Battery },
                cancellationToken,
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ScopedApplyOutcome(
                submissionIndeterminate
                    ? Indeterminate("Battery", exception)
                    : ReadFailure("Battery", exception),
                null,
                WriteAttempted: submissionIndeterminate,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: true,
                ConfirmationUnavailable: setterCompleted,
                PreWriteState: setterCompleted ? initial : null);
        }
    }

    private async Task<ScopedApplyOutcome> ApplyScopedAsync(
        DeviceSubsystem subsystem,
        string name,
        bool valid,
        Func<DeviceCapabilities, bool> supported,
        Func<ConfirmedDeviceState, bool> matches,
        Func<CancellationToken, Task> setter,
        CancellationToken cancellationToken,
        Func<ConfirmedDeviceState, string?>? initialStateBlockReason = null,
        bool acceptSetterCompletionWithoutReadback = false,
        Func<ConfirmedDeviceState, bool>? setterCompletionEvidence = null,
        bool forceWrite = false,
        Func<ConfirmedDeviceState, CancellationToken, Task>? stateAwareSetter = null,
        Func<CancellationToken, Task<ConfirmedDeviceState>>? stateReader = null,
        Func<bool>? writeAllowed = null,
        Func<string?>? writeBlockReason = null,
        Func<DeviceCapabilities, CancellationToken, Task>? beforeInitialStateRead = null,
        bool resolveIndeterminateWriteByReadback = false,
        Func<ConfirmedDeviceState, ConfirmedDeviceState, bool>?
            unchangedAfterIndeterminateWrite = null,
        bool returnBlockedOnWriteAuthorityLoss = false,
        bool reconcileUnconfirmableIndeterminateWrite = false)
    {
        if (!valid)
        {
            return new ScopedApplyOutcome(
                Unsupported(name),
                null,
                WriteAttempted: false,
                ReadbackMatched: false,
                null,
                DeviceAccessFailed: false);
        }

        bool writeAttempted = false;
        bool setterCompleted = false;
        bool initialStateReadStarted = false;
        bool initialStateReadCompleted = false;
        DeviceCapabilities? capabilities = null;
        ConfirmedDeviceState? initial = null;
        Func<CancellationToken, Task<ConfirmedDeviceState>> readState =
            stateReader ?? scopedStateReader ?? device.ReadStateAsync;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            EnsureGpuClockOffsetManagementContract(capabilities);
            cancellationToken.ThrowIfCancellationRequested();
            if (!supported(capabilities))
            {
                return new ScopedApplyOutcome(
                    Unsupported(name),
                    null,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            if (beforeInitialStateRead is not null)
            {
                await beforeInitialStateRead(capabilities, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            initialStateReadStarted = true;
            initial = await readState(cancellationToken);
            initialStateReadCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!forceWrite && matches(initial))
            {
                return new ScopedApplyOutcome(
                    Unchanged(name),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: true,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            string? blockReason = initialStateBlockReason?.Invoke(initial);
            if (blockReason is not null)
            {
                return new ScopedApplyOutcome(
                    new ControlResult(ControlResultStatus.Blocked, blockReason),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? writeRefusal = writeBlockReason?.Invoke();
            if (writeRefusal is not null)
            {
                throw new WriteAuthorityLostException(writeRefusal);
            }

            if (writeAllowed?.Invoke() == false)
            {
                throw new WriteAuthorityLostException();
            }

            writeAttempted = true;
            await (stateAwareSetter is null
                ? setter(cancellationToken)
                : stateAwareSetter(initial, cancellationToken));
            setterCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await readState(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (matches(final))
            {
                return new ScopedApplyOutcome(
                    Applied(name),
                    final,
                    WriteAttempted: true,
                    ReadbackMatched: true,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            return new ScopedApplyOutcome(
                acceptSetterCompletionWithoutReadback
                    && (setterCompletionEvidence?.Invoke(final) ?? true)
                    ? AppliedWithoutReadback(name)
                    : Indeterminate(name, null),
                final,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false);
        }
        catch (CapabilityUnavailableException exception)
            when (writeAttempted)
        {
            return new ScopedApplyOutcome(
                CapabilityRefused(name, exception),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false);
        }
        catch (BladeWriteException exception)
            when (writeAttempted
                && exception.SubmissionState is WriteSubmissionState.KnownNotApplied)
        {
            bool preWriteReadFailed = exception.FailurePhase
                == BladeWriteFailurePhase.ThermalPreWriteRead;
            return new ScopedApplyOutcome(
                WriteRejected(name, exception),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: preWriteReadFailed,
                WriteSubmissionState: exception.SubmissionState,
                WriteFailurePhase: exception.FailurePhase);
        }
        catch (BladeWriteException exception)
            when (writeAttempted
                && exception.SubmissionState is WriteSubmissionState.Indeterminate
                && reconcileUnconfirmableIndeterminateWrite)
        {
            return await ReconcileUnconfirmableIndeterminateWriteAsync(
                    subsystem,
                    name,
                    exception,
                    readState,
                    initial,
                    capabilities,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BladeWriteException exception)
            when (writeAttempted
                && exception.SubmissionState is WriteSubmissionState.Indeterminate
                && resolveIndeterminateWriteByReadback)
        {
            return await ResolveIndeterminateWriteByReadbackAsync(
                    subsystem,
                    name,
                    exception,
                    matches,
                    unchangedAfterIndeterminateWrite,
                    readState,
                    initial,
                    capabilities,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && writeAttempted)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem> { subsystem },
                cancellationToken,
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException exception)
            when (returnBlockedOnWriteAuthorityLoss)
        {
            string message = exception.Message;
            return new ScopedApplyOutcome(
                new ControlResult(
                    ControlResultStatus.Blocked,
                    message),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false,
                WriteSubmissionState: WriteSubmissionState.KnownNotApplied);
        }
        catch (WriteAuthorityLostException)
        {
            throw;
        }
        catch (Exception exception)
        {
            bool retryableThermalInitialReadFailure =
                !writeAttempted
                && initialStateReadStarted
                && !initialStateReadCompleted
                && IsRetryableThermalInitialReadFailure(subsystem, exception);
            if (setterCompleted && acceptSetterCompletionWithoutReadback)
            {
                return new ScopedApplyOutcome(
                    AppliedWithoutReadback(name),
                    initial,
                    WriteAttempted: true,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
            }

            if (setterCompleted && resolveIndeterminateWriteByReadback)
            {
                return await ResolveIndeterminateWriteByReadbackAsync(
                        subsystem,
                        name,
                        exception,
                        matches,
                        unchangedAfterIndeterminateWrite,
                        readState,
                        initial,
                        capabilities,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new ScopedApplyOutcome(
                writeAttempted
                    ? Indeterminate(name, exception)
                    : ReadFailure(name, exception),
                null,
                writeAttempted,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: !writeAttempted
                    || setterCompleted
                    || IsTerminalDeviceFailure(exception),
                ConfirmationUnavailable: setterCompleted,
                PreWriteState: setterCompleted ? initial : null,
                WriteSubmissionState: retryableThermalInitialReadFailure
                    ? WriteSubmissionState.KnownNotApplied
                    : exception is BladeWriteException writeFailure
                        ? writeFailure.SubmissionState
                        : null,
                WriteFailurePhase: retryableThermalInitialReadFailure
                    ? BladeWriteFailurePhase.ThermalPreWriteRead
                    : exception is BladeWriteException phasedFailure
                        ? phasedFailure.FailurePhase
                        : null);
        }
    }

    private static async Task<ScopedApplyOutcome>
        ReconcileUnconfirmableIndeterminateWriteAsync(
        DeviceSubsystem subsystem,
        string name,
        BladeWriteException exception,
        Func<CancellationToken, Task<ConfirmedDeviceState>> readState,
        ConfirmedDeviceState? initial,
        DeviceCapabilities? capabilities,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await readState(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ScopedApplyOutcome(
                new ControlResult(
                    ControlResultStatus.Failed,
                    $"{Indeterminate(name, exception).Message} Fresh readback cannot confirm exact fan targets."),
                final,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false,
                ConfirmationUnavailable: true,
                PreWriteState: initial,
                WriteSubmissionState: WriteSubmissionState.Indeterminate,
                WriteFailurePhase: exception.FailurePhase);
        }
        catch (OperationCanceledException cancellationException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem> { subsystem },
                cancellationToken,
                cancellationException);
        }
        catch
        {
            return new ScopedApplyOutcome(
                Indeterminate(name, exception),
                null,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: true,
                ConfirmationUnavailable: true,
                PreWriteState: initial,
                WriteSubmissionState: WriteSubmissionState.Indeterminate,
                WriteFailurePhase: exception.FailurePhase);
        }
    }

    private static async Task<ScopedApplyOutcome>
        ResolveIndeterminateWriteByReadbackAsync(
        DeviceSubsystem subsystem,
        string name,
        Exception exception,
        Func<ConfirmedDeviceState, bool> matches,
        Func<ConfirmedDeviceState, ConfirmedDeviceState, bool>?
            unchangedAfterIndeterminateWrite,
        Func<CancellationToken, Task<ConfirmedDeviceState>> readState,
        ConfirmedDeviceState? initial,
        DeviceCapabilities? capabilities,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await readState(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (matches(final))
            {
                return new ScopedApplyOutcome(
                    new ControlResult(
                        ControlResultStatus.Applied,
                        $"{name} setting applied and confirmed by fresh readback after an indeterminate submission."),
                    final,
                    WriteAttempted: true,
                    ReadbackMatched: true,
                    capabilities,
                    DeviceAccessFailed: false,
                    WriteSubmissionState: WriteSubmissionState.Indeterminate,
                    WriteFailurePhase: exception is BladeWriteException matchedFailure
                        ? matchedFailure.FailurePhase
                        : null);
            }

            if (initial is not null
                && unchangedAfterIndeterminateWrite?.Invoke(initial, final) == true)
            {
                return new ScopedApplyOutcome(
                    new ControlResult(
                        ControlResultStatus.Failed,
                        $"{Indeterminate(name, exception).Message} Fresh readback confirmed that the prior {name.ToLowerInvariant()} state remained active."),
                    final,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false,
                    WriteSubmissionState: WriteSubmissionState.Indeterminate,
                    WriteFailurePhase: exception is BladeWriteException unchangedFailure
                        ? unchangedFailure.FailurePhase
                        : null);
            }

            return new ScopedApplyOutcome(
                new ControlResult(
                    ControlResultStatus.Failed,
                    $"{Indeterminate(name, exception).Message} Fresh readback did not match the requested or prior {name.ToLowerInvariant()} state."),
                final,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false,
                WriteSubmissionState: WriteSubmissionState.Indeterminate,
                WriteFailurePhase: exception is BladeWriteException contradictedFailure
                    ? contradictedFailure.FailurePhase
                    : null);
        }
        catch (OperationCanceledException cancellationException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem> { subsystem },
                cancellationToken,
                cancellationException);
        }
        catch
        {
            return new ScopedApplyOutcome(
                Indeterminate(name, exception),
                null,
                WriteAttempted: true,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: true,
                ConfirmationUnavailable: initial is not null,
                PreWriteState: initial,
                WriteSubmissionState: WriteSubmissionState.Indeterminate,
                WriteFailurePhase: exception is BladeWriteException writeFailure
                    ? writeFailure.FailurePhase
                    : null);
        }
    }

    private static FullTransitionApplyOutcome CreateFullOutcome(
        IReadOnlyDictionary<DeviceSubsystem, ControlResult> results,
        ConfirmedDeviceState? readback,
        IReadOnlySet<DeviceSubsystem> attempted,
        IReadOnlySet<DeviceSubsystem> matched,
        IReadOnlySet<DeviceSubsystem> contradicted,
        DeviceCapabilities capabilities,
        bool DeviceAccessFailed,
        IReadOnlySet<DeviceSubsystem>? confirmationUnavailable = null,
        ConfirmedDeviceState? preWriteState = null,
        DesiredDeviceState? effectiveDesired = null) => new(
            new SubsystemApplyResults(
                results[DeviceSubsystem.Performance],
                results[DeviceSubsystem.Fans],
                results[DeviceSubsystem.Battery],
                results[DeviceSubsystem.Lighting]),
            readback,
            new HashSet<DeviceSubsystem>(attempted),
            new HashSet<DeviceSubsystem>(matched),
            new HashSet<DeviceSubsystem>(contradicted),
            capabilities,
            DeviceAccessFailed,
            confirmationUnavailable,
            preWriteState,
            effectiveDesired);

    private BatteryOperationPlan CreateBatteryPlan(
        DesiredBatteryState desired,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        if (confirmed?.TemporaryFullCharge is null)
        {
            return BlockedBatteryPlan("Battery");
        }

        if (desired.TemporaryFullCharge != confirmed.TemporaryFullCharge.Value)
        {
            return CreateTemporaryFullChargePlan(
                desired.TemporaryFullCharge,
                capabilities,
                confirmed);
        }

        if (confirmed.TemporaryFullCharge.Value)
        {
            return BatteryMatches(desired, confirmed)
                ? MatchingBatteryPlan("Battery", confirmed)
                : BlockedBatteryPlan("Battery");
        }

        if (desired.ProtectionEnabled != confirmed.ProtectionEnabled)
        {
            return CreateProtectionPlan(
                desired.ProtectionEnabled,
                desired.ConfiguredPolicy,
                capabilities,
                confirmed);
        }

        if (desired.ProtectionEnabled
            && desired.ConfiguredPolicy.ChargeLimitPercent != confirmed.ChargeLimitPercent)
        {
            return CreateChargeLimitPlan(
                desired.ConfiguredPolicy,
                capabilities,
                confirmed);
        }

        return BatteryMatches(desired, confirmed)
            ? MatchingBatteryPlan("Battery", confirmed)
            : BlockedBatteryPlan("Battery");
    }

    private BatteryOperationPlan CreateChargeLimitPlan(
        BatteryPolicy value,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        bool admitted = capabilities.ChargeLimits.Contains(value.ChargeLimitPercent);
        bool exactPrecondition = confirmed is
        {
            ProtectionEnabled: true,
            TemporaryFullCharge: false,
            ChargeLimitPercent: int currentLimit,
        } && capabilities.ChargeLimits.Contains(currentLimit);
        if (!admitted || !exactPrecondition)
        {
            return BlockedBatteryPlan("Charge limit");
        }

        bool Matches(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            TemporaryFullCharge: false,
            ChargeLimitPercent: var limit,
        } && limit == value.ChargeLimitPercent;
        return new BatteryOperationPlan(
            "Charge limit",
            Supported: true,
            InitiallyMatching: Matches(confirmed),
            token => device.SetChargeLimitAsync(value, token),
            Matches);
    }

    private BatteryOperationPlan CreateProtectionPlan(
        bool enabled,
        BatteryPolicy storedPolicy,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        if (confirmed is { ProtectionEnabled: false, TemporaryFullCharge: false }
            && !enabled)
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        bool storedLimitAdmitted = capabilities.ChargeLimits.Contains(
            storedPolicy.ChargeLimitPercent);
        if (!storedLimitAdmitted || confirmed?.TemporaryFullCharge is not false)
        {
            return BlockedBatteryPlan("Battery protection");
        }

        if (!enabled)
        {
            if (!capabilities.SupportsBatteryProtectionOff
                || confirmed is not
                {
                    ProtectionEnabled: true,
                    ChargeLimitPercent: var currentLimit,
                }
                || currentLimit != storedPolicy.ChargeLimitPercent)
            {
                return BlockedBatteryPlan("Battery protection");
            }

            bool MatchesOff(ConfirmedBatteryState? state) => state is
            {
                ProtectionEnabled: false,
                ChargeLimitPercent: null,
                TemporaryFullCharge: false,
            };
            return new BatteryOperationPlan(
                "Battery protection",
                Supported: true,
                InitiallyMatching: false,
                device.SetBatteryProtectionOffAsync,
                MatchesOff);
        }

        bool MatchesOn(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            TemporaryFullCharge: false,
            ChargeLimitPercent: var limit,
        } && limit == storedPolicy.ChargeLimitPercent;
        if (MatchesOn(confirmed))
        {
            return MatchingBatteryPlan("Battery protection", confirmed);
        }

        if (confirmed is not
            {
                ProtectionEnabled: false,
                ChargeLimitPercent: null,
                TemporaryFullCharge: false,
            })
        {
            return BlockedBatteryPlan("Battery protection");
        }

        return new BatteryOperationPlan(
            "Battery protection",
            Supported: true,
            InitiallyMatching: false,
            token => device.SetChargeLimitAsync(storedPolicy, token),
            MatchesOn);
    }

    private BatteryOperationPlan CreateTemporaryFullChargePlan(
        bool enabled,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? confirmed)
    {
        string operationName = enabled
            ? "Temporary full charge"
            : "Temporary full charge cancellation";

        if (confirmed?.TemporaryFullCharge == enabled)
        {
            return MatchingBatteryPlan(operationName, confirmed);
        }

        if (!capabilities.SupportsTemporaryFullCharge
            || confirmed is not
            {
                ProtectionEnabled: true,
                ChargeLimitPercent: int currentLimit,
                TemporaryFullCharge: not null,
            }
            || !capabilities.ChargeLimits.Contains(currentLimit))
        {
            return BlockedBatteryPlan(operationName);
        }

        bool Matches(ConfirmedBatteryState? state) => state is
        {
            ProtectionEnabled: true,
            ChargeLimitPercent: var limit,
            TemporaryFullCharge: var temporary,
        } && limit == currentLimit && temporary == enabled;
        return new BatteryOperationPlan(
            operationName,
            Supported: true,
            InitiallyMatching: false,
            token => device.SetTemporaryFullChargeAsync(enabled, token),
            Matches);
    }

    private static BatteryOperationPlan MatchingBatteryPlan(
        string name,
        ConfirmedBatteryState confirmed) => new(
            name,
            Supported: true,
            InitiallyMatching: true,
            _ => Task.CompletedTask,
            state => state == confirmed);

    private static BatteryOperationPlan BlockedBatteryPlan(string name) => new(
        name,
        Supported: false,
        InitiallyMatching: false,
        _ => Task.CompletedTask,
        _ => false);

    private static bool BatteryMatches(
        DesiredBatteryState desired,
        ConfirmedBatteryState? confirmed) => confirmed is not null
        && desired.ProtectionEnabled == confirmed.ProtectionEnabled
        && (!desired.ProtectionEnabled
            || desired.ConfiguredPolicy.ChargeLimitPercent == confirmed.ChargeLimitPercent)
        && desired.TemporaryFullCharge == confirmed.TemporaryFullCharge;

    private static bool IsLightingValid(LightingSettings value)
        => LightingSettingsValidation.IsWellFormed(value);

    private static bool LightingSupported(
        DeviceCapabilities capabilities,
        LightingSettings value) =>
        capabilities.LightingEffects.Contains(value.Effect)
        && (value.LogoMode is null
            || capabilities.LogoLightingModes.Contains(value.LogoMode.Value));

    private static bool LightingMatches(
        ConfirmedDeviceState confirmed,
        LightingSettings desired)
    {
        LightingSettings desiredKeyboard = desired with { LogoMode = null };
        LightingSettings? confirmedKeyboard = confirmed.Lighting is { } lighting
            ? lighting with { LogoMode = null }
            : null;
        return confirmedKeyboard == desiredKeyboard && LogoMatches(confirmed, desired);
    }

    private static bool LogoMatches(
        ConfirmedDeviceState confirmed,
        LightingSettings desired) => desired.LogoMode is null
        || confirmed.LogoLighting == desired.LogoMode;

    private static bool IsTerminalDeviceFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or RazerProtocolException
            or TimeoutException;

    private static bool IsRetryableThermalInitialReadFailure(
        DeviceSubsystem subsystem,
        Exception exception) =>
        (subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans)
        && (exception is IOException or TimeoutException
            || exception is RazerProtocolException
            {
                ValidationFailure:
                    HidProtocolValidationFailure.TransactionMismatch
                    or HidProtocolValidationFailure.CommandClassMismatch
                    or HidProtocolValidationFailure.CommandIdMismatch,
            });

    private static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum => Enum.IsDefined(value);

    private static ControlResult Unsupported(string name) => new(
        ControlResultStatus.Blocked,
        $"{name} value is not supported by the verified capability table.");

    private static ControlResult Unchanged(string name) => new(
        ControlResultStatus.Unchanged,
        $"{name} is already confirmed.");

    private static ControlResult ExternalChangePreserved(string name) => new(
        ControlResultStatus.Blocked,
        $"Automatic {name.ToLowerInvariant()} profile application is paused because " +
        "the setting changed outside OpenBlade.");

    private static ControlResult PreservedForHyperBoost(string name) => new(
        ControlResultStatus.Blocked,
        $"Automatic {name.ToLowerInvariant()} profile application was deferred " +
        "because HyperBoost remains active; performance and fan changes are deferred until it exits.");

    private static ControlResult AutomaticReadUnavailable(string name) => new(
        ControlResultStatus.Blocked,
        $"Automatic {name.ToLowerInvariant()} profile application was skipped because " +
        "the state could not be confirmed during the power-source transition.");

    private static ControlResult Applied(string name) => new(
        ControlResultStatus.Applied,
        $"{name} setting applied and confirmed by readback.");

    private static ControlResult AppliedWithoutReadback(string name) => new(
        ControlResultStatus.Applied,
        $"{name} command completed, but this laptop does not report the resulting state.");

    private static ControlResult Indeterminate(string name, Exception? exception) => new(
        ControlResultStatus.Failed,
        exception is null
            ? $"{name} write is indeterminate because readback did not match."
            : $"{name} write is indeterminate: {exception.Message}");

    private static ControlResult WriteRejected(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} write was not applied: {exception.Message}");

    private static ControlResult CapabilityRefused(string name, Exception exception) => new(
        ControlResultStatus.Blocked,
        $"{name} is unavailable: {exception.Message}");

    private static ControlResult ReadFailure(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} could not be read before writing: {exception.Message}");

    private static ControlResult FinalReadbackChanged(string name) => new(
        ControlResultStatus.Failed,
        $"{name} changed before the final aggregate readback; no {name.ToLowerInvariant()} setter was attempted.");

    private static ControlResult SubsequentWritesBlocked(string reason) => new(
        ControlResultStatus.Blocked,
        $"No further hardware writes were attempted: {reason}");

    private static bool IsVerifiedThermalCombination(
        PerformancePreset performance,
        FanMode fans) => (performance, fans) is
        (PerformancePreset.Silent, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Automatic)
        or (PerformancePreset.Performance, FanMode.Automatic)
        or (PerformancePreset.Custom, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Maximum)
        or (PerformancePreset.Balanced, FanMode.Fixed)
        or (PerformancePreset.Balanced, FanMode.ManualCurve)
        or (PerformancePreset.BatterySaver, FanMode.Automatic)
        or (PerformancePreset.BatteryBalanced, FanMode.Automatic);

    private static bool IsConfirmedManualFanWriteBaseline(
        ConfirmedDeviceState state) => state.Fans switch
        {
            FanMode.Automatic => state.Performance == PerformancePreset.Balanced,
            FanMode.Maximum or FanMode.Fixed or FanMode.ManualCurve =>
                state.Performance is null or PerformancePreset.Balanced,
            _ => false,
        };

    private static bool FanModeMatches(FanMode? confirmed, FanMode desired) =>
        desired == FanMode.Automatic
            ? confirmed == FanMode.Automatic
            : DeviceSubsystemOrder.IsManualFanMode(desired)
                && DeviceSubsystemOrder.IsManualFanMode(confirmed);

    private bool PerformanceMatches(
        ConfirmedDeviceState state,
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning) =>
        state.Performance == performance
        && (performance != PerformancePreset.Custom
            || state.CustomPerformance is { } confirmed
                && customPerformance is { } desired
                && confirmed.Cpu == desired.Cpu
                && confirmed.Gpu == desired.Gpu)
        && (gpuClockTuning is null
            || (gpuClockTuning.Enabled
                ? state.GpuClockOffsets?.Current == gpuClockTuning.EffectiveOffsets
                : device is IGpuClockOffsetManagementDevice managedDevice
                    ? state.GpuClockOffsets is null
                        && managedDevice.GpuClockOffsetManagementState is
                            GpuClockOffsetManagementState.ResetConfirmed
                            or GpuClockOffsetManagementState
                                .RelinquishedDefaultConfirmed
                    : state.GpuClockOffsets?.Current ==
                        GpuClockOffsetSettings.Default));

    private static bool PerformanceStateMatches(
        ConfirmedDeviceState left,
        ConfirmedDeviceState right) =>
        left.Performance == right.Performance
        && left.CustomPerformance == right.CustomPerformance
        && left.GpuClockOffsets?.Current == right.GpuClockOffsets?.Current
        && left.Fans == right.Fans
        && left.HyperBoostActive == right.HyperBoostActive;

    private void EnsureGpuClockOffsetManagementContract(
        DeviceCapabilities capabilities)
    {
        if (capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite }
            && device is not IGpuClockOffsetManagementDevice)
        {
            throw new CapabilityUnavailableException(
                "Writable GPU tuning requires serialized service management support.");
        }
    }

    private static bool CanApplyGpuClockOffsetsOnly(
        ConfirmedDeviceState state,
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning) =>
        gpuClockTuning is not null
        && state.Performance == performance
        && (performance != PerformancePreset.Custom
            || state.CustomPerformance is { } confirmed
                && customPerformance is { } desired
                && confirmed.Cpu == desired.Cpu
                && confirmed.Gpu == desired.Gpu);

    private Task ApplyPerformanceAndGpuClockTuningAsync(
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning,
        CancellationToken cancellationToken) =>
        singleAttemptPerformanceWrites
            ? device is ISingleAttemptThermalDevice singleAttemptDevice
                ? singleAttemptDevice.SetPerformanceOnceAsync(
                    performance,
                    customPerformance,
                    gpuClockTuning?.EffectiveOffsets,
                    gpuClockTuning?.Enabled == true,
                    cancellationToken)
                : Task.FromException(new CapabilityUnavailableException(
                    "The verified device does not expose single-attempt thermal writes."))
            : device is IGpuClockOffsetManagementDevice managedDevice
            ? managedDevice.SetPerformanceAsync(
                performance,
                customPerformance,
                gpuClockTuning?.EffectiveOffsets,
                gpuClockTuning?.Enabled == true,
                cancellationToken)
            : device.SetPerformanceAsync(
                performance,
                customPerformance,
                gpuClockTuning?.EffectiveOffsets,
                cancellationToken);

    private Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken) => singleAttemptFanWrites
            ? device is ISingleAttemptThermalDevice singleAttemptDevice
                ? singleAttemptDevice.SetFanControlOnceAsync(value, cancellationToken)
                : Task.FromException(new CapabilityUnavailableException(
                    "The verified device does not expose single-attempt thermal writes."))
            : device.SetFanControlAsync(value, cancellationToken);

    private Task ContinueFanTargetsAfterManualEntryOnceAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken) =>
        device is ISingleAttemptThermalDevice singleAttemptDevice
            ? singleAttemptDevice.ContinueFanTargetsAfterManualEntryOnceAsync(
                fan1Rpm,
                fan2Rpm,
                cancellationToken)
            : Task.FromException(new CapabilityUnavailableException(
                "The verified device does not expose target-only fan continuation."));

    private Task SetFanTargetsOnceAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken) =>
        device is ISingleAttemptThermalDevice singleAttemptDevice
            ? singleAttemptDevice.SetFanTargetsOnceAsync(
                fan1Rpm,
                fan2Rpm,
                cancellationToken)
            : Task.FromException(new CapabilityUnavailableException(
                "The verified device does not expose single-attempt fan-target writes."));

    private Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings value,
        bool retainManagement,
        CancellationToken cancellationToken) =>
        device is IGpuClockOffsetManagementDevice managedDevice
            ? managedDevice.SetGpuClockOffsetsAsync(
                value,
                retainManagement,
                cancellationToken)
            : device.SetGpuClockOffsetsAsync(value, cancellationToken);

    private Task RelinquishGpuClockOffsetManagementAsync(
        CancellationToken cancellationToken) =>
        device is IGpuClockOffsetManagementDevice managedDevice
            ? managedDevice.RelinquishGpuClockOffsetManagementAsync(cancellationToken)
            : Task.CompletedTask;

    private static bool GpuClockTuningSupported(
        DeviceCapabilities capabilities,
        GpuClockTuningSettings? gpuClockTuning) =>
        gpuClockTuning is null
        || capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite };

    private static DesiredDeviceState DropUnsupportedGpuClockTuning(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities)
    {
        GpuClockTuningSettings? gpuClockTuning = FilterUnsupportedGpuClockTuning(
            desired.GpuClockTuning,
            capabilities);
        if (ReferenceEquals(gpuClockTuning, desired.GpuClockTuning))
        {
            return desired;
        }

        return desired with { GpuClockTuning = gpuClockTuning };
    }

    private static GpuClockTuningSettings? FilterUnsupportedGpuClockTuning(
        GpuClockTuningSettings? gpuClockTuning,
        DeviceCapabilities capabilities)
    {
        if (gpuClockTuning is null
            || capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite })
        {
            return gpuClockTuning;
        }

        // Legacy profiles may retain GPU offsets on firmware where the model
        // policy is unavailable. Ignore only that optional component so power
        // mode and fan reconciliation can continue safely.
        return null;
    }

    private static void NormalizeLegacyGpuClockTuning(
        ref CustomPerformanceSettings? customPerformance,
        ref GpuClockTuningSettings? gpuClockTuning)
    {
        if (gpuClockTuning is not null
            || customPerformance?.GpuClockOffsets is not { } legacyOffsets)
        {
            return;
        }

        customPerformance = customPerformance with { GpuClockOffsets = null };
        gpuClockTuning = new GpuClockTuningSettings(
            Enabled: true,
            legacyOffsets);
    }

    private static bool IsThermalSubsystem(DeviceSubsystem subsystem) =>
        subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans;

    private sealed class FullTransitionApplyState
    {
        public Dictionary<DeviceSubsystem, ControlResult> Provisional { get; } = [];
        public HashSet<DeviceSubsystem> Attempted { get; } = [];
        public HashSet<DeviceSubsystem> Completed { get; } = [];
        public HashSet<DeviceSubsystem> Matched { get; } = [];
        public HashSet<DeviceSubsystem> Contradicted { get; } = [];
        public HashSet<DeviceSubsystem> ConfirmationUnavailable { get; } = [];
        public bool DeviceAccessFailed { get; set; }
    }

    private sealed record PendingSubsystem(
        string Name,
        bool Supported,
        bool InitiallyMatching,
        Func<CancellationToken, Task> Setter,
        Func<ConfirmedDeviceState, bool> Matches,
        bool AcceptSetterCompletionWithoutReadback = false)
    {
        public Func<ConfirmedDeviceState, CancellationToken, Task>? StateAwareSetter
        {
            get;
            init;
        }
    }

    private sealed record BatteryOperationPlan(
        string Name,
        bool Supported,
        bool InitiallyMatching,
        Func<CancellationToken, Task> Setter,
        Func<ConfirmedBatteryState?, bool> Matches);
}
