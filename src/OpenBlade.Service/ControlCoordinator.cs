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
        (await ApplyBatteryOutcomeAsync(value, cancellationToken)).Result;

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
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyFanOutcomeAsync(
        FanMode value,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Fans,
            "Fans",
            IsDefined(value),
            capabilities => capabilities.FanModes.Contains(value),
            state => state.Fans == value,
            token => device.SetFanModeAsync(value, token),
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyBatteryOutcomeAsync(
        BatteryPolicy value,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Battery,
            "Battery",
            value is not null,
            capabilities => IsBatterySupported(value!, capabilities),
            state => BatteryMatches(value!, state.Battery),
            token => device.SetBatteryPolicyAsync(value!, token),
            cancellationToken);

    internal Task<ScopedApplyOutcome> ApplyLightingOutcomeAsync(
        LightingSettings value,
        CancellationToken cancellationToken) => ApplyScopedAsync(
            DeviceSubsystem.Lighting,
            "Lighting",
            IsLightingValid(value),
            capabilities => capabilities.LightingEffects.Contains(value.Effect),
            state => state.Lighting == value,
            token => device.SetLightingAsync(value, token),
            cancellationToken);

    internal async Task<FullTransitionApplyOutcome> ApplyDesiredStateOutcomeAsync(
        DesiredDeviceState desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var attempted = new HashSet<DeviceSubsystem>();
        var matched = new HashSet<DeviceSubsystem>();
        DeviceCapabilities capabilities;
        ConfirmedDeviceState initial;
        try
        {
            capabilities = await device.ReadCapabilitiesAsync(cancellationToken);
            initial = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
                null,
                DeviceAccessFailed: true);
        }

        var states = new Dictionary<DeviceSubsystem, PendingSubsystem>
        {
            [DeviceSubsystem.Performance] = new(
                "Performance",
                IsDefined(desired.Performance.Value)
                    && capabilities.PerformancePresets.Contains(desired.Performance.Value),
                initial.Performance == desired.Performance.Value,
                token => device.SetPerformanceAsync(desired.Performance.Value, token),
                state => state.Performance == desired.Performance.Value),
            [DeviceSubsystem.Fans] = new(
                "Fans",
                IsDefined(desired.Fans.Value)
                    && capabilities.FanModes.Contains(desired.Fans.Value),
                initial.Fans == desired.Fans.Value,
                token => device.SetFanModeAsync(desired.Fans.Value, token),
                state => state.Fans == desired.Fans.Value),
            [DeviceSubsystem.Battery] = new(
                "Battery",
                IsBatterySupported(desired.Battery, capabilities),
                BatteryMatches(desired.Battery, initial.Battery),
                token => device.SetBatteryPolicyAsync(desired.Battery, token),
                state => BatteryMatches(desired.Battery, state.Battery)),
            [DeviceSubsystem.Lighting] = new(
                "Lighting",
                IsLightingValid(desired.Lighting.Value)
                    && capabilities.LightingEffects.Contains(desired.Lighting.Value.Effect),
                initial.Lighting == desired.Lighting.Value,
                token => device.SetLightingAsync(desired.Lighting.Value, token),
                state => state.Lighting == desired.Lighting.Value),
        };
        var provisional = new Dictionary<DeviceSubsystem, ControlResult>();
        bool stopFurtherWrites = false;
        string? stopReason = null;

        try
        {
            foreach (DeviceSubsystem subsystem in OrderedSubsystems)
            {
                PendingSubsystem pending = states[subsystem];
                if (!pending.Supported)
                {
                    provisional[subsystem] = Unsupported(pending.Name);
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

                cancellationToken.ThrowIfCancellationRequested();
                attempted.Add(subsystem);
                try
                {
                    await pending.Setter(cancellationToken);
                    provisional[subsystem] = Indeterminate(pending.Name, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    provisional[subsystem] = Indeterminate(pending.Name, exception);
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

                return CreateFullOutcome(
                    provisional,
                    null,
                    attempted,
                    matched,
                    capabilities,
                    DeviceAccessFailed: true);
            }

            foreach (DeviceSubsystem subsystem in attempted)
            {
                PendingSubsystem pending = states[subsystem];
                if (pending.Matches(final))
                {
                    provisional[subsystem] = Applied(pending.Name);
                    matched.Add(subsystem);
                }
                else
                {
                    provisional[subsystem] = Indeterminate(pending.Name, null);
                }
            }

            return CreateFullOutcome(
                provisional,
                final,
                attempted,
                matched,
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

    private async Task<ScopedApplyOutcome> ApplyScopedAsync(
        DeviceSubsystem subsystem,
        string name,
        bool valid,
        Func<DeviceCapabilities, bool> supported,
        Func<ConfirmedDeviceState, bool> matches,
        Func<CancellationToken, Task> setter,
        CancellationToken cancellationToken)
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

            ConfirmedDeviceState initial = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (matches(initial))
            {
                return new ScopedApplyOutcome(
                    Unchanged(name),
                    initial,
                    WriteAttempted: false,
                    ReadbackMatched: true,
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
                Indeterminate(name, null),
                null,
                WriteAttempted: true,
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
            capabilities,
            DeviceAccessFailed);

    private static bool IsBatterySupported(
        BatteryPolicy value,
        DeviceCapabilities capabilities) => value is not null
        && capabilities.ChargeLimitStep > 0
        && value.ChargeLimitPercent >= capabilities.MinimumChargeLimit
        && value.ChargeLimitPercent <= capabilities.MaximumChargeLimit
        && (value.ChargeLimitPercent - capabilities.MinimumChargeLimit)
            % capabilities.ChargeLimitStep == 0;

    private static bool BatteryMatches(BatteryPolicy desired, BatteryPolicy? confirmed) =>
        desired.TemporaryFullCharge ? confirmed is null : confirmed == desired;

    private static bool IsLightingValid(LightingSettings value)
    {
        if (value is null || !IsDefined(value.Effect) || value.Brightness > 100)
        {
            return false;
        }

        if (value.Effect == LightingEffect.Off && value.Brightness != 0)
        {
            return false;
        }

        bool consumesColor = value.Effect is LightingEffect.Static
            or LightingEffect.Breathing
            or LightingEffect.Reactive;
        return consumesColor || value.Color == default;
    }

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

    private static ControlResult Applied(string name) => new(
        ControlResultStatus.Applied,
        $"{name} setting applied and confirmed by readback.");

    private static ControlResult Indeterminate(string name, Exception? exception) => new(
        ControlResultStatus.Failed,
        exception is null
            ? $"{name} write is indeterminate because readback did not match."
            : $"{name} write is indeterminate: {exception.Message}");

    private static ControlResult ReadFailure(string name, Exception exception) => new(
        ControlResultStatus.Failed,
        $"{name} could not be read before writing: {exception.Message}");

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

    private sealed record PendingSubsystem(
        string Name,
        bool Supported,
        bool InitiallyMatching,
        Func<CancellationToken, Task> Setter,
        Func<ConfirmedDeviceState, bool> Matches);
}
