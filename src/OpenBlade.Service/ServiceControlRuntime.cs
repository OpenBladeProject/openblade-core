using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IServiceControlRuntime
{
    ServiceStatusSnapshot GetStatus();

    Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token);

    Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token);

    Task RefreshAsync(CancellationToken token);
}

public sealed class ServiceControlRuntime : IServiceControlRuntime, IDisposable
{
    private static readonly ConfirmedDeviceState UnknownConfirmedState =
        new(null, null, null, null);
    private static readonly DeviceCapabilities UnavailableCapabilities =
        DeviceCapabilities.Unavailable();

    private readonly ProfileStore store;
    private readonly IHardwareAccessGuard hardwareAccessGuard;
    private readonly IPowerTelemetryProvider telemetryProvider;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly IVerifiedBladeDeviceProvider deviceProvider;
    private readonly DiagnosticHistory history;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ProfileSetValidator validator =
        ProfileSetValidator.CreateUnverifiedSafeDefaults();
    private readonly ProfileAutomationState automation;
    private ServiceStatusSnapshot status;

    public ServiceControlRuntime(
        ProfileStore store,
        ProfileSet profiles,
        IHardwareAccessGuard hardwareAccessGuard,
        IPowerTelemetryProvider telemetryProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(profiles);
        this.hardwareAccessGuard = hardwareAccessGuard
            ?? throw new ArgumentNullException(nameof(hardwareAccessGuard));
        this.telemetryProvider = telemetryProvider
            ?? throw new ArgumentNullException(nameof(telemetryProvider));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.deviceProvider = deviceProvider
            ?? throw new ArgumentNullException(nameof(deviceProvider));
        this.history = history ?? throw new ArgumentNullException(nameof(history));

        TelemetrySnapshot telemetry = telemetryProvider.Capture();
        automation = new ProfileAutomationState(profiles, telemetry.PowerSource);
        HardwareAccessDecision access = hardwareAccessGuard.Check();
        status = CreateBlockedSnapshot(0, telemetry, access);
    }

    public ServiceStatusSnapshot GetStatus() => Volatile.Read(ref status);

