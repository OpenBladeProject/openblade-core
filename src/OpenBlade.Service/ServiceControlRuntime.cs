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

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcuts are unavailable."));

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Lighting power policy is unavailable."));

    Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Reactive lighting input is unavailable."));

    Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "One-shot laptop controls are unavailable."));

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

    Task InvalidateLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RestoreLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class ServiceControlRuntime : IServiceControlRuntime, IDisposable
{
    private const string UnclassifiedExternalPowerMessage =
        "The external power adapter could not be classified; control writes are blocked.";
    private const string TransientExternalPowerMessage =
        "Power adapter classification is temporarily unavailable; the last confirmed power source is retained and control writes are blocked.";

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
    private readonly ProfileSetValidator validator;
    private readonly object initializationGate = new();
    private ProfileAutomationState? automation;
    private PersistentOverrideState persistentOverrides = PersistentOverrideState.Empty;
    private readonly ConfirmedStateDriftTracker driftTracker = new();
    private readonly HashSet<DeviceSubsystem> initialAutomaticComparisons = [];
    private ServiceStatusSnapshot? status;
    private bool mcuVersionReadAttempted;
    private LightingSettings? lastOwnedLightingSettings;
    private LightingSettings? pendingLightingRestore;
    private LightingSettings? faultedLightingRecoveryCandidate;
    private bool controllerLightingRelinquishPending;
    private readonly HashSet<LightingSuppressionReason> lightingSuppressions = [];

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
            static (delay, token) => Task.Delay(delay, token),
            ProfileSetValidator.CreateUnverifiedSafeDefaults())
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
        IFirmwareVersionSource firmwareVersionSource,
        ProfileSetValidator validator)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            controllerPresenceMonitor,
            firmwareVersionSource,
            static (delay, token) => Task.Delay(delay, token),
            validator)
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
            transitionRetryDelay,
            ProfileSetValidator.CreateUnverifiedSafeDefaults())
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
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            controllerPresenceMonitor,
            firmwareVersionSource,
            transitionRetryDelay,
            ProfileSetValidator.CreateUnverifiedSafeDefaults())
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
        Func<TimeSpan, CancellationToken, Task> transitionRetryDelay,
        ProfileSetValidator validator)
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
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public void Initialize(BatteryConfiguration configuration, PowerSource powerSource) =>
        Initialize(configuration, powerSource, PersistentOverrideState.Empty);

    public void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ProfileSet profiles = ProfileStore.NormalizeLegacyBatteryProfile(
            configuration.Profiles,
            out _);
        persistentOverrides = PersistentOverrideStore.NormalizeLegacyLighting(
            persistentOverrides);
        validator.Validate(profiles);
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
                profiles,
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
            bool lightingWasSuppressed = lightingSuppressions.Count > 0;
            try
            {
                profiles = ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _);
                validator.Validate(profiles);
                await store.SaveProfilesAsync(profiles, token);
                Automation.UpdateProfiles(profiles);
                RemoveDisabledLightingSuppressions();
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
                LightingSuppressions = CurrentLightingSuppressions(),
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);

            if (lightingWasSuppressed
                && lightingSuppressions.Count == 0
                && current.ActivePowerSource is
                    PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                && !IsControllerPresenceUnsafe(current)
                && !current.ExternalChanges.Contains(DeviceSubsystem.Lighting))
            {
                ControlOperationResult restored = await ApplyScopedIntentAsync(
                    DeviceSubsystem.Lighting,
                    current.ActivePowerSource,
                    (coordinator, desired, cancellationToken) =>
                        coordinator.ApplyLightingOutcomeAsync(
                            desired.Lighting.Value,
                            cancellationToken,
                            forceWrite: true),
                    token).ConfigureAwait(false);
                if (restored.Status != ControlOperationStatus.Applied)
                {
                    return restored with
                    {
                        Message = $"Profiles were stored. Lighting restore result: {restored.Message}",
                    };
                }
            }

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

    public async Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(source))
        {
            return RecordRejectedIntent(new ArgumentOutOfRangeException(nameof(source)));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Performance,
                    powerSource,
                    "The external power adapter could not be classified; the performance shortcut is blocked.");
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            ServiceStatusSnapshot current = GetStatus();
            if (IsControllerPresenceUnsafe(current)
                || current.ExternalChanges.Contains(DeviceSubsystem.Performance)
                || current.ExternalChanges.Contains(DeviceSubsystem.Fans))
            {
                const string conflictMessage =
                    "The performance shortcut is paused while another controller or an external performance change is present.";
                history.RecordSummary(ControlOperationStatus.Blocked, conflictMessage);
                PublishIfChanged(current with { Message = conflictMessage });
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    conflictMessage);
            }

            IReadOnlyList<PerformancePreset> cycle = PerformanceShortcutCycle(
                powerSource,
                current.Capabilities?.PerformancePresets);
            if (cycle.Count < 2)
            {
                const string unavailableMessage =
                    "No alternate performance preset is available for the current power source.";
                history.RecordSummary(ControlOperationStatus.Blocked, unavailableMessage);
                PublishIfChanged(current with { Message = unavailableMessage });
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    unavailableMessage);
            }

            PerformancePreset currentPreset = Automation
                .Resolve(current.ConfirmedState ?? UnknownConfirmedState)
                .Performance.Value;
            int currentIndex = -1;
            for (int index = 0; index < cycle.Count; index++)
            {
                if (cycle[index] == currentPreset)
                {
                    currentIndex = index;
                    break;
                }
            }
            PerformancePreset next = cycle[(currentIndex + 1 + cycle.Count) % cycle.Count];
            var stagedOverride = new ManualOverride<PerformancePreset>(
                next,
                OverrideLifetime.UntilPowerSourceChange);
            try
            {
                ValidateThermalOverrideCombination(
                    stagedOverride,
                    Automation.FanOverride);
            }
            catch (Exception exception) when (IsIntentFailure(exception))
            {
                return RecordRejectedIntent(exception);
            }

            ControlOperationResult result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Performance,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyPerformanceOutcomeAsync(
                        desired.Performance.Value,
                        cancellationToken),
                token,
                desired => desired with
                {
                    Performance = new ResolvedSetting<PerformancePreset>(
                        next,
                        SettingValueSource.TemporaryOverride),
                },
                cancellationToken => SetPerformanceOverrideCoreAsync(
                    stagedOverride,
                    cancellationToken)).ConfigureAwait(false);
            if (result.Status is ControlOperationStatus.Applied or ControlOperationStatus.Stored)
            {
                string message = source == PerformanceShortcutSource.DedicatedButton
                    ? $"The performance button selected {next}."
                    : $"Fn+P selected {next}.";
                history.RecordSummary(result.Status, message);
                PublishIfChanged(GetStatus() with { Message = message });
                return result with { Message = message };
            }

            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

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
        CancellationToken token)
    {
        ManualOverride<LightingSettings>? normalized = value is null
            ? null
            : value with
            {
                Value = LightingSettingsFactory.NormalizeLegacyOff(value.Value),
            };
        return ExecuteScopedMutationAsync(
            DeviceSubsystem.Lighting,
            cancellationToken => SetLightingOverrideCoreAsync(
                normalized,
                cancellationToken),
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(
                    desired.Lighting.Value,
                    cancellationToken),
            token,
            normalized is null
                ? "Lighting override was cleared."
                : "Lighting override intent was stored.");
    }

    public async Task<ControlOperationResult> SetLightingSuppressionAsync(
        LightingSuppressionReason reason,
        bool suppressed,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(reason))
        {
            return RecordRejectedIntent(new ArgumentOutOfRangeException(nameof(reason)));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool alreadySet = lightingSuppressions.Contains(reason);
            if (alreadySet == suppressed)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "Lighting power policy state is already current.");
            }

            if (suppressed && !IsLightingSuppressionConfigured(reason))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The requested lighting power policy is not enabled.");
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    powerSource,
                    "The external power adapter could not be classified; lighting power policy writes are blocked.");
            }
            if (IsControllerPresenceUnsafe(current)
                || current.ExternalChanges.Contains(DeviceSubsystem.Lighting))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Lighting power policy is paused while another controller or an external lighting change is present.");
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            bool wasSuppressed = lightingSuppressions.Count > 0;
            if (suppressed)
            {
                lightingSuppressions.Add(reason);
            }
            else
            {
                lightingSuppressions.Remove(reason);
            }
            bool isSuppressed = lightingSuppressions.Count > 0;
            PublishIfChanged(GetStatus() with
            {
                LightingSuppressions = CurrentLightingSuppressions(),
            });

            if (wasSuppressed == isSuppressed)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "Lighting power policy state was updated.");
            }

            ControlOperationResult result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Lighting,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyLightingOutcomeAsync(
                        desired.Lighting.Value,
                        cancellationToken,
                        forceWrite: true),
                token).ConfigureAwait(false);
            return result.Status == ControlOperationStatus.Applied
                ? result with
                {
                    Message = isSuppressed
                        ? "Keyboard lighting was switched off by the lighting power policy."
                        : "Keyboard lighting was restored from the current OpenBlade settings.",
                }
                : result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || current.WritesBlocked
                || current.ActivePowerSource == PowerSource.ExternalUnknown)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Reactive lighting input is blocked while laptop controls are unavailable.");
            }

            if (current.LightingRendererOwnership !=
                LightingRendererOwnershipState.Reactive)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Reactive lighting is not currently active.");
            }

            IBladeDevice? device = null;
            try
            {
                if (!deviceProvider.TryGetVerifiedDevice(out device))
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        "Reactive lighting input is unavailable for this laptop.");
                }

                LightingRendererOwnershipState liveOwnership =
                    await ReadLightingOwnershipAsync(device, token).ConfigureAwait(false);
                PublishIfChanged(GetStatus() with
                {
                    LightingRendererOwnership = liveOwnership,
                });
                if (liveOwnership != LightingRendererOwnershipState.Reactive)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        "Reactive lighting is not currently active.");
                }

                token.ThrowIfCancellationRequested();
                await device.PulseReactiveKeyAsync(pulse, token).ConfigureAwait(false);
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Reactive lighting input was applied.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (device is not null)
                {
                    await PublishObservedLightingOwnershipAsync(
                        device,
                        CancellationToken.None).ConfigureAwait(false);
                }
                return new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "Reactive lighting input could not be applied.");
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsWellFormed())
        {
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "The laptop control request is invalid.");
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || current.WritesBlocked
                || current.Capabilities is null
                || !current.Capabilities.OneShotLaptopControls.Contains(value.Control))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "This laptop control is unavailable right now.");
            }

            try
            {
                if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        "This laptop control is unavailable right now.");
                }

                await device.ApplyOneShotLaptopControlAsync(value, token)
                    .ConfigureAwait(false);
                string label = value.Control switch
                {
                    OneShotLaptopControl.StartupAnimation => "Startup animation",
                    OneShotLaptopControl.FunctionKeyPrimary => "Function key behavior",
                    OneShotLaptopControl.GamingMode => "Gaming Mode",
                    _ => "Laptop control",
                };
                var result = new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    $"{label} was applied. OpenBlade cannot read its current firmware value.");
                history.RecordSummary(result.Status, result.Message);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                var result = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The laptop control could not be applied.");
                history.RecordSummary(result.Status, result.Message);
                return result;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);

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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(
                cancellationToken).ConfigureAwait(false);
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
                bool powerSourceConfirmed = powerSource != PowerSource.ExternalUnknown;
                await ObserveCoreAsync(
                    PowerSourceForStatus(powerSource),
                    powerSourceConfirmed,
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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(
                cancellationToken).ConfigureAwait(false);
            if (powerSource != PowerSource.ExternalUnknown
                && Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
            }
            else
            {
                bool powerSourceConfirmed = powerSource != PowerSource.ExternalUnknown;
                await ObserveCoreAsync(
                    PowerSourceForStatus(powerSource),
                    powerSourceConfirmed,
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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(
                cancellationToken).ConfigureAwait(false);
            PowerSource classified =
                await ResolveCurrentPowerSourceAsync(cancellationToken);
            ServiceStatusSnapshot current = GetStatus();
            if (classified == PowerSource.ExternalUnknown)
            {
                PowerSource statusPowerSource = PowerSourceForStatus(classified);
                PublishIfChanged(current with
                {
                    ActivePowerSource = statusPowerSource,
                    WritesBlocked = true,
                    Message = statusPowerSource == PowerSource.ExternalUnknown
                        ? UnclassifiedExternalPowerMessage
                        : TransientExternalPowerMessage,
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

            if (current.ActivePowerSource != classified
                || string.Equals(
                    current.Message,
                    TransientExternalPowerMessage,
                    StringComparison.Ordinal))
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

            await RecoverFaultedLightingRendererCoreAsync(cancellationToken)
                .ConfigureAwait(false);
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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task InvalidateLightingRendererAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InvalidateLightingRendererCoreAsync(
                preserveForLifecycleRestore: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task InvalidateLightingRendererCoreAsync(
        bool preserveForLifecycleRestore,
        CancellationToken cancellationToken)
    {
        if (!preserveForLifecycleRestore)
        {
            pendingLightingRestore = null;
        }
        faultedLightingRecoveryCandidate = null;

        try
        {
            LightingSettings? owned = lastOwnedLightingSettings;
            LightingRendererOwnershipState liveOwnership =
                await deviceProvider
                    .InvalidateLightingRendererAsync(cancellationToken)
                    .ConfigureAwait(false);

            // Do not clear the cached ownership until the provider confirms that
            // it reached the live port. A canceled hardware-gate wait leaves the
            // renderer untouched and must remain retryable.
            lastOwnedLightingSettings = null;
            if (preserveForLifecycleRestore
                && IsExplicitLightingOwnership(liveOwnership)
                && owned is not null
                && OwnershipMatches(liveOwnership, owned))
            {
                pendingLightingRestore = owned;
            }
            else if (preserveForLifecycleRestore
                && (liveOwnership == LightingRendererOwnershipState.Faulted
                    || owned is not null))
            {
                // A live fault, relinquished renderer, or ownership mismatch
                // disproves the cached status and must cancel any stale restore.
                pendingLightingRestore = null;
            }
            // Repeated invalidation after a successful suspend/removal has no
            // live renderer and no new owned snapshot. Preserve the restore
            // already captured by the first invalidation.

            PublishIfChanged(GetStatus() with
            {
                LightingRendererOwnership = LightingRendererOwnershipState.None,
            });
        }
        finally
        {
            if (!preserveForLifecycleRestore)
            {
                pendingLightingRestore = null;
            }
        }
    }

    public async Task RestoreLightingRendererAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LightingSettings? requested = pendingLightingRestore;
            pendingLightingRestore = null;
            if (requested is null || lightingSuppressions.Count > 0)
            {
                return;
            }

            ServiceStatusSnapshot current = GetStatus();
            if (IsExplicitLightingOwnership(current.LightingRendererOwnership))
            {
                // A power-source transition already established the destination
                // profile's renderer. Never replace it with the pre-lifecycle one.
                return;
            }

            if (IsControllerPresenceUnsafe(current))
            {
                const string blockedMessage =
                    "OpenBlade lighting was not restored because another Razer controller may be active.";
                history.RecordSummary(ControlOperationStatus.Blocked, blockedMessage);
                PublishIfChanged(current with { Message = blockedMessage });
                return;
            }

            if (!current.DeviceVerified || current.WritesBlocked)
            {
                DeferLightingRestoreUntilDeviceArrival(requested, current);
                return;
            }

            IBladeDevice device;
            try
            {
                if (!deviceProvider.TryGetVerifiedDevice(out device))
                {
                    DeferLightingRestoreUntilDeviceArrival(requested, current);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                PublishLightingRestoreFailure(
                    "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                    LightingRendererOwnershipState.Faulted);
                return;
            }

            ScopedApplyOutcome outcome;
            try
            {
                outcome = await new ControlCoordinator(device)
                    .ApplyLightingOutcomeAsync(requested, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                PublishLightingRestoreFailure(
                    "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                    LightingRendererOwnershipState.Faulted);
                return;
            }

            LightingRendererOwnershipState observed =
                await PublishObservedLightingOwnershipAsync(device, cancellationToken)
                    .ConfigureAwait(false);
            bool restored = outcome.Result.Status is
                    ControlResultStatus.Applied or ControlResultStatus.Unchanged
                && OwnershipMatches(observed, requested);
            UpdateOwnedLightingSnapshot(
                observed,
                restored ? requested : null);
            if (!restored)
            {
                PublishLightingRestoreFailure(
                    "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                    observed);
                return;
            }

            const string message = "OpenBlade lighting was restored after the system lifecycle change.";
            history.RecordSubsystemResult(
                DeviceSubsystem.Lighting,
                new ControlResult(ControlResultStatus.Applied, message));
            PublishIfChanged(GetStatus() with { Message = message });
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void DeferLightingRestoreUntilDeviceArrival(
        LightingSettings requested,
        ServiceStatusSnapshot current)
    {
        pendingLightingRestore = requested;
        const string message =
            "OpenBlade is waiting for the laptop to reconnect before restoring lighting.";
        history.RecordSummary(ControlOperationStatus.Stored, message);
        PublishIfChanged(current with { Message = message });
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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);
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
            await RefreshControllerPresenceAndRelinquishIfNewConflictAsync(token)
                .ConfigureAwait(false);
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

        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        DesiredDeviceState desired = ResolveHardwareDesired(
            cachedConfirmed ?? UnknownConfirmedState);
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

        if (subsystem == DeviceSubsystem.Lighting)
        {
            LightingRendererOwnershipState observed =
                await PublishObservedLightingOwnershipAsync(device, token)
                    .ConfigureAwait(false);
            LightingSettings requested = desired.Lighting.Value;
            if (outcome.Result.Status is ControlResultStatus.Applied
                    or ControlResultStatus.Unchanged
                && !OwnershipMatches(observed, requested))
            {
                outcome = outcome with
                {
                    Result = new ControlResult(
                        ControlResultStatus.Failed,
                        "Keyboard lighting did not remain active after the change."),
                    ReadbackMatched = false,
                };
            }
            UpdateOwnedLightingSnapshot(
                observed,
                outcome.Result.Status is ControlResultStatus.Applied
                    or ControlResultStatus.Unchanged
                    ? requested
                    : null);
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
        else if (outcome.Result.Status == ControlResultStatus.Applied)
        {
            driftTracker.AcknowledgeIntentionalWithoutReadback(subsystem);
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

    private async Task RefreshControllerPresenceAndRelinquishIfNewConflictAsync(
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot before = GetStatus();
        bool previouslyUnsafe = IsControllerPresenceUnsafe(before);
        RefreshControllerPresenceCore(cancellationToken);
        ServiceStatusSnapshot current = GetStatus();
        if (!IsControllerPresenceUnsafe(current))
        {
            controllerLightingRelinquishPending = false;
            return;
        }

        bool hasRendererState =
            IsExplicitLightingOwnership(current.LightingRendererOwnership)
            || lastOwnedLightingSettings is not null
            || pendingLightingRestore is not null;
        if (!previouslyUnsafe && hasRendererState)
        {
            controllerLightingRelinquishPending = true;
        }

        if (!controllerLightingRelinquishPending)
        {
            return;
        }

        await InvalidateLightingRendererCoreAsync(
            preserveForLifecycleRestore: false,
            cancellationToken).ConfigureAwait(false);
        controllerLightingRelinquishPending = false;
        string message = current.PotentialControllers.Count > 0
            ? "Another Razer controller was detected; OpenBlade released keyboard lighting control."
            : "Other-controller detection became incomplete; OpenBlade released keyboard lighting control.";
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        ServiceStatusSnapshot relinquished = GetStatus();
        PublishIfChanged(relinquished with
        {
            ConfirmedState = relinquished.ConfirmedState is null
                ? null
                : relinquished.ConfirmedState with { Lighting = null },
            Message = message,
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

        DesiredDeviceState desired = ResolveHardwareDesired(
            cachedConfirmed ?? UnknownConfirmedState);
        bool allowAutomaticLightingWrite =
            CanClaimLightingForAutomaticProfile(current);
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
                        return GetCoupledAutomaticWriteExclusions(
                            allowAutomaticLightingWrite);
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
                            return GetCoupledAutomaticWriteExclusions(
                                allowAutomaticLightingWrite);
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

        LightingRendererOwnershipState observedLightingOwnership =
            await PublishObservedLightingOwnershipAsync(device, token)
                .ConfigureAwait(false);
        ControlResult lightingResult = ResultFor(
            outcome.Results,
            DeviceSubsystem.Lighting);
        if (lightingResult.Status is ControlResultStatus.Applied
                or ControlResultStatus.Unchanged
            && !OwnershipMatches(observedLightingOwnership, desired.Lighting.Value))
        {
            lightingResult = new ControlResult(
                ControlResultStatus.Failed,
                "Keyboard lighting did not remain active after the profile change.");
            var matched = new HashSet<DeviceSubsystem>(outcome.ReadbackMatched);
            matched.Remove(DeviceSubsystem.Lighting);
            var contradicted = new HashSet<DeviceSubsystem>(outcome.ReadbackContradicted)
            {
                DeviceSubsystem.Lighting,
            };
            outcome = outcome with
            {
                Results = outcome.Results with { Lighting = lightingResult },
                ReadbackMatched = matched,
                ReadbackContradicted = contradicted,
            };
        }

        foreach (DeviceSubsystem subsystem in OrderedSubsystems)
        {
            if (outcome.ReadbackMatched.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: true);
            }
            else if (ResultFor(outcome.Results, subsystem).Status == ControlResultStatus.Applied)
            {
                driftTracker.AcknowledgeIntentionalWithoutReadback(subsystem);
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
        UpdateOwnedLightingSnapshot(
            observedLightingOwnership,
            lightingResult.Status is ControlResultStatus.Applied
                or ControlResultStatus.Unchanged
                ? desired.Lighting.Value
                : null);

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

            AdapterPowerState classified = first;
            if (first != second)
            {
                // A single adapter query can transiently return Unknown while USB-C PD
                // remains connected. Retry only when the other sample is known. Two
                // different known values still indicate a real/unstable transition and
                // must remain unresolved so no control write uses ambiguous evidence.
                if (first == AdapterPowerState.Unknown
                    ^ second == AdapterPowerState.Unknown)
                {
                    AdapterPowerState known = first == AdapterPowerState.Unknown
                        ? second
                        : first;
                    AdapterPowerState retry =
                        await device.ReadAdapterPowerStateAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (retry == known)
                    {
                        classified = known;
                    }
                    else
                    {
                        return PowerSource.ExternalUnknown;
                    }
                }
                else
                {
                    return PowerSource.ExternalUnknown;
                }
            }

            return classified switch
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
        bool powerSourceConfirmed,
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
            LightingRendererOwnershipState lightingOwnership =
                await ReadLightingOwnershipAsync(device, cancellationToken)
                    .ConfigureAwait(false);
            CaptureFaultedLightingRecoveryCandidate(lightingOwnership);
            UpdateOwnedLightingSnapshot(lightingOwnership, appliedSettings: null);
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
                WritesBlocked = !powerSourceConfirmed,
                ExternalChanges = driftTracker.ExternalChanges,
                Message = !powerSourceConfirmed
                    ? powerSource == PowerSource.ExternalUnknown
                        ? "Verified device state observed, but the external power adapter could not be classified; control writes are blocked."
                        : TransientExternalPowerMessage
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
                LightingRendererOwnership = lightingOwnership,
            });

            if (powerSourceConfirmed)
            {
                await RecoverFaultedLightingRendererCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
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
        DesiredDeviceState desired = expected ?? ResolveHardwareDesired(confirmed);
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

    private IReadOnlySet<DeviceSubsystem> GetCoupledAutomaticWriteExclusions(
        bool allowLightingWrite)
    {
        var exclusions = driftTracker.ExternalChanges.ToHashSet();
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }

        if (!allowLightingWrite)
        {
            exclusions.Add(DeviceSubsystem.Lighting);
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

    private PowerSource PowerSourceForStatus(PowerSource observed)
    {
        if (observed != PowerSource.ExternalUnknown)
        {
            return observed;
        }

        PowerSource lastConfirmed = GetStatus().ActivePowerSource;
        return lastConfirmed is PowerSource.PluggedIn or PowerSource.UsbC
            ? lastConfirmed
            : PowerSource.ExternalUnknown;
    }

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
            LightingRendererOwnership = LightingRendererOwnershipState.None,
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
            ServiceVersion: ServiceVersion,
            LightingRendererOwnership: LightingRendererOwnershipState.None,
            LightingSuppressions: []);

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
            LightingRendererOwnership = deviceVerified
                ? current.LightingRendererOwnership
                : LightingRendererOwnershipState.None,
        });
    }

    private bool ResolveFullChargeOverride(ConfirmedDeviceState? confirmed) =>
        confirmed?.Battery?.TemporaryFullCharge
        ?? Automation.DesiredBattery.TemporaryFullCharge;

    private DesiredDeviceState ResolveHardwareDesired(ConfirmedDeviceState confirmed)
    {
        DesiredDeviceState desired = Automation.Resolve(confirmed);
        if (lightingSuppressions.Count == 0)
        {
            return desired;
        }

        return desired with
        {
            Lighting = desired.Lighting with
            {
                Value = LightingSettingsFactory.Off(),
            },
        };
    }

    private bool IsLightingSuppressionConfigured(LightingSuppressionReason reason)
    {
        LightingPowerPolicy policy = Automation.Profiles.EffectiveLightingPowerPolicy;
        return reason switch
        {
            LightingSuppressionReason.DisplayOff => policy.TurnOffWhenDisplayTurnsOff,
            LightingSuppressionReason.UserIdle => policy.IdleTimeoutMinutes.HasValue,
            _ => false,
        };
    }

    private LightingSuppressionReason[] CurrentLightingSuppressions() =>
        [.. lightingSuppressions.Order()];

    private void RemoveDisabledLightingSuppressions()
    {
        lightingSuppressions.RemoveWhere(reason => !IsLightingSuppressionConfigured(reason));
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
        && left.LightingRendererOwnership == right.LightingRendererOwnership
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private static async Task<LightingRendererOwnershipState> ReadLightingOwnershipAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadLightingRendererOwnershipAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return LightingRendererOwnershipState.None;
        }
    }

    private async Task<LightingRendererOwnershipState> PublishObservedLightingOwnershipAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        LightingRendererOwnershipState ownership =
            await ReadLightingOwnershipAsync(device, cancellationToken).ConfigureAwait(false);
        PublishIfChanged(GetStatus() with { LightingRendererOwnership = ownership });
        return ownership;
    }

    private void CaptureFaultedLightingRecoveryCandidate(
        LightingRendererOwnershipState observed)
    {
        if (observed != LightingRendererOwnershipState.Faulted
            || lastOwnedLightingSettings is not { } owned)
        {
            return;
        }

        LightingRendererOwnershipState previous = GetStatus().LightingRendererOwnership;
        if (IsExplicitLightingOwnership(previous) && OwnershipMatches(previous, owned))
        {
            faultedLightingRecoveryCandidate = owned;
        }
    }

    private async Task RecoverFaultedLightingRendererCoreAsync(
        CancellationToken cancellationToken)
    {
        if (lightingSuppressions.Count > 0)
        {
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        LightingSettings? requested = faultedLightingRecoveryCandidate;
        IBladeDevice? device = null;

        if (requested is null
            && lastOwnedLightingSettings is { } owned
            && current.LightingRendererOwnership == LightingRendererOwnershipState.None)
        {
            lastOwnedLightingSettings = null;
            requested = owned;
        }
        else if (requested is null
            && lastOwnedLightingSettings is { } currentlyOwned
            && IsExplicitLightingOwnership(current.LightingRendererOwnership)
            && OwnershipMatches(current.LightingRendererOwnership, currentlyOwned))
        {
            if (!deviceProvider.TryGetVerifiedDevice(out device))
            {
                return;
            }

            LightingRendererOwnershipState observed =
                await ReadLightingOwnershipAsync(device, cancellationToken)
                    .ConfigureAwait(false);
            if (observed is not (
                LightingRendererOwnershipState.None or
                LightingRendererOwnershipState.Faulted))
            {
                UpdateOwnedLightingSnapshot(observed, appliedSettings: null);
                PublishIfChanged(GetStatus() with
                {
                    LightingRendererOwnership = observed,
                });
                return;
            }

            faultedLightingRecoveryCandidate = currentlyOwned;
            lastOwnedLightingSettings = null;
            requested = currentlyOwned;
            PublishIfChanged(GetStatus() with
            {
                LightingRendererOwnership = LightingRendererOwnershipState.Faulted,
            });
        }

        if (requested is null)
        {
            return;
        }

        // Consume the candidate before writing. A persistent failure must not
        // become an unbounded 15-second retry loop; an explicit later lighting
        // action can establish a fresh ownership snapshot.
        faultedLightingRecoveryCandidate = null;
        current = GetStatus();
        if (IsControllerPresenceUnsafe(current)
            || current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            || !current.DeviceVerified
            || current.WritesBlocked)
        {
            PublishLightingRestoreFailure(
                "Keyboard lighting stopped after a renderer error and was not restarted automatically.",
                LightingRendererOwnershipState.Faulted);
            return;
        }

        if (device is null && !deviceProvider.TryGetVerifiedDevice(out device))
        {
            PublishLightingRestoreFailure(
                "Keyboard lighting stopped after a renderer error and could not be restarted automatically.",
                LightingRendererOwnershipState.Faulted);
            return;
        }

        ScopedApplyOutcome outcome;
        try
        {
            outcome = await new ControlCoordinator(device)
                .ApplyLightingOutcomeAsync(
                    requested,
                    cancellationToken,
                    forceWrite: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            PublishLightingRestoreFailure(
                "Keyboard lighting stopped after a renderer error and could not be restarted automatically.",
                LightingRendererOwnershipState.Faulted);
            return;
        }

        LightingRendererOwnershipState recoveredOwnership =
            await PublishObservedLightingOwnershipAsync(device, cancellationToken)
                .ConfigureAwait(false);
        bool recovered = outcome.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged
            && OwnershipMatches(recoveredOwnership, requested);
        UpdateOwnedLightingSnapshot(
            recoveredOwnership,
            recovered ? requested : null);
        if (!recovered)
        {
            PublishLightingRestoreFailure(
                "Keyboard lighting stopped after a renderer error and could not be restarted automatically.",
                recoveredOwnership);
            return;
        }

        const string message =
            "Keyboard lighting recovered after a transient renderer error.";
        history.RecordSubsystemResult(
            DeviceSubsystem.Lighting,
            new ControlResult(ControlResultStatus.Applied, message));
        driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: true);
        PublishIfChanged(GetStatus() with
        {
            ExternalChanges = driftTracker.ExternalChanges,
            Message = message,
        });
    }

    private void UpdateOwnedLightingSnapshot(
        LightingRendererOwnershipState ownership,
        LightingSettings? appliedSettings)
    {
        if (ownership == LightingRendererOwnershipState.Faulted)
        {
            lastOwnedLightingSettings = null;
            return;
        }

        if (ownership == LightingRendererOwnershipState.None)
        {
            // Observation can temporarily lose the provider's live port even
            // though OpenBlade still expects to own the saved host-rendered
            // effect. Intentional invalidation and Off writes clear the snapshot
            // explicitly; preserve it here so the bounded recovery path can
            // re-establish an unexpectedly missing renderer.
            if (appliedSettings is not null)
            {
                lastOwnedLightingSettings = null;
            }
            return;
        }

        if (appliedSettings is not null && OwnershipMatches(ownership, appliedSettings))
        {
            lastOwnedLightingSettings = appliedSettings;
            return;
        }

        if (lastOwnedLightingSettings is not { } existing
            || !OwnershipMatches(ownership, existing))
        {
            lastOwnedLightingSettings = null;
        }
    }

    private void PublishLightingRestoreFailure(
        string message,
        LightingRendererOwnershipState ownership)
    {
        lastOwnedLightingSettings = null;
        history.RecordSubsystemResult(
            DeviceSubsystem.Lighting,
            new ControlResult(ControlResultStatus.Failed, message));
        PublishIfChanged(GetStatus() with
        {
            Message = message,
            LightingRendererOwnership = ownership,
        });
    }

    private static bool IsExplicitLightingOwnership(
        LightingRendererOwnershipState ownership) => ownership is
        LightingRendererOwnershipState.Static or
        LightingRendererOwnershipState.Spectrum or
        LightingRendererOwnershipState.Breathing or
        LightingRendererOwnershipState.Reactive or
        LightingRendererOwnershipState.Tidal or
        LightingRendererOwnershipState.Starlight or
        LightingRendererOwnershipState.Wave or
        LightingRendererOwnershipState.Wheel;

    private static bool CanClaimLightingForAutomaticProfile(
        ServiceStatusSnapshot status) =>
        status.PotentialControllers.Count == 0
        && string.IsNullOrWhiteSpace(status.ControllerDetectionWarning);

    private static bool IsControllerPresenceUnsafe(ServiceStatusSnapshot status) =>
        status.PotentialControllers.Count > 0
        || !string.IsNullOrWhiteSpace(status.ControllerDetectionWarning);

    private static IReadOnlyList<PerformancePreset> PerformanceShortcutCycle(
        PowerSource powerSource,
        IReadOnlySet<PerformancePreset>? admitted)
    {
        PerformancePreset[] ordered = powerSource switch
        {
            PowerSource.PluggedIn =>
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
            ],
            PowerSource.Battery =>
            [
                PerformancePreset.BatteryBalanced,
                PerformancePreset.BatterySaver,
            ],
            _ => [],
        };
        if (admitted is null || admitted.Count == 0)
        {
            return ordered;
        }

        return ordered.Where(admitted.Contains).ToArray();
    }

    private static bool OwnershipMatches(
        LightingRendererOwnershipState ownership,
        LightingSettings settings) => ownership ==
        (settings.Effect switch
        {
            LightingEffect.Static => LightingRendererOwnershipState.Static,
            LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
            LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
            LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
            LightingEffect.Tidal => LightingRendererOwnershipState.Tidal,
            LightingEffect.Starlight => LightingRendererOwnershipState.Starlight,
            LightingEffect.Wave => LightingRendererOwnershipState.Wave,
            LightingEffect.Wheel => LightingRendererOwnershipState.Wheel,
            _ => LightingRendererOwnershipState.None,
        });

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

    private static ControlResult ResultFor(
        SubsystemApplyResults results,
        DeviceSubsystem subsystem) => subsystem switch
        {
            DeviceSubsystem.Performance => results.Performance,
            DeviceSubsystem.Fans => results.Fans,
            DeviceSubsystem.Battery => results.Battery,
            DeviceSubsystem.Lighting => results.Lighting,
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };

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
            || !LightingSettingsValidation.IsWellFormed(lighting))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
