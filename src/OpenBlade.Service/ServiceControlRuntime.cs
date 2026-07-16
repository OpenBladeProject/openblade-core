using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IServiceControlRuntime
{
    void Initialize(BatteryConfiguration configuration, PowerSource powerSource) =>
        throw new NotSupportedException();

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

    Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token) => throw new NotSupportedException();

    Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token);

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

    private readonly BatteryConfigurationStore store;
    private readonly IPowerTelemetryProvider telemetryProvider;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly IVerifiedBladeDeviceProvider deviceProvider;
    private readonly IControllerPresenceMonitor controllerPresenceMonitor;
    private readonly DiagnosticHistory history;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ProfileSetValidator validator =
        ProfileSetValidator.CreateUnverifiedSafeDefaults();
    private readonly object initializationGate = new();
    private ProfileAutomationState? automation;
    private readonly ConfirmedStateDriftTracker driftTracker = new();
    private ServiceStatusSnapshot? status;

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        IPowerTelemetryProvider telemetryProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.telemetryProvider = telemetryProvider
            ?? throw new ArgumentNullException(nameof(telemetryProvider));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.deviceProvider = deviceProvider
            ?? throw new ArgumentNullException(nameof(deviceProvider));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.controllerPresenceMonitor = controllerPresenceMonitor
            ?? throw new ArgumentNullException(nameof(controllerPresenceMonitor));

    }

    public void Initialize(BatteryConfiguration configuration, PowerSource powerSource)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        validator.Validate(configuration.Profiles);
        validator.ValidatePreferences(configuration.Preferences);
        if (!Enum.IsDefined(powerSource))
        {
            throw new ArgumentOutOfRangeException(nameof(powerSource));
        }

        lock (initializationGate)
        {
            if (automation is not null)
            {
                throw new InvalidOperationException("The service runtime is already initialized.");
            }

            var initializedAutomation = new ProfileAutomationState(
                configuration.Profiles,
                configuration.Preferences,
                powerSource);
            TelemetrySnapshot telemetry = telemetryProvider.Capture() with
            {
                PowerSource = powerSource,
            };
            automation = initializedAutomation;
            Volatile.Write(ref status, CreateInitialSnapshot(telemetry));
        }
    }

    public ServiceStatusSnapshot GetStatus() => Volatile.Read(ref status)
        ?? throw new InvalidOperationException("The service runtime is not initialized.");

    public async Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            try
            {
                validator.Validate(profiles);
                await store.SaveProfilesAsync(profiles, token);
                Automation.UpdateProfiles(profiles);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                return RecordRejectedIntent(exception);
            }

            ServiceStatusSnapshot current = GetStatus();
            ConfirmedDeviceState confirmed = current.ConfirmedState ?? UnknownConfirmedState;
            const string message = "Profiles stored without automatic hardware application.";
            PublishIfChanged(current with
            {
                Message = message,
                Profiles = Automation.Profiles,
                DesiredState = Automation.Resolve(confirmed),
                PerformanceOverride = Automation.PerformanceOverride,
                FanOverride = Automation.FanOverride,
                LightingOverride = Automation.LightingOverride,
                FullChargeOverride = Automation.DesiredBattery.TemporaryFullCharge,
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);
            return new ControlOperationResult(ControlOperationStatus.Stored, message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Performance,
            _ =>
            {
                ValidateOverride(value, nameof(value));
                Automation.SetPerformanceOverride(value);
                return Task.CompletedTask;
            },
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyPerformanceOutcomeAsync(
                    desired.Performance.Value,
                    cancellationToken),
            token);

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Fans,
            _ =>
            {
                ValidateOverride(value, nameof(value));
                Automation.SetFanOverride(value);
                return Task.CompletedTask;
            },
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyFanOutcomeAsync(desired.Fans.Value, cancellationToken),
            token);

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Lighting,
            _ =>
            {
                ValidateLightingOverride(value);
                Automation.SetLightingOverride(value);
                return Task.CompletedTask;
            },
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(
                    desired.Lighting.Value,
                    cancellationToken),
            token);

    public Task<ControlOperationResult> SetBatteryPolicyAsync(
        BatteryPolicy value,
        CancellationToken token)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(value);
        return ExecuteBatteryIntentAsync(
            current => current.TemporaryFullCharge
                ? null
                : new DesiredBatteryState(
                    value,
                    current.ProtectionEnabled,
                    temporaryFullCharge: false),
            (coordinator, staged, cancellationToken) =>
                coordinator.ApplyChargeLimitOutcomeAsync(
                    staged.ConfiguredPolicy,
                    cancellationToken),
            token,
            "Charge-limit changes are blocked while temporary full charge is active.");
    }

    public Task<ControlOperationResult> SetBatteryProtectionAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        return ExecuteBatteryIntentAsync(
            current => current.TemporaryFullCharge
                ? null
                : new DesiredBatteryState(
                    current.ConfiguredPolicy,
                    enabled,
                    temporaryFullCharge: false),
            (coordinator, staged, cancellationToken) =>
                coordinator.ApplyBatteryProtectionOutcomeAsync(
                    enabled,
                    staged.ConfiguredPolicy,
                    cancellationToken),
            token,
            "Battery protection is blocked while temporary full charge is active.");
    }

    public async Task<ControlOperationResult> SetFullChargeOverrideAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            TelemetrySnapshot telemetry = telemetryProvider.Capture();

            if (enabled && telemetry.PowerSource == PowerSource.Battery)
            {
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The requested intent was rejected: Temporary full charge can only " +
                    "be enabled while plugged in.");
                history.RecordSummary(rejected.Status, rejected.Message);
                PublishIfChanged(GetStatus() with { Telemetry = telemetry });
                return rejected;
            }

            if (enabled && !Automation.DesiredBattery.ProtectionEnabled)
            {
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The requested intent was rejected: Temporary full charge requires " +
                    "battery protection to be enabled.");
                history.RecordSummary(rejected.Status, rejected.Message);
                PublishIfChanged(GetStatus() with { Telemetry = telemetry });
                return rejected;
            }

            return await ApplyScopedIntentAsync(
                DeviceSubsystem.Battery,
                telemetry,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyTemporaryFullChargeOutcomeAsync(
                        enabled,
                        cancellationToken),
                token,
                desired => desired with
                {
                    Battery = new DesiredBatteryState(
                        desired.Battery.ConfiguredPolicy,
                        desired.Battery.ProtectionEnabled,
                        enabled),
                },
                _ =>
                {
                    if (enabled)
                    {
                        Automation.EnableTemporaryFullCharge();
                    }
                    else
                    {
                        Automation.CancelTemporaryFullCharge();
                    }

                    return Task.CompletedTask;
                });
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
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
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            TelemetrySnapshot telemetry = telemetryProvider.Capture();
            cancellationToken.ThrowIfCancellationRequested();
            if (Automation.ChangePowerSource(telemetry.PowerSource))
            {
                await ApplyFullTransitionAsync(telemetry, cancellationToken);
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
        EnsureInitialized();
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

    private async Task<ControlOperationResult> ExecuteScopedMutationAsync(
        DeviceSubsystem subsystem,
        Func<CancellationToken, Task> mutation,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token)
    {
        EnsureInitialized();
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
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                return RecordRejectedIntent(exception);
            }

            return await ApplyScopedIntentAsync(
                subsystem,
                GetStatus().Telemetry,
                apply,
                token);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ExecuteBatteryIntentAsync(
        Func<DesiredBatteryState, DesiredBatteryState?> stageIntent,
        Func<ControlCoordinator, DesiredBatteryState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        string blockedMessage = "Battery intent is blocked by the confirmed battery state.")
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            DesiredBatteryState? staged = stageIntent(Automation.DesiredBattery);
            if (staged is null)
            {
                var blocked = new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    blockedMessage);
                history.RecordSummary(blocked.Status, blocked.Message);
                return blocked;
            }

            ProfileSet stagedProfiles = Automation.Profiles with
            {
                BatteryPolicy = staged.ConfiguredPolicy,
            };
            try
            {
                validator.Validate(stagedProfiles);
            }
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                return RecordRejectedIntent(exception);
            }

            return await ApplyScopedIntentAsync(
                DeviceSubsystem.Battery,
                GetStatus().Telemetry,
                (coordinator, _, cancellationToken) =>
                    apply(coordinator, staged, cancellationToken),
                token,
                desired => desired with { Battery = staged },
                async cancellationToken =>
                {
                    await store.SaveConfigurationAsync(
                        stagedProfiles,
                        new BatteryAutomationPreferences(staged.ProtectionEnabled),
                        cancellationToken);
                    Automation.CommitBatteryState(staged);
                });
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ApplyScopedIntentAsync(
        DeviceSubsystem subsystem,
        TelemetrySnapshot telemetry,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired = null,
        Func<CancellationToken, Task>? commitIntent = null)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        DesiredDeviceState desired = Automation.Resolve(cachedConfirmed ?? UnknownConfirmedState);
        if (stageDesired is not null)
        {
            desired = stageDesired(desired);
        }

        IBladeDevice device;
        try
        {
            bool available = deviceProvider.TryGetVerifiedDevice(out device);
            token.ThrowIfCancellationRequested();
            if (!available)
            {
                return PublishScopedUnavailable(
                    subsystem,
                    telemetry,
                    "No verified PID 02E0 device is available.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PublishScopedUnavailable(
                subsystem,
                telemetry,
                $"Verified device admission failed: {exception.Message}",
                failed: true);
        }

        ScopedApplyOutcome outcome;
        try
        {
            outcome = await apply(new ControlCoordinator(device), desired, token);
        }
        catch (IntentionalWriteCanceledException exception)
            when (token.IsCancellationRequested)
        {
            foreach (DeviceSubsystem attempted in exception.WriteAttempted)
            {
                driftTracker.ConfirmIntentional(attempted, matched: false);
            }

            var failed = new ControlResult(
                ControlResultStatus.Failed,
                $"{subsystem} write is indeterminate because confirmation was canceled.");
            history.RecordSubsystemResult(subsystem, failed);
            PublishScopedStatus(
                telemetry,
                cachedConfirmed,
                current.Capabilities,
                failed.Message,
                deviceVerified: true,
                writesBlocked: false);
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }

        ConfirmedDeviceState? confirmed = cachedConfirmed;
        if (outcome.Readback is not null)
        {
            driftTracker.Observe(outcome.Readback);
            confirmed = outcome.Readback;
        }

        if (outcome.ReadbackMatched)
        {
            driftTracker.ConfirmIntentional(subsystem, matched: true);
        }
        else if (outcome.WriteAttempted)
        {
            driftTracker.ConfirmIntentional(subsystem, matched: false);
        }

        if (outcome.Result.Status is ControlResultStatus.Applied
            or ControlResultStatus.Unchanged)
        {
            try
            {
                if (commitIntent is not null)
                {
                    await commitIntent(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                var failedCommit = new ControlResult(
                    ControlResultStatus.Failed,
                    $"Confirmed hardware intent could not be committed: {exception.Message}");
                history.RecordSubsystemResult(subsystem, failedCommit);
                PublishScopedStatus(
                    telemetry,
                    confirmed,
                    outcome.Capabilities ?? current.Capabilities,
                    failedCommit.Message,
                    deviceVerified: !outcome.DeviceAccessFailed,
                    writesBlocked: outcome.DeviceAccessFailed);
                return ToOperationResult(failedCommit);
            }
        }

        history.RecordSubsystemResult(subsystem, outcome.Result);
        PublishScopedStatus(
            telemetry,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            outcome.Result.Message,
            deviceVerified: !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed);
        return ToOperationResult(outcome.Result);
    }

    private async Task ApplyFullTransitionAsync(
        TelemetrySnapshot telemetry,
        CancellationToken token)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        IBladeDevice device;
        try
        {
            bool available = deviceProvider.TryGetVerifiedDevice(out device);
            token.ThrowIfCancellationRequested();
            if (!available)
            {
                PublishFullUnavailable(telemetry, "No verified PID 02E0 device is available.");
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PublishFullUnavailable(
                telemetry,
                $"Verified device admission failed: {exception.Message}",
                failed: true);
            return;
        }

        DesiredDeviceState desired = Automation.Resolve(cachedConfirmed ?? UnknownConfirmedState);
        FullTransitionApplyOutcome outcome;
        try
        {
            outcome = await new ControlCoordinator(device)
                .ApplyDesiredStateOutcomeAsync(desired, token);
        }
        catch (IntentionalWriteCanceledException exception)
            when (token.IsCancellationRequested)
        {
            foreach (DeviceSubsystem attempted in exception.WriteAttempted)
            {
                driftTracker.ConfirmIntentional(attempted, matched: false);
            }

            PublishScopedStatus(
                telemetry,
                cachedConfirmed,
                current.Capabilities,
                "Power-source transition writes are indeterminate because confirmation was canceled.",
                deviceVerified: true,
                writesBlocked: false);
            throw;
        }

        ConfirmedDeviceState? confirmed = cachedConfirmed;
        if (outcome.Readback is not null)
        {
            driftTracker.Observe(outcome.Readback);
            confirmed = outcome.Readback;
        }

        foreach (DeviceSubsystem subsystem in OrderedSubsystems)
        {
            if (outcome.ReadbackMatched.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: true);
            }
            else if (outcome.WriteAttempted.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
            else if (outcome.ReadbackContradicted.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }

        history.RecordSubsystemResults(outcome.Results);
        string message = Summarize(outcome.Results);
        PublishScopedStatus(
            telemetry,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            message,
            deviceVerified: !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed);
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
                Profiles = Automation.Profiles,
                DesiredState = Automation.Resolve(confirmed),
                Telemetry = telemetry,
                PerformanceOverride = Automation.PerformanceOverride,
                FanOverride = Automation.FanOverride,
                LightingOverride = Automation.LightingOverride,
                FullChargeOverride = Automation.DesiredBattery.TemporaryFullCharge,
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
            Profiles = Automation.Profiles,
            DesiredState = Automation.Resolve(confirmed),
            Telemetry = telemetry,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = Automation.DesiredBattery.TemporaryFullCharge,
        });
    }

    private ServiceStatusSnapshot CreateInitialSnapshot(TelemetrySnapshot telemetry) => new(
            Revision: 0,
            DeviceVerified: false,
            WritesBlocked: true,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: driftTracker.ExternalChanges,
            Message: "No verified PID 02E0 device is available.",
            Capabilities: UnavailableCapabilities,
            ConfirmedState: null,
            Profiles: Automation.Profiles,
            DesiredState: Automation.Resolve(UnknownConfirmedState),
            Telemetry: telemetry,
            PerformanceOverride: Automation.PerformanceOverride,
            FanOverride: Automation.FanOverride,
            LightingOverride: Automation.LightingOverride,
            FullChargeOverride: Automation.DesiredBattery.TemporaryFullCharge);

    private ControlOperationResult PublishScopedUnavailable(
        DeviceSubsystem subsystem,
        TelemetrySnapshot telemetry,
        string reason,
        bool failed = false)
    {
        ControlResultStatus resultStatus = failed
            ? ControlResultStatus.Failed
            : ControlResultStatus.Blocked;
        var result = new ControlResult(
            resultStatus,
            $"Hardware application was not confirmed. {reason}");
        history.RecordSubsystemResult(subsystem, result);
        PublishScopedStatus(
            telemetry,
            GetStatus().ConfirmedState,
            GetStatus().Capabilities,
            result.Message,
            deviceVerified: false,
            writesBlocked: true);
        return ToOperationResult(result);
    }

    private void PublishFullUnavailable(
        TelemetrySnapshot telemetry,
        string reason,
        bool failed = false)
    {
        ControlOperationStatus status = failed
            ? ControlOperationStatus.Failed
            : ControlOperationStatus.Blocked;
        PublishScopedStatus(
            telemetry,
            GetStatus().ConfirmedState,
            GetStatus().Capabilities,
            reason,
            deviceVerified: false,
            writesBlocked: true);
        history.RecordSummary(status, reason);
    }

    private void PublishScopedStatus(
        TelemetrySnapshot telemetry,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities? capabilities,
        string message,
        bool deviceVerified,
        bool writesBlocked)
    {
        ServiceStatusSnapshot current = GetStatus();
        PublishIfChanged(current with
        {
            DeviceVerified = deviceVerified,
            WritesBlocked = writesBlocked,
            ExternalChanges = driftTracker.ExternalChanges,
            Message = message,
            Capabilities = capabilities ?? UnavailableCapabilities,
            ConfirmedState = confirmed,
            Profiles = Automation.Profiles,
            DesiredState = Automation.Resolve(confirmed ?? UnknownConfirmedState),
            Telemetry = telemetry,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = Automation.DesiredBattery.TemporaryFullCharge,
        });
    }

    private ControlOperationResult RecordRejectedIntent(Exception exception)
    {
        var rejected = new ControlOperationResult(
            ControlOperationStatus.Failed,
            $"The requested intent was rejected: {exception.Message}");
        history.RecordSummary(rejected.Status, rejected.Message);
        return rejected;
    }

    private static ControlOperationResult ToOperationResult(ControlResult result) => new(
        result.Status switch
        {
            ControlResultStatus.Blocked => ControlOperationStatus.Blocked,
            ControlResultStatus.Failed => ControlOperationStatus.Failed,
            _ => ControlOperationStatus.Applied,
        },
        result.Message);

    private static bool IsIntentFailure(Exception exception) => exception is
        ArgumentException or InvalidDataException or IOException;

    private ProfileAutomationState Automation => automation
        ?? throw new InvalidOperationException("The service runtime is not initialized.");

    private void EnsureInitialized()
    {
        _ = GetStatus();
        _ = Automation;
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
            && left.ChargeLimits.SetEquals(right.ChargeLimits)
            && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
            && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge);

    private static string Summarize(SubsystemApplyResults results) =>
        string.Join(
            " ",
            results.Performance.Message,
            results.Fans.Message,
            results.Battery.Message,
            results.Lighting.Message);

    private static IReadOnlyList<DeviceSubsystem> OrderedSubsystems { get; } =
    [
        DeviceSubsystem.Performance,
        DeviceSubsystem.Fans,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

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
