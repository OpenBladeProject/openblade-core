using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record ScopedApplyOutcome(
    ControlResult Result,
    ConfirmedDeviceState? Readback,
    bool WriteAttempted,
    bool ReadbackMatched,
    DeviceCapabilities? Capabilities,
    bool DeviceAccessFailed);

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

public sealed class ControlCoordinator
{
    private readonly IBladeDevice device;

    public ControlCoordinator(IBladeDevice device)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
    }

    // Kept for source compatibility with existing Service tests and callers. Controller
    // presence is advisory and the guard is intentionally never consulted.
    public ControlCoordinator(IBladeDevice device, IHardwareAccessGuard _)
        : this(device)
    {
    }

    public async Task<SubsystemApplyResults> ApplyDesiredStateAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken) =>
        (await ApplyDesiredStateOutcomeAsync(desired, cancellationToken)).Results;

    public async Task<ControlResult> ApplyPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) =>
        (await ApplyPerformanceOutcomeAsync(value, cancellationToken)).Result;

    public async Task<ControlResult> ApplyFanAsync(
        FanMode value,
        CancellationToken cancellationToken) =>
        (await ApplyFanOutcomeAsync(value, cancellationToken)).Result;

    public async Task<ControlResult> ApplyBatteryAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken) =>
        (await ApplyChargeLimitOutcomeAsync(value, cancellationToken)).Result;

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
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Performance,
            "Performance",
            IsDefined(value),
            capabilities => capabilities.PerformancePresets.Contains(value),
            state => state.Performance == value,
            token => device.SetPerformanceAsync(value, token),
            cancellationToken,
            state => state.Fans == FanMode.Maximum
                ? "Performance changes require Automatic fan mode."
                : null);

    internal Task<ScopedApplyOutcome> ApplyFanOutcomeAsync(
        FanMode value,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            IsDefined(value),
            capabilities => capabilities.FanModes.Contains(value),
            state => state.Fans == value,
            token => device.SetFanModeAsync(value, token),
            cancellationToken,
            value == FanMode.Maximum
                ? state => state.Performance == PerformancePreset.Balanced
                    && state.Fans == FanMode.Automatic
                        ? null
                        : "Maximum fan mode requires confirmed Balanced performance and Automatic fans."
                : null);

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
        bool forceWrite = false) => ApplyScopedAsync(
            DeviceSubsystem.Lighting,
            "Lighting",
            IsLightingValid(value),
            capabilities => capabilities.LightingEffects.Contains(value.Effect),
            state => state.Lighting == value,
            token => device.SetLightingAsync(value, token),
            cancellationToken,
            acceptSetterCompletionWithoutReadback: value.Effect != LightingEffect.Off,
            forceWrite: forceWrite);

    internal async Task<FullTransitionApplyOutcome> ApplyDesiredStateOutcomeAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken,
        IReadOnlySet<DeviceSubsystem>? automaticWriteExclusions = null,
        Func<ConfirmedDeviceState, IReadOnlySet<DeviceSubsystem>>?
            automaticWriteExclusionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var attempted = new HashSet<DeviceSubsystem>();
        var matched = new HashSet<DeviceSubsystem>();
        var contradicted = new HashSet<DeviceSubsystem>();
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
                attempted,
                matched,
                contradicted,
                null,
                DeviceAccessFailed: true);
        }

        BatteryOperationPlan batteryPlan = CreateBatteryPlan(
            desired.Battery,
            capabilities,
            initial.Battery);
        bool thermalCombinationSupported = IsVerifiedThermalCombination(
            desired.Performance.Value,
            desired.Fans.Value);
        var states = new Dictionary<DeviceSubsystem, PendingSubsystem>
        {
            [DeviceSubsystem.Performance] = new(
                "Performance",
                thermalCombinationSupported
                    && IsDefined(desired.Performance.Value)
                    && capabilities.PerformancePresets.Contains(desired.Performance.Value),
                initial.Performance == desired.Performance.Value,
                token => device.SetPerformanceAsync(desired.Performance.Value, token),
                state => state.Performance == desired.Performance.Value),
            [DeviceSubsystem.Fans] = new(
                "Fans",
                thermalCombinationSupported
                    && IsDefined(desired.Fans.Value)
                    && capabilities.FanModes.Contains(desired.Fans.Value),
                initial.Fans == desired.Fans.Value,
                token => device.SetFanModeAsync(desired.Fans.Value, token),
                state => state.Fans == desired.Fans.Value),
            [DeviceSubsystem.Battery] = new(
                batteryPlan.Name,
                batteryPlan.Supported,
                batteryPlan.InitiallyMatching,
                batteryPlan.Setter,
                state => batteryPlan.Matches(state.Battery)),
            [DeviceSubsystem.Lighting] = new(
                "Lighting",
                IsLightingValid(desired.Lighting.Value)
                    && capabilities.LightingEffects.Contains(desired.Lighting.Value.Effect),
                initial.Lighting == desired.Lighting.Value,
                token => device.SetLightingAsync(desired.Lighting.Value, token),
                state => state.Lighting == desired.Lighting.Value,
                AcceptSetterCompletionWithoutReadback:
                    desired.Lighting.Value.Effect != LightingEffect.Off),
        };
        var provisional = new Dictionary<DeviceSubsystem, ControlResult>();
        bool stopFurtherWrites = false;
        string? stopReason = null;
        bool stopThermalWrites = false;
        string? thermalStopReason = null;

        try
        {
            IReadOnlyList<DeviceSubsystem> orderedSubsystems =
                initial.Fans == FanMode.Maximum
                    && desired.Fans.Value == FanMode.Automatic
                    ? FansFirstOrderedSubsystems
                    : OrderedSubsystems;
            foreach (DeviceSubsystem subsystem in orderedSubsystems)
            {
                PendingSubsystem pending = states[subsystem];
                if (automaticWriteExclusions?.Contains(subsystem) == true)
                {
                    provisional[subsystem] = ExternalChangePreserved(pending.Name);
                    continue;
                }

                if (!pending.Supported)
                {
                    provisional[subsystem] = Unsupported(pending.Name);
                    if (IsThermalSubsystem(subsystem) && !pending.InitiallyMatching)
                    {
                        stopThermalWrites = true;
                        thermalStopReason = provisional[subsystem].Message;
                    }
                    continue;
                }

                if (pending.InitiallyMatching)
                {
                    provisional[subsystem] = Unchanged(pending.Name);
                    matched.Add(subsystem);
                    continue;
                }

                if (stopFurtherWrites)
                {
                    provisional[subsystem] = SubsequentWritesBlocked(stopReason!);
                    continue;
                }

                if (stopThermalWrites && IsThermalSubsystem(subsystem))
                {
                    provisional[subsystem] = SubsequentWritesBlocked(thermalStopReason!);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                attempted.Add(subsystem);
                try
                {
                    await pending.Setter(cancellationToken);
                    provisional[subsystem] = Indeterminate(pending.Name, null);
                }
                catch (BladeWriteException exception)
                    when (exception.SubmissionState is WriteSubmissionState.KnownNotApplied)
                {
                    attempted.Remove(subsystem);
                    provisional[subsystem] = WriteRejected(pending.Name, exception);
                    if (IsThermalSubsystem(subsystem))
                    {
                        stopThermalWrites = true;
                        thermalStopReason = provisional[subsystem].Message;
                    }
                }
                catch (BladeWriteException exception)
                    when (exception.SubmissionState is WriteSubmissionState.Indeterminate)
                {
                    provisional[subsystem] = Indeterminate(pending.Name, exception);
                    stopFurtherWrites = true;
                    stopReason = provisional[subsystem].Message;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    provisional[subsystem] = Indeterminate(pending.Name, exception);
                    if (IsThermalSubsystem(subsystem))
                    {
                        stopThermalWrites = true;
                        thermalStopReason = provisional[subsystem].Message;
                    }
                    if (IsTerminalDeviceFailure(exception))
                    {
                        stopFurtherWrites = true;
                        stopReason = provisional[subsystem].Message;
                    }
                }
            }

            if (attempted.Count == 0)
            {
                return CreateFullOutcome(
                    provisional,
                    initial,
                    attempted,
                    matched,
                    contradicted,
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
                foreach (DeviceSubsystem subsystem in attempted)
                {
                    provisional[subsystem] = Indeterminate(states[subsystem].Name, exception);
                }

                matched.Clear();

                return CreateFullOutcome(
                    provisional,
                    null,
                    attempted,
                    matched,
                    contradicted,
                    capabilities,
                    DeviceAccessFailed: true);
            }

            matched.Clear();
            foreach (DeviceSubsystem subsystem in OrderedSubsystems)
            {
                PendingSubsystem pending = states[subsystem];
                if (attempted.Contains(subsystem))
                {
                    if (pending.Matches(final))
                    {
                        provisional[subsystem] = Applied(pending.Name);
                        matched.Add(subsystem);
                    }
                    else
                    {
                        provisional[subsystem] =
                            pending.AcceptSetterCompletionWithoutReadback
                                ? AppliedWithoutEffectReadback(pending.Name)
                                : Indeterminate(pending.Name, null);
                    }
                }
                else if (provisional[subsystem].Status == ControlResultStatus.Unchanged)
                {
                    if (pending.Matches(final))
                    {
                        matched.Add(subsystem);
                    }
                    else
                    {
                        provisional[subsystem] = FinalReadbackChanged(pending.Name);
                        if (subsystem == DeviceSubsystem.Battery
                            && !desired.Battery.TemporaryFullCharge
                            && final.Battery is { ProtectionEnabled: false })
                        {
                            contradicted.Add(DeviceSubsystem.Battery);
                        }
                    }
                }
            }

            return CreateFullOutcome(
                provisional,
                final,
                attempted,
                matched,
                contradicted,
                capabilities,
                DeviceAccessFailed: stopFurtherWrites);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && attempted.Count > 0)
        {
            throw new IntentionalWriteCanceledException(
                new HashSet<DeviceSubsystem>(attempted),
                cancellationToken,
                exception);
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
        bool forceWrite = false)
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

            initial = await device.ReadStateAsync(cancellationToken);
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
            writeAttempted = true;
            await setter(cancellationToken);
            setterCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedDeviceState final = await device.ReadStateAsync(cancellationToken);
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
                    ? AppliedWithoutEffectReadback(name)
                    : Indeterminate(name, null),
                final,
                WriteAttempted: true,
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
        catch (Exception exception)
        {
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

    private static ControlResult AppliedWithoutEffectReadback(string name) => new(
        ControlResultStatus.Applied,
        $"{name} applied. Brightness was verified; this laptop does not report the active effect.");

    private static ControlResult Indeterminate(string name, Exception? exception) => new(
        ControlResultStatus.Failed,
        exception is null
            ? $"{name} write is indeterminate because readback did not match."
            : $"{name} write is indeterminate: {exception.Message}");

    private static ControlResult WriteRejected(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} write was not applied: {exception.Message}");

    private static ControlResult ReadFailure(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} could not be read before writing: {exception.Message}");

    private static ControlResult FinalReadbackChanged(string name) => new(
        ControlResultStatus.Failed,
        $"{name} changed before the final aggregate readback; no {name.ToLowerInvariant()} setter was attempted.");

    private static ControlResult SubsequentWritesBlocked(string reason) => new(
        ControlResultStatus.Blocked,
        $"No further hardware writes were attempted: {reason}");

    private static IReadOnlyList<DeviceSubsystem> OrderedSubsystems { get; } =
    [
        DeviceSubsystem.Performance,
        DeviceSubsystem.Fans,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

    private static IReadOnlyList<DeviceSubsystem> FansFirstOrderedSubsystems { get; } =
    [
        DeviceSubsystem.Fans,
        DeviceSubsystem.Performance,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

    private static bool IsVerifiedThermalCombination(
        PerformancePreset performance,
        FanMode fans) => (performance, fans) is
        (PerformancePreset.Silent, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Automatic)
        or (PerformancePreset.Performance, FanMode.Automatic)
        or (PerformancePreset.Balanced, FanMode.Maximum)
        or (PerformancePreset.BatterySaver, FanMode.Automatic)
        or (PerformancePreset.BatteryBalanced, FanMode.Automatic);

    private static bool IsThermalSubsystem(DeviceSubsystem subsystem) =>
        subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans;

    private sealed record PendingSubsystem(
        string Name,
        bool Supported,
        bool InitiallyMatching,
        Func<CancellationToken, Task> Setter,
        Func<ConfirmedDeviceState, bool> Matches,
        bool AcceptSetterCompletionWithoutReadback = false);

    private sealed record BatteryOperationPlan(
        string Name,
        bool Supported,
        bool InitiallyMatching,
        Func<CancellationToken, Task> Setter,
        Func<ConfirmedBatteryState?, bool> Matches);
}
