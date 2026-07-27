using System.Diagnostics;
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

    Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token) => value is { Value: PerformancePreset.Custom }
            ? Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Custom performance is unavailable."))
            : SetPerformanceOverrideAsync(
                value is null
                    ? null
                    : new ManualOverride<PerformancePreset>(value.Value, value.Lifetime),
                token);

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcuts are unavailable."));

    Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcut previews are unavailable."));

    Task<ControlOperationResult> SetFanOverrideAsync(
        ManualOverride<FanMode>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token) => SetFanOverrideAsync(value, token);

    Task<ControlOperationResult> SetLightingOverrideAsync(
        ManualOverride<LightingSettings>? value,
        CancellationToken token);

    Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Keyboard brightness stepping is unavailable."));

    Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "External lighting override policy is unavailable."));

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

    Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Ambient Awareness input is unavailable."));

    Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Playback mute indication is unavailable."));

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

    Task ObserveAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken) => ObserveAsync(
            requestedLiveReadings != LiveReadingSensors.None,
            cancellationToken);

    Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken);

    Task RefreshPowerSourceClassificationAsync(CancellationToken cancellationToken) =>
        HandlePowerSourceChangedAsync(cancellationToken);

    Task RefreshControllerPresenceAsync(CancellationToken cancellationToken);

    Task MaintainDriverModeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<ControlOperationResult> PrepareForShutdownAsync(
        CancellationToken cancellationToken) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Keyboard Driver mode is released."));

    Task InvalidateLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RestoreLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task MaintainFanCurveAsync(CancellationToken cancellationToken) =>
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
    private static readonly TimeSpan FullObservationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FastPathControllerPresenceInterval =
        TimeSpan.FromSeconds(15);
    private const int ObservationFailureThreshold = 3;
    private static readonly ConfirmedDeviceState UnknownConfirmedState =
        new(null, null, null, null);
    private static readonly DeviceCapabilities UnavailableCapabilities =
        DeviceCapabilities.Unavailable();
    private static readonly HashSet<string> KeyboardDriverModeControllers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RazerAppEngine",
            "Razer Synapse Service",
        };

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
    private readonly object statusPublishGate = new();
    private ProfileAutomationState? automation;
    private BatteryAutomationPreferences preferences =
        new(ProtectionEnabled: true);
    private PersistentOverrideState persistentOverrides = PersistentOverrideState.Empty;
    private readonly ConfirmedStateDriftTracker driftTracker = new();
    private readonly HashSet<DeviceSubsystem> initialAutomaticComparisons = [];
    private ServiceStatusSnapshot? status;
    private bool mcuVersionReadAttempted;
    private LightingSettings? lastOwnedLightingSettings;
    private LightingSettings? pendingLightingRestore;
    private bool pendingLightingRestoreFromLifecycle;
    private LightingSettings? faultedLightingRecoveryCandidate;
    private readonly HashSet<LightingSuppressionReason> lightingSuppressions = [];
    private readonly IFanTemperatureSource fanTemperatureSource;
    private int? lastCurveFan1Rpm;
    private int? lastCurveFan2Rpm;
    private int consecutiveDeviceProbeFailures;
    private int consecutiveVerifiedAccessFailures;
    private bool driverModePortRecoveryAttempted;
    private bool fullObservationCompleted;
    private long lastFullObservationTimestamp;
    private bool controllerPresenceObserved;
    private long lastControllerPresenceTimestamp;
    private bool batteryHealthVerificationRetryPending;

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
        ProfileSetValidator validator,
        IFanTemperatureSource? fanTemperatureSource = null)
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
        this.fanTemperatureSource = fanTemperatureSource
            ?? new SafeWindowsFanTemperatureSource();
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
            preferences = configuration.Preferences;
            this.persistentOverrides = persistentOverrides;
            pendingLightingRestore = initializedAutomation
                .Resolve(UnknownConfirmedState)
                .Lighting
                .Value;
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
                FanControlOverride = Automation.FanControlOverride,
                LightingOverride = Automation.LightingOverride,
                FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
                LightingSuppressions = CurrentLightingSuppressions(),
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);

            if (lightingWasSuppressed
                && lightingSuppressions.Count == 0
                && current.ActivePowerSource is
                    PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                && (!current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
                    || preferences.AlwaysOverrideExternalLightingChanges))
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
        CancellationToken token) => SetPerformanceConfigurationOverrideAsync(
            value is null
                ? null
                : new PerformanceOverride(value.Value, value.Lifetime),
            token);

    public Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        CancellationToken token) => ExecuteScopedMutationAsync(
            DeviceSubsystem.Performance,
            cancellationToken => SetPerformanceOverrideCoreAsync(value, cancellationToken),
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyPerformanceOutcomeAsync(
                    desired.Performance.Value,
                    desired.CustomPerformance,
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
            RefreshControllerPresenceCore(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Performance,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; the performance shortcut is blocked.",
                    preserveCurrentAvailability: true);
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            ServiceStatusSnapshot current = GetStatus();
            PerformancePreset currentPreset =
                current.ExternalChanges.Contains(DeviceSubsystem.Performance)
                    && current.ConfirmedState?.Performance is { } observedPreset
                    ? observedPreset
                    : Automation
                        .Resolve(current.ConfirmedState ?? UnknownConfirmedState)
                        .Performance.Value;
            if (!PerformanceShortcutSelection.TrySelectNext(
                powerSource,
                current.Capabilities?.PerformancePresets,
                currentPreset,
                out PerformancePreset next))
            {
                const string unavailableMessage =
                    "No alternate performance preset is available for the current power source.";
                history.RecordSummary(ControlOperationStatus.Blocked, unavailableMessage);
                PublishIfChanged(current with { Message = unavailableMessage });
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    unavailableMessage);
            }
            var stagedOverride = new PerformanceOverride(
                next,
                OverrideLifetime.UntilPowerSourceChange,
                next == PerformancePreset.Custom
                    ? Automation.Profiles.PluggedIn.CustomPerformance
                        ?? CustomPerformanceSettings.Default
                    : null);
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
                        desired.CustomPerformance,
                        cancellationToken),
                token,
                desired => desired with
                {
                    Performance = new ResolvedSetting<PerformancePreset>(
                        next,
                        SettingValueSource.TemporaryOverride),
                    CustomPerformance = stagedOverride.CustomPerformance,
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
        CancellationToken token) => SetFanControlOverrideAsync(value, null, token);

    public Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken token)
    {
        FanControlSettings? previous = null;
        return ExecuteScopedMutationAsync(
            DeviceSubsystem.Fans,
            async cancellationToken =>
            {
                DesiredDeviceState before = ResolveHardwareDesired(
                    GetStatus().ConfirmedState ?? UnknownConfirmedState);
                previous = before.FanControl
                    ?? FanControlSettings.FromLegacy(before.Fans.Value);
                await SetFanOverrideCoreAsync(
                    value,
                    settings,
                    cancellationToken).ConfigureAwait(false);
            },
            (coordinator, desired, cancellationToken) =>
            {
                FanControlSettings requested = desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value);
                return coordinator.ApplyFanControlTransitionOutcomeAsync(
                    previous ?? requested,
                    requested,
                    cancellationToken);
            },
            token,
            value is null
                ? "Fan override was cleared."
                : "Fan override intent was stored.");
    }

    public async Task MaintainFanCurveAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ServiceStatusSnapshot snapshot = GetStatus();
        FanControlSettings settings = snapshot.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(snapshot.DesiredState.Fans.Value);
        if (settings.Mode != FanMode.ManualCurve
            || !snapshot.DeviceVerified
            || snapshot.WritesBlocked
            || snapshot.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            lastCurveFan1Rpm = null;
            lastCurveFan2Rpm = null;
            return;
        }

        FanTemperatureReadings temperatures =
            await fanTemperatureSource.ReadAsync(cancellationToken);
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            settings.ManualCurve,
            temperatures);
        if (lastCurveFan1Rpm == targets.Fan1Rpm
            && lastCurveFan2Rpm == targets.Fan2Rpm)
        {
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            FanControlSettings currentSettings = current.DesiredState.FanControl
                ?? FanControlSettings.FromLegacy(current.DesiredState.Fans.Value);
            if (currentSettings != settings
                || !current.DeviceVerified
                || current.WritesBlocked
                || current.ExternalChanges.Contains(DeviceSubsystem.Fans)
                || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return;
            }

            try
            {
                await device.SetFanTargetsAsync(
                    targets.Fan1Rpm,
                    targets.Fan2Rpm,
                    cancellationToken).ConfigureAwait(false);
                lastCurveFan1Rpm = targets.Fan1Rpm;
                lastCurveFan2Rpm = targets.Fan2Rpm;
                if (targets.FailSafe)
                {
                    string message = temperatures.CpuCelsius is null
                        ? "Manual fan curve is using maximum speed because CPU package temperature is unavailable."
                        : "Manual fan curve is using maximum speed for thermal safety.";
                    if (!string.Equals(current.Message, message, StringComparison.Ordinal))
                    {
                        PublishIfChanged(current with { Message = message });
                    }
                }
            }
            catch (CapabilityUnavailableException exception)
            {
                driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
                PublishIfChanged(current with
                {
                    ExternalChanges = driftTracker.ExternalChanges,
                    Message = exception.Message,
                });
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                // A GET immediately precedes target writes in the protocol port.
                // If another controller exited manual mode, preserve that change
                // and stop the curve loop instead of reclaiming the firmware.
                driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
                PublishIfChanged(current with
                {
                    ExternalChanges = driftTracker.ExternalChanges,
                    Message = exception.Message,
                });
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

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

    public async Task<ControlOperationResult> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(lifetime))
        {
            return RecordRejectedIntent(
                new ArgumentOutOfRangeException(nameof(direction)));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
            }

            if (Automation.ChangePowerSource(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            ServiceStatusSnapshot current = GetStatus();
            LightingSettings lighting = ResolveHardwareDesired(
                current.ConfirmedState ?? UnknownConfirmedState).Lighting.Value;
            if (lighting.Effect == LightingEffect.Off)
            {
                return await StepKeyboardBrightnessFromOffAsync(
                    direction,
                    lifetime,
                    powerSource,
                    lighting,
                    token).ConfigureAwait(false);
            }

            ControlOperationResult? admissionFailure = TryAdmitKeyboardBrightnessDevice(
                powerSource,
                token,
                out IBladeDevice device);
            if (admissionFailure is not null)
            {
                return admissionFailure;
            }

            (byte resultingBrightness, ControlOperationResult? stepFailure) =
                await StepVerifiedKeyboardBrightnessDeviceAsync(
                    device,
                    direction,
                    powerSource,
                    current,
                    token).ConfigureAwait(false);
            if (stepFailure is not null)
            {
                return stepFailure;
            }
            return await CompleteKeyboardBrightnessStepAsync(
                resultingBrightness,
                lifetime,
                powerSource,
                current,
                lighting,
                token).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> StepKeyboardBrightnessFromOffAsync(
        KeyboardBrightnessDirection direction,
        OverrideLifetime lifetime,
        PowerSource powerSource,
        LightingSettings lighting,
        CancellationToken token)
    {
        if (direction == KeyboardBrightnessDirection.Decrease)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Keyboard brightness is already at its minimum.");
        }

        LightingSettings baseline = ResolveVisibleLightingBaseline(powerSource);
        LightingSettings visible = baseline with
        {
            Brightness = 6,
            LogoMode = lighting.LogoMode ?? baseline.LogoMode,
        };
        var restored = new ManualOverride<LightingSettings>(visible, lifetime);
        await SetLightingOverrideCoreAsync(restored, token).ConfigureAwait(false);
        return await ApplyScopedIntentAsync(
            DeviceSubsystem.Lighting,
            powerSource,
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(
                    desired.Lighting.Value,
                    cancellationToken),
            token).ConfigureAwait(false);
    }

    private ControlOperationResult? TryAdmitKeyboardBrightnessDevice(
        PowerSource powerSource,
        CancellationToken token,
        out IBladeDevice device)
    {
        try
        {
            bool available = deviceProvider.TryGetVerifiedDevice(out device);
            token.ThrowIfCancellationRequested();
            return available
                ? null
                : PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    powerSource,
                    "The laptop could not be admitted for this control operation.",
                    preserveCurrentAvailability: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            device = null!;
            return PublishScopedUnavailable(
                DeviceSubsystem.Lighting,
                powerSource,
                $"Verified device admission failed: {exception.Message}",
                failed: true,
                preserveCurrentAvailability: true);
        }
    }

    private async Task<(byte Brightness, ControlOperationResult? Failure)>
        StepVerifiedKeyboardBrightnessDeviceAsync(
            IBladeDevice device,
            KeyboardBrightnessDirection direction,
            PowerSource powerSource,
            ServiceStatusSnapshot current,
            CancellationToken token)
    {
        try
        {
            byte resultingBrightness = await device.StepKeyboardBrightnessAsync(
                direction,
                token).ConfigureAwait(false);
            return (resultingBrightness, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);
            var blocked = new ControlResult(ControlResultStatus.Blocked, exception.Message);
            return (0, PublishKeyboardBrightnessFailure(powerSource, current, blocked));
        }
        catch (BladeWriteException exception)
        {
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);
            var failed = new ControlResult(
                exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    ? ControlResultStatus.Blocked
                    : ControlResultStatus.Failed,
                exception.Message);
            return (0, PublishKeyboardBrightnessFailure(powerSource, current, failed));
        }
        catch (Exception exception)
        {
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);
            var failed = new ControlResult(
                ControlResultStatus.Failed,
                $"Keyboard brightness could not be changed: {exception.Message}");
            return (0, PublishKeyboardBrightnessFailure(powerSource, current, failed));
        }
    }

    private async Task<ControlOperationResult> CompleteKeyboardBrightnessStepAsync(
        byte resultingBrightness,
        OverrideLifetime lifetime,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        LightingSettings lighting,
        CancellationToken token)
    {
        LightingSettings next = resultingBrightness == 0
            ? LightingSettingsFactory.Off() with { LogoMode = lighting.LogoMode }
            : lighting with { Brightness = resultingBrightness };
        try
        {
            await SetLightingOverrideCoreAsync(
                new ManualOverride<LightingSettings>(next, lifetime),
                token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsIntentFailure(exception))
        {
            var failed = new ControlResult(
                ControlResultStatus.Failed,
                $"Keyboard brightness changed, but its override could not be saved: {exception.Message}");
            return PublishKeyboardBrightnessFailure(
                powerSource,
                current,
                failed,
                deviceVerified: true,
                writesBlocked: false);
        }

        LightingRendererOwnershipState ownership = resultingBrightness == 0
            ? LightingRendererOwnershipState.None
            : current.LightingRendererOwnership;
        UpdateOwnedLightingSnapshot(
            ownership,
            resultingBrightness == 0 ? next : lighting with { Brightness = resultingBrightness });
        await TryPersistLastKnownLightingAsync(next, token).ConfigureAwait(false);
        driftTracker.AcknowledgeIntentionalWithoutReadback(DeviceSubsystem.Lighting);
        var applied = new ControlResult(
            ControlResultStatus.Applied,
            $"Keyboard brightness changed to {resultingBrightness}%.");
        history.RecordSubsystemResult(DeviceSubsystem.Lighting, applied);
        PublishScopedStatus(
            powerSource,
            current.ConfirmedState,
            current.Capabilities,
            applied.Message,
            deviceVerified: true,
            writesBlocked: false);
        if (ownership != current.LightingRendererOwnership)
        {
            PublishIfChanged(GetStatus() with
            {
                LightingRendererOwnership = ownership,
            });
        }

        return ToOperationResult(applied);
    }

    private ControlOperationResult PublishKeyboardBrightnessFailure(
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ControlResult failed,
        bool? deviceVerified = null,
        bool? writesBlocked = null)
    {
        history.RecordSubsystemResult(DeviceSubsystem.Lighting, failed);
        PublishScopedStatus(
            powerSource,
            current.ConfirmedState,
            current.Capabilities,
            failed.Message,
            deviceVerified: deviceVerified ?? current.DeviceVerified,
            writesBlocked: writesBlocked ?? current.WritesBlocked);
        return ToOperationResult(failed);
    }

    public async Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BatteryAutomationPreferences staged = preferences with
            {
                AlwaysOverrideExternalLightingChanges = enabled,
            };
            await store.SaveConfigurationAsync(Automation.Profiles, staged, token)
                .ConfigureAwait(false);
            preferences = staged;
            ServiceStatusSnapshot current = GetStatus();
            PublishIfChanged(current with
            {
                AlwaysOverrideExternalLightingChanges = enabled,
                Message = enabled
                    ? "OpenBlade will reapply its last lighting effect after external changes."
                    : "OpenBlade will preserve confirmed external lighting changes.",
            });

            if (!enabled)
            {
                if (GetStatus().ExternalChanges.Contains(DeviceSubsystem.Lighting)
                    && (IsExplicitLightingOwnership(GetStatus().LightingRendererOwnership)
                        || lastOwnedLightingSettings is not null))
                {
                    await InvalidateLightingRendererCoreAsync(
                        preserveForLifecycleRestore: true,
                        token).ConfigureAwait(false);
                }

                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "External lighting changes will be preserved.");
            }

            driftTracker.AcknowledgeIntentionalWithoutReadback(DeviceSubsystem.Lighting);
            PowerSource powerSource = PowerSourceForStatus(
                await ResolveCurrentPowerSourceAsync(token).ConfigureAwait(false));
            ControlOperationResult result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Lighting,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyLightingOutcomeAsync(
                        desired.Lighting.Value,
                        cancellationToken,
                        forceWrite: true),
                token).ConfigureAwait(false);
            return result.Status == ControlOperationStatus.Failed
                ? result with
                {
                    Message = "The preference was saved, but the last lighting effect could not be reapplied. "
                        + result.Message,
                }
                : result;
        }
        finally
        {
            operationGate.Release();
        }
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
            RefreshControllerPresenceCore(token);
            ServiceStatusSnapshot current = GetStatus();
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; lighting power policy writes are blocked.",
                    preserveCurrentAvailability: true);
            }
            if (current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
                && !preferences.AlwaysOverrideExternalLightingChanges)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Lighting power policy is paused because an external lighting change is present.");
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
        token.ThrowIfCancellationRequested();
        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || current.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Key-triggered lighting input is blocked while laptop controls are unavailable.");
        }

        if (!IsKeyTriggeredLightingOwnership(current.LightingRendererOwnership))
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Key-triggered lighting is not currently active.");
        }

        IBladeDevice? device = null;
        try
        {
            if (!deviceProvider.TryGetOwnedKeyTriggeredLightingDevice(out device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Key-triggered lighting input is unavailable for this laptop.");
            }

            // This only updates the already-owned in-memory renderer. Periodic
            // firmware observation and unrelated controls must not stall it.
            await device.PulseReactiveKeyAsync(pulse, token).ConfigureAwait(false);
            return new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Key-triggered lighting input was applied.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            return new ControlOperationResult(ControlOperationStatus.Blocked, exception.Message);
        }
        catch
        {
            if (device is not null)
            {
                // Successful pulses intentionally bypass the general operation
                // gate for latency. A failure changes shared status, so merge
                // that observation through the serialized state path instead
                // of racing an unrelated observation or mutation publisher.
                await operationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await PublishObservedLightingOwnershipAsync(
                        device,
                        token).ConfigureAwait(false);
                }
                finally
                {
                    operationGate.Release();
                }
            }
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Reactive lighting input could not be applied.");
        }
    }

    public async Task<ControlOperationResult> PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken token)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsWellFormed())
        {
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "The Ambient Awareness frame is invalid.");
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || current.WritesBlocked
            || current.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Ambient Awareness is blocked while laptop controls are unavailable.");
        }

        if (current.LightingRendererOwnership !=
            LightingRendererOwnershipState.AmbientAwareness)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Ambient Awareness is not currently active.");
        }

        IBladeDevice? device = null;
        try
        {
            if (!deviceProvider.TryGetOwnedAmbientLightingDevice(out device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Ambient Awareness is unavailable for this laptop.");
            }

            await device.PushAmbientLightingFrameAsync(frame.Copy(), token)
                .ConfigureAwait(false);
            return new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Ambient Awareness frame was applied.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            return new ControlOperationResult(ControlOperationStatus.Blocked, exception.Message);
        }
        catch
        {
            if (device is not null)
            {
                await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await PublishObservedLightingOwnershipAsync(
                        device,
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    operationGate.Release();
                }
            }
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Ambient Awareness could not update the keyboard.");
        }
    }

    public async Task<ControlOperationResult> SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || current.WritesBlocked
            || current.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Playback mute indication is blocked while laptop controls are unavailable.");
        }

        if (current.Capabilities?.LightingEffects.Count == 0)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Playback mute indication is unavailable for this laptop.");
        }

        try
        {
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Playback mute indication is unavailable for this laptop.");
            }

            await device.SetPlaybackMuteIndicatorAsync(muted, token).ConfigureAwait(false);
            return new ControlOperationResult(
                ControlOperationStatus.Applied,
                muted
                    ? "The playback mute indicator was enabled."
                    : "The playback mute indicator was disabled.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            var result = new ControlOperationResult(
                ControlOperationStatus.Blocked,
                exception.Message);
            history.RecordSummary(result.Status, result.Message);
            return result;
        }
        catch
        {
            return new ControlOperationResult(
                ControlOperationStatus.Failed,
                "The playback mute indicator could not be updated.");
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
            RefreshControllerPresenceCore(token);
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

            return await ApplyVerifiedOneShotLaptopControlAsync(value, token)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ApplyVerifiedOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken token)
    {
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
            OneShotLaptopControlReadback readback =
                await ReadOneShotLaptopControlStateAsync(device, value.Control, token)
                    .ConfigureAwait(false);
            ControlOperationResult result = CreateOneShotLaptopControlResult(value, readback);
            PublishOneShotLaptopControlReadback(readback, result);
            history.RecordSummary(result.Status, result.Message);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            var result = new ControlOperationResult(
                ControlOperationStatus.Blocked,
                exception.Message);
            history.RecordSummary(result.Status, result.Message);
            return result;
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

    private static async Task<OneShotLaptopControlReadback>
        ReadOneShotLaptopControlStateAsync(
            IBladeDevice device,
            OneShotLaptopControl control,
            CancellationToken token)
    {
        try
        {
            return control switch
            {
                OneShotLaptopControl.StartupAnimation => new(
                    await device.ReadStartupAnimationAsync(token).ConfigureAwait(false),
                    null,
                    null),
                OneShotLaptopControl.FunctionKeyPrimary => new(
                    null,
                    await device.ReadFunctionKeyPrimaryAsync(token).ConfigureAwait(false),
                    null),
                OneShotLaptopControl.GamingMode => new(
                    null,
                    null,
                    await device.ReadGamingModeAsync(token).ConfigureAwait(false)),
                _ => new(null, null, null),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(null, null, null);
        }
    }

    private static ControlOperationResult CreateOneShotLaptopControlResult(
        ApplyOneShotLaptopControl value,
        OneShotLaptopControlReadback readback)
    {
        string label = value.Control switch
        {
            OneShotLaptopControl.StartupAnimation => "Startup animation",
            OneShotLaptopControl.FunctionKeyPrimary => "Function key behavior",
            OneShotLaptopControl.GamingMode => "Gaming Mode",
            OneShotLaptopControl.MicrophoneMuteIndicator => "Microphone mute indicator",
            _ => "Laptop control",
        };
        bool startupReadbackMatches = readback.StartupAnimation == value.Enabled;
        bool functionKeyReadbackMatches =
            readback.FunctionKeyPrimary == value.FunctionKeyMode;
        bool gamingModeReadbackMatches = readback.GamingMode == value.Enabled;
        bool readbackMismatch =
            (value.Control == OneShotLaptopControl.StartupAnimation
                && readback.StartupAnimation is not null
                && !startupReadbackMatches)
            || (value.Control == OneShotLaptopControl.FunctionKeyPrimary
                && readback.FunctionKeyPrimary is not null
                && !functionKeyReadbackMatches)
            || (value.Control == OneShotLaptopControl.GamingMode
                && readback.GamingMode is not null
                && !gamingModeReadbackMatches);
        return new ControlOperationResult(
            readbackMismatch
                ? ControlOperationStatus.Failed
                : ControlOperationStatus.Applied,
            value.Control switch
            {
                OneShotLaptopControl.StartupAnimation when startupReadbackMatches =>
                    $"{label} is {(value.Enabled is true ? "on" : "off")}.",
                OneShotLaptopControl.StartupAnimation when readback.StartupAnimation is not null =>
                    $"{label} did not match the requested setting.",
                OneShotLaptopControl.StartupAnimation =>
                    $"{label} was applied, but its current state could not be read.",
                OneShotLaptopControl.FunctionKeyPrimary when functionKeyReadbackMatches =>
                    $"{label} is {(value.FunctionKeyMode == FunctionKeyPrimaryMode.FunctionKeys ? "Function keys" : "Media keys")}.",
                OneShotLaptopControl.FunctionKeyPrimary when
                    readback.FunctionKeyPrimary is not null =>
                    $"{label} did not match the requested setting.",
                OneShotLaptopControl.FunctionKeyPrimary =>
                    $"{label} was applied, but its current state could not be read.",
                OneShotLaptopControl.GamingMode when gamingModeReadbackMatches =>
                    $"{label} is {(value.Enabled is true ? "on" : "off")}.",
                OneShotLaptopControl.GamingMode when readback.GamingMode is not null =>
                    $"{label} did not match the requested setting.",
                OneShotLaptopControl.GamingMode =>
                    $"{label} was applied, but its current state could not be read.",
                _ => $"{label} was applied. OpenBlade cannot read its current firmware value.",
            });
    }

    private void PublishOneShotLaptopControlReadback(
        OneShotLaptopControlReadback readback,
        ControlOperationResult result)
    {
        if (readback.StartupAnimation is not null)
        {
            PublishIfChanged(GetStatus() with
            {
                StartupAnimationEnabled = readback.StartupAnimation,
                Message = result.Message,
            });
        }
        if (readback.FunctionKeyPrimary is not null)
        {
            PublishIfChanged(GetStatus() with
            {
                FunctionKeyPrimary = readback.FunctionKeyPrimary,
                Message = result.Message,
            });
        }
        if (readback.GamingMode is not null)
        {
            PublishIfChanged(GetStatus() with
            {
                GamingModeEnabled = readback.GamingMode,
                Message = result.Message,
            });
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
            RefreshControllerPresenceCore(token);

            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
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

    public Task ObserveAsync(
        bool includeLiveReadings,
        CancellationToken cancellationToken) => ObserveAsync(
            includeLiveReadings ? LiveReadingSensors.All : LiveReadingSensors.None,
            cancellationToken);

    public async Task ObserveAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            bool includeLiveReadings = requestedLiveReadings != LiveReadingSensors.None;
            cancellationToken.ThrowIfCancellationRequested();
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(
                cancellationToken,
                allowCachedExternalClassification: includeLiveReadings);
            int? batteryPowerFlow = Requests(
                requestedLiveReadings,
                LiveReadingSensors.BatteryPower)
                ? powerStateProvider.CaptureBatteryPowerFlowMilliwatts()
                : null;
            bool sourceChanged = powerSource != PowerSource.ExternalUnknown
                && Automation.PowerSource != powerSource;
            bool canUseReadOnlyFastPath = !sourceChanged
                && CanUseObservationFastPath(
                    includeLiveReadings,
                    powerSource != PowerSource.ExternalUnknown);
            RefreshControllerPresenceCore(
                cancellationToken,
                allowRecentObservation: canUseReadOnlyFastPath);
            bool changed = powerSource != PowerSource.ExternalUnknown
                && Automation.ChangePowerSource(powerSource);
            if (changed)
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
            }
            else
            {
                bool powerSourceConfirmed = powerSource != PowerSource.ExternalUnknown;
                PowerSource statusPowerSource = PowerSourceForStatus(powerSource);
                if (canUseReadOnlyFastPath)
                {
                    await ObserveFastPathAsync(
                        statusPowerSource,
                        requestedLiveReadings,
                        batteryPowerFlow,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ObserveCoreAsync(
                        statusPowerSource,
                        powerSourceConfirmed,
                        requestedLiveReadings,
                        batteryPowerFlow,
                        cancellationToken).ConfigureAwait(false);
                }
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
            RefreshBatteryHealthAfterPowerSourceChangeCore();
            RefreshControllerPresenceCore(cancellationToken);
            if (powerSource != PowerSource.ExternalUnknown
                && Automation.ChangePowerSource(powerSource))
            {
                // The destination profile supersedes any renderer snapshot captured
                // before the power-source transition.
                pendingLightingRestore = null;
                pendingLightingRestoreFromLifecycle = false;
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
            }
            else
            {
                // Resume can leave firmware reporting Off after Windows has
                // already retired the matrix stream. Restore the owned renderer
                // before observation so that transient firmware state is not
                // misclassified as an external lighting change.
                if (pendingLightingRestoreFromLifecycle
                    && pendingLightingRestore is not null
                    && lightingSuppressions.Count == 0)
                {
                    bool restored =
                        await RestoreLightingRendererCoreAsync(cancellationToken)
                            .ConfigureAwait(false);
                    if (!restored)
                    {
                        return;
                    }
                }

                bool powerSourceConfirmed = powerSource != PowerSource.ExternalUnknown;
                await ObserveCoreAsync(
                    PowerSourceForStatus(powerSource),
                    powerSourceConfirmed,
                    requestedLiveReadings: LiveReadingSensors.None,
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
            RefreshControllerPresenceCore(cancellationToken);
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
            RefreshControllerPresenceCore(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task MaintainDriverModeAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                driverModePortRecoveryAttempted = false;
                PublishIfChanged(GetStatus() with { KeyboardDriverModeActive = null });
                return;
            }

            try
            {
                bool? active = await device.MaintainDriverModeAsync(cancellationToken)
                    .ConfigureAwait(false);
                driverModePortRecoveryAttempted = false;
                PublishIfChanged(GetStatus() with
                {
                    KeyboardDriverModeActive = active,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                PublishIfChanged(GetStatus() with { KeyboardDriverModeActive = null });

                if (!driverModePortRecoveryAttempted)
                {
                    driverModePortRecoveryAttempted = true;
                    try
                    {
                        // A control-interface failure can leave the independently
                        // running matrix renderer healthy while its command session
                        // is no longer usable. Retire that port once, preserve its
                        // owned lighting for the periodic restore step, and confirm
                        // Driver mode through a fresh exact-device session.
                        await InvalidateLightingRendererCoreAsync(
                            preserveForLifecycleRestore: true,
                            cancellationToken).ConfigureAwait(false);
                        if (deviceProvider.TryGetVerifiedDevice(
                                out IBladeDevice recoveredDevice))
                        {
                            bool? recovered = await recoveredDevice
                                .MaintainDriverModeAsync(cancellationToken)
                                .ConfigureAwait(false);
                            driverModePortRecoveryAttempted = false;
                            PublishIfChanged(GetStatus() with
                            {
                                KeyboardDriverModeActive = recovered,
                            });
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Keep the state unknown and let the normal observation
                        // cadence retry. Do not repeatedly recycle the renderer
                        // during one continuous failure streak.
                    }
                }

                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<ControlOperationResult> SetPerformanceShortcutPreviewAsync(
        Guid sessionId,
        long generation,
        PerformancePreset? preset,
        CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty
            || generation <= 0
            || preset is { } value && !Enum.IsDefined(value))
        {
            return Task.FromResult(RecordRejectedIntent(
                new ArgumentOutOfRangeException(nameof(generation))));
        }

        bool newerPreviewActive = false;
        bool previewChanged = false;
        if (preset is null)
        {
            PublishStatusUpdate(current =>
            {
                PerformanceShortcutPreview? existing =
                    current.PerformanceShortcutPreview;
                if (existing is null
                    || existing.SessionId != sessionId
                    || existing.Generation != generation)
                {
                    newerPreviewActive = true;
                    return current;
                }

                previewChanged = true;
                return current with { PerformanceShortcutPreview = null };
            });
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Stored,
                previewChanged
                    ? "Performance shortcut preview was cleared."
                    : "A newer performance shortcut preview is already active."));
        }

        PublishStatusUpdate(current =>
        {
            PerformanceShortcutPreview? existing =
                current.PerformanceShortcutPreview;
            if (existing is
                {
                    SessionId: var existingSession,
                    Generation: var existingGeneration,
                }
                && existingSession == sessionId
                && existingGeneration > generation)
            {
                newerPreviewActive = true;
                return current;
            }

            previewChanged = true;
            return current with
            {
                PerformanceShortcutPreview = new PerformanceShortcutPreview(
                    sessionId,
                    generation,
                    preset.Value),
            };
        });
        return Task.FromResult(new ControlOperationResult(
            ControlOperationStatus.Stored,
            newerPreviewActive && !previewChanged
                ? "A newer performance shortcut preview is already active."
                : "Performance shortcut preview was published."));
    }

    public async Task<ControlOperationResult> PrepareForShutdownAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefreshControllerPresenceCore(cancellationToken);
            ServiceStatusSnapshot current = GetStatus();
            if (current.ControllerDetectionWarning is not null)
            {
                const string unverified =
                    "Keyboard Driver mode was left unchanged because other Razer software could not be checked.";
                history.RecordSummary(ControlOperationStatus.Stored, unverified);
                return new ControlOperationResult(ControlOperationStatus.Stored, unverified);
            }

            if (current.PotentialControllers.Any(KeyboardDriverModeControllers.Contains))
            {
                const string retained =
                    "Keyboard Driver mode was left unchanged because Razer Synapse is active.";
                history.RecordSummary(ControlOperationStatus.Stored, retained);
                return new ControlOperationResult(ControlOperationStatus.Stored, retained);
            }

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                const string unavailable =
                    "OpenBlade could not verify the laptop before releasing keyboard Driver mode.";
                history.RecordSummary(ControlOperationStatus.Failed, unavailable);
                return new ControlOperationResult(ControlOperationStatus.Failed, unavailable);
            }

            bool? active = await device.ReleaseDriverModeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (active is not false)
            {
                const string unconfirmed =
                    "The laptop did not confirm that keyboard Driver mode was released.";
                history.RecordSummary(ControlOperationStatus.Failed, unconfirmed);
                return new ControlOperationResult(ControlOperationStatus.Failed, unconfirmed);
            }

            driverModePortRecoveryAttempted = false;
            const string message = "Keyboard Driver mode was released before shutdown.";
            history.RecordSummary(ControlOperationStatus.Applied, message);
            PublishIfChanged(GetStatus() with
            {
                KeyboardDriverModeActive = false,
                Message = message,
            });
            return new ControlOperationResult(ControlOperationStatus.Applied, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string failure =
                $"Keyboard Driver mode could not be released: {exception.Message}";
            history.RecordSummary(ControlOperationStatus.Failed, failure);
            return new ControlOperationResult(ControlOperationStatus.Failed, failure);
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
            pendingLightingRestoreFromLifecycle = false;
        }
        faultedLightingRecoveryCandidate = null;

        try
        {
            LightingSettings? owned = lastOwnedLightingSettings;
            LightingRendererOwnershipState publishedOwnership =
                GetStatus().LightingRendererOwnership;
            LightingRendererOwnershipState liveOwnership =
                await deviceProvider
                    .InvalidateLightingRendererAsync(cancellationToken)
                    .ConfigureAwait(false);

            // Do not clear the cached ownership until the provider confirms that
            // it reached the live port. A canceled hardware-gate wait leaves the
            // renderer untouched and must remain retryable.
            lastOwnedLightingSettings = null;
            if (preserveForLifecycleRestore
                && owned is not null
                && (IsExplicitLightingOwnership(liveOwnership)
                        && OwnershipMatches(liveOwnership, owned)
                    || liveOwnership == LightingRendererOwnershipState.None
                        && IsExplicitLightingOwnership(publishedOwnership)
                        && OwnershipMatches(publishedOwnership, owned)))
            {
                pendingLightingRestore = owned;
                pendingLightingRestoreFromLifecycle = true;
            }
            else if (preserveForLifecycleRestore
                && (liveOwnership == LightingRendererOwnershipState.Faulted
                    || owned is not null))
            {
                // A live fault, relinquished renderer, or ownership mismatch
                // disproves the cached status and must cancel any stale restore.
                pendingLightingRestore = null;
                pendingLightingRestoreFromLifecycle = false;
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
                pendingLightingRestoreFromLifecycle = false;
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
            _ = await RestoreLightingRendererCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<bool> RestoreLightingRendererCoreAsync(
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot current = GetStatus();
        LightingSettings? requested = pendingLightingRestore;
        bool lifecycleRestore = pendingLightingRestoreFromLifecycle;
        if (requested is null
            && preferences.AlwaysOverrideExternalLightingChanges
            && current.ExternalChanges.Contains(DeviceSubsystem.Lighting))
        {
            requested = preferences.LastKnownLighting
                ?? current.DesiredState.Lighting.Value;
        }
        if (requested is null || lightingSuppressions.Count > 0)
        {
            return true;
        }

        pendingLightingRestore = null;
        if (requested.Effect == LightingEffect.Off
            && current.ConfirmedState?.Lighting is { } confirmedLighting
            && LightingSettingsFactory.NormalizeLegacyOff(confirmedLighting)
                == LightingSettingsFactory.NormalizeLegacyOff(requested))
        {
            await TryPersistLastKnownLightingAsync(requested, cancellationToken)
                .ConfigureAwait(false);
            pendingLightingRestoreFromLifecycle = false;
            return true;
        }

        bool correctingExternalLightingChange =
            preferences.AlwaysOverrideExternalLightingChanges
            && current.ExternalChanges.Contains(DeviceSubsystem.Lighting);
        if (IsExplicitLightingOwnership(current.LightingRendererOwnership)
            && !correctingExternalLightingChange)
        {
            // A power-source transition already established the destination
            // profile's renderer. Never replace it with the pre-lifecycle one.
            pendingLightingRestoreFromLifecycle = false;
            return true;
        }

        if (current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            && !preferences.AlwaysOverrideExternalLightingChanges)
        {
            pendingLightingRestore = requested;
            pendingLightingRestoreFromLifecycle = lifecycleRestore;
            const string blockedMessage =
                "OpenBlade lighting was not restored because an external lighting change is present.";
            history.RecordSummary(ControlOperationStatus.Blocked, blockedMessage);
            PublishIfChanged(current with { Message = blockedMessage });
            return false;
        }

        if (!current.DeviceVerified || current.WritesBlocked)
        {
            DeferLightingRestoreUntilDeviceArrival(requested, current);
            return false;
        }

        IBladeDevice device;
        try
        {
            if (!deviceProvider.TryGetVerifiedDevice(out device))
            {
                DeferLightingRestoreUntilDeviceArrival(requested, current);
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            pendingLightingRestoreFromLifecycle = false;
            PublishLightingRestoreFailure(
                "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                LightingRendererOwnershipState.Faulted);
            return false;
        }

        ScopedApplyOutcome outcome;
        try
        {
            outcome = await new ControlCoordinator(device)
                .ApplyLightingOutcomeAsync(
                    requested,
                    cancellationToken,
                    forceWrite: preferences.AlwaysOverrideExternalLightingChanges)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            pendingLightingRestoreFromLifecycle = false;
            PublishLightingRestoreFailure(
                "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                LightingRendererOwnershipState.Faulted);
            return false;
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
            pendingLightingRestoreFromLifecycle = false;
            PublishLightingRestoreFailure(
                "OpenBlade lighting could not be restored after the system lifecycle change; it will not retry automatically.",
                observed);
            return false;
        }

        await TryPersistLastKnownLightingAsync(requested, cancellationToken)
            .ConfigureAwait(false);
        pendingLightingRestoreFromLifecycle = false;

        const string message = "OpenBlade lighting was restored after the system lifecycle change.";
        history.RecordSubsystemResult(
            DeviceSubsystem.Lighting,
            new ControlResult(ControlResultStatus.Applied, message));
        PublishIfChanged(GetStatus() with { Message = message });
        return true;
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

    public void Dispose()
    {
        operationGate.Dispose();
        if (fanTemperatureSource is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task SetPerformanceOverrideCoreAsync(
        PerformanceOverride? value,
        CancellationToken cancellationToken)
    {
        ValidateOverride(value, nameof(value));
        ValidateThermalOverrideCombination(value, Automation.FanOverride);
        PersistentOverrideState staged = persistentOverrides with
        {
            Performance = value?.Lifetime == OverrideLifetime.Persistent
                ? value.Value
                : null,
            CustomPerformance = value?.Lifetime == OverrideLifetime.Persistent
                ? value.CustomPerformance
                : null,
        };
        await PersistOverridesIfChangedAsync(staged, cancellationToken);
        Automation.SetPerformanceOverride(value);
        persistentOverrides = staged;
    }

    private async Task SetFanOverrideCoreAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        CancellationToken cancellationToken)
    {
        ValidateOverride(value, nameof(value));
        ValidateThermalOverrideCombination(Automation.PerformanceOverride, value);
        PersistentOverrideState staged = persistentOverrides with
        {
            Fans = value?.Lifetime == OverrideLifetime.Persistent
                ? value.Value
                : null,
            FanControl = value?.Lifetime == OverrideLifetime.Persistent
                ? settings ?? FanControlSettings.FromLegacy(value.Value)
                : null,
        };
        await PersistOverridesIfChangedAsync(staged, cancellationToken);
        Automation.SetFanOverride(value, settings);
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

    private LightingSettings ResolveVisibleLightingBaseline(PowerSource powerSource)
    {
        if (preferences.LastKnownLighting is
            {
                Effect: not LightingEffect.Off,
                Brightness: > 0,
            } remembered)
        {
            return remembered;
        }

        LightingSettings profile = Automation.Profiles.ForPowerSource(powerSource).Lighting;
        return profile.Effect != LightingEffect.Off && profile.Brightness > 0
            ? profile
            : LightingSettingsFactory.Static(
                LightingSettingsFactory.DefaultVisibleColor,
                brightness: 6);
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
            RefreshControllerPresenceCore(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    subsystem,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; overrides and control writes are blocked.",
                    preserveCurrentAvailability: true);
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
            RefreshControllerPresenceCore(token);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
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
                        preferences with { ProtectionEnabled = staged.ProtectionEnabled },
                        cancellationToken);
                    preferences = preferences with
                    {
                        ProtectionEnabled = staged.ProtectionEnabled,
                    };
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
                PowerSourceForStatus(powerSource),
                "The external power adapter could not be classified; control writes are blocked.",
                preserveCurrentAvailability: true);
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
                    "The laptop could not be admitted for this control operation.",
                    preserveCurrentAvailability: true);
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
                failed: true,
                preserveCurrentAvailability: true);
        }

        ScopedApplyOutcome outcome = await ApplyScopedIntentOutcomeAsync(
            subsystem,
            powerSource,
            current,
            cachedConfirmed,
            device,
            desired,
            apply,
            token);
        (outcome, ConfirmedDeviceState? confirmed) =
            await ReconcileScopedIntentReadbackAsync(
                subsystem,
                device,
                desired,
                cachedConfirmed,
                outcome,
                token).ConfigureAwait(false);

        if (!outcome.ReadbackMatched && outcome.WriteAttempted)
        {
            driftTracker.ConfirmIntentional(subsystem, matched: false);
        }

        ControlOperationResult? commitFailure = await CommitScopedIntentAsync(
            subsystem,
            powerSource,
            current,
            desired,
            confirmed,
            outcome,
            commitIntent,
            token).ConfigureAwait(false);
        if (commitFailure is not null)
        {
            return commitFailure;
        }

        return PublishScopedIntentOutcome(
            subsystem,
            powerSource,
            current,
            confirmed,
            outcome);
    }

    private async Task<ScopedApplyOutcome> ApplyScopedIntentOutcomeAsync(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? cachedConfirmed,
        IBladeDevice device,
        DesiredDeviceState desired,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token)
    {
        try
        {
            return await apply(new ControlCoordinator(device), desired, token);
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
    }

    private async Task<(ScopedApplyOutcome Outcome, ConfirmedDeviceState? Confirmed)>
        ReconcileScopedIntentReadbackAsync(
            DeviceSubsystem subsystem,
            IBladeDevice device,
            DesiredDeviceState desired,
            ConfirmedDeviceState? cachedConfirmed,
            ScopedApplyOutcome outcome,
            CancellationToken token)
    {
        ConfirmedDeviceState? confirmed = cachedConfirmed;
        if (outcome.Readback is not null)
        {
            confirmed = MergeScopedReadback(
                subsystem,
                cachedConfirmed,
                outcome.Readback);
            driftTracker.Observe(confirmed);
        }

        if (subsystem != DeviceSubsystem.Lighting)
        {
            return (outcome, confirmed);
        }

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
        return (outcome, confirmed);
    }

    private async Task<ControlOperationResult?> CommitScopedIntentAsync(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        DesiredDeviceState desired,
        ConfirmedDeviceState? confirmed,
        ScopedApplyOutcome outcome,
        Func<CancellationToken, Task>? commitIntent,
        CancellationToken token)
    {
        if (outcome.Result.Status is not (
            ControlResultStatus.Applied or ControlResultStatus.Unchanged))
        {
            return null;
        }

        try
        {
            if (commitIntent is not null)
            {
                await commitIntent(token);
            }

            if (subsystem == DeviceSubsystem.Lighting)
            {
                await TryPersistLastKnownLightingAsync(
                    desired.Lighting.Value,
                    token).ConfigureAwait(false);
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

        return null;
    }

    private ControlOperationResult PublishScopedIntentOutcome(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? confirmed,
        ScopedApplyOutcome outcome)
    {
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
        // A pre-write read failure is not proof that the exact laptop vanished.
        // Keep the last confirmed global availability until observation retries;
        // every later mutation still performs its own verified-device admission.
        // Once a write was attempted, retain fail-closed behavior because the
        // resulting firmware state may be indeterminate.
        bool preserveCurrentAvailability = outcome.DeviceAccessFailed
            && !outcome.WriteAttempted
            && current.DeviceVerified;
        PublishScopedStatus(
            powerSource,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            outcome.Result.Message,
            deviceVerified: preserveCurrentAvailability
                ? current.DeviceVerified
                : !outcome.DeviceAccessFailed,
            writesBlocked: preserveCurrentAvailability
                ? current.WritesBlocked
                : outcome.DeviceAccessFailed);
        return ToOperationResult(outcome.Result);
    }

    private void RefreshControllerPresenceCore(
        CancellationToken cancellationToken,
        bool allowRecentObservation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (allowRecentObservation
            && controllerPresenceObserved
            && Stopwatch.GetElapsedTime(lastControllerPresenceTimestamp)
                < FastPathControllerPresenceInterval)
        {
            return;
        }

        ControllerPresenceObservation observation = controllerPresenceMonitor.Observe();
        cancellationToken.ThrowIfCancellationRequested();
        controllerPresenceObserved = true;
        lastControllerPresenceTimestamp = Stopwatch.GetTimestamp();
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

        DesiredDeviceState desired = ResolveHardwareDesired(
            cachedConfirmed ?? UnknownConfirmedState);
        FullTransitionApplyOutcome outcome =
            await ApplyDesiredTransitionWithRetryAsync(
                device,
                desired,
                priorDesired,
                powerSource,
                current,
                cachedConfirmed,
                token);

        ConfirmedDeviceState? confirmed = cachedConfirmed;
        if (outcome.Readback is not null)
        {
            driftTracker.Observe(outcome.Readback);
            confirmed = outcome.Readback;
        }

        string? laptopBehaviorMessage = current.WritesBlocked
            ? null
            : await ApplyProfileLaptopBehaviorAsync(
                device,
                powerSource,
                outcome.Capabilities ?? current.Capabilities,
                token).ConfigureAwait(false);

        (outcome, LightingRendererOwnershipState observedLightingOwnership) =
            await ReconcileFullTransitionLightingAsync(
                device,
                desired,
                outcome,
                token).ConfigureAwait(false);

        await PublishFullTransitionOutcomeAsync(
            desired,
            outcome,
            observedLightingOwnership,
            powerSource,
            confirmed,
            current.Capabilities,
            laptopBehaviorMessage,
            preserveLiveReadings,
            token).ConfigureAwait(false);
    }

    private async Task<FullTransitionApplyOutcome> ApplyDesiredTransitionWithRetryAsync(
        IBladeDevice device,
        DesiredDeviceState desired,
        DesiredDeviceState? priorDesired,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? cachedConfirmed,
        CancellationToken token)
    {
        IReadOnlySet<DeviceSubsystem> ResolveAutomaticWriteExclusions(
            ConfirmedDeviceState confirmed)
        {
            ObserveAutomaticState(confirmed, priorDesired, desired);
            return GetCoupledAutomaticWriteExclusions();
        }

        try
        {
            FullTransitionApplyOutcome outcome = await new ControlCoordinator(device)
                .ApplyDesiredStateOutcomeAsync(
                    desired,
                    token,
                    automaticWriteExclusionResolver: ResolveAutomaticWriteExclusions);

            if (ShouldRetryPowerTransitionAccess(outcome))
            {
                await transitionRetryDelay(PowerTransitionAccessRetryDelay, token);
                outcome = await new ControlCoordinator(device)
                    .ApplyDesiredStateOutcomeAsync(
                        desired,
                        token,
                        automaticWriteExclusionResolver: ResolveAutomaticWriteExclusions);
            }

            return outcome;
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
    }

    private async Task<(
        FullTransitionApplyOutcome Outcome,
        LightingRendererOwnershipState ObservedOwnership)> ReconcileFullTransitionLightingAsync(
            IBladeDevice device,
            DesiredDeviceState desired,
            FullTransitionApplyOutcome outcome,
            CancellationToken token)
    {
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

        return (outcome, observedLightingOwnership);
    }

    private async Task PublishFullTransitionOutcomeAsync(
        DesiredDeviceState desired,
        FullTransitionApplyOutcome outcome,
        LightingRendererOwnershipState observedLightingOwnership,
        PowerSource powerSource,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities? cachedCapabilities,
        string? laptopBehaviorMessage,
        bool preserveLiveReadings,
        CancellationToken token)
    {
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
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

        ControlResult lightingResult = ResultFor(
            outcome.Results,
            DeviceSubsystem.Lighting);
        UpdateOwnedLightingSnapshot(
            observedLightingOwnership,
            lightingResult.Status is ControlResultStatus.Applied
                or ControlResultStatus.Unchanged
                ? desired.Lighting.Value
                : null);
        if (lightingResult.Status is ControlResultStatus.Applied
            or ControlResultStatus.Unchanged)
        {
            await TryPersistLastKnownLightingAsync(desired.Lighting.Value, token)
                .ConfigureAwait(false);
        }

        history.RecordSubsystemResults(outcome.Results);
        string message = Summarize(outcome.Results);
        if (!string.IsNullOrWhiteSpace(laptopBehaviorMessage))
        {
            message = $"{message} {laptopBehaviorMessage}";
            history.RecordSummary(ControlOperationStatus.Failed, laptopBehaviorMessage);
        }
        if (!preserveLiveReadings)
        {
            PublishIfChanged(GetStatus() with { LiveReadings = null });
        }
        PublishScopedStatus(
            powerSource,
            confirmed,
            outcome.Capabilities ?? cachedCapabilities,
            message,
            deviceVerified: !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed);
    }

    private async Task<string?> ApplyProfileLaptopBehaviorAsync(
        IBladeDevice device,
        PowerSource powerSource,
        DeviceCapabilities? capabilities,
        CancellationToken token)
    {
        LaptopBehaviorProfileSettings behavior = Automation.Profiles
            .ForPowerSource(powerSource)
            .EffectiveLaptopBehavior;
        if (!behavior.IsConfigured)
        {
            return null;
        }

        var failures = new List<string>();
        if (behavior.FunctionKeyPrimary is { } functionKeyMode)
        {
            await ApplyProfileLaptopControlAsync(
                device,
                capabilities,
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: functionKeyMode),
                failures,
                token).ConfigureAwait(false);
        }
        if (behavior.GamingModeEnabled is { } gamingModeEnabled)
        {
            await ApplyProfileLaptopControlAsync(
                device,
                capabilities,
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.GamingMode,
                    Enabled: gamingModeEnabled),
                failures,
                token).ConfigureAwait(false);
        }
        if (behavior.StartupAnimationEnabled is { } startupAnimationEnabled)
        {
            await ApplyProfileLaptopControlAsync(
                device,
                capabilities,
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.StartupAnimation,
                    Enabled: startupAnimationEnabled),
                failures,
                token).ConfigureAwait(false);
        }

        return failures.Count == 0
            ? null
            : $"Laptop behavior could not be synchronized: {string.Join(" ", failures)}";
    }

    private async Task ApplyProfileLaptopControlAsync(
        IBladeDevice device,
        DeviceCapabilities? capabilities,
        ApplyOneShotLaptopControl requested,
        ICollection<string> failures,
        CancellationToken token)
    {
        string label = requested.Control switch
        {
            OneShotLaptopControl.FunctionKeyPrimary => "Function key behavior",
            OneShotLaptopControl.GamingMode => "Gaming mode",
            OneShotLaptopControl.StartupAnimation => "Startup animation",
            _ => "Unknown laptop control",
        };
        if (capabilities is null
            || !capabilities.OneShotLaptopControls.Contains(requested.Control))
        {
            failures.Add($"{label} is unsupported.");
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        bool alreadyMatches = requested.Control switch
        {
            OneShotLaptopControl.FunctionKeyPrimary =>
                current.FunctionKeyPrimary == requested.FunctionKeyMode,
            OneShotLaptopControl.GamingMode =>
                current.GamingModeEnabled == requested.Enabled,
            OneShotLaptopControl.StartupAnimation =>
                current.StartupAnimationEnabled == requested.Enabled,
            _ => false,
        };
        if (alreadyMatches)
        {
            return;
        }

        try
        {
            await device.ApplyOneShotLaptopControlAsync(requested, token)
                .ConfigureAwait(false);
            switch (requested.Control)
            {
                case OneShotLaptopControl.FunctionKeyPrimary:
                    {
                        FunctionKeyPrimaryMode? readback = await device
                            .ReadFunctionKeyPrimaryAsync(token)
                            .ConfigureAwait(false);
                        if (readback != requested.FunctionKeyMode)
                        {
                            failures.Add($"{label} was not confirmed.");
                            return;
                        }
                        PublishIfChanged(GetStatus() with { FunctionKeyPrimary = readback });
                        break;
                    }
                case OneShotLaptopControl.GamingMode:
                    {
                        bool? readback = await device.ReadGamingModeAsync(token)
                            .ConfigureAwait(false);
                        if (readback != requested.Enabled)
                        {
                            failures.Add($"{label} was not confirmed.");
                            return;
                        }
                        PublishIfChanged(GetStatus() with { GamingModeEnabled = readback });
                        break;
                    }
                case OneShotLaptopControl.StartupAnimation:
                    {
                        bool? readback = await device.ReadStartupAnimationAsync(token)
                            .ConfigureAwait(false);
                        if (readback != requested.Enabled)
                        {
                            failures.Add($"{label} was not confirmed.");
                            return;
                        }
                        PublishIfChanged(GetStatus() with { StartupAnimationEnabled = readback });
                        break;
                    }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            failures.Add($"{label} could not be applied.");
        }
    }

    private static ConfirmedDeviceState MergeScopedReadback(
        DeviceSubsystem subsystem,
        ConfirmedDeviceState? cached,
        ConfirmedDeviceState readback)
    {
        if (cached is null || subsystem != DeviceSubsystem.Lighting)
        {
            return readback;
        }

        return cached with
        {
            Lighting = readback.Lighting,
            LogoLighting = readback.LogoLighting,
        };
    }

    private static bool ShouldRetryPowerTransitionAccess(
        FullTransitionApplyOutcome outcome) =>
        outcome.DeviceAccessFailed
        && outcome.WriteAttempted.Count == 0
        && outcome.Readback is null;

    private async Task<PowerSource> ResolveCurrentPowerSourceAsync(
        CancellationToken cancellationToken,
        bool allowCachedExternalClassification = false)
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

        if (allowCachedExternalClassification
            && Automation.PowerSource is PowerSource.PluggedIn or PowerSource.UsbC)
        {
            // Windows reports only "external power" here. Reuse the last
            // verified barrel-versus-USB-C classification for high-frequency
            // telemetry; lifecycle notifications and the 15-second classifier
            // continue to refresh it independently.
            return Automation.PowerSource;
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
        LiveReadingSensors requestedLiveReadings,
        int? batteryPowerFlowMilliwatts,
        CancellationToken cancellationToken)
    {
        bool includeLiveReadings = requestedLiveReadings != LiveReadingSensors.None;
        bool deviceAvailable = deviceProvider.TryGetVerifiedDevice(out IBladeDevice device);
        cancellationToken.ThrowIfCancellationRequested();
        if (!deviceAvailable)
        {
            PublishUnavailableDeviceObservation(
                powerSource,
                includeLiveReadings,
                batteryPowerFlowMilliwatts,
                cancellationToken);
            return;
        }

        consecutiveDeviceProbeFailures = 0;

        try
        {
            FullDeviceObservation observation = await ReadFullDeviceObservationAsync(
                device,
                requestedLiveReadings,
                includeLiveReadings,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObserveAutomaticState(observation.Confirmed);
            consecutiveVerifiedAccessFailures = 0;
            fullObservationCompleted = true;
            lastFullObservationTimestamp = Stopwatch.GetTimestamp();
            BatteryHealthStatus? batteryHealth =
                CaptureBatteryHealthAfterFirstVerificationCore();
            PublishVerifiedDeviceObservation(
                powerSource,
                powerSourceConfirmed,
                includeLiveReadings,
                batteryPowerFlowMilliwatts,
                observation,
                batteryHealth);

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
            PublishVerifiedDeviceObservationFailure(
                powerSource,
                includeLiveReadings,
                batteryPowerFlowMilliwatts,
                exception,
                cancellationToken);
        }
    }

    private void PublishUnavailableDeviceObservation(
        PowerSource powerSource,
        bool includeLiveReadings,
        int? batteryPowerFlowMilliwatts,
        CancellationToken cancellationToken)
    {
        fullObservationCompleted = false;
        consecutiveVerifiedAccessFailures = 0;
        cancellationToken.ThrowIfCancellationRequested();
        LiveReadings? liveReadings = includeLiveReadings
            ? new LiveReadings(batteryPowerFlowMilliwatts, null, null)
            : null;
        if (GetStatus().DeviceVerified
            && ++consecutiveDeviceProbeFailures < ObservationFailureThreshold)
        {
            PublishTransientDeviceProbeFailure(powerSource, liveReadings);
            return;
        }

        PublishObservationUnavailable(
            powerSource,
            liveReadings,
            "No verified PID 02E0 device is available.");
    }

    private async Task<FullDeviceObservation> ReadFullDeviceObservationAsync(
        IBladeDevice device,
        LiveReadingSensors requestedLiveReadings,
        bool includeLiveReadings,
        CancellationToken cancellationToken)
    {
        DeviceCapabilities capabilities =
            await device.ReadCapabilitiesAsync(cancellationToken);
        ServiceStatusSnapshot currentBeforeRead = GetStatus();
        if (!HasAnyControlCapability(capabilities)
            && currentBeforeRead.DeviceVerified
            && currentBeforeRead.Capabilities is { } previousCapabilities
            && HasAnyControlCapability(previousCapabilities))
        {
            // Capabilities are immutable for a connected model. A failed
            // refresh must not make every UI control disappear until the
            // next audit; each mutation still re-admits its own command.
            capabilities = previousCapabilities;
        }
        ConfirmedDeviceState confirmed = await ReadObservedStateAsync(
            device,
            cancellationToken);
        bool? startupAnimation = await ReadStartupAnimationStateAsync(
            device,
            cancellationToken).ConfigureAwait(false);
        FunctionKeyPrimaryMode? functionKeyPrimary =
            await ReadFunctionKeyPrimaryStateAsync(
                device,
                cancellationToken).ConfigureAwait(false);
        bool? gamingModeEnabled = await ReadGamingModeStateAsync(
            device,
            cancellationToken).ConfigureAwait(false);
        LightingRendererOwnershipState lightingOwnership =
            await ReadLightingOwnershipAsync(device, cancellationToken)
                .ConfigureAwait(false);
        CaptureFaultedLightingRecoveryCandidate(lightingOwnership);
        UpdateOwnedLightingSnapshot(lightingOwnership, appliedSettings: null);
        FirmwareVersions? firmware = await ReadFirmwareVersionsAsync(
            device,
            cancellationToken);
        FanRpmReadings? fans = null;
        FanTemperatureReadings temperatures = default;
        if (includeLiveReadings)
        {
            if (Requests(requestedLiveReadings, LiveReadingSensors.FanSpeeds))
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

            bool includeCpu = Requests(
                requestedLiveReadings,
                LiveReadingSensors.CpuTemperature);
            bool includeGpu = Requests(
                requestedLiveReadings,
                LiveReadingSensors.GpuTemperature);
            if (includeCpu || includeGpu)
            {
                try
                {
                    temperatures = await fanTemperatureSource.ReadAsync(
                        includeCpu,
                        includeGpu,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    temperatures = default;
                }
            }
        }

        return new(
            capabilities,
            confirmed,
            startupAnimation,
            functionKeyPrimary,
            gamingModeEnabled,
            lightingOwnership,
            firmware,
            fans,
            temperatures);
    }

    private void PublishVerifiedDeviceObservation(
        PowerSource powerSource,
        bool powerSourceConfirmed,
        bool includeLiveReadings,
        int? batteryPowerFlowMilliwatts,
        FullDeviceObservation observation,
        BatteryHealthStatus? batteryHealth)
    {
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
            Capabilities = observation.Capabilities,
            ConfirmedState = observation.Confirmed,
            Profiles = Automation.Profiles,
            DesiredState = Automation.Resolve(observation.Confirmed),
            ActivePowerSource = powerSource,
            LiveReadings = includeLiveReadings
                ? new LiveReadings(
                    batteryPowerFlowMilliwatts,
                    observation.Fans?.Selector1Rpm,
                    observation.Fans?.Selector2Rpm,
                    observation.Temperatures.CpuCelsius,
                    observation.Temperatures.GpuCelsius)
                : null,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            FanControlOverride = Automation.FanControlOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = ResolveFullChargeOverride(observation.Confirmed),
            Firmware = observation.Firmware,
            LightingRendererOwnership = observation.LightingOwnership,
            StartupAnimationEnabled = observation.StartupAnimation,
            FunctionKeyPrimary = observation.FunctionKeyPrimary,
            GamingModeEnabled = observation.GamingModeEnabled,
            BatteryHealth = batteryHealth,
        });
    }

    private void PublishVerifiedDeviceObservationFailure(
        PowerSource powerSource,
        bool includeLiveReadings,
        int? batteryPowerFlowMilliwatts,
        Exception exception,
        CancellationToken cancellationToken)
    {
        fullObservationCompleted = false;
        cancellationToken.ThrowIfCancellationRequested();
        string message = $"Verified device access failed: {exception.Message}";
        ServiceStatusSnapshot current = GetStatus();
        LiveReadings? liveReadings = includeLiveReadings
            ? new LiveReadings(batteryPowerFlowMilliwatts, null, null)
            : null;
        if (current.DeviceVerified
            && ++consecutiveVerifiedAccessFailures < ObservationFailureThreshold)
        {
            PublishTransientVerifiedAccessFailure(
                powerSource,
                liveReadings,
                message);
        }
        else
        {
            PublishObservationUnavailable(
                powerSource,
                liveReadings,
                message,
                deviceVerified: true);
        }
        history.RecordSummary(
            ControlOperationStatus.Failed,
            GetStatus().Message);
    }

    private bool CanUseObservationFastPath(
        bool includeLiveReadings,
        bool powerSourceConfirmed)
    {
        ServiceStatusSnapshot current = GetStatus();
        return includeLiveReadings
            && powerSourceConfirmed
            && current.DeviceVerified
            && !current.WritesBlocked
            && fullObservationCompleted
            && Stopwatch.GetElapsedTime(lastFullObservationTimestamp)
                < FullObservationInterval;
    }

    private async Task ObserveFastPathAsync(
        PowerSource powerSource,
        LiveReadingSensors requestedLiveReadings,
        int? batteryPowerFlowMilliwatts,
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (!deviceProvider.TryGetRecentlyVerifiedReadOnlyDevice(out IBladeDevice device))
        {
            // Preserve the existing one-miss debounce, but force the next poll
            // through the full observation path so stale availability and
            // renderer ownership cannot survive until the audit interval.
            fullObservationCompleted = false;
            consecutiveVerifiedAccessFailures = 0;
            if (current.DeviceVerified
                && ++consecutiveDeviceProbeFailures < ObservationFailureThreshold)
            {
                PublishTransientDeviceProbeFailure(
                    powerSource,
                    new LiveReadings(batteryPowerFlowMilliwatts, null, null));
            }
            else
            {
                PublishObservationUnavailable(
                    powerSource,
                    new LiveReadings(batteryPowerFlowMilliwatts, null, null),
                    "No verified PID 02E0 device is available.");
            }
            return;
        }

        consecutiveDeviceProbeFailures = 0;
        FanRpmReadings? fans = null;
        FanTemperatureReadings temperatures = default;
        if (Requests(requestedLiveReadings, LiveReadingSensors.FanSpeeds))
        {
            try
            {
                fans = await device.ReadFanRpmAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep the confirmed control state while one optional sensor is unavailable.
            }
        }

        bool includeCpu = Requests(
            requestedLiveReadings,
            LiveReadingSensors.CpuTemperature);
        bool includeGpu = Requests(
            requestedLiveReadings,
            LiveReadingSensors.GpuTemperature);
        if (includeCpu || includeGpu)
        {
            try
            {
                temperatures = await fanTemperatureSource.ReadAsync(
                        includeCpu,
                        includeGpu,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep the confirmed control state while one optional sensor is unavailable.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        PublishIfChanged(GetStatus() with
        {
            ActivePowerSource = powerSource,
            LiveReadings = new LiveReadings(
                batteryPowerFlowMilliwatts,
                fans?.Selector1Rpm,
                fans?.Selector2Rpm,
                temperatures.CpuCelsius,
                temperatures.GpuCelsius),
        });
    }

    private static bool Requests(
        LiveReadingSensors requested,
        LiveReadingSensors sensor) => (requested & sensor) != 0;

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

    private static async Task<bool?> ReadStartupAnimationStateAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadStartupAnimationAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<FunctionKeyPrimaryMode?> ReadFunctionKeyPrimaryStateAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadFunctionKeyPrimaryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool?> ReadGamingModeStateAsync(
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            return await device.ReadGamingModeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            return null;
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
            CompareTransitionAutomaticPerformance(
                confirmed,
                desired,
                alternateExpected);
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

        CompareInitialAutomaticPerformance(confirmed, desired);
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
        if (preferences.AlwaysOverrideExternalLightingChanges)
        {
            exclusions.Remove(DeviceSubsystem.Lighting);
        }
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }

        return exclusions;
    }

    private void ValidateThermalOverrideCombination(
        PerformanceOverride? performanceOverride,
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

    private void CompareInitialAutomaticPerformance(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState desired)
    {
        if (confirmed.Performance is null
            || !initialAutomaticComparisons.Add(DeviceSubsystem.Performance))
        {
            return;
        }

        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Performance,
            PerformanceMatchesDesired(confirmed, desired));
    }

    private void CompareTransitionAutomaticPerformance(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState priorDesired,
        DesiredDeviceState newDesired)
    {
        if (confirmed.Performance is null)
        {
            return;
        }

        initialAutomaticComparisons.Add(DeviceSubsystem.Performance);
        driftTracker.ConfirmIntentional(
            DeviceSubsystem.Performance,
            PerformanceMatchesDesired(confirmed, priorDesired)
                || PerformanceMatchesDesired(confirmed, newDesired));
    }

    private static bool PerformanceMatchesDesired(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState desired) =>
        confirmed.Performance == desired.Performance.Value
        && (confirmed.Performance != PerformancePreset.Custom
            || confirmed.CustomPerformance == desired.CustomPerformance);

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

    private static bool HasAnyControlCapability(DeviceCapabilities capabilities) =>
        capabilities.PerformancePresets.Count > 0
        || capabilities.FanModes.Count > 0
        || capabilities.LightingEffects.Count > 0
        || capabilities.LogoLightingModes.Count > 0
        || capabilities.ChargeLimits.Count > 0
        || capabilities.OneShotLaptopControls.Count > 0
        || capabilities.SupportsBatteryProtectionOff
        || capabilities.SupportsTemporaryFullCharge;

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
            FanControlOverride = Automation.FanControlOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
            // A verified session read can fail while the independently-owned
            // matrix renderer remains healthy. Preserve renderer ownership and
            // the last confirmed one-shot states so status polling cannot create
            // a false key-input dead zone or flash unrelated controls unknown.
            LightingRendererOwnership = deviceVerified
                ? current.LightingRendererOwnership
                : LightingRendererOwnershipState.None,
            StartupAnimationEnabled = deviceVerified
                ? current.StartupAnimationEnabled
                : null,
            FunctionKeyPrimary = deviceVerified
                ? current.FunctionKeyPrimary
                : null,
            GamingModeEnabled = deviceVerified
                ? current.GamingModeEnabled
                : null,
            KeyboardDriverModeActive = deviceVerified
                ? current.KeyboardDriverModeActive
                : null,
        });
    }

    private void PublishTransientDeviceProbeFailure(
        PowerSource powerSource,
        LiveReadings? liveReadings)
    {
        ServiceStatusSnapshot current = GetStatus();
        // Do not flash all controls unavailable for one missed enumeration.
        // Hardware operations independently re-admit PID 02E0 before access;
        // a second consecutive miss still publishes the fail-closed state.
        PublishIfChanged(current with
        {
            Message = "Checking the laptop connection after a temporary device-access failure.",
            ActivePowerSource = powerSource,
            LiveReadings = liveReadings,
        });
    }

    private void PublishTransientVerifiedAccessFailure(
        PowerSource powerSource,
        LiveReadings? liveReadings,
        string detail)
    {
        ServiceStatusSnapshot current = GetStatus();
        PublishIfChanged(current with
        {
            Message = $"Refreshing laptop controls after a temporary device-access failure. {detail}",
            ActivePowerSource = powerSource,
            LiveReadings = liveReadings,
        });
    }

    private ServiceStatusSnapshot CreateInitialSnapshot(PowerSource powerSource)
    {
        BatteryHealthStatus? batteryHealth = powerStateProvider.CaptureBatteryHealth();
        batteryHealthVerificationRetryPending = batteryHealth is null;
        return new(
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
            LightingSuppressions: [],
            AlwaysOverrideExternalLightingChanges:
                preferences.AlwaysOverrideExternalLightingChanges,
            BatteryHealth: batteryHealth)
        {
            FanControlOverride = Automation.FanControlOverride,
        };
    }

    private void RefreshBatteryHealthAfterPowerSourceChangeCore()
    {
        if (GetStatus().BatteryHealth is not null)
        {
            return;
        }

        BatteryHealthStatus? batteryHealth = powerStateProvider.CaptureBatteryHealth();
        if (batteryHealth is not null)
        {
            PublishIfChanged(GetStatus() with { BatteryHealth = batteryHealth });
        }
    }

    private BatteryHealthStatus? CaptureBatteryHealthAfterFirstVerificationCore()
    {
        BatteryHealthStatus? current = GetStatus().BatteryHealth;
        if (!batteryHealthVerificationRetryPending)
        {
            return current;
        }

        batteryHealthVerificationRetryPending = false;
        return current ?? powerStateProvider.CaptureBatteryHealth();
    }

    private ControlOperationResult PublishScopedUnavailable(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        string reason,
        bool failed = false,
        bool preserveCurrentAvailability = false)
    {
        ControlResultStatus resultStatus = failed
            ? ControlResultStatus.Failed
            : ControlResultStatus.Blocked;
        var result = new ControlResult(
            resultStatus,
            $"Hardware application was not confirmed. {reason}");
        history.RecordSubsystemResult(subsystem, result);
        ServiceStatusSnapshot current = GetStatus();
        PublishScopedStatus(
            powerSource,
            current.ConfirmedState,
            current.Capabilities,
            result.Message,
            deviceVerified: preserveCurrentAvailability && current.DeviceVerified,
            writesBlocked: preserveCurrentAvailability
                ? current.WritesBlocked
                : true);
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
            FanControlOverride = Automation.FanControlOverride,
            LightingOverride = Automation.LightingOverride,
            FullChargeOverride = ResolveFullChargeOverride(confirmed),
            LightingRendererOwnership = deviceVerified
                ? current.LightingRendererOwnership
                : LightingRendererOwnershipState.None,
            StartupAnimationEnabled = deviceVerified
                ? current.StartupAnimationEnabled
                : null,
            FunctionKeyPrimary = deviceVerified
                ? current.FunctionKeyPrimary
                : null,
            GamingModeEnabled = deviceVerified
                ? current.GamingModeEnabled
                : null,
            KeyboardDriverModeActive = deviceVerified
                ? current.KeyboardDriverModeActive
                : null,
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
        LightingPowerPolicy policy = Automation.Profiles.LightingPowerPolicyFor(
            Automation.PowerSource);
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

    private void PublishIfChanged(
        ServiceStatusSnapshot candidate,
        bool retainPerformanceShortcutPreview = true)
        => PublishStatusUpdate(previous => retainPerformanceShortcutPreview
            ? candidate with
            {
                PerformanceShortcutPreview =
                    previous.PerformanceShortcutPreview,
            }
            : candidate);

    private void PublishStatusUpdate(
        Func<ServiceStatusSnapshot, ServiceStatusSnapshot> update)
    {
        ServiceStatusSnapshot? published = null;
        bool publishToSubscribers = false;
        lock (statusPublishGate)
        {
            ServiceStatusSnapshot previous = GetStatus();
            ServiceStatusSnapshot candidate = update(previous);
            if (Equivalent(previous, candidate))
            {
                return;
            }

            published = candidate with { Revision = previous.Revision + 1 };
            Volatile.Write(ref status, published);
            publishToSubscribers = !Equivalent(
                previous,
                candidate with { LiveReadings = previous.LiveReadings });
        }

        if (publishToSubscribers)
        {
            subscriptionHub.Publish(published!);
        }
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
        && left.PerformanceShortcutPreview == right.PerformanceShortcutPreview
        && left.FanOverride == right.FanOverride
        && left.FanControlOverride == right.FanControlOverride
        && left.LightingOverride == right.LightingOverride
        && left.FullChargeOverride == right.FullChargeOverride
        && left.StartupAnimationEnabled == right.StartupAnimationEnabled
        && left.FunctionKeyPrimary == right.FunctionKeyPrimary
        && left.GamingModeEnabled == right.GamingModeEnabled
        && left.KeyboardDriverModeActive == right.KeyboardDriverModeActive
        && left.AlwaysOverrideExternalLightingChanges
            == right.AlwaysOverrideExternalLightingChanges
        && left.LightingRendererOwnership == right.LightingRendererOwnership
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && left.BatteryHealth == right.BatteryHealth
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
        if ((current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
                && !preferences.AlwaysOverrideExternalLightingChanges)
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

        await TryPersistLastKnownLightingAsync(requested, cancellationToken)
            .ConfigureAwait(false);

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

    private async Task PersistLastKnownLightingAsync(
        LightingSettings lighting,
        CancellationToken cancellationToken)
    {
        lighting = LightingSettingsFactory.NormalizeLegacyOff(lighting);
        if (preferences.LastKnownLighting == lighting)
        {
            return;
        }

        BatteryAutomationPreferences staged = preferences with
        {
            LastKnownLighting = lighting,
        };
        validator.ValidatePreferences(staged);
        await store.SaveConfigurationAsync(
            Automation.Profiles,
            staged,
            cancellationToken).ConfigureAwait(false);
        preferences = staged;
    }

    private async Task TryPersistLastKnownLightingAsync(
        LightingSettings lighting,
        CancellationToken cancellationToken)
    {
        try
        {
            await PersistLastKnownLightingAsync(lighting, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"The last lighting effect could not be saved: {exception.Message}");
        }
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
        LightingRendererOwnershipState.Wheel or
        LightingRendererOwnershipState.Ripple or
        LightingRendererOwnershipState.AmbientAwareness;

    private static bool IsKeyTriggeredLightingOwnership(
        LightingRendererOwnershipState ownership) => ownership is
        LightingRendererOwnershipState.Reactive or
        LightingRendererOwnershipState.Ripple;

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
            LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
            LightingEffect.AmbientAwareness => LightingRendererOwnershipState.AmbientAwareness,
            _ => LightingRendererOwnershipState.None,
        });

    private sealed class UnavailableFirmwareVersionSource : IFirmwareVersionSource
    {
        internal static UnavailableFirmwareVersionSource Instance { get; } = new();

        public FirmwareVersions Capture() => FirmwareVersions.Unavailable();
    }

    private sealed record OneShotLaptopControlReadback(
        bool? StartupAnimation,
        FunctionKeyPrimaryMode? FunctionKeyPrimary,
        bool? GamingMode);

    private sealed record FullDeviceObservation(
        DeviceCapabilities Capabilities,
        ConfirmedDeviceState Confirmed,
        bool? StartupAnimation,
        FunctionKeyPrimaryMode? FunctionKeyPrimary,
        bool? GamingModeEnabled,
        LightingRendererOwnershipState LightingOwnership,
        FirmwareVersions? Firmware,
        FanRpmReadings? Fans,
        FanTemperatureReadings Temperatures);

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

    private static void ValidateOverride<T>(ManualOverride<T>? value, string parameterName)
        where T : struct, Enum
    {
        if (value is not null
            && (!Enum.IsDefined(value.Value) || !Enum.IsDefined(value.Lifetime)))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateOverride(PerformanceOverride? value, string parameterName)
    {
        if (value is not null && !value.IsWellFormed())
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
