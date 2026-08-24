using System.Diagnostics;
using System.Security.Principal;
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

    void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides,
        bool recoveringUnexpectedExit) =>
        throw new NotSupportedException(
            "This runtime does not implement unexpected-exit startup classification.");

    ServiceStatusSnapshot GetStatus();

    Rz090581CapabilityDiagnostics? GetCachedRz090581CapabilityDiagnostics() => null;

    Task ActivateUserAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task ActivateUserAsync(
        SecurityIdentifier userSid,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken) => stillControlsSession()
            ? ActivateUserAsync(userSid, cancellationToken)
            : Task.CompletedTask;

    Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "HyperBoost control is unavailable."));

    Task<ControlOperationResult> ValidatePadlessHyperBoostLifecycleAsync(
        ValidatePadlessHyperBoostLifecycleRequest request,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Pad-less HyperBoost lifecycle validation is unavailable."));


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

    Task<ControlOperationResult> SetDisplayBrightnessHidUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Native display-brightness input is unavailable."));

    Task<ControlOperationResult> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Keyboard lighting ownership selection is unavailable."));

    Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "External lighting override policy is unavailable."));

    Task<ControlOperationResult> SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Selectable lighting refresh rates are unavailable."));

    Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Firmware compatibility override is unavailable."));

    Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Changed settings cannot be reapplied."));

    Task<ControlOperationResult> RecoverHyperBoostRestoreAsync(
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The saved HyperBoost restore cannot be retried."));

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

    // Observation and power classification can reconcile profiles and recover
    // lighting. Callers must treat both entry points as write-capable.
    Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken);

    Task ObserveAsync(
        LiveReadingSensors requestedLiveReadings,
        CancellationToken cancellationToken) => ObserveAsync(
            requestedLiveReadings != LiveReadingSensors.None,
            cancellationToken);

    Task ReconcileStartupProfileAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task ReconcileUnexpectedExitAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task PrepareManualFanCurveRecoveryAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

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

    Task<ControlOperationResult> PrepareForLowPowerAsync(
        CancellationToken cancellationToken) => PrepareForShutdownAsync(cancellationToken);

    Task<ControlOperationResult> PrepareOwnedHyperBoostForDisplayOffAsync(
        CancellationToken cancellationToken) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Stored,
                "OpenBlade-owned HyperBoost was left unchanged."));

    Task BeginResumeValidationAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task CancelResumeValidationAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task CompleteResumeValidationAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task InvalidateLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RestoreLightingRendererAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task MaintainFanCurveAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal enum PadlessHyperBoostLifecycleRunClaim
{
    Claimed,
    DeviceUnavailable,
    AlreadyUsed,
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
    // Five full open/query/close HID sessions permit two agreeing known replies
    // despite up to three transient failures, while bounding every classification
    // path, including the read-before-write paths used by control commands.
    private const int MaximumAdapterPowerClassificationSamples = 5;
    private const int RequiredAgreeingAdapterPowerSamples = 2;
    // Allow slow HID re-enumeration without carrying resume attribution into a
    // later, unrelated device or power lifecycle.
    internal static readonly TimeSpan ResumeValidationObservationWindow =
        TimeSpan.FromMinutes(1);
    private const int ObservationFailureThreshold = 3;
    private const int Rz090581CapabilityDiagnosticsReadAttemptLimit = 3;
    private const int MaximumMcuVersionReadAttempts = 3;
    private static readonly ConfirmedDeviceState UnknownConfirmedState =
        new(null, null, null, null);
    private static readonly DeviceCapabilities UnavailableCapabilities =
        DeviceCapabilities.Unavailable();
    private static readonly HashSet<string> CompetingControllerProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RazerAppEngine",
            "Razer Synapse Service",
        };

    private UserConfigurationStores userStores;
    private readonly IUserConfigurationRepository? userConfigurationRepository;
    private readonly IPowerStateProvider powerStateProvider;
    private readonly IStatusSubscriptionHub subscriptionHub;
    private readonly IVerifiedBladeDeviceProvider deviceProvider;
    private readonly IControllerPresenceMonitor controllerPresenceMonitor;
    private readonly DiagnosticHistory history;
    private readonly LowPowerGate lowPowerGate;
    private readonly IFirmwareVersionSource firmwareVersionSource;
    private readonly IDisplayBrightnessHidController displayBrightnessHidController;
    private readonly Func<TimeSpan, CancellationToken, Task> transitionRetryDelay;
    private readonly Func<CancellationToken, Task> hyperBoostLifecycleExitBoundary;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ProfileSetValidator validator;
    private readonly object initializationGate = new();
    private readonly object statusPublishGate = new();
    private readonly object pendingWriteConfirmationGate = new();
    private ProfileAutomationState? automation;
    private volatile BatteryAutomationPreferences preferences =
        new();
    private PersistentOverrideState persistentOverrides = PersistentOverrideState.Empty;
    private ConfirmedStateDriftTracker driftTracker = new();
    private readonly HashSet<DeviceSubsystem> initialAutomaticComparisons = [];
    // Process-local by design. Service startup establishes a fresh read-only
    // baseline before startup reconciliation can apply saved intent.
    private readonly Dictionary<DeviceSubsystem, PendingWriteConfirmation>
        pendingWriteConfirmations = [];
    private ServiceStatusSnapshot? status;
    private int rz090581CapabilityDiagnosticsReadAttempts;
    private Rz090581CapabilityDiagnostics? rz090581CapabilityDiagnostics;
    private int mcuVersionReadAttempts;
    private LightingSettings? lastOwnedLightingSettings;
    private FanControlSettings? lastOwnedFanControlSettings;
    // Process-local by design. A first observation of mode 7 does not establish
    // ownership; only a successful OpenBlade enable with matching readback does.
    private bool ownsHyperBoost;
    private PerformancePreset? ownedHyperBoostBaseMode;
    private HyperBoostRestoreIntent? ownedHyperBoostRestoreIntent;
    private HyperBoostRestoreIntent? pendingIndeterminateHyperBoostEntryIntent;
    private PerformancePreset pendingIndeterminateHyperBoostEntryBaseMode =
        PerformancePreset.Balanced;
    private HyperBoostRestoreIntent? pendingIndeterminateHyperBoostExitIntent;
    private bool pendingOwnedHyperBoostPowerBoundaryExit;
    private bool ownedHyperBoostPowerBoundaryExitSubmitted;
    private bool ownedHyperBoostRequiresCoolingPad;
    private bool hyperBoostJournalSafetyBlock;
    private bool hyperBoostRestorePending;
    private HyperBoostOwnershipJournal? lastReportedRetainedHyperBoostRestoreJournal;
    private readonly HashSet<Guid> padlessHyperBoostLifecycleValidationRuns = [];
    private long hyperBoostOwnershipGeneration;
    private long powerSourceGeneration;
    private LightingSettings? pendingLightingRestore;
    private bool pendingLightingRestoreFromLifecycle;
    private LightingSettings? faultedLightingRecoveryCandidate;
    private readonly HashSet<LightingSuppressionReason> lightingSuppressions = [];
    private bool lightingSuppressionWritePending;
    private bool explicitLightingOwnershipActive;
    private string? lightingRecoveryPauseMessage;
    private readonly IFanTemperatureSource fanTemperatureSource;
    private readonly ICoolingPadDiscoverySource hyperBoostPadDiscoverySource;
    private readonly IHyperBoostOwnershipJournalStore hyperBoostOwnershipJournalStore;
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
    private bool startupProfileReconciliationPending;
    private bool startupProfileReconciliationRequested;
    private bool unexpectedExitRecoveryPending;
    private bool manualFanCurveRecoveryPending;
    private bool unexpectedExitRecoveryDeferredForUserConfiguration;
    private DeferredManualFanSafetyState deferredManualFanSafetyState;
    private bool recoveryUserConfigurationLoaded;
    private bool suppressInitialUserProfileApplyAfterUnexpectedExit;
    private bool suppressInitialKnownPowerSourceApplyAfterUnexpectedExit;
    private string? activeUserSid;
    private ResumeStateBaseline? pendingResumeStateBaseline;
    private DisplayBrightnessHidUsage assertedDisplayBrightnessUsage;
    private bool displayBrightnessReleaseRequired;
    private bool displayBrightnessNeutralizationPending = true;
    private AdapterPowerClassificationDiagnosticKind?
        lastAdapterPowerClassificationDiagnosticKind;

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            lowPowerGate,
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
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            lowPowerGate,
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
        LowPowerGate lowPowerGate,
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
            lowPowerGate,
            controllerPresenceMonitor,
            firmwareVersionSource,
            static (delay, token) => Task.Delay(delay, token),
            validator)
    {
    }

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource,
        ProfileSetValidator validator,
        IUserConfigurationRepository userConfigurationRepository)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            lowPowerGate,
            controllerPresenceMonitor,
            firmwareVersionSource,
            static (delay, token) => Task.Delay(delay, token),
            validator,
            fanTemperatureSource: null,
            userConfigurationRepository: userConfigurationRepository)
    {
    }

    public ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource,
        ProfileSetValidator validator,
        IUserConfigurationRepository userConfigurationRepository,
        IDisplayBrightnessHidController displayBrightnessHidController)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            lowPowerGate,
            controllerPresenceMonitor,
            firmwareVersionSource,
            static (delay, token) => Task.Delay(delay, token),
            validator,
            fanTemperatureSource: null,
            userConfigurationRepository: userConfigurationRepository,
            displayBrightnessHidController: displayBrightnessHidController)
    {
    }

    internal ServiceControlRuntime(
        BatteryConfigurationStore store,
        PersistentOverrideStore overrideStore,
        IPowerStateProvider powerStateProvider,
        IStatusSubscriptionHub subscriptionHub,
        IVerifiedBladeDeviceProvider deviceProvider,
        DiagnosticHistory history,
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor,
        Func<TimeSpan, CancellationToken, Task> transitionRetryDelay)
        : this(
            store,
            overrideStore,
            powerStateProvider,
            subscriptionHub,
            deviceProvider,
            history,
            lowPowerGate,
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
        LowPowerGate lowPowerGate,
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
            lowPowerGate,
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
        LowPowerGate lowPowerGate,
        IControllerPresenceMonitor controllerPresenceMonitor,
        IFirmwareVersionSource firmwareVersionSource,
        Func<TimeSpan, CancellationToken, Task> transitionRetryDelay,
        ProfileSetValidator validator,
        IFanTemperatureSource? fanTemperatureSource = null,
        TimeProvider? timeProvider = null,
        IUserConfigurationRepository? userConfigurationRepository = null,
        IDisplayBrightnessHidController? displayBrightnessHidController = null,
        ICoolingPadDiscoverySource? hyperBoostPadDiscoverySource = null,
        IHyperBoostOwnershipJournalStore? hyperBoostOwnershipJournalStore = null,
        Func<CancellationToken, Task>? hyperBoostLifecycleExitBoundary = null)
    {
        userStores = new UserConfigurationStores(
            store ?? throw new ArgumentNullException(nameof(store)),
            overrideStore ?? throw new ArgumentNullException(nameof(overrideStore)));
        this.powerStateProvider = powerStateProvider
            ?? throw new ArgumentNullException(nameof(powerStateProvider));
        this.subscriptionHub = subscriptionHub
            ?? throw new ArgumentNullException(nameof(subscriptionHub));
        this.deviceProvider = deviceProvider
            ?? throw new ArgumentNullException(nameof(deviceProvider));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.lowPowerGate = lowPowerGate
            ?? throw new ArgumentNullException(nameof(lowPowerGate));
        this.controllerPresenceMonitor = controllerPresenceMonitor
            ?? throw new ArgumentNullException(nameof(controllerPresenceMonitor));
        this.firmwareVersionSource = firmwareVersionSource
            ?? throw new ArgumentNullException(nameof(firmwareVersionSource));
        this.transitionRetryDelay = transitionRetryDelay
            ?? throw new ArgumentNullException(nameof(transitionRetryDelay));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.userConfigurationRepository = userConfigurationRepository;
        this.fanTemperatureSource = fanTemperatureSource
            ?? new SafeWindowsFanTemperatureSource();
        this.displayBrightnessHidController = displayBrightnessHidController
            ?? UnavailableDisplayBrightnessHidController.Instance;
        this.hyperBoostPadDiscoverySource = hyperBoostPadDiscoverySource
            ?? CoolingPadDiscoveryFactory.CreateWindows();
        this.hyperBoostOwnershipJournalStore = hyperBoostOwnershipJournalStore
            ?? UnavailableHyperBoostOwnershipJournalStore.Instance;
        this.hyperBoostLifecycleExitBoundary = hyperBoostLifecycleExitBoundary
            ?? (static _ => Task.CompletedTask);
    }

    public void Initialize(BatteryConfiguration configuration, PowerSource powerSource) =>
        Initialize(configuration, powerSource, PersistentOverrideState.Empty);

    public void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides)
        => Initialize(
            configuration,
            powerSource,
            persistentOverrides,
            recoveringUnexpectedExit: false);

    public void Initialize(
        BatteryConfiguration configuration,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides,
        bool recoveringUnexpectedExit)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ProfileSet profiles = ProfileStore.NormalizeLegacyBatteryProfile(
            configuration.Profiles,
            out _,
            batteryProtectionDefault: null);
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
            deviceProvider.SetAllowUntestedFirmware(
                configuration.Preferences.AllowUntestedFirmware);
            deviceProvider.SetLightingRefreshRate(
                configuration.Preferences.LightingRefreshRate);
            this.persistentOverrides = persistentOverrides;
            startupProfileReconciliationPending = true;
            startupProfileReconciliationRequested = false;
            unexpectedExitRecoveryPending = recoveringUnexpectedExit;
            manualFanCurveRecoveryPending = recoveringUnexpectedExit;
            // Production profiles and overrides become authoritative only after
            // an authorized active user is known. The machine bootstrap must not
            // classify retained user-owned hardware as external drift.
            unexpectedExitRecoveryDeferredForUserConfiguration =
                recoveringUnexpectedExit && userConfigurationRepository is not null;
            deferredManualFanSafetyState =
                unexpectedExitRecoveryDeferredForUserConfiguration
                    ? DeferredManualFanSafetyState.PendingUnowned
                    : DeferredManualFanSafetyState.Lost;
            recoveryUserConfigurationLoaded = false;
            suppressInitialUserProfileApplyAfterUnexpectedExit = recoveringUnexpectedExit;
            suppressInitialKnownPowerSourceApplyAfterUnexpectedExit =
                recoveringUnexpectedExit && powerSource == PowerSource.ExternalUnknown;
            pendingLightingRestore = recoveringUnexpectedExit
                && !unexpectedExitRecoveryDeferredForUserConfiguration
                ? configuration.Preferences.LastKnownLighting
                : initializedAutomation
                    .Resolve(UnknownConfirmedState)
                    .Lighting
                    .Value;
            Volatile.Write(ref status, CreateInitialSnapshot(powerSource));
        }
    }

    public ServiceStatusSnapshot GetStatus() => Volatile.Read(ref status)
        ?? throw new InvalidOperationException("The service runtime is not initialized.");

    public Rz090581CapabilityDiagnostics? GetCachedRz090581CapabilityDiagnostics() =>
        Volatile.Read(ref rz090581CapabilityDiagnostics);

    public async Task ActivateUserAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken) => await ActivateUserAsync(
            userSid,
            static () => true,
            cancellationToken).ConfigureAwait(false);

    public async Task ActivateUserAsync(
        SecurityIdentifier userSid,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        if (userConfigurationRepository is null)
        {
            return;
        }

        bool alreadyActive = string.Equals(
            Volatile.Read(ref activeUserSid),
            userSid.Value,
            StringComparison.OrdinalIgnoreCase);
        if (alreadyActive)
        {
            if (!unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                return;
            }

            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (string.Equals(
                        activeUserSid,
                        userSid.Value,
                        StringComparison.OrdinalIgnoreCase)
                    && stillControlsSession())
                {
                    await TryCompleteDeferredUnexpectedExitRecoveryCoreAsync(
                        stillControlsSession,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                operationGate.Release();
            }

            return;
        }

        UserConfigurationContext context = await userConfigurationRepository
            .LoadAsync(userSid, cancellationToken)
            .ConfigureAwait(false);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(
                    activeUserSid,
                    userSid.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!stillControlsSession())
            {
                return;
            }

            ProfileSet profiles = ProfileStore.NormalizeLegacyBatteryProfile(
                context.Configuration.Profiles,
                out _,
                batteryProtectionDefault: null);
            PersistentOverrideState overrides =
                PersistentOverrideStore.NormalizeLegacyFanControl(
                    PersistentOverrideStore.NormalizeLegacyLighting(context.Overrides));
            validator.Validate(profiles);
            validator.ValidatePreferences(context.Configuration.Preferences);
            PersistentOverrideStore.Validate(overrides);

            ServiceStatusSnapshot current = GetStatus();
            PowerSource powerSource = current.ActivePowerSource;
            bool suppressProfileApply =
                suppressInitialUserProfileApplyAfterUnexpectedExit
                || unexpectedExitRecoveryDeferredForUserConfiguration;
            if (!unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                suppressInitialUserProfileApplyAfterUnexpectedExit = false;
            }
            if (!suppressProfileApply)
            {
                explicitLightingOwnershipActive = false;
                lightingRecoveryPauseMessage = null;
                RefreshControllerPresenceCore(cancellationToken);
                current = GetStatus();
                // An incomplete controller check must still stop the old user's
                // renderer, but retaining the port avoids an unsafe Driver-mode
                // cleanup write while ownership is uncertain.
                bool shouldRelinquishRendererAndRetainLightingPort =
                    context.Configuration.Preferences.KeyboardLightingControlMode
                        == KeyboardLightingControlMode.OpenBlade
                    && !context.Configuration.Preferences
                        .AlwaysOverrideExternalLightingChanges
                    && ControllerMayOwnLighting(current);
                if (shouldRelinquishRendererAndRetainLightingPort)
                {
                    _ = await RelinquishLightingOwnershipCoreAsync(
                        current,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await InvalidateLightingRendererCoreAsync(
                        preserveForLifecycleRestore: false,
                        cancellationToken).ConfigureAwait(false);
                }
                current = GetStatus();
            }
            Volatile.Write(
                ref userStores,
                new UserConfigurationStores(context.Store, context.OverrideStore));
            automation = new ProfileAutomationState(
                profiles,
                context.Configuration.Preferences,
                powerSource,
                overrides);
            preferences = context.Configuration.Preferences;
            deviceProvider.SetAllowUntestedFirmware(
                context.Configuration.Preferences.AllowUntestedFirmware);
            deviceProvider.SetLightingRefreshRate(
                context.Configuration.Preferences.LightingRefreshRate);
            persistentOverrides = overrides;
            lightingSuppressions.Clear();
            lightingSuppressionWritePending = false;
            if (unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                recoveryUserConfigurationLoaded = true;
                pendingLightingRestore = context.Configuration.Preferences.LastKnownLighting;
                pendingLightingRestoreFromLifecycle = false;
            }
            if (!suppressProfileApply)
            {
                pendingLightingRestore = null;
                pendingLightingRestoreFromLifecycle = false;
                await RefreshControllerPresenceAndRelinquishLightingAsync(
                    cancellationToken).ConfigureAwait(false);
            }

            ConfirmedDeviceState confirmed = current.ConfirmedState ?? UnknownConfirmedState;
            if (!suppressProfileApply
                && powerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
            {
                await ApplyFullTransitionAsync(
                        powerSource,
                        cancellationToken,
                        preserveObservedExternalChanges: false)
                    .ConfigureAwait(false);
            }

            current = GetStatus();
            confirmed = current.ConfirmedState ?? UnknownConfirmedState;
            PublishIfChanged(current with
            {
                Message = suppressProfileApply
                    ? "The active Windows user's profile is loaded without hardware application during unexpected-exit recovery."
                    : "The active Windows user's profile is loaded.",
                Profiles = profiles,
                DesiredState = Automation.Resolve(confirmed),
                PerformanceOverride = Automation.PerformanceOverride,
                FanOverride = Automation.FanOverride,
                FanControlOverride = Automation.FanControlOverride,
                LightingOverride = Automation.LightingOverride,
                BatteryPolicyOverride = Automation.BatteryPolicyOverride,
                BatteryProtectionOverride = Automation.BatteryProtectionOverride,
                FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
                LightingSuppressions = CurrentLightingSuppressions(),
                AlwaysOverrideExternalLightingChanges =
                    context.Configuration.Preferences
                        .AlwaysOverrideExternalLightingChanges,
                LightingRefreshRate =
                    EffectiveLightingRefreshRate(current.Capabilities),
                KeyboardLightingControlMode =
                    context.Configuration.Preferences.KeyboardLightingControlMode,
                AllowUntestedFirmware =
                    context.Configuration.Preferences.AllowUntestedFirmware,
                PerformanceShortcutPreview = null,
            });

            if (unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                await TryCompleteDeferredUnexpectedExitRecoveryCoreAsync(
                    stillControlsSession,
                    cancellationToken).ConfigureAwait(false);
            }

            if (stillControlsSession())
            {
                Volatile.Write(ref activeUserSid, userSid.Value);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

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
                profiles = ProfileStore.NormalizeLegacyBatteryProfile(
                    profiles,
                    out _,
                    batteryProtectionDefault: null);
                validator.Validate(profiles);
                await ConfigurationStore.SaveProfilesAsync(profiles, token);
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
                BatteryPolicyOverride = Automation.BatteryPolicyOverride,
                BatteryProtectionOverride = Automation.BatteryProtectionOverride,
                FullChargeOverride = ResolveFullChargeOverride(current.ConfirmedState),
                LightingSuppressions = CurrentLightingSuppressions(),
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);

            if (lightingWasSuppressed && lightingSuppressions.Count == 0)
            {
                await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                    .ConfigureAwait(false);
                current = GetStatus();
                if (current.ActivePowerSource is
                        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                    && !ShouldYieldAutomaticLighting(current))
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
                    desired,
                    cancellationToken),
            token,
            value is null
                ? "Performance override was cleared."
                : "Performance override intent was stored.",
            deferHardwareApply: DeferredPerformanceIntentMessage);

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
            if (IsWritePendingFor(DeviceSubsystem.Performance))
            {
                return PendingWriteBlockedResult(DeviceSubsystem.Performance);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Performance,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; the performance shortcut is blocked.",
                    preserveCurrentAvailability: true);
            }

            if (ChangePowerSourceCore(powerSource))
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
                    : null)
            {
                GpuClockTuning = current.DesiredState.GpuClockTuning,
            };
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

            ControlOperationResult result;
            string? deferredMessage = DeferredPerformanceIntentMessage(
                powerSource,
                current);
            if (deferredMessage is not null)
            {
                try
                {
                    await SetPerformanceOverrideCoreAsync(stagedOverride, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsIntentFailure(exception))
                {
                    return RecordRejectedIntent(exception);
                }

                history.RecordSummary(ControlOperationStatus.Stored, deferredMessage);
                PublishScopedStatus(
                    powerSource,
                    current.ConfirmedState,
                    current.Capabilities,
                    deferredMessage,
                    current.DeviceVerified,
                    current.WritesBlocked);
                result = new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    deferredMessage);
            }
            else
            {
                result = await ApplyScopedIntentAsync(
                    DeviceSubsystem.Performance,
                    powerSource,
                    (coordinator, desired, cancellationToken) =>
                        coordinator.ApplyPerformanceOutcomeAsync(
                            desired,
                            cancellationToken),
                    token,
                    desired => desired with
                    {
                        Performance = new ResolvedSetting<PerformancePreset>(
                            next,
                            SettingValueSource.TemporaryOverride),
                        CustomPerformance = stagedOverride.CustomPerformance,
                        GpuClockTuning = stagedOverride.GpuClockTuning,
                    },
                    cancellationToken => SetPerformanceOverrideCoreAsync(
                        stagedOverride,
                        cancellationToken)).ConfigureAwait(false);
            }
            if (result.Status is ControlOperationStatus.Applied or ControlOperationStatus.Stored)
            {
                string selectedBy = source == PerformanceShortcutSource.DedicatedButton
                    ? $"The performance button selected {next}."
                    : $"Fn+P selected {next}.";
                string message = result.Status == ControlOperationStatus.Stored
                    ? $"{selectedBy} It will be applied when fans return to Automatic."
                    : selectedBy;
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
        if (GetStatus().ConfirmedState?.HyperBoostActive == true
            && value is not null
            && (settings ?? FanControlSettings.FromLegacy(value.Value)).Mode
                != FanMode.Automatic)
        {
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Laptop fan overrides must remain Automatic while HyperBoost is active."));
        }

        FanControlSettings? previous = null;
        PerformancePreset performanceIntent = PerformancePreset.Balanced;
        CustomPerformanceSettings? customPerformanceIntent = null;
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
                DesiredDeviceState intended = Automation.Resolve(
                    GetStatus().ConfirmedState ?? UnknownConfirmedState);
                performanceIntent = intended.Performance.Value;
                customPerformanceIntent = intended.CustomPerformance;
            },
            (coordinator, desired, cancellationToken) =>
            {
                FanControlSettings requested = desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value);
                return coordinator.ApplyFanControlTransitionOutcomeAsync(
                    previous ?? requested,
                    requested,
                    performanceIntent,
                    customPerformanceIntent,
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
            || snapshot.ConfirmedState?.HyperBoostActive == true
            || !snapshot.DeviceVerified
            || snapshot.WritesBlocked
            || unexpectedExitRecoveryDeferredForUserConfiguration
            || manualFanCurveRecoveryPending
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
            temperatures,
            snapshot.Capabilities?.FanSpeedRange?.MaximumRpm
                ?? FanControlSettings.MaximumSpeedRpm);
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
                || unexpectedExitRecoveryDeferredForUserConfiguration
                || manualFanCurveRecoveryPending
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
                lastOwnedFanControlSettings = settings;
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
                : "Lighting override intent was stored.",
            afterApply: () =>
            {
                lightingRecoveryPauseMessage = null;
                explicitLightingOwnershipActive =
                    normalized is not null
                    && normalized.Value.Effect != LightingEffect.Off
                    && IsExplicitLightingOwnership(
                        GetStatus().LightingRendererOwnership);
            });
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
        if (IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
            }

            if (ChangePowerSourceCore(powerSource))
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

    public async Task<ControlOperationResult> SetDisplayBrightnessHidUsageAsync(
        DisplayBrightnessHidUsage usage,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(usage))
        {
            return RecordRejectedIntent(new ArgumentOutOfRangeException(nameof(usage)));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (usage == DisplayBrightnessHidUsage.Released)
            {
                return await ReleaseDisplayBrightnessUsageCoreAsync(token)
                    .ConfigureAwait(false);
            }

            if (displayBrightnessReleaseRequired
                && assertedDisplayBrightnessUsage == usage)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "The native display-brightness input remains asserted.");
            }

            if (displayBrightnessReleaseRequired)
            {
                ControlOperationResult release =
                    await ReleaseDisplayBrightnessUsageCoreAsync(token)
                        .ConfigureAwait(false);
                if (release.Status != ControlOperationStatus.Applied)
                {
                    return release;
                }
            }

            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || current.WritesBlocked
                || !deviceProvider.TryGetVerifiedDevice(out _))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Native display-brightness input requires the verified admitted laptop device.");
            }

            if (displayBrightnessNeutralizationPending)
            {
                ControlOperationResult neutralization =
                    await ReleaseDisplayBrightnessUsageCoreAsync(token)
                        .ConfigureAwait(false);
                if (neutralization.Status != ControlOperationStatus.Applied)
                {
                    return neutralization;
                }
            }

            ControlOperationResult result =
                await displayBrightnessHidController.SetUsageAsync(usage, token)
                    .ConfigureAwait(false);
            if (result.Status != ControlOperationStatus.Blocked)
            {
                assertedDisplayBrightnessUsage = usage;
                displayBrightnessReleaseRequired = true;
            }

            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ReleaseDisplayBrightnessUsageCoreAsync(
        CancellationToken token)
    {
        ControlOperationResult release =
            await displayBrightnessHidController.SetUsageAsync(
                DisplayBrightnessHidUsage.Released,
                token).ConfigureAwait(false);
        if (release.Status == ControlOperationStatus.Applied)
        {
            assertedDisplayBrightnessUsage = DisplayBrightnessHidUsage.Released;
            displayBrightnessReleaseRequired = false;
            displayBrightnessNeutralizationPending = false;
        }

        return release;
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
        ControlOperationResult result = await ApplyScopedIntentAsync(
            DeviceSubsystem.Lighting,
            powerSource,
            (coordinator, desired, cancellationToken) =>
                coordinator.ApplyLightingOutcomeAsync(
                    desired.Lighting.Value,
                    cancellationToken),
            token).ConfigureAwait(false);
        if (result.Status == ControlOperationStatus.Applied)
        {
            lightingRecoveryPauseMessage = null;
            explicitLightingOwnershipActive = IsExplicitLightingOwnership(
                GetStatus().LightingRendererOwnership);
        }
        return result;
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
        lightingRecoveryPauseMessage = null;
        explicitLightingOwnershipActive = resultingBrightness > 0
            && IsExplicitLightingOwnership(ownership);
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

    public async Task<ControlOperationResult> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(mode))
        {
            return RecordRejectedIntent(new ArgumentOutOfRangeException(nameof(mode)));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (mode == KeyboardLightingControlMode.WindowsDynamicLighting
                && GetStatus().Capabilities?.LampArrayLightingAccess is null)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Windows Dynamic Lighting is not available for this keyboard.");
            }

            bool delegateToWindows =
                mode == KeyboardLightingControlMode.WindowsDynamicLighting;
            if (preferences.KeyboardLightingControlMode == mode)
            {
                if (delegateToWindows)
                {
                    // Reassert the ownership boundary even when the persisted
                    // preference already says Windows owns lighting. This
                    // recovers safely from a previously interrupted transition
                    // or an unexpectedly live renderer.
                    await InvalidateLightingRendererCoreAsync(
                        preserveForLifecycleRestore: false,
                        token).ConfigureAwait(false);
                }

                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    delegateToWindows
                        ? "Keyboard lighting is already delegated to Windows."
                        : "OpenBlade already controls keyboard lighting.");
            }

            BatteryAutomationPreferences staged = preferences with
            {
                KeyboardLightingControlMode = mode,
                AlwaysOverrideExternalLightingChanges =
                    delegateToWindows
                        ? false
                        : preferences.AlwaysOverrideExternalLightingChanges,
            };
            if (delegateToWindows)
            {
                // Stop the live renderer before persisting or publishing the
                // delegation. Keeping a lifecycle restore candidate lets us
                // put the renderer back if persistence fails.
                await InvalidateLightingRendererCoreAsync(
                    preserveForLifecycleRestore: true,
                    token).ConfigureAwait(false);
            }

            try
            {
                await ConfigurationStore.SaveConfigurationAsync(Automation.Profiles, staged, token)
                    .ConfigureAwait(false);
            }
            catch (Exception saveException) when (delegateToWindows)
            {
                try
                {
                    bool restored = await RestoreLightingRendererCoreAsync(
                        CancellationToken.None).ConfigureAwait(false);
                    if (!restored)
                    {
                        throw new InvalidOperationException(
                            "The previous lighting renderer could not be restored.");
                    }
                }
                catch (Exception restoreException)
                {
                    throw new AggregateException(
                        "The lighting-control preference could not be saved and the previous renderer could not be restored.",
                        saveException,
                        restoreException);
                }

                throw;
            }

            preferences = staged;

            if (delegateToWindows)
            {
                pendingLightingRestore = null;
                pendingLightingRestoreFromLifecycle = false;
                faultedLightingRecoveryCandidate = null;
                lastOwnedLightingSettings = null;
                lightingSuppressions.Clear();
                lightingSuppressionWritePending = false;
            }

            string message = delegateToWindows
                ? "Keyboard lighting was delegated to Windows Dynamic Lighting. OpenBlade will not write keyboard lighting."
                : "OpenBlade keyboard lighting control is enabled. No lighting change was applied.";
            PublishIfChanged(GetStatus() with
            {
                KeyboardLightingControlMode = mode,
                AlwaysOverrideExternalLightingChanges =
                    staged.AlwaysOverrideExternalLightingChanges,
                LightingRendererOwnership = LightingRendererOwnershipState.None,
                LightingSuppressions = CurrentLightingSuppressions(),
                Message = message,
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);
            return new ControlOperationResult(ControlOperationStatus.Stored, message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasPendingWriteConfirmations())
            {
                const string pendingMessage =
                    "OpenBlade must confirm the current laptop state before settings can be reapplied.";
                history.RecordSummary(ControlOperationStatus.Blocked, pendingMessage);
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    pendingMessage);
            }

            if (driftTracker.ExternalChanges.Count == 0)
            {
                const string unchangedMessage =
                    "No settings changed outside OpenBlade.";
                history.RecordSummary(ControlOperationStatus.Stored, unchangedMessage);
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    unchangedMessage);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                const string blockedMessage =
                    "Changed settings cannot be reapplied until the external power adapter is classified.";
                history.RecordSummary(ControlOperationStatus.Blocked, blockedMessage);
                PublishIfChanged(GetStatus() with
                {
                    ActivePowerSource = PowerSourceForStatus(powerSource),
                    Message = blockedMessage,
                });
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    blockedMessage);
            }

            if (ChangePowerSourceCore(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            HashSet<DeviceSubsystem> targetSet =
                [.. driftTracker.ExternalChanges];
            if (targetSet.Count == 0)
            {
                const string reconciledMessage =
                    "OpenBlade settings already match the current profile and overrides.";
                history.RecordSummary(ControlOperationStatus.Applied, reconciledMessage);
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    reconciledMessage);
            }

            ConfirmedDeviceState confirmed =
                GetStatus().ConfirmedState ?? UnknownConfirmedState;
            DesiredDeviceState desired = ResolveHardwareDesired(confirmed);
            DeviceSubsystem[] targets =
            [
                .. DeviceSubsystemOrder.ForTransition(confirmed, desired)
                    .Where(targetSet.Contains),
            ];
            bool manualFanPerformanceBlocked =
                targetSet.Contains(DeviceSubsystem.Performance)
                && DeviceSubsystemOrder.RequiresManualFanPerformanceBridge(
                    confirmed,
                    desired);

            var results = new Dictionary<DeviceSubsystem, ControlOperationResult>(
                targets.Length);
            foreach (DeviceSubsystem subsystem in targets)
            {
                if (manualFanPerformanceBlocked
                    && DeviceSubsystemOrder.IsThermal(subsystem))
                {
                    // Restoring this pair would require a staged
                    // Manual -> Automatic -> Performance -> Manual transition.
                    // That transition remains deferred until it has exact-model
                    // capture evidence and interactive hardware validation.
                    results[subsystem] = new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        subsystem == DeviceSubsystem.Performance
                            ? "Power mode cannot be reapplied while manual fan control is active. Switch fans to Automatic, then reapply."
                            : "Fans were not reapplied because OpenBlade kept manual fan control unchanged while Power mode recovery was blocked.");
                    continue;
                }

                results[subsystem] = await ReapplyChangedSubsystemAsync(
                    subsystem,
                    powerSource,
                    token).ConfigureAwait(false);
                if (GetStatus().WritesBlocked)
                {
                    break;
                }
            }

            var remaining = driftTracker.ExternalChanges
                .Where(subsystem => targets.Contains(subsystem))
                .ToArray();
            var reapplied = targets.Except(remaining).ToArray();
            ControlOperationStatus status = remaining.Length == 0
                ? ControlOperationStatus.Applied
                : results.Values.Any(
                    result => result.Status == ControlOperationStatus.Failed)
                    ? ControlOperationStatus.Failed
                    : ControlOperationStatus.Blocked;
            string remainingReasons = FormatRemainingReasons(remaining, results);
            string message = remaining.Length == 0
                ? $"OpenBlade reapplied {FormatSubsystemList(reapplied)} from the current settings."
                : reapplied.Length == 0
                    ? $"OpenBlade could not reapply the changed settings. Still changed: {FormatSubsystemList(remaining)}.{remainingReasons}"
                    : $"OpenBlade reapplied {FormatSubsystemList(reapplied)}. Still changed: {FormatSubsystemList(remaining)}.{remainingReasons}";
            history.RecordSummary(status, message);
            PublishIfChanged(GetStatus() with
            {
                ExternalChanges = driftTracker.ExternalChanges,
                Message = message,
            });
            return new ControlOperationResult(status, message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> RecoverHyperBoostRestoreAsync(
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            HyperBoostOwnershipJournalLoadResult load;
            try
            {
                load = await hyperBoostOwnershipJournalStore.LoadAsync(token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    $"The saved HyperBoost restore could not be inspected: {exception.Message}",
                    confirmed: null,
                    restorePending: true);
            }

            if (load.Invalid)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved HyperBoost restore is invalid and was preserved for manual inspection.",
                    confirmed: null,
                    restorePending: false);
            }

            if (load.Journal is null)
            {
                hyperBoostJournalSafetyBlock = false;
                hyperBoostRestorePending = false;
                lastReportedRetainedHyperBoostRestoreJournal = null;
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Stored,
                    "There is no saved HyperBoost restore to retry.",
                    confirmed: null,
                    restorePending: false);
            }

            if (load.Journal is not
                {
                    State: HyperBoostOwnershipJournalState.RestorePending,
                    RestoreIntent: { } restoreIntent,
                } journal)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The HyperBoost ownership journal is not awaiting a user-authorized restore retry.",
                    confirmed: null,
                    restorePending: false);
            }

            hyperBoostRestorePending = true;
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            bool thermalExternalChange = current.ExternalChanges.Any(
                DeviceSubsystemOrder.IsThermal);
            if (!current.DeviceVerified
                || !hyperBoostJournalSafetyBlock
                || HasPendingWriteConfirmations()
                || current.ControllerDetectionWarning is not null
                || current.PotentialControllers.Any(CompetingControllerProcesses.Contains)
                || powerSource != PowerSource.PluggedIn
                || current.ActivePowerSource != PowerSource.PluggedIn
                || current.Capabilities is null
                || !IsSupportedFullPowerHyperBoostIntent(
                    current.Capabilities,
                    restoreIntent)
                || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved HyperBoost restore can be retried only after fresh exact-device, full-power AC, controller, and thermal safety checks pass.",
                    confirmed: null,
                    restorePending: true);
            }

            ConfirmedDeviceState fresh;
            try
            {
                fresh = await ReadHyperBoostThermalStateAsync(
                        device,
                        current.ConfirmedState,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    $"The saved HyperBoost restore was not retried because fresh thermal readback failed: {exception.Message}",
                    confirmed: null,
                    restorePending: true);
            }

            if (fresh.HyperBoostActive)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved restore was not retried because fresh readback shows HyperBoost is active.",
                    fresh,
                    restorePending: true);
            }

            if (thermalExternalChange
                && !ConfirmedStateMatchesHyperBoostTransitionBaseline(
                    fresh,
                    journal.BaseMode))
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved restore was not retried because fresh thermal readback no longer matches the known HyperBoost transition baseline.",
                    fresh,
                    restorePending: true);
            }

            HyperBoostIntentRestorationResult restoration =
                await RestoreHyperBoostIntentAsync(
                        device,
                        restoreIntent,
                        fresh,
                        token)
                    .ConfigureAwait(false);
            ConfirmedDeviceState restored = restoration.Readback ?? fresh;
            if (!restoration.Succeeded)
            {
                hyperBoostJournalSafetyBlock = true;
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    $"The user-authorized HyperBoost restore retry did not complete. {restoration.Message} The saved restore was preserved and will not be retried automatically.",
                    restored,
                    restorePending: true);
            }

            driftTracker.Observe(restored);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
            RestoreRecoveredHyperBoostIntentInAutomation(restoreIntent);
            bool retired = await RetireHyperBoostOwnershipJournalAsync(
                    journal.BaseMode,
                    journal.RequiresCoolingPad,
                    journal.RestoreIntent,
                    "The user-authorized HyperBoost restore completed, but its ownership journal could not be retired.")
                .ConfigureAwait(false);
            hyperBoostRestorePending = !retired;
            if (retired)
            {
                lastReportedRetainedHyperBoostRestoreJournal = null;
            }

            return PublishHyperBoostRestoreRecoveryResult(
                retired
                    ? ControlOperationStatus.Applied
                    : ControlOperationStatus.Failed,
                retired
                    ? $"The saved {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored after one user-authorized retry."
                    : "The saved performance and fan intent was restored, but its ownership journal could not be retired; control writes remain blocked.",
                restored,
                restorePending: !retired);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private ControlOperationResult PublishHyperBoostRestoreRecoveryResult(
        ControlOperationStatus status,
        string message,
        ConfirmedDeviceState? confirmed,
        bool restorePending)
    {
        hyperBoostRestorePending = restorePending;
        if (restorePending)
        {
            hyperBoostJournalSafetyBlock = true;
        }
        var result = new ControlOperationResult(status, message);
        history.RecordSummary(status, message);
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState effectiveConfirmed = confirmed
            ?? current.ConfirmedState
            ?? UnknownConfirmedState;
        PublishIfChanged(current with
        {
            ConfirmedState = confirmed ?? current.ConfirmedState,
            DesiredState = Automation.Resolve(effectiveConfirmed),
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = restorePending || HasPendingWriteConfirmations(),
            HyperBoostRestorePending = restorePending,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            FanControlOverride = Automation.FanControlOverride,
            Message = message,
        });
        return result;
    }

    private Task<ControlOperationResult> ReapplyChangedSubsystemAsync(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        CancellationToken token) => subsystem switch
        {
            DeviceSubsystem.Performance => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyPerformanceOutcomeAsync(
                        desired,
                        cancellationToken),
                token),
            DeviceSubsystem.Fans => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyFanControlOutcomeAsync(
                        desired.FanControl
                            ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                        cancellationToken),
                token),
            DeviceSubsystem.Battery => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyBatteryOutcomeAsync(
                        desired.Battery,
                        cancellationToken),
                token),
            DeviceSubsystem.Lighting => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyLightingOutcomeAsync(
                        desired.Lighting.Value,
                        cancellationToken,
                        forceWrite: true),
                token),
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };

    private static string FormatSubsystemList(IEnumerable<DeviceSubsystem> subsystems) =>
        string.Join(
            ", ",
            subsystems.Select(subsystem => subsystem switch
            {
                DeviceSubsystem.Performance => "Power mode",
                DeviceSubsystem.Fans => "Fans",
                DeviceSubsystem.Battery => "Battery",
                DeviceSubsystem.Lighting => "Lighting",
                _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
            }));

    private static string FormatRemainingReasons(
        IEnumerable<DeviceSubsystem> remaining,
        IReadOnlyDictionary<DeviceSubsystem, ControlOperationResult> results)
    {
        string[] reasons = remaining
            .Select(subsystem => results.GetValueOrDefault(subsystem)?.Message)
            .OfType<string>()
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return reasons.Length == 0
            ? string.Empty
            : $" {string.Join(" ", reasons)}";
    }

    public async Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        if (enabled && IsKeyboardLightingDelegated)
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "OpenBlade cannot override external keyboard lighting while control is delegated to Windows.");
        }
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BatteryAutomationPreferences staged = preferences with
            {
                AlwaysOverrideExternalLightingChanges = enabled,
            };
            await ConfigurationStore.SaveConfigurationAsync(Automation.Profiles, staged, token)
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
                await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                    .ConfigureAwait(false);

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

    public async Task<ControlOperationResult> SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(refreshRate))
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The requested lighting refresh rate is invalid.");
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceStatusSnapshot current = GetStatus();
            if (IsKeyboardLightingDelegated)
            {
                return WindowsDynamicLightingOwnsKeyboard();
            }

            if (!current.DeviceVerified
                || current.WritesBlocked
                || current.ActivePowerSource == PowerSource.ExternalUnknown)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The lighting refresh rate cannot change while laptop controls are unavailable.");
            }

            if (current.Capabilities?.SupportedLightingRefreshRates.Contains(refreshRate)
                != true)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The requested lighting refresh rate is not supported by this keyboard.");
            }

            BatteryAutomationPreferences previousPreferences = preferences;
            bool preferenceChanged = previousPreferences.LightingRefreshRate != refreshRate;
            if (preferenceChanged)
            {
                BatteryAutomationPreferences staged = preferences with
                {
                    LightingRefreshRate = refreshRate,
                };
                await ConfigurationStore.SaveConfigurationAsync(Automation.Profiles, staged, token)
                    .ConfigureAwait(false);
                preferences = staged;
            }

            deviceProvider.SetLightingRefreshRate(refreshRate);

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    preferenceChanged
                        ? "The lighting refresh rate was saved and will apply when the keyboard reconnects."
                        : $"Keyboard lighting is already set to {RefreshRateLabel(refreshRate)}.");
            }

            async Task<Exception?> TryRollbackPreferenceAsync()
            {
                if (!preferenceChanged)
                {
                    return null;
                }

                try
                {
                    await ConfigurationStore.SaveConfigurationAsync(
                        Automation.Profiles,
                        previousPreferences,
                        CancellationToken.None).ConfigureAwait(false);
                    preferences = previousPreferences;
                    deviceProvider.SetLightingRefreshRate(
                        previousPreferences.LightingRefreshRate);
                    return null;
                }
                catch (Exception rollbackException)
                {
                    return rollbackException;
                }
            }

            try
            {
                await device.SetLightingRefreshRateAsync(refreshRate, token)
                    .ConfigureAwait(false);
                PublishIfChanged(current with
                {
                    LightingRefreshRate = refreshRate,
                    Message = $"Keyboard lighting refresh rate is set to {RefreshRateLabel(refreshRate)}.",
                });
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    $"Keyboard lighting is running at {RefreshRateLabel(refreshRate)}.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Exception? rollbackException = await TryRollbackPreferenceAsync()
                    .ConfigureAwait(false);
                if (rollbackException is not null)
                {
                    throw new InvalidOperationException(
                        "The lighting refresh-rate change was canceled after persistence, and the saved selection could not be rolled back.",
                        rollbackException);
                }

                throw;
            }
            catch (Exception exception)
            {
                Exception? rollbackException = await TryRollbackPreferenceAsync()
                    .ConfigureAwait(false);
                if (rollbackException is not null)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Failed,
                        "The active lighting renderer could not be updated, and the saved refresh-rate selection could not be rolled back. Retry the selection. "
                        + exception.Message
                        + " Rollback: "
                        + rollbackException.Message);
                }

                return new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The active lighting renderer could not be updated, so the refresh-rate selection was rolled back. "
                    + exception.Message);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        string message = enabled
            ? "OpenBlade will allow this exact laptop model to run on untested firmware."
            : "OpenBlade will require the tested firmware baseline.";

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BatteryAutomationPreferences staged = preferences with
            {
                AllowUntestedFirmware = enabled,
            };
            await ConfigurationStore.SaveConfigurationAsync(
                    Automation.Profiles,
                    staged,
                    token)
                .ConfigureAwait(false);

            Exception? cleanupFailure = null;
            if (!enabled
                && deviceProvider.RequiresUntestedFirmwareOverride)
            {
                try
                {
                    // Relinquish any long-lived renderer while the old admission
                    // policy can still reach its exact device for cleanup.
                    await InvalidateLightingRendererCoreAsync(
                        preserveForLifecycleRestore: false,
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    preferences = staged;
                    deviceProvider.SetAllowUntestedFirmware(enabled);
                    throw;
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            preferences = staged;
            deviceProvider.SetAllowUntestedFirmware(enabled);
            // This is a deliberate policy transition, not a transient missed
            // enumeration. Skip the normal one-miss presentation debounce so
            // the following observation publishes the new admission state.
            consecutiveDeviceProbeFailures = ObservationFailureThreshold;
            PublishIfChanged(GetStatus() with
            {
                AllowUntestedFirmware = enabled,
                FirmwareCompatibility = deviceProvider.FirmwareCompatibility,
                Message = cleanupFailure is null
                    ? message
                    : $"{message} Renderer access is blocked, but lighting cleanup could not complete. {cleanupFailure.Message}",
            });
            history.RecordSummary(ControlOperationStatus.Stored, GetStatus().Message);
        }
        finally
        {
            operationGate.Release();
        }

        try
        {
            await ObserveAsync(
                    includeLiveReadings: false,
                    token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string refreshMessage =
                $"{message} Laptop status will refresh automatically. {exception.Message}";
            history.RecordSummary(ControlOperationStatus.Stored, refreshMessage);
            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                refreshMessage);
        }

        return new ControlOperationResult(ControlOperationStatus.Stored, message);
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
        if (IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool alreadySet = lightingSuppressions.Contains(reason);
            if (alreadySet == suppressed && !lightingSuppressionWritePending)
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

            if (lowPowerGate.IsActive)
            {
                if (alreadySet != suppressed)
                {
                    bool wasSuppressedBeforeDeferred = lightingSuppressions.Count > 0;
                    UpdateLightingSuppressionState(reason, suppressed);
                    if (wasSuppressedBeforeDeferred != (lightingSuppressions.Count > 0))
                    {
                        lightingSuppressionWritePending = true;
                    }
                }
                const string deferred =
                    "Lighting power policy state was recorded without a firmware write while the low-power gate is active.";
                history.RecordSummary(ControlOperationStatus.Stored, deferred);
                return new ControlOperationResult(ControlOperationStatus.Stored, deferred);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Lighting,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; lighting power policy writes are blocked.",
                    preserveCurrentAvailability: true);
            }
            if (ShouldYieldAutomaticLighting(current))
            {
                bool wasSuppressedBeforeDeferred = lightingSuppressions.Count > 0;
                if (alreadySet != suppressed)
                {
                    UpdateLightingSuppressionState(reason, suppressed);
                }
                if (wasSuppressedBeforeDeferred != (lightingSuppressions.Count > 0))
                {
                    lightingSuppressionWritePending = true;
                }
                const string deferred =
                    "Lighting power policy state was recorded without changing externally controlled lighting.";
                history.RecordSummary(ControlOperationStatus.Stored, deferred);
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    deferred);
            }

            if (ChangePowerSourceCore(powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, token).ConfigureAwait(false);
            }

            bool wasSuppressed = lightingSuppressions.Count > 0;
            if (alreadySet != suppressed)
            {
                UpdateLightingSuppressionState(reason, suppressed);
            }
            bool isSuppressed = lightingSuppressions.Count > 0;

            if (wasSuppressed == isSuppressed && !lightingSuppressionWritePending)
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
            if (result.Status == ControlOperationStatus.Applied)
            {
                lightingSuppressionWritePending = false;
            }
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
        if (IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }
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
        if (IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }
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

        if (current.LightingRendererOwnership is not (
            LightingRendererOwnershipState.AmbientAwareness or
            LightingRendererOwnershipState.AudioMeter))
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
        if (IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }
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

        if (current.Capabilities?.SupportsPlaybackMuteIndicator != true)
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
            if (!muted)
            {
                try
                {
                    await ReconcileUnmutedLightingRendererAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    history.RecordSummary(
                        ControlOperationStatus.Failed,
                        "Playback mute was updated, but keyboard lighting reconciliation could not be completed.");
                }
            }
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

    private async Task ReconcileUnmutedLightingRendererAsync(
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lastOwnedLightingSettings is null
                && faultedLightingRecoveryCandidate is null)
            {
                return;
            }

            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken,
                allowRecentObservation: true).ConfigureAwait(false);
            await RecoverFaultedLightingRendererCoreAsync(cancellationToken)
                .ConfigureAwait(false);
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
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
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

            return await ApplyVerifiedOneShotLaptopControlAsync(value, token)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken token) => SetHyperBoostCoreAsync(
            enabled,
            expectedOwnershipGeneration: null,
            lifecycleValidation: null,
            restoreUserIntent: true,
            exitPowerSourceOverride: null,
            expectedExitPowerSourceGeneration: null,
            token);

    public Task<ControlOperationResult> ValidatePadlessHyperBoostLifecycleAsync(
        ValidatePadlessHyperBoostLifecycleRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Scenario))
        {
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The pad-less HyperBoost lifecycle validation scenario is invalid."));
        }
        PadlessHyperBoostLifecycleValidationBuildIdentity build =
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent();
        if (!build.Matches(
                request.ExpectedSourceRevision,
                request.ExpectedServiceAssemblySha256,
                request.ExpectedAdmissionManifestSha256)
            || !HyperBoostWithoutCoolingPadEvidenceGates.Current
                .LifecycleValidationReady)
        {
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Pad-less HyperBoost lifecycle validation is unavailable until the exact validation build and every non-lifecycle evidence gate are confirmed."));
        }

        return SetHyperBoostCoreAsync(
            enabled: true,
            expectedOwnershipGeneration: null,
            request,
            restoreUserIntent: false,
            exitPowerSourceOverride: null,
            expectedExitPowerSourceGeneration: null,
            token);
    }

    internal static PadlessHyperBoostLifecycleRunClaim ClaimPadlessLifecycleRun(
        HashSet<Guid> claimedRuns,
        Guid validationRunId,
        bool validationDeviceAvailable)
    {
        ArgumentNullException.ThrowIfNull(claimedRuns);
        if (!validationDeviceAvailable)
        {
            return PadlessHyperBoostLifecycleRunClaim.DeviceUnavailable;
        }

        return claimedRuns.Add(validationRunId)
            ? PadlessHyperBoostLifecycleRunClaim.Claimed
            : PadlessHyperBoostLifecycleRunClaim.AlreadyUsed;
    }

    private async Task<ControlOperationResult> SetHyperBoostCoreAsync(
        bool enabled,
        long? expectedOwnershipGeneration,
        ValidatePadlessHyperBoostLifecycleRequest? lifecycleValidation,
        bool restoreUserIntent,
        PowerSource? exitPowerSourceOverride,
        long? expectedExitPowerSourceGeneration,
        CancellationToken token,
        bool operationGateAlreadyHeld = false,
        Action<bool>? writeAttemptObserver = null)
    {
        EnsureInitialized();
        ServiceStatusSnapshot? writeBaseline = null;
        bool setterCompleted = false;
        bool ownedDisableAttempt = false;
        bool ownershipJournalPrepared = false;
        bool journalRequiresCoolingPad = false;
        PerformancePreset pendingBaseMode = PerformancePreset.Balanced;
        HyperBoostRestoreIntent? transitionIntent = null;
        IBladeDevice? transitionDevice = null;
        bool baselineTransitionChanged = false;
        bool baselineTransitionCompleted = false;
        bool hyperBoostWriteAttempted = false;
        bool finalHyperBoostReadbackCompleted = false;
        if (!operationGateAlreadyHeld)
        {
            await operationGate.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (expectedOwnershipGeneration is { } expected
                && (!ownsHyperBoost || hyperBoostOwnershipGeneration != expected))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "HyperBoost lifecycle restoration was skipped because OpenBlade ownership changed before the write.");
            }
            bool ownedLifecycleRestore = !enabled
                && expectedOwnershipGeneration is not null
                && ownsHyperBoost
                && current.ConfirmedState?.HyperBoostActive == true
                && !HasPendingWriteConfirmations();
            if (!current.DeviceVerified
                || current.WritesBlocked && !ownedLifecycleRestore
                || current.Capabilities?.HyperBoostAccess
                    != DeviceFeatureAccess.ReadWrite
                || current.ConfirmedState is not { } confirmed)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost control is unavailable right now.");
            }

            if (confirmed.HyperBoostActive == enabled)
            {
                if (!enabled)
                {
                    bool hadOwnership = ownsHyperBoost
                        || ownedHyperBoostBaseMode is not null;
                    bool retired = !hadOwnership
                        || await RetireInactiveHyperBoostJournalIfPresentAsync(
                                "HyperBoost was already off, but its ownership journal could not be retired.")
                            .ConfigureAwait(false);
                    if (hadOwnership)
                    {
                        hyperBoostOwnershipGeneration++;
                    }
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    pendingOwnedHyperBoostPowerBoundaryExit = false;
                    ownedHyperBoostPowerBoundaryExitSubmitted = false;
                    if (!retired)
                    {
                        return new ControlOperationResult(
                            ControlOperationStatus.Failed,
                            "HyperBoost is already off, but OpenBlade could not safely retire its ownership journal. Control writes remain blocked.");
                    }
                }

                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    enabled
                        ? "HyperBoost is already active."
                        : "HyperBoost is already off.");
            }

            bool padAbsentAccessAdmitted = current.Capabilities
                    ?.HyperBoostWithoutCoolingPadAccess
                == DeviceFeatureAccess.ReadWrite
                || lifecycleValidation is not null
                    && current.Capabilities?.HyperBoostWithoutCoolingPadAccess
                        == DeviceFeatureAccess.ObservedOnly;
            if (enabled
                && (confirmed.Fans is null
                    || confirmed.Fans == FanMode.Automatic
                        && confirmed.Performance is null))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost requires a fresh confirmed performance and fan state.");
            }

            PerformancePreset requestedBaseMode = enabled
                ? PerformancePreset.Balanced
                : ownedHyperBoostBaseMode ?? PerformancePreset.Balanced;
            pendingBaseMode = requestedBaseMode;
            DeviceFeatureAccess? requestedBaseAccess = current.Capabilities
                ?.HyperBoostBaseModes?.AccessFor(requestedBaseMode)
                ?? (requestedBaseMode == PerformancePreset.Balanced
                    ? current.Capabilities?.HyperBoostAccess
                    : null);
            if (enabled && requestedBaseAccess != DeviceFeatureAccess.ReadWrite)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Pad-less HyperBoost requires the admitted Balanced transition baseline.");
            }

            transitionIntent = enabled
                ? CreateHyperBoostRestoreIntent(current.DesiredState)
                : ResolveHyperBoostExitIntent(
                    current,
                    ownedHyperBoostRestoreIntent,
                    current.WritesBlocked
                        ? PowerSource.ExternalUnknown
                        : exitPowerSourceOverride is { } exitSource
                            && expectedExitPowerSourceGeneration
                                == powerSourceGeneration
                                ? exitSource
                                : current.ActivePowerSource);
            if (enabled && !IsSupportedFullPowerHyperBoostIntent(
                    current.Capabilities!,
                    transitionIntent))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The current full-power performance or fan intent is not admitted for this verified device.");
            }

            if (enabled
                && (current.ActivePowerSource != PowerSource.PluggedIn
                    || !padAbsentAccessAdmitted))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Pad-less HyperBoost requires an admitted full-power adapter.");
            }

            if (enabled)
            {
                // Absence is a pre-write eligibility condition, not a continuous
                // thermal dependency of an already-owned pad-less session. A pad
                // connected later can only add cooling, so observation reports the
                // new state without issuing an automatic HyperBoost write.
                CoolingPadConnectionState freshPadState =
                    hyperBoostPadDiscoverySource.Observe();
                if (freshPadState != CoolingPadConnectionState.Absent)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        "Pad-less HyperBoost requires a fresh observation confirming that no target cooling-pad device is present. Ambiguous or failed enumeration is blocked.");
                }
            }
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost control is unavailable right now.");
            }
            transitionDevice = device;

            if (lifecycleValidation is not null)
            {
                PadlessHyperBoostLifecycleRunClaim claim = ClaimPadlessLifecycleRun(
                    padlessHyperBoostLifecycleValidationRuns,
                    lifecycleValidation.ValidationRunId,
                    device is IPadlessHyperBoostLifecycleValidationDevice);
                if (claim != PadlessHyperBoostLifecycleRunClaim.Claimed)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        claim == PadlessHyperBoostLifecycleRunClaim.DeviceUnavailable
                            ? "The exact validation-only device path is unavailable. The lifecycle validation run was not consumed."
                            : "This lifecycle validation run was already used.");
                }
            }

            writeBaseline = current;
            ownedDisableAttempt = !enabled && ownsHyperBoost;
            if (enabled || ownedDisableAttempt)
            {
                await hyperBoostOwnershipJournalStore.WriteAsync(
                        new HyperBoostOwnershipJournal(
                            enabled
                                ? HyperBoostOwnershipJournalState.EnablePending
                                : HyperBoostOwnershipJournalState.RestorePending,
                            requestedBaseMode,
                            journalRequiresCoolingPad,
                            timeProvider.GetUtcNow())
                        {
                            RestoreIntent = transitionIntent
                                ?? ownedHyperBoostRestoreIntent
                                ?? HyperBoostRestoreIntent.BalancedAutomatic,
                        },
                        token)
                    .ConfigureAwait(false);
                ownershipJournalPrepared = true;
                hyperBoostRestorePending = !enabled;
                lastReportedRetainedHyperBoostRestoreJournal = null;
            }
            if (enabled)
            {
                var coordinator = new ControlCoordinator(
                    device,
                    device.ReadPerformanceFanStateAsync,
                    resolveIndeterminatePerformanceWrites: true);
                ScopedApplyOutcome automaticFans = await coordinator
                    .ApplyFanControlOutcomeAsync(
                        FanControlSettings.FromLegacy(FanMode.Automatic),
                        token)
                    .ConfigureAwait(false);
                automaticFans = await ResolveIndeterminateHyperBoostFanBaselineAsync(
                        device,
                        confirmed.Fans,
                        automaticFans,
                        token)
                    .ConfigureAwait(false);
                if (!HyperBoostBaselineStepSucceeded(automaticFans))
                {
                    return await FinishFailedHyperBoostBaselineTransitionAsync(
                            current,
                            automaticFans,
                            requestedBaseMode,
                            device,
                            transitionIntent!,
                            baselineChanged: false)
                        .ConfigureAwait(false);
                }
                baselineTransitionChanged = automaticFans.Result.Status
                    == ControlResultStatus.Applied;

                ScopedApplyOutcome balancedPerformance = await coordinator
                    .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                        PerformancePreset.Balanced,
                        customPerformance: null,
                        token)
                    .ConfigureAwait(false);
                if (!HyperBoostBaselineStepSucceeded(balancedPerformance))
                {
                    return await FinishFailedHyperBoostBaselineTransitionAsync(
                            current,
                            balancedPerformance,
                            requestedBaseMode,
                            device,
                            transitionIntent!,
                            baselineTransitionChanged)
                        .ConfigureAwait(false);
                }
                baselineTransitionChanged |= balancedPerformance.Result.Status
                    == ControlResultStatus.Applied;

                baselineTransitionCompleted = true;
                if (lifecycleValidation is not null)
                {
                    hyperBoostWriteAttempted = true;
                    await ((IPadlessHyperBoostLifecycleValidationDevice)device)
                        .EnablePadlessHyperBoostLifecycleValidationAsync(token)
                        .ConfigureAwait(false);
                }
                else
                {
                    hyperBoostWriteAttempted = true;
                    await device.SetHyperBoostAsync(
                            enabled: true,
                            PerformancePreset.Balanced,
                            token)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                hyperBoostWriteAttempted = true;
                await device.SetHyperBoostAsync(enabled, requestedBaseMode, token)
                    .ConfigureAwait(false);
            }
            setterCompleted = true;
            ConfirmedDeviceState readback = await ReadHyperBoostThermalStateAsync(
                    device,
                    current.ConfirmedState,
                    token)
                .ConfigureAwait(false);
            finalHyperBoostReadbackCompleted = true;
            bool matched = readback.HyperBoostActive == enabled
                && (enabled
                    || readback is
                    {
                        Performance: var restoredPerformance,
                        Fans: FanMode.Automatic,
                    } && restoredPerformance == requestedBaseMode);
            if (!matched)
            {
                HyperBoostIntentRestorationResult? failedTransitionRestore = null;
                if (!readback.HyperBoostActive
                    && transitionIntent is not null
                    && (enabled && baselineTransitionCompleted
                        || !enabled && restoreUserIntent))
                {
                    failedTransitionRestore = await RestoreHyperBoostIntentAsync(
                            device,
                            transitionIntent,
                            readback,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    readback = failedTransitionRestore.Readback ?? readback;
                }

                bool journalRecovered = true;
                if (ownershipJournalPrepared)
                {
                    journalRecovered = failedTransitionRestore is { Succeeded: false }
                        ? false
                        : readback.HyperBoostActive
                        ? await TryWriteHyperBoostOwnershipJournalAsync(
                                HyperBoostOwnershipJournalState.OwnedConfirmed,
                                requestedBaseMode,
                                ownedHyperBoostRequiresCoolingPad,
                                "HyperBoost remained active after a mismatched transition, but its ownership journal could not be returned to owned state.",
                                transitionIntent)
                            .ConfigureAwait(false)
                        : await RetireHyperBoostOwnershipJournalAsync(
                                requestedBaseMode,
                                ownedHyperBoostRequiresCoolingPad,
                                transitionIntent!,
                                "HyperBoost was confirmed inactive after a mismatched transition, but its ownership journal could not be retired.")
                            .ConfigureAwait(false);
                }

                if (readback.HyperBoostActive && enabled)
                {
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = true;
                    ownedHyperBoostBaseMode = requestedBaseMode;
                    ownedHyperBoostRestoreIntent = transitionIntent;
                    ownedHyperBoostRequiresCoolingPad = false;
                }
                else if (!readback.HyperBoostActive)
                {
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                }

                string mismatchMessage =
                    "HyperBoost changed, but the final laptop state did not match the requested mode. OpenBlade will not retry it.";
                if (failedTransitionRestore is { Succeeded: true })
                {
                    mismatchMessage += " The prior performance and fan intent was restored.";
                }
                else if (failedTransitionRestore is { Succeeded: false } failed)
                {
                    mismatchMessage += $" The prior intent could not be fully restored: {failed.Message}";
                }
                if (!journalRecovered)
                {
                    hyperBoostJournalSafetyBlock = true;
                    mismatchMessage +=
                        " Its ownership journal could not be made safe, so control writes remain blocked.";
                }
                if (failedTransitionRestore is { Succeeded: true })
                {
                    driftTracker.Observe(readback);
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Performance,
                        matched: true);
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Fans,
                        matched: true);
                }
                var mismatch = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    mismatchMessage);
                history.RecordSummary(mismatch.Status, mismatch.Message);
                PublishIfChanged(current with
                {
                    ConfirmedState = readback,
                    ExternalChanges = driftTracker.ExternalChanges,
                    Message = mismatch.Message,
                });
                return mismatch;
            }

            if (!enabled && restoreUserIntent && transitionIntent is not null)
            {
                HyperBoostIntentRestorationResult restoration =
                    await RestoreHyperBoostIntentAsync(
                            device,
                            transitionIntent,
                            readback,
                            token)
                        .ConfigureAwait(false);
                readback = restoration.Readback ?? readback;
                if (!restoration.Succeeded)
                {
                    hyperBoostJournalSafetyBlock = true;
                    hyperBoostRestorePending = true;
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    string message =
                        $"HyperBoost is off, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade will not retry an indeterminate write.";
                    var failedRestore = new ControlOperationResult(
                        ControlOperationStatus.Failed,
                        message);
                    history.RecordSummary(failedRestore.Status, failedRestore.Message);
                    PublishIfChanged(current with
                    {
                        ConfirmedState = readback,
                        WritesBlocked = true,
                        Message = message,
                    });
                    return failedRestore;
                }
            }

            if (enabled)
            {
                bool journalFinalized =
                    await TryWriteHyperBoostOwnershipJournalAsync(
                        HyperBoostOwnershipJournalState.OwnedConfirmed,
                        requestedBaseMode,
                        journalRequiresCoolingPad,
                        "HyperBoost was confirmed, but its durable ownership journal could not be finalized.",
                        transitionIntent)
                    .ConfigureAwait(false);
                if (!journalFinalized)
                {
                    return await RestoreAfterHyperBoostJournalFailureAsync(
                            device,
                            current,
                            readback,
                            requestedBaseMode,
                            requiresCoolingPad: false,
                            transitionIntent!)
                    .ConfigureAwait(false);
                }
            }
            bool disableJournalCleared = true;
            if (!enabled && ownedDisableAttempt)
            {
                disableJournalCleared = await TryClearHyperBoostOwnershipJournalAsync(
                        "HyperBoost was restored, but its durable ownership journal could not be cleared.")
                    .ConfigureAwait(false);
            }

            hyperBoostOwnershipGeneration++;
            ownsHyperBoost = enabled;
            ownedHyperBoostBaseMode = enabled ? requestedBaseMode : null;
            ownedHyperBoostRestoreIntent = enabled ? transitionIntent : null;
            pendingIndeterminateHyperBoostEntryIntent = null;
            pendingIndeterminateHyperBoostExitIntent = null;
            ownedHyperBoostRequiresCoolingPad = false;
            pendingOwnedHyperBoostPowerBoundaryExit = false;
            ownedHyperBoostPowerBoundaryExitSubmitted = false;

            driftTracker.Observe(readback);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);

            string resultMessage = enabled
                ? "HyperBoost is active through the admitted pad-less path; no cooling pad is required for this owned session."
                : disableJournalCleared
                    ? $"HyperBoost is off and the prior {PerformanceLabel(transitionIntent?.Performance ?? requestedBaseMode)} performance and fan intent is restored."
                    : $"HyperBoost is off and the prior {PerformanceLabel(transitionIntent?.Performance ?? requestedBaseMode)} performance and fan intent is restored, but its ownership journal could not be cleared; control writes remain blocked.";
            if (lifecycleValidation is not null)
            {
                resultMessage += lifecycleValidation.Scenario switch
                {
                    PadlessHyperBoostLifecycleScenario.LowPower =>
                        " The low-power lifecycle validation scenario is armed.",
                    PadlessHyperBoostLifecycleScenario.UnexpectedExit =>
                        " The unexpected-exit lifecycle validation scenario is armed.",
                    _ => " The lifecycle validation scenario is armed.",
                };
            }

            var result = new ControlOperationResult(
                disableJournalCleared
                    ? ControlOperationStatus.Applied
                    : ControlOperationStatus.Failed,
                resultMessage)
            {
                HyperBoostRestoredPerformance = !enabled && disableJournalCleared
                    ? transitionIntent?.Performance ?? requestedBaseMode
                    : null,
            };
            history.RecordSummary(result.Status, result.Message);
            PublishIfChanged(current with
            {
                ConfirmedState = readback,
                ExternalChanges = driftTracker.ExternalChanges,
                Message = result.Message,
            });
            return result;
        }
        catch (OperationCanceledException exception)
            when (token.IsCancellationRequested)
        {
            if (setterCompleted
                || exception is IntentionalWriteCanceledException)
            {
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(writeBaseline, enabled, pendingBaseMode);
                PublishPendingHyperBoostStatus(
                    setterCompleted
                        ? "HyperBoost was submitted, but final readback was canceled. Thermal writes remain blocked until a fresh observation resolves it."
                        : "The HyperBoost entry transition was canceled after a write attempt. Thermal writes remain blocked until a fresh observation resolves it.");
            }
            else if (ownershipJournalPrepared)
            {
                if (enabled
                    && baselineTransitionChanged
                    && transitionDevice is not null
                    && transitionIntent is not null)
                {
                    await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
            }
            throw;
        }
        catch (CapabilityUnavailableException exception)
        {
            HyperBoostIntentRestorationResult? failedEntryRestoration = null;
            if (ownershipJournalPrepared && !setterCompleted)
            {
                if (enabled
                    && baselineTransitionChanged
                    && transitionDevice is not null
                    && transitionIntent is not null)
                {
                    failedEntryRestoration =
                        await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
            }
            if (failedEntryRestoration is { Succeeded: false })
            {
                string restorationMessage = GetStatus().Message
                    ?? $"HyperBoost was not submitted, but its entry-time intent could not be restored. {failedEntryRestoration.Message} Control writes remain blocked.";
                var restorationFailed = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    restorationMessage);
                history.RecordSummary(
                    restorationFailed.Status,
                    restorationFailed.Message);
                return restorationFailed;
            }
            var result = new ControlOperationResult(
                ControlOperationStatus.Blocked,
                exception.Message);
            history.RecordSummary(result.Status, result.Message);
            return result;
        }
        catch (BladeWriteException exception)
        {
            HyperBoostIntentRestorationResult? failedEntryRestoration = null;
            if (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(writeBaseline, enabled, pendingBaseMode);
                PublishPendingHyperBoostStatus(exception.Message);

                if (hyperBoostWriteAttempted
                    && !setterCompleted
                    && transitionDevice is not null
                    && writeBaseline?.ConfirmedState is not null)
                {
                    ControlOperationResult? resolved =
                        await TryResolveIndeterminateHyperBoostWriteAsync(
                                enabled,
                                transitionDevice,
                                writeBaseline)
                            .ConfigureAwait(false);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
            }
            else if (ownershipJournalPrepared)
            {
                if (enabled
                    && baselineTransitionChanged
                    && transitionDevice is not null
                    && transitionIntent is not null)
                {
                    failedEntryRestoration =
                        await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
            }

            if (failedEntryRestoration is { Succeeded: false })
            {
                string restorationMessage = GetStatus().Message
                    ?? $"HyperBoost was not submitted, but its entry-time intent could not be restored. {failedEntryRestoration.Message} Control writes remain blocked.";
                var restorationFailed = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    restorationMessage);
                history.RecordSummary(
                    restorationFailed.Status,
                    restorationFailed.Message);
                return restorationFailed;
            }

            var result = new ControlOperationResult(
                exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                    ? ControlOperationStatus.Blocked
                    : ControlOperationStatus.Failed,
                exception.Message);
            history.RecordSummary(result.Status, result.Message);
            if (exception.SubmissionState != WriteSubmissionState.Indeterminate)
            {
                PublishIfChanged(GetStatus() with { Message = result.Message });
            }
            return result;
        }
        catch
        {
            if (setterCompleted)
            {
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(writeBaseline, enabled, pendingBaseMode);
                PublishPendingHyperBoostStatus(
                    "HyperBoost was submitted, but final readback failed. Thermal writes remain blocked until a fresh observation resolves it.");
                if (!finalHyperBoostReadbackCompleted
                    && transitionDevice is not null
                    && writeBaseline?.ConfirmedState is not null)
                {
                    ControlOperationResult? resolved =
                        await TryResolveIndeterminateHyperBoostWriteAsync(
                                enabled,
                                transitionDevice,
                                writeBaseline)
                            .ConfigureAwait(false);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
            }
            else if (ownershipJournalPrepared)
            {
                if (enabled && baselineTransitionCompleted)
                {
                    LatchPendingHyperBoost(writeBaseline, enabled, pendingBaseMode);
                    PublishPendingHyperBoostStatus(
                        "The HyperBoost entry baseline was established, but the final submission state is unknown. Thermal writes remain blocked until a fresh observation resolves it.");
                }
                else if (enabled
                    && baselineTransitionChanged
                    && transitionDevice is not null
                    && transitionIntent is not null)
                {
                    await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode)
                        .ConfigureAwait(false);
                }
            }
            var result = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "HyperBoost could not be updated. OpenBlade did not retry the write.");
            history.RecordSummary(result.Status, result.Message);
            return result;
        }
        finally
        {
            writeAttemptObserver?.Invoke(hyperBoostWriteAttempted);
            if (!operationGateAlreadyHeld)
            {
                operationGate.Release();
            }
        }
    }

    private sealed record HyperBoostIntentRestorationResult(
        bool Succeeded,
        ConfirmedDeviceState? Readback,
        string Message);

    private static HyperBoostRestoreIntent CreateHyperBoostRestoreIntent(
        DesiredDeviceState desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return new HyperBoostRestoreIntent(
            desired.Performance.Value,
            desired.CustomPerformance,
            desired.GpuClockTuning,
            desired.FanControl
                ?? FanControlSettings.FromLegacy(desired.Fans.Value));
    }

    private static HyperBoostRestoreIntent ResolveHyperBoostExitIntent(
        ServiceStatusSnapshot current,
        HyperBoostRestoreIntent? ownedIntent,
        PowerSource exitPowerSource) => exitPowerSource switch
        {
            PowerSource.PluggedIn => ownedIntent
                ?? CreateHyperBoostRestoreIntent(current.DesiredState),
            PowerSource.Battery or PowerSource.UsbC =>
                CreateHyperBoostRestoreIntent(current.DesiredState),
            _ => HyperBoostRestoreIntent.BalancedAutomatic,
        };

    private void RestoreRecoveredHyperBoostIntentInAutomation(
        HyperBoostRestoreIntent intent)
        => Automation.RestoreHyperBoostIntentUntilPowerSourceChange(intent);

    private static bool IsSupportedFullPowerHyperBoostIntent(
        DeviceCapabilities capabilities,
        HyperBoostRestoreIntent intent)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.IsWellFormed
            || intent.Performance is not (PerformancePreset.Silent
                or PerformancePreset.Balanced
                or PerformancePreset.Performance
                or PerformancePreset.Custom)
            || !capabilities.PerformancePresets.Contains(intent.Performance)
            || !capabilities.FanModes.Contains(intent.FanControl.Mode))
        {
            return false;
        }

        if (intent.Performance == PerformancePreset.Custom
            && capabilities.CustomPerformance?.Supports(intent.CustomPerformance)
                == false)
        {
            return false;
        }

        if (intent.GpuClockTuning is not null
            && capabilities.GpuOverclock?.Access != DeviceFeatureAccess.ReadWrite)
        {
            return false;
        }

        return intent.FanControl.Mode != FanMode.Fixed
            || capabilities.FanSpeedRange?.Supports(
                intent.FanControl.FixedSpeedRpm) == true;
    }

    private static bool ConfirmedStateMatchesHyperBoostRestoreIntent(
        ConfirmedDeviceState confirmed,
        HyperBoostRestoreIntent intent) =>
        !confirmed.HyperBoostActive
        && confirmed.Performance == intent.Performance
        && (intent.Performance != PerformancePreset.Custom
            || confirmed.CustomPerformance == intent.CustomPerformance)
        && intent.FanControl.Mode == FanMode.Automatic
        && confirmed.Fans == FanMode.Automatic
        && intent.GpuClockTuning is null;

    private static bool ConfirmedStateMatchesHyperBoostTransitionBaseline(
        ConfirmedDeviceState confirmed,
        PerformancePreset baseMode) =>
        !confirmed.HyperBoostActive
        && confirmed.Performance == baseMode
        && confirmed.Fans == FanMode.Automatic;

    private static bool HyperBoostBaselineStepSucceeded(
        ScopedApplyOutcome outcome) => outcome.Result.Status is
        ControlResultStatus.Applied or ControlResultStatus.Unchanged;

    private static async Task<ScopedApplyOutcome>
        ResolveIndeterminateHyperBoostFanBaselineAsync(
            IBladeDevice device,
            FanMode? entryFans,
            ScopedApplyOutcome outcome,
            CancellationToken cancellationToken)
    {
        if (HyperBoostBaselineStepSucceeded(outcome)
            || !outcome.WriteAttempted)
        {
            return outcome;
        }

        try
        {
            ConfirmedDeviceState readback = await device
                .ReadPerformanceFanStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (readback.Fans == FanMode.Automatic)
            {
                return outcome with
                {
                    Result = new ControlResult(
                        ControlResultStatus.Applied,
                        "Fans setting applied and confirmed by fresh readback after an indeterminate submission."),
                    Readback = readback,
                    ReadbackMatched = true,
                    DeviceAccessFailed = false,
                    ConfirmationUnavailable = false,
                    PreWriteState = null,
                };
            }

            if (EquivalentFanReadback(entryFans, readback.Fans))
            {
                return outcome with
                {
                    Result = new ControlResult(
                        ControlResultStatus.Failed,
                        $"{outcome.Result.Message} Fresh readback confirmed that the prior fan intent remained active."),
                    Readback = readback,
                    WriteAttempted = false,
                    DeviceAccessFailed = false,
                    ConfirmationUnavailable = false,
                    PreWriteState = null,
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Preserve the indeterminate outcome. The existing reconciliation
            // path will require a later fresh observation before any write.
        }

        return outcome;
    }

    private static bool EquivalentFanReadback(FanMode? left, FanMode? right) =>
        left == right
        || left is { } leftMode
            && right is { } rightMode
            && DeviceSubsystemOrder.IsManualFanMode(leftMode)
            && DeviceSubsystemOrder.IsManualFanMode(rightMode);

    private async Task<ControlOperationResult>
        FinishFailedHyperBoostBaselineTransitionAsync(
            ServiceStatusSnapshot baseline,
            ScopedApplyOutcome outcome,
            PerformancePreset baseMode,
            IBladeDevice device,
            HyperBoostRestoreIntent intent,
            bool baselineChanged)
    {
        ControlOperationStatus status;
        string message;
        if (outcome.WriteAttempted)
        {
            LatchPendingHyperBoost(
                baseline,
                enabled: true,
                baseMode: baseMode);
            message =
                $"HyperBoost was not submitted because its Balanced Automatic baseline was not confirmed. {outcome.Result.Message} OpenBlade will not retry the indeterminate write.";
            PublishPendingHyperBoostStatus(message);
            status = ControlOperationStatus.Failed;
        }
        else
        {
            if (baselineChanged)
            {
                HyperBoostIntentRestorationResult restoration =
                    await RestoreKnownFailedHyperBoostEntryAsync(
                        device,
                        intent,
                        baseMode)
                    .ConfigureAwait(false);
                if (!restoration.Succeeded)
                {
                    message = GetStatus().Message
                        ?? $"HyperBoost was not submitted, but its entry-time intent could not be restored. {restoration.Message} Control writes remain blocked.";
                    status = ControlOperationStatus.Failed;
                    var failed = new ControlOperationResult(status, message);
                    history.RecordSummary(failed.Status, failed.Message);
                    return failed;
                }
            }
            else
            {
                await RestorePreparedHyperBoostJournalAsync(
                        enabled: true,
                        baseMode: baseMode)
                    .ConfigureAwait(false);
            }
            message =
                baselineChanged
                    ? $"HyperBoost was not submitted because its Balanced Automatic baseline could not be established. {outcome.Result.Message} The entry-time intent was restored."
                    : $"HyperBoost was not submitted because its Balanced Automatic baseline could not be established. {outcome.Result.Message}";
            status = outcome.Result.Status == ControlResultStatus.Blocked
                ? ControlOperationStatus.Blocked
                : ControlOperationStatus.Failed;
            PublishIfChanged(GetStatus() with { Message = message });
        }

        var result = new ControlOperationResult(status, message);
        history.RecordSummary(result.Status, result.Message);
        return result;
    }

    private async Task<HyperBoostIntentRestorationResult>
        RestoreHyperBoostIntentAsync(
            IBladeDevice device,
            HyperBoostRestoreIntent intent,
            ConfirmedDeviceState? preservedState,
            CancellationToken cancellationToken)
    {
        var coordinator = new ControlCoordinator(
            device,
            device.ReadPerformanceFanStateAsync,
            resolveIndeterminatePerformanceWrites: true);
        if (DeviceSubsystemOrder.IsManualFanMode(intent.FanControl.Mode))
        {
            ScopedApplyOutcome fans = await coordinator
                .ApplyFanControlTransitionOutcomeAsync(
                    FanControlSettings.FromLegacy(FanMode.Automatic),
                    intent.FanControl,
                    intent.Performance,
                    intent.CustomPerformance,
                    cancellationToken)
                .ConfigureAwait(false);
            if (ShouldRetryHyperBoostRestorationPreWriteRead(fans))
            {
                fans = await coordinator
                    .ApplyFanControlTransitionOutcomeAsync(
                        FanControlSettings.FromLegacy(FanMode.Automatic),
                        intent.FanControl,
                        intent.Performance,
                        intent.CustomPerformance,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            UpdateOwnedFanControlSettings(
                intent.FanControl,
                fans.Result,
                fans.WriteAttempted);
            return new HyperBoostIntentRestorationResult(
                HyperBoostBaselineStepSucceeded(fans),
                MergeHyperBoostThermalState(preservedState, fans.Readback),
                fans.Result.Message);
        }

        ScopedApplyOutcome performance = await coordinator
            .ApplyPerformanceOutcomeAsync(
                intent.Performance,
                intent.CustomPerformance,
                intent.GpuClockTuning,
                cancellationToken)
            .ConfigureAwait(false);
        if (ShouldRetryHyperBoostRestorationPreWriteRead(performance))
        {
            performance = await coordinator
                .ApplyPerformanceOutcomeAsync(
                    intent.Performance,
                    intent.CustomPerformance,
                    intent.GpuClockTuning,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (!HyperBoostBaselineStepSucceeded(performance))
        {
            return new HyperBoostIntentRestorationResult(
                Succeeded: false,
                MergeHyperBoostThermalState(preservedState, performance.Readback),
                performance.Result.Message);
        }

        ScopedApplyOutcome automaticFans = await coordinator
            .ApplyFanControlOutcomeAsync(intent.FanControl, cancellationToken)
            .ConfigureAwait(false);
        if (ShouldRetryHyperBoostRestorationPreWriteRead(automaticFans))
        {
            automaticFans = await coordinator
                .ApplyFanControlOutcomeAsync(intent.FanControl, cancellationToken)
                .ConfigureAwait(false);
        }
        UpdateOwnedFanControlSettings(
            intent.FanControl,
            automaticFans.Result,
            automaticFans.WriteAttempted);
        return new HyperBoostIntentRestorationResult(
            HyperBoostBaselineStepSucceeded(automaticFans),
            MergeHyperBoostThermalState(
                preservedState,
                automaticFans.Readback ?? performance.Readback),
            automaticFans.Result.Message);
    }

    private static async Task<ConfirmedDeviceState> ReadHyperBoostThermalStateAsync(
        IBladeDevice device,
        ConfirmedDeviceState? preservedState,
        CancellationToken cancellationToken)
    {
        ConfirmedDeviceState thermalState = await device
            .ReadPerformanceFanStateAsync(cancellationToken)
            .ConfigureAwait(false);
        return MergeHyperBoostThermalState(preservedState, thermalState)
            ?? thermalState;
    }

    private async Task<ControlOperationResult?>
        TryResolveIndeterminateHyperBoostWriteAsync(
        bool enabled,
        IBladeDevice device,
        ServiceStatusSnapshot baseline)
    {
        PowerSource powerSource = await ResolveCurrentPowerSourceAsync(
                CancellationToken.None)
            .ConfigureAwait(false);
        if (powerSource == PowerSource.ExternalUnknown)
        {
            return null;
        }

        ConfirmedDeviceState readback;
        try
        {
            readback = await ReadHyperBoostThermalStateAsync(
                    device,
                    baseline.ConfirmedState,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        bool deferPowerSourceChange = readback.HyperBoostActive
            && Automation.PowerSource != powerSource;
        if (!deferPowerSourceChange)
        {
            _ = ChangePowerSourceCore(powerSource);
        }
        ObserveAutomaticState(readback);
        ReconcilePendingWriteConfirmations(readback, powerSource, device);
        IndeterminateHyperBoostReconciliation reconciliation = enabled
            ? await ReconcileIndeterminateHyperBoostEntryAsync(
                    readback,
                    powerSource,
                    device,
                    CancellationToken.None)
                .ConfigureAwait(false)
            : await ReconcileIndeterminateHyperBoostExitAsync(
                    readback,
                    powerSource,
                    device,
                    CancellationToken.None)
                .ConfigureAwait(false);
        if (reconciliation.Resolution == IndeterminateHyperBoostResolution.None
            || reconciliation.Message is null)
        {
            return null;
        }

        PublishIfChanged(GetStatus() with
        {
            ConfirmedState = reconciliation.Confirmed,
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = reconciliation.Resolution
                == IndeterminateHyperBoostResolution.Unsafe,
            ActivePowerSource = deferPowerSourceChange
                ? baseline.ActivePowerSource
                : powerSource,
            DesiredState = Automation.Resolve(reconciliation.Confirmed),
            Message = reconciliation.Message,
        });
        return new ControlOperationResult(
            reconciliation.Resolution == IndeterminateHyperBoostResolution.Applied
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Failed,
            reconciliation.Message)
        {
            HyperBoostRestoredPerformance =
                reconciliation.Resolution == IndeterminateHyperBoostResolution.Applied
                    ? reconciliation.RestoredPerformance
                    : null,
        };
    }

    private static ConfirmedDeviceState? MergeHyperBoostThermalState(
        ConfirmedDeviceState? preservedState,
        ConfirmedDeviceState? thermalState)
    {
        if (thermalState is null || preservedState is null)
        {
            return thermalState ?? preservedState;
        }

        return preservedState with
        {
            Performance = thermalState.Performance,
            Fans = thermalState.Fans,
            HyperBoostActive = thermalState.HyperBoostActive,
            CustomPerformance = thermalState.CustomPerformance,
            GpuClockOffsets = thermalState.GpuClockOffsets,
        };
    }

    private static bool ShouldRetryHyperBoostRestorationPreWriteRead(
        ScopedApplyOutcome outcome) =>
        !HyperBoostBaselineStepSucceeded(outcome)
        && outcome.DeviceAccessFailed
        && !outcome.WriteAttempted;

    private async Task<IndeterminateHyperBoostReconciliation>
        ReconcileIndeterminateHyperBoostExitAsync(
            ConfirmedDeviceState confirmed,
            PowerSource powerSource,
            IBladeDevice device,
            CancellationToken cancellationToken)
    {
        if (pendingIndeterminateHyperBoostExitIntent is not { } savedIntent)
        {
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                Message: null,
                IndeterminateHyperBoostResolution.None);
        }

        if (confirmed.HyperBoostActive)
        {
            bool journalRecovered = await TryWriteHyperBoostOwnershipJournalAsync(
                    HyperBoostOwnershipJournalState.OwnedConfirmed,
                    ownedHyperBoostBaseMode ?? PerformancePreset.Balanced,
                    ownedHyperBoostRequiresCoolingPad,
                    "Fresh readback confirmed that an indeterminate HyperBoost disable did not apply, but its ownership journal could not be returned to owned state.",
                    savedIntent)
                .ConfigureAwait(false);
            driftTracker.Observe(confirmed);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
            string activeMessage = journalRecovered
                ? "Fresh readback confirmed that HyperBoost remained active after the indeterminate disable. OpenBlade did not retry the write."
                : "Fresh readback confirmed that HyperBoost remained active after the indeterminate disable, but its ownership journal could not be made safe. Control writes remain blocked.";
            history.RecordSummary(
                journalRecovered
                    ? ControlOperationStatus.Stored
                    : ControlOperationStatus.Failed,
                activeMessage);
            pendingIndeterminateHyperBoostExitIntent = null;
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                activeMessage,
                journalRecovered
                    ? IndeterminateHyperBoostResolution.NotApplied
                    : IndeterminateHyperBoostResolution.Unsafe);
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || hyperBoostJournalSafetyBlock
            || current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostExitIntent = null;
            ClearOwnedHyperBoostState();
            const string blockedMessage =
                "Fresh readback confirmed that HyperBoost is off after an indeterminate disable, but the prior performance and fan intent could not be restored because write safety is not confirmed. OpenBlade did not retry the HyperBoost write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, blockedMessage);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                blockedMessage,
                IndeterminateHyperBoostResolution.Unsafe);
        }

        DesiredDeviceState currentDesired = Automation.Resolve(confirmed);
        HyperBoostRestoreIntent restoreIntent = powerSource switch
        {
            PowerSource.PluggedIn => savedIntent,
            PowerSource.Battery or PowerSource.UsbC =>
                CreateHyperBoostRestoreIntent(currentDesired),
            _ => HyperBoostRestoreIntent.BalancedAutomatic,
        };
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    restoreIntent,
                    confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
        pendingIndeterminateHyperBoostExitIntent = null;
        ConfirmedDeviceState restored = restoration.Readback
            ?? confirmed;
        ClearOwnedHyperBoostState();
        if (!restoration.Succeeded)
        {
            hyperBoostJournalSafetyBlock = true;
            string failedMessage =
                $"Fresh readback confirmed that HyperBoost is off after an indeterminate disable, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade did not retry the HyperBoost write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, failedMessage);
            return new IndeterminateHyperBoostReconciliation(
                restored,
                SuppressInactiveJournalRetirement: true,
                failedMessage,
                IndeterminateHyperBoostResolution.Unsafe);
        }

        driftTracker.Observe(restored);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
        bool journalRetired = await TryClearHyperBoostOwnershipJournalAsync(
                "HyperBoost was confirmed off and the prior intent was restored after an indeterminate disable, but its ownership journal could not be cleared.")
            .ConfigureAwait(false);
        string restoredMessage = journalRetired
            ? $"Fresh readback confirmed that HyperBoost is off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write."
            : $"Fresh readback confirmed that HyperBoost is off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write, but its ownership journal could not be cleared; control writes remain blocked.";
        history.RecordSummary(
            journalRetired
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Failed,
            restoredMessage);
        return new IndeterminateHyperBoostReconciliation(
            restored,
            SuppressInactiveJournalRetirement: !journalRetired,
            restoredMessage,
            journalRetired
                ? IndeterminateHyperBoostResolution.Applied
                : IndeterminateHyperBoostResolution.Unsafe,
            RestoredPerformance: journalRetired
                ? restoreIntent.Performance
                : null);
    }

    private async Task<IndeterminateHyperBoostReconciliation>
        ReconcileIndeterminateHyperBoostEntryAsync(
            ConfirmedDeviceState confirmed,
            PowerSource powerSource,
            IBladeDevice device,
            CancellationToken cancellationToken)
    {
        if (pendingIndeterminateHyperBoostEntryIntent is not { } savedIntent)
        {
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                Message: null,
                IndeterminateHyperBoostResolution.None);
        }

        PerformancePreset baseMode = pendingIndeterminateHyperBoostEntryBaseMode;
        if (confirmed.HyperBoostActive)
        {
            bool journalFinalized = await TryWriteHyperBoostOwnershipJournalAsync(
                    HyperBoostOwnershipJournalState.OwnedConfirmed,
                    baseMode,
                    requiresCoolingPad: false,
                    "Fresh readback confirmed active HyperBoost after an indeterminate enable, but its ownership journal could not be finalized.",
                    savedIntent)
                .ConfigureAwait(false);
            pendingIndeterminateHyperBoostEntryIntent = null;
            if (journalFinalized)
            {
                hyperBoostOwnershipGeneration++;
                ownsHyperBoost = true;
                ownedHyperBoostBaseMode = baseMode;
                ownedHyperBoostRestoreIntent = savedIntent;
                ownedHyperBoostRequiresCoolingPad = false;
            }

            driftTracker.Observe(confirmed);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
            string activeMessage = journalFinalized
                ? "Fresh readback confirmed that HyperBoost became active after the indeterminate enable. OpenBlade retained ownership without retrying the write."
                : "Fresh readback confirmed that HyperBoost became active after the indeterminate enable, but its ownership journal could not be made safe. Control writes remain blocked.";
            history.RecordSummary(
                journalFinalized
                    ? ControlOperationStatus.Stored
                    : ControlOperationStatus.Failed,
                activeMessage);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                activeMessage,
                journalFinalized
                    ? IndeterminateHyperBoostResolution.Applied
                    : IndeterminateHyperBoostResolution.Unsafe);
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || hyperBoostJournalSafetyBlock
            || current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostEntryIntent = null;
            ClearOwnedHyperBoostState();
            const string blockedMessage =
                "Fresh readback confirmed that HyperBoost stayed off after an indeterminate enable, but the prior performance and fan intent could not be restored because write safety is not confirmed. OpenBlade did not retry the HyperBoost write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, blockedMessage);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                blockedMessage,
                IndeterminateHyperBoostResolution.Unsafe);
        }

        DesiredDeviceState currentDesired = Automation.Resolve(confirmed);
        HyperBoostRestoreIntent restoreIntent = powerSource switch
        {
            PowerSource.PluggedIn => savedIntent,
            PowerSource.Battery or PowerSource.UsbC =>
                CreateHyperBoostRestoreIntent(currentDesired),
            _ => HyperBoostRestoreIntent.BalancedAutomatic,
        };
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    restoreIntent,
                    confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
        pendingIndeterminateHyperBoostEntryIntent = null;
        ConfirmedDeviceState restored = restoration.Readback
            ?? confirmed;
        ClearOwnedHyperBoostState();
        if (!restoration.Succeeded)
        {
            hyperBoostJournalSafetyBlock = true;
            string failedMessage =
                $"Fresh readback confirmed that HyperBoost stayed off after an indeterminate enable, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade did not retry the HyperBoost write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, failedMessage);
            return new IndeterminateHyperBoostReconciliation(
                restored,
                SuppressInactiveJournalRetirement: true,
                failedMessage,
                IndeterminateHyperBoostResolution.Unsafe);
        }

        driftTracker.Observe(restored);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
        bool journalRetired = await TryClearHyperBoostOwnershipJournalAsync(
                "HyperBoost was confirmed inactive and the prior intent was restored after an indeterminate enable, but its ownership journal could not be cleared.")
            .ConfigureAwait(false);
        string restoredMessage = journalRetired
            ? $"Fresh readback confirmed that HyperBoost stayed off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write."
            : $"Fresh readback confirmed that HyperBoost stayed off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write, but its ownership journal could not be cleared; control writes remain blocked.";
        history.RecordSummary(
            journalRetired
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Failed,
            restoredMessage);
        return new IndeterminateHyperBoostReconciliation(
            restored,
            SuppressInactiveJournalRetirement: true,
            restoredMessage,
            journalRetired
                ? IndeterminateHyperBoostResolution.NotApplied
                : IndeterminateHyperBoostResolution.Unsafe);
    }

    private void ClearOwnedHyperBoostState()
    {
        hyperBoostOwnershipGeneration++;
        ownsHyperBoost = false;
        ownedHyperBoostBaseMode = null;
        ownedHyperBoostRestoreIntent = null;
        ownedHyperBoostRequiresCoolingPad = false;
        pendingOwnedHyperBoostPowerBoundaryExit = false;
        ownedHyperBoostPowerBoundaryExitSubmitted = false;
    }

    private async Task<HyperBoostIntentRestorationResult>
        RestoreKnownFailedHyperBoostEntryAsync(
        IBladeDevice device,
        HyperBoostRestoreIntent intent,
        PerformancePreset baseMode)
    {
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    intent,
                    GetStatus().ConfirmedState,
                    CancellationToken.None)
                .ConfigureAwait(false);
        if (restoration.Succeeded)
        {
            bool journalRecovered = await RestorePreparedHyperBoostJournalAsync(
                    enabled: true,
                    baseMode: baseMode)
                .ConfigureAwait(false);
            if (journalRecovered)
            {
                return restoration;
            }

            var journalFailure = new HyperBoostIntentRestorationResult(
                    Succeeded: false,
                    restoration.Readback,
                    "The entry-time intent was restored, but the pending ownership journal could not be cleared.");
            string journalMessage =
                $"HyperBoost was not submitted. {journalFailure.Message} Control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, journalMessage);
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = journalFailure.Readback ?? GetStatus().ConfirmedState,
                WritesBlocked = true,
                Message = journalMessage,
            });
            return journalFailure;
        }

        hyperBoostJournalSafetyBlock = true;
        string message =
            $"HyperBoost was not submitted, but its entry baseline could not be restored. {restoration.Message} Control writes remain blocked.";
        history.RecordSummary(ControlOperationStatus.Failed, message);
        PublishIfChanged(GetStatus() with
        {
            ConfirmedState = restoration.Readback ?? GetStatus().ConfirmedState,
            WritesBlocked = true,
            Message = message,
        });
        return restoration;
    }

    private Task<bool> RestorePreparedHyperBoostJournalAsync(
        bool enabled,
        PerformancePreset baseMode) => enabled
        ? TryClearHyperBoostOwnershipJournalAsync(
            "A non-submitted HyperBoost enable left a pending ownership journal that could not be cleared.")
        : TryWriteHyperBoostOwnershipJournalAsync(
            HyperBoostOwnershipJournalState.OwnedConfirmed,
            baseMode,
            ownedHyperBoostRequiresCoolingPad,
            "A non-submitted HyperBoost restore left a pending ownership journal that could not be returned to owned state.");

    private async Task<bool> TryWriteHyperBoostOwnershipJournalAsync(
        HyperBoostOwnershipJournalState state,
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        string failureMessage,
        HyperBoostRestoreIntent? restoreIntent = null)
    {
        try
        {
            await hyperBoostOwnershipJournalStore.WriteAsync(
                new HyperBoostOwnershipJournal(
                    state,
                    baseMode,
                    requiresCoolingPad,
                    timeProvider.GetUtcNow())
                {
                    RestoreIntent = restoreIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic,
                },
                CancellationToken.None)
            .ConfigureAwait(false);
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = state
                == HyperBoostOwnershipJournalState.RestorePending;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} {exception.Message}");
            return false;
        }
    }

    private async Task<ControlOperationResult>
        RestoreAfterHyperBoostJournalFailureAsync(
            IBladeDevice device,
            ServiceStatusSnapshot baseline,
            ConfirmedDeviceState activeReadback,
            PerformancePreset baseMode,
            bool requiresCoolingPad,
            HyperBoostRestoreIntent restoreIntent)
    {
        const string failurePrefix =
            "HyperBoost was activated, but durable ownership could not be recorded.";
        try
        {
            await device.SetHyperBoostAsync(
                    enabled: false,
                    baseMode,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ConfirmedDeviceState restored = await ReadHyperBoostThermalStateAsync(
                    device,
                    baseline.ConfirmedState,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (restored is
                {
                    HyperBoostActive: false,
                    Performance: var restoredBase,
                    Fans: FanMode.Automatic,
                }
                && restoredBase == baseMode)
            {
                HyperBoostIntentRestorationResult intentRestoration =
                    await RestoreHyperBoostIntentAsync(
                            device,
                            restoreIntent,
                            restored,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                restored = intentRestoration.Readback ?? restored;
                if (!intentRestoration.Succeeded)
                {
                    hyperBoostJournalSafetyBlock = true;
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    string restorationFailed =
                        $"{failurePrefix} HyperBoost is off, but the entry-time performance or fan intent could not be restored. {intentRestoration.Message} Control writes remain blocked.";
                    var failed = new ControlOperationResult(
                        ControlOperationStatus.Failed,
                        restorationFailed);
                    history.RecordSummary(failed.Status, failed.Message);
                    PublishIfChanged(baseline with
                    {
                        ConfirmedState = restored,
                        WritesBlocked = true,
                        Message = failed.Message,
                    });
                    return failed;
                }

                bool retired = await RetireHyperBoostOwnershipJournalAsync(
                        baseMode,
                        requiresCoolingPad,
                        restoreIntent,
                        "HyperBoost was restored after its ownership journal could not be finalized.")
                    .ConfigureAwait(false);
                hyperBoostOwnershipGeneration++;
                ownsHyperBoost = false;
                ownedHyperBoostBaseMode = null;
                ownedHyperBoostRestoreIntent = null;
                ownedHyperBoostRequiresCoolingPad = false;
                string message = retired
                    ? $"{failurePrefix} OpenBlade restored the entry-time {PerformanceLabel(restoreIntent.Performance)} performance and fan intent and did not leave HyperBoost active."
                    : $"{failurePrefix} OpenBlade restored the entry-time {PerformanceLabel(restoreIntent.Performance)} performance and fan intent, but the pending ownership journal could not be retired; control writes remain blocked.";
                var result = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    message);
                history.RecordSummary(result.Status, result.Message);
                PublishIfChanged(baseline with
                {
                    ConfirmedState = restored,
                    WritesBlocked = baseline.WritesBlocked || !retired,
                    Message = result.Message,
                });
                return result;
            }
        }
        catch (Exception exception)
        {
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failurePrefix} The one-shot restoration did not complete: {exception.Message}");
        }

        LatchPendingHyperBoost(baseline, enabled: false, baseMode);
        const string uncertainMessage =
            "HyperBoost ownership could not be recorded and the one-shot restoration was not confirmed. OpenBlade will not retry; thermal writes remain blocked until a fresh observation and manual recovery resolve the state.";
        var uncertain = new ControlOperationResult(
            ControlOperationStatus.Failed,
            uncertainMessage);
        history.RecordSummary(uncertain.Status, uncertain.Message);
        PublishIfChanged(baseline with
        {
            ConfirmedState = activeReadback,
            Message = uncertain.Message,
        });
        return uncertain;
    }

    private async Task<bool> TryClearHyperBoostOwnershipJournalAsync(
        string failureMessage)
    {
        try
        {
            await hyperBoostOwnershipJournalStore.ClearAsync(CancellationToken.None)
                .ConfigureAwait(false);
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} {exception.Message}");
            return false;
        }
    }

    private async Task<bool> RetireHyperBoostOwnershipJournalAsync(
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        HyperBoostRestoreIntent restoreIntent,
        string failureMessage)
    {
        bool relinquished = false;
        try
        {
            await hyperBoostOwnershipJournalStore.WriteAsync(
                    new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.Relinquished,
                        baseMode,
                        requiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = restoreIntent,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            relinquished = true;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
        }
        catch (Exception exception)
        {
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The relinquished marker could not be written: {exception.Message}");
        }

        try
        {
            await hyperBoostOwnershipJournalStore.ClearAsync(CancellationToken.None)
                .ConfigureAwait(false);
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }
        catch (Exception exception)
        {
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The journal could not be removed: {exception.Message}");
            hyperBoostJournalSafetyBlock = !relinquished;
            return relinquished;
        }
    }

    private async Task<bool> RetireInactiveHyperBoostJournalIfPresentAsync(
        string failureMessage,
        ConfirmedDeviceState? confirmed = null)
    {
        HyperBoostOwnershipJournalLoadResult load;
        try
        {
            load = await hyperBoostOwnershipJournalStore.LoadAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The journal could not be inspected: {exception.Message}");
            return false;
        }

        if (load.Invalid)
        {
            hyperBoostJournalSafetyBlock = true;
            hyperBoostRestorePending = false;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The journal is invalid and was preserved for manual inspection.");
            return false;
        }

        if (load.Journal is not { } journal)
        {
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }

        if (journal.State == HyperBoostOwnershipJournalState.RestorePending)
        {
            hyperBoostRestorePending = true;
            if (confirmed is not null
                && ConfirmedStateMatchesHyperBoostRestoreIntent(
                    confirmed,
                    journal.RestoreIntent))
            {
                bool cleared = await TryClearHyperBoostOwnershipJournalAsync(
                        "Fresh readback confirmed the saved HyperBoost restore intent, but its ownership journal could not be cleared.")
                    .ConfigureAwait(false);
                if (cleared)
                {
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Performance,
                        matched: true);
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Fans,
                        matched: true);
                    history.RecordSummary(
                        ControlOperationStatus.Applied,
                        $"Fresh readback confirmed that HyperBoost is off and the saved {PerformanceLabel(journal.RestoreIntent.Performance)} performance and Automatic fan intent is restored. The pending ownership journal was retired without another hardware write.");
                }
                return cleared;
            }

            hyperBoostJournalSafetyBlock = true;
            if (lastReportedRetainedHyperBoostRestoreJournal != journal)
            {
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The pending restore marker was preserved because inactive HyperBoost does not confirm the saved performance and fan intent.");
                lastReportedRetainedHyperBoostRestoreJournal = journal;
            }
            return false;
        }

        hyperBoostRestorePending = false;
        lastReportedRetainedHyperBoostRestoreJournal = null;

        return await RetireHyperBoostOwnershipJournalAsync(
                journal.BaseMode,
                journal.RequiresCoolingPad,
                journal.RestoreIntent,
                failureMessage)
            .ConfigureAwait(false);
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
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);

            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
            }

            if (ChangePowerSourceCore(powerSource))
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
            DiscardExpiredResumeBaselineCore();
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
            bool canUseReadOnlyFastPath = pendingResumeStateBaseline?.ResumeStarted != true
                && !sourceChanged
                && CanUseObservationFastPath(
                    includeLiveReadings,
                    powerSource != PowerSource.ExternalUnknown);
            Exception? lightingRelinquishFailure = null;
            bool transitionMessageHasPriority = false;
            try
            {
                await RefreshControllerPresenceAndRelinquishLightingAsync(
                    cancellationToken,
                    allowRecentObservation: canUseReadOnlyFastPath).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lightingRelinquishFailure = exception;
            }
            bool changed = powerSource != PowerSource.ExternalUnknown
                && ChangePowerSourceCore(powerSource);
            if (changed)
            {
                RecordResumePowerSourceTransitionCore(powerSource);
            }
            if (startupProfileReconciliationPending
                && startupProfileReconciliationRequested
                && !unexpectedExitRecoveryPending
                && powerSource != PowerSource.ExternalUnknown)
            {
                await ReconcileStartupProfileCoreAsync(
                    powerSource,
                    cancellationToken).ConfigureAwait(false);
                transitionMessageHasPriority = true;
            }
            else if (ShouldRetryPendingOwnedHyperBoostPowerBoundaryExit(
                powerSource))
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
                transitionMessageHasPriority = true;
            }
            else if (changed && !startupProfileReconciliationPending)
            {
                await ApplyFullTransitionAsync(powerSource, cancellationToken);
                transitionMessageHasPriority = true;
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
                        cancellationToken,
                        skipLightingHandoff: lightingRelinquishFailure is not null)
                        .ConfigureAwait(false);
                }
            }

            if (lightingRelinquishFailure is not null && GetStatus().DeviceVerified)
            {
                PublishLightingRelinquishFailure(
                    lightingRelinquishFailure,
                    publishStatus: !transitionMessageHasPriority);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task ObserveAsync(CancellationToken cancellationToken) =>
        ObserveAsync(includeLiveReadings: false, cancellationToken);

    public async Task ReconcileStartupProfileAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            startupProfileReconciliationRequested = true;
            if (!startupProfileReconciliationPending)
            {
                return;
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(
                cancellationToken).ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return;
            }

            await ReconcileStartupProfileCoreAsync(
                powerSource,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task ReconcileUnexpectedExitAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        bool restoreJournaledHyperBoost = false;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!unexpectedExitRecoveryPending)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            startupProfileReconciliationRequested = true;
            HyperBoostOwnershipJournalLoadResult journalLoad =
                await hyperBoostOwnershipJournalStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (journalLoad.Invalid)
            {
                hyperBoostJournalSafetyBlock = true;
                const string invalidJournal =
                    "The HyperBoost ownership journal is invalid. OpenBlade will not issue an automatic recovery write.";
                history.RecordSummary(ControlOperationStatus.Failed, invalidJournal);
                PublishIfChanged(GetStatus() with
                {
                    WritesBlocked = true,
                    Message = invalidJournal,
                });
                return;
            }

            if (journalLoad.Journal is { } journal)
            {
                ServiceStatusSnapshot current = GetStatus();
                if (!current.DeviceVerified
                    || current.ConfirmedState is not { } confirmed)
                {
                    return;
                }

                if (!confirmed.HyperBoostActive)
                {
                    bool retired = await RetireInactiveHyperBoostJournalIfPresentAsync(
                            "Inactive HyperBoost was confirmed after an unexpected exit, but its stale ownership journal could not be retired.")
                        .ConfigureAwait(false);
                    if (!retired)
                    {
                        const string retirementFailed =
                            "Inactive HyperBoost was confirmed after an unexpected exit, but OpenBlade could not safely retire its ownership journal. Control writes remain blocked.";
                        PublishIfChanged(current with
                        {
                            WritesBlocked = true,
                            Message = retirementFailed,
                        });
                        return;
                    }
                }
                else if (journal.State
                    != HyperBoostOwnershipJournalState.OwnedConfirmed)
                {
                    hyperBoostJournalSafetyBlock = true;
                    string pending =
                        $"OpenBlade found active HyperBoost after an unexpected exit, but the ownership journal is {journal.State}. Because ownership was never durably confirmed, no automatic recovery write was issued.";
                    history.RecordSummary(ControlOperationStatus.Failed, pending);
                    PublishIfChanged(current with
                    {
                        WritesBlocked = true,
                        Message = pending,
                    });
                    return;
                }
                else
                {
                    DeviceFeatureAccess? baseAccess = current.Capabilities
                        ?.HyperBoostBaseModes?.AccessFor(journal.BaseMode)
                        ?? (journal.BaseMode == PerformancePreset.Balanced
                            ? current.Capabilities?.HyperBoostAccess
                            : null);
                    if (current.Capabilities?.HyperBoostAccess
                            != DeviceFeatureAccess.ReadWrite
                        || baseAccess != DeviceFeatureAccess.ReadWrite)
                    {
                        hyperBoostJournalSafetyBlock = true;
                        string unavailable =
                            $"OpenBlade found journaled HyperBoost ownership after an unexpected exit, but direct restoration to {PerformanceLabel(journal.BaseMode)} is not admitted. No recovery write was issued.";
                        history.RecordSummary(ControlOperationStatus.Failed, unavailable);
                        PublishIfChanged(current with
                        {
                            WritesBlocked = true,
                            Message = unavailable,
                        });
                        return;
                    }

                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = true;
                    ownedHyperBoostBaseMode = journal.BaseMode;
                    ownedHyperBoostRestoreIntent = journal.RestoreIntent;
                    ownedHyperBoostRequiresCoolingPad = journal.RequiresCoolingPad;
                    restoreJournaledHyperBoost = true;
                }
            }

            if (!restoreJournaledHyperBoost)
            {
                if (unexpectedExitRecoveryDeferredForUserConfiguration)
                {
                    return;
                }

                if (GetStatus() is { DeviceVerified: true, WritesBlocked: false })
                {
                    CompleteUnexpectedExitRecoveryCore();
                }
            }
        }
        finally
        {
            operationGate.Release();
        }

        if (!restoreJournaledHyperBoost)
        {
            return;
        }

        ControlOperationResult restoration =
            await RestoreOwnedHyperBoostForLifecycleCoreAsync(
                    "unexpected-exit recovery",
                    cancellationToken)
                .ConfigureAwait(false);
        if (restoration.Status != ControlOperationStatus.Applied)
        {
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!unexpectedExitRecoveryPending
                || unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                return;
            }

            if (GetStatus() is { DeviceVerified: true, WritesBlocked: false })
            {
                CompleteUnexpectedExitRecoveryCore();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task PrepareManualFanCurveRecoveryAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareManualFanCurveRecoveryCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task PrepareManualFanCurveRecoveryCoreAsync(
        CancellationToken cancellationToken,
        Func<bool>? stillControlsSession = null)
    {
        if (!manualFanCurveRecoveryPending)
        {
            return;
        }

        if (unexpectedExitRecoveryDeferredForUserConfiguration)
        {
            await MaintainDeferredManualFanSafetyFloorCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        FanControlSettings settings = current.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(current.DesiredState.Fans.Value);
        if (settings.Mode != FanMode.ManualCurve)
        {
            manualFanCurveRecoveryPending = false;
            return;
        }

        if (!current.DeviceVerified
            || current.WritesBlocked
            || current.ExternalChanges.Contains(DeviceSubsystem.Fans)
            || !DeviceSubsystemOrder.IsManualFanMode(
                current.ConfirmedState?.Fans ?? FanMode.Automatic))
        {
            if (!current.ExternalChanges.Contains(DeviceSubsystem.Fans))
            {
                const string message =
                    "Manual fan-curve crash recovery could not confirm existing fan ownership; automatic recovery is blocked.";
                initialAutomaticComparisons.Add(DeviceSubsystem.Fans);
                driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
                history.RecordSummary(ControlOperationStatus.Blocked, message);
                PublishIfChanged(current with
                {
                    ExternalChanges = driftTracker.ExternalChanges,
                    Message = message,
                });
            }

            return;
        }

        if (stillControlsSession?.Invoke() == false)
        {
            return;
        }

        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        if (stillControlsSession?.Invoke() == false)
        {
            return;
        }

        // Consume the bounded attempt immediately before admission. A plain
        // pre-submission cancellation re-arms it; an indeterminate submitted
        // write remains consumed and is never retried automatically.
        manualFanCurveRecoveryPending = false;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                current.ActivePowerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyFanControlOutcomeAsync(
                        settings,
                        token,
                        stillControlsSession),
                cancellationToken).ConfigureAwait(false);
        }
        catch (WriteAuthorityLostException)
        {
            manualFanCurveRecoveryPending = true;
            throw;
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            manualFanCurveRecoveryPending = true;
            throw;
        }
        if (result.Status == ControlOperationStatus.Applied)
        {
            const string message =
                "Manual fan-curve crash recovery entered maximum speed before resuming host temperature calculations.";
            history.RecordSummary(ControlOperationStatus.Applied, message);
            PublishIfChanged(GetStatus() with { Message = message });
        }
        else
        {
            initialAutomaticComparisons.Add(DeviceSubsystem.Fans);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
            PublishIfChanged(GetStatus() with
            {
                ExternalChanges = driftTracker.ExternalChanges,
                Message = result.Message,
            });
        }
    }

    private async Task MaintainDeferredManualFanSafetyFloorCoreAsync(
        CancellationToken cancellationToken,
        bool allowOwnedAutomaticBaseline = false,
        Func<bool>? stillControlsSession = null)
    {
        if (deferredManualFanSafetyState == DeferredManualFanSafetyState.Lost)
        {
            return;
        }

        PowerSource powerSource = await ResolveCurrentPowerSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if (powerSource == PowerSource.ExternalUnknown
            || stillControlsSession?.Invoke() == false)
        {
            return;
        }

        _ = ChangePowerSourceCore(powerSource);
        PublishDeferredRecoveryPowerSourceCore(powerSource);
        ServiceStatusSnapshot current = GetStatus();
        if (!ValidateDeferredManualFanSafetyStateCore(current))
        {
            return;
        }

        if (powerSource == PowerSource.UsbC)
        {
            // PendingUnowned is intentionally not eligible. A first manual
            // readback cannot prove that the prior service instance wrote it;
            // only this process's confirmed floor write permits handback.
            await HandBackDeferredManualFanSafetyFloorCoreAsync(
                powerSource,
                cancellationToken,
                stillControlsSession).ConfigureAwait(false);
            return;
        }

        bool pendingBaseline =
            deferredManualFanSafetyState == DeferredManualFanSafetyState.PendingUnowned
            && DeviceSubsystemOrder.IsManualFanMode(
                current.ConfirmedState?.Fans ?? FanMode.Automatic);
        bool ownedAutomaticBaseline =
            allowOwnedAutomaticBaseline
            && deferredManualFanSafetyState ==
                DeferredManualFanSafetyState.AutomaticOwned
            && current.ConfirmedState?.Fans == FanMode.Automatic;
        if (!manualFanCurveRecoveryPending
            || powerSource is not (PowerSource.PluggedIn or PowerSource.Battery)
            || (!pendingBaseline && !ownedAutomaticBaseline)
            || !current.DeviceVerified
            || current.WritesBlocked
            || current.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            if (deferredManualFanSafetyState == DeferredManualFanSafetyState.PendingUnowned
                && current.ConfirmedState?.Fans is not null
                && !pendingBaseline)
            {
                LoseDeferredManualFanSafetyOwnershipCore();
            }

            return;
        }

        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        current = GetStatus();
        if (current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(CompetingControllerProcesses.Contains)
            || stillControlsSession?.Invoke() == false)
        {
            return;
        }

        PowerSource confirmedPowerSource =
            await ResolveCurrentPowerSourceAsync(cancellationToken).ConfigureAwait(false);
        if (confirmedPowerSource != powerSource
            || stillControlsSession?.Invoke() == false)
        {
            if (confirmedPowerSource != PowerSource.ExternalUnknown)
            {
                _ = ChangePowerSourceCore(confirmedPowerSource);
                PublishDeferredRecoveryPowerSourceCore(confirmedPowerSource);
            }

            if (confirmedPowerSource == PowerSource.UsbC)
            {
                await HandBackDeferredManualFanSafetyFloorCoreAsync(
                    confirmedPowerSource,
                    cancellationToken,
                    stillControlsSession).ConfigureAwait(false);
            }

            return;
        }

        var safetyFloor = FanControlSettings.FromLegacy(FanMode.ManualCurve);
        DeferredManualFanSafetyState priorState = deferredManualFanSafetyState;
        FanControlSettings? priorOwnedSettings = lastOwnedFanControlSettings;
        // Revoke any stale ownership before submission. ApplyScopedIntentAsync
        // records the staged ManualCurve settings again only after an Applied
        // floor write, so shutdown cleanup cannot act on an uncertain attempt.
        deferredManualFanSafetyState = DeferredManualFanSafetyState.Lost;
        manualFanCurveRecoveryPending = false;
        lastOwnedFanControlSettings = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyFanControlOutcomeAsync(
                        safetyFloor,
                        token,
                        stillControlsSession,
                        state => DeferredManualFanSafetyStateBlockReason(
                            priorState,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = FanMode.ManualCurve },
                    FanControl = safetyFloor,
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyState = priorState;
            manualFanCurveRecoveryPending = true;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyState = priorState;
            manualFanCurveRecoveryPending = true;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            return;
        }

        deferredManualFanSafetyState = DeferredManualFanSafetyState.FloorOwned;
        const string message =
            "Unexpected-exit recovery established a maximum fan-speed safety floor while user-owned recovery remains deferred.";
        history.RecordSummary(ControlOperationStatus.Applied, message);
        PublishIfChanged(GetStatus() with { Message = message });
    }

    private async Task HandBackDeferredManualFanSafetyFloorCoreAsync(
        PowerSource powerSource,
        CancellationToken cancellationToken,
        Func<bool>? stillControlsSession = null)
    {
        if (deferredManualFanSafetyState != DeferredManualFanSafetyState.FloorOwned
            || stillControlsSession?.Invoke() == false)
        {
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!ValidateDeferredManualFanSafetyStateCore(current)
            || !current.DeviceVerified
            || current.WritesBlocked
            || current.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            return;
        }

        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        current = GetStatus();
        if (current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(CompetingControllerProcesses.Contains)
            || stillControlsSession?.Invoke() == false)
        {
            return;
        }

        PowerSource confirmedPowerSource =
            await ResolveCurrentPowerSourceAsync(cancellationToken).ConfigureAwait(false);
        if (confirmedPowerSource != powerSource
            || stillControlsSession?.Invoke() == false)
        {
            return;
        }

        FanControlSettings automatic = FanControlSettings.FromLegacy(FanMode.Automatic);
        FanControlSettings? priorOwnedSettings = lastOwnedFanControlSettings;
        deferredManualFanSafetyState = DeferredManualFanSafetyState.Lost;
        lastOwnedFanControlSettings = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyFanControlOutcomeAsync(
                        automatic,
                        token,
                        stillControlsSession,
                        state => DeferredManualFanSafetyStateBlockReason(
                            DeferredManualFanSafetyState.FloorOwned,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = FanMode.Automatic },
                    FanControl = automatic,
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyState = DeferredManualFanSafetyState.FloorOwned;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyState = DeferredManualFanSafetyState.FloorOwned;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            return;
        }

        deferredManualFanSafetyState = DeferredManualFanSafetyState.AutomaticOwned;
        manualFanCurveRecoveryPending = true;
        const string message =
            "The deferred crash-recovery fan safety floor was handed back to firmware Automatic mode.";
        history.RecordSummary(ControlOperationStatus.Applied, message);
        PublishIfChanged(GetStatus() with { Message = message });
    }

    private bool ValidateDeferredManualFanSafetyStateCore(
        ServiceStatusSnapshot current)
    {
        FanMode? confirmedFans = current.ConfirmedState?.Fans;
        bool matches = deferredManualFanSafetyState switch
        {
            DeferredManualFanSafetyState.FloorOwned =>
                confirmedFans is { } value
                && DeviceSubsystemOrder.IsManualFanMode(value),
            DeferredManualFanSafetyState.AutomaticOwned =>
                confirmedFans == FanMode.Automatic,
            _ => true,
        };
        if (!matches)
        {
            LoseDeferredManualFanSafetyOwnershipCore();
        }

        return matches;
    }

    private static string? DeferredManualFanSafetyStateBlockReason(
        DeferredManualFanSafetyState expected,
        ConfirmedDeviceState state)
    {
        bool matches = expected switch
        {
            DeferredManualFanSafetyState.PendingUnowned or
                DeferredManualFanSafetyState.FloorOwned =>
                DeviceSubsystemOrder.IsManualFanMode(
                    state.Fans ?? FanMode.Automatic),
            DeferredManualFanSafetyState.AutomaticOwned =>
                state.Fans == FanMode.Automatic,
            _ => false,
        };
        return matches
            ? null
            : "Fan state changed after crash-recovery ownership was classified; the automatic recovery write was blocked.";
    }

    private void LoseDeferredManualFanSafetyOwnershipCore()
    {
        deferredManualFanSafetyState = DeferredManualFanSafetyState.Lost;
        manualFanCurveRecoveryPending = false;
        lastOwnedFanControlSettings = null;
    }

    private async Task<bool> ReconcileDeferredManualFanSafetyWithUserIntentCoreAsync(
        PowerSource powerSource,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (!ValidateDeferredManualFanSafetyStateCore(current)
            || !stillControlsSession())
        {
            return false;
        }

        FanControlSettings desired = Automation.Resolve(
                current.ConfirmedState ?? UnknownConfirmedState)
            .FanControl
            ?? FanControlSettings.FromLegacy(
                Automation.Resolve(current.ConfirmedState ?? UnknownConfirmedState)
                    .Fans
                    .Value);
        if (deferredManualFanSafetyState == DeferredManualFanSafetyState.PendingUnowned)
        {
            return true;
        }

        if (deferredManualFanSafetyState == DeferredManualFanSafetyState.Lost)
        {
            return false;
        }

        if (deferredManualFanSafetyState == DeferredManualFanSafetyState.FloorOwned)
        {
            if (desired.Mode == FanMode.Automatic)
            {
                await HandBackDeferredManualFanSafetyFloorCoreAsync(
                    powerSource,
                    cancellationToken,
                    stillControlsSession).ConfigureAwait(false);
                return deferredManualFanSafetyState ==
                    DeferredManualFanSafetyState.AutomaticOwned;
            }

            if (desired.Mode is FanMode.ManualCurve or FanMode.Maximum)
            {
                manualFanCurveRecoveryPending = false;
                return true;
            }

            return await ApplyDeferredOwnedFanIntentCoreAsync(
                desired,
                powerSource,
                stillControlsSession,
                cancellationToken).ConfigureAwait(false);
        }

        if (desired.Mode == FanMode.Automatic)
        {
            manualFanCurveRecoveryPending = false;
            return true;
        }

        if (desired.Mode == FanMode.ManualCurve)
        {
            await MaintainDeferredManualFanSafetyFloorCoreAsync(
                cancellationToken,
                allowOwnedAutomaticBaseline: true,
                stillControlsSession).ConfigureAwait(false);
            return deferredManualFanSafetyState ==
                DeferredManualFanSafetyState.FloorOwned;
        }

        return await ApplyDeferredOwnedFanIntentCoreAsync(
            desired,
            powerSource,
            stillControlsSession,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ApplyDeferredOwnedFanIntentCoreAsync(
        FanControlSettings settings,
        PowerSource powerSource,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        ServiceStatusSnapshot current = GetStatus();
        if (current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(CompetingControllerProcesses.Contains)
            || !stillControlsSession())
        {
            return false;
        }

        PowerSource confirmedPowerSource =
            await ResolveCurrentPowerSourceAsync(cancellationToken).ConfigureAwait(false);
        if (confirmedPowerSource != powerSource || !stillControlsSession())
        {
            return false;
        }

        DeferredManualFanSafetyState priorState = deferredManualFanSafetyState;
        FanControlSettings? priorOwnedSettings = lastOwnedFanControlSettings;
        bool priorRecoveryPending = manualFanCurveRecoveryPending;
        deferredManualFanSafetyState = DeferredManualFanSafetyState.Lost;
        manualFanCurveRecoveryPending = false;
        lastOwnedFanControlSettings = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyFanControlOutcomeAsync(
                        settings,
                        token,
                        stillControlsSession,
                        state => DeferredManualFanSafetyStateBlockReason(
                            priorState,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = settings.Mode },
                    FanControl = settings,
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyState = priorState;
            manualFanCurveRecoveryPending = priorRecoveryPending;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyState = priorState;
            manualFanCurveRecoveryPending = priorRecoveryPending;
            lastOwnedFanControlSettings = priorOwnedSettings;
            throw;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            return false;
        }

        deferredManualFanSafetyState = settings.Mode == FanMode.Automatic
            ? DeferredManualFanSafetyState.AutomaticOwned
            : DeferredManualFanSafetyState.FloorOwned;
        return true;
    }

    private async Task<bool> TryCompleteDeferredUnexpectedExitRecoveryCoreAsync(
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        if (!unexpectedExitRecoveryDeferredForUserConfiguration
            || !recoveryUserConfigurationLoaded
            || !stillControlsSession())
        {
            return false;
        }

        PowerSource powerSource = await ResolveCurrentPowerSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!stillControlsSession()
            || powerSource == PowerSource.ExternalUnknown)
        {
            return false;
        }

        _ = ChangePowerSourceCore(powerSource);

        FanControlSettings? retainedFanControl = Automation.FanControlOverride;
        FanControlSettings fullPowerProfileFanControl =
            Automation.Profiles.PluggedIn.FanControl
            ?? FanControlSettings.FromLegacy(Automation.Profiles.PluggedIn.Fans);
        // A manual curve is intentionally inactive on USB-C. Keep the bounded
        // recovery attempt available until a compatible source is confirmed.
        if (powerSource == PowerSource.UsbC
            && (retainedFanControl is { Mode: FanMode.ManualCurve }
                || fullPowerProfileFanControl.Mode == FanMode.ManualCurve))
        {
            PublishDeferredRecoveryPowerSourceCore(powerSource);
            await MaintainDeferredManualFanSafetyFloorCoreAsync(
                cancellationToken,
                stillControlsSession: stillControlsSession)
                .ConfigureAwait(false);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        await ObserveCoreAsync(
            powerSource,
            powerSourceConfirmed: true,
            requestedLiveReadings: LiveReadingSensors.None,
            batteryPowerFlowMilliwatts: null,
            cancellationToken).ConfigureAwait(false);

        if (!stillControlsSession())
        {
            return false;
        }

        // Reclassify the adapter after the awaited device observation so an
        // AC-to-USB-C transition cannot race a manual recovery write.
        PowerSource confirmedPowerSource =
            await ResolveCurrentPowerSourceAsync(cancellationToken).ConfigureAwait(false);
        if (!stillControlsSession()
            || confirmedPowerSource == PowerSource.ExternalUnknown)
        {
            return false;
        }

        if (confirmedPowerSource != powerSource)
        {
            _ = ChangePowerSourceCore(confirmedPowerSource);
            PublishDeferredRecoveryPowerSourceCore(confirmedPowerSource);
            if (confirmedPowerSource == PowerSource.UsbC)
            {
                await MaintainDeferredManualFanSafetyFloorCoreAsync(
                    cancellationToken,
                    stillControlsSession: stillControlsSession).ConfigureAwait(false);
            }
            return false;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || current.WritesBlocked
            || current.ConfirmedState is not { } confirmed)
        {
            return false;
        }

        bool deferredFanStateConverged =
            await ReconcileDeferredManualFanSafetyWithUserIntentCoreAsync(
                confirmedPowerSource,
                stillControlsSession,
                cancellationToken).ConfigureAwait(false);
        if (!stillControlsSession())
        {
            return false;
        }

        current = GetStatus();
        if (current.ConfirmedState is not { } reconciledConfirmed)
        {
            return false;
        }
        confirmed = reconciledConfirmed;

        if (!stillControlsSession())
        {
            return false;
        }

        bool expectedRendererShutdown =
            pendingLightingRestore is { Effect: not LightingEffect.Off }
            && confirmed.Lighting is { Effect: LightingEffect.Off }
            && current.LightingRendererOwnership == LightingRendererOwnershipState.None;

        unexpectedExitRecoveryDeferredForUserConfiguration = false;
        startupProfileReconciliationRequested = true;
        ObserveAutomaticState(confirmed);
        if (!deferredFanStateConverged)
        {
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
        }
        if (expectedRendererShutdown)
        {
            // Host-rendered effects cannot survive the service process. Firmware
            // Off with no renderer ownership is the expected crash boundary, not
            // evidence that another controller replaced the saved effect.
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: true);
        }

        current = GetStatus();
        PublishIfChanged(current with
        {
            ExternalChanges = driftTracker.ExternalChanges,
            DesiredState = Automation.Resolve(confirmed),
        });
        try
        {
            if (!stillControlsSession())
            {
                ResetDeferredUnexpectedExitClassificationCore();
                return false;
            }

            await PrepareManualFanCurveRecoveryCoreAsync(
                cancellationToken,
                stillControlsSession).ConfigureAwait(false);
            if (!stillControlsSession())
            {
                ResetDeferredUnexpectedExitClassificationCore();
                return false;
            }

            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            if (!stillControlsSession())
            {
                ResetDeferredUnexpectedExitClassificationCore();
                return false;
            }

            _ = await RestoreLightingRendererCoreAsync(
                cancellationToken,
                stillControlsSession).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            pendingLightingRestore = null;
            pendingLightingRestoreFromLifecycle = false;
            recoveryUserConfigurationLoaded = false;
            suppressInitialUserProfileApplyAfterUnexpectedExit = false;
            CompleteUnexpectedExitRecoveryCore();
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            ResetDeferredUnexpectedExitClassificationCore();
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetDeferredUnexpectedExitClassificationCore();
            throw;
        }

        recoveryUserConfigurationLoaded = false;
        suppressInitialUserProfileApplyAfterUnexpectedExit = false;
        CompleteUnexpectedExitRecoveryCore();
        return true;
    }

    private void ResetDeferredUnexpectedExitClassificationCore()
    {
        unexpectedExitRecoveryDeferredForUserConfiguration = true;
        initialAutomaticComparisons.Clear();
        driftTracker = new ConfirmedStateDriftTracker();
        // An unexpected-exit classification reset is not hardware evidence.
        // Retain pending writes until a later full observation resolves them.
        ServiceStatusSnapshot current = GetStatus();
        if (current.ConfirmedState is { } confirmed)
        {
            driftTracker.Observe(confirmed);
        }

        PublishIfChanged(current with
        {
            ExternalChanges = driftTracker.ExternalChanges,
        });
    }

    private void PublishDeferredRecoveryPowerSourceCore(PowerSource powerSource)
    {
        ServiceStatusSnapshot current = GetStatus();
        ConfirmedDeviceState confirmed =
            current.ConfirmedState ?? UnknownConfirmedState;
        PublishIfChanged(current with
        {
            ActivePowerSource = powerSource,
            WritesBlocked = false,
            DesiredState = Automation.Resolve(confirmed),
            Message =
                "The power source changed while crash recovery waits for an authorized active-user request; no hardware profile was applied.",
        });
    }

    public async Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            DiscardExpiredResumeBaselineCore();
            DiscardUnstartedResumeBaselineCore();
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RefreshBatteryHealthAfterPowerSourceChangeCore();
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            bool changed = powerSource != PowerSource.ExternalUnknown
                && ChangePowerSourceCore(powerSource);
            if (unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                PublishDeferredRecoveryPowerSourceCore(powerSource);
                await MaintainDeferredManualFanSafetyFloorCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (changed
                || ShouldRetryPendingOwnedHyperBoostPowerBoundaryExit(powerSource))
            {
                if (changed)
                {
                    RecordResumePowerSourceTransitionCore(powerSource);
                }
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
            await RefreshControllerPresenceAndRelinquishLightingAsync(
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

            bool changed = ChangePowerSourceCore(classified);
            if (unexpectedExitRecoveryDeferredForUserConfiguration)
            {
                PublishDeferredRecoveryPowerSourceCore(classified);
                await MaintainDeferredManualFanSafetyFloorCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (changed
                || ShouldRetryPendingOwnedHyperBoostPowerBoundaryExit(classified))
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
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
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

            if (displayBrightnessNeutralizationPending)
            {
                ControlOperationResult neutralization =
                    await ReleaseDisplayBrightnessUsageCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (neutralization.Status == ControlOperationStatus.Failed)
                {
                    history.RecordSummary(
                        ControlOperationStatus.Failed,
                        $"Stale native display-brightness input could not be released: {neutralization.Message}");
                }
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

    private async Task<ControlOperationResult> RestoreOwnedHyperBoostForLifecycleCoreAsync(
        string reasonLabel,
        CancellationToken cancellationToken)
    {
        long expectedOwnershipGeneration = 0;
        PowerSource exitPowerSource = PowerSource.ExternalUnknown;
        long expectedExitPowerSourceGeneration = 0;
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ServiceStatusSnapshot current = GetStatus();
                if (!ownsHyperBoost)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Stored,
                        "HyperBoost was left unchanged because OpenBlade did not establish ownership of the active mode.");
                }

                if (current.ConfirmedState?.HyperBoostActive != true)
                {
                    bool retired = await RetireInactiveHyperBoostJournalIfPresentAsync(
                            "HyperBoost was already inactive at the lifecycle boundary, but its ownership journal could not be retired.")
                        .ConfigureAwait(false);
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    return new ControlOperationResult(
                        retired
                            ? ControlOperationStatus.Stored
                            : ControlOperationStatus.Failed,
                        retired
                            ? "HyperBoost was already inactive at the lifecycle boundary."
                            : "HyperBoost was already inactive, but its ownership journal could not be retired; control writes remain blocked.");
                }

                exitPowerSource = await ResolveCurrentPowerSourceAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                if (exitPowerSource != PowerSource.ExternalUnknown
                    && ChangePowerSourceCore(exitPowerSource))
                {
                    current = GetStatus();
                    PublishIfChanged(current with
                    {
                        ActivePowerSource = exitPowerSource,
                        DesiredState = Automation.Resolve(
                            current.ConfirmedState ?? UnknownConfirmedState),
                        Message =
                            "The power source was refreshed before HyperBoost lifecycle restoration so the destination profile is authoritative.",
                    });
                }

                expectedOwnershipGeneration = hyperBoostOwnershipGeneration;
                expectedExitPowerSourceGeneration = powerSourceGeneration;
            }
            finally
            {
                operationGate.Release();
            }

            await hyperBoostLifecycleExitBoundary(cancellationToken)
                .ConfigureAwait(false);

            string boundary = reasonLabel == "shutdown"
                ? "before shutdown"
                : $"before {reasonLabel}";
            history.RecordSummary(
                ControlOperationStatus.Stored,
                $"Attempting to exit OpenBlade-owned HyperBoost and restore its entry-time profile {boundary}.");

            ControlOperationResult result = await SetHyperBoostCoreAsync(
                    enabled: false,
                    expectedOwnershipGeneration,
                    lifecycleValidation: null,
                    restoreUserIntent: true,
                    exitPowerSourceOverride: exitPowerSource,
                    expectedExitPowerSourceGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != ControlOperationStatus.Applied)
            {
                return result;
            }

            string restored =
                $"OpenBlade-owned HyperBoost exited and its entry-time profile was restored {boundary}.";
            history.RecordSummary(ControlOperationStatus.Applied, restored);
            return new ControlOperationResult(ControlOperationStatus.Applied, restored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string canceled =
                "HyperBoost lifecycle restoration was canceled. OpenBlade will not retry it during this lifecycle boundary.";
            history.RecordSummary(ControlOperationStatus.Failed, canceled);
            return new ControlOperationResult(ControlOperationStatus.Failed, canceled);
        }
        catch (Exception exception)
        {
            string failure =
                $"HyperBoost lifecycle restoration failed. OpenBlade will not retry it: {exception.Message}";
            history.RecordSummary(ControlOperationStatus.Failed, failure);
            return new ControlOperationResult(ControlOperationStatus.Failed, failure);
        }
    }

    public Task<ControlOperationResult> PrepareOwnedHyperBoostForDisplayOffAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return RestoreOwnedHyperBoostForLifecycleCoreAsync(
            "display-off safety boundary",
            cancellationToken);
    }

    public async Task<ControlOperationResult> PrepareForShutdownAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ControlOperationResult hyperBoostResult =
            await RestoreOwnedHyperBoostForLifecycleCoreAsync(
                "shutdown",
                cancellationToken).ConfigureAwait(false);
        CancellationToken cleanupToken = cancellationToken.IsCancellationRequested
            ? CancellationToken.None
            : cancellationToken;
        await operationGate.WaitAsync(cleanupToken).ConfigureAwait(false);
        try
        {
            ControlOperationResult? brightnessResult = displayBrightnessReleaseRequired
                ? await ReleaseDisplayBrightnessUsageCoreAsync(cleanupToken)
                    .ConfigureAwait(false)
                : null;
            ControlOperationResult driverModeResult = await ReleaseDriverModeCoreAsync(
                "shutdown",
                cleanupToken).ConfigureAwait(false);
            ControlOperationResult fanResult;
            try
            {
                fanResult = await RestoreAutomaticFansForShutdownCoreAsync(
                    cleanupToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanupToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string failure =
                    $"Fan control could not be handed back to firmware: {exception.Message}";
                history.RecordSummary(ControlOperationStatus.Failed, failure);
                fanResult = new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    failure);
            }

            ControlOperationResult combined = CombineShutdownCleanupResults(
                hyperBoostResult,
                coolingPadFanResult: null,
                coolingPadLightingResult: null,
                fanResult,
                driverModeResult);
            combined = CombineDisplayBrightnessCleanup(brightnessResult, combined);
            return combined;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> PrepareForLowPowerAsync(
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ControlOperationResult hyperBoostResult =
            await RestoreOwnedHyperBoostForLifecycleCoreAsync(
                    "low power",
                    cancellationToken)
                .ConfigureAwait(false);

        CancellationToken cleanupToken = cancellationToken.IsCancellationRequested
            ? CancellationToken.None
            : cancellationToken;
        ControlOperationResult driverModeResult = await ReleaseDriverModeAsync(
                preserveLightingIntent: true,
                reasonLabel: "low power",
                cleanupToken)
            .ConfigureAwait(false);
        return CombineLowPowerCleanupResults(
            hyperBoostResult,
            coolingPadFanResult: null,
            coolingPadLightingResult: null,
            driverModeResult);
    }

    public async Task BeginResumeValidationAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resume begins a new hardware-enumeration session. Restore the bounded
            // MCU inventory budget without turning ordinary healthy observations
            // into an unbounded retry loop.
            mcuVersionReadAttempts = 0;
            bool gpuClockOffsetConfirmationInvalidated = false;
            if (deviceProvider is IGpuClockOffsetConfirmationInvalidator invalidator)
            {
                gpuClockOffsetConfirmationInvalidated =
                    await invalidator.InvalidateGpuClockOffsetConfirmationAsync(
                        cancellationToken).ConfigureAwait(false);
            }

            if (pendingResumeStateBaseline is { } baseline)
            {
                pendingResumeStateBaseline = baseline with
                {
                    ResumeStarted = true,
                    GpuClockOffsetConfirmationInvalidated =
                        gpuClockOffsetConfirmationInvalidated,
                };
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task CancelResumeValidationAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pendingResumeStateBaseline = null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task CompleteResumeValidationAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pendingResumeStateBaseline is not { ResumeStarted: true } baseline)
            {
                return;
            }

            ResumeStateBaseline completed = baseline with
            {
                ResumeLifecycleCompleted = true,
                ObservationWindowStartedAt = timeProvider.GetTimestamp(),
            };
            pendingResumeStateBaseline = completed;
            if (completed.Observed is { } observed)
            {
                RecordResumeStateValidationCore(completed, observed);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ReleaseDriverModeAsync(
        bool preserveLightingIntent,
        string reasonLabel,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LightingSettings? preservedLighting = preserveLightingIntent
            ? pendingLightingRestore
            : null;
        bool preservedLifecycleRestore = preserveLightingIntent
            && pendingLightingRestoreFromLifecycle;
        if (preserveLightingIntent)
        {
            CaptureResumeStateBaselineCore();
        }
        try
        {
            ControlOperationResult? brightnessResult = displayBrightnessReleaseRequired
                ? await ReleaseDisplayBrightnessUsageCoreAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;
            ControlOperationResult driverModeResult = await ReleaseDriverModeCoreAsync(
                reasonLabel,
                cancellationToken).ConfigureAwait(false);
            return CombineDisplayBrightnessCleanup(brightnessResult, driverModeResult);
        }
        finally
        {
            if (preserveLightingIntent)
            {
                pendingLightingRestore = preservedLighting;
                pendingLightingRestoreFromLifecycle = preservedLifecycleRestore;
            }

            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ReleaseDriverModeCoreAsync(
        string reasonLabel,
        CancellationToken cancellationToken)
    {
        string boundary = reasonLabel == "shutdown"
            ? "before shutdown"
            : $"before {reasonLabel}";
        history.RecordSummary(
            ControlOperationStatus.Stored,
            $"Attempting to release keyboard Driver mode {boundary}.");
        try
        {
            try
            {
                await RefreshControllerPresenceAndRelinquishLightingAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string failure =
                    $"Keyboard Driver mode was left unchanged {boundary} because keyboard lighting control could not be relinquished: {exception.Message}";
                history.RecordSummary(ControlOperationStatus.Failed, failure);
                PublishIfChanged(GetStatus() with { Message = failure });
                return new ControlOperationResult(ControlOperationStatus.Failed, failure);
            }
            ServiceStatusSnapshot current = GetStatus();
            if (current.ControllerDetectionWarning is not null)
            {
                string unverified = reasonLabel == "shutdown"
                    ? "Keyboard Driver mode was left unchanged because other Razer software could not be checked."
                    : $"Keyboard Driver mode was left unchanged {boundary} because other Razer software could not be checked.";
                history.RecordSummary(ControlOperationStatus.Stored, unverified);
                return new ControlOperationResult(ControlOperationStatus.Stored, unverified);
            }

            if (current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
            {
                string retained = reasonLabel == "shutdown"
                    ? "Keyboard Driver mode was left unchanged because Razer Synapse is active."
                    : $"Keyboard Driver mode was left unchanged {boundary} because Razer Synapse is active.";
                history.RecordSummary(ControlOperationStatus.Stored, retained);
                return new ControlOperationResult(ControlOperationStatus.Stored, retained);
            }

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                string unavailable = reasonLabel == "shutdown"
                    ? "No verified laptop was available during shutdown, so keyboard Driver mode was left unchanged."
                    : $"OpenBlade could not verify the laptop while releasing keyboard Driver mode {boundary}.";
                ControlOperationStatus unavailableStatus = reasonLabel == "shutdown"
                    ? ControlOperationStatus.Stored
                    : ControlOperationStatus.Failed;
                history.RecordSummary(unavailableStatus, unavailable);
                return new ControlOperationResult(unavailableStatus, unavailable);
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
            string message = $"Keyboard Driver mode was released {boundary}.";
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
    }

    private async Task<ControlOperationResult> RestoreAutomaticFansForShutdownCoreAsync(
        CancellationToken cancellationToken)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (current.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            const string externallyChanged =
                "Fan control was left unchanged because OpenBlade no longer owns the confirmed fan state.";
            history.RecordSummary(ControlOperationStatus.Stored, externallyChanged);
            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                externallyChanged);
        }

        FanMode? confirmedFanMode = current.ConfirmedState?.Fans;
        if (confirmedFanMode is null
            || !DeviceSubsystemOrder.IsManualFanMode(confirmedFanMode.Value))
        {
            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                confirmedFanMode == FanMode.Automatic
                    ? "Fan control already uses firmware-managed Automatic mode."
                    : "Fan control was left unchanged because an OpenBlade-owned manual state was not confirmed.");
        }

        if (lastOwnedFanControlSettings is not { } ownedFanControl
            || !DeviceSubsystemOrder.IsManualFanMode(ownedFanControl.Mode))
        {
            const string notOwned =
                "Manual fan control was left unchanged because OpenBlade did not establish ownership of the current state.";
            history.RecordSummary(ControlOperationStatus.Stored, notOwned);
            return new ControlOperationResult(ControlOperationStatus.Stored, notOwned);
        }

        history.RecordSummary(
            ControlOperationStatus.Stored,
            "Attempting to restore firmware-managed Automatic fan control before shutdown.");
        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        current = GetStatus();
        if (current.ControllerDetectionWarning is not null)
        {
            const string unverified =
                "Manual fan control was left unchanged because other Razer software could not be checked.";
            history.RecordSummary(ControlOperationStatus.Stored, unverified);
            return new ControlOperationResult(ControlOperationStatus.Stored, unverified);
        }

        if (current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
        {
            const string retained =
                "Manual fan control was left unchanged because Razer Synapse is active.";
            history.RecordSummary(ControlOperationStatus.Stored, retained);
            return new ControlOperationResult(ControlOperationStatus.Stored, retained);
        }

        FanControlSettings automatic = FanControlSettings.FromLegacy(FanMode.Automatic);
        ControlOperationResult result = await ApplyScopedIntentAsync(
            DeviceSubsystem.Fans,
            current.ActivePowerSource,
            (coordinator, _, token) => coordinator.ApplyFanControlOutcomeAsync(
                automatic,
                token),
            cancellationToken,
            state => state with
            {
                Fans = state.Fans with { Value = FanMode.Automatic },
                FanControl = automatic,
            }).ConfigureAwait(false);
        if (result.Status == ControlOperationStatus.Applied)
        {
            lastCurveFan1Rpm = null;
            lastCurveFan2Rpm = null;
            const string restored =
                "Firmware-managed Automatic fan control was confirmed before shutdown.";
            history.RecordSummary(ControlOperationStatus.Applied, restored);
            return result with { Message = restored };
        }

        return result with
        {
            Message = $"Fan control could not be handed back to firmware. {result.Message}",
        };
    }


    private static ControlOperationResult CombineShutdownCleanupResults(
        ControlOperationResult hyperBoostResult,
        ControlOperationResult? coolingPadFanResult,
        ControlOperationResult? coolingPadLightingResult,
        ControlOperationResult fanResult,
        ControlOperationResult driverModeResult)
    {
        var results = new List<ControlOperationResult> { hyperBoostResult };
        var messages = new List<string>
        {
            $"HyperBoost cleanup: {hyperBoostResult.Message}",
        };
        AddOptionalCleanupResult(
            results,
            messages,
            "Cooling-pad fan cleanup",
            coolingPadFanResult);
        AddOptionalCleanupResult(
            results,
            messages,
            "Cooling-pad lighting cleanup",
            coolingPadLightingResult);
        results.Add(fanResult);
        messages.Add($"Fan cleanup: {fanResult.Message}");
        results.Add(driverModeResult);
        messages.Add($"Keyboard cleanup: {driverModeResult.Message}");
        return new ControlOperationResult(
            CombineCleanupStatus([.. results]),
            string.Join(' ', messages));
    }

    private static ControlOperationResult CombineLowPowerCleanupResults(
        ControlOperationResult hyperBoostResult,
        ControlOperationResult? coolingPadFanResult,
        ControlOperationResult? coolingPadLightingResult,
        ControlOperationResult driverModeResult)
    {
        var results = new List<ControlOperationResult> { hyperBoostResult };
        var messages = new List<string>
        {
            $"HyperBoost cleanup: {hyperBoostResult.Message}",
        };
        AddOptionalCleanupResult(
            results,
            messages,
            "Cooling-pad fan cleanup",
            coolingPadFanResult);
        AddOptionalCleanupResult(
            results,
            messages,
            "Cooling-pad lighting cleanup",
            coolingPadLightingResult);
        results.Add(driverModeResult);
        messages.Add($"Keyboard cleanup: {driverModeResult.Message}");
        return new ControlOperationResult(
            CombineCleanupStatus([.. results]),
            string.Join(' ', messages));
    }

    private static void AddOptionalCleanupResult(
        List<ControlOperationResult> results,
        List<string> messages,
        string label,
        ControlOperationResult? result)
    {
        if (result is not { } value)
        {
            return;
        }

        results.Add(value);
        messages.Add($"{label}: {value.Message}");
    }

    private static ControlOperationStatus CombineCleanupStatus(
        params ControlOperationResult[] results)
    {
        if (results.Any(static result => result.Status == ControlOperationStatus.Failed))
        {
            return ControlOperationStatus.Failed;
        }

        if (results.Any(static result => result.Status == ControlOperationStatus.Blocked))
        {
            return ControlOperationStatus.Blocked;
        }

        return results.Any(static result => result.Status == ControlOperationStatus.Applied)
            ? ControlOperationStatus.Applied
            : ControlOperationStatus.Stored;
    }

    private static ControlOperationResult CombineDisplayBrightnessCleanup(
        ControlOperationResult? brightnessResult,
        ControlOperationResult otherResult)
    {
        if (brightnessResult is null
            || brightnessResult.Status == ControlOperationStatus.Applied)
        {
            return otherResult;
        }

        ControlOperationStatus status = brightnessResult.Status == ControlOperationStatus.Failed
            || otherResult.Status == ControlOperationStatus.Failed
                ? ControlOperationStatus.Failed
                : ControlOperationStatus.Blocked;
        return new ControlOperationResult(
            status,
            $"Display-brightness input cleanup: {brightnessResult.Message} {otherResult.Message}");
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
        explicitLightingOwnershipActive = false;
        lightingRecoveryPauseMessage = null;
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
            LightingRendererInvalidationResult invalidation =
                await deviceProvider
                    .InvalidateLightingRendererAsync(cancellationToken)
                    .ConfigureAwait(false);
            LightingRendererOwnershipState liveOwnership = invalidation.Ownership;
            RecordDriverModeRestoreOutcome(invalidation.DriverModeRestoreOutcome);

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

    private void RecordDriverModeRestoreOutcome(DriverModeRestoreOutcome outcome)
    {
        (ControlOperationStatus status, string? message) = outcome switch
        {
            DriverModeRestoreOutcome.NotOwned => (ControlOperationStatus.Stored, null),
            DriverModeRestoreOutcome.ForeignChange => (
                ControlOperationStatus.Stored,
                "Lighting-port cleanup found keyboard Driver mode already changed externally; no mode write was submitted."),
            DriverModeRestoreOutcome.Released => (
                ControlOperationStatus.Applied,
                "Lighting-port cleanup confirmed keyboard Driver mode was released."),
            DriverModeRestoreOutcome.Indeterminate => (
                ControlOperationStatus.Failed,
                "Lighting-port cleanup could not confirm that keyboard Driver mode was released."),
            DriverModeRestoreOutcome.Failed => (
                ControlOperationStatus.Failed,
                "Lighting-port cleanup failed while attempting to release keyboard Driver mode."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        if (message is not null)
        {
            history.RecordSummary(status, message);
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
        CancellationToken cancellationToken,
        Func<bool>? stillControlsSession = null)
    {
        if (unexpectedExitRecoveryDeferredForUserConfiguration)
        {
            return true;
        }

        if (IsKeyboardLightingDelegated)
        {
            pendingLightingRestore = null;
            pendingLightingRestoreFromLifecycle = false;
            return true;
        }

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

        if (ShouldYieldAutomaticLighting(current))
        {
            if (ShouldDiscardRememberedLighting(current))
            {
                bool rendererWasOwned = IsExplicitLightingOwnership(
                        current.LightingRendererOwnership)
                    || lastOwnedLightingSettings is not null;
                bool ownershipRelinquished = await RelinquishLightingOwnershipCoreAsync(
                    current,
                    cancellationToken).ConfigureAwait(false);
                if (ownershipRelinquished)
                {
                    PublishLightingYield(GetStatus(), rendererWasOwned);
                }
                return true;
            }

            PublishLightingRecoveryPaused(current);
            return true;
        }

        if (stillControlsSession?.Invoke() == false)
        {
            return false;
        }

        if (stillControlsSession is not null)
        {
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            current = GetStatus();
            if (ShouldYieldAutomaticLighting(current))
            {
                if (ShouldDiscardRememberedLighting(current))
                {
                    bool rendererWasOwned = IsExplicitLightingOwnership(
                            current.LightingRendererOwnership)
                        || lastOwnedLightingSettings is not null;
                    bool ownershipRelinquished =
                        await RelinquishLightingOwnershipCoreAsync(
                            current,
                            cancellationToken).ConfigureAwait(false);
                    if (ownershipRelinquished)
                    {
                        PublishLightingYield(GetStatus(), rendererWasOwned);
                    }
                    return true;
                }

                PublishLightingRecoveryPaused(current);
                return true;
            }
            if (!stillControlsSession())
            {
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lightingRecoveryPauseMessage = null;
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
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pendingLightingRestore = requested;
            pendingLightingRestoreFromLifecycle = lifecycleRestore;
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
        bool recoveryLightingChanged = false;
        Func<ConfirmedDeviceState, string?>? recoveryStateBlockReason =
            stillControlsSession is null
                || preferences.AlwaysOverrideExternalLightingChanges
                ? null
                : state =>
                {
                    if (state.Lighting is { Effect: not LightingEffect.Off } observed
                        && LightingSettingsFactory.NormalizeLegacyOff(observed)
                            != LightingSettingsFactory.NormalizeLegacyOff(requested))
                    {
                        recoveryLightingChanged = true;
                        return "Lighting changed after crash-recovery classification; the saved effect was not restored.";
                    }

                    return null;
                };
        if (stillControlsSession?.Invoke() == false)
        {
            pendingLightingRestore = requested;
            pendingLightingRestoreFromLifecycle = lifecycleRestore;
            return false;
        }

        try
        {
            outcome = await new ControlCoordinator(device)
                .ApplyLightingOutcomeAsync(
                    requested,
                    cancellationToken,
                    forceWrite: preferences.AlwaysOverrideExternalLightingChanges,
                    writeAllowed: stillControlsSession,
                    initialStateBlockReason: recoveryStateBlockReason)
                .ConfigureAwait(false);
        }
        catch (WriteAuthorityLostException)
        {
            pendingLightingRestore = requested;
            pendingLightingRestoreFromLifecycle = lifecycleRestore;
            throw;
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pendingLightingRestore = requested;
            pendingLightingRestoreFromLifecycle = lifecycleRestore;
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

        if (recoveryLightingChanged)
        {
            pendingLightingRestoreFromLifecycle = false;
            initialAutomaticComparisons.Add(DeviceSubsystem.Lighting);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);
            ServiceStatusSnapshot changed = GetStatus();
            ConfirmedDeviceState? confirmed = outcome.Readback is null
                ? changed.ConfirmedState
                : MergeScopedReadback(
                    DeviceSubsystem.Lighting,
                    changed.ConfirmedState,
                    outcome.Readback);
            history.RecordSubsystemResult(DeviceSubsystem.Lighting, outcome.Result);
            PublishIfChanged(changed with
            {
                ConfirmedState = confirmed,
                DesiredState = Automation.Resolve(confirmed ?? UnknownConfirmedState),
                ExternalChanges = driftTracker.ExternalChanges,
                Message = outcome.Result.Message,
            });
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

        if (pendingResumeStateBaseline is { ResumeStarted: true } baseline)
        {
            pendingResumeStateBaseline = baseline with
            {
                LightingRestoredDuringResume = true,
                LightingWriteApplied =
                    outcome.Result.Status == ControlResultStatus.Applied,
            };
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
            GpuClockTuning = value?.Lifetime == OverrideLifetime.Persistent
                ? value.GpuClockTuning
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
            : OverrideStore.SaveAsync(staged, cancellationToken);

    private async Task<ControlOperationResult> ExecuteScopedMutationAsync(
        DeviceSubsystem subsystem,
        Func<CancellationToken, Task> mutation,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        string committedMessage,
        Func<PowerSource, ServiceStatusSnapshot, string?>? deferHardwareApply = null,
        Action? afterApply = null)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            if (IsWritePendingFor(subsystem))
            {
                return PendingWriteBlockedResult(subsystem);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    subsystem,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; overrides and control writes are blocked.",
                    preserveCurrentAvailability: true);
            }

            if (ChangePowerSourceCore(powerSource))
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

            ServiceStatusSnapshot staged = GetStatus();
            string? deferredMessage = deferHardwareApply?.Invoke(powerSource, staged);
            if (deferredMessage is not null)
            {
                history.RecordSummary(ControlOperationStatus.Stored, deferredMessage);
                PublishScopedStatus(
                    powerSource,
                    staged.ConfirmedState,
                    staged.Capabilities,
                    deferredMessage,
                    staged.DeviceVerified,
                    staged.WritesBlocked);
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    deferredMessage);
            }

            ControlOperationResult result = await ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                apply,
                token);
            if (result.Status == ControlOperationStatus.Applied)
            {
                afterApply?.Invoke();
            }
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
            if (IsWritePendingFor(DeviceSubsystem.Battery))
            {
                return PendingWriteBlockedResult(DeviceSubsystem.Battery);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (powerSource == PowerSource.ExternalUnknown)
            {
                return PublishScopedUnavailable(
                    DeviceSubsystem.Battery,
                    PowerSourceForStatus(powerSource),
                    "The external power adapter could not be classified; control writes are blocked.",
                    preserveCurrentAvailability: true);
            }

            if (ChangePowerSourceCore(powerSource))
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

            return await ApplyScopedIntentAsync(
                DeviceSubsystem.Battery,
                powerSource,
                (coordinator, _, cancellationToken) =>
                    apply(coordinator, staged, cancellationToken),
                token,
                desired => desired with { Battery = staged },
                _ =>
                {
                    Automation.CommitBatteryState(staged);
                    return Task.CompletedTask;
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
        if (IsWritePendingFor(subsystem))
        {
            return PendingWriteBlockedResult(subsystem);
        }

        if (subsystem == DeviceSubsystem.Lighting && IsKeyboardLightingDelegated)
        {
            return WindowsDynamicLightingOwnsKeyboard();
        }

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
        if (confirmed is { HyperBoostActive: false })
        {
            bool retired = true;
            if (ownsHyperBoost || ownedHyperBoostBaseMode is not null)
            {
                retired = await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "A confirmed inactive observation relinquished HyperBoost ownership, but its journal could not be retired.")
                    .ConfigureAwait(false);
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostRequiresCoolingPad = false;
            if (!retired)
            {
                PublishIfChanged(GetStatus() with
                {
                    WritesBlocked = true,
                    Message =
                        "HyperBoost is inactive, but its ownership journal could not be retired. Control writes remain blocked.",
                });
            }
        }

        if (!outcome.ReadbackMatched
            && outcome.WriteAttempted
            && !outcome.ConfirmationUnavailable)
        {
            RemovePendingWriteConfirmation(subsystem);
            driftTracker.ConfirmIntentional(subsystem, matched: false);
        }

        if (subsystem == DeviceSubsystem.Fans)
        {
            UpdateOwnedFanControlSettings(
                desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                outcome.Result,
                outcome.WriteAttempted);
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

        if (outcome.ConfirmationUnavailable
            && outcome.PreWriteState is { } preWriteState)
        {
            SetPendingWriteConfirmation(subsystem, new PendingWriteConfirmation(
                powerSource,
                outcome.EffectiveDesired ?? desired,
                preWriteState));
        }

        if (outcome.Result.Status is
                ControlResultStatus.Applied or ControlResultStatus.Unchanged
            && outcome.ConfirmedCoupledWrites is not null)
        {
            foreach (DeviceSubsystem coupled in outcome.ConfirmedCoupledWrites)
            {
                driftTracker.ConfirmIntentional(coupled, matched: true);
            }
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
            ControlCoordinator coordinator = subsystem is
                DeviceSubsystem.Performance or DeviceSubsystem.Fans
                    ? new ControlCoordinator(
                        device,
                        device.ReadPerformanceFanStateAsync,
                        resolveIndeterminatePerformanceWrites: true)
                    : new ControlCoordinator(device);
            return await apply(coordinator, desired, token);
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
                await TryPersistRememberedLightingIntentAsync(
                    confirmed,
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
            RemovePendingWriteConfirmation(subsystem);
            driftTracker.ConfirmIntentional(subsystem, matched: true);
        }
        else if (outcome.Result.Status == ControlResultStatus.Applied)
        {
            RemovePendingWriteConfirmation(subsystem);
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
        if (outcome.ConfirmationUnavailable && current.DeviceVerified)
        {
            preserveCurrentAvailability = true;
        }
        PublishScopedStatus(
            powerSource,
            confirmed,
            outcome.Capabilities ?? current.Capabilities,
            outcome.Result.Message,
            deviceVerified: preserveCurrentAvailability
                ? current.DeviceVerified
                : !outcome.DeviceAccessFailed,
            writesBlocked: preserveCurrentAvailability
                ? current.WritesBlocked || HasPendingWriteConfirmations()
                : outcome.DeviceAccessFailed || HasPendingWriteConfirmations());
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

    private async Task RefreshControllerPresenceAndRelinquishLightingAsync(
        CancellationToken cancellationToken,
        bool allowRecentObservation = false)
    {
        RefreshControllerPresenceCore(cancellationToken, allowRecentObservation);
        ServiceStatusSnapshot current = GetStatus();
        if (!ShouldRelinquishLightingOwnership(current))
        {
            return;
        }

        bool rendererWasOwned = IsExplicitLightingOwnership(
                current.LightingRendererOwnership)
            || lastOwnedLightingSettings is not null;
        bool ownershipRelinquished = await RelinquishLightingOwnershipCoreAsync(
            current,
            cancellationToken).ConfigureAwait(false);
        if (!ownershipRelinquished)
        {
            return;
        }

        PublishLightingYield(GetStatus(), rendererWasOwned);
    }

    private async Task<bool> RelinquishLightingOwnershipCoreAsync(
        ServiceStatusSnapshot current,
        CancellationToken cancellationToken)
    {
        bool hasOwnedRenderer =
            IsExplicitLightingOwnership(current.LightingRendererOwnership)
            || lastOwnedLightingSettings is not null;
        bool hasRememberedRenderer = hasOwnedRenderer
            || pendingLightingRestore is not null
            || faultedLightingRecoveryCandidate is not null;
        if (!hasRememberedRenderer)
        {
            return false;
        }

        if (!hasOwnedRenderer)
        {
            RelinquishRememberedLightingOwnership();
            current = GetStatus();
            if (current.LightingRendererOwnership ==
                LightingRendererOwnershipState.Faulted)
            {
                PublishIfChanged(current with
                {
                    ConfirmedState = current.ConfirmedState is null
                        ? null
                        : current.ConfirmedState with { Lighting = null },
                    LightingRendererOwnership = LightingRendererOwnershipState.None,
                });
            }
            return true;
        }

        _ = await deviceProvider
            .RelinquishLightingRendererAsync(cancellationToken)
            .ConfigureAwait(false);
        RelinquishRememberedLightingOwnership();
        current = GetStatus();
        PublishIfChanged(current with
        {
            ConfirmedState = current.ConfirmedState is null
                ? null
                : current.ConfirmedState with { Lighting = null },
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        return true;
    }

    private bool ShouldYieldAutomaticLighting(ServiceStatusSnapshot current) =>
        !preferences.AlwaysOverrideExternalLightingChanges
        && (current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            || ControllerMayOwnLighting(current));

    private bool ShouldRelinquishLightingOwnership(ServiceStatusSnapshot current) =>
        ShouldYieldAutomaticLighting(current)
        && (current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            || HasCompetingLightingController(current)
                && !explicitLightingOwnershipActive);

    private bool ShouldDiscardRememberedLighting(ServiceStatusSnapshot current) =>
        current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
        || (HasCompetingLightingController(current)
            && !explicitLightingOwnershipActive);

    private static bool ControllerMayOwnLighting(ServiceStatusSnapshot current) =>
        current.ControllerDetectionWarning is not null
        || HasCompetingLightingController(current);

    private static bool HasCompetingLightingController(ServiceStatusSnapshot current) =>
        current.PotentialControllers.Any(CompetingControllerProcesses.Contains);

    private void RelinquishRememberedLightingOwnership()
    {
        explicitLightingOwnershipActive = false;
        lightingRecoveryPauseMessage = null;
        lastOwnedLightingSettings = null;
        pendingLightingRestore = null;
        pendingLightingRestoreFromLifecycle = false;
        faultedLightingRecoveryCandidate = null;
    }

    private void PublishLightingYield(
        ServiceStatusSnapshot current,
        bool rendererReleased = true)
    {
        string action = rendererReleased
            ? "released keyboard lighting control"
            : "did not restore keyboard lighting";
        string message = current.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            ? $"OpenBlade {action} after a confirmed external lighting change."
            : current.PotentialControllers.Any(CompetingControllerProcesses.Contains)
                ? $"Razer Synapse is active; OpenBlade {action}."
                : $"OpenBlade {action} because other-controller detection is incomplete.";
        history.RecordSummary(ControlOperationStatus.Stored, message);
        PublishIfChanged(current with { Message = message });
    }

    private void PublishLightingRecoveryPaused(ServiceStatusSnapshot current)
    {
        string message = current.ControllerDetectionWarning is not null
            && !HasCompetingLightingController(current)
                ? "OpenBlade paused automatic keyboard lighting recovery until other-controller detection succeeds."
                : "Razer Synapse is active; OpenBlade paused automatic keyboard lighting recovery.";
        if (!string.Equals(
                lightingRecoveryPauseMessage,
                message,
                StringComparison.Ordinal))
        {
            lightingRecoveryPauseMessage = message;
            history.RecordSummary(ControlOperationStatus.Stored, message);
        }
        PublishIfChanged(current with { Message = message });
    }

    private void PublishLightingRelinquishFailure(
        Exception exception,
        bool publishStatus = true)
    {
        string failure =
            $"Keyboard lighting control could not be relinquished: {exception.Message}";
        history.RecordSummary(ControlOperationStatus.Failed, failure);
        if (publishStatus)
        {
            PublishIfChanged(GetStatus() with { Message = failure });
        }
    }

    private async Task<bool> ExitOwnedHyperBoostForPowerSourceTransitionAsync(
        IBladeDevice device,
        PowerSource powerSource,
        CancellationToken cancellationToken)
    {
        if (powerSource is not (PowerSource.Battery or PowerSource.UsbC))
        {
            return true;
        }
        if (!ownsHyperBoost)
        {
            pendingOwnedHyperBoostPowerBoundaryExit = false;
            ownedHyperBoostPowerBoundaryExitSubmitted = false;
            return true;
        }
        if (!pendingOwnedHyperBoostPowerBoundaryExit)
        {
            return true;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (current.ConfirmedState is not { } cachedConfirmed)
        {
            PublishFullUnavailable(
                powerSource,
                "Owned HyperBoost could not be checked at the power-source boundary.");
            return false;
        }

        ConfirmedDeviceState freshConfirmed;
        try
        {
            try
            {
                freshConfirmed = await ReadHyperBoostThermalStateAsync(
                        device,
                        cachedConfirmed,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsTransientHyperBoostBoundaryReadFailure(exception))
            {
                await transitionRetryDelay(
                        PowerTransitionAccessRetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
                freshConfirmed = await ReadHyperBoostThermalStateAsync(
                        device,
                        cachedConfirmed,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PublishFullUnavailable(
                powerSource,
                $"Owned HyperBoost could not be checked at the power-source boundary: {exception.Message}",
                failed: true);
            return false;
        }

        driftTracker.Observe(freshConfirmed);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
        DesiredDeviceState destinationIntent = ResolveHardwareDesired(freshConfirmed);
        PublishIfChanged(current with
        {
            ActivePowerSource = powerSource,
            DeviceVerified = true,
            ConfirmedState = freshConfirmed,
            DesiredState = destinationIntent,
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = HasPendingWriteConfirmations()
                || hyperBoostJournalSafetyBlock,
            Message = freshConfirmed.HyperBoostActive
                ? "The power source changed; OpenBlade is exiting owned HyperBoost before applying the destination profile."
                : "The power source changed and firmware already exited owned HyperBoost; OpenBlade is retiring ownership before applying the destination profile.",
        });

        if (!freshConfirmed.HyperBoostActive)
        {
            return await RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync(
                    device,
                    freshConfirmed,
                    destinationIntent,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ControlOperationResult result = await SetHyperBoostCoreAsync(
                enabled: false,
                expectedOwnershipGeneration: hyperBoostOwnershipGeneration,
                lifecycleValidation: null,
                restoreUserIntent: true,
                exitPowerSourceOverride: powerSource,
                expectedExitPowerSourceGeneration: powerSourceGeneration,
                token: cancellationToken,
                operationGateAlreadyHeld: true,
                writeAttemptObserver: attempted =>
                {
                    pendingOwnedHyperBoostPowerBoundaryExit =
                        !attempted && ownsHyperBoost;
                    ownedHyperBoostPowerBoundaryExitSubmitted = attempted;
                })
            .ConfigureAwait(false);
        return result.Status == ControlOperationStatus.Applied;
    }

    private static bool IsTransientHyperBoostBoundaryReadFailure(
        Exception exception) => exception is
        IOException or TimeoutException or ObjectDisposedException
            or UnauthorizedAccessException
        || exception is RazerProtocolException
        {
            ValidationFailure: HidProtocolValidationFailure.PayloadValidator,
        };

    private async Task<bool>
        RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync(
            IBladeDevice device,
            ConfirmedDeviceState confirmed,
            DesiredDeviceState destinationIntent,
            CancellationToken cancellationToken)
    {
        HyperBoostRestoreIntent restoreIntent =
            CreateHyperBoostRestoreIntent(destinationIntent);
        PerformancePreset baseMode = ownedHyperBoostBaseMode
            ?? PerformancePreset.Balanced;
        bool journalPrepared = await TryWriteHyperBoostOwnershipJournalAsync(
                HyperBoostOwnershipJournalState.RestorePending,
                baseMode,
                ownedHyperBoostRequiresCoolingPad,
                "Firmware exited HyperBoost at the power-source boundary, but the destination restore journal could not be prepared.",
                restoreIntent)
            .ConfigureAwait(false);
        if (!journalPrepared)
        {
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message =
                    "Firmware exited HyperBoost at the power-source boundary, but its destination restore journal could not be prepared. Control writes remain blocked.",
            });
            return false;
        }

        // The durable destination intent now protects every following outcome.
        // Disarm automatic reconciliation before any restoration write so an
        // indeterminate submission is never retried.
        pendingOwnedHyperBoostPowerBoundaryExit = false;

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || HasPendingWriteConfirmations()
            || hyperBoostJournalSafetyBlock
            || current.ControllerDetectionWarning is not null
            || current.PotentialControllers.Any(
                CompetingControllerProcesses.Contains))
        {
            hyperBoostJournalSafetyBlock = true;
            ClearOwnedHyperBoostState();
            const string blockedMessage =
                "Firmware exited HyperBoost at the power-source boundary, but destination restoration is blocked until exact-device and competing-controller safety are confirmed.";
            history.RecordSummary(ControlOperationStatus.Failed, blockedMessage);
            PublishIfChanged(current with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message = blockedMessage,
            });
            return false;
        }

        HyperBoostIntentRestorationResult restoration;
        try
        {
            restoration = await RestoreHyperBoostIntentAsync(
                    device,
                    restoreIntent,
                    confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            hyperBoostJournalSafetyBlock = true;
            ClearOwnedHyperBoostState();
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message =
                    "Firmware exited HyperBoost, but destination restoration was canceled. The pending restore journal was preserved and no write will be retried.",
            });
            throw;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            ClearOwnedHyperBoostState();
            string failure =
                $"Firmware exited HyperBoost, but destination restoration failed: {exception.Message} The pending restore journal was preserved and no write will be retried.";
            history.RecordSummary(ControlOperationStatus.Failed, failure);
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message = failure,
            });
            return false;
        }

        ConfirmedDeviceState restored = restoration.Readback ?? confirmed;
        ClearOwnedHyperBoostState();
        if (!restoration.Succeeded)
        {
            hyperBoostJournalSafetyBlock = true;
            string failure =
                $"Firmware exited HyperBoost, but the destination performance or fan intent was not fully restored. {restoration.Message} The pending restore journal was preserved and no write will be retried.";
            history.RecordSummary(ControlOperationStatus.Failed, failure);
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = restored,
                WritesBlocked = true,
                Message = failure,
            });
            return false;
        }

        driftTracker.Observe(restored);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
        bool journalCleared = await TryClearHyperBoostOwnershipJournalAsync(
                "Firmware exited HyperBoost and the destination intent was restored, but its pending journal could not be cleared.")
            .ConfigureAwait(false);
        string message = journalCleared
            ? $"Firmware exited HyperBoost and OpenBlade directly restored the destination {PerformanceLabel(restoreIntent.Performance)} performance and fan intent."
            : $"Firmware exited HyperBoost and OpenBlade directly restored the destination {PerformanceLabel(restoreIntent.Performance)} performance and fan intent, but its pending journal could not be cleared; control writes remain blocked.";
        history.RecordSummary(
            journalCleared
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Failed,
            message);
        PublishIfChanged(GetStatus() with
        {
            ConfirmedState = restored,
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = !journalCleared,
            Message = message,
        });
        return journalCleared;
    }

    private async Task ApplyFullTransitionAsync(
        PowerSource powerSource,
        CancellationToken token,
        bool preserveLiveReadings = false,
        bool preserveObservedExternalChanges = true)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (powerSource is PowerSource.Battery or PowerSource.UsbC
            && ownsHyperBoost
            && current.ActivePowerSource != powerSource
            && !ownedHyperBoostPowerBoundaryExitSubmitted)
        {
            pendingOwnedHyperBoostPowerBoundaryExit = true;
        }
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        DesiredDeviceState? priorDesired = current.DesiredState;
        IBladeDevice device;
        try
        {
            bool available = deviceProvider.TryGetVerifiedDevice(out device);
            token.ThrowIfCancellationRequested();
            if (!available)
            {
                PublishFullUnavailable(powerSource, "No verified supported device is available.");
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

        if (!await ExitOwnedHyperBoostForPowerSourceTransitionAsync(
                device,
                powerSource,
                token)
            .ConfigureAwait(false))
        {
            return;
        }

        current = GetStatus();
        cachedConfirmed = current.ConfirmedState;

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
                preserveObservedExternalChanges,
                token);

        if (outcome.PreWriteState is { } preWriteState)
        {
            foreach (DeviceSubsystem subsystem in
                outcome.ConfirmationUnavailable ?? new HashSet<DeviceSubsystem>())
            {
                SetPendingWriteConfirmation(subsystem, new PendingWriteConfirmation(
                    powerSource,
                    outcome.EffectiveDesired ?? desired,
                    preWriteState));
            }
        }

        ConfirmedDeviceState? confirmed = cachedConfirmed;
        if (outcome.Readback is not null)
        {
            driftTracker.Observe(outcome.Readback);
            confirmed = outcome.Readback;
        }

        string? laptopBehaviorMessage = current.WritesBlocked
            || outcome.ConfirmationUnavailable?.Count > 0
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

    private bool ShouldRetryPendingOwnedHyperBoostPowerBoundaryExit(
        PowerSource powerSource) =>
        pendingOwnedHyperBoostPowerBoundaryExit
        && ownsHyperBoost
        && powerSource is PowerSource.Battery or PowerSource.UsbC;

    private async Task<FullTransitionApplyOutcome> ApplyDesiredTransitionWithRetryAsync(
        IBladeDevice device,
        DesiredDeviceState desired,
        DesiredDeviceState? priorDesired,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? cachedConfirmed,
        bool preserveObservedExternalChanges,
        CancellationToken token)
    {
        bool ShouldConfirmTransientLightingOff() =>
            preserveObservedExternalChanges
            && priorDesired is not null
            && !preferences.AlwaysOverrideExternalLightingChanges
            && !driftTracker.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            && !ControllerMayOwnLighting(current)
            && !IsKeyboardLightingDelegated
            && IsExplicitLightingOwnership(current.LightingRendererOwnership)
            && lastOwnedLightingSettings == priorDesired.Lighting.Value
            && OwnershipMatches(
                current.LightingRendererOwnership,
                lastOwnedLightingSettings)
            && priorDesired.Lighting.Value.Effect != LightingEffect.Off
            && desired.Lighting.Value.Effect != LightingEffect.Off;

        Func<CancellationToken, Task<PowerTransitionDeviceStateReadResult>>?
            transitionStateReader = ShouldConfirmTransientLightingOff()
                ? device.ReadStateForPowerTransitionAsync
                : null;

        IReadOnlySet<DeviceSubsystem> ResolveAutomaticWriteExclusions(
            ConfirmedDeviceState confirmed)
        {
            if (preserveObservedExternalChanges)
            {
                ObserveAutomaticState(confirmed, priorDesired, desired);
            }
            else
            {
                driftTracker.Observe(confirmed);
            }
            var exclusions = new HashSet<DeviceSubsystem>(
                preserveObservedExternalChanges
                    ? GetCoupledAutomaticWriteExclusions()
                    : GetPendingWriteExclusions());
            if (confirmed.HyperBoostActive)
            {
                // Owned Battery and USB-C transitions exit HyperBoost before
                // reaching this resolver. Preserve every other active session:
                // unowned state is advisory-only, and full-power transitions do
                // not issue an implicit thermal write.
                exclusions.Add(DeviceSubsystem.Performance);
                exclusions.Add(DeviceSubsystem.Fans);
            }
            if (!preferences.AlwaysOverrideExternalLightingChanges
                && ControllerMayOwnLighting(current))
            {
                exclusions.Add(DeviceSubsystem.Lighting);
            }
            if (IsKeyboardLightingDelegated)
            {
                exclusions.Add(DeviceSubsystem.Lighting);
            }
            return exclusions;
        }

        try
        {
            FullTransitionApplyOutcome outcome = await new ControlCoordinator(device)
                .ApplyDesiredStateOutcomeAsync(
                    desired,
                    token,
                    automaticWriteExclusionResolver: ResolveAutomaticWriteExclusions,
                    stateReader: transitionStateReader);

            if (ShouldRetryPowerTransitionAccess(outcome))
            {
                await transitionRetryDelay(PowerTransitionAccessRetryDelay, token);
                outcome = await new ControlCoordinator(device)
                    .ApplyDesiredStateOutcomeAsync(
                        desired,
                        token,
                        automaticWriteExclusionResolver: ResolveAutomaticWriteExclusions,
                        stateReader: transitionStateReader);
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
        if (IsKeyboardLightingDelegated)
        {
            return (outcome, LightingRendererOwnershipState.None);
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
        bool hyperBoostJournalRetired = true;
        if (confirmed is { HyperBoostActive: false })
        {
            hyperBoostJournalRetired =
                await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "HyperBoost was confirmed inactive, but its ownership journal could not be retired.")
                    .ConfigureAwait(false);
            if (ownsHyperBoost || ownedHyperBoostBaseMode is not null)
            {
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostRequiresCoolingPad = false;
        }

        IReadOnlySet<DeviceSubsystem> confirmationUnavailable =
            outcome.ConfirmationUnavailable ?? new HashSet<DeviceSubsystem>();
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
        {
            if (outcome.ReadbackMatched.Contains(subsystem))
            {
                RemovePendingWriteConfirmation(subsystem);
                driftTracker.ConfirmIntentional(subsystem, matched: true);
            }
            else if (ResultFor(outcome.Results, subsystem).Status == ControlResultStatus.Applied)
            {
                RemovePendingWriteConfirmation(subsystem);
                driftTracker.AcknowledgeIntentionalWithoutReadback(subsystem);
            }
            else if (outcome.WriteAttempted.Contains(subsystem)
                && !confirmationUnavailable.Contains(subsystem))
            {
                RemovePendingWriteConfirmation(subsystem);
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
            else if (outcome.ReadbackContradicted.Contains(subsystem))
            {
                RemovePendingWriteConfirmation(subsystem);
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }

        UpdateOwnedFanControlSettings(
            desired.FanControl
                ?? FanControlSettings.FromLegacy(desired.Fans.Value),
            ResultFor(outcome.Results, DeviceSubsystem.Fans),
            outcome.WriteAttempted.Contains(DeviceSubsystem.Fans));

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
            await TryPersistRememberedLightingIntentAsync(
                confirmed,
                token)
                .ConfigureAwait(false);
        }

        history.RecordSubsystemResults(outcome.Results);
        string message = Summarize(outcome.Results);
        if (!string.IsNullOrWhiteSpace(laptopBehaviorMessage))
        {
            message = $"{message} {laptopBehaviorMessage}";
            history.RecordSummary(ControlOperationStatus.Failed, laptopBehaviorMessage);
        }
        if (!hyperBoostJournalRetired)
        {
            message = $"{message} HyperBoost is inactive, but its ownership journal could not be retired; control writes remain blocked.";
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
            deviceVerified: confirmationUnavailable.Count > 0
                ? GetStatus().DeviceVerified
                : !outcome.DeviceAccessFailed,
            writesBlocked: outcome.DeviceAccessFailed
                || HasPendingWriteConfirmations()
                || !hyperBoostJournalRetired);
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
            lastAdapterPowerClassificationDiagnosticKind = null;
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

            AdapterPowerState classified = AdapterPowerState.Unknown;
            int agreeingSamples = 0;
            int unknownSamples = 0;
            int accessFailures = 0;
            string? lastFailureKind = null;
            int attempts = 0;
            for (;
                attempts < MaximumAdapterPowerClassificationSamples;
                attempts++)
            {
                AdapterPowerState sample;
                try
                {
                    sample = await device.ReadAdapterPowerStateAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    accessFailures++;
                    lastFailureKind = exception.GetType().Name;
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (sample == AdapterPowerState.Unknown)
                {
                    unknownSamples++;
                    continue;
                }

                if (classified == AdapterPowerState.Unknown)
                {
                    classified = sample;
                    agreeingSamples = 1;
                    continue;
                }

                if (sample != classified)
                {
                    RecordAdapterPowerClassificationDiagnostic(
                        AdapterPowerClassificationDiagnosticKind.ConflictingKnownReplies,
                        ControlOperationStatus.Blocked,
                        "Power adapter classification observed conflicting known read-only replies; control writes remain blocked.");
                    return PowerSource.ExternalUnknown;
                }

                agreeingSamples++;
                if (agreeingSamples >= RequiredAgreeingAdapterPowerSamples)
                {
                    break;
                }
            }

            if (agreeingSamples < RequiredAgreeingAdapterPowerSamples)
            {
                AdapterPowerClassificationDiagnosticKind diagnosticKind =
                    accessFailures > 0
                        ? AdapterPowerClassificationDiagnosticKind.DeviceAccessFailures
                        : AdapterPowerClassificationDiagnosticKind.UnknownReplies;
                string detail = accessFailures > 0
                    ? $"Power adapter classification remained unavailable after {attempts} read-only samples ({accessFailures} device-access failures, {unknownSamples} unknown replies; last failure {lastFailureKind})."
                    : $"Power adapter classification remained unknown after {attempts} read-only samples.";
                RecordAdapterPowerClassificationDiagnostic(
                    diagnosticKind,
                    ControlOperationStatus.Blocked,
                    detail);
                return PowerSource.ExternalUnknown;
            }

            if (accessFailures > 0 || unknownSamples > 0)
            {
                RecordAdapterPowerClassificationDiagnostic(
                    AdapterPowerClassificationDiagnosticKind.Recovered,
                    ControlOperationStatus.Stored,
                    $"Power adapter classification recovered after {attempts + 1} "
                    + $"read-only samples ({accessFailures} device-access failures, "
                    + $"{unknownSamples} unknown replies).");
            }
            else
            {
                lastAdapterPowerClassificationDiagnosticKind = null;
            }

            return classified switch
            {
                AdapterPowerState.FullPower280W => PowerSource.PluggedIn,
                AdapterPowerState.FullPower330W => PowerSource.PluggedIn,
                AdapterPowerState.UsbCPower45W => PowerSource.UsbC,
                AdapterPowerState.UsbCPower60W => PowerSource.UsbC,
                AdapterPowerState.UsbCPower65W => PowerSource.UsbC,
                AdapterPowerState.UsbCPower80W => PowerSource.UsbC,
                AdapterPowerState.UsbCPower90W => PowerSource.UsbC,
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

    private void RecordAdapterPowerClassificationDiagnostic(
        AdapterPowerClassificationDiagnosticKind kind,
        ControlOperationStatus status,
        string detail)
    {
        if (lastAdapterPowerClassificationDiagnosticKind == kind)
        {
            return;
        }

        lastAdapterPowerClassificationDiagnosticKind = kind;
        history.RecordSummary(status, detail);
    }

    private enum AdapterPowerClassificationDiagnosticKind
    {
        ConflictingKnownReplies,
        DeviceAccessFailures,
        UnknownReplies,
        Recovered,
    }

    private async Task ObserveCoreAsync(
        PowerSource powerSource,
        bool powerSourceConfirmed,
        LiveReadingSensors requestedLiveReadings,
        int? batteryPowerFlowMilliwatts,
        CancellationToken cancellationToken,
        bool skipLightingHandoff = false)
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
            if (startupProfileReconciliationPending
                && (!unexpectedExitRecoveryPending
                    || unexpectedExitRecoveryDeferredForUserConfiguration))
            {
                // A first sample has no preceding OpenBlade observation, so it
                // cannot prove an external change. Establish a baseline; the
                // startup reconciliation path will then apply saved intent.
                driftTracker.Observe(observation.Confirmed);
            }
            else
            {
                ObserveAutomaticState(observation.Confirmed);
                ServiceStatusSnapshot current = GetStatus();
                if (pendingLightingRestoreFromLifecycle
                    && pendingLightingRestore is { Effect: not LightingEffect.Off }
                    && current.ControllerDetectionWarning is not null
                    && !HasCompetingLightingController(current)
                    && current.LightingRendererOwnership
                        == LightingRendererOwnershipState.None
                    && observation.Confirmed.Lighting is { Effect: LightingEffect.Off })
                {
                    // Lifecycle invalidation intentionally retires the renderer.
                    // A warning-only pause preserves its restore candidate, so
                    // the expected firmware Off sample is not external drift.
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Lighting,
                        matched: true);
                }
            }
            if (powerSourceConfirmed)
            {
                ReconcilePendingWriteConfirmations(
                    observation.Confirmed,
                    powerSource,
                    device);
            }
            bool suppressInactiveHyperBoostJournalRetirement = false;
            string? pendingHyperBoostMessage = null;
            if (powerSourceConfirmed)
            {
                IndeterminateHyperBoostReconciliation entry =
                    await ReconcileIndeterminateHyperBoostEntryAsync(
                            observation.Confirmed,
                            powerSource,
                            device,
                            cancellationToken)
                        .ConfigureAwait(false);
                observation = observation with { Confirmed = entry.Confirmed };
                IndeterminateHyperBoostReconciliation exit =
                    await ReconcileIndeterminateHyperBoostExitAsync(
                            observation.Confirmed,
                            powerSource,
                            device,
                            cancellationToken)
                        .ConfigureAwait(false);
                observation = observation with { Confirmed = exit.Confirmed };
                suppressInactiveHyperBoostJournalRetirement =
                    entry.SuppressInactiveJournalRetirement
                    || exit.SuppressInactiveJournalRetirement;
                pendingHyperBoostMessage =
                    exit.Message ?? entry.Message;
            }
            consecutiveVerifiedAccessFailures = 0;
            fullObservationCompleted = true;
            lastFullObservationTimestamp = Stopwatch.GetTimestamp();
            BatteryHealthStatus? batteryHealth =
                CaptureBatteryHealthAfterFirstVerificationCore();
            bool journalRetired = true;
            if (!observation.Confirmed.HyperBoostActive
                && !suppressInactiveHyperBoostJournalRetirement)
            {
                journalRetired = await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "A fresh inactive HyperBoost observation relinquished ownership, but its journal could not be retired.",
                        observation.Confirmed)
                    .ConfigureAwait(false);
            }
            PublishVerifiedDeviceObservation(
                powerSource,
                powerSourceConfirmed,
                includeLiveReadings,
                batteryPowerFlowMilliwatts,
                observation,
                batteryHealth);
            if (pendingHyperBoostMessage is not null)
            {
                PublishIfChanged(GetStatus() with
                {
                    WritesBlocked = hyperBoostJournalSafetyBlock,
                    Message = pendingHyperBoostMessage,
                });
            }
            if (!journalRetired)
            {
                PublishIfChanged(GetStatus() with
                {
                    WritesBlocked = true,
                    Message =
                        "HyperBoost is inactive, but its ownership journal could not be retired. Control writes remain blocked.",
                });
            }

            bool lightingRelinquishFailed = skipLightingHandoff;
            if (!skipLightingHandoff)
            {
                try
                {
                    await RefreshControllerPresenceAndRelinquishLightingAsync(
                        cancellationToken,
                        allowRecentObservation: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lightingRelinquishFailed = true;
                    PublishLightingRelinquishFailure(exception);
                }
            }

            if (unexpectedExitRecoveryPending
                && startupProfileReconciliationRequested
                && powerSourceConfirmed)
            {
                CompleteUnexpectedExitRecoveryCore();
            }

            if (powerSourceConfirmed && !lightingRelinquishFailed)
            {
                await RecoverFaultedLightingRendererCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            CompletePendingResumeStateValidationCore(
                powerSource,
                observation.Confirmed);
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

    private void CaptureResumeStateBaselineCore()
    {
        ServiceStatusSnapshot current = GetStatus();
        pendingResumeStateBaseline = current.DeviceVerified
            && current.ConfirmedState is { } confirmed
                ? new ResumeStateBaseline(
                    current.ActivePowerSource,
                    confirmed,
                    ResumeStarted: false,
                    ResumeLifecycleCompleted: false,
                    LightingRestoredDuringResume: false,
                    LightingWriteApplied: false,
                    Observed: null,
                    ObservationWindowStartedAt: null)
                : null;
    }

    private void DiscardUnstartedResumeBaselineCore()
    {
        if (pendingResumeStateBaseline is { ResumeStarted: false })
        {
            pendingResumeStateBaseline = null;
        }
    }

    private void DiscardExpiredResumeBaselineCore()
    {
        if (pendingResumeStateBaseline is
            {
                ResumeLifecycleCompleted: true,
                ObservationWindowStartedAt: { } startedAt,
            }
            && timeProvider.GetElapsedTime(startedAt)
                >= ResumeValidationObservationWindow)
        {
            pendingResumeStateBaseline = null;
        }
    }

    private void RecordResumePowerSourceTransitionCore(PowerSource powerSource)
    {
        if (pendingResumeStateBaseline is not { ResumeStarted: true } baseline)
        {
            return;
        }

        pendingResumeStateBaseline = null;
        if (baseline.ResumeLifecycleCompleted)
        {
            return;
        }

        history.RecordResumeValidation(
            ControlOperationStatus.Stored,
            $"Resume changed the power source from {baseline.PowerSource} to {powerSource}; the existing destination-profile transition was used instead of comparing the two power profiles.");
    }

    private void CompletePendingResumeStateValidationCore(
        PowerSource powerSource,
        ConfirmedDeviceState observed)
    {
        if (pendingResumeStateBaseline is not { ResumeStarted: true } baseline)
        {
            return;
        }

        if (baseline.PowerSource != powerSource)
        {
            RecordResumePowerSourceTransitionCore(powerSource);
            return;
        }

        if (!baseline.ResumeLifecycleCompleted)
        {
            pendingResumeStateBaseline = baseline with { Observed = observed };
            return;
        }

        RecordResumeStateValidationCore(baseline, observed);
    }

    private void RecordResumeStateValidationCore(
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed)
    {
        if (pendingResumeStateBaseline != baseline)
        {
            return;
        }

        pendingResumeStateBaseline = null;
        IReadOnlyList<DeviceSubsystem> compared = GetComparableResumeSubsystems(
            baseline,
            observed);
        IReadOnlyList<DeviceSubsystem> changed = GetChangedResumeSubsystems(
            baseline.Confirmed,
            observed,
            compared);
        if (changed.Count > 0)
        {
            history.RecordResumeValidation(
                ControlOperationStatus.Blocked,
                BuildResumeValidationSummary(
                    $"Resume validation observed changed control state for {FormatSubsystemList(changed)}. The changes remain externally classified; no validation-driven recovery writes were attempted.",
                    baseline,
                    observed,
                    compared));
            return;
        }

        history.RecordResumeValidation(
            baseline.LightingWriteApplied
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Stored,
            BuildResumeValidationSummary(
                compared.Count > 0
                    ? $"Resume validation confirmed retained control state for {FormatSubsystemList(compared)}. No validation-driven recovery writes were needed."
                    : "Resume validation could not compare control state because no subsystem had confirmed readings in both samples. No validation-driven recovery writes were attempted.",
                baseline,
                observed,
                compared));
    }

    private static string BuildResumeValidationSummary(
        string result,
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed,
        IReadOnlyList<DeviceSubsystem> compared)
    {
        var details = new List<string> { result };
        if (baseline.LightingRestoredDuringResume)
        {
            details.Add(baseline.LightingWriteApplied
                ? "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; that restoration applied a lighting control write."
                : "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; no new lighting control write was required.");
        }
        else if (!compared.Contains(DeviceSubsystem.Lighting))
        {
            details.Add("Lighting was unavailable in at least one sample and was not compared.");
        }

        IReadOnlyList<DeviceSubsystem> unavailable = GetUnavailableResumeSubsystems(
            compared,
            excludeLighting: baseline.LightingRestoredDuringResume);
        if (unavailable.Count > 0)
        {
            details.Add($"Unavailable for comparison: {FormatSubsystemList(unavailable)}.");
        }
        return string.Join(" ", details);
    }

    private static IReadOnlyList<DeviceSubsystem> GetComparableResumeSubsystems(
        ResumeStateBaseline baseline,
        ConfirmedDeviceState observed)
    {
        var compared = new List<DeviceSubsystem>(4);
        if ((baseline.Confirmed.Performance is not null
                || baseline.Confirmed.HyperBoostActive)
            && (observed.Performance is not null
                || observed.HyperBoostActive)
            && !baseline.GpuClockOffsetConfirmationInvalidated
            && (baseline.Confirmed.GpuClockOffsets is null
                || observed.GpuClockOffsets is not null))
        {
            compared.Add(DeviceSubsystem.Performance);
        }
        if (baseline.Confirmed.Fans is not null && observed.Fans is not null)
        {
            compared.Add(DeviceSubsystem.Fans);
        }
        if (baseline.Confirmed.Battery is not null && observed.Battery is not null)
        {
            compared.Add(DeviceSubsystem.Battery);
        }
        if (!baseline.LightingRestoredDuringResume
            && baseline.Confirmed.Lighting is not null
            && observed.Lighting is not null)
        {
            compared.Add(DeviceSubsystem.Lighting);
        }
        return compared;
    }

    private static IReadOnlyList<DeviceSubsystem> GetUnavailableResumeSubsystems(
        IReadOnlyList<DeviceSubsystem> compared,
        bool excludeLighting)
    {
        var unavailable = new List<DeviceSubsystem>(4);
        foreach (DeviceSubsystem subsystem in ResumeComparedSubsystems)
        {
            if ((!excludeLighting || subsystem != DeviceSubsystem.Lighting)
                && !compared.Contains(subsystem))
            {
                unavailable.Add(subsystem);
            }
        }
        return unavailable;
    }

    private static IReadOnlyList<DeviceSubsystem> GetChangedResumeSubsystems(
        ConfirmedDeviceState baseline,
        ConfirmedDeviceState observed,
        IReadOnlyList<DeviceSubsystem> compared)
    {
        var changed = new List<DeviceSubsystem>(4);
        foreach (DeviceSubsystem subsystem in compared)
        {
            bool matches = subsystem switch
            {
                DeviceSubsystem.Performance =>
                    baseline.Performance == observed.Performance
                    && baseline.HyperBoostActive == observed.HyperBoostActive
                    && CompareWhenBothKnown(
                        baseline.CustomPerformance,
                        observed.CustomPerformance)
                    && CompareWhenBothKnown(
                        baseline.GpuClockOffsets,
                        observed.GpuClockOffsets),
                DeviceSubsystem.Fans => baseline.Fans == observed.Fans,
                DeviceSubsystem.Battery => baseline.Battery == observed.Battery,
                DeviceSubsystem.Lighting =>
                    baseline.Lighting == observed.Lighting
                    && CompareWhenBothKnown(
                        baseline.LogoLighting,
                        observed.LogoLighting),
                _ => true,
            };
            if (!matches)
            {
                changed.Add(subsystem);
            }
        }
        return changed;
    }

    private static bool CompareWhenBothKnown<T>(T? baseline, T? observed)
        where T : class => baseline is null
            || observed is null
            || EqualityComparer<T>.Default.Equals(baseline, observed);

    private static bool CompareWhenBothKnown<T>(T? baseline, T? observed)
        where T : struct => baseline is null
            || observed is null
            || EqualityComparer<T>.Default.Equals(baseline.Value, observed.Value);

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
            "No verified supported device is available.");
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
        await ReadRz090581CapabilityDiagnosticsAsync(
            device,
            firmware,
            cancellationToken).ConfigureAwait(false);
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
        ServiceStatusSnapshot current = GetStatus();
        if (!observation.Confirmed.HyperBoostActive)
        {
            if (ownsHyperBoost || ownedHyperBoostBaseMode is not null)
            {
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostRequiresCoolingPad = false;
        }

        PublishIfChanged(current with
        {
            DeviceVerified = true,
            WritesBlocked = !powerSourceConfirmed || hyperBoostJournalSafetyBlock,
            ExternalChanges = driftTracker.ExternalChanges,
            Message = !powerSourceConfirmed
                ? powerSource == PowerSource.ExternalUnknown
                    ? "Verified device state observed, but the external power adapter could not be classified; control writes are blocked."
                    : TransientExternalPowerMessage
                : "Verified device state observed.",
            Capabilities = observation.Capabilities,
            LightingRefreshRate = EffectiveLightingRefreshRate(
                observation.Capabilities),
            ConfirmedState = observation.Confirmed,
            PadlessHyperBoostEligibility = ResolvePadlessHyperBoostEligibility(
                observation.Capabilities),
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
            FirmwareCompatibility = deviceProvider.FirmwareCompatibility,
            DisplayBrightnessHidAvailability =
                displayBrightnessHidController.GetAvailability(),
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
                    "No verified supported device is available.");
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

    private void CompleteStartupProfileReconciliation()
    {
        ServiceStatusSnapshot current = GetStatus();
        if (current.DeviceVerified && !current.WritesBlocked)
        {
            startupProfileReconciliationPending = false;
        }
    }

    private bool ChangePowerSourceCore(PowerSource powerSource)
    {
        bool changed = Automation.ChangePowerSource(powerSource);
        if (changed)
        {
            powerSourceGeneration++;
            InvalidatePendingWriteConfirmations(markExternal: true);
            explicitLightingOwnershipActive = false;
            lightingRecoveryPauseMessage = null;
        }
        if (!changed || !suppressInitialKnownPowerSourceApplyAfterUnexpectedExit)
        {
            return changed;
        }

        suppressInitialKnownPowerSourceApplyAfterUnexpectedExit = false;
        ServiceStatusSnapshot current = GetStatus();
        PublishIfChanged(current with
        {
            ActivePowerSource = powerSource,
            WritesBlocked = false,
            Message =
                "The initial power adapter classification was recorded without hardware application during unexpected-exit recovery.",
            DesiredState = Automation.Resolve(
                current.ConfirmedState ?? UnknownConfirmedState),
        });
        return false;
    }

    private void CompleteUnexpectedExitRecoveryCore()
    {
        ServiceStatusSnapshot current = GetStatus();
        if (!unexpectedExitRecoveryPending
            || unexpectedExitRecoveryDeferredForUserConfiguration
            || !current.DeviceVerified
            || current.WritesBlocked)
        {
            return;
        }

        startupProfileReconciliationPending = false;
        unexpectedExitRecoveryPending = false;
        string result = current.ExternalChanges.Count == 0
            ? "No external control changes were observed. Owned long-running behavior is eligible for bounded recovery."
            : $"External control changes remain latched for {string.Join(", ", current.ExternalChanges)}; ordinary profile writes were skipped.";
        const string prefix =
            "Unexpected service-exit recovery observed fresh exact-device state before any recovery write.";
        history.RecordSummary(ControlOperationStatus.Stored, $"{prefix} {result}");
        PublishIfChanged(current with { Message = $"{prefix} {result}" });
    }

    private async Task ReconcileStartupProfileCoreAsync(
        PowerSource powerSource,
        CancellationToken cancellationToken)
    {
        if (!startupProfileReconciliationPending
            || powerSource == PowerSource.ExternalUnknown)
        {
            return;
        }

        _ = ChangePowerSourceCore(powerSource);
        await ApplyFullTransitionAsync(
            powerSource,
            cancellationToken,
            preserveObservedExternalChanges: false).ConfigureAwait(false);
        CompleteStartupProfileReconciliation();
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
        if (mcuVersionReadAttempts >= MaximumMcuVersionReadAttempts
            || current is null
            || !string.Equals(
                current.McuVersion,
                "Unavailable",
                StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        mcuVersionReadAttempts++;
        try
        {
            string? mcuVersion = await device.ReadMcuVersionAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(mcuVersion)
                ? current
                : current with { McuVersion = mcuVersion.Trim() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            mcuVersionReadAttempts--;
            throw;
        }
        catch
        {
            return current;
        }
    }

    private async Task ReadRz090581CapabilityDiagnosticsAsync(
        IBladeDevice device,
        FirmwareVersions? firmware,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                firmware?.ModelNumber,
                RazerBladeModelCatalog.OpenBladeTargetModelNumber,
                StringComparison.OrdinalIgnoreCase)
            || deviceProvider.FirmwareCompatibility is not
                (FirmwareCompatibilityStatus.Tested
                    or FirmwareCompatibilityStatus.ModelScoped))
        {
            return;
        }

        if (Volatile.Read(ref rz090581CapabilityDiagnostics) is not null
            || rz090581CapabilityDiagnosticsReadAttempts
                >= Rz090581CapabilityDiagnosticsReadAttemptLimit)
        {
            return;
        }

        rz090581CapabilityDiagnosticsReadAttempts++;
        try
        {
            Rz090581CapabilityDiagnostics? observed =
                await device.ReadRz090581CapabilityDiagnosticsAsync(cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (observed is not null)
            {
                Volatile.Write(ref rz090581CapabilityDiagnostics, observed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            rz090581CapabilityDiagnosticsReadAttempts--;
            throw;
        }
        catch
        {
            return;
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
        CompareInitialAutomaticFans(confirmed.Fans, desired.Fans.Value);
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

    private IReadOnlyList<DeviceSubsystem> CurrentUnconfirmedWrites()
    {
        lock (pendingWriteConfirmationGate)
        {
            return [.. pendingWriteConfirmations.Keys.Order()];
        }
    }

    private bool HasPendingWriteConfirmations()
    {
        lock (pendingWriteConfirmationGate)
        {
            return pendingWriteConfirmations.Count > 0;
        }
    }

    private void LatchPendingHyperBoost(
        ServiceStatusSnapshot? baseline,
        bool enabled,
        PerformancePreset baseMode)
    {
        // Either an indeterminate enable or disable may have left firmware in
        // HyperBoost. Keep both thermal subsystems latched until a later fresh
        // observation resolves the transition; no retry is scheduled here.
        if (baseline?.ConfirmedState is not { } preWriteState)
        {
            return;
        }

        var pending = new PendingWriteConfirmation(
            baseline.ActivePowerSource,
            baseline.DesiredState,
            preWriteState,
            HyperBoostActive: enabled,
            HyperBoostBaseMode: baseMode);
        if (enabled)
        {
            pendingIndeterminateHyperBoostEntryIntent =
                CreateHyperBoostRestoreIntent(baseline.DesiredState);
            pendingIndeterminateHyperBoostEntryBaseMode = baseMode;
        }
        SetPendingWriteConfirmation(DeviceSubsystem.Performance, pending);
        SetPendingWriteConfirmation(DeviceSubsystem.Fans, pending);
    }

    private void PublishPendingHyperBoostStatus(string message)
    {
        ServiceStatusSnapshot current = GetStatus();
        PublishIfChanged(current with
        {
            Message = message,
        });
    }


    private void SetPendingWriteConfirmation(
        DeviceSubsystem subsystem,
        PendingWriteConfirmation pending)
    {
        lock (pendingWriteConfirmationGate)
        {
            pendingWriteConfirmations[subsystem] = pending;
        }
    }

    private void RemovePendingWriteConfirmation(DeviceSubsystem subsystem)
    {
        lock (pendingWriteConfirmationGate)
        {
            pendingWriteConfirmations.Remove(subsystem);
        }
    }

    private KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[]
        SnapshotPendingWriteConfirmations()
    {
        lock (pendingWriteConfirmationGate)
        {
            return [.. pendingWriteConfirmations];
        }
    }

    private DeviceSubsystem[] SnapshotPendingWriteSubsystems()
    {
        lock (pendingWriteConfirmationGate)
        {
            return [.. pendingWriteConfirmations.Keys];
        }
    }

    private bool IsWritePendingFor(DeviceSubsystem subsystem)
    {
        lock (pendingWriteConfirmationGate)
        {
            return pendingWriteConfirmations.ContainsKey(subsystem)
                || DeviceSubsystemOrder.IsThermal(subsystem)
                    && (pendingWriteConfirmations.ContainsKey(DeviceSubsystem.Performance)
                        || pendingWriteConfirmations.ContainsKey(DeviceSubsystem.Fans));
        }
    }

    private ControlOperationResult PendingWriteBlockedResult(DeviceSubsystem subsystem)
    {
        string message =
            $"{subsystem} was not written because OpenBlade must confirm the current laptop state first.";
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        PublishIfChanged(GetStatus() with { Message = message });
        return new ControlOperationResult(ControlOperationStatus.Blocked, message);
    }

    private void InvalidatePendingWriteConfirmations(bool markExternal)
    {
        KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] pending;
        lock (pendingWriteConfirmationGate)
        {
            pending = [.. pendingWriteConfirmations];
            pendingWriteConfirmations.Clear();
        }

        if (markExternal)
        {
            foreach ((DeviceSubsystem subsystem, _) in pending)
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }
    }

    private void ReconcilePendingWriteConfirmations(
        ConfirmedDeviceState confirmed,
        PowerSource powerSource,
        IBladeDevice device)
    {
        foreach ((DeviceSubsystem subsystem, PendingWriteConfirmation pending) in
            SnapshotPendingWriteConfirmations())
        {
            PendingConfirmationResult result = pending.PowerSource == powerSource
                ? MatchPendingWrite(subsystem, pending, confirmed, device)
                : PendingConfirmationResult.Mismatch;
            if (result == PendingConfirmationResult.Unavailable)
            {
                continue;
            }

            // ObserveAutomaticState has already compared the old baseline with this
            // observation. Clear both latches on a causal match; retain the drift
            // latch on a mismatch. No write is scheduled from this path.
            driftTracker.ConfirmIntentional(
                subsystem,
                matched: result == PendingConfirmationResult.Match);
            RemovePendingWriteConfirmation(subsystem);
        }
    }

    private static PendingConfirmationResult MatchPendingWrite(
        DeviceSubsystem subsystem,
        PendingWriteConfirmation pending,
        ConfirmedDeviceState confirmed,
        IBladeDevice device) => subsystem switch
        {
            DeviceSubsystem.Performance => MatchPendingHyperBoost(
                pending,
                confirmed) ?? MatchPendingPerformance(
                    pending,
                    confirmed,
                    device),
            DeviceSubsystem.Fans => MatchPendingHyperBoost(
                pending,
                confirmed) ?? (confirmed.Fans is null
                    ? PendingConfirmationResult.Unavailable
                    : confirmed.Fans == (pending.Target.FanControl?.Mode
                        ?? pending.Target.Fans.Value)
                        ? PendingConfirmationResult.Match
                        : PendingConfirmationResult.Mismatch),
            DeviceSubsystem.Battery =>
                // Temporary full charge changes the meaning of the other battery
                // fields. A null value is insufficient evidence, so keep the write
                // pending rather than guessing or retrying it.
                confirmed.Battery is not { TemporaryFullCharge: not null } battery
                ? PendingConfirmationResult.Unavailable
                : BatteryMatchesDesired(battery, pending.Target.Battery)
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
            DeviceSubsystem.Lighting => confirmed.Lighting is null
                ? PendingConfirmationResult.Unavailable
                : confirmed.Lighting == pending.Target.Lighting.Value
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
            _ => PendingConfirmationResult.Unavailable,
        };

    private static PendingConfirmationResult? MatchPendingHyperBoost(
        PendingWriteConfirmation pending,
        ConfirmedDeviceState confirmed)
    {
        if (pending.HyperBoostActive is not bool expected)
        {
            return null;
        }

        if (confirmed.HyperBoostActive != expected)
        {
            return PendingConfirmationResult.Mismatch;
        }

        return expected
            ? confirmed.Fans == FanMode.Automatic
                ? PendingConfirmationResult.Match
                : confirmed.Fans is not null
                    ? PendingConfirmationResult.Mismatch
                    : PendingConfirmationResult.Unavailable
            : confirmed is
            {
                Performance: var restoredPerformance,
                Fans: FanMode.Automatic,
            } && restoredPerformance == pending.HyperBoostBaseMode
                ? PendingConfirmationResult.Match
                : confirmed.Performance is not null
                    && confirmed.Fans is not null
                    ? PendingConfirmationResult.Mismatch
                    : PendingConfirmationResult.Unavailable;
    }

    private static PendingConfirmationResult MatchPendingPerformance(
        PendingWriteConfirmation pending,
        ConfirmedDeviceState confirmed,
        IBladeDevice device)
    {
        FanMode desiredFans = pending.Target.FanControl?.Mode
            ?? pending.Target.Fans.Value;
        if (DeviceSubsystemOrder.IsManualFanMode(desiredFans)
            && DeviceSubsystemOrder.IsManualFanMode(confirmed.Fans))
        {
            // Manual thermal modes do not expose a separate performance preset.
            // This is the same semantic match used by ControlCoordinator.
            return PendingConfirmationResult.Match;
        }

        if (confirmed.Performance is null)
        {
            return PendingConfirmationResult.Unavailable;
        }
        if (confirmed.Performance != pending.Target.Performance.Value
            || confirmed.Performance == PerformancePreset.Custom
                && confirmed.CustomPerformance != pending.Target.CustomPerformance)
        {
            return PendingConfirmationResult.Mismatch;
        }

        if (pending.Target.GpuClockTuning is { } tuning)
        {
            if (!tuning.Enabled)
            {
                if (confirmed.GpuClockOffsets is not null)
                {
                    return PendingConfirmationResult.Mismatch;
                }

                return device is IGpuClockOffsetManagementDevice managedDevice
                    ? managedDevice.GpuClockOffsetManagementState is
                        GpuClockOffsetManagementState.ResetConfirmed
                        or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                        ? PendingConfirmationResult.Match
                        : PendingConfirmationResult.Mismatch
                    : PendingConfirmationResult.Unavailable;
            }

            return confirmed.GpuClockOffsets is null
                ? PendingConfirmationResult.Unavailable
                : confirmed.GpuClockOffsets.Current == tuning.EffectiveOffsets
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch;
        }

        GpuClockOffsetCapabilities? before = pending.PreWriteState.GpuClockOffsets;
        GpuClockOffsetCapabilities? after = confirmed.GpuClockOffsets;
        if (before is null)
        {
            return after is null
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch;
        }
        return after is null
            ? PendingConfirmationResult.Unavailable
            : before.Current == after.Current
                ? PendingConfirmationResult.Match
                : PendingConfirmationResult.Mismatch;
    }

    private IReadOnlySet<DeviceSubsystem> GetCoupledAutomaticWriteExclusions()
    {
        var exclusions = driftTracker.ExternalChanges.ToHashSet();
        if (preferences.AlwaysOverrideExternalLightingChanges)
        {
            exclusions.Remove(DeviceSubsystem.Lighting);
        }
        exclusions.UnionWith(SnapshotPendingWriteSubsystems());
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }

        return exclusions;
    }

    private IReadOnlySet<DeviceSubsystem> GetPendingWriteExclusions()
    {
        var exclusions = SnapshotPendingWriteSubsystems().ToHashSet();
        if (exclusions.Contains(DeviceSubsystem.Performance)
            || exclusions.Contains(DeviceSubsystem.Fans))
        {
            exclusions.Add(DeviceSubsystem.Performance);
            exclusions.Add(DeviceSubsystem.Fans);
        }

        return exclusions;
    }

    private static string? DeferredPerformanceIntentMessage(
        PowerSource powerSource,
        ServiceStatusSnapshot status) =>
        powerSource == PowerSource.PluggedIn
        && DeviceSubsystemOrder.IsManualFanMode(status.ConfirmedState?.Fans)
        && DeviceSubsystemOrder.IsManualFanMode(status.DesiredState.Fans.Value)
            ? "Performance intent was stored and will be applied when fans return to Automatic."
            : null;

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

    private void CompareInitialAutomaticFans(
        FanMode? confirmed,
        FanMode desired)
    {
        if (confirmed is null
            || !initialAutomaticComparisons.Add(DeviceSubsystem.Fans))
        {
            return;
        }

        bool matches = confirmed == desired
            || DeviceSubsystemOrder.IsManualFanMode(confirmed.Value)
                && DeviceSubsystemOrder.IsManualFanMode(desired);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matches);
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
            || confirmed.CustomPerformance == desired.CustomPerformance)
        && (desired.GpuClockTuning is null
            || confirmed.GpuClockOffsets is null
            || confirmed.GpuClockOffsets.Current
                == desired.GpuClockTuning.EffectiveOffsets);

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
        if (!deviceVerified)
        {
            // A confirmed loss of the verified device ends the current hardware
            // session. A later re-verification receives a fresh bounded budget.
            mcuVersionReadAttempts = 0;
        }

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
            FirmwareCompatibility = deviceProvider.FirmwareCompatibility,
            DisplayBrightnessHidAvailability =
                displayBrightnessHidController.GetAvailability(),
        });
    }

    private void PublishTransientDeviceProbeFailure(
        PowerSource powerSource,
        LiveReadings? liveReadings)
    {
        ServiceStatusSnapshot current = GetStatus();
        // Do not flash all controls unavailable for one missed enumeration.
        // Hardware operations independently re-admit the exact supported device before access;
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
            Message: "No verified supported device is available.",
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
            LightingRefreshRate: LightingRefreshRate.ThirtyHz,
            BatteryHealth: batteryHealth,
            KeyboardLightingControlMode:
                preferences.KeyboardLightingControlMode,
            AllowUntestedFirmware: preferences.AllowUntestedFirmware,
            FirmwareCompatibility: deviceProvider.FirmwareCompatibility,
            DisplayBrightnessHidAvailability:
                displayBrightnessHidController.GetAvailability())
        {
            FanControlOverride = Automation.FanControlOverride,
            BatteryPolicyOverride = Automation.BatteryPolicyOverride,
            BatteryProtectionOverride = Automation.BatteryProtectionOverride,
        };
    }

    private bool IsKeyboardLightingDelegated =>
        preferences.KeyboardLightingControlMode
            == KeyboardLightingControlMode.WindowsDynamicLighting;

    private LightingRefreshRate EffectiveLightingRefreshRate(
        DeviceCapabilities? capabilities) =>
        capabilities?.SupportedLightingRefreshRates.Contains(
            preferences.LightingRefreshRate) == true
            ? preferences.LightingRefreshRate
            : LightingRefreshRate.ThirtyHz;

    private static ControlOperationResult WindowsDynamicLightingOwnsKeyboard() => new(
        ControlOperationStatus.Blocked,
        "Keyboard lighting is delegated to Windows Dynamic Lighting. OpenBlade did not write the keyboard.");

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
            WritesBlocked = writesBlocked || hyperBoostJournalSafetyBlock,
            ExternalChanges = driftTracker.ExternalChanges,
            Message = message,
            Capabilities = capabilities ?? UnavailableCapabilities,
            LightingRefreshRate = EffectiveLightingRefreshRate(capabilities),
            ConfirmedState = confirmed,
            Profiles = Automation.Profiles,
            DesiredState = Automation.Resolve(confirmed ?? UnknownConfirmedState),
            ActivePowerSource = powerSource,
            PerformanceOverride = Automation.PerformanceOverride,
            FanOverride = Automation.FanOverride,
            FanControlOverride = Automation.FanControlOverride,
            LightingOverride = Automation.LightingOverride,
            BatteryPolicyOverride = Automation.BatteryPolicyOverride,
            BatteryProtectionOverride = Automation.BatteryProtectionOverride,
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
        if (Automation.PowerSource == PowerSource.PluggedIn
            && DeviceSubsystemOrder.IsManualFanMode(desired.Fans.Value))
        {
            desired = desired with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    desired.Performance.Source),
                CustomPerformance = null,
            };
        }

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

    private Task TryPersistRememberedLightingIntentAsync(
        ConfirmedDeviceState? confirmed,
        CancellationToken cancellationToken)
    {
        ResolvedSetting<LightingSettings> lighting =
            Automation.Resolve(confirmed ?? UnknownConfirmedState).Lighting;
        return lighting.Source == SettingValueSource.ConfirmedFirmwareState
            ? Task.CompletedTask
            : TryPersistLastKnownLightingAsync(lighting.Value, cancellationToken);
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

    private void UpdateLightingSuppressionState(
        LightingSuppressionReason reason,
        bool suppressed)
    {
        if (suppressed)
        {
            lightingSuppressions.Add(reason);
        }
        else
        {
            lightingSuppressions.Remove(reason);
        }

        PublishIfChanged(GetStatus() with
        {
            LightingSuppressions = CurrentLightingSuppressions(),
        });
    }

    private void RemoveDisabledLightingSuppressions()
    {
        int removed = lightingSuppressions.RemoveWhere(
            reason => !IsLightingSuppressionConfigured(reason));
        if (removed > 0 && lightingSuppressions.Count == 0)
        {
            lightingSuppressionWritePending = false;
        }
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

    private void UpdateOwnedFanControlSettings(
        FanControlSettings requested,
        ControlResult result,
        bool writeAttempted)
    {
        if (result.Status == ControlResultStatus.Unchanged
            && requested.Mode == FanMode.Automatic)
        {
            lastOwnedFanControlSettings = null;
            return;
        }

        if (result.Status != ControlResultStatus.Applied || !writeAttempted)
        {
            return;
        }

        lastOwnedFanControlSettings = DeviceSubsystemOrder.IsManualFanMode(requested.Mode)
            ? requested
            : null;
    }

    private static bool IsIntentFailure(Exception exception) => exception is
        ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException;

    private ProfileAutomationState Automation => automation
        ?? throw new InvalidOperationException("The service runtime is not initialized.");

    private BatteryConfigurationStore ConfigurationStore =>
        Volatile.Read(ref userStores).Configuration;

    private PersistentOverrideStore OverrideStore =>
        Volatile.Read(ref userStores).Overrides;

    private sealed record UserConfigurationStores(
        BatteryConfigurationStore Configuration,
        PersistentOverrideStore Overrides);

    private sealed record PendingWriteConfirmation(
        PowerSource PowerSource,
        DesiredDeviceState Target,
        ConfirmedDeviceState PreWriteState,
        bool? HyperBoostActive = null,
        PerformancePreset? HyperBoostBaseMode = null);

    private enum PendingConfirmationResult
    {
        Unavailable,
        Match,
        Mismatch,
    }

    private enum DeferredManualFanSafetyState
    {
        PendingUnowned,
        FloorOwned,
        AutomaticOwned,
        Lost,
    }

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
            ServiceStatusSnapshot updated = update(previous);
            IReadOnlyList<DeviceSubsystem> unconfirmedWrites =
                CurrentUnconfirmedWrites();
            ServiceStatusSnapshot candidate = updated with
            {
                WritesBlocked = updated.WritesBlocked
                    || hyperBoostJournalSafetyBlock
                    || unconfirmedWrites.Count > 0,
                UnconfirmedWrites = unconfirmedWrites,
                HyperBoostRestorePending = hyperBoostRestorePending,
            };
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

    internal static bool Equivalent(ServiceStatusSnapshot left, ServiceStatusSnapshot right) =>
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
        && left.UnconfirmedWrites.SequenceEqual(right.UnconfirmedWrites)
        && left.HyperBoostRestorePending == right.HyperBoostRestorePending
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
        && left.BatteryPolicyOverride == right.BatteryPolicyOverride
        && left.BatteryProtectionOverride == right.BatteryProtectionOverride
        && left.FullChargeOverride == right.FullChargeOverride
        && left.StartupAnimationEnabled == right.StartupAnimationEnabled
        && left.FunctionKeyPrimary == right.FunctionKeyPrimary
        && left.GamingModeEnabled == right.GamingModeEnabled
        && left.KeyboardDriverModeActive == right.KeyboardDriverModeActive
        && left.AlwaysOverrideExternalLightingChanges
            == right.AlwaysOverrideExternalLightingChanges
        && left.LightingRefreshRate == right.LightingRefreshRate
        && left.AllowUntestedFirmware == right.AllowUntestedFirmware
        && left.FirmwareCompatibility == right.FirmwareCompatibility
        && left.DisplayBrightnessHidAvailability
            == right.DisplayBrightnessHidAvailability
        && left.KeyboardLightingControlMode == right.KeyboardLightingControlMode
        && left.LightingRendererOwnership == right.LightingRendererOwnership
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && left.BatteryHealth == right.BatteryHealth
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private static string RefreshRateLabel(LightingRefreshRate value) => value switch
    {
        LightingRefreshRate.ThirtyHz => "30 Hz",
        LightingRefreshRate.SixtyHz => "60 Hz",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

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
        if (IsKeyboardLightingDelegated)
        {
            faultedLightingRecoveryCandidate = null;
            lastOwnedLightingSettings = null;
            return;
        }

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

        current = GetStatus();
        if (ShouldYieldAutomaticLighting(current))
        {
            if (ShouldDiscardRememberedLighting(current))
            {
                RelinquishRememberedLightingOwnership();
                PublishIfChanged(current with
                {
                    ConfirmedState = current.ConfirmedState is null
                        ? null
                        : current.ConfirmedState with { Lighting = null },
                    LightingRendererOwnership = LightingRendererOwnershipState.None,
                });
                PublishLightingYield(GetStatus(), rendererReleased: false);
            }
            else
            {
                faultedLightingRecoveryCandidate = requested;
                PublishLightingRecoveryPaused(current);
            }
            return;
        }

        // Consume the candidate before writing. A persistent failure must not
        // become an unbounded 15-second retry loop; an explicit later lighting
        // action can establish a fresh ownership snapshot.
        lightingRecoveryPauseMessage = null;
        faultedLightingRecoveryCandidate = null;

        if (!current.DeviceVerified
            || current.WritesBlocked)
        {
            RelinquishRememberedLightingOwnership();
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
        await ConfigurationStore.SaveConfigurationAsync(
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
        if (appliedSettings is not null
            && IsExplicitLightingOwnership(ownership))
        {
            lightingRecoveryPauseMessage = null;
        }

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
        LightingRendererOwnershipState.AmbientAwareness or
        LightingRendererOwnershipState.AudioMeter or
        LightingRendererOwnershipState.Fire or
        LightingRendererOwnershipState.Sequence;

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
            LightingEffect.AudioMeter => LightingRendererOwnershipState.AudioMeter,
            LightingEffect.Fire => LightingRendererOwnershipState.Fire,
            LightingEffect.Sequence => LightingRendererOwnershipState.Sequence,
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

    internal static IReadOnlySet<string> ResumeComparedStatePropertyNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ConfirmedDeviceState.Performance),
            nameof(ConfirmedDeviceState.CustomPerformance),
            nameof(ConfirmedDeviceState.GpuClockOffsets),
            nameof(ConfirmedDeviceState.HyperBoostActive),
            nameof(ConfirmedDeviceState.Fans),
            nameof(ConfirmedDeviceState.Battery),
            nameof(ConfirmedDeviceState.Lighting),
            nameof(ConfirmedDeviceState.LogoLighting),
        };

    private static IReadOnlyList<DeviceSubsystem> ResumeComparedSubsystems { get; } =
    [
        DeviceSubsystem.Performance,
        DeviceSubsystem.Fans,
        DeviceSubsystem.Battery,
        DeviceSubsystem.Lighting,
    ];

    private sealed record ResumeStateBaseline(
        PowerSource PowerSource,
        ConfirmedDeviceState Confirmed,
        bool ResumeStarted,
        bool ResumeLifecycleCompleted,
        bool LightingRestoredDuringResume,
        bool LightingWriteApplied,
        ConfirmedDeviceState? Observed,
        long? ObservationWindowStartedAt,
        bool GpuClockOffsetConfirmationInvalidated = false);

    private enum IndeterminateHyperBoostResolution
    {
        None,
        Applied,
        NotApplied,
        Unsafe,
    }

    private sealed record IndeterminateHyperBoostReconciliation(
        ConfirmedDeviceState Confirmed,
        bool SuppressInactiveJournalRetirement,
        string? Message,
        IndeterminateHyperBoostResolution Resolution,
        PerformancePreset? RestoredPerformance = null);

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
            && CustomPerformanceCapabilitiesEqual(
                left.CustomPerformance,
                right.CustomPerformance)
            && left.CpuPowerLimit == right.CpuPowerLimit
            && left.CpuVoltageOptimizer == right.CpuVoltageOptimizer
            && left.GpuOverclock == right.GpuOverclock
            && left.HyperBoostAccess == right.HyperBoostAccess
            && left.HyperBoostWithoutCoolingPadAccess
                == right.HyperBoostWithoutCoolingPadAccess
            && left.HyperBoostBaseModes == right.HyperBoostBaseModes
            && left.SpecialKeyBindingsAccess == right.SpecialKeyBindingsAccess
            && left.WritableSpecialKeySlots == right.WritableSpecialKeySlots
            && left.LampArrayLightingAccess == right.LampArrayLightingAccess
            && left.ObservedPowerSourceBehaviors.SequenceEqual(
                right.ObservedPowerSourceBehaviors)
            && left.FanModes.SetEquals(right.FanModes)
            && left.FanSpeedRange == right.FanSpeedRange
            && left.LightingEffects.SetEquals(right.LightingEffects)
            && left.ObservedLightingEffects.SetEquals(right.ObservedLightingEffects)
            && left.SupportedLightingRefreshRates.SetEquals(
                right.SupportedLightingRefreshRates)
            && left.LogoLightingModes.SetEquals(right.LogoLightingModes)
            && left.OneShotLaptopControls.SetEquals(right.OneShotLaptopControls)
            && left.ChargeLimits.SetEquals(right.ChargeLimits)
            && left.SupportsKeyboardBrightness == right.SupportsKeyboardBrightness
            && left.SupportsPlaybackMuteIndicator
                == right.SupportsPlaybackMuteIndicator
            && string.Equals(
                left.KeyboardLightingLayoutId,
                right.KeyboardLightingLayoutId,
                StringComparison.Ordinal)
            && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
            && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge);

    private static string PerformanceLabel(PerformancePreset mode) => mode switch
    {
        PerformancePreset.Silent => "Quiet",
        PerformancePreset.Balanced => "Balanced",
        PerformancePreset.Performance => "Performance",
        _ => mode.ToString(),
    };

    private PadlessHyperBoostEligibilityState ResolvePadlessHyperBoostEligibility(
        DeviceCapabilities capabilities)
    {
        if (capabilities.HyperBoostWithoutCoolingPadAccess is null)
        {
            return PadlessHyperBoostEligibilityState.Unknown;
        }

        return hyperBoostPadDiscoverySource.Observe() switch
        {
            CoolingPadConnectionState.Absent =>
                PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            CoolingPadConnectionState.PresentExact or
            CoolingPadConnectionState.Ambiguous =>
                PadlessHyperBoostEligibilityState.CoolingPadPresent,
            _ => PadlessHyperBoostEligibilityState.Unknown,
        };
    }

    private static bool CustomPerformanceCapabilitiesEqual(
        CustomPerformanceCapabilities? left,
        CustomPerformanceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && left.AllowedCombinations.SetEquals(right.AllowedCombinations));

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
