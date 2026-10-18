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
    DesiredDeviceState? EffectiveDesired = null,
    IReadOnlySet<DeviceSubsystem>? DeferredNotSubmitted = null);

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

    private readonly BatteryOperationExecutor batteryOperations;

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
        batteryOperations = new BatteryOperationExecutor(device);
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

    internal async Task<ScopedApplyOutcome> ApplyPerformanceTransitionOutcomeAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        IBladeDevice capabilityDevice = thermalStepRunner?.CurrentDevice ?? device;
        DeviceCapabilities capabilities;
        try
        {
            capabilities = await capabilityDevice.ReadCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ScopedApplyOutcome(
                ReadFailure("Performance", exception),
                Readback: null,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: true,
                EffectiveDesired: desired);
        }

        ConfirmedDeviceState? initial = null;
        bool restorePresetRangedFans = false;
        if (SupportsPresetRangedFixedFans(desired, capabilities))
        {
            try
            {
                initial = await capabilityDevice.ReadPerformanceFanStateAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                restorePresetRangedFans = RequiresPresetRangedFanRestore(
                    desired,
                    capabilities,
                    initial);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new ScopedApplyOutcome(
                    ReadFailure("Performance", exception),
                    Readback: null,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    Capabilities: capabilities,
                    DeviceAccessFailed: true,
                    EffectiveDesired: desired);
            }
        }

        if (!RequiresAutomaticFansForCustom(desired, capabilities)
            && !restorePresetRangedFans)
        {
            return await RunThermalStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.Requested,
                    (coordinator, token) => coordinator.ApplyPerformanceOutcomeAsync(
                        desired,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (restorePresetRangedFans)
        {
            var restoredCoupledWrites = new HashSet<DeviceSubsystem>();
            ScopedApplyOutcome restoredPerformance = await RunThermalStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.Requested,
                    (coordinator, token) => coordinator.ApplyPerformanceOutcomeAsync(
                        desired,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            RecordConfirmedCoupledWrite(
                restoredCoupledWrites,
                DeviceSubsystem.Performance,
                restoredPerformance);
            if (restoredPerformance.Result.Status is not (
                    ControlResultStatus.Applied or ControlResultStatus.Unchanged)
                || !restoredPerformance.ReadbackMatched)
            {
                return restoredPerformance with
                {
                    ConfirmedCoupledWrites = restoredCoupledWrites,
                    EffectiveDesired = desired,
                };
            }

            FanControlSettings requestedFans = desired.FanControl
                ?? FanControlSettings.FromLegacy(desired.Fans.Value);
            ScopedApplyOutcome fans = await RunThermalStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.Restoration,
                    (coordinator, token) => coordinator.ApplyFanControlOutcomeAsync(
                        requestedFans,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            RecordConfirmedCoupledWrite(
                restoredCoupledWrites,
                DeviceSubsystem.Fans,
                fans);
            return fans with
            {
                Readback = fans.Readback ?? restoredPerformance.Readback,
                WriteAttempted = fans.WriteAttempted || restoredPerformance.WriteAttempted,
                ReadbackMatched = fans.ReadbackMatched && restoredPerformance.ReadbackMatched,
                Capabilities = fans.Capabilities ?? restoredPerformance.Capabilities,
                DeviceAccessFailed = fans.DeviceAccessFailed
                    || restoredPerformance.DeviceAccessFailed,
                ConfirmedCoupledWrites = restoredCoupledWrites,
                ConfirmationUnavailable = fans.ConfirmationUnavailable
                    || restoredPerformance.ConfirmationUnavailable,
                PreWriteState = initial ?? restoredPerformance.PreWriteState,
                EffectiveDesired = desired,
            };
        }

        var confirmedCoupledWrites = new HashSet<DeviceSubsystem>();
        ScopedApplyOutcome automatic = await RunThermalStepAsync(
                DeviceSubsystem.Fans,
                ThermalTransitionStepPurpose.AutomaticBridge,
                (coordinator, token) => coordinator.ApplyFanControlOutcomeAsync(
                    FanControlSettings.Default,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        RecordConfirmedCoupledWrite(
            confirmedCoupledWrites,
            DeviceSubsystem.Fans,
            automatic);
        if (automatic.Result.Status is not (
                ControlResultStatus.Applied or ControlResultStatus.Unchanged)
            || !automatic.ReadbackMatched)
        {
            return automatic with
            {
                Result = automatic.Result with
                {
                    Message = $"Custom performance requires confirmed Automatic fans. {automatic.Result.Message}",
                },
                ConfirmedCoupledWrites = confirmedCoupledWrites,
                EffectiveDesired = desired,
            };
        }

        ScopedApplyOutcome performance = await RunThermalStepAsync(
                DeviceSubsystem.Performance,
                ThermalTransitionStepPurpose.Requested,
                (coordinator, token) => coordinator.ApplyPerformanceOutcomeAsync(
                    desired,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        return performance with
        {
            Readback = performance.Readback ?? automatic.Readback,
            WriteAttempted = performance.WriteAttempted || automatic.WriteAttempted,
            Capabilities = performance.Capabilities ?? automatic.Capabilities,
            DeviceAccessFailed = performance.DeviceAccessFailed
                || automatic.DeviceAccessFailed,
            ConfirmedCoupledWrites = confirmedCoupledWrites,
            ConfirmationUnavailable = performance.ConfirmationUnavailable
                || automatic.ConfirmationUnavailable,
            PreWriteState = automatic.PreWriteState ?? performance.PreWriteState,
            EffectiveDesired = desired,
        };
    }

    // Product 672's reviewed Synapse policy enters Custom only after returning
    // its preset-ranged manual fans to Automatic. Express that relationship
    // through existing capabilities so unrelated experimental models retain
    // their admitted Custom/manual behavior.
    internal static bool RequiresAutomaticFansForCustom(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities) =>
        desired.Performance.Value == PerformancePreset.Custom
        && UsesAutomaticCustomFanPolicy(capabilities)
        && capabilities.CustomPerformance?.Supports(desired.CustomPerformance) == true;

    internal static bool RequiresPresetRangedFanRestore(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities,
        ConfirmedDeviceState? confirmed) =>
        UsesAutomaticCustomFanPolicy(capabilities)
        && SupportsPresetRangedFixedFans(desired, capabilities)
        && confirmed is
        {
            Performance: PerformancePreset.Custom,
            Fans: FanMode.Automatic,
        };

    private static bool UsesAutomaticCustomFanPolicy(
        DeviceCapabilities capabilities) =>
        capabilities.PerformancePresets.Contains(PerformancePreset.Custom)
        && capabilities.CustomPerformance is not null
        && capabilities.FanSpeedRangesByPerformancePreset.Count > 0
        && capabilities.GetFanSpeedRange(PerformancePreset.Custom) is null;

    internal static bool SupportsPresetRangedFixedFans(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities) =>
        desired.Fans.Value == FanMode.Fixed
        && capabilities.GetFanSpeedRange(desired.Performance.Value)?.Supports(
            (desired.FanControl ?? FanControlSettings.FromLegacy(FanMode.Fixed))
                .FixedSpeedRpm) == true;

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
        bool supportsDirectManualFanTransitions = false;
        bool reportsPerformanceWithManualFans = false;
        bool PerformanceSupported(DeviceCapabilities capabilities)
        {
            supportsDirectManualFanTransitions =
                capabilities.SupportsDirectManualFanTransitions;
            reportsPerformanceWithManualFans = capabilities.ReportsPerformanceWithManualFans;
            effectiveGpuClockTuning = FilterUnsupportedGpuClockTuning(
                gpuClockTuning,
                capabilities);
            return capabilities.PerformancePresets.Contains(value)
                && (value != PerformancePreset.Custom
                    || capabilities.CustomPerformance?.Supports(customPerformance) == true);
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
            state => (!supportsDirectManualFanTransitions
                    || reportsPerformanceWithManualFans
                    || state.Fans == FanMode.Automatic)
                && PerformanceMatches(
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
            state => PerformanceInitialStateBlockReason(
                state,
                value,
                supportsDirectManualFanTransitions),
            stateAwareSetter: (state, token) =>
                CanApplyGpuClockOffsetsOnly(
                    state,
                    value,
                    customPerformance,
                    effectiveGpuClockTuning,
                    firmwarePerformanceSelectorHidden: false)
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

    internal Task<ScopedApplyOutcome> ApplyGpuClockTuningOutcomeAsync(
        GpuClockTuningSettings value,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeWrite = null,
        bool ownsHyperBoost = false) => ApplyScopedAsync(
            DeviceSubsystem.Performance,
            "GPU clock tuning",
            GpuClockTuningSettings.IsWellFormed(value),
            capabilities => capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite },
            state => (state.HyperBoostActive == false || ownsHyperBoost)
                && GpuClockTuningMatches(state, value),
            async token =>
            {
                if (beforeWrite is not null)
                {
                    await beforeWrite(token).ConfigureAwait(false);
                }
                await SetGpuClockOffsetsAsync(value.EffectiveOffsets, value.Enabled, token)
                    .ConfigureAwait(false);
            },
            cancellationToken,
            initialStateBlockReason: state => state.HyperBoostActive != false && !ownsHyperBoost
                ? "GPU clock tuning requires confirmed OpenBlade ownership of active HyperBoost."
                : null,
            resolveIndeterminateWriteByReadback: true,
            unchangedAfterIndeterminateWrite: PerformanceStateMatches,
            writeBlockReason: performanceWriteBlockReason,
            returnBlockedOnWriteAuthorityLoss: true);

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
        bool supportsDirectManualFanTransitions = false;
        DeviceCapabilities? admittedCapabilities = null;
        bool FansSupported(DeviceCapabilities capabilities)
        {
            admittedCapabilities = capabilities;
            supportsDirectManualFanTransitions =
                capabilities.SupportsDirectManualFanTransitions;
            return capabilities.FanModes.Contains(value.Mode)
                && (value.Mode != FanMode.Fixed
                    || capabilities.SupportsFanSpeedInAnyPerformancePreset(
                        value.FixedSpeedRpm));
        }
        Func<ConfirmedDeviceState, string?>? manualEntryBlockReason =
            DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                ? state => value.Mode == FanMode.Fixed
                        && state.Performance is { } performance
                        && admittedCapabilities?.FanSpeedRangesByPerformancePreset.Count > 0
                        ? admittedCapabilities.GetFanSpeedRange(performance)
                                ?.Supports(value.FixedSpeedRpm) == true
                            && IsConfirmedDirectManualFanWriteBaseline(state)
                                ? null
                                : "Fixed fan control requires a supported speed for the refreshed, confirmed performance preset with HyperBoost off."
                    : (supportsDirectManualFanTransitions
                        ? IsConfirmedDirectManualFanWriteBaseline(state)
                        : IsConfirmedManualFanWriteBaseline(state))
                    ? null
                    : supportsDirectManualFanTransitions
                        ? "Fixed and manual fan control require a refreshed, confirmed Automatic or manual fan state with HyperBoost off."
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
            FansSupported,
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

    internal Task<ScopedApplyOutcome> ApplyRecoveryFanControlOutcomeAsync(
        FanControlSettings value,
        CancellationToken cancellationToken,
        Func<ConfirmedDeviceState, string?>? initialStateBlockReason = null) =>
        ApplyRequestedFanControlStepOutcomeAsync(
            value,
            cancellationToken,
            initialStateBlockReason);

    private async Task<ScopedApplyOutcome> ApplyRequestedFanControlStepOutcomeAsync(
        FanControlSettings requested,
        CancellationToken cancellationToken,
        Func<ConfirmedDeviceState, string?>? initialStateBlockReason = null)
    {
        ScopedApplyOutcome fans = await RunThermalStepAsync(
            DeviceSubsystem.Fans,
            ThermalTransitionStepPurpose.Requested,
            (coordinator, token) => coordinator.ApplyFanControlOutcomeAsync(
                requested,
                token,
                initialStateBlockReason: initialStateBlockReason),
            cancellationToken).ConfigureAwait(false);
        if (thermalStepRunner is null
            || fans.WriteSubmissionState != WriteSubmissionState.Indeterminate
            || !IsAdjustableFanManualEntrySubmission(fans.WriteFailurePhase)
            || ResolveManualFanTarget(requested, fans.Capabilities) is not int targetRpm)
        {
            return fans;
        }

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
            return continuation with
            {
                Result = continuation.Result with
                {
                    Message =
                        "Fans entered manual mode and the target-only continuation completed without replaying either entry command.",
                },
            };
        }

        return fans with
        {
            Result = fans.Result with
            {
                Message = $"{fans.Result.Message} Target-only continuation result: {continuation.Result.Message}",
            },
            ConfirmationUnavailable = true,
        };
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
                && state.HyperBoostActive == false,
            // ApplyScopedAsync evaluates this only after its post-write read
            // completes. A read failure keeps the continuation unconfirmed.
            setterCompletionEvidenceConfirmsReadback: true,
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
                && state.HyperBoostActive == false,
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
        DeviceCapabilities transitionCapabilities;
        try
        {
            IBladeDevice capabilityDevice = thermalStepRunner?.CurrentDevice ?? device;
            transitionCapabilities = await capabilityDevice
                .ReadCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ScopedApplyOutcome(
                ReadFailure("Fans", exception),
                null,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: null,
                DeviceAccessFailed: true);
        }

        if (!transitionCapabilities.FanModes.Contains(requested.Mode))
        {
            return new ScopedApplyOutcome(
                Unsupported("Fans"),
                null,
                WriteAttempted: false,
                ReadbackMatched: false,
                Capabilities: transitionCapabilities,
                DeviceAccessFailed: false);
        }

        bool supportsDirectManualFanTransitions =
            transitionCapabilities.SupportsDirectManualFanTransitions;
        if (supportsDirectManualFanTransitions
            && !transitionCapabilities.ReportsPerformanceWithManualFans
            && requested.Mode == FanMode.Automatic
            && previousManual)
        {
            return await ApplyDirectAutomaticFanTransitionOutcomeAsync(
                performanceIntent,
                customPerformanceIntent,
                writeAttempted,
                confirmedCoupledWrites,
                cancellationToken).ConfigureAwait(false);
        }

        bool requiresAutomaticBridge = !supportsDirectManualFanTransitions
            && requestedManual
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

        ScopedApplyOutcome fans = await ApplyRequestedFanControlStepOutcomeAsync(
            requested,
            cancellationToken).ConfigureAwait(false);
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

    private async Task<ScopedApplyOutcome>
        ApplyDirectAutomaticFanTransitionOutcomeAsync(
        PerformancePreset performanceIntent,
        CustomPerformanceSettings? customPerformanceIntent,
        HashSet<DeviceSubsystem> writeAttempted,
        HashSet<DeviceSubsystem> confirmedCoupledWrites,
        CancellationToken cancellationToken)
    {
        ScopedApplyOutcome performance;
        try
        {
            performance = await RunThermalStepAsync(
                DeviceSubsystem.Performance,
                ThermalTransitionStepPurpose.Requested,
                (coordinator, token) => coordinator
                    .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                        performanceIntent,
                        customPerformanceIntent,
                        token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (exception.WriteAttempted.Contains(DeviceSubsystem.Performance))
            {
                writeAttempted.Add(DeviceSubsystem.Performance);
                writeAttempted.Add(DeviceSubsystem.Fans);
            }
            throw;
        }

        RecordWriteAttempt(writeAttempted, DeviceSubsystem.Performance, performance);
        if (performance.WriteAttempted)
        {
            writeAttempted.Add(DeviceSubsystem.Fans);
        }
        RecordConfirmedCoupledWrite(
            confirmedCoupledWrites,
            DeviceSubsystem.Performance,
            performance);

        string message = performance.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged
            ? performance.WriteAttempted
                ? $"Fans returned to Automatic with {performanceIntent} performance."
                : $"Fans and {performanceIntent} performance already matched the requested state."
            : $"Fans could not return to Automatic with the saved {performanceIntent} performance intent. {performance.Result.Message}";
        return performance with
        {
            Result = performance.Result with { Message = message },
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
            (capabilities, confirmed) => BatteryOperationPlanner.CreateChargeLimitPlan(
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
            (capabilities, confirmed) => BatteryOperationPlanner.CreateProtectionPlan(
                enabled,
                storedPolicy,
                capabilities,
                confirmed),
            cancellationToken);
    }

    internal Task<ScopedApplyOutcome> ApplyTemporaryFullChargeOutcomeAsync(
        bool enabled,
        CancellationToken cancellationToken) => ApplyBatteryOperationAsync(
            (capabilities, confirmed) => BatteryOperationPlanner.CreateTemporaryFullChargePlan(
                enabled,
                capabilities,
                confirmed),
            cancellationToken);

    private Task<ScopedApplyOutcome> ApplyBatteryDesiredOutcomeAsync(
        DesiredBatteryState desired,
        CancellationToken cancellationToken) => ApplyBatteryOperationAsync(
            (capabilities, confirmed) => BatteryOperationPlanner.CreateBatteryPlan(
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
            Task<PowerTransitionDeviceStateReadResult>>? deviceStateReader = null,
        Func<IBladeDevice, ConfirmedDeviceState, IReadOnlySet<DeviceSubsystem>>?
            continuationScopeResolver = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var transition = new FullTransitionApplyState();
        DeviceCapabilities capabilities;
        ConfirmedDeviceState initial;
        bool lightingReadUnavailable = false;
        PartialDeviceStateRead? initialPartialRead = null;
        IReadOnlySet<DeviceSubsystem>? continuationScope = null;

        // A content-rejected getter leaves only its dependent subsystems
        // unknown; every channel or safety failure still escapes the read.
        async Task<(PowerTransitionDeviceStateReadResult Read, PartialDeviceStateRead? Partial)>
            ReadTransitionStateAsync(IBladeDevice readDevice)
        {
            try
            {
                PowerTransitionDeviceStateReadResult read =
                    deviceStateReader is not null
                        ? await deviceStateReader(readDevice, cancellationToken)
                            .ConfigureAwait(false)
                        : stateReader is not null
                            ? await stateReader(cancellationToken).ConfigureAwait(false)
                            : new(
                                await readDevice.ReadStateAsync(cancellationToken)
                                    .ConfigureAwait(false),
                                LightingOffConfirmationUnavailable: false);
                return (read, null);
            }
            catch (RazerProtocolException exception) when (
                DeviceStateReadIsolation.PartialRead(exception) is { } partial)
            {
                return (
                    new(partial.State, partial.LightingOffConfirmationUnavailable),
                    partial);
            }
        }

        try
        {
            IBladeDevice initialDevice = thermalStepRunner?.CurrentDevice ?? device;
            capabilities = await initialDevice.ReadCapabilitiesAsync(cancellationToken);
            EnsureGpuClockOffsetManagementContract(capabilities);
            desired = DropUnsupportedGpuClockTuning(desired, capabilities);
            if (desired.GpuClockTuning is null && continuationScopeResolver is null)
            {
                // Null means OpenBlade no longer manages or reports NVIDIA
                // offsets; it does not mean reset them. A reset would require
                // touching NVML and could wake an idle dGPU. Driver-held
                // offsets may therefore remain applied until a later managed
                // profile applies new values or the driver reloads.
                await RelinquishGpuClockOffsetManagementAsync(cancellationToken);
            }

            PowerTransitionDeviceStateReadResult initialRead;
            (initialRead, initialPartialRead) =
                await ReadTransitionStateAsync(initialDevice).ConfigureAwait(false);
            initial = initialRead.State;
            lightingReadUnavailable =
                initialRead.LightingOffConfirmationUnavailable;
            cancellationToken.ThrowIfCancellationRequested();
            continuationScope = continuationScopeResolver?.Invoke(initialDevice, initial);
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
                cancellationToken,
                continuationScope,
                initialPartialRead);

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
                    effectiveDesired: desired,
                    deferredNotSubmitted: transition.DeferredNotSubmitted);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final;
            bool finalLightingReadUnavailable;
            PartialDeviceStateRead? finalPartialRead;
            try
            {
                IBladeDevice finalDevice = thermalStepRunner?.CurrentDevice ?? device;
                PowerTransitionDeviceStateReadResult finalRead;
                (finalRead, finalPartialRead) =
                    await ReadTransitionStateAsync(finalDevice).ConfigureAwait(false);
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
                    // A setter whose completion is the only confirmation this laptop
                    // offers (host-rendered keyboard effects, manual fan modes) was
                    // already validated by the setter itself; a later aggregate read
                    // failure is a device-access fault, not evidence the write was
                    // undone, so report it the same way as the scoped path and the
                    // success path do. A subsystem already recorded as
                    // confirmation-unavailable must still be reported Indeterminate,
                    // matching the precedence ReconcileFinalReadback gives it.
                    transition.Provisional[subsystem] =
                        HasAcceptedSetterCompletionWithoutReadback(
                            subsystem,
                            states[subsystem],
                            transition)
                            && !transition.ConfirmationUnavailable.Contains(subsystem)
                            ? AppliedWithoutReadback(states[subsystem].Name)
                            : Indeterminate(states[subsystem].Name, exception);
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
                    effectiveDesired: desired,
                    deferredNotSubmitted: transition.DeferredNotSubmitted);
            }

            var finalReadUnavailable = new HashSet<DeviceSubsystem>();
            if (finalLightingReadUnavailable)
            {
                finalReadUnavailable.Add(DeviceSubsystem.Lighting);
            }
            if (finalPartialRead is not null)
            {
                finalReadUnavailable.UnionWith(finalPartialRead.UnavailableSubsystems);
            }
            ReconcileFinalReadback(
                desired,
                states,
                final,
                finalReadUnavailable,
                transition,
                capabilities.SupportsDirectManualFanTransitions && !capabilities.ReportsPerformanceWithManualFans,
                finalPartialRead);
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
                    || transition.DeferredNotSubmitted.Count > 0
                    ? initial
                    : null,
                effectiveDesired: desired,
                deferredNotSubmitted: transition.DeferredNotSubmitted);
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
        DesiredBatteryState desiredBattery = BatteryOperationPlanner.WithoutStaleFullCharge(
            desired.Battery,
            initial.Battery);
        BatteryOperationPlan batteryPlan = BatteryOperationPlanner.CreateBatteryPlan(
            desiredBattery,
            capabilities,
            initial.Battery);
        bool manualFansDesired = DeviceSubsystemOrder.IsManualFanMode(desired.Fans.Value);
        bool directTransitionBaselineSupported =
            !capabilities.SupportsDirectManualFanTransitions
            || !manualFansDesired
            || IsConfirmedDirectManualFanWriteBaseline(initial);
        bool thermalCombinationSupported = directTransitionBaselineSupported
            && IsVerifiedThermalCombination(
                desired.Performance.Value,
                desired.Fans.Value,
                capabilities);
        // A direct fan transition alone does not imply hidden performance.
        // Experimental backends with independent getters must apply and confirm
        // Custom changes while retaining manual fans; legacy selector handling stays.
        bool FirmwarePerformanceSelectorHidden(ConfirmedDeviceState state) =>
            !capabilities.ReportsPerformanceWithManualFans
            && manualFansDesired
            && DeviceSubsystemOrder.IsManualFanMode(state.Fans);
        bool latentManualPerformanceIntent =
            capabilities.SupportsDirectManualFanTransitions
            && !capabilities.ReportsPerformanceWithManualFans
            && manualFansDesired;
        bool PerformanceStateMatches(ConfirmedDeviceState state) =>
            !(capabilities.SupportsDirectManualFanTransitions
                && desired.Fans.Value == FanMode.Automatic
                && DeviceSubsystemOrder.IsManualFanMode(state.Fans))
            && (FirmwarePerformanceSelectorHidden(state)
                || PerformancePresetMatches(
                    state,
                    desired.Performance.Value,
                    desired.CustomPerformance))
            && GpuClockTuningMatches(state, desired.GpuClockTuning);
        bool PerformanceInitiallyMatches(ConfirmedDeviceState state) =>
            latentManualPerformanceIntent
                ? GpuClockTuningMatches(state, desired.GpuClockTuning)
                : PerformanceStateMatches(state);
        return new Dictionary<DeviceSubsystem, PendingSubsystem>
        {
            [DeviceSubsystem.Performance] = new(
                "Performance",
                thermalCombinationSupported
                    && IsDefined(desired.Performance.Value)
                    && capabilities.PerformancePresets.Contains(desired.Performance.Value)
                    && GpuClockTuningSupported(capabilities, desired.GpuClockTuning),
                PerformanceInitiallyMatches(initial),
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
                        desired.GpuClockTuning,
                        latentManualPerformanceIntent
                            || FirmwarePerformanceSelectorHidden(state))
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
                token => batteryOperations.ApplyTransitionAsync(
                    desiredBattery,
                    capabilities,
                    initial.Battery,
                    token),
                state => BatteryOperationPlanner.BatteryMatches(desiredBattery, state.Battery)),
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

    private async Task ApplyPendingSubsystemWritesAsync(
        DesiredDeviceState desired,
        DeviceCapabilities capabilities,
        ConfirmedDeviceState initial,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> states,
        IReadOnlySet<DeviceSubsystem>? automaticWriteExclusions,
        bool lightingReadUnavailable,
        FullTransitionApplyState transition,
        CancellationToken cancellationToken,
        IReadOnlySet<DeviceSubsystem>? continuationScope = null,
        PartialDeviceStateRead? initialPartialRead = null)
    {
        bool stopFurtherWrites = false;
        string? stopReason = null;
        bool stopThermalWrites = false;
        string? thermalStopReason = null;
        bool directAutomaticPair =
            capabilities.SupportsDirectManualFanTransitions
            && !capabilities.ReportsPerformanceWithManualFans
            && DeviceSubsystemOrder.IsManualFanMode(initial.Fans)
            && desired.Fans.Value == FanMode.Automatic
            && initial.HyperBoostActive == false
            && IsAutomaticPerformancePreset(desired.Performance.Value)
            && automaticWriteExclusions?.Contains(DeviceSubsystem.Performance) != true
            && automaticWriteExclusions?.Contains(DeviceSubsystem.Fans) != true
            && (continuationScope is null
                || continuationScope.Contains(DeviceSubsystem.Performance)
                    && continuationScope.Contains(DeviceSubsystem.Fans));
        bool coupledAutomaticFansHandled = false;
        IReadOnlyList<DeviceSubsystem> orderedSubsystems =
            directAutomaticPair
                ? DeviceSubsystemOrder.Default
                : DeviceSubsystemOrder.ForTransition(initial, desired);

        foreach (DeviceSubsystem subsystem in orderedSubsystems)
        {
            PendingSubsystem pending = states[subsystem];
            if (subsystem == DeviceSubsystem.Fans && coupledAutomaticFansHandled)
            {
                continue;
            }
            if (continuationScope is not null && !continuationScope.Contains(subsystem))
            {
                transition.ContinuationExcluded.Add(subsystem);
                transition.Provisional[subsystem] = new(
                    ControlResultStatus.Blocked,
                    $"{pending.Name} is not part of the remaining transition work.");
                continue;
            }
            if (initialPartialRead?.MessageFor(subsystem) is { } readFailure)
            {
                // Only the subsystem whose getter was content-rejected stays
                // unread; it is never written without a confirmed baseline.
                transition.Provisional[subsystem] = ReadFailure(pending.Name, readFailure);
                continue;
            }
            if (automaticWriteExclusions?.Contains(subsystem) == true)
            {
                transition.Provisional[subsystem] =
                    subsystem == DeviceSubsystem.Lighting && lightingReadUnavailable
                        ? AutomaticReadUnavailable(pending.Name)
                        : initial.HyperBoostActive == true
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
                if (!IsLatentManualPerformanceIntent(
                        subsystem,
                        desired,
                        capabilities.SupportsDirectManualFanTransitions && !capabilities.ReportsPerformanceWithManualFans))
                {
                    transition.Matched.Add(subsystem);
                }
                continue;
            }

            if (stopFurtherWrites)
            {
                transition.DeferredNotSubmitted.Add(subsystem);
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
                if (directAutomaticPair
                    && subsystem == DeviceSubsystem.Performance
                    && thermalOutcome.WriteAttempted)
                {
                    coupledAutomaticFansHandled = true;
                    transition.Attempted.Add(DeviceSubsystem.Fans);
                    if (thermalOutcome.ConfirmationUnavailable)
                    {
                        transition.ConfirmationUnavailable.Add(DeviceSubsystem.Fans);
                    }
                    if (thermalOutcome.Result.Status is
                        ControlResultStatus.Applied or ControlResultStatus.Unchanged)
                    {
                        transition.Completed.Add(DeviceSubsystem.Fans);
                        transition.Provisional[DeviceSubsystem.Fans] =
                            Indeterminate(states[DeviceSubsystem.Fans].Name, null);
                    }
                    else
                    {
                        transition.Provisional[DeviceSubsystem.Fans] =
                            thermalOutcome.Result;
                    }
                }
                continue;
            }

            transition.Attempted.Add(subsystem);
            bool appliesDirectAutomaticPair = directAutomaticPair
                && subsystem == DeviceSubsystem.Performance;
            if (appliesDirectAutomaticPair)
            {
                transition.Attempted.Add(DeviceSubsystem.Fans);
                coupledAutomaticFansHandled = true;
            }
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
                if (appliesDirectAutomaticPair)
                {
                    transition.Completed.Add(DeviceSubsystem.Fans);
                    transition.Provisional[DeviceSubsystem.Fans] =
                        Indeterminate(states[DeviceSubsystem.Fans].Name, null);
                }
            }
            catch (CapabilityUnavailableException exception)
            {
                transition.Attempted.Remove(subsystem);
                if (appliesDirectAutomaticPair)
                {
                    transition.Attempted.Remove(DeviceSubsystem.Fans);
                    coupledAutomaticFansHandled = false;
                }
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
                if (appliesDirectAutomaticPair)
                {
                    transition.Attempted.Remove(DeviceSubsystem.Fans);
                    coupledAutomaticFansHandled = false;
                }
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
                transition.ConfirmationUnavailable.Add(subsystem);
                if (appliesDirectAutomaticPair)
                {
                    transition.Provisional[DeviceSubsystem.Fans] =
                        Indeterminate(states[DeviceSubsystem.Fans].Name, exception);
                    transition.ConfirmationUnavailable.Add(DeviceSubsystem.Fans);
                }
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
                ConfirmationUnavailable: true,
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
                DeviceAccessFailed: IsTerminalDeviceFailure(exception),
                ConfirmationUnavailable: true,
                WriteSubmissionState: WriteSubmissionState.Indeterminate);
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
        FullTransitionApplyState transition,
        bool supportsDirectManualFanTransitions,
        PartialDeviceStateRead? finalPartialRead = null)
    {
        transition.Matched.Clear();
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
        {
            if (transition.ContinuationExcluded.Contains(subsystem))
            {
                continue;
            }
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
                                    finalPartialRead?.MessageFor(subsystem)
                                        ?? "The final lighting state could not be confirmed."));
                }
                else if (transition.Provisional[subsystem].Status
                    == ControlResultStatus.Unchanged
                    && finalPartialRead?.MessageFor(subsystem) is null)
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
                if (IsLatentManualPerformanceIntent(
                        subsystem,
                        desired,
                        supportsDirectManualFanTransitions))
                {
                    transition.Matched.Remove(subsystem);
                }
                else if (pending.Matches(final))
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

    private static bool IsLatentManualPerformanceIntent(
        DeviceSubsystem subsystem,
        DesiredDeviceState desired,
        bool supportsDirectManualFanTransitions) =>
        supportsDirectManualFanTransitions
        && subsystem == DeviceSubsystem.Performance
        && DeviceSubsystemOrder.IsManualFanMode(desired.Fans.Value);

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
            initial = await DeviceStateReadIsolation.ReadAsync(
                device.ReadStateAsync,
                DeviceStateReadIsolation.Battery,
                cancellationToken);
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
                await batteryOperations.ExecuteAsync(plan.Operation, cancellationToken);
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
                // A lost command response is not evidence of an external write.
                // Retain causal intent for a later read-only confirmation; never
                // replay a command that may already have reached the device.
                return new ScopedApplyOutcome(
                    Indeterminate(plan.Name, exception),
                    null,
                    WriteAttempted: true,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: true,
                    ConfirmationUnavailable: true,
                    PreWriteState: initial,
                    WriteSubmissionState: exception.SubmissionState,
                    WriteFailurePhase: exception.FailurePhase);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await DeviceStateReadIsolation.ReadAsync(
                device.ReadStateAsync,
                DeviceStateReadIsolation.Battery,
                cancellationToken);
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
                DeviceAccessFailed: false,
                ConfirmationUnavailable: final.Battery?.TemporaryFullCharge is null,
                PreWriteState: initial);
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
        bool setterCompletionEvidenceConfirmsReadback = false,
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
        Func<CancellationToken, Task<ConfirmedDeviceState>> rawReadState =
            stateReader ?? scopedStateReader ?? device.ReadStateAsync;
        IReadOnlySet<DeviceSubsystem> readDependencies =
            DeviceStateReadIsolation.DependenciesOf(subsystem);
        Func<CancellationToken, Task<ConfirmedDeviceState>> readState =
            token => DeviceStateReadIsolation.ReadAsync(
                rawReadState,
                readDependencies,
                token);
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

            bool setterCompletionConfirmed = acceptSetterCompletionWithoutReadback
                && (setterCompletionEvidence?.Invoke(final) ?? true);
            return new ScopedApplyOutcome(
                setterCompletionConfirmed
                    ? AppliedWithoutReadback(name)
                    : Indeterminate(name, null),
                final,
                WriteAttempted: true,
                ReadbackMatched: setterCompletionConfirmed
                    && setterCompletionEvidenceConfirmsReadback,
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
        DesiredDeviceState? effectiveDesired = null,
        IReadOnlySet<DeviceSubsystem>? deferredNotSubmitted = null) => new(
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
            effectiveDesired,
            deferredNotSubmitted is null ? null : new HashSet<DeviceSubsystem>(deferredNotSubmitted));

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
            : $"{name} write is indeterminate: {NativeFailureDescription.Describe(exception)}");

    private static ControlResult WriteRejected(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} write was not applied: {NativeFailureDescription.Describe(exception)}");

    private static ControlResult CapabilityRefused(string name, Exception exception) => new(
        ControlResultStatus.Blocked,
        $"{name} is unavailable: {NativeFailureDescription.Describe(exception)}");

    private static ControlResult ReadFailure(string name, Exception exception) =>
        ReadFailure(name, NativeFailureDescription.Describe(exception));

    private static ControlResult ReadFailure(string name, string reason) => new(
        ControlResultStatus.Failed,
        $"{name} could not be read before writing: {reason}");

    private static ControlResult FinalReadbackChanged(string name) => new(
        ControlResultStatus.Failed,
        $"{name} changed before the final aggregate readback; no {name.ToLowerInvariant()} setter was attempted.");

    private static ControlResult SubsequentWritesBlocked(string reason) => new(
        ControlResultStatus.Blocked,
        $"No further hardware writes were attempted: {reason}");

    private static bool IsVerifiedThermalCombination(
        PerformancePreset performance,
        FanMode fans,
        DeviceCapabilities capabilities) =>
        (capabilities.FanSpeedRangesByPerformancePreset.Count > 0
            && fans == FanMode.Fixed
            && capabilities.GetFanSpeedRange(performance) is not null)
        || (capabilities.SupportsDirectManualFanTransitions
            && IsAutomaticPerformancePreset(performance)
            && DeviceSubsystemOrder.IsManualFanMode(fans)
            && (performance is not (PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced)
                || fans is FanMode.Fixed or FanMode.Maximum))
        || (performance, fans) is
        (PerformancePreset.Silent, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Automatic)
        or (PerformancePreset.Performance, FanMode.Automatic)
        or (PerformancePreset.Custom, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Maximum)
        or (PerformancePreset.Balanced, FanMode.Fixed)
        or (PerformancePreset.Balanced, FanMode.ManualCurve)
        or (PerformancePreset.BatterySaver, FanMode.Automatic)
        or (PerformancePreset.BatteryBalanced, FanMode.Automatic);

    private static string? PerformanceInitialStateBlockReason(
        ConfirmedDeviceState state,
        PerformancePreset requested,
        bool supportsDirectManualFanTransitions)
    {
        if (!supportsDirectManualFanTransitions)
        {
            return DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                ? "Performance changes require Automatic fan mode."
                : null;
        }

        if (state.Fans == FanMode.Automatic)
        {
            return null;
        }

        return DeviceSubsystemOrder.IsManualFanMode(state.Fans)
            && state.HyperBoostActive == false
            && IsAutomaticPerformancePreset(requested)
                ? null
                : "Performance changes require confirmed Automatic fans, or an admitted direct transition from confirmed manual fans.";
    }

    private static bool IsAutomaticPerformancePreset(
        PerformancePreset performance) => performance is
        PerformancePreset.Silent
        or PerformancePreset.Balanced
        or PerformancePreset.Performance
        or PerformancePreset.Custom
        or PerformancePreset.BatterySaver
        or PerformancePreset.BatteryBalanced;

    private static bool IsConfirmedManualFanWriteBaseline(
        ConfirmedDeviceState state) => state.Fans switch
        {
            FanMode.Automatic => state.Performance == PerformancePreset.Balanced,
            FanMode.Maximum or FanMode.Fixed or FanMode.ManualCurve =>
                state.Performance is null or PerformancePreset.Balanced,
            _ => false,
        };

    private static bool IsConfirmedDirectManualFanWriteBaseline(
        ConfirmedDeviceState state) => state.HyperBoostActive == false
        && (DeviceSubsystemOrder.IsManualFanMode(state.Fans)
            || state.Fans == FanMode.Automatic
                && state.Performance is { } performance
                && IsAutomaticPerformancePreset(performance));

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
        PerformancePresetMatches(state, performance, customPerformance)
        && GpuClockTuningMatches(state, gpuClockTuning);

    private static bool PerformancePresetMatches(
        ConfirmedDeviceState state,
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance) =>
        state.Performance == performance
        && (performance != PerformancePreset.Custom
            || state.CustomPerformance is { } confirmed
                && customPerformance is { } desired
                && confirmed.Cpu == desired.Cpu
                && confirmed.Gpu == desired.Gpu);

    private bool GpuClockTuningMatches(
        ConfirmedDeviceState state,
        GpuClockTuningSettings? gpuClockTuning) =>
        gpuClockTuning is null
            || (gpuClockTuning.Enabled
                ? state.GpuClockOffsets?.Current == gpuClockTuning.EffectiveOffsets
                : device is IGpuClockOffsetManagementDevice managedDevice
                    ? state.GpuClockOffsets is null
                        && managedDevice.GpuClockOffsetManagementState is
                            GpuClockOffsetManagementState.ResetConfirmed
                            or GpuClockOffsetManagementState
                                .RelinquishedDefaultConfirmed
                    : state.GpuClockOffsets?.Current ==
                        GpuClockOffsetSettings.Default);

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
        GpuClockTuningSettings? gpuClockTuning,
        bool firmwarePerformanceSelectorHidden) =>
        gpuClockTuning is not null
        && (firmwarePerformanceSelectorHidden
            || PerformancePresetMatches(state, performance, customPerformance));

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
        public HashSet<DeviceSubsystem> DeferredNotSubmitted { get; } = [];
        public HashSet<DeviceSubsystem> ContinuationExcluded { get; } = [];
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
}
