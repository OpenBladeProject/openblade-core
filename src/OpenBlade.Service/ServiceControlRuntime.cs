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

    Task RefreshConflictsAsync(CancellationToken token);

    Task ObserveAsync(CancellationToken cancellationToken);

    Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken);

    Task RefreshControllerPresenceAsync(CancellationToken cancellationToken);
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
    private readonly IControllerPresenceMonitor controllerPresenceMonitor;
    private readonly DiagnosticHistory history;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ProfileSetValidator validator =
        ProfileSetValidator.CreateUnverifiedSafeDefaults();
    private readonly ProfileAutomationState automation;
    private readonly ConfirmedStateDriftTracker driftTracker = new();
    private ServiceStatusSnapshot status;

    public ServiceControlRuntime(
        ProfileStore store,
        ProfileSet profiles,
        IHardwareAccessGuard hardwareAccessGuard,
        IPowerTelemetryProvider telemetryProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor)
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
        this.controllerPresenceMonitor = controllerPresenceMonitor
            ?? throw new ArgumentNullException(nameof(controllerPresenceMonitor));

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

    public Task RefreshAsync(CancellationToken token) => ObserveAsync(token);

    public Task RefreshConflictsAsync(CancellationToken token) =>
        RefreshControllerPresenceAsync(token);

    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TelemetrySnapshot telemetry = telemetryProvider.Capture();
            await ObserveCoreAsync(telemetry, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            TelemetrySnapshot telemetry = telemetryProvider.Capture();
            cancellationToken.ThrowIfCancellationRequested();
            if (automation.ChangePowerSource(telemetry.PowerSource))
            {
                await ReconcileAsync(telemetry, intentStored: false, cancellationToken);
            }
            else
            {
                await ObserveCoreAsync(telemetry, cancellationToken);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RefreshControllerPresenceAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControllerPresenceObservation observation = controllerPresenceMonitor.Observe();
            cancellationToken.ThrowIfCancellationRequested();
            string[] potentialControllers = observation.PotentialControllers
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(name => name, StringComparer.Ordinal).First())
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(16)
                .ToArray();
            PublishIfChanged(GetStatus() with
            {
                PotentialControllers = potentialControllers,
                ControllerDetectionWarning = observation.DetectionWarning,
            });
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

        bool deviceAvailable = deviceProvider.TryGetVerifiedDevice(out IBladeDevice device);
        token.ThrowIfCancellationRequested();
        if (!deviceAvailable)
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
            confirmed = await device.ReadStateAsync(token);
            token.ThrowIfCancellationRequested();
            driftTracker.Observe(confirmed);
            ConfirmIntentionalResults(desired, confirmed, results);
            history.RecordSubsystemResults(results);

            bool writesBlocked = ResultsContainBlock(results);
            string message = Summarize(results);
            ServiceStatusSnapshot next = new(
                GetStatus().Revision,
                DeviceVerified: true,
                WritesBlocked: writesBlocked,
                PotentialControllers: access.Conflicts
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
                ControllerDetectionWarning: access.DetectionFailure,
                ExternalChanges: driftTracker.ExternalChanges,
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

    private async Task ObserveCoreAsync(
        TelemetrySnapshot telemetry,
        CancellationToken cancellationToken)
    {
        bool deviceAvailable = deviceProvider.TryGetVerifiedDevice(out IBladeDevice device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!deviceAvailable)
        {
            PublishObservationUnavailable(
                telemetry,
                "No verified PID 02E0 device is available.");
            return;
        }

        try
        {
            DeviceCapabilities capabilities =
                await device.ReadCapabilitiesAsync(cancellationToken);
            ConfirmedDeviceState confirmed = await device.ReadStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            driftTracker.Observe(confirmed);
            PublishIfChanged(GetStatus() with
            {
                DeviceVerified = true,
                WritesBlocked = false,
                ExternalChanges = driftTracker.ExternalChanges,
                Message = "Verified device state observed.",
                Capabilities = capabilities,
                ConfirmedState = confirmed,
                Profiles = automation.Profiles,
                DesiredState = automation.Resolve(confirmed),
                Telemetry = telemetry,
                PerformanceOverride = automation.PerformanceOverride,
                FanOverride = automation.FanOverride,
                LightingOverride = automation.LightingOverride,
                FullChargeOverride = automation.BatteryPolicy.TemporaryFullCharge,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishObservationUnavailable(
                telemetry,
                $"Verified device access failed: {exception.Message}");
            history.RecordSummary(
                ControlOperationStatus.Failed,
                GetStatus().Message);
        }
    }

    private void PublishObservationUnavailable(TelemetrySnapshot telemetry, string message)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState confirmed = current.ConfirmedState ?? UnknownConfirmedState;
        PublishIfChanged(current with
        {
            DeviceVerified = false,
            WritesBlocked = true,
            ExternalChanges = driftTracker.ExternalChanges,
            Message = message,
            Profiles = automation.Profiles,
            DesiredState = automation.Resolve(confirmed),
            Telemetry = telemetry,
            PerformanceOverride = automation.PerformanceOverride,
            FanOverride = automation.FanOverride,
            LightingOverride = automation.LightingOverride,
            FullChargeOverride = automation.BatteryPolicy.TemporaryFullCharge,
        });
    }

    private void ConfirmIntentionalResults(
        DesiredDeviceState desired,
        ConfirmedDeviceState confirmed,
        SubsystemApplyResults results)
    {
        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Performance,
            IsSuccessful(results.Performance) && confirmed.Performance == desired.Performance.Value);
        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Fans,
            IsSuccessful(results.Fans) && confirmed.Fans == desired.Fans.Value);
        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Battery,
            IsSuccessful(results.Battery) && confirmed.Battery == desired.Battery);
        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Lighting,
            IsSuccessful(results.Lighting) && confirmed.Lighting == desired.Lighting.Value);
    }

    private static bool IsSuccessful(ControlResult result) =>
        result.Status is ControlResultStatus.Applied or ControlResultStatus.Unchanged;

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
            PotentialControllers: conflicts,
            ControllerDetectionWarning: access.DetectionFailure,
            ExternalChanges: [],
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
        && left.PotentialControllers.SequenceEqual(
            right.PotentialControllers,
            StringComparer.Ordinal)
        && string.Equals(
            left.ControllerDetectionWarning,
            right.ControllerDetectionWarning,
            StringComparison.Ordinal)
        && left.ExternalChanges.SequenceEqual(right.ExternalChanges)
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