    public Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token) => ExecuteMutationAsync(
            async cancellationToken =>
            {
                validator.Validate(profiles);
                await store.SaveAsync(profiles, cancellationToken);
                automation.UpdateProfiles(profiles);
            },
            token);

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) => ExecuteMutationAsync(
            _ =>
            {
                ValidateOverride(value, nameof(value));
                automation.SetPerformanceOverride(value);
                return Task.CompletedTask;
            },
            token);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => ExecuteMutationAsync(
            _ =>
            {
                ValidateOverride(value, nameof(value));
                automation.SetFanOverride(value);
                return Task.CompletedTask;
            },
            token);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => ExecuteMutationAsync(
            _ =>
            {
                ValidateLightingOverride(value);
                automation.SetLightingOverride(value);
                return Task.CompletedTask;
            },
            token);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token) => ExecuteMutationAsync(
            async cancellationToken =>
            {
                ArgumentNullException.ThrowIfNull(value);
                ProfileSet updated = automation.Profiles with { BatteryPolicy = value };
                validator.Validate(updated);
                await store.SaveAsync(updated, cancellationToken);
                automation.SetBatteryPolicy(value);
            },
            token);

    public async Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token)
    {
        await operationGate.WaitAsync(token);
        try
        {
            TelemetrySnapshot telemetry = telemetryProvider.Capture();
            automation.ChangePowerSource(telemetry.PowerSource);

            if (enabled && telemetry.PowerSource == PowerSource.Battery)
            {
                await ReconcileAsync(telemetry, intentStored: false, token);
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The requested intent was rejected: Temporary full charge can only " +
                    "be enabled while plugged in.");
                history.RecordSummary(rejected.Status, rejected.Message);
                return rejected;
            }

            if (enabled)
            {
                automation.EnableTemporaryFullCharge();
            }
            else
            {
                automation.CancelTemporaryFullCharge();
            }

            return await ReconcileAsync(telemetry, intentStored: true, token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        await operationGate.WaitAsync(token);
        try
        {
            TelemetrySnapshot telemetry = telemetryProvider.Capture();
            automation.ChangePowerSource(telemetry.PowerSource);
            await ReconcileAsync(telemetry, intentStored: false, token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose() => operationGate.Dispose();

    private async Task<ControlOperationResult> ExecuteMutationAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken token)
    {
        await operationGate.WaitAsync(token);
        try
        {
            try
            {
                await mutation(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidDataException or IOException)
            {
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    $"The requested intent was rejected: {exception.Message}");
                history.RecordSummary(rejected.Status, rejected.Message);
                return rejected;
            }

            return await ReconcileAsync(GetStatus().Telemetry, intentStored: true, token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ReconcileAsync(
        TelemetrySnapshot telemetry,
        bool intentStored,
        CancellationToken token)
    {
        // This guard deliberately precedes even asking for a verified device.
        HardwareAccessDecision access = hardwareAccessGuard.Check();
        if (!access.Safe)
        {
            ServiceStatusSnapshot blocked = CreateBlockedSnapshot(
                GetStatus().Revision,
                telemetry,
                access);
            PublishIfChanged(blocked);
            history.RecordSummary(ControlOperationStatus.Blocked, blocked.Message);
            return BlockedResult(blocked.Message, intentStored);
        }

        access = hardwareAccessGuard.Check();
        if (!access.Safe)
        {
            ServiceStatusSnapshot blocked = CreateBlockedSnapshot(
                GetStatus().Revision,
                telemetry,
                access);
            PublishIfChanged(blocked);
            history.RecordSummary(ControlOperationStatus.Blocked, blocked.Message);
            return BlockedResult(blocked.Message, intentStored);
        }

        if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
        {
            ServiceStatusSnapshot unavailable = CreateBlockedSnapshot(
                GetStatus().Revision,
                telemetry,
                access);
            PublishIfChanged(unavailable);
            history.RecordSummary(ControlOperationStatus.Blocked, unavailable.Message);
            return BlockedResult(unavailable.Message, intentStored);
        }

        try
        {
            DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(token);
            ConfirmedDeviceState confirmed = await device.ReadStateAsync(token);
            DesiredDeviceState desired = automation.Resolve(confirmed);
            var coordinator = new ControlCoordinator(device, hardwareAccessGuard);
            SubsystemApplyResults results =
                await coordinator.ApplyDesiredStateAsync(desired, token);
            history.RecordSubsystemResults(results);
            confirmed = await device.ReadStateAsync(token);

            bool writesBlocked = ResultsContainBlock(results);
            string message = Summarize(results);
            ServiceStatusSnapshot next = new(
                GetStatus().Revision,
                DeviceVerified: true,
                WritesBlocked: writesBlocked,
                Conflicts: [],
                Message: message,
                Capabilities: capabilities,
                ConfirmedState: confirmed,
                Profiles: automation.Profiles,
                DesiredState: automation.Resolve(confirmed),
                Telemetry: telemetry,
                PerformanceOverride: automation.PerformanceOverride,
                FanOverride: automation.FanOverride,
                LightingOverride: automation.LightingOverride,
                FullChargeOverride: automation.BatteryPolicy.TemporaryFullCharge);
            PublishIfChanged(next);
            if (ResultsContainFailure(results))
            {
                history.RecordSummary(ControlOperationStatus.Failed, message);
            }
            else if (writesBlocked)
            {
                history.RecordSummary(ControlOperationStatus.Blocked, message);
            }

            return new ControlOperationResult(
                ResultsContainFailure(results)
                    ? ControlOperationStatus.Failed
                    : writesBlocked
                        ? ControlOperationStatus.Blocked
                        : ControlOperationStatus.Applied,
                message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServiceStatusSnapshot failed = CreateBlockedSnapshot(
                GetStatus().Revision,
                telemetry,
                access,
                $"Verified device access failed: {exception.Message}");
            PublishIfChanged(failed);
            history.RecordSummary(ControlOperationStatus.Failed, failed.Message);
            return new ControlOperationResult(ControlOperationStatus.Failed, failed.Message);
        }
    }

    private ServiceStatusSnapshot CreateBlockedSnapshot(
        long revision,
        TelemetrySnapshot telemetry,
        HardwareAccessDecision access,
        string? failure = null)
    {
        string[] conflicts = access.Conflicts
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string message = failure ?? (access.DetectionFailure is not null
            ? conflicts.Length == 0
                ? $"Controller conflict detection failed: {access.DetectionFailure}"
                : $"Controller conflict detection failed: {access.DetectionFailure} " +
                    $"Detected conflicts: {string.Join(", ", conflicts)}."
            : conflicts.Length > 0
                ? $"Razer controller conflicts detected: {string.Join(", ", conflicts)}."
                : "No verified PID 02E0 device is available.");

        return new ServiceStatusSnapshot(
            revision,
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: conflicts,
            Message: message,
            Capabilities: UnavailableCapabilities,
            ConfirmedState: null,
            Profiles: automation.Profiles,
            DesiredState: automation.Resolve(UnknownConfirmedState),
            Telemetry: telemetry,
            PerformanceOverride: automation.PerformanceOverride,
            FanOverride: automation.FanOverride,
            LightingOverride: automation.LightingOverride,
            FullChargeOverride: automation.BatteryPolicy.TemporaryFullCharge);
    }

    private void PublishIfChanged(ServiceStatusSnapshot candidate)
    {
        ServiceStatusSnapshot previous = GetStatus();
        if (Equivalent(previous, candidate))
        {
            return;
        }

        ServiceStatusSnapshot published = candidate with { Revision = previous.Revision + 1 };
        Volatile.Write(ref status, published);
        subscriptionHub.Publish(published);
    }

    private static bool Equivalent(ServiceStatusSnapshot left, ServiceStatusSnapshot right) =>
        left.DeviceVerified == right.DeviceVerified
        && left.WritesBlocked == right.WritesBlocked
        && left.Conflicts.SequenceEqual(right.Conflicts, StringComparer.Ordinal)
        && left.Message == right.Message
        && CapabilitiesEqual(left.Capabilities, right.Capabilities)
        && left.ConfirmedState == right.ConfirmedState
        && left.Profiles == right.Profiles
        && left.DesiredState == right.DesiredState
        && left.Telemetry == right.Telemetry
        && left.PerformanceOverride == right.PerformanceOverride
        && left.FanOverride == right.FanOverride
        && left.LightingOverride == right.LightingOverride
        && left.FullChargeOverride == right.FullChargeOverride;

    private static bool CapabilitiesEqual(
        DeviceCapabilities? left,
        DeviceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.PerformancePresets.SetEquals(right.PerformancePresets)
            && left.FanModes.SetEquals(right.FanModes)
            && left.LightingEffects.SetEquals(right.LightingEffects)
            && left.MinimumChargeLimit == right.MinimumChargeLimit
            && left.MaximumChargeLimit == right.MaximumChargeLimit
            && left.ChargeLimitStep == right.ChargeLimitStep);

    private static ControlOperationResult BlockedResult(string reason, bool intentStored) => new(
        ControlOperationStatus.Blocked,
        intentStored
            ? $"Intent was stored without hardware application. {reason}"
            : reason);

    private static bool ResultsContainBlock(SubsystemApplyResults results) =>
        results.Performance.Status == ControlResultStatus.Blocked
        || results.Fans.Status == ControlResultStatus.Blocked
        || results.Battery.Status == ControlResultStatus.Blocked
        || results.Lighting.Status == ControlResultStatus.Blocked;

    private static bool ResultsContainFailure(SubsystemApplyResults results) =>
        results.Performance.Status == ControlResultStatus.Failed
        || results.Fans.Status == ControlResultStatus.Failed
        || results.Battery.Status == ControlResultStatus.Failed
        || results.Lighting.Status == ControlResultStatus.Failed;

    private static string Summarize(SubsystemApplyResults results) =>
        string.Join(
            " ",
            results.Performance.Message,
            results.Fans.Message,
            results.Battery.Message,
            results.Lighting.Message);

    private static void ValidateOverride<T>(ManualOverride<T>? value, string parameterName)
        where T : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateLightingOverride(ManualOverride<LightingSettings>? value)
    {
        if (value is null)
        {
            return;
        }

        LightingSettings lighting = value.Value;
        if (!Enum.IsDefined(lighting.Effect) || lighting.Brightness > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
