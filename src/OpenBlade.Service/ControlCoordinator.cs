using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record ScopedApplyOutcome(
    ControlResult Result,
    ConfirmedDeviceState? Readback,
    bool WriteAttempted,
    bool ReadbackMatched,
    DeviceCapabilities? Capabilities,
    bool DeviceAccessFailed,
    IReadOnlySet<DeviceSubsystem>? ConfirmedCoupledWrites = null);

internal sealed record FullTransitionApplyOutcome(
    SubsystemApplyResults Results,
    ConfirmedDeviceState? Readback,
    IReadOnlySet<DeviceSubsystem> WriteAttempted,
    IReadOnlySet<DeviceSubsystem> ReadbackMatched,
    IReadOnlySet<DeviceSubsystem> ReadbackContradicted,
    DeviceCapabilities? Capabilities,
    bool DeviceAccessFailed);

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
    public WriteAuthorityLostException()
        : base("The active Windows session changed before the recovery write.")
    {
    }
}

public sealed class ControlCoordinator
{
    private readonly IBladeDevice device;

    public ControlCoordinator(IBladeDevice device)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public async Task<SubsystemApplyResults> ApplyDesiredStateAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken) =>
        (await ApplyDesiredStateOutcomeAsync(desired, cancellationToken)).Results;

    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) =>
        (await ApplyPerformanceOutcomeAsync(
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

    internal Task<ScopedApplyOutcome> ApplyPerformanceOutcomeAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) => ApplyPerformanceOutcomeAsync(
            value,
            value == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyPerformanceOutcomeAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Performance,
            "Performance",
            IsDefined(value)
                && (value == PerformancePreset.Custom
                    ? CustomPerformanceSettings.IsWellFormed(customPerformance)
                    : customPerformance is null),
            capabilities => capabilities.PerformancePresets.Contains(value)
                && (value != PerformancePreset.Custom
                    || capabilities.CustomPerformance?.Supports(customPerformance) != false),
            state => PerformanceMatches(state, value, customPerformance),
            token => device.SetPerformanceAsync(value, customPerformance, token),
            cancellationToken,
            state => DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                ? "Performance changes require Automatic fan mode."
                : null,
            stateAwareSetter: (state, token) =>
                CanApplyGpuClockOffsetsOnly(state, value, customPerformance)
                    ? device.SetGpuClockOffsetsAsync(
                        customPerformance!.GpuClockOffsets!,
                        token)
                    : device.SetPerformanceAsync(value, customPerformance, token));

    internal Task<ScopedApplyOutcome> ApplyFanOutcomeAsync(
        FanMode value,
        CancellationToken cancellationToken) => ApplyFanControlOutcomeAsync(
            FanControlSettings.FromLegacy(value),
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyFanControlOutcomeAsync(
        FanControlSettings value,
        CancellationToken cancellationToken,
        Func<bool>? writeAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            IsDefined(value.Mode),
            capabilities => capabilities.FanModes.Contains(value.Mode)
                && (value.Mode != FanMode.Fixed
                    || capabilities.FanSpeedRange?.Supports(value.FixedSpeedRpm) == true),
            state => FanModeMatches(state.Fans, value.Mode),
            token => device.SetFanControlAsync(value, token),
            cancellationToken,
            DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                ? state => IsConfirmedManualFanWriteBaseline(state)
                        ? null
                        : "Fixed and manual fan control require confirmed Balanced Automatic fans to enter manual mode, or an already-confirmed manual fan state."
                : null,
            writeAllowed: writeAllowed,
            acceptSetterCompletionWithoutReadback:
                DeviceSubsystemOrder.IsManualFanMode(value.Mode),
            setterCompletionEvidence: DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                ? state => DeviceSubsystemOrder.IsManualFanMode(state.Fans)
                : null,
            // Firmware exposes only Automatic versus manual state. It cannot
            // read back a fixed target or distinguish Fixed, Curve and Maximum,
            // so every explicit manual-mode action must submit its RPM target.
            forceWrite: DeviceSubsystemOrder.IsManualFanMode(value.Mode));
    }

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
            automaticBridge = await ApplyFanControlOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Automatic),
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
            balancedBridge = await ApplyPerformanceOutcomeAsync(
                PerformancePreset.Balanced,
                customPerformance: null,
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

        ScopedApplyOutcome fans = await ApplyFanControlOutcomeAsync(
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
                ScopedApplyOutcome restored = await ApplyPerformanceOutcomeAsync(
                    performanceIntent,
                    customPerformanceIntent,
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

        ScopedApplyOutcome performance = await ApplyPerformanceOutcomeAsync(
            performanceIntent,
            customPerformanceIntent,
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
            automaticWriteExclusionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var transition = new FullTransitionApplyState();
        DeviceCapabilities capabilities;
        ConfirmedDeviceState initial;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            initial = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (automaticWriteExclusionResolver is not null)
            {
                automaticWriteExclusions = automaticWriteExclusionResolver(initial);
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
                initial,
                states,
                automaticWriteExclusions,
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
                    DeviceAccessFailed: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final;
            try
            {
                final = await device.ReadStateAsync(cancellationToken);
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

                return CreateFullOutcome(
                    transition.Provisional,
                    null,
                    transition.Attempted,
                    transition.Matched,
                    transition.Contradicted,
                    capabilities,
                    DeviceAccessFailed: true);
            }

            ReconcileFinalReadback(desired, states, final, transition);

            return CreateFullOutcome(
                transition.Provisional,
                final,
                transition.Attempted,
                transition.Matched,
                transition.Contradicted,
                capabilities,
                transition.DeviceAccessFailed);
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
                desired.CustomPerformance);
        return new Dictionary<DeviceSubsystem, PendingSubsystem>
        {
            [DeviceSubsystem.Performance] = new(
                "Performance",
                thermalCombinationSupported
                    && IsDefined(desired.Performance.Value)
                    && capabilities.PerformancePresets.Contains(desired.Performance.Value),
                PerformanceStateMatches(initial),
                token => device.SetPerformanceAsync(
                    desired.Performance.Value,
                    desired.CustomPerformance,
                    token),
                PerformanceStateMatches)
            {
                StateAwareSetter = (state, token) =>
                    CanApplyGpuClockOffsetsOnly(
                        state,
                        desired.Performance.Value,
                        desired.CustomPerformance)
                        ? device.SetGpuClockOffsetsAsync(
                            desired.CustomPerformance!.GpuClockOffsets!,
                            token)
                        : device.SetPerformanceAsync(
                            desired.Performance.Value,
                            desired.CustomPerformance,
                            token),
            },
            [DeviceSubsystem.Fans] = new(
                "Fans",
                thermalCombinationSupported
                    && IsDefined(desired.Fans.Value)
                    && capabilities.FanModes.Contains(desired.Fans.Value),
                FanModeMatches(initial.Fans, desired.Fans.Value),
                token => device.SetFanControlAsync(
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
                batteryPlan.Setter,
                state => batteryPlan.Matches(state.Battery)),
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
        ConfirmedDeviceState initial,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> states,
        IReadOnlySet<DeviceSubsystem>? automaticWriteExclusions,
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
                transition.Provisional[subsystem] = ExternalChangePreserved(pending.Name);
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
            transition.Attempted.Add(subsystem);
            try
            {
                await (pending.StateAwareSetter is null
                    ? pending.Setter(cancellationToken)
                    : pending.StateAwareSetter(initial, cancellationToken));
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
                }
            }
        }

        transition.DeviceAccessFailed = stopFurtherWrites;
    }

    private static void ReconcileFinalReadback(
        DesiredDeviceState desired,
        IReadOnlyDictionary<DeviceSubsystem, PendingSubsystem> states,
        ConfirmedDeviceState final,
        FullTransitionApplyState transition)
    {
        transition.Matched.Clear();
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
        {
            PendingSubsystem pending = states[subsystem];
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
                        pending.AcceptSetterCompletionWithoutReadback
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

    private async Task<ScopedApplyOutcome> ApplyBatteryOperationAsync(
        Func<DeviceCapabilities, ConfirmedBatteryState?, BatteryOperationPlan> createPlan,
        CancellationToken cancellationToken)
    {
        DeviceCapabilities? capabilities = null;
        bool submissionIndeterminate = false;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState initial = await device.ReadStateAsync(cancellationToken);
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
                return new ScopedApplyOutcome(
                    WriteRejected(plan.Name, exception),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: false,
                    capabilities,
                    DeviceAccessFailed: false);
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
                DeviceAccessFailed: true);
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
        Func<bool>? writeAllowed = null)
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
        DeviceCapabilities? capabilities = null;
        ConfirmedDeviceState? initial = null;
        Func<CancellationToken, Task<ConfirmedDeviceState>> readState =
            stateReader ?? device.ReadStateAsync;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
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

            initial = await readState(cancellationToken);
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
            return new ScopedApplyOutcome(
                WriteRejected(name, exception),
                initial,
                WriteAttempted: false,
                ReadbackMatched: false,
                capabilities,
                DeviceAccessFailed: false);
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
        catch (WriteAuthorityLostException)
        {
            throw;
        }
        catch (Exception exception)
        {
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
                    || IsTerminalDeviceFailure(exception));
        }
    }

    private static FullTransitionApplyOutcome CreateFullOutcome(
        IReadOnlyDictionary<DeviceSubsystem, ControlResult> results,
        ConfirmedDeviceState? readback,
        IReadOnlySet<DeviceSubsystem> attempted,
        IReadOnlySet<DeviceSubsystem> matched,
        IReadOnlySet<DeviceSubsystem> contradicted,
        DeviceCapabilities capabilities,
        bool DeviceAccessFailed) => new(
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
            DeviceAccessFailed);

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

    private static bool PerformanceMatches(
        ConfirmedDeviceState state,
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance) =>
        state.Performance == performance
        && (performance != PerformancePreset.Custom
            || state.CustomPerformance is { } confirmed
                && customPerformance is { } desired
                && confirmed.Cpu == desired.Cpu
                && confirmed.Gpu == desired.Gpu
                && (desired.GpuClockOffsets is null
                    || state.GpuClockOffsets?.Current == desired.GpuClockOffsets));

    private static bool CanApplyGpuClockOffsetsOnly(
        ConfirmedDeviceState state,
        PerformancePreset performance,
        CustomPerformanceSettings? customPerformance) =>
        performance == PerformancePreset.Custom
        && customPerformance?.GpuClockOffsets is not null
        && state.Performance == PerformancePreset.Custom
        && state.CustomPerformance is { } confirmed
        && confirmed.Cpu == customPerformance.Cpu
        && confirmed.Gpu == customPerformance.Gpu;

    private static bool IsThermalSubsystem(DeviceSubsystem subsystem) =>
        subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans;

    private sealed class FullTransitionApplyState
    {
        public Dictionary<DeviceSubsystem, ControlResult> Provisional { get; } = [];
        public HashSet<DeviceSubsystem> Attempted { get; } = [];
        public HashSet<DeviceSubsystem> Matched { get; } = [];
        public HashSet<DeviceSubsystem> Contradicted { get; } = [];
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
