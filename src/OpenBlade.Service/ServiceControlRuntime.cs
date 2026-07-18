using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public interface IServiceControlRuntime
{
    void Initialize(BatteryConfiguration configuration, PowerSource powerSource) =>
        throw new NotSupportedException();

    void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides) =>
        Initialize(configuration, powerSource);

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

    Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken);

    Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken);

    Task RefreshPowerSourceClassificationAsync(CancellationToken cancellationToken) =>
        HandlePowerSourceChangedAsync(cancellationToken);

    Task RefreshControllerPresenceAsync(CancellationToken cancellationToken);
}

public sealed class ServiceControlRuntime : IServiceControlRuntime, IDisposable
{
    private static readonly string ServiceVersion = OpenBladeProductVersion.Read(
        typeof(ServiceControlRuntime).Assembly);
    private static readonly TimeSpan PowerTransitionAccessRetryDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly ConfirmedDeviceState UnknownConfirmedState =
        new(null, null, null, null);
    private static readonly DeviceCapabilities UnavailableCapabilities =
        DeviceCapabilities.Unavailable();

    private readonly BatteryConfigurationStore store;
    private readonly PersistentOverrideStore overrideStore;
    private readonly IPowerStateProvider powerStateProvider;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly IVerifiedBladeDeviceProvider deviceProvider;
    private readonly IControllerPresenceMonitor controllerPresenceMonitor;
    private readonly DiagnosticHistory history;
    private readonly IFirmwareVersionSource firmwareVersionSource;
    private readonly Func<TimeSpan, CancellationToken, Task> transitionRetryDelay;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ProfileSetValidator validator =
        ProfileSetValidator.CreateUnverifiedSafeDefaults();
    private readonly object initializationGate = new();
    private ProfileAutomationState? automation;
    private PersistentOverrideState persistentOverrides = PersistentOverrideState.Empty;
    private readonly ConfirmedStateDriftTracker driftTracker = new();
    private readonly HashSet<DeviceSubsystem> initialAutomaticComparisons = [];
    private ServiceStatusSnapshot? status;
    private bool mcuVersionReadAttempted;

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            controllerPresenceMonitor,
            UnavailableFirmwareVersionSource.Instance)
    {
    }

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            controllerPresenceMonitor,
            firmwareVersionSource,
            static (delay, token) => Task.Delay(delay, token))
    {
    }

    internal ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor,
        Func<TimeSpan, CancellationToken, Task> transitionRetryDelay)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            controllerPresenceMonitor,
            UnavailableFirmwareVersionSource.Instance,
            transitionRetryDelay)
    {
    }

    internal ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource,
        Func<TimeSpan, CancellationToken, Task> transitionRetryDelay)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.overrideStore = overrideStore
            ?? throw new ArgumentNullException(nameof(overrideStore));
        this.powerStateProvider = powerStateProvider
            ?? throw new ArgumentNullException(nameof(powerStateProvider));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.deviceProvider = deviceProvider
            ?? throw new ArgumentNullException(nameof(deviceProvider));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.controllerPresenceMonitor = controllerPresenceMonitor
            ?? throw new ArgumentNullException(nameof(controllerPresenceMonitor));
        this.firmwareVersionSource = firmwareVersionSource
            ?? throw new ArgumentNullException(nameof(firmwareVersionSource));
        this.transitionRetryDelay = transitionRetryDelay
            ?? throw new ArgumentNullException(nameof(transitionRetryDelay));
    }

    public void Initialize(BatteryConfiguration configuration, PowerSource powerSource) =>
        Initialize(configuration, powerSource, PersistentOverrideState.Empty);

    public void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        validator.Validate(configuration.Profiles);
        validator.ValidatePreferences(configuration.Preferences);
        PersistentOverrideStore.Validate(persistentOverrides);
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
                powerSource,
                persistentOverrides);
            automation = initializedAutomation;
            this.persistentOverrides = persistentOverrides;
            Volatile.Write(ref status, CreateInitialSnapshot(powerSource));
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
                FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
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
            cancellationToken => SetPerformanceOverrideCoreAsync(value, cancellationToken),
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyPerformanceOutcomeAsync(
                    desired.Performance.Value,
                    cancellationToken),
            token,
            value is null
                ? "Performance override was cleared."
                : "Performance override intent was stored.");

    public Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Fans,
            cancellationToken => SetFanOverrideCoreAsync(value, cancellationToken),
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyFanOutcomeAsync(desired.Fans.Value, cancellationToken),
            token,
            value is null
                ? "Fan override was cleared."
                : "Fan override intent was stored.");

    public Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Lighting,
            cancellationToken => SetLightingOverrideCoreAsync(value, cancellationToken),
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(
                    desired.Lighting.Value,
                    cancellationToken),
            token,
            value is null
                ? "Lighting override was cleared."
                : "Lighting override intent was stored.");

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
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);

            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    powerSource,
                    "The external power adapter could not be classified; control writes are blocked.");
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token);
            }

            if (enabled && powerSource == PowerSource.Battery)
            {
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The requested intent was rejected: Temporary full charge can only " +
                    "be enabled while plugged in.");
                history.RecordSummary(rejected.Status, rejected.Message);
                PublishIfChanged(GetStatus() with { ActivePowerSource = powerSource });
                return rejected;
            }

            if (enabled && !Automation.DesiredBattery.ProtectionEnabled)
            {
                var rejected = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The requested intent was rejected: Temporary full charge requires " +
                    "battery protection to be enabled.");
                history.RecordSummary(rejected.Status, rejected.Message);
                PublishIfChanged(GetStatus() with { ActivePowerSource = powerSource });
                return rejected;
            }

            return await ApplyScopedIntentAsync(
                DeviceSubsystem.Battery,
                powerSource,
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

    public async Task ObserveAsync(
        bool includeLiveReadings,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshControllerPresenceCore(cancellationToken);
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(cancellationToken);
            int? batteryPowerFlow = includeLiveReadings
                ? powerStateProvider.CaptureBatteryPowerFlowMilliwatts()
                : null;
            bool changed = powerSource != PowerSource.ExternalUnknown
                && Automation.ChangePowerSource(powerSource);
            if (changed)
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
            }
            else
            {
                await ObserveCoreAsync(
                    powerSource,
                    includeLiveReadings,
                    batteryPowerFlow,
                    cancellationToken);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task ObserveAsync(CancellationToken cancellationToken) =>
        ObserveAsync(includeLiveReadings: false, cancellationToken);

    public async Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RefreshControllerPresenceCore(cancellationToken);
            if (powerSource != PowerSource.ExternalUnknown
                && Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
            }
            else
            {
                await ObserveCoreAsync(
                    powerSource,
                    includeLiveReadings: false,
                    batteryPowerFlowMilliwatts: null,
                    cancellationToken);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RefreshPowerSourceClassificationAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            PowerSource classified =
                await ResolveCurrentPowerSourceAsync(cancellationToken);
            ServiceStatusSnapshot current = GetStatus();
            if (classified == PowerSource.ExternalUnknown)
            {
                PublishIfChanged(current with
                {
                    ActivePowerSource = PowerSource.ExternalUnknown,
                    WritesBlocked = true,
                    Message = "The external power adapter could not be classified; control writes are blocked.",
                });
                return;
            }

            if (Automation.ChangePowerSource(classified))
            {
                await ApplyFullTransitionAsync(
                    classified,
                    cancellationToken,
                    preserveLiveReadings: true);
                return;
            }

            if (current.ActivePowerSource != classified)
            {
                PublishIfChanged(current with
                {
                    ActivePowerSource = classified,
                    WritesBlocked = false,
                    Message = "Power adapter classification recovered.",
                    DesiredState = Automation.Resolve(
                        current.ConfirmedState ?? UnknownConfirmedState),
                });
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
            RefreshControllerPresenceCore(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose() => operationGate.Dispose();

    private async Task SetPerformanceOverrideCoreAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken cancellationToken)
    {
        ValidateOverride(value, nameof(value));
        ValidateThermalOverrideCombination(value, Automation.FanOverride);
        PersistentOverrideState staged = persistentOverrides with
        {
            Performance = value?.Lifetime == OverrideLifetime.Persistent
                ? value.Value
                : null,
        };
        await PersistOverridesIfChangedAsync(staged, cancellationToken);
        Automation.SetPerformanceOverride(value);
        persistentOverrides = staged;
    }

    private async Task SetFanOverrideCoreAsync(
        ManualOverride<FanMode>? value,
        CancellationToken cancellationToken)
    {
        ValidateOverride(value, nameof(value));
        ValidateThermalOverrideCombination(Automation.PerformanceOverride, value);
        PersistentOverrideState staged = persistentOverrides with
        {
            Fans = value?.Lifetime == OverrideLifetime.Persistent
                ? value.Value
                : null,
        };
        await PersistOverridesIfChangedAsync(staged, cancellationToken);
        Automation.SetFanOverride(value);
        persistentOverrides = staged;
    }

    private async Task SetLightingOverrideCoreAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken cancellationToken)
    {
        ValidateLightingOverride(value);
        PersistentOverrideState staged = persistentOverrides with
        {
            Lighting = value?.Lifetime == OverrideLifetime.Persistent
                ? value.Value
                : null,
        };
        await PersistOverridesIfChangedAsync(staged, cancellationToken);
        Automation.SetLightingOverride(value);
        persistentOverrides = staged;
    }

    private Task PersistOverridesIfChangedAsync(
        PersistentOverrideState staged,
        CancellationToken cancellationToken) =>
        staged == persistentOverrides
            ? Task.CompletedTask
            : overrideStore.SaveAsync(staged, cancellationToken);

    private async Task<ControlOperationResult> ExecuteScopedMutationAsync(
        DeviceSubsystem subsystem,
        Func<CancellationToken, Task> mutation,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        string committedMessage)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    subsystem,
                    powerSource,
                    "The external power adapter could not be classified; overrides and control writes are blocked.");
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token);
            }
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

            ControlOperationResult result = await ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                apply,
                token);
            return result.Status == ControlOperationStatus.Applied
                ? result
                : result with { Message = $"{committedMessage} Hardware result: {result.Message}" };
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
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    powerSource,
                    "The external power adapter could not be classified; control writes are blocked.");
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token);
            }
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
                powerSource,
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
        PowerSource powerSource,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired = null,
        Func<CancellationToken, Task>? commitIntent = null)
    {
        if (powerSource == PowerSource.ExternalUnknown)
        {
            return PublishScopedUnavailable(
                subsystem,
                powerSource,
                "The external power adapter could not be classified; control writes are blocked.");
        }

        RefreshControllerPresenceCore(token);
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
                    powerSource,
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
                powerSource,
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
                powerSource,
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

        if (!outcome.ReadbackMatched && outcome.WriteAttempted)
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
                driftTracker.ConfirmIntentional(subsystem, matched: false);
                var canceledCommit = new ControlResult(
                    ControlResultStatus.Failed,
                    $"{subsystem} hardware was confirmed, but persistence was canceled.");
                history.RecordSubsystemResult(subsystem, canceledCommit);
                PublishScopedStatus(
                    powerSource,
                    confirmed,
                    outcome.Capabilities ?? current.Capabilities,
                    canceledCommit.Message,
                    deviceVerified: !outcome.DeviceAccessFailed,
                    writesBlocked: outcome.DeviceAccessFailed);
                throw;
            }
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
                var failedCommit = new ControlResult(
                    ControlResultStatus.Failed,
                    $"Confirmed hardware intent could not be committed: {exception.Message}");
                history.RecordSubsystemResult(subsystem, failedCommit);
                PublishScopedStatus(
                    powerSource,
                    confirmed,
                    outcome.Capabilities ?? current.Capabilities,
                    failedCommit.Message,
                    deviceVerified: !outcome.DeviceAccessFailed,
                    writesBlocked: outcome.DeviceAccessFailed);
                return ToOperationResult(failedCommit);
            }
        }

        if (outcome.ReadbackMatched)
        {
            // Matching hardware becomes the new drift baseline only after any durable
            // intent commit and in-memory commit have both succeeded.
            driftTracker.ConfirmIntentional(subsystem, matched: true);
        }

        history.RecordSubsystemResult(subsystem, outcome.Result);
        PublishScopedStatus(
            powerSource,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            outcome.Result.Message,
            deviceVerified: !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed);
        return ToOperationResult(outcome.Result);
    }

    private void RefreshControllerPresenceCore(CancellationToken cancellationToken)
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

    private async Task ApplyFullTransitionAsync(
        PowerSource powerSource,
        CancellationToken token,
        bool preserveLiveReadings = false)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        DesiredDeviceState? priorDesired = current.DesiredState;
        IBladeDevice device;
        try
        {
            bool available = deviceProvider.TryGetVerifiedDevice(out device);
            token.ThrowIfCancellationRequested();
            if (!available)
            {
                PublishFullUnavailable(powerSource, "No verified PID 02E0 device is available.");
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
                powerSource,
                $"Verified device admission failed: {exception.Message}",
                failed: true);
            return;
        }

        DesiredDeviceState desired = Automation.Resolve(cachedConfirmed ?? UnknownConfirmedState);
        FullTransitionApplyOutcome outcome;
        try
        {
            outcome = await new ControlCoordinator(device)
                .ApplyDesiredStateOutcomeAsync(
                    desired,
                    token,
                    automaticWriteExclusionResolver: confirmed =>
                    {
                        ObserveAutomaticState(confirmed, priorDesired, desired);
                        return GetCoupledAutomaticWriteExclusions();
                    });

            if (ShouldRetryPowerTransitionAccess(outcome))
            {
                await transitionRetryDelay(PowerTransitionAccessRetryDelay, token);
                outcome = await new ControlCoordinator(device)
                    .ApplyDesiredStateOutcomeAsync(
                        desired,
                        token,
                        automaticWriteExclusionResolver: confirmed =>
                        {
                            ObserveAutomaticState(confirmed, priorDesired, desired);
                            return GetCoupledAutomaticWriteExclusions();
                        });
            }
        }
        catch (IntentionalWriteCanceledException exception)
            when (token.IsCancellationRequested)
        {
            foreach (DeviceSubsystem attempted in exception.WriteAttempted)
            {
                driftTracker.ConfirmIntentional(attempted, matched: false);
            }

            PublishScopedStatus(
                powerSource,
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
        if (!preserveLiveReadings)
        {
            PublishIfChanged(GetStatus() with { LiveReadings = null });
        }
        PublishScopedStatus(
            powerSource,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            message,
            deviceVerified: !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed);
    }

    private static bool ShouldRetryPowerTransitionAccess(
        FullTransitionApplyOutcome outcome) =>
        outcome.DeviceAccessFailed
        && outcome.WriteAttempted.Count == 0
        && outcome.Readback is null;

    private async Task<PowerSource> ResolveCurrentPowerSourceAsync(
        CancellationToken cancellationToken)
    {
        PowerSource windowsSource = powerStateProvider.CapturePowerSource();
        cancellationToken.ThrowIfCancellationRequested();
        if (windowsSource == PowerSource.Battery)
        {
            return PowerSource.Battery;
        }

        if (windowsSource != PowerSource.ExternalUnknown)
        {
            // Test doubles and future providers may already have stronger evidence.
            return windowsSource;
        }

        try
        {
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return PowerSource.ExternalUnknown;
            }

            AdapterPowerState first =
                await device.ReadAdapterPowerStateAsync(cancellationToken);
            AdapterPowerState second =
                await device.ReadAdapterPowerStateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (first != second)
            {
                return PowerSource.ExternalUnknown;
            }

            return first switch
            {
                AdapterPowerState.FullPower280W => PowerSource.PluggedIn,
                AdapterPowerState.UsbCPower100W => PowerSource.UsbC,
                _ => PowerSource.ExternalUnknown,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PowerSource.ExternalUnknown;
        }
    }

    private async Task ObserveCoreAsync(
        PowerSource powerSource,
        bool includeLiveReadings,
        int? batteryPowerFlowMilliwatts,
        CancellationToken cancellationToken)
    {
        bool deviceAvailable = deviceProvider.TryGetVerifiedDevice(out IBladeDevice device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!deviceAvailable)
        {
            PublishObservationUnavailable(
                powerSource,
                includeLiveReadings
                    ? new LiveReadings(batteryPowerFlowMilliwatts, null, null)
                    : null,
                "No verified PID 02E0 device is available.");
            return;
        }

        try
        {
            DeviceCapabilities capabilities =
                await device.ReadCapabilitiesAsync(cancellationToken);
            ConfirmedDeviceState confirmed = await ReadObservedStateAsync(
                device,
                cancellationToken);
            FirmwareVersions? firmware = await ReadFirmwareVersionsAsync(
                device,
                cancellationToken);
            FanRpmReadings? fans = null;
            if (includeLiveReadings)
            {
                try
                {
                    fans = await device.ReadFanRpmAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    fans = null;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            ObserveAutomaticState(confirmed);
            PublishIfChanged(GetStatus() with
            {
                DeviceVerified = true,
                WritesBlocked = powerSource == PowerSource.ExternalUnknown,
                ExternalChanges = driftTracker.ExternalChanges,
                Message = powerSource == PowerSource.ExternalUnknown
                    ? "Verified device state observed, but the external power adapter could not be classified; control writes are blocked."
                    : "Verified device state observed.",
                Capabilities = capabilities,
                ConfirmedState = confirmed,
                Profiles = Automation.Profiles,
                DesiredState = Automation.Resolve(confirmed),
                ActivePowerSource = powerSource,
                LiveReadings = includeLiveReadings
                    ? new LiveReadings(
                        batteryPowerFlowMilliwatts,
                        fans?.Selector1Rpm,
                        fans?.Selector2Rpm)
                    : null,
                PerformanceOverride = Automation.PerformanceOverride,
                FanOverride = Automation.FanOverride,
                LightingOverride = Automation.LightingOverride,
                FullChargeOverride = ResolveFullChargeOverride(confirmed),
                Firmware = firmware,
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
                powerSource,
                includeLiveReadings
                    ? new LiveReadings(batteryPowerFlowMilliwatts, null, null)
                    : null,
                $"Verified device access failed: {exception.Message}",
                deviceVerified: true);
            history.RecordSummary(
                ControlOperationStatus.Failed,
                GetStatus().Message);
        }
    }

    private static async Task<ConfirmedDeviceState> ReadObservedStateAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RazerProtocolException exception) when (
            exception.ValidationFailure == HidProtocolValidationFailure.PayloadValidator)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await device.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FirmwareVersions?> ReadFirmwareVersionsAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        FirmwareVersions? current = GetStatus().Firmware;
        if (mcuVersionReadAttempted
            || current is null
            || !string.Equals(
                current.McuVersion,
                "Unavailable",
                StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        mcuVersionReadAttempted = true;
        try
        {
            string? mcuVersion = await device.ReadMcuVersionAsync(cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(mcuVersion)
                ? current
                : current with { McuVersion = mcuVersion.Trim() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return current;
        }
    }

    private void ObserveAutomaticState(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState? expected = null,
        DesiredDeviceState? alternateExpected = null)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        DesiredDeviceState desired = expected ?? Automation.Resolve(confirmed);
        driftTracker.Observe(confirmed);

        if (alternateExpected is not null)
        {
            CompareTransitionAutomaticValue(
                DeviceSubsystem.Performance,
                confirmed.Performance,
                desired.Performance.Value,
                alternateExpected.Performance.Value);
            CompareTransitionAutomaticValue(
                DeviceSubsystem.Fans,
                confirmed.Fans,
                desired.Fans.Value,
                alternateExpected.Fans.Value);
            CompareTransitionAutomaticValue(
                DeviceSubsystem.Lighting,
                confirmed.Lighting,
                desired.Lighting.Value,
                alternateExpected.Lighting.Value);
            if (confirmed.Battery is { } transitionBattery)
            {
                initialAutomaticComparisons.Add(DeviceSubsystem.Battery);
                bool matches = transitionBattery.TemporaryFullCharge is true
                    || BatteryMatchesDesired(transitionBattery, desired.Battery)
                    || BatteryMatchesDesired(
                        transitionBattery,
                        alternateExpected.Battery);
                driftTracker.ConfirmIntentional(DeviceSubsystem.Battery, matches);
            }

            return;
        }

        CompareInitialAutomaticValue(
            DeviceSubsystem.Performance,
            confirmed.Performance,
            desired.Performance.Value);
        CompareInitialAutomaticValue(
            DeviceSubsystem.Fans,
            confirmed.Fans,
            desired.Fans.Value);
        CompareInitialAutomaticValue(
            DeviceSubsystem.Lighting,
            confirmed.Lighting,
            desired.Lighting.Value);

        if (confirmed.Battery is { } battery
            && initialAutomaticComparisons.Add(DeviceSubsystem.Battery))
        {
            bool matches = battery.TemporaryFullCharge is true
                || BatteryMatchesDesired(battery, desired.Battery);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Battery, matches);
        }
    }

    private IReadOnlySet<DeviceSubsystem> GetCoupledAutomaticWriteExclusions()
    {
        var exclusions = driftTracker.ExternalChanges.ToHashSet();
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }

        return exclusions;
    }

    private void ValidateThermalOverrideCombination(
        ManualOverride<PerformancePreset>? performanceOverride,
        ManualOverride<FanMode>? fanOverride)
    {
        PowerProfile activeProfile = Automation.PowerSource switch
        {
            PowerSource.PluggedIn => Automation.Profiles.PluggedIn,
            PowerSource.Battery => Automation.Profiles.Battery,
            PowerSource.UsbC => Automation.Profiles.UsbC,
            _ => throw new InvalidDataException(
                "Overrides are blocked until the external power adapter is classified."),
        };
        validator.ValidatePerformanceFanCombination(
            performanceOverride?.Value ?? activeProfile.Performance,
            fanOverride?.Value ?? activeProfile.Fans,
            Automation.PowerSource);
    }

    private void CompareInitialAutomaticValue<T>(
        DeviceSubsystem subsystem,
        T? confirmed,
        T desired)
        where T : struct
    {
        if (confirmed is null || !initialAutomaticComparisons.Add(subsystem))
        {
            return;
        }

        driftTracker.ConfirmIntentional(
            subsystem,
            EqualityComparer<T>.Default.Equals(confirmed.Value, desired));
    }

    private void CompareInitialAutomaticValue<T>(
        DeviceSubsystem subsystem,
        T? confirmed,
        T desired)
        where T : class
    {
        if (confirmed is null || !initialAutomaticComparisons.Add(subsystem))
        {
            return;
        }

        driftTracker.ConfirmIntentional(
            subsystem,
            EqualityComparer<T>.Default.Equals(confirmed, desired));
    }

    private void CompareTransitionAutomaticValue<T>(
        DeviceSubsystem subsystem,
        T? confirmed,
        T priorDesired,
        T newDesired)
        where T : struct
    {
        if (confirmed is null)
        {
            return;
        }

        initialAutomaticComparisons.Add(subsystem);
        driftTracker.ConfirmIntentional(
            subsystem,
            EqualityComparer<T>.Default.Equals(confirmed.Value, priorDesired)
            || EqualityComparer<T>.Default.Equals(confirmed.Value, newDesired));
    }

    private void CompareTransitionAutomaticValue<T>(
        DeviceSubsystem subsystem,
        T? confirmed,
        T priorDesired,
        T newDesired)
        where T : class
    {
        if (confirmed is null)
        {
            return;
        }

        initialAutomaticComparisons.Add(subsystem);
        driftTracker.ConfirmIntentional(
            subsystem,
            EqualityComparer<T>.Default.Equals(confirmed, priorDesired)
            || EqualityComparer<T>.Default.Equals(confirmed, newDesired));
    }

    private static bool BatteryMatchesDesired(
        ConfirmedBatteryState confirmed,
        DesiredBatteryState desired) =>
        confirmed.ProtectionEnabled == desired.ProtectionEnabled
        && (!desired.ProtectionEnabled
            || confirmed.ChargeLimitPercent == desired.ConfiguredPolicy.ChargeLimitPercent);

    private void PublishObservationUnavailable(
        PowerSource powerSource,
        LiveReadings? liveReadings,
        string message,
        bool deviceVerified = false)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState confirmed = current.ConfirmedState ?? UnknownConfirmedState;
        PublishIfChanged(current with
        {
            DeviceVerified = deviceVerified,
            WritesBlocked = true,
            ExternalChanges = driftTracker.ExternalChanges,
            Message = message,
            Profiles = Automation.Profiles,
            DesiredState = Automation.Resolve(confirmed),
            ActivePowerSource = powerSource,
            LiveReadings = liveReadings,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
        });
    }

    private ServiceStatusSnapshot CreateInitialSnapshot(PowerSource powerSource) => new(
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
            ActivePowerSource: powerSource,
            LiveReadings: null,
            PerformanceOverride: Automation.PerformanceOverride,
            FanOverride: Automation.FanOverride,
            LightingOverride: Automation.LightingOverride,
            FullChargeOverride: Automation.DesiredBattery.TemporaryFullCharge,
            Firmware: firmwareVersionSource.Capture(),
            ServiceVersion: ServiceVersion);

    private ControlOperationResult PublishScopedUnavailable(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
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
            powerSource,
            GetStatus().ConfirmedState,
            GetStatus().Capabilities,
            result.Message,
            deviceVerified: false,
            writesBlocked: true);
        return ToOperationResult(result);
    }

    private void PublishFullUnavailable(
        PowerSource powerSource,
        string reason,
        bool failed = false)
    {
        PublishIfChanged(GetStatus() with { LiveReadings = null });
        ControlOperationStatus status = failed
            ? ControlOperationStatus.Failed
            : ControlOperationStatus.Blocked;
        PublishScopedStatus(
            powerSource,
            GetStatus().ConfirmedState,
            GetStatus().Capabilities,
            reason,
            deviceVerified: false,
            writesBlocked: true);
        history.RecordSummary(status, reason);
    }

    private void PublishScopedStatus(
        PowerSource powerSource,
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
            ActivePowerSource = powerSource,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = ResolveFullChargeOverride(confirmed),
        });
    }

    private bool ResolveFullChargeOverride(ConfirmedDeviceState? confirmed) =>
        confirmed?.Battery?.TemporaryFullCharge
        ?? Automation.DesiredBattery.TemporaryFullCharge;

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
        ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException;

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
        && left.ActivePowerSource == right.ActivePowerSource
        && left.LiveReadings == right.LiveReadings
        && left.PerformanceOverride == right.PerformanceOverride
        && left.FanOverride == right.FanOverride
        && left.LightingOverride == right.LightingOverride
        && left.FullChargeOverride == right.FullChargeOverride
        && left.Firmware == right.Firmware
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private sealed class UnavailableFirmwareVersionSource : IFirmwareVersionSource
    {
        internal static UnavailableFirmwareVersionSource Instance { get; } = new();

        public FirmwareVersions Capture() => FirmwareVersions.Unavailable();
    }

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
        if (value is not null
            && (!Enum.IsDefined(value.Value) || !Enum.IsDefined(value.Lifetime)))
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
        if (!Enum.IsDefined(value.Lifetime)
            || !Enum.IsDefined(lighting.Effect)
            || lighting.Brightness > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
