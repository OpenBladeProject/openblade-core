using System.Diagnostics;
using System.Security.Principal;
using System.Text;
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

    DiagnosticDeviceVerification GetCachedDeviceVerificationDiagnostics() =>
        DiagnosticDeviceVerification.NotObserved;

    Task<DiagnosticHyperBoostOwnershipJournal>
        GetHyperBoostOwnershipJournalDiagnosticsAsync(
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DiagnosticHyperBoostOwnershipJournal.Missing);
    }

    Task ActivateUserAsync(
        SecurityIdentifier userSid,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task ActivateUserAsync(
        SecurityIdentifier userSid,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken) => stillControlsSession()
            ? ActivateUserAsync(userSid, cancellationToken)
            : Task.CompletedTask;

    Task<ControlOperationResult> SetThermalControlPolicyAsync(
        ThermalControlPolicy policy,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal control policy is unavailable."));

    Task<ControlOperationResult> AcknowledgeThermalPolicyUpgradeDisclosureAsync(
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal policy disclosure acknowledgement is unavailable."));

    Task<ControlOperationResult> UpdateProfilesAsync(
        ProfileSet profiles,
        CancellationToken token);

    Task<ControlOperationResult> SetPerformanceOverrideAsync(
        ManualOverride<PerformancePreset>? value,
        CancellationToken token);

    Task<ControlOperationResult> SetGpuClockTuningAsync(
        GpuClockTuningSettings value,
        OverrideLifetime lifetime,
        Func<bool> stillControlsSession,
        CancellationToken token) => Task.FromResult(new ControlOperationResult(
            ControlOperationStatus.Blocked,
            "Independent GPU clock tuning is unavailable."));

    Task<ControlOperationResult> SetHyperBoostAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "HyperBoost control is unavailable."));

    Task<ControlOperationResult> SetHyperBoostAsync(
        SetHyperBoostRequest request,
        CancellationToken token) => SetHyperBoostAsync(request.Enabled, token);

    Task<ControlOperationResult> ValidatePadlessHyperBoostLifecycleAsync(
        ValidatePadlessHyperBoostLifecycleRequest request,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Pad-less HyperBoost lifecycle validation is unavailable."));

    Task<ControlOperationResult> ValidateThermalExternalDriftAsync(
        ValidateThermalExternalDriftRequest request,
        Func<bool> stillControlsSession,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal external-drift validation is unavailable."));


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

    Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        Func<bool> stillControlsSession,
        CancellationToken token) => stillControlsSession()
            ? SetPerformanceConfigurationOverrideAsync(value, token)
            : Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The active Windows session changed before the performance request started."));

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Performance shortcuts are unavailable."));

    Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        Func<bool> stillControlsSession,
        CancellationToken token) => stillControlsSession()
            ? CyclePerformanceShortcutAsync(source, token)
            : Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The active Windows session changed before the performance shortcut started."));

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

    Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        Func<bool> stillControlsSession,
        CancellationToken token) => stillControlsSession()
            ? SetFanControlOverrideAsync(value, settings, token)
            : Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The active Windows session changed before the fan request started."));

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

    Task<ControlOperationResult> SetExperimentalFeaturesAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Experimental laptop controls are unavailable."));

    DiagnosticExperimentalSupport GetCachedExperimentalSupportDiagnostics() =>
        DiagnosticExperimentalSupport.NotDetected;

    Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        CancellationToken token) => Task.FromResult(
            new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Changed settings cannot be reapplied."));

    Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        Func<bool> stillControlsSession,
        CancellationToken token) => stillControlsSession()
            ? ReapplyChangedSettingsAsync(token)
            : Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The active Windows session changed before settings could be reapplied."));

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

    /// <summary>
    /// Records the console display state reported by the Windows lifecycle
    /// source, independent of whether any lighting power policy is enabled.
    /// </summary>
    void ReportConsoleDisplayState(bool displayOn)
    {
    }

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

    bool ShouldRunThermalConvergenceObservation() => false;

    bool ShouldRunPendingTransitionObservation() => false;

    Task RunThermalConvergenceAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    IDisposable RegisterProtocolCompatibilityClient() =>
        EmptyDisposable.Instance;

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

    Task<ControlOperationResult> PrepareForLowPowerAsync(
        long expectedGeneration,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken) =>
        stillAuthorized()
            ? PrepareForLowPowerAsync(cancellationToken)
            : Task.FromResult(new ControlOperationResult(ControlOperationStatus.Blocked,
                "The suspend preparation is no longer authorized."));

    Task InvalidateLightingRendererForLowPowerAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

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

internal sealed class EmptyDisposable : IDisposable
{
    public static EmptyDisposable Instance { get; } = new();

    private EmptyDisposable()
    {
    }

    public void Dispose()
    {
    }
}

internal sealed record ScopedIntentApplyExecution(
    ScopedApplyOutcome Outcome,
    ThermalTransitionTrace? ThermalTrace = null);

public sealed class ServiceControlRuntime : IServiceControlRuntime, IDisposable
{
    private const string UnclassifiedExternalPowerMessage =
        "The external power adapter could not be classified; control writes are blocked.";
    private const string TransientExternalPowerMessage =
        "Power adapter classification is temporarily unavailable; the last confirmed power source is retained and control writes are blocked.";
    private const string ConfigurationQuarantineAdvisory =
        "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.";
    private const string ConfigurationQuarantineBlockedMessage =
        ConfigurationQuarantineAdvisory;
    private const string ManualFanCurveTargetsChangedRetryBlockReason =
        "Manual fan-curve targets changed while waiting for the fresh-session retry.";

    private static readonly string ServiceVersion = OpenBladeProductVersion.Read(
        typeof(ServiceControlRuntime).Assembly);
    private static readonly TimeSpan PowerTransitionAccessRetryDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ThermalPreWriteRetryDelay =
        TimeSpan.FromSeconds(5);
    private static readonly DeviceSubsystem[] HyperBoostThermalComponents =
        [DeviceSubsystem.Performance, DeviceSubsystem.Fans];
    private static readonly TimeSpan FullObservationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FastPathControllerPresenceInterval =
        TimeSpan.FromSeconds(15);
    // Five full open/query/close HID sessions permit two agreeing known replies
    // despite up to three transient failures, while bounding every classification
    // path, including the read-before-write paths used by control commands.
    private const int MaximumAdapterPowerClassificationSamples = 5;
    private const int RequiredAgreeingAdapterPowerSamples = 2;
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
    private readonly ThermalConvergenceArbiter thermalConvergenceArbiter = new();
    private readonly ProfileSetValidator validator;
    private readonly object initializationGate = new();
    private readonly object statusPublishGate = new();
    private ProfileAutomationState? automation;
    private volatile BatteryAutomationPreferences preferences =
        new();
    private PersistentOverrideState persistentOverrides = PersistentOverrideState.Empty;
    private ConfirmedStateDriftTracker driftTracker = new();
    private readonly HashSet<DeviceSubsystem> initialAutomaticComparisons = [];
    // Process-local by design. Service startup establishes a fresh read-only
    // baseline before startup reconciliation can apply saved intent.
    private readonly PendingWriteConfirmationTracker pendingWriteConfirmations = new();
    private volatile PendingTransitionContinuation? pendingTransitionContinuation;
    private volatile PendingResumePerformanceDrift? pendingResumePerformanceDrift;
    private long resumePowerSourceTransitionGeneration = -1;
    private ServiceStatusSnapshot? status;
    private int rz090581CapabilityDiagnosticsReadAttempts;
    private Rz090581CapabilityDiagnostics? rz090581CapabilityDiagnostics;
    private int mcuVersionReadAttempts;
    private LightingSettings? lastOwnedLightingSettings;
    private long lastLightingOffSuspendGeneration = -1;
    private static readonly TimeSpan LowPowerLightingBudget = TimeSpan.FromSeconds(2);
    private ExactFanControlOwnership exactFanControlOwnership =
        ExactFanControlOwnership.Unowned;
    private long fanAuthorityEpoch;
    private long fanDesiredRevision;
    // Process-local by design. A first observation of mode 7 does not establish
    // ownership; only a successful OpenBlade enable with matching readback does.
    private bool ownsHyperBoost;
    private PerformancePreset? ownedHyperBoostBaseMode;
    private HyperBoostRestoreIntent? ownedHyperBoostRestoreIntent;
    private Guid? ownedHyperBoostTransitionId;
    private HyperBoostOwnershipJournal? ownedHyperBoostJournal;
    private string? ownedHyperBoostJournalSha256;
    private HyperBoostRestoreIntent? pendingIndeterminateHyperBoostEntryIntent;
    private Guid? pendingIndeterminateHyperBoostEntryTransitionId;
    private PerformancePreset pendingIndeterminateHyperBoostEntryBaseMode =
        PerformancePreset.Balanced;
    private HyperBoostRestoreIntent? pendingIndeterminateHyperBoostExitIntent;
    private HyperBoostOwnershipJournal?
        pendingIndeterminateHyperBoostJournal;
    private string? pendingIndeterminateHyperBoostJournalSha256;
    private ThermalTransitionRecoveryState?
        pendingIndeterminateHyperBoostRecoveryState;
    private bool pendingOwnedHyperBoostPowerBoundaryExit;
    private bool ownedHyperBoostPowerBoundaryExitSubmitted;
    private bool ownedHyperBoostRequiresCoolingPad;
    private long? coolingPadExitAttemptGeneration;
    private bool hyperBoostJournalSafetyBlock;
    private bool hyperBoostRestorePending;
    private HyperBoostOwnershipJournal? lastReportedRetainedHyperBoostRestoreJournal;
    private readonly HashSet<Guid> padlessHyperBoostLifecycleValidationRuns = [];
    private readonly HashSet<Guid> thermalExternalDriftValidationRuns = [];
    private long hyperBoostOwnershipGeneration;
    private long powerSourceGeneration;
    // Advances before power work waits on operationGate so an already-running
    // thermal transition can observe queued AC/battery/USB-C boundary work.
    private long powerAdmissionEpoch;
    private int pendingPowerAdmissions;
    // Advances before a different interactive user's configuration load can
    // queue behind an already-running thermal transition.
    private long sessionAdmissionEpoch;
    private int pendingSessionAdmissions;
    private LightingSettings? pendingLightingRestore;
    private bool pendingLightingRestoreFromLifecycle;
    private LightingSettings? faultedLightingRecoveryCandidate;
    private readonly HashSet<LightingSuppressionReason> lightingSuppressions = [];
    private bool lightingSuppressionWritePending;
    // The last console display state reported by the Windows lifecycle source,
    // kept even when the active profile does not enable the display-off policy.
    // A closed lid or Modern Standby leaves the keyboard dark whether or not
    // OpenBlade asked for it, so a power-source transition that samples firmware
    // Off while the display is off must not classify it as an external change,
    // and a destination profile that enables the policy must adopt the
    // suppression instead of relighting a keyboard under a closed lid.
    private bool displayReportedOff;
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
    // Entry intent written by unexpected-exit HyperBoost recovery before the deferred user
    // configuration loaded; that activation rebuilds automation and would discard it.
    private HyperBoostRestoreIntent? deferredRecoveredHyperBoostEntryIntent;
    private DeferredManualFanSafetyOwnership deferredManualFanSafetyOwnership =
        DeferredManualFanSafetyOwnership.Lost;
    private bool recoveryUserConfigurationLoaded;
    private bool suppressInitialUserProfileApplyAfterUnexpectedExit;
    private bool suppressInitialKnownPowerSourceApplyAfterUnexpectedExit;
    private string? activeUserSid;
    private Func<bool>? activeUserStillControlsSession;
    private ThermalConvergenceStateStore? thermalConvergenceStore;
    private ThermalConvergenceLedgerState? thermalConvergenceLedger;
    private string? thermalConvergenceLedgerSha256;
    private bool thermalConvergenceLedgerAuthoritative;
    private bool thermalDeviceAuthorityInvalidated;
    private int protocolCompatibilityClients;
    private bool thermalConvergenceShutdown;
    private int activeConfigurationDisposition =
        (int)ConfigurationLoadDisposition.Quarantined;
    private readonly ResumeStateValidationTracker resumeStateValidation;
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
        resumeStateValidation = new ResumeStateValidationTracker(this.timeProvider);
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
            Volatile.Write(
                ref activeConfigurationDisposition,
                (int)configuration.Disposition);
            preferences = configuration.Preferences;
            deviceProvider.SetAllowUntestedFirmware(
                configuration.Preferences.AllowUntestedFirmware);
            deviceProvider.SetAllowExperimentalFeatures(
                configuration.Preferences.AllowExperimentalFeatures);
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
            deferredRecoveredHyperBoostEntryIntent = null;
            deferredManualFanSafetyOwnership =
                unexpectedExitRecoveryDeferredForUserConfiguration
                    ? DeferredManualFanSafetyOwnership.Pending(
                        FanMode.ManualCurve,
                        reestablishAfterUserChange: false)
                    : DeferredManualFanSafetyOwnership.Lost;
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

    public ServiceStatusSnapshot GetStatus()
    {
        ServiceStatusSnapshot current = Volatile.Read(ref status)
            ?? throw new InvalidOperationException("The service runtime is not initialized.");
        return ApplyConfigurationQuarantineAdvisory(current);
    }

    public IDisposable RegisterProtocolCompatibilityClient()
    {
        if (Interlocked.Increment(ref protocolCompatibilityClients) == 1)
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.ProtocolCompatibilityClient,
                thermalConvergenceShutdown: false);
        }

        return new ActionDisposable(() =>
        {
            if (Interlocked.Decrement(ref protocolCompatibilityClients) == 0
                && Volatile.Read(ref status) is not null)
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    thermalConvergenceShutdown: false);
            }
        });
    }

    private ServiceStatusSnapshot ApplyConfigurationQuarantineAdvisory(
        ServiceStatusSnapshot current)
    {
        string baseMessage = StripConfigurationQuarantineAdvisory(current.Message);
        string message = ConfigurationIsAdmitted
            ? baseMessage
            : string.IsNullOrWhiteSpace(baseMessage)
                ? ConfigurationQuarantineAdvisory
                : $"{ConfigurationQuarantineAdvisory} {baseMessage}";
        return string.Equals(message, current.Message, StringComparison.Ordinal)
            ? current
            : current with { Message = message };
    }

    public Rz090581CapabilityDiagnostics? GetCachedRz090581CapabilityDiagnostics() =>
        Volatile.Read(ref rz090581CapabilityDiagnostics);

    public DiagnosticDeviceVerification GetCachedDeviceVerificationDiagnostics() =>
        deviceProvider.GetCachedDeviceVerificationDiagnostics();

    public async Task<DiagnosticHyperBoostOwnershipJournal>
        GetHyperBoostOwnershipJournalDiagnosticsAsync(
            CancellationToken cancellationToken)
    {
        HyperBoostOwnershipJournalLoadResult load =
            await hyperBoostOwnershipJournalStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        return CreateHyperBoostOwnershipJournalDiagnostics(load);
    }

    internal static DiagnosticHyperBoostOwnershipJournal
        CreateHyperBoostOwnershipJournalDiagnostics(
            HyperBoostOwnershipJournalLoadResult load)
    {
        if (load.Journal is not { } journal)
        {
            return load.Invalid
                ? new DiagnosticHyperBoostOwnershipJournal(
                    Exists: true,
                    Valid: false,
                    State: null,
                    BaseMode: null,
                    RequiresCoolingPad: null,
                    RestoreIntent: null,
                    Sha256: null)
                : DiagnosticHyperBoostOwnershipJournal.Missing;
        }

        return new DiagnosticHyperBoostOwnershipJournal(
            Exists: true,
            Valid: !load.Invalid && load.Sha256 is not null,
            journal.State.ToString(),
            journal.BaseMode,
            journal.RequiresCoolingPad,
            new DiagnosticHyperBoostRestoreIntent(
                journal.RestoreIntent.Performance,
                journal.RestoreIntent.CustomPerformance,
                journal.RestoreIntent.GpuClockTuning,
                journal.RestoreIntent.FanControl),
            load.Sha256)
        {
            RestoreRetryDisposition = journal.RestoreRetryDisposition.ToString(),
            RestoreRetryTarget = journal.RestoreRetryTarget.ToString(),
            TransitionId = journal.TransitionId,
        };
    }

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
            activeUserStillControlsSession = stillControlsSession;
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

        CloseThermalConvergenceAdmission(
            ThermalConvergenceReason.InactiveSession,
            thermalConvergenceShutdown: false);
        Interlocked.Increment(ref sessionAdmissionEpoch);
        Interlocked.Increment(ref pendingSessionAdmissions);
        bool sessionAdmissionPending = true;
        try
        {
            UserConfigurationContext context = await userConfigurationRepository
                .LoadAsync(userSid, cancellationToken)
                .ConfigureAwait(false);
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref pendingSessionAdmissions);
            sessionAdmissionPending = false;
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
                // Exact manual fan intent belongs to the active-user hardware
                // session. A new user may carry identical settings, but that does
                // not transfer proof that this service wrote them for that user.
                AdvanceFanAuthorityEpoch(
                    preserveDeferredRecoveryBaseline:
                        unexpectedExitRecoveryDeferredForUserConfiguration);
                AdvanceFanDesiredRevision();
                Volatile.Write(
                    ref userStores,
                    new UserConfigurationStores(context.Store, context.OverrideStore));
                thermalConvergenceStore = context.ThermalConvergenceStore;
                thermalConvergenceLedger = context.ThermalConvergence?.State;
                thermalConvergenceLedgerSha256 = context.ThermalConvergence?.Sha256;
                thermalConvergenceLedgerAuthoritative =
                    context.ThermalConvergence?.IsAuthoritative == true;
                Volatile.Write(
                    ref activeConfigurationDisposition,
                    (int)context.Configuration.Disposition);
                automation = new ProfileAutomationState(
                    profiles,
                    context.Configuration.Preferences,
                    powerSource,
                    overrides);
                // A non-authoritative convergence ledger fails closed at the
                // effective-policy boundary. Preserve the user's durable policy
                // here so an unrelated preference save cannot turn that runtime
                // fallback into a permanent Strict selection.
                preferences = context.Configuration.Preferences;
                deviceProvider.SetAllowUntestedFirmware(
                    context.Configuration.Preferences.AllowUntestedFirmware);
                deviceProvider.SetAllowExperimentalFeatures(
                    context.Configuration.Preferences.AllowExperimentalFeatures);
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
                            preserveObservedExternalChanges: false,
                            stillControlsSession: stillControlsSession)
                        .ConfigureAwait(false);
                }

                if (!stillControlsSession())
                {
                    return;
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
                    AllowExperimentalFeatures =
                        context.Configuration.Preferences.AllowExperimentalFeatures,
                    IsExperimentalDevice = deviceProvider.IsExperimentalDevice,
                    ExperimentalSupportSource = deviceProvider.ExperimentalSupportSource,
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
                    activeUserStillControlsSession = stillControlsSession;
                    thermalConvergenceShutdown = false;
                    RefreshThermalConvergenceIntentState(
                        GetStatus().ConfirmedState ?? UnknownConfirmedState);
                    if (ThermalConvergencePolicyAdmitted())
                    {
                        thermalConvergenceArbiter.OpenAdmission();
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.AwaitingObservation,
                            ThermalConvergenceReason.AwaitingFreshObservation,
                            []);
                    }
                    else
                    {
                        CloseThermalConvergenceAdmission(
                            ThermalConvergenceReason.AwaitingFreshObservation,
                            thermalConvergenceShutdown: false);
                    }
                }
            }
            finally
            {
                operationGate.Release();
            }
        }
        finally
        {
            if (sessionAdmissionPending)
            {
                Interlocked.Decrement(ref pendingSessionAdmissions);
            }
        }
    }

    public async Task<ControlOperationResult> SetThermalControlPolicyAsync(
        ThermalControlPolicy policy,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession()
                || string.IsNullOrWhiteSpace(Volatile.Read(ref activeUserSid)))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The active local Windows session changed before the thermal policy could be saved.");
            }
            if (!ConfigurationIsAdmitted || !thermalConvergenceLedgerAuthoritative)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Thermal policy changes are unavailable while configuration recovery is required.");
            }
            if (preferences.ThermalControlPolicy == policy)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    policy == ThermalControlPolicy.Strict
                        ? "Strict thermal control is already active."
                        : "Maintain desired thermal state is already selected.");
            }

            BatteryAutomationPreferences staged = preferences with
            {
                ThermalControlPolicy = policy,
            };
            validator.ValidatePreferences(staged);
            await ConfigurationStore.SaveConfigurationAsync(
                Automation.Profiles,
                staged,
                cancellationToken).ConfigureAwait(false);
            preferences = staged;
            RefreshThermalConvergenceIntentState(
                GetStatus().ConfirmedState ?? UnknownConfirmedState,
                policyChanged: true);

            if (ThermalConvergencePolicyAdmitted())
            {
                thermalConvergenceArbiter.OpenAdmission();
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.AwaitingObservation,
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    []);
            }
            else
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    thermalConvergenceShutdown: false);
            }

            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                policy == ThermalControlPolicy.Strict
                    ? "Strict thermal control is active; externally drifted thermal state remains advisory until an explicit command."
                    : preferences.ThermalPolicyUpgradeDisclosure
                        == ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending
                        ? "Maintain desired thermal state is selected and will remain paused until the upgrade disclosure is acknowledged."
                        : "Maintain desired thermal state is selected; a fresh safe observation is required before background convergence.");
        }
        catch (ConfigurationQuarantinedException)
        {
            ControlOperationResult blocked = ConfigurationQuarantineBlocked();
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.ConfigurationQuarantined,
                thermalConvergenceShutdown: false);
            return blocked;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult>
        AcknowledgeThermalPolicyUpgradeDisclosureAsync(
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession()
                || string.IsNullOrWhiteSpace(Volatile.Read(ref activeUserSid)))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The active local Windows session changed before the disclosure could be acknowledged.");
            }
            if (!ConfigurationIsAdmitted || !thermalConvergenceLedgerAuthoritative)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The thermal policy disclosure cannot be acknowledged while configuration recovery is required.");
            }
            if (preferences.ThermalPolicyUpgradeDisclosure !=
                ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "The thermal policy disclosure is already acknowledged or was not required.");
            }

            BatteryAutomationPreferences staged = preferences with
            {
                ThermalPolicyUpgradeDisclosure =
                    ThermalPolicyUpgradeDisclosureState.Acknowledged,
            };
            validator.ValidatePreferences(staged);
            await ConfigurationStore.SaveConfigurationAsync(
                Automation.Profiles,
                staged,
                cancellationToken).ConfigureAwait(false);
            preferences = staged;
            RefreshThermalConvergenceIntentState(
                GetStatus().ConfirmedState ?? UnknownConfirmedState,
                policyChanged: true);
            if (ThermalConvergencePolicyAdmitted())
            {
                thermalConvergenceArbiter.OpenAdmission();
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.AwaitingObservation,
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    []);
            }
            else
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    thermalConvergenceShutdown: false);
            }
            return new ControlOperationResult(
                ControlOperationStatus.Stored,
                "The Maintain desired state disclosure was acknowledged. A fresh safe observation is required before background convergence.");
        }
        catch (ConfigurationQuarantinedException)
        {
            ControlOperationResult blocked = ConfigurationQuarantineBlocked();
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.ConfigurationQuarantined,
                thermalConvergenceShutdown: false);
            return blocked;
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
            if (!ConfigurationIsAdmitted)
            {
                return ConfigurationQuarantineBlocked(DeviceSubsystem.Performance);
            }

            bool lightingWasSuppressed = lightingSuppressions.Count > 0;
            FanControlSettings priorFanIntent = ResolveCurrentFanIntent();
            try
            {
                profiles = ProfileStore.NormalizeLegacyBatteryProfile(
                    profiles,
                    out _,
                    batteryProtectionDefault: null);
                validator.Validate(profiles);
                await ConfigurationStore.SaveProfilesAsync(profiles, token);
                Automation.UpdateProfiles(profiles);
                if (ResolveCurrentFanIntent() != priorFanIntent)
                {
                    AdvanceFanDesiredRevision();
                }
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

    public async Task<ControlOperationResult> SetGpuClockTuningAsync(
        GpuClockTuningSettings value,
        OverrideLifetime lifetime,
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        EnsureInitialized();
        if (!GpuClockTuningSettings.IsWellFormed(value) || !Enum.IsDefined(lifetime))
        {
            return RecordRejectedIntent(new ArgumentException("GPU clock tuning is invalid."));
        }

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(token);
            // This operation never performs a firmware transition, including a
            // power-destination transition discovered while admitting the request.
            if (powerSource != PowerSource.PluggedIn || Automation.PowerSource != powerSource)
            {
                return new ControlOperationResult(ControlOperationStatus.Blocked,
                    "GPU clock tuning requires a confirmed, unchanged full-power adapter connection.");
            }

            ServiceStatusSnapshot current = GetStatus();
            DesiredDeviceState desired = ResolveHardwareDesired(
                current.ConfirmedState ?? UnknownConfirmedState) with
            {
                GpuClockTuning = value,
            };
            string? GpuWriteBlockReason() =>
                GetStatus().ConfirmedState?.HyperBoostActive == true && !ownsHyperBoost
                    ? "GPU clock tuning requires confirmed OpenBlade ownership of active HyperBoost."
                    : hyperBoostRestorePending
                    || pendingOwnedHyperBoostPowerBoundaryExit
                    || pendingIndeterminateHyperBoostEntryIntent is not null
                    || pendingIndeterminateHyperBoostExitIntent is not null
                    ? "HyperBoost restoration requires read-only reconciliation before GPU tuning."
                    : GetStatus().ControllerDetectionWarning is not null
                        || RazerThermalControllerPolicy.GetBlockingControllers(GetStatus()).Count > 0
                        ? "GPU clock tuning is blocked by a competing or unverified controller."
                        : null;
            string? block = ScopedPerformanceWriteBlockReason(
                desired, state => state with { GpuClockTuning = value },
                stillControlsSession, GpuWriteBlockReason,
                ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent);
            if (block is not null)
            {
                return new ControlOperationResult(ControlOperationStatus.Blocked, block);
            }

            HyperBoostOwnershipJournal? expectedJournal = ownedHyperBoostJournal;
            string? expectedSha256 = ownedHyperBoostJournalSha256;
            if (ownsHyperBoost)
            {
                HyperBoostOwnershipJournalLoadResult load;
                try
                {
                    load = await hyperBoostOwnershipJournalStore.LoadAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return PublishScopedUnavailable(DeviceSubsystem.Performance, powerSource,
                        $"The owned HyperBoost journal could not be read: {exception.Message}",
                        failed: true, preserveCurrentAvailability: true);
                }
                if (load.Invalid
                    || expectedJournal is not { State: HyperBoostOwnershipJournalState.OwnedConfirmed }
                    || expectedSha256 is null || load.Journal != expectedJournal
                    || load.Sha256 != expectedSha256)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return PublishScopedUnavailable(DeviceSubsystem.Performance, powerSource,
                        "The owned HyperBoost journal changed; GPU tuning is blocked.",
                        preserveCurrentAvailability: true);
                }
            }

            bool gpuSubmissionStarted = false;
            // Once admitted for submission, finish bounded readback and the
            // durable intent commit even if the requesting client disconnects.
            // The original token is checked at the last pre-submission gate.
            return await ApplyScopedIntentAsync(
                DeviceSubsystem.Performance,
                powerSource,
                (coordinator, _, cancellationToken) =>
                    coordinator.ApplyGpuClockTuningOutcomeAsync(value, cancellationToken,
                        async _ =>
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                PowerSource freshPower = await ResolveCurrentPowerSourceAsync(token)
                                    .ConfigureAwait(false);
                                RefreshControllerPresenceCore(token);
                                string? freshBlock = ScopedPerformanceWriteBlockReason(
                                    desired, state => state with { GpuClockTuning = value },
                                    stillControlsSession, GpuWriteBlockReason,
                                    ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent);
                                if (freshPower != PowerSource.PluggedIn || freshBlock is not null
                                    || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice freshDevice)
                                    || !ReferenceEquals(freshDevice, coordinator.ThermalDevice))
                                {
                                    throw new WriteAuthorityLostException(freshBlock
                                        ?? "Power or exact-device admission changed before GPU tuning.");
                                }
                                if (ownsHyperBoost)
                                {
                                    HyperBoostOwnershipJournalLoadResult latestJournal;
                                    try
                                    {
                                        latestJournal = await hyperBoostOwnershipJournalStore.LoadAsync(token)
                                            .ConfigureAwait(false);
                                    }
                                    catch (Exception exception) when (exception is not OperationCanceledException)
                                    {
                                        hyperBoostJournalSafetyBlock = true;
                                        throw new WriteAuthorityLostException(
                                            $"The owned HyperBoost journal could not be read before GPU tuning: {exception.Message}");
                                    }
                                    if (latestJournal.Invalid || expectedJournal is null || expectedSha256 is null
                                        || latestJournal.Journal != expectedJournal || latestJournal.Sha256 != expectedSha256)
                                    {
                                        hyperBoostJournalSafetyBlock = true;
                                        throw new WriteAuthorityLostException(
                                            "The owned HyperBoost journal changed before GPU tuning.");
                                    }
                                }
                                token.ThrowIfCancellationRequested();
                                gpuSubmissionStarted = true;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw new WriteAuthorityLostException(
                                    "GPU clock tuning was canceled before NVIDIA submission.");
                            }
                        }, ownsHyperBoost: ownsHyperBoost),
                CancellationToken.None,
                stageDesired: state => state with { GpuClockTuning = value },
                commitIntent: async cancellationToken =>
                {
                    if (!gpuSubmissionStarted)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                    if (ownsHyperBoost && expectedJournal is not null)
                    {
                        HyperBoostOwnershipJournal replacement = expectedJournal with
                        {
                            RestoreIntent = expectedJournal.RestoreIntent with { GpuClockTuning = value },
                        };
                        HyperBoostOwnershipJournalMutationResult updated =
                            await TryReplaceHyperBoostOwnershipJournalAsync(
                                expectedJournal, expectedSha256, replacement,
                                "GPU tuning was confirmed, but its HyperBoost restore intent could not be saved.")
                                .ConfigureAwait(false);
                        if (!updated.Applied || updated.Sha256 is null)
                        {
                            hyperBoostJournalSafetyBlock = true;
                            throw new IOException("The HyperBoost GPU restore intent could not be committed; control writes remain blocked.");
                        }
                        ownedHyperBoostJournal = replacement;
                        ownedHyperBoostJournalSha256 = updated.Sha256;
                        ownedHyperBoostRestoreIntent = replacement.RestoreIntent;
                    }
                    try
                    {
                        await SetPerformanceOverrideCoreAsync(new PerformanceOverride(
                            desired.Performance.Value, lifetime)
                        {
                            CustomPerformance = desired.CustomPerformance,
                            GpuClockTuning = value,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The journal remains the durable recovery authority if
                        // saving the user's normal override fails after its CAS.
                        if (ownsHyperBoost)
                        {
                            hyperBoostJournalSafetyBlock = true;
                        }
                        throw;
                    }
                },
                stillControlsSession: stillControlsSession,
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Performance], additionalWriteBlockReason: GpuWriteBlockReason),
                gpuClockTuningOnly: true).ConfigureAwait(false);
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
        CancellationToken token) => SetPerformanceConfigurationOverrideAsync(
            value,
            static () => true,
            token);

    public Task<ControlOperationResult> SetPerformanceConfigurationOverrideAsync(
        PerformanceOverride? value,
        Func<bool> stillControlsSession,
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
            deferHardwareApply: DeferredPerformanceIntentMessage,
            stillControlsSession: stillControlsSession);

    public async Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken token) => await CyclePerformanceShortcutAsync(
            source,
            static () => true,
            token).ConfigureAwait(false);

    public async Task<ControlOperationResult> CyclePerformanceShortcutAsync(
        PerformanceShortcutSource source,
        Func<bool> stillControlsSession,
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
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before the performance shortcut started.");
            }

            if (pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance))
            {
                return PendingWriteBlockedResult(DeviceSubsystem.Performance);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed during performance shortcut admission.");
            }

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
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before the performance shortcut intent was stored.");
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
                if (!stillControlsSession())
                {
                    return SessionAuthorityChanged(
                        "The active Windows session changed before the performance shortcut intent was stored.");
                }

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
                        cancellationToken),
                    stillControlsSession: stillControlsSession).ConfigureAwait(false);
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
        CancellationToken token) => SetFanControlOverrideCoreAsync(
            value,
            settings,
            stillControlsSession: null,
            token);

    public Task<ControlOperationResult> SetFanControlOverrideAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        Func<bool> stillControlsSession,
        CancellationToken token) => SetFanControlOverrideCoreAsync(
            value,
            settings,
            stillControlsSession
                ?? throw new ArgumentNullException(nameof(stillControlsSession)),
            token);

    private Task<ControlOperationResult> SetFanControlOverrideCoreAsync(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings,
        Func<bool>? stillControlsSession,
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
                FanControlSettings intendedFanControl = intended.FanControl
                    ?? FanControlSettings.FromLegacy(intended.Fans.Value);
                if (intendedFanControl != previous)
                {
                    AdvanceFanDesiredRevision();
                }
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
                : "Fan override intent was stored.",
            allowFanOwnershipRearm: true,
            stillControlsSession: stillControlsSession,
            thermalComponentResolver: desired =>
            {
                FanControlSettings requested = desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value);
                FanControlSettings prior = previous ?? requested;
                bool requestedManual = DeviceSubsystemOrder.IsManualFanMode(
                    requested.Mode);
                bool priorManual = DeviceSubsystemOrder.IsManualFanMode(prior.Mode);
                bool performanceMayChange = requestedManual
                        && (!priorManual || prior.Mode != requested.Mode)
                    || requested.Mode == FanMode.Automatic && priorManual;
                return performanceMayChange
                    ? new[]
                    {
                        DeviceSubsystem.Fans,
                        DeviceSubsystem.Performance,
                    }
                    : new[] { DeviceSubsystem.Fans };
            });
    }

    public async Task MaintainFanCurveAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (!ConfigurationIsAdmitted)
        {
            ClearLastFanCurveTargets();
            return;
        }

        ServiceStatusSnapshot snapshot = GetStatus();
        FanControlSettings settings = snapshot.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(snapshot.DesiredState.Fans.Value);
        if (!CanMaintainFanCurve(snapshot, settings))
        {
            ClearLastFanCurveTargets();
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
            try
            {
                RefreshControllerPresenceCore(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ClearLastFanCurveTargets();
                return;
            }

            ServiceStatusSnapshot current = GetStatus();
            if (!CanMaintainFanCurve(current, settings)
                || FanControllerAdmissionUnsafe(current))
            {
                ClearLastFanCurveTargets();
                return;
            }

            PowerSource confirmedPowerSource;
            try
            {
                confirmedPowerSource = await ResolveCurrentPowerSourceAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ClearLastFanCurveTargets();
                return;
            }

            if (confirmedPowerSource != PowerSource.ExternalUnknown
                && confirmedPowerSource != current.ActivePowerSource)
            {
                // The power handler owns destination-profile application, but a
                // fresh contradictory classification is already enough to
                // invalidate this destination's exact fan-intent stamp.
                AdvanceFanDesiredRevision();
                powerSourceGeneration++;
                return;
            }

            if (confirmedPowerSource == PowerSource.ExternalUnknown
                || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                ClearLastFanCurveTargets();
                return;
            }

            var authority = new ManualFanCurveAuthority(
                settings,
                current.DesiredState,
                targets,
                fanAuthorityEpoch,
                fanDesiredRevision,
                powerSourceGeneration,
                confirmedPowerSource,
                Interlocked.Read(ref sessionAdmissionEpoch),
                Volatile.Read(ref activeUserSid));
            if (ManualFanCurveWriteBlockReason(authority) is not null)
            {
                ClearLastFanCurveTargets();
                return;
            }

            try
            {
                ThermalTransitionExecution execution = await ThermalTransitionExecutor
                    .ExecuteAsync(
                        [DeviceSubsystem.Fans],
                        device,
                        (attemptDevice, runner) => new ControlCoordinator(
                            attemptDevice,
                            attemptDevice.ReadPerformanceFanStateAsync,
                            singleAttemptFanWrites: true,
                            thermalStepRunner: runner,
                            fanWriteBlockReason: () =>
                                ManualFanCurveWriteBlockReason(authority)),
                        (runner, token) => runner.ApplyStepAsync(
                            DeviceSubsystem.Fans,
                            ThermalTransitionStepPurpose.ManualCurveTarget,
                            (coordinator, stepToken) =>
                                coordinator.ApplyFanTargetsOutcomeAsync(
                                    targets.Fan1Rpm,
                                    targets.Fan2Rpm,
                                    stepToken),
                            token),
                        (_, token) => RevalidateManualFanCurveRetryAsync(
                            authority,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);
                ScopedApplyOutcome outcome = execution.Outcome;
                if (outcome.Result.Status == ControlResultStatus.Applied
                    && outcome.WriteAttempted)
                {
                    if (ManualFanCurveWriteBlockReason(authority) is not null)
                    {
                        AdvanceFanAuthorityEpoch();
                        return;
                    }
                    OwnExactFanIntent(settings);
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
                    return;
                }

                ClearLastFanCurveTargets();
                if (outcome.WriteSubmissionState == WriteSubmissionState.Indeterminate)
                {
                    exactFanControlOwnership = ExactFanControlOwnership.SubmissionUnresolved;
                    driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
                    PublishIfChanged(GetStatus() with
                    {
                        ExternalChanges = driftTracker.ExternalChanges,
                        Message =
                            $"{outcome.Result.Message} Automatic fan-curve maintenance will not replay the target write until an explicit fan action establishes fresh ownership.",
                    });
                    return;
                }

                if (outcome.Result.Status == ControlResultStatus.Blocked
                    && (outcome.Readback?.HyperBoostActive == true
                        || outcome.Readback?.Fans is { } observedFans
                            && !DeviceSubsystemOrder.IsManualFanMode(observedFans)))
                {
                    AdvanceFanAuthorityEpoch();
                    if (outcome.Readback?.Fans is { } contradictedFans
                        && !DeviceSubsystemOrder.IsManualFanMode(contradictedFans))
                    {
                        driftTracker.ConfirmIntentional(
                            DeviceSubsystem.Fans,
                            matched: false);
                    }
                    PublishIfChanged(GetStatus() with
                    {
                        ExternalChanges = driftTracker.ExternalChanges,
                        Message = outcome.Result.Message,
                    });
                    return;
                }

                if (outcome.Result.Status == ControlResultStatus.Blocked
                    || string.Equals(
                        execution.Trace.RetryBlockReason,
                        ManualFanCurveTargetsChangedRetryBlockReason,
                        StringComparison.Ordinal))
                {
                    return;
                }

                exactFanControlOwnership = ExactFanControlOwnership.Unowned;
                driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
                PublishIfChanged(GetStatus() with
                {
                    ExternalChanges = driftTracker.ExternalChanges,
                    Message = outcome.Result.Message,
                });
            }
            catch (IntentionalWriteCanceledException exception)
                when (exception.WriteAttempted.Contains(DeviceSubsystem.Fans))
            {
                exactFanControlOwnership = ExactFanControlOwnership.SubmissionUnresolved;
                ClearLastFanCurveTargets();
                throw;
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
            if (lowPowerGate.IsActive)
            {
                return new(ControlOperationStatus.Blocked,
                    "Keyboard brightness input is deferred during a low-power transition.");
            }
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
        try
        {
            await SetLightingOverrideCoreAsync(restored, token).ConfigureAwait(false);
        }
        catch (ConfigurationQuarantinedException)
        {
            return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
        }
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
        catch (ConfigurationQuarantinedException)
        {
            return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
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
                await SaveCommittedLightingPreferencesAsync(staged, token)
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

                if (saveException is ConfigurationQuarantinedException)
                {
                    return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
                }

                throw;
            }
            catch (ConfigurationQuarantinedException)
            {
                return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
            }

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
            PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
            {
                LightingRendererOwnership = LightingRendererOwnershipState.None,
                LightingSuppressions = CurrentLightingSuppressions(),
                Message = message,
            }));
            history.RecordSummary(ControlOperationStatus.Stored, message);
            return new ControlOperationResult(ControlOperationStatus.Stored, message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        CancellationToken token) => await ReapplyChangedSettingsAsync(
            static () => true,
            token).ConfigureAwait(false);

    public async Task<ControlOperationResult> ReapplyChangedSettingsAsync(
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before settings could be reapplied.");
            }

            if (pendingWriteConfirmations.HasPending)
            {
                const string pendingMessage =
                    "OpenBlade must confirm the current laptop state before settings can be reapplied.";
                history.RecordSummary(ControlOperationStatus.Blocked, pendingMessage);
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    pendingMessage);
            }

            if (thermalConvergenceLedger is { InFlight: not null })
            {
                const string reconciliationMessage =
                    "OpenBlade must reconcile the reserved thermal transition before changed settings can be reapplied.";
                history.RecordSummary(
                    ControlOperationStatus.Blocked,
                    reconciliationMessage);
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    reconciliationMessage);
            }

            if (thermalConvergenceLedgerAuthoritative
                && thermalConvergenceLedger is { } convergenceLedger
                && (convergenceLedger.Tombstones.Count > 0
                    || convergenceLedger.Budgets.Count > 0))
            {
                ThermalConvergenceLedgerState rearmed = convergenceLedger with
                {
                    Tombstones = [],
                    Budgets = [],
                    TerminalDisposition = null,
                    TerminalAffectedComponents = [],
                };
                if (!await TryPersistThermalConvergenceLedgerAsync(rearmed, token)
                        .ConfigureAwait(false))
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        "The durable thermal convergence suppression could not be retired, so no background retry was armed.");
                }
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.AwaitingObservation,
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    []);
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
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed during changed-settings admission.");
            }

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
            if (!stillControlsSession())
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before settings could be reapplied.");
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
                if (!stillControlsSession())
                {
                    results[subsystem] = SessionAuthorityChanged(
                        "The active Windows session changed before settings could be reapplied.");
                    break;
                }

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
                    token,
                    stillControlsSession).ConfigureAwait(false);
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
                ? $"OpenBlade reapplied {DeviceSubsystemFormatter.FormatList(reapplied)} from the current settings."
                : reapplied.Length == 0
                    ? $"OpenBlade could not reapply the changed settings. Still changed: {DeviceSubsystemFormatter.FormatList(remaining)}.{remainingReasons}"
                    : $"OpenBlade reapplied {DeviceSubsystemFormatter.FormatList(reapplied)}. Still changed: {DeviceSubsystemFormatter.FormatList(remaining)}.{remainingReasons}";
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
                hyperBoostJournalSafetyBlock = true;
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
            HyperBoostOwnershipJournal transitionJournal = journal;
            string? transitionJournalSha256 = load.Sha256;

            hyperBoostRestorePending = true;
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            ServiceStatusSnapshot current = GetStatus();
            if (!current.DeviceVerified
                || !hyperBoostJournalSafetyBlock
                || pendingWriteConfirmations.HasPending
                || current.ControllerDetectionWarning is not null
                || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0
                || powerSource != PowerSource.PluggedIn
                || current.ActivePowerSource != powerSource
                || current.Capabilities is null
                || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved HyperBoost restore can be retried only on a confirmed full-power adapter after fresh exact-device, controller, and thermal safety checks pass.",
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

            bool matchesTransitionBaseline =
                ConfirmedStateMatchesHyperBoostTransitionBaseline(
                    fresh,
                    journal);
            HyperBoostRecoveryBaseline? recoveryBaseline =
                journal.RecoveryBaseline;
            bool matchesRecordedRecoveryBaseline =
                recoveryBaseline is not null
                && ConfirmedStateMatchesHyperBoostRecoveryBaseline(
                    fresh,
                    recoveryBaseline);
            if (!matchesTransitionBaseline
                && recoveryBaseline is null
                && load.AllowsExplicitRecoveryBaselineCapture)
            {
                recoveryBaseline = CreateExactHyperBoostRecoveryBaseline(
                    fresh,
                    journal);
                if (recoveryBaseline is not null)
                {
                    var replacementJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        journal.BaseMode,
                        journal.RequiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = restoreIntent,
                        RecoveryBaseline = recoveryBaseline,
                        TransitionId = transitionJournal.TransitionId,
                    };
                    HyperBoostOwnershipJournalMutationResult replacement =
                        await TryReplaceHyperBoostOwnershipJournalAsync(
                                transitionJournal,
                                transitionJournalSha256,
                                replacementJournal,
                                "The legacy HyperBoost restore could not preserve its fresh recovery baseline.")
                            .ConfigureAwait(false);
                    if (!replacement.Applied)
                    {
                        return PublishHyperBoostRestoreRecoveryResult(
                            ControlOperationStatus.Failed,
                            "The legacy saved restore was not retried because its fresh recovery baseline could not be durably recorded.",
                            fresh,
                            restorePending: true);
                    }
                    transitionJournal = replacementJournal;
                    transitionJournalSha256 = replacement.Sha256;

                    matchesRecordedRecoveryBaseline =
                        ConfirmedStateMatchesHyperBoostRecoveryBaseline(
                            fresh,
                            recoveryBaseline);
                }
            }
            if (!matchesTransitionBaseline
                && !matchesRecordedRecoveryBaseline)
            {
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved restore was not retried because fresh thermal readback no longer matches a recorded safe recovery baseline.",
                    fresh,
                    restorePending: true);
            }

            if (!IsSupportedFullPowerHyperBoostIntent(
                    current.Capabilities,
                    restoreIntent))
            {
                if (!ConfigurationIsAdmitted)
                {
                    return PublishHyperBoostRestoreRecoveryResult(
                        ControlOperationStatus.Blocked,
                        "The saved HyperBoost restore was preserved because quarantined configuration cannot replace its exact journaled intent.",
                        fresh,
                        restorePending: true);
                }

                if (restoreIntent.Performance is not
                    (PerformancePreset.BatterySaver
                        or PerformancePreset.BatteryBalanced))
                {
                    return PublishHyperBoostRestoreRecoveryResult(
                        ControlOperationStatus.Blocked,
                        "The saved full-power restore intent is no longer admitted for this exact device and was preserved without a hardware write.",
                        fresh,
                        restorePending: true);
                }

                HyperBoostRestoreIntent currentDestinationIntent =
                    CreateHyperBoostRestoreIntent(Automation.Resolve(fresh));
                if (!IsSupportedFullPowerHyperBoostIntent(
                        current.Capabilities,
                        currentDestinationIntent))
                {
                    return PublishHyperBoostRestoreRecoveryResult(
                        ControlOperationStatus.Blocked,
                        "The saved restore was not retried because the current full-power destination intent is not admitted for this exact device.",
                        fresh,
                        restorePending: true);
                }

                var retryBaseline = new HyperBoostRecoveryBaseline(
                    fresh.Performance,
                    fresh.Fans ?? FanMode.Automatic,
                    fresh.HyperBoostActive,
                    fresh.CustomPerformance,
                    fresh.GpuClockOffsets?.Current);
                if (!retryBaseline.IsWellFormed)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return PublishHyperBoostRestoreRecoveryResult(
                        ControlOperationStatus.Failed,
                        "The saved restore was not retried because its current full-power destination could not be recorded durably.",
                        fresh,
                        restorePending: true);
                }
                var replacementJournal = new HyperBoostOwnershipJournal(
                    HyperBoostOwnershipJournalState.RestorePending,
                    journal.BaseMode,
                    journal.RequiresCoolingPad,
                    timeProvider.GetUtcNow())
                {
                    RestoreIntent = currentDestinationIntent,
                    RecoveryBaseline = retryBaseline,
                    TransitionId = transitionJournal.TransitionId,
                };
                HyperBoostOwnershipJournalMutationResult replacement =
                    await TryReplaceHyperBoostOwnershipJournalAsync(
                        transitionJournal,
                        transitionJournalSha256,
                        replacementJournal,
                        "The user-authorized HyperBoost restore could not durably replace its obsolete low-power destination before submission.")
                    .ConfigureAwait(false);
                if (!replacement.Applied)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return PublishHyperBoostRestoreRecoveryResult(
                        ControlOperationStatus.Failed,
                        "The saved restore was not retried because its current full-power destination could not be recorded durably.",
                        fresh,
                        restorePending: true);
                }
                transitionJournal = replacementJournal;
                transitionJournalSha256 = replacement.Sha256;
                hyperBoostJournalSafetyBlock = true;
                restoreIntent = currentDestinationIntent;
            }

            HyperBoostOwnershipJournalLoadResult transitionLoad;
            try
            {
                transitionLoad = await hyperBoostOwnershipJournalStore
                    .LoadAsync(token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                hyperBoostJournalSafetyBlock = true;
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    $"The saved HyperBoost restore was not retried because its final durable journal could not be inspected: {exception.Message}",
                    fresh,
                    restorePending: true);
            }

            if (transitionLoad.Invalid
                || transitionLoad.Journal != transitionJournal
                || transitionLoad.Sha256 != transitionJournalSha256)
            {
                hyperBoostJournalSafetyBlock = true;
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Blocked,
                    "The saved HyperBoost restore was not retried because its durable journal changed before transition admission.",
                    fresh,
                    restorePending: true);
            }

            long transitionOwnershipGeneration = hyperBoostOwnershipGeneration;
            long transitionPowerGeneration = powerSourceGeneration;
            ThermalTransitionStepRunner transitionRunner =
                CreateHyperBoostTransitionRunner(
                    device,
                    enabled: false,
                    transitionJournal.BaseMode,
                    restoreIntent,
                    transitionJournal,
                    transitionJournalSha256,
                    transitionOwnershipGeneration,
                    transitionPowerGeneration,
                    powerSource,
                    ownedLifecycleRestore: false,
                    allowOwnedJournalCleanup: true,
                    validationOnly: false,
                    authorityPurpose:
                        ScopedThermalAuthorityPurpose.PriorAuthorityCleanup,
                    allowIntentRebase: true);

            HyperBoostIntentRestorationResult restoration;
            try
            {
                restoration = await RestoreHyperBoostIntentAsync(
                            transitionRunner.CurrentDevice,
                            restoreIntent,
                            fresh,
                            token,
                            transitionRunner: transitionRunner)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                FailedHyperBoostRestoreObservation cancellationObservation =
                    await CaptureFailedHyperBoostRestoreObservationAsync(
                            transitionRunner.CurrentDevice,
                            restoreIntent,
                            new HyperBoostIntentRestorationResult(
                                false,
                                fresh,
                                "The user-authorized restore was canceled."),
                            fresh)
                        .ConfigureAwait(false);
                bool recoveryBaselineUpdated = false;
                if (cancellationObservation.RecoveryBaseline is { } canceledBaseline)
                {
                    var replacementJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        transitionJournal.BaseMode,
                        transitionJournal.RequiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = restoreIntent,
                        RecoveryBaseline = canceledBaseline,
                        TransitionId = transitionJournal.TransitionId,
                    };
                    HyperBoostOwnershipJournalMutationResult replacement =
                        await TryReplaceHyperBoostOwnershipJournalAsync(
                            transitionJournal,
                            transitionJournalSha256,
                            replacementJournal,
                            "The canceled user-authorized HyperBoost restore could not preserve its fresh recovery baseline.")
                        .ConfigureAwait(false);
                    recoveryBaselineUpdated = replacement.Applied;
                }
                hyperBoostJournalSafetyBlock = true;
                PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    "The user-authorized HyperBoost restore was canceled. The saved restore remains blocked and will not be retried automatically."
                    + (recoveryBaselineUpdated
                        ? " Its fresh post-cancellation topology was recorded for a later explicit retry."
                        : " Its fresh post-cancellation topology could not be recorded."),
                    cancellationObservation.State,
                    restorePending: true);
                throw;
            }
            ConfirmedDeviceState restored = restoration.Readback ?? fresh;
            if (!restoration.Succeeded)
            {
                FailedHyperBoostRestoreObservation failureObservation =
                    await CaptureFailedHyperBoostRestoreObservationAsync(
                            transitionRunner.CurrentDevice,
                            restoreIntent,
                            restoration,
                            restored)
                        .ConfigureAwait(false);
                restored = failureObservation.State;
                bool recoveryBaselineUpdated = false;
                if (failureObservation.RecoveryBaseline is { } failedBaseline)
                {
                    var replacementJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        transitionJournal.BaseMode,
                        transitionJournal.RequiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = restoreIntent,
                        RecoveryBaseline = failedBaseline,
                        TransitionId = transitionJournal.TransitionId,
                    };
                    HyperBoostOwnershipJournalMutationResult replacement =
                        await TryReplaceHyperBoostOwnershipJournalAsync(
                            transitionJournal,
                            transitionJournalSha256,
                            replacementJournal,
                            "The failed user-authorized HyperBoost restore could not preserve its fresh recovery baseline.")
                        .ConfigureAwait(false);
                    recoveryBaselineUpdated = replacement.Applied;
                }
                hyperBoostJournalSafetyBlock = true;
                return PublishHyperBoostRestoreRecoveryResult(
                    ControlOperationStatus.Failed,
                    $"The user-authorized HyperBoost restore retry did not complete. {restoration.Message} The saved restore was preserved and will not be retried automatically."
                    + (recoveryBaselineUpdated
                        ? " Its fresh post-failure topology was recorded for a later explicit retry."
                        : " Its fresh post-failure topology could not be recorded."),
                    restored,
                    restorePending: true);
            }

            driftTracker.Observe(restored);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
            bool retired = await RetireHyperBoostOwnershipJournalAsync(
                    transitionJournal.BaseMode,
                    transitionJournal.RequiresCoolingPad,
                    restoreIntent,
                    "The user-authorized HyperBoost restore completed, but its ownership journal could not be retired.",
                    transitionJournal,
                    transitionJournalSha256)
                .ConfigureAwait(false);
            hyperBoostRestorePending = !retired;
            if (retired)
            {
                RestoreRecoveredHyperBoostIntentInAutomation(restoreIntent);
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
            ObservationReason = status == ControlOperationStatus.Applied && confirmed is not null
                ? DeviceObservationReason.None
                : current.ObservationReason,
            DesiredState = Automation.Resolve(effectiveConfirmed),
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = hyperBoostJournalSafetyBlock
                || restorePending
                || pendingWriteConfirmations.HasPending,
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
        CancellationToken token,
        Func<bool>? stillControlsSession = null) => subsystem switch
        {
            DeviceSubsystem.Performance => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyPerformanceOutcomeAsync(
                        desired,
                        cancellationToken),
                token,
                stillControlsSession: stillControlsSession),
            DeviceSubsystem.Fans => ApplyScopedIntentAsync(
                subsystem,
                powerSource,
                (coordinator, desired, cancellationToken) =>
                    coordinator.ApplyRecoveryFanControlOutcomeAsync(
                        desired.FanControl
                            ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                        cancellationToken),
                token,
                stillControlsSession: stillControlsSession,
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Fans])),
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
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (enabled && IsKeyboardLightingDelegated)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "OpenBlade cannot override external keyboard lighting while control is delegated to Windows.");
            }
            BatteryAutomationPreferences staged = preferences with
            {
                AlwaysOverrideExternalLightingChanges = enabled,
            };
            try
            {
                await SaveCommittedLightingPreferencesAsync(staged, token)
                    .ConfigureAwait(false);
            }
            catch (ConfigurationQuarantinedException)
            {
                return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
            }
            ServiceStatusSnapshot current = GetStatus();
            PublishIfChanged(ProjectCommittedLightingPreferences(current with
            {
                Message = enabled
                    ? "OpenBlade will reapply its last lighting effect after external changes."
                    : "OpenBlade will preserve confirmed external lighting changes.",
            }));

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
                try
                {
                    await SaveCommittedLightingPreferencesAsync(staged, token)
                        .ConfigureAwait(false);
                }
                catch (ConfigurationQuarantinedException)
                {
                    return ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
                }
                PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                {
                    Message = "The lighting refresh rate was saved; renderer confirmation is pending.",
                }));
            }

            deviceProvider.SetLightingRefreshRate(refreshRate);

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                string message = preferenceChanged
                    ? "The lighting refresh rate was saved and will apply when the keyboard reconnects."
                    : $"Keyboard lighting is already set to {RefreshRateLabel(refreshRate)}.";
                PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                {
                    Message = message,
                }));
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    message);
            }

            async Task<Exception?> TryRollbackPreferenceAsync()
            {
                if (!preferenceChanged)
                {
                    return null;
                }

                try
                {
                    await SaveCommittedLightingPreferencesAsync(
                        previousPreferences,
                        CancellationToken.None).ConfigureAwait(false);
                    deviceProvider.SetLightingRefreshRate(
                        previousPreferences.LightingRefreshRate);
                    PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                    {
                        Message = current.Message,
                    }));
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
                PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                {
                    Message = $"Keyboard lighting refresh rate is set to {RefreshRateLabel(refreshRate)}.",
                }));
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
                    const string message =
                        "The lighting refresh-rate change was canceled after persistence, and the saved selection could not be rolled back.";
                    PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                    {
                        Message = message,
                    }));
                    throw new InvalidOperationException(
                        message,
                        rollbackException);
                }

                throw;
            }
            catch (Exception exception)
            {
                Exception? rollbackException = await TryRollbackPreferenceAsync()
                    .ConfigureAwait(false);
                string message = rollbackException is not null
                    ? "The active lighting renderer could not be updated, and the saved refresh-rate selection could not be rolled back. Retry the selection. "
                        + exception.Message
                        + " Rollback: "
                        + rollbackException.Message
                    : "The active lighting renderer could not be updated, so the refresh-rate selection was rolled back. "
                        + exception.Message;
                PublishIfChanged(ProjectCommittedLightingPreferences(GetStatus() with
                {
                    Message = message,
                }));
                return new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    message);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    // Callers hold operationGate and retain their own renderer stop/apply/rollback
    // ordering. Only successful persistence advances the committed preference.
    private async Task SaveCommittedLightingPreferencesAsync(
        BatteryAutomationPreferences staged,
        CancellationToken cancellationToken)
    {
        await ConfigurationStore.SaveConfigurationAsync(
            Automation.Profiles, staged, cancellationToken).ConfigureAwait(false);
        preferences = staged;
    }

    private ServiceStatusSnapshot ProjectCommittedLightingPreferences(ServiceStatusSnapshot snapshot) =>
        snapshot with
        {
            KeyboardLightingControlMode = preferences.KeyboardLightingControlMode,
            AlwaysOverrideExternalLightingChanges = preferences.AlwaysOverrideExternalLightingChanges,
            LightingRefreshRate = EffectiveLightingRefreshRate(snapshot.Capabilities),
        };

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
            try
            {
                await ConfigurationStore.SaveConfigurationAsync(
                        Automation.Profiles,
                        staged,
                        token)
                    .ConfigureAwait(false);
            }
            catch (ConfigurationQuarantinedException)
            {
                return ConfigurationQuarantineBlocked();
            }

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

    public async Task<ControlOperationResult> SetExperimentalFeaturesAsync(
        bool enabled,
        CancellationToken token)
    {
        EnsureInitialized();
        string message = enabled
            ? "Experimental laptop controls are enabled for this Windows user."
            : "Experimental laptop controls are disabled.";

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BatteryAutomationPreferences staged = preferences with
            {
                AllowExperimentalFeatures = enabled,
            };
            try
            {
                await ConfigurationStore.SaveConfigurationAsync(
                        Automation.Profiles,
                        staged,
                        token)
                    .ConfigureAwait(false);
            }
            catch (ConfigurationQuarantinedException)
            {
                return ConfigurationQuarantineBlocked();
            }

            preferences = staged;
            // The provider owns the final admission gate for every experimental
            // write path. Revocation is forwarded before any subsequent status
            // observation so disabling consent cannot submit a cleanup write.
            deviceProvider.SetAllowExperimentalFeatures(enabled);
            consecutiveDeviceProbeFailures = ObservationFailureThreshold;
            PublishIfChanged(GetStatus() with
            {
                AllowExperimentalFeatures = enabled,
                IsExperimentalDevice = deviceProvider.IsExperimentalDevice,
                ExperimentalSupportSource = deviceProvider.ExperimentalSupportSource,
                Message = message,
            });
            history.RecordSummary(ControlOperationStatus.Stored, message);
        }
        finally
        {
            operationGate.Release();
        }

        try
        {
            await ObserveAsync(includeLiveReadings: false, token).ConfigureAwait(false);
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

    public DiagnosticExperimentalSupport GetCachedExperimentalSupportDiagnostics() =>
        deviceProvider.GetExperimentalSupportDiagnostics();

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
        if (lowPowerGate.IsActive)
        {
            return new(ControlOperationStatus.Blocked, "Key-triggered lighting is paused during standby.");
        }
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
        if (lowPowerGate.IsActive)
        {
            return new(ControlOperationStatus.Blocked, "Ambient lighting is paused during standby.");
        }
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
        if (lowPowerGate.IsActive)
        {
            return new(ControlOperationStatus.Blocked, "Playback lighting is paused during standby.");
        }
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
            Guid.NewGuid(),
            expectedOwnershipGeneration: null,
            lifecycleValidation: null,
            restoreUserIntent: true,
            exitPowerSourceOverride: null,
            expectedExitPowerSourceGeneration: null,
            token);

    public Task<ControlOperationResult> SetHyperBoostAsync(
        SetHyperBoostRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetHyperBoostCoreAsync(
            request.Enabled,
            request.RequestId,
            expectedOwnershipGeneration: null,
            lifecycleValidation: null,
            restoreUserIntent: true,
            exitPowerSourceOverride: null,
            expectedExitPowerSourceGeneration: null,
            token);
    }

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
        PadlessHyperBoostLifecycleTarget? target =
            PadlessHyperBoostLifecycleTarget.ForCurrentWindowsHost();
        PadlessHyperBoostLifecycleValidationBuildIdentity build =
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent(target);
        if (!build.Authorizes(
                target,
                request.ExpectedSourceRevision,
                request.ExpectedServiceAssemblySha256,
                request.ExpectedAdmissionManifestSha256))
        {
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Pad-less HyperBoost lifecycle validation is unavailable until the exact validation build and every non-lifecycle evidence gate are confirmed."));
        }

        return SetHyperBoostCoreAsync(
            enabled: true,
            transitionId: request.ValidationRunId,
            expectedOwnershipGeneration: null,
            request,
            restoreUserIntent: false,
            exitPowerSourceOverride: null,
            expectedExitPowerSourceGeneration: null,
            token,
            lifecycleTarget: target);
    }

    public Task<ControlOperationResult> ValidateThermalExternalDriftAsync(
        ValidateThermalExternalDriftRequest request,
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        ThermalExternalDriftValidationBuildIdentity build =
            ThermalExternalDriftValidationBuildIdentity.ReadCurrent();
        if (!build.Matches(
                request.ExpectedSourceRevision,
                request.ExpectedServiceAssemblySha256,
                request.ExpectedProtocolAssemblySha256,
                request.ExpectedProductionFixtureSha256))
        {
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "Thermal external-drift validation requires the exact validation-marked Service, Protocol assembly, source revision, and captured production fixture."));
        }

        return ValidateThermalExternalDriftCoreAsync(
            request,
            stillControlsSession,
            token);
    }

    internal async Task<ControlOperationResult> ValidateThermalExternalDriftCoreAsync(
        ValidateThermalExternalDriftRequest request,
        Func<bool> stillControlsSession,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stillControlsSession);
        EnsureInitialized();

        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!stillControlsSession())
            {
                return ThermalExternalDriftBlocked(
                    "The active Windows session changed before validation preflight completed.");
            }
            if (!fullObservationCompleted || startupProfileReconciliationPending)
            {
                return ThermalExternalDriftBlocked(
                    "Thermal external-drift validation requires completed startup reconciliation and a fresh full observation.");
            }
            if (!ThermalConvergencePolicyAdmitted())
            {
                return ThermalExternalDriftBlocked(
                    "Thermal external-drift validation requires an admitted Maintain desired state policy in the active local session.");
            }
            if (Volatile.Read(ref pendingPowerAdmissions) > 0
                || Volatile.Read(ref pendingSessionAdmissions) > 0)
            {
                return ThermalExternalDriftBlocked(
                    "A power or active-user admission is queued before validation.");
            }

            long expectedPowerAdmissionEpoch =
                Interlocked.Read(ref powerAdmissionEpoch);
            long expectedSessionAdmissionEpoch =
                Interlocked.Read(ref sessionAdmissionEpoch);
            string? expectedActiveUserSid = Volatile.Read(ref activeUserSid);
            string? expectedLedgerSha256 = thermalConvergenceLedgerSha256;
            ThermalConvergenceLedgerState? expectedLedger = thermalConvergenceLedger;

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(token);
            ServiceStatusSnapshot current = GetStatus();
            string? preflightBlock = ThermalExternalDriftPreflightBlockReason(
                current,
                powerSource,
                expectedLedger,
                expectedLedgerSha256,
                expectedPowerAdmissionEpoch,
                expectedSessionAdmissionEpoch,
                expectedActiveUserSid,
                stillControlsSession);
            if (preflightBlock is not null)
            {
                return ThermalExternalDriftBlocked(preflightBlock);
            }
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device)
                || device is not IThermalExternalDriftValidationDevice validationDevice)
            {
                return ThermalExternalDriftBlocked(
                    "The exact RZ09-0581 validation-only device path is unavailable.");
            }

            DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(token)
                .ConfigureAwait(false);
            if (!capabilities.PerformancePresets.Contains(PerformancePreset.Balanced)
                || !capabilities.PerformancePresets.Contains(PerformancePreset.Silent))
            {
                return ThermalExternalDriftBlocked(
                    "The exact device no longer admits both captured automatic validation presets.");
            }

            ConfirmedDeviceState fresh = await device
                .ReadPerformanceFanStateAsync(token)
                .ConfigureAwait(false);
            DesiredDeviceState desired = current.DesiredState;
            if (!IsThermalExternalDriftBaseline(desired, fresh))
            {
                return ThermalExternalDriftBlocked(
                    "Fresh readback must match a Balanced or Silent performance intent with Automatic fans and no GPU tuning before validation.");
            }

            PowerSource finalPowerSource = await ResolveCurrentPowerSourceAsync(token)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(token);
            current = GetStatus();
            string? finalBlock = ThermalExternalDriftPreflightBlockReason(
                current,
                finalPowerSource,
                expectedLedger,
                expectedLedgerSha256,
                expectedPowerAdmissionEpoch,
                expectedSessionAdmissionEpoch,
                expectedActiveUserSid,
                stillControlsSession);
            if (finalBlock is not null
                || !IsThermalExternalDriftBaseline(current.DesiredState, fresh)
                || current.DesiredState != desired)
            {
                return ThermalExternalDriftBlocked(
                    finalBlock
                    ?? "The authoritative thermal intent changed before validation submission.");
            }
            string? durableBlock =
                await RevalidateThermalExternalDriftLedgerAsync(
                        expectedLedger,
                        expectedLedgerSha256,
                        token)
                    .ConfigureAwait(false);
            if (durableBlock is not null)
            {
                return ThermalExternalDriftBlocked(durableBlock);
            }
            if (!thermalExternalDriftValidationRuns.Add(request.ValidationRunId))
            {
                return ThermalExternalDriftBlocked(
                    "This thermal external-drift validation run was already used.");
            }

            PerformancePreset candidate = desired.Performance.Value ==
                PerformancePreset.Balanced
                    ? PerformancePreset.Silent
                    : PerformancePreset.Balanced;
            Exception? possibleSubmission = null;
            try
            {
                await validationDevice.InjectPerformanceDriftOnceAsync(
                        candidate,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (BladeWriteException exception) when (
                exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                return ThermalExternalDriftFailed(
                    $"The single synthetic drift submission was proven not applied and was not retried: {exception.Message}");
            }
            catch (BladeWriteException exception)
            {
                possibleSubmission = exception;
            }
            catch (Exception exception)
            {
                possibleSubmission = exception;
            }

            ConfirmedDeviceState readback;
            try
            {
                readback = await device
                    .ReadPerformanceFanStateAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LatchThermalExternalDriftConfirmation(
                    current,
                    fresh,
                    candidate,
                    "Synthetic performance drift may have been submitted, but readback failed. No retry or restoration write was issued.");
                return ThermalExternalDriftFailed(
                    $"Synthetic performance drift requires read-only reconciliation before any recovery: {exception.Message}");
            }

            if (IsThermalExternalDriftCandidate(candidate, readback))
            {
                string detail =
                    $"Thermal external-drift validation run {request.ValidationRunId:D} confirmed one Service-owned synthetic performance mutation from {fresh.Performance} to {candidate}; deviceFingerprint={expectedLedger!.DeviceFingerprint}; desired intent and convergence authority were unchanged, and the periodic Maintain observation now owns reconciliation.";
                history.RecordSummary(
                    ControlOperationStatus.Applied,
                    detail,
                    request.ValidationRunId);
                return new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    possibleSubmission is null
                        ? detail
                        : detail + " The setter reported possible submission, and fresh readback resolved it without replay.");
            }

            if (IsThermalExternalDriftBaseline(desired, readback))
            {
                return ThermalExternalDriftFailed(
                    possibleSubmission is null
                        ? "The synthetic mutation returned without error, but fresh readback remained at the original baseline. No retry was issued."
                        : "The synthetic mutation reported possible submission, but fresh readback resolved to the original baseline. No retry was issued.");
            }

            LatchThermalExternalDriftConfirmation(
                current,
                fresh,
                candidate,
                "Synthetic performance drift may have been submitted, but readback was ambiguous. No retry or restoration write was issued.");
            return ThermalExternalDriftFailed(
                "Synthetic performance drift requires read-only reconciliation before any recovery; the validation run stopped without replay.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ThermalExternalDriftFailed(
                $"Thermal external-drift validation stopped safely before a confirmed result: {exception.Message}");
        }
        finally
        {
            operationGate.Release();
        }
    }

    private string? ThermalExternalDriftPreflightBlockReason(
        ServiceStatusSnapshot current,
        PowerSource powerSource,
        ThermalConvergenceLedgerState? expectedLedger,
        string? expectedLedgerSha256,
        long expectedPowerAdmissionEpoch,
        long expectedSessionAdmissionEpoch,
        string? expectedActiveUserSid,
        Func<bool> stillControlsSession)
    {
        if (!stillControlsSession()
            || Interlocked.Read(ref powerAdmissionEpoch)
                != expectedPowerAdmissionEpoch
            || Interlocked.Read(ref sessionAdmissionEpoch)
                != expectedSessionAdmissionEpoch
            || Volatile.Read(ref pendingPowerAdmissions) > 0
            || Volatile.Read(ref pendingSessionAdmissions) > 0
            || !string.Equals(
                Volatile.Read(ref activeUserSid),
                expectedActiveUserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Power or active-user authority changed during validation preflight.";
        }
        if (!ThermalConvergencePolicyAdmitted()
            || !ReferenceEquals(thermalConvergenceLedger, expectedLedger)
            || !string.Equals(
                thermalConvergenceLedgerSha256,
                expectedLedgerSha256,
                StringComparison.Ordinal))
        {
            return "Maintain policy or durable convergence authority changed during validation preflight.";
        }
        if (powerSource != PowerSource.PluggedIn
            || current.ActivePowerSource != PowerSource.PluggedIn)
        {
            return "Thermal external-drift validation requires confirmed settled full power.";
        }
        if (!current.DeviceVerified
            || current.WritesBlocked
            || lowPowerGate.IsActive
            || thermalConvergenceShutdown
            || pendingWriteConfirmations.HasPending)
        {
            return "Device, lifecycle, shutdown, or pending-write admission is closed.";
        }
        if (!controllerPresenceObserved
            || FanControllerAdmissionUnsafe(current))
        {
            return "Competing-controller absence could not be freshly confirmed.";
        }
        if (current.ExternalChanges.Count > 0
            || current.UnconfirmedWrites.Count > 0
            || current.ConfirmedState?.HyperBoostActive == true
            || current.HyperBoostRestorePending
            || hyperBoostJournalSafetyBlock
            || hyperBoostRestorePending
            || pendingIndeterminateHyperBoostEntryIntent is not null
            || pendingIndeterminateHyperBoostExitIntent is not null)
        {
            return "External, unconfirmed, or HyperBoost state must be fully settled before validation.";
        }
        if (current.ThermalConvergence is not
            {
                Policy: ThermalControlPolicy.MaintainDesiredState,
                State: ThermalConvergenceState.Matched,
                Reason: ThermalConvergenceReason.None,
            })
        {
            return "Maintain convergence must be freshly matched before synthetic drift is admitted.";
        }
        if (expectedLedger is not
            {
                DeviceAuthorityEpoch: var deviceAuthorityEpoch,
                DeviceFingerprint: { } deviceFingerprint,
                InFlight: null,
                Contradiction: ThermalConvergenceContradiction.None,
                EffectiveIntent: not null,
                PerformanceDesiredRevision: { } performanceRevision,
                PolicyRevision: { } policyRevision,
            })
        {
            return "The durable convergence ledger is not in a settled authoritative state.";
        }
        if (deviceAuthorityEpoch == Guid.Empty
            || string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            return "The durable convergence ledger is not bound to a confirmed device authority epoch.";
        }
        bool performanceClaimSpent = expectedLedger.Tombstones.Any(tombstone =>
                tombstone.Destination == PowerSource.PluggedIn
                && tombstone.Contradiction.HasFlag(
                    ThermalConvergenceContradiction.Performance)
                && string.Equals(
                    tombstone.PerformanceDesiredRevision,
                    performanceRevision,
                    StringComparison.Ordinal)
                && string.Equals(
                    tombstone.PolicyRevision,
                    policyRevision,
                    StringComparison.Ordinal))
            || expectedLedger.Budgets.Any(budget =>
                budget.Destination == PowerSource.PluggedIn
                && budget.Component == ThermalComponent.Performance
                && string.Equals(
                    budget.DesiredRevision,
                    performanceRevision,
                    StringComparison.Ordinal)
                && string.Equals(
                    budget.PolicyRevision,
                    policyRevision,
                    StringComparison.Ordinal)
                && budget.ConsumedClaims >= budget.MaximumClaims);
        return performanceClaimSpent
            ? "The current performance convergence claim is already spent; explicitly Reapply before starting a new validation run."
            : null;
    }

    private static bool IsThermalExternalDriftBaseline(
        DesiredDeviceState desired,
        ConfirmedDeviceState confirmed)
    {
        FanMode desiredFanMode = desired.FanControl?.Mode ?? desired.Fans.Value;
        bool gpuIntentSafe = desired.GpuClockTuning is null
            || desired.GpuClockTuning == GpuClockTuningSettings.Disabled;
        bool gpuReadbackSafe = confirmed.GpuClockOffsets is null
            || confirmed.GpuClockOffsets.Current == GpuClockOffsetSettings.Default;
        return desired.Performance.Value is
                PerformancePreset.Balanced or PerformancePreset.Silent
            && desiredFanMode == FanMode.Automatic
            && desired.CustomPerformance is null
            && gpuIntentSafe
            && confirmed.Performance == desired.Performance.Value
            && confirmed.Fans == FanMode.Automatic
            && !confirmed.HyperBoostActive
            && confirmed.CustomPerformance is null
            && gpuReadbackSafe;
    }

    private static bool IsThermalExternalDriftCandidate(
        PerformancePreset candidate,
        ConfirmedDeviceState confirmed) => confirmed.Performance == candidate
            && confirmed.Fans == FanMode.Automatic
            && !confirmed.HyperBoostActive
            && confirmed.CustomPerformance is null
            && (confirmed.GpuClockOffsets is null
                || confirmed.GpuClockOffsets.Current
                    == GpuClockOffsetSettings.Default);

    private async Task<string?> RevalidateThermalExternalDriftLedgerAsync(
        ThermalConvergenceLedgerState? expectedLedger,
        string? expectedLedgerSha256,
        CancellationToken cancellationToken)
    {
        if (!thermalConvergenceLedgerAuthoritative
            || thermalConvergenceStore is null
            || expectedLedger is null)
        {
            return "Durable Maintain authority is unavailable before validation submission.";
        }

        try
        {
            ThermalConvergenceLoadResult load = await thermalConvergenceStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (load.IsAuthoritative
                && StringComparer.Ordinal.Equals(
                    load.Sha256,
                    expectedLedgerSha256))
            {
                return null;
            }

            const string changed =
                "Durable Maintain authority changed before validation submission.";
            CloseThermalConvergenceReservationAuthority(expectedLedger, changed);
            return changed;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ConfigurationQuarantinedException)
        {
            string unavailable =
                $"Durable Maintain authority could not be inspected before validation submission: {exception.Message}";
            CloseThermalConvergenceReservationAuthority(
                expectedLedger,
                unavailable);
            return unavailable;
        }
    }

    private void LatchThermalExternalDriftConfirmation(
        ServiceStatusSnapshot baseline,
        ConfirmedDeviceState preWrite,
        PerformancePreset candidate,
        string message)
    {
        DesiredDeviceState candidateTarget = baseline.DesiredState with
        {
            Performance = baseline.DesiredState.Performance with
            {
                Value = candidate,
            },
            CustomPerformance = null,
        };
        SetPendingWriteConfirmation(
            DeviceSubsystem.Performance,
            new PendingWriteConfirmation(
                PowerSource.PluggedIn,
                candidateTarget,
                preWrite));
        PublishIfChanged(GetStatus() with
        {
            WritesBlocked = true,
            UnconfirmedWrites = pendingWriteConfirmations.SnapshotSubsystems(),
            Message = message,
        });
        history.RecordSummary(ControlOperationStatus.Failed, message);
    }

    private ControlOperationResult ThermalExternalDriftBlocked(string message)
    {
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        return new ControlOperationResult(ControlOperationStatus.Blocked, message);
    }

    private ControlOperationResult ThermalExternalDriftFailed(string message)
    {
        history.RecordSummary(ControlOperationStatus.Failed, message);
        return new ControlOperationResult(ControlOperationStatus.Failed, message);
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

    private ThermalTransitionStepRunner CreateHyperBoostTransitionRunner(
        IBladeDevice device,
        bool enabled,
        PerformancePreset baseMode,
        HyperBoostRestoreIntent expectedIntent,
        HyperBoostOwnershipJournal? expectedJournal,
        string? expectedJournalSha256,
        long expectedOwnershipGeneration,
        long expectedPowerGeneration,
        PowerSource expectedPowerSource,
        bool ownedLifecycleRestore,
        bool allowOwnedJournalCleanup,
        bool validationOnly,
        ScopedThermalAuthorityPurpose authorityPurpose =
            ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
        bool allowIntentRebase = false,
        ThermalTransitionRecoveryState? recoveryState = null,
        Func<bool>? stillPreparing = null)
    {
        Guid attemptId = recoveryState?.AttemptId ?? Guid.NewGuid();
        long expectedPowerAdmissionEpoch =
            Interlocked.Read(ref powerAdmissionEpoch);
        long expectedSessionAdmissionEpoch =
            Interlocked.Read(ref sessionAdmissionEpoch);
        string? expectedActiveUserSid = Volatile.Read(ref activeUserSid);
        string? TransitionWriteBlockReason()
        {
            if (stillPreparing?.Invoke() == false)
            {
                return "The original suspend preparation expired before the thermal command.";
            }
            if (Volatile.Read(ref pendingPowerAdmissions) > 0
                || Volatile.Read(ref pendingSessionAdmissions) > 0)
            {
                return "A power or active-user admission request is queued before the HyperBoost thermal command can be submitted.";
            }

            if (Interlocked.Read(ref powerAdmissionEpoch)
                    != expectedPowerAdmissionEpoch
                || Interlocked.Read(ref sessionAdmissionEpoch)
                    != expectedSessionAdmissionEpoch
                || !string.Equals(
                    Volatile.Read(ref activeUserSid),
                    expectedActiveUserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "HyperBoost power or active-user admission changed before the thermal command was submitted.";
            }

            return HyperBoostTransitionWriteBlockReason(
                expectedIntent,
                expectedOwnershipGeneration,
                expectedPowerGeneration,
                ownedLifecycleRestore,
                allowOwnedJournalCleanup,
                allowIntentRebase,
                authorityPurpose);
        }

        return new ThermalTransitionStepRunner(
            attemptId,
            HyperBoostThermalComponents,
            device,
            (attemptDevice, runner) => new ControlCoordinator(
                attemptDevice,
                attemptDevice.ReadPerformanceFanStateAsync,
                resolveIndeterminatePerformanceWrites: true,
                resolveIndeterminateAutomaticFanWrites: false,
                singleAttemptPerformanceWrites: true,
                singleAttemptFanWrites: true,
                thermalStepRunner: runner,
                performanceWriteBlockReason: TransitionWriteBlockReason,
                fanWriteBlockReason: TransitionWriteBlockReason),
            (_, cancellationToken) => RevalidateHyperBoostTransitionRetryAsync(
                enabled,
                baseMode,
                expectedIntent,
                expectedJournal,
                expectedJournalSha256,
                expectedOwnershipGeneration,
                expectedPowerGeneration,
                expectedPowerSource,
                ownedLifecycleRestore,
                allowOwnedJournalCleanup,
                allowIntentRebase,
                validationOnly,
                TransitionWriteBlockReason,
                cancellationToken),
            recoveryState: recoveryState,
            revalidateSubmission: (purpose, _, cancellationToken) =>
                RevalidateHyperBoostTransitionSubmissionAsync(
                    purpose,
                    enabled,
                    baseMode,
                    expectedIntent,
                    expectedJournal,
                    expectedJournalSha256,
                    expectedOwnershipGeneration,
                    expectedPowerGeneration,
                    expectedPowerSource,
                    ownedLifecycleRestore,
                    allowOwnedJournalCleanup,
                    allowIntentRebase,
                    validationOnly,
                    TransitionWriteBlockReason,
                    cancellationToken));
    }

    private string? HyperBoostTransitionWriteBlockReason(
        HyperBoostRestoreIntent expectedIntent,
        long expectedOwnershipGeneration,
        long expectedPowerGeneration,
        bool ownedLifecycleRestore,
        bool allowOwnedJournalCleanup,
        bool allowIntentRebase,
        ScopedThermalAuthorityPurpose authorityPurpose)
    {
        if (ConfigurationThermalWriteBlockReason(authorityPurpose) is { } configurationBlock)
        {
            return configurationBlock;
        }

        if (hyperBoostOwnershipGeneration != expectedOwnershipGeneration)
        {
            return "HyperBoost ownership changed before the thermal command was submitted.";
        }
        if (powerSourceGeneration != expectedPowerGeneration)
        {
            return "The confirmed power destination changed before the thermal command was submitted.";
        }
        if (lowPowerGate.IsActive && !ownedLifecycleRestore)
        {
            return "The laptop entered a low-power transition before the thermal command was submitted.";
        }
        if (pendingWriteConfirmations.HasPending)
        {
            return "A prior thermal write requires read-only reconciliation before another submission.";
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || current.ControllerDetectionWarning is not null
            || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0
            || current.WritesBlocked
                && !ownedLifecycleRestore
                && !allowOwnedJournalCleanup)
        {
            return "HyperBoost thermal write admission closed before submission.";
        }
        if (!ownedLifecycleRestore
            && !allowIntentRebase
            && CreateHyperBoostRestoreIntent(current.DesiredState) != expectedIntent)
        {
            return "The authoritative thermal intent changed before submission.";
        }
        return null;
    }

    private async Task<ThermalRetryAdmission>
        RevalidateHyperBoostTransitionSubmissionAsync(
        ThermalTransitionStepPurpose purpose,
        bool enabled,
        PerformancePreset baseMode,
        HyperBoostRestoreIntent expectedIntent,
        HyperBoostOwnershipJournal? expectedJournal,
        string? expectedJournalSha256,
        long expectedOwnershipGeneration,
        long expectedPowerGeneration,
        PowerSource expectedPowerSource,
        bool ownedLifecycleRestore,
        bool allowOwnedJournalCleanup,
        bool allowIntentRebase,
        bool validationOnly,
        Func<string?> transitionWriteBlockReason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedJournal is not null)
        {
            HyperBoostOwnershipJournalLoadResult load;
            try
            {
                load = await hyperBoostOwnershipJournalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                hyperBoostJournalSafetyBlock = true;
                return ThermalRetryAdmission.Block(
                    $"The durable HyperBoost transition journal could not be inspected before submission: {exception.Message}");
            }
            if (load.Invalid
                || load.Journal != expectedJournal
                || load.Sha256 != expectedJournalSha256)
            {
                hyperBoostJournalSafetyBlock = true;
                return ThermalRetryAdmission.Block(
                    "The durable HyperBoost transition journal changed before submission.");
            }
        }

        if (transitionWriteBlockReason() is { } initialBlock)
        {
            return ThermalRetryAdmission.Block(initialBlock);
        }

        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(cancellationToken);
            if (powerSource != expectedPowerSource
                || powerSource == PowerSource.ExternalUnknown)
            {
                return ThermalRetryAdmission.Block(
                    "The confirmed power destination changed before HyperBoost transition submission.");
            }

            if (transitionWriteBlockReason() is { } refreshedBlock)
            {
                return ThermalRetryAdmission.Block(refreshedBlock);
            }

            bool hyperBoostCommand = purpose == ThermalTransitionStepPurpose.HyperBoost;
            if (hyperBoostCommand
                && enabled
                && hyperBoostPadDiscoverySource.Observe()
                    != (expectedJournal?.RequiresCoolingPad == true
                        ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent))
            {
                return ThermalRetryAdmission.Block(
                    "The required cooling-pad state could not be confirmed before HyperBoost submission.");
            }

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice admittedDevice)
                || admittedDevice is not ISingleAttemptThermalDevice
                || hyperBoostCommand
                    && validationOnly
                    && admittedDevice is not
                        IPadlessHyperBoostLifecycleValidationDevice)
            {
                return ThermalRetryAdmission.Block(
                    "The exact laptop identity and single-attempt thermal path could not be re-admitted before submission.");
            }

            DeviceCapabilities capabilities = await admittedDevice
                .ReadCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsSupportedHyperBoostRestoreIntent(capabilities, expectedIntent))
            {
                return ThermalRetryAdmission.Block(
                    "The saved HyperBoost thermal intent is no longer admitted for this exact device.");
            }

            if (hyperBoostCommand)
            {
                DeviceFeatureAccess? baseAccess = capabilities.HyperBoostBaseModes
                        ?.AccessFor(baseMode)
                    ?? (baseMode == PerformancePreset.Balanced
                        ? capabilities.HyperBoostAccess
                        : null);
                if (capabilities.HyperBoostAccess
                        != DeviceFeatureAccess.ReadWrite
                    || enabled && baseAccess != DeviceFeatureAccess.ReadWrite
                    || enabled && !validationOnly
                        && !HyperBoostCapabilityPolicy.IsDirectEntryBase(
                                capabilities,
                                expectedJournal?.RequiresCoolingPad == true,
                                baseMode)
                    || enabled && expectedJournal?.RequiresCoolingPad != true
                        && (validationOnly
                            ? capabilities.HyperBoostWithoutCoolingPadAccess
                                is not (DeviceFeatureAccess.ReadWrite
                                    or DeviceFeatureAccess.ObservedOnly)
                            : capabilities.HyperBoostWithoutCoolingPadAccess
                                != DeviceFeatureAccess.ReadWrite))
                {
                    return ThermalRetryAdmission.Block(
                        "HyperBoost command admission changed before submission.");
                }
            }

            if (expectedJournal is not null
                && !await IsExpectedHyperBoostJournalCurrentAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return ThermalRetryAdmission.Block(
                    "The durable HyperBoost transition journal changed before final submission.");
            }

            if (transitionWriteBlockReason() is { } finalBlock)
            {
                return ThermalRetryAdmission.Block(finalBlock);
            }

            return ThermalRetryAdmission.Allow(admittedDevice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ThermalRetryAdmission.Block(
                $"Read-only HyperBoost submission admission failed: {exception.Message}");
        }
    }

    private async Task<ThermalRetryAdmission>
        RevalidateHyperBoostTransitionRetryAsync(
        bool enabled,
        PerformancePreset baseMode,
        HyperBoostRestoreIntent expectedIntent,
        HyperBoostOwnershipJournal? expectedJournal,
        string? expectedJournalSha256,
        long expectedOwnershipGeneration,
        long expectedPowerGeneration,
        PowerSource expectedPowerSource,
        bool ownedLifecycleRestore,
        bool allowOwnedJournalCleanup,
        bool allowIntentRebase,
        bool validationOnly,
        Func<string?> transitionWriteBlockReason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedJournal is not null)
        {
            HyperBoostOwnershipJournalLoadResult load;
            try
            {
                load = await hyperBoostOwnershipJournalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                hyperBoostJournalSafetyBlock = true;
                return ThermalRetryAdmission.Block(
                    $"The durable HyperBoost transition journal could not be inspected before the retry: {exception.Message}");
            }
            if (load.Invalid
                || load.Journal != expectedJournal
                || load.Sha256 != expectedJournalSha256)
            {
                hyperBoostJournalSafetyBlock = true;
                return ThermalRetryAdmission.Block(
                    "The durable HyperBoost transition journal changed before the retry.");
            }
        }

        if (transitionWriteBlockReason() is { } initialBlock)
        {
            return ThermalRetryAdmission.Block(initialBlock);
        }

        await transitionRetryDelay(
                ThermalPreWriteRetryDelay,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(cancellationToken);
            if (powerSource != expectedPowerSource
                || powerSource == PowerSource.ExternalUnknown)
            {
                return ThermalRetryAdmission.Block(
                    "The confirmed power destination changed before the HyperBoost retry.");
            }

            if (transitionWriteBlockReason() is { } refreshedBlock)
            {
                return ThermalRetryAdmission.Block(refreshedBlock);
            }

            if (enabled
                && hyperBoostPadDiscoverySource.Observe()
                    != (expectedJournal?.RequiresCoolingPad == true
                        ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent))
            {
                return ThermalRetryAdmission.Block(
                    "The required cooling-pad state could not be confirmed before the HyperBoost retry.");
            }

            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice retryDevice))
            {
                return ThermalRetryAdmission.Block(
                    "The exact laptop identity could not be re-admitted for the HyperBoost retry.");
            }
            if (retryDevice is not ISingleAttemptThermalDevice
                || validationOnly
                    && retryDevice is not IPadlessHyperBoostLifecycleValidationDevice)
            {
                return ThermalRetryAdmission.Block(
                    "The verified device does not expose single-attempt HyperBoost writes.");
            }

            DeviceCapabilities capabilities = await retryDevice
                .ReadCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            DeviceFeatureAccess? baseAccess = capabilities.HyperBoostBaseModes
                    ?.AccessFor(baseMode)
                ?? (baseMode == PerformancePreset.Balanced
                    ? capabilities.HyperBoostAccess
                    : null);
            if (capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
                || enabled && baseAccess != DeviceFeatureAccess.ReadWrite
                || enabled && !validationOnly
                    && !HyperBoostCapabilityPolicy.IsDirectEntryBase(
                            capabilities,
                            expectedJournal?.RequiresCoolingPad == true,
                            baseMode)
                || !IsSupportedHyperBoostRestoreIntent(
                    capabilities,
                    expectedIntent)
                || enabled && expectedJournal?.RequiresCoolingPad != true
                    && (validationOnly
                        ? capabilities.HyperBoostWithoutCoolingPadAccess
                            is not (DeviceFeatureAccess.ReadWrite
                                or DeviceFeatureAccess.ObservedOnly)
                        : capabilities.HyperBoostWithoutCoolingPadAccess
                            != DeviceFeatureAccess.ReadWrite))
            {
                return ThermalRetryAdmission.Block(
                    "HyperBoost capability admission changed before the retry.");
            }

            if (expectedJournal is not null
                && !await IsExpectedHyperBoostJournalCurrentAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return ThermalRetryAdmission.Block(
                    "The durable HyperBoost transition journal changed before final retry submission.");
            }

            if (transitionWriteBlockReason() is { } finalBlock)
            {
                return ThermalRetryAdmission.Block(finalBlock);
            }

            return ThermalRetryAdmission.Allow(retryDevice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ThermalRetryAdmission.Block(
                $"Read-only HyperBoost retry admission failed: {exception.Message}");
        }
    }

    private static bool IsHyperBoostCommandRetryEligible(
        BladeWriteException exception) =>
        exception.SubmissionState == WriteSubmissionState.KnownNotApplied
        && exception.FailurePhase is
            BladeWriteFailurePhase.ThermalPreWriteRead or
            BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead;

    private async Task ApplyHyperBoostCommandOnceAsync(
        IBladeDevice device,
        bool enabled,
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        ValidatePadlessHyperBoostLifecycleRequest? lifecycleValidation,
        CancellationToken cancellationToken)
    {
        if (enabled && hyperBoostPadDiscoverySource.Observe()
            != (requiresCoolingPad
                ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent))
        {
            throw new CapabilityUnavailableException(
                "The required cooling-pad state could not be confirmed immediately before HyperBoost submission.");
        }

        if (lifecycleValidation is not null)
        {
            if (device is not IPadlessHyperBoostLifecycleValidationDevice validationDevice)
            {
                throw new CapabilityUnavailableException(
                    "The exact validation-only device path is unavailable.");
            }
            await validationDevice.EnablePadlessHyperBoostLifecycleValidationOnceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (device is not ISingleAttemptThermalDevice singleAttemptDevice)
        {
            throw new CapabilityUnavailableException(
                "The verified device does not expose single-attempt HyperBoost writes.");
        }
        await singleAttemptDevice.SetHyperBoostOnceAsync(
                enabled,
                baseMode,
                requiresCoolingPad,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ControlOperationResult> SetHyperBoostCoreAsync(
        bool enabled,
        Guid? transitionId,
        long? expectedOwnershipGeneration,
        ValidatePadlessHyperBoostLifecycleRequest? lifecycleValidation,
        bool restoreUserIntent,
        PowerSource? exitPowerSourceOverride,
        long? expectedExitPowerSourceGeneration,
        CancellationToken token,
        bool operationGateAlreadyHeld = false,
        Action<bool>? writeAttemptObserver = null,
        Func<bool>? stillPreparing = null,
        long? lowPowerResumeGeneration = null,
        PadlessHyperBoostLifecycleTarget? lifecycleTarget = null)
    {
        EnsureInitialized();
        if (lifecycleValidation is not null
            && (lifecycleTarget is null || !lifecycleTarget.Readiness.LifecycleValidationReady))
        {
            return new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The exact-model pad-less lifecycle evidence is not ready.");
        }
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
        bool ownedLifecycleRestore = false;
        ThermalTransitionStepRunner? transitionRunner = null;
        HyperBoostOwnershipJournal? preparedTransitionJournal = null;
        string? preparedTransitionJournalSha256 = null;
        if (!operationGateAlreadyHeld)
        {
            await operationGate.WaitAsync(token).ConfigureAwait(false);
        }
        try
        {
            if (expectedOwnershipGeneration is { } expected
                && (!ownsHyperBoost || hyperBoostOwnershipGeneration != expected))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Stored,
                    "HyperBoost lifecycle restoration was skipped because OpenBlade ownership changed before the write.");
            }
            if (enabled)
            {
                await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                    .ConfigureAwait(false);
            }
            else
            {
                RefreshControllerPresenceCore(token);
            }
            ServiceStatusSnapshot current = GetStatus();
            if (current.ControllerDetectionWarning is not null
                || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0)
            {
                string transition = enabled ? "entry" : "exit";
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    $"HyperBoost {transition} is blocked until competing-controller detection succeeds and no potential thermal controller is active.");
            }
            ownedLifecycleRestore = !enabled
                && expectedOwnershipGeneration is not null
                && ownsHyperBoost
                && current.ConfirmedState?.HyperBoostActive == true
                && !pendingWriteConfirmations.HasPending;
            if (!ConfigurationIsAdmitted && !ownedLifecycleRestore)
            {
                return ConfigurationQuarantineBlocked(DeviceSubsystem.Performance);
            }

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
                    bool retired = true;
                    if (hadOwnership)
                    {
                        retired = (await RetireInactiveHyperBoostJournalIfPresentAsync(
                                "HyperBoost was already off, but its ownership journal could not be retired.")
                            .ConfigureAwait(false)).Retired;
                    }
                    if (hadOwnership)
                    {
                        hyperBoostOwnershipGeneration++;
                    }
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostTransitionId = null;
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
            journalRequiresCoolingPad = enabled
                ? !padAbsentAccessAdmitted
                : ownedHyperBoostRequiresCoolingPad;
            // Where both the pad-present and pad-less paths are admitted, a fresh
            // observation selects and journals the path; ambiguity selects pad-less,
            // whose absence check below then blocks the entry.
            if (enabled
                && lifecycleValidation is null
                && HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(current.Capabilities!) is not null
                && HyperBoostCapabilityPolicy.PadlessEntryBaseMode(current.Capabilities!) is not null)
            {
                journalRequiresCoolingPad = hyperBoostPadDiscoverySource.Observe()
                    == CoolingPadConnectionState.PresentExact;
            }
            if (enabled
                && (confirmed.Fans is null
                    || confirmed.Fans == FanMode.Automatic
                        && confirmed.Performance is null))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost requires a fresh confirmed performance and fan state.");
            }

            if (enabled
                && lifecycleValidation is not null
                && PadlessLifecycleEntryRefusal(lifecycleTarget!, confirmed, current.DesiredState) is { } entryRefusal)
            {
                return new ControlOperationResult(ControlOperationStatus.Blocked, entryRefusal);
            }

            PerformancePreset requestedBaseMode = enabled
                ? lifecycleValidation is not null
                    ? lifecycleTarget!.BaseMode
                    // Enter directly from the confirmed mode where that transition is admitted.
                    : confirmed.Performance is { } currentMode
                        && HyperBoostCapabilityPolicy.IsDirectEntryBase(
                            current.Capabilities!, journalRequiresCoolingPad, currentMode)
                        ? currentMode
                        : HyperBoostCapabilityPolicy.EntryBaseMode(
                                current.Capabilities!,
                                journalRequiresCoolingPad)
                            ?? PerformancePreset.Performance
                : ownedHyperBoostBaseMode ?? PerformancePreset.Balanced;
            pendingBaseMode = requestedBaseMode;
            DeviceFeatureAccess? requestedBaseAccess = current.Capabilities
                ?.HyperBoostBaseModes?.AccessFor(requestedBaseMode)
                ?? (requestedBaseMode == PerformancePreset.Balanced
                    ? current.Capabilities?.HyperBoostAccess
                    : null);
            if (enabled && (requestedBaseAccess != DeviceFeatureAccess.ReadWrite
                || lifecycleValidation is null
                    && !HyperBoostCapabilityPolicy.IsDirectEntryBase(
                        current.Capabilities!,
                        journalRequiresCoolingPad,
                        requestedBaseMode)))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost requires an admitted transition baseline for this laptop.");
            }

            // On a model admitting both pad paths (RZ09-0528), pad-less HyperBoost enters directly
            // from the current Silent, Balanced or Performance mode; it never switches mode first.
            if (enabled
                && lifecycleValidation is null
                && !journalRequiresCoolingPad
                && HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(current.Capabilities!) is not null
                && !IsDirectPadlessEntry(confirmed, current.DesiredState, requestedBaseMode))
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "Pad-less HyperBoost on this laptop starts directly from the current AC mode; refresh the laptop state and try again.");
            }

            transitionIntent = enabled
                ? CreateHyperBoostRestoreIntent(current.DesiredState)
                : !ConfigurationIsAdmitted
                    ? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic
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
                && current.ActivePowerSource != PowerSource.PluggedIn)
            {
                return new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "HyperBoost requires an admitted full-power adapter.");
            }

            if (enabled)
            {
                // Absence is a pre-write eligibility condition, not a continuous
                // thermal dependency of an already-owned pad-less session. A pad
                // connected later can only add cooling, so observation reports the
                // new state without issuing an automatic HyperBoost write.
                CoolingPadConnectionState freshPadState =
                    hyperBoostPadDiscoverySource.Observe();
                if (freshPadState != (journalRequiresCoolingPad
                    ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent))
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Blocked,
                        journalRequiresCoolingPad
                            ? "Connect the supported cooling pad before enabling HyperBoost."
                            : "Pad-less HyperBoost requires a fresh observation confirming that no target cooling-pad device is present. Ambiguous or failed enumeration is blocked.");
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
            long transitionOwnershipGeneration = hyperBoostOwnershipGeneration;
            long transitionPowerGeneration = powerSourceGeneration;
            PowerSource transitionPowerSource = enabled
                ? current.ActivePowerSource
                : exitPowerSourceOverride is { } transitionExitSource
                    && expectedExitPowerSourceGeneration == powerSourceGeneration
                        ? transitionExitSource
                        : current.ActivePowerSource;
            if (enabled || ownedDisableAttempt)
            {
                preparedTransitionJournal = new HyperBoostOwnershipJournal(
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
                    TransitionId = enabled
                        ? transitionId
                        : ownedHyperBoostTransitionId,
                };
                if (enabled)
                {
                    preparedTransitionJournalSha256 =
                        await hyperBoostOwnershipJournalStore.WriteAsync(
                            preparedTransitionJournal,
                            token)
                        .ConfigureAwait(false);
                }
                else if (ownedHyperBoostJournal is not { } expectedOwnedJournal
                    || string.IsNullOrWhiteSpace(ownedHyperBoostJournalSha256))
                {
                    hyperBoostJournalSafetyBlock = true;
                    const string missingIdentity =
                        "The exact owned HyperBoost journal identity is unavailable. No disable write was attempted; control writes remain blocked.";
                    PublishIfChanged(current with
                    {
                        WritesBlocked = true,
                        Message = missingIdentity,
                    });
                    return new ControlOperationResult(
                        ControlOperationStatus.Failed,
                        missingIdentity);
                }
                else
                {
                    HyperBoostOwnershipJournalMutationResult preparation;
                    try
                    {
                        preparation = await hyperBoostOwnershipJournalStore
                            .TryReplaceAsync(
                                expectedOwnedJournal,
                                ownedHyperBoostJournalSha256,
                                preparedTransitionJournal,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        hyperBoostJournalSafetyBlock = true;
                        await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                            .ConfigureAwait(false);
                        string preparationFailure =
                            $"The owned HyperBoost journal could not be prepared for disable: {exception.Message} No hardware write was attempted; control writes remain blocked.";
                        PublishIfChanged(current with
                        {
                            WritesBlocked = true,
                            Message = preparationFailure,
                        });
                        return new ControlOperationResult(
                            ControlOperationStatus.Failed,
                            preparationFailure);
                    }
                    if (!preparation.Applied)
                    {
                        hyperBoostJournalSafetyBlock = true;
                        await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                            .ConfigureAwait(false);
                        const string changedJournal =
                            "The owned HyperBoost journal changed before disable preparation. No hardware write was attempted; control writes remain blocked.";
                        PublishIfChanged(current with
                        {
                            WritesBlocked = true,
                            Message = changedJournal,
                        });
                        return new ControlOperationResult(
                            ControlOperationStatus.Failed,
                            changedJournal);
                    }
                    preparedTransitionJournalSha256 = preparation.Sha256;
                }
                if (preparedTransitionJournalSha256 is null)
                {
                    hyperBoostJournalSafetyBlock = true;
                    const string missingPreparedIdentity =
                        "The HyperBoost ownership journal was prepared but its exact identity could not be retained. No hardware write was attempted; control writes remain blocked.";
                    PublishIfChanged(current with
                    {
                        WritesBlocked = true,
                        Message = missingPreparedIdentity,
                    });
                    return new ControlOperationResult(
                        ControlOperationStatus.Failed,
                        missingPreparedIdentity);
                }
                ownershipJournalPrepared = true;
                hyperBoostRestorePending = !enabled;
                lastReportedRetainedHyperBoostRestoreJournal = null;
            }
            transitionRunner = CreateHyperBoostTransitionRunner(
                device,
                enabled,
                requestedBaseMode,
                transitionIntent
                    ?? ownedHyperBoostRestoreIntent
                    ?? HyperBoostRestoreIntent.BalancedAutomatic,
                preparedTransitionJournal,
                preparedTransitionJournalSha256,
                transitionOwnershipGeneration,
                transitionPowerGeneration,
                transitionPowerSource,
                ownedLifecycleRestore,
                allowOwnedJournalCleanup: preparedTransitionJournal is not null,
                validationOnly: lifecycleValidation is not null,
                authorityPurpose: ownedLifecycleRestore
                    ? ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
                    : ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
                stillPreparing: stillPreparing);
            if (enabled)
            {
                ScopedApplyOutcome automaticFans = await transitionRunner
                    .ApplyStepAsync(
                        DeviceSubsystem.Fans,
                        ThermalTransitionStepPurpose.AutomaticBridge,
                        (stepCoordinator, cancellationToken) => stepCoordinator
                            .ApplyFanControlOutcomeAsync(
                        FanControlSettings.FromLegacy(FanMode.Automatic),
                                cancellationToken),
                        token)
                    .ConfigureAwait(false);
                transitionDevice = transitionRunner.CurrentDevice;
                automaticFans = await ResolveIndeterminateHyperBoostFanBaselineAsync(
                        transitionRunner.CurrentDevice,
                        confirmed.Fans,
                        automaticFans,
                        token)
                    .ConfigureAwait(false);
                RecordConfirmedResumeWrites(DeviceSubsystem.Fans, automaticFans);
                if (!HyperBoostBaselineStepSucceeded(automaticFans))
                {
                    return await FinishFailedHyperBoostBaselineTransitionAsync(
                            current,
                            automaticFans,
                            requestedBaseMode,
                            journalRequiresCoolingPad,
                            transitionRunner.CurrentDevice,
                            transitionIntent!,
                            baselineChanged: false,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                baselineTransitionChanged = automaticFans.Result.Status
                    == ControlResultStatus.Applied;

                ScopedApplyOutcome balancedPerformance = await transitionRunner
                    .ApplyStepAsync(
                        DeviceSubsystem.Performance,
                        requestedBaseMode == PerformancePreset.Balanced
                            ? ThermalTransitionStepPurpose.BalancedBridge
                            : ThermalTransitionStepPurpose.Requested,
                        (stepCoordinator, cancellationToken) => stepCoordinator
                            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                                requestedBaseMode,
                                // A Custom base keeps its entry-time CPU/GPU levels.
                                customPerformance: requestedBaseMode == PerformancePreset.Custom
                                    ? transitionIntent?.CustomPerformance
                                    : null,
                                cancellationToken),
                        token)
                    .ConfigureAwait(false);
                transitionDevice = transitionRunner.CurrentDevice;
                RecordConfirmedResumeWrites(DeviceSubsystem.Performance, balancedPerformance);
                if (!HyperBoostBaselineStepSucceeded(balancedPerformance))
                {
                    return await FinishFailedHyperBoostBaselineTransitionAsync(
                            current,
                            balancedPerformance,
                            requestedBaseMode,
                            journalRequiresCoolingPad,
                            transitionRunner.CurrentDevice,
                            transitionIntent!,
                            baselineTransitionChanged,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                baselineTransitionChanged |= balancedPerformance.Result.Status
                    == ControlResultStatus.Applied;

                baselineTransitionCompleted = true;
            }
            await transitionRunner.ApplyCommandStepAsync(
                    HyperBoostThermalComponents,
                    ThermalTransitionStepPurpose.HyperBoost,
                    async (attemptDevice, cancellationToken) =>
                    {
                        try
                        {
                            if (stillPreparing?.Invoke() == false)
                            {
                                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                                    "The original suspend preparation expired before HyperBoost submission.");
                            }
                            await ApplyHyperBoostCommandOnceAsync(
                                    attemptDevice,
                                    enabled,
                                    requestedBaseMode,
                                    journalRequiresCoolingPad,
                                    lifecycleValidation,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            hyperBoostWriteAttempted = true;
                        }
                        catch (BladeWriteException exception) when (
                            exception.SubmissionState
                                == WriteSubmissionState.KnownNotApplied)
                        {
                            throw;
                        }
                        catch (CapabilityUnavailableException)
                        {
                            throw;
                        }
                        catch
                        {
                            hyperBoostWriteAttempted = true;
                            throw;
                        }
                    },
                    IsHyperBoostCommandRetryEligible,
                    token)
                .ConfigureAwait(false);
            transitionDevice = transitionRunner.CurrentDevice;
            setterCompleted = true;
            ConfirmedDeviceState readback = await ReadHyperBoostThermalStateAsync(
                    transitionRunner.CurrentDevice,
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
                HyperBoostRecoveryBaseline? failedTransitionRecoveryBaseline = null;
                if (!readback.HyperBoostActive
                    && transitionIntent is not null
                    && (enabled && baselineTransitionCompleted
                        || !enabled && restoreUserIntent))
                {
                    failedTransitionRestore = await RestoreHyperBoostIntentAsync(
                            transitionRunner.CurrentDevice,
                            transitionIntent,
                            readback,
                            CancellationToken.None,
                            transitionRunner: transitionRunner)
                        .ConfigureAwait(false);
                    if (failedTransitionRestore.Succeeded)
                    {
                        readback = failedTransitionRestore.Readback ?? readback;
                    }
                    else
                    {
                        FailedHyperBoostRestoreObservation failureObservation =
                            await CaptureFailedHyperBoostRestoreObservationAsync(
                                    transitionRunner.CurrentDevice,
                                    transitionIntent,
                                    failedTransitionRestore,
                                    readback)
                                .ConfigureAwait(false);
                        readback = failureObservation.State;
                        failedTransitionRecoveryBaseline =
                            failureObservation.RecoveryBaseline;
                    }
                }

                bool journalRecovered = true;
                if (ownershipJournalPrepared)
                {
                    if (failedTransitionRestore is { Succeeded: false })
                    {
                        var recoveryJournal = new HyperBoostOwnershipJournal(
                            HyperBoostOwnershipJournalState.RestorePending,
                            requestedBaseMode,
                            journalRequiresCoolingPad,
                            timeProvider.GetUtcNow())
                        {
                            RestoreIntent = transitionIntent!,
                            RecoveryBaseline = failedTransitionRecoveryBaseline,
                            TransitionId = preparedTransitionJournal!.TransitionId,
                        };
                        journalRecovered = !hyperBoostJournalSafetyBlock
                            && (await TryReplaceHyperBoostOwnershipJournalAsync(
                                    preparedTransitionJournal,
                                    preparedTransitionJournalSha256,
                                    recoveryJournal,
                                    "The failed HyperBoost transition restore could not preserve its fresh recovery baseline.")
                                .ConfigureAwait(false)).Applied;
                    }
                    else if (readback.HyperBoostActive)
                    {
                        var ownedJournal = new HyperBoostOwnershipJournal(
                            HyperBoostOwnershipJournalState.OwnedConfirmed,
                            requestedBaseMode,
                            ownedHyperBoostRequiresCoolingPad,
                            timeProvider.GetUtcNow())
                        {
                            RestoreIntent = transitionIntent!,
                            TransitionId = preparedTransitionJournal!.TransitionId,
                        };
                        HyperBoostOwnershipJournalMutationResult ownedReplacement =
                            await TryReplaceHyperBoostOwnershipJournalAsync(
                                    preparedTransitionJournal,
                                    preparedTransitionJournalSha256,
                                    ownedJournal,
                                    "HyperBoost remained active after a mismatched transition, but its ownership journal could not be returned to owned state.")
                                .ConfigureAwait(false);
                        journalRecovered = ownedReplacement.Applied;
                        if (journalRecovered)
                        {
                            ownedHyperBoostJournal = ownedJournal;
                            ownedHyperBoostJournalSha256 =
                                ownedReplacement.Sha256;
                        }
                    }
                    else
                    {
                        journalRecovered =
                            await RetireHyperBoostOwnershipJournalAsync(
                                requestedBaseMode,
                                ownedHyperBoostRequiresCoolingPad,
                                transitionIntent!,
                                "HyperBoost was confirmed inactive after a mismatched transition, but its ownership journal could not be retired.",
                                preparedTransitionJournal!,
                                preparedTransitionJournalSha256!)
                            .ConfigureAwait(false);
                    }
                }
                bool restoreFailed = failedTransitionRestore is { Succeeded: false };
                if (restoreFailed)
                {
                    hyperBoostJournalSafetyBlock = true;
                }

                if (readback.HyperBoostActive && enabled)
                {
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = true;
                    ownedHyperBoostBaseMode = requestedBaseMode;
                    ownedHyperBoostRestoreIntent = transitionIntent;
                    ownedHyperBoostTransitionId =
                        preparedTransitionJournal?.TransitionId;
                    ownedHyperBoostRequiresCoolingPad = journalRequiresCoolingPad;
                }
                else if (!readback.HyperBoostActive)
                {
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostTransitionId = null;
                    ownedHyperBoostJournal = null;
                    ownedHyperBoostJournalSha256 = null;
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
                else if (restoreFailed)
                {
                    mismatchMessage +=
                        " The exact saved intent remains pending for one explicit recovery; control writes remain blocked.";
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
                    WritesBlocked = current.WritesBlocked
                        || hyperBoostJournalSafetyBlock,
                    Message = mismatch.Message,
                });
                return mismatch;
            }

            resumeStateValidation.RecordConfirmedWrite(DeviceSubsystem.Performance);
            resumeStateValidation.RecordConfirmedWrite(DeviceSubsystem.Fans);
            if (!enabled && restoreUserIntent && transitionIntent is not null)
            {
                HyperBoostIntentRestorationResult restoration =
                    await RestoreHyperBoostIntentAsync(
                            transitionRunner.CurrentDevice,
                            transitionIntent,
                            readback,
                            token,
                            confirmIndeterminateManualRestore: true,
                            transitionRunner: transitionRunner)
                        .ConfigureAwait(false);
                readback = restoration.Readback ?? readback;
                if (!restoration.Succeeded)
                {
                    FailedHyperBoostRestoreObservation failureObservation =
                        await CaptureFailedHyperBoostRestoreObservationAsync(
                                transitionRunner.CurrentDevice,
                                transitionIntent,
                                restoration,
                                readback)
                            .ConfigureAwait(false);
                    readback = failureObservation.State;
                    HyperBoostRestoreRetryDisposition retryDisposition =
                        restoration.DeferredRetryEligible
                        && failureObservation.RecoveryBaseline is not null
                        && exitPowerSourceOverride is
                            (PowerSource.Battery or PowerSource.UsbC)
                            ? HyperBoostRestoreRetryDisposition
                                .DeferredPreSubmission
                            : HyperBoostRestoreRetryDisposition.None;
                    var recoveryJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        requestedBaseMode,
                        journalRequiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = transitionIntent,
                        RecoveryBaseline = failureObservation.RecoveryBaseline,
                        RestoreRetryDisposition = retryDisposition,
                        RestoreRetryTarget = retryDisposition
                            == HyperBoostRestoreRetryDisposition
                                .DeferredPreSubmission
                            ? HyperBoostRestoreRetryTarget
                                .CurrentPowerDestination
                            : HyperBoostRestoreRetryTarget.None,
                        TransitionId = preparedTransitionJournal!.TransitionId,
                    };
                    bool recoveryRecorded = !hyperBoostJournalSafetyBlock
                        && (await TryReplaceHyperBoostOwnershipJournalAsync(
                                preparedTransitionJournal,
                                preparedTransitionJournalSha256,
                                recoveryJournal,
                                "The failed HyperBoost exit restore could not preserve its fresh recovery baseline.")
                            .ConfigureAwait(false)).Applied;
                    hyperBoostJournalSafetyBlock = true;
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostTransitionId = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    string message =
                        $"HyperBoost is off, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade will not retry an indeterminate write."
                        + (retryDisposition
                                == HyperBoostRestoreRetryDisposition
                                    .DeferredPreSubmission
                            ? " One deferred low-power destination retry is permitted only after a fresh matching exact-device, power-source, and controller observation."
                            : string.Empty);
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
                var finalizedJournal = new HyperBoostOwnershipJournal(
                    HyperBoostOwnershipJournalState.OwnedConfirmed,
                    requestedBaseMode,
                    journalRequiresCoolingPad,
                    timeProvider.GetUtcNow())
                {
                    RestoreIntent = transitionIntent!,
                    TransitionId = preparedTransitionJournal?.TransitionId,
                };
                HyperBoostOwnershipJournalMutationResult journalFinalization =
                    await TryReplaceHyperBoostOwnershipJournalAsync(
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            finalizedJournal,
                            "HyperBoost was confirmed, but its durable ownership journal could not be finalized.")
                        .ConfigureAwait(false);
                bool journalFinalized = journalFinalization.Applied;
                if (!journalFinalized)
                {
                    return await RestoreAfterHyperBoostJournalFailureAsync(
                            transitionRunner.CurrentDevice,
                            current,
                            readback,
                            requestedBaseMode,
                            requiresCoolingPad: journalRequiresCoolingPad,
                            transitionIntent!,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                    .ConfigureAwait(false);
                }
                ownedHyperBoostJournal = finalizedJournal;
                ownedHyperBoostJournalSha256 = journalFinalization.Sha256;
            }
            bool disableJournalCleared = true;
            if (!enabled && ownedDisableAttempt)
            {
                disableJournalCleared =
                    await RetireHyperBoostOwnershipJournalAsync(
                            requestedBaseMode,
                            journalRequiresCoolingPad,
                            transitionIntent!,
                            "HyperBoost was restored, but its durable ownership journal could not be retired.",
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!)
                        .ConfigureAwait(false);
            }

            hyperBoostOwnershipGeneration++;
            ownsHyperBoost = enabled;
            ownedHyperBoostBaseMode = enabled ? requestedBaseMode : null;
            ownedHyperBoostRestoreIntent = enabled ? transitionIntent : null;
            ownedHyperBoostTransitionId = enabled
                ? preparedTransitionJournal?.TransitionId
                : null;
            if (!enabled)
            {
                ownedHyperBoostJournal = null;
                ownedHyperBoostJournalSha256 = null;
            }
            pendingIndeterminateHyperBoostEntryIntent = null;
            pendingIndeterminateHyperBoostEntryTransitionId = null;
            pendingIndeterminateHyperBoostExitIntent = null;
            pendingIndeterminateHyperBoostJournal = null;
            pendingIndeterminateHyperBoostJournalSha256 = null;
            pendingIndeterminateHyperBoostRecoveryState = null;
            ownedHyperBoostRequiresCoolingPad = enabled && journalRequiresCoolingPad;
            pendingOwnedHyperBoostPowerBoundaryExit = false;
            ownedHyperBoostPowerBoundaryExitSubmitted = false;

            driftTracker.Observe(readback);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);

            string resultMessage = enabled
                ? journalRequiresCoolingPad
                    ? "HyperBoost is active. Keep the supported cooling pad connected."
                    : "HyperBoost is active through the admitted pad-less path; no cooling pad is required for this owned session."
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
                ObservationReason = result.Status == ControlOperationStatus.Applied
                    ? DeviceObservationReason.None
                    : current.ObservationReason,
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
                if (transitionRunner is not null)
                {
                    pendingIndeterminateHyperBoostRecoveryState =
                        transitionRunner.CaptureRecoveryState();
                }
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(
                    writeBaseline,
                    enabled,
                    pendingBaseMode,
                    preparedTransitionJournal!,
                    preparedTransitionJournalSha256!);
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
                    && transitionIntent is not null
                    && transitionRunner is not null)
                {
                    await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode,
                            journalRequiresCoolingPad,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!)
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
                    && transitionIntent is not null
                    && transitionRunner is not null)
                {
                    failedEntryRestoration =
                        await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode,
                            journalRequiresCoolingPad,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!)
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
                if (transitionRunner is not null)
                {
                    pendingIndeterminateHyperBoostRecoveryState =
                        transitionRunner.CaptureRecoveryState();
                }
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(
                    writeBaseline,
                    enabled,
                    pendingBaseMode,
                    preparedTransitionJournal!,
                    preparedTransitionJournalSha256!);
                if (exception.FailurePhase
                    == BladeWriteFailurePhase.ThermalFinalConfirmationRead)
                {
                    history.RecordSummary(
                        ControlOperationStatus.Stored,
                        $"{exception.Message} OpenBlade will attempt to resolve the indeterminate HyperBoost submission through one fresh readback without retrying the write.");
                }
                PublishPendingHyperBoostStatus(exception.Message);

                if (hyperBoostWriteAttempted
                    && !setterCompleted
                    && transitionDevice is not null
                    && transitionRunner is not null
                    && writeBaseline?.ConfirmedState is not null)
                {
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(
                            "HyperBoost was submitted, but its final confirmation was canceled. The indeterminate state remains blocked for a later fresh observation.",
                            exception,
                            token);
                    }

                    ControlOperationResult? resolved =
                        await TryResolveIndeterminateHyperBoostWriteAsync(
                                enabled,
                                transitionDevice,
                                writeBaseline,
                                transitionRunner,
                                preparedTransitionJournal,
                                ownedLifecycleRestore)
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
                    && transitionIntent is not null
                    && transitionRunner is not null)
                {
                    failedEntryRestoration =
                        await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode,
                            journalRequiresCoolingPad,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!)
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
                if (transitionRunner is not null)
                {
                    pendingIndeterminateHyperBoostRecoveryState =
                        transitionRunner.CaptureRecoveryState();
                }
                if (ownedDisableAttempt)
                {
                    pendingIndeterminateHyperBoostExitIntent = transitionIntent
                        ?? ownedHyperBoostRestoreIntent
                        ?? HyperBoostRestoreIntent.BalancedAutomatic;
                }

                LatchPendingHyperBoost(
                    writeBaseline,
                    enabled,
                    pendingBaseMode,
                    preparedTransitionJournal!,
                    preparedTransitionJournalSha256!);
                PublishPendingHyperBoostStatus(
                    "HyperBoost was submitted, but final readback failed. Thermal writes remain blocked until a fresh observation resolves it.");
                if (!finalHyperBoostReadbackCompleted
                    && transitionDevice is not null
                    && transitionRunner is not null
                    && writeBaseline?.ConfirmedState is not null)
                {
                    ControlOperationResult? resolved =
                        await TryResolveIndeterminateHyperBoostWriteAsync(
                                enabled,
                                transitionDevice,
                                writeBaseline,
                                transitionRunner,
                                preparedTransitionJournal,
                                ownedLifecycleRestore)
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
                    if (transitionRunner is not null)
                    {
                        pendingIndeterminateHyperBoostRecoveryState =
                            transitionRunner.CaptureRecoveryState();
                    }
                    LatchPendingHyperBoost(
                        writeBaseline,
                        enabled,
                        pendingBaseMode,
                        preparedTransitionJournal!,
                        preparedTransitionJournalSha256!);
                    PublishPendingHyperBoostStatus(
                        "The HyperBoost entry baseline was established, but the final submission state is unknown. Thermal writes remain blocked until a fresh observation resolves it.");
                }
                else if (enabled
                    && baselineTransitionChanged
                    && transitionDevice is not null
                    && transitionIntent is not null
                    && transitionRunner is not null)
                {
                    await RestoreKnownFailedHyperBoostEntryAsync(
                            transitionDevice,
                            transitionIntent,
                            pendingBaseMode,
                            journalRequiresCoolingPad,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!,
                            transitionRunner)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RestorePreparedHyperBoostJournalAsync(
                            enabled,
                            pendingBaseMode,
                            preparedTransitionJournal!,
                            preparedTransitionJournalSha256!)
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
            if (hyperBoostWriteAttempted)
            {
                RebaseLowPowerResumeState(lowPowerResumeGeneration);
            }
            if (!operationGateAlreadyHeld)
            {
                operationGate.Release();
            }
        }
    }

    private sealed record HyperBoostIntentRestorationResult(
        bool Succeeded,
        ConfirmedDeviceState? Readback,
        string Message,
        bool DeferredRetryEligible = false);

    private sealed record FailedHyperBoostRestoreObservation(
        ConfirmedDeviceState State,
        HyperBoostRecoveryBaseline? RecoveryBaseline);

    private sealed record InactiveHyperBoostJournalRetirement(
        bool Retired,
        ConfirmedDeviceState? ConfirmedState = null,
        string? Message = null);

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

    // A Performance-based lifecycle target (RZ09-0528) captured only Performance/Automatic -> mode 7 -> back.
    // Lifecycle validation must never compose an uncaptured bridge, GPU-tuned or custom entry for it.
    internal static string? PadlessLifecycleEntryRefusal(
        PadlessHyperBoostLifecycleTarget target,
        ConfirmedDeviceState confirmed,
        DesiredDeviceState desired) =>
        target.BaseMode != PerformancePreset.Performance
            || IsCapturedPerformancePadlessEntry(confirmed, desired)
            ? null
            : "Exact-model lifecycle validation requires confirmed and desired Performance with Automatic fans and no GPU tuning.";

    // The Performance-based pad-less evidence (RZ09-0528) covers only a direct entry from
    // confirmed and desired Performance/Automatic, with no custom performance, no GPU tuning
    // intent and no confirmed GPU offsets. Production and lifecycle entries share this rule.
    // The developer lifecycle evidence was recorded from Performance/Automatic without GPU tuning.
    internal static bool IsCapturedPerformancePadlessEntry(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState desired) =>
        confirmed.Fans == FanMode.Automatic
        && desired.Fans.Value == FanMode.Automatic
        && IsDirectPadlessEntry(confirmed, desired, PerformancePreset.Performance)
        && (confirmed.GpuClockOffsets?.Current ?? GpuClockOffsetSettings.Default)
            == GpuClockOffsetSettings.Default
        && desired.GpuClockTuning?.Enabled != true;

    // Direct pad-less entry: the confirmed and selected mode is the base; from Custom, the
    // confirmed CPU/GPU levels must also match the selected ones so exit restores them exactly.
    internal static bool IsDirectPadlessEntry(
        ConfirmedDeviceState confirmed,
        DesiredDeviceState desired,
        PerformancePreset baseMode) =>
        confirmed.Performance == baseMode
        && desired.Performance.Value == baseMode
        && (baseMode == PerformancePreset.Custom
            ? desired.CustomPerformance is not null
                && confirmed.CustomPerformance == desired.CustomPerformance
            : desired.CustomPerformance is null);

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

    private static bool ConfirmedStateMatchesRecoveredIntent(
        ConfirmedDeviceState confirmed,
        HyperBoostRestoreIntent intent) =>
        confirmed.Performance == intent.Performance
        && confirmed.Fans == intent.FanControl.Mode
        && (intent.Performance != PerformancePreset.Custom
            || confirmed.CustomPerformance == intent.CustomPerformance)
        && (intent.GpuClockTuning?.Enabled != true
            || confirmed.GpuClockOffsets?.Current == intent.GpuClockTuning.EffectiveOffsets);

    private void RestoreRecoveredHyperBoostIntentInAutomation(
        HyperBoostRestoreIntent intent)
        => Automation.RestoreHyperBoostIntentUntilPowerSourceChange(intent);

    private static bool IsSupportedFullPowerHyperBoostIntent(
        DeviceCapabilities capabilities,
        HyperBoostRestoreIntent intent) =>
        intent.Performance is (PerformancePreset.Silent
            or PerformancePreset.Balanced
            or PerformancePreset.Performance
            or PerformancePreset.Custom)
        && IsSupportedHyperBoostRestoreIntent(capabilities, intent);

    private static bool IsSupportedHyperBoostRestoreIntent(
        DeviceCapabilities capabilities,
        HyperBoostRestoreIntent intent)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.IsWellFormed
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
        HyperBoostOwnershipJournal journal) =>
        !confirmed.HyperBoostActive
        && confirmed.Performance == journal.BaseMode
        && confirmed.Fans == FanMode.Automatic
        && confirmed.CustomPerformance is null
        && (journal.RestoreIntent.GpuClockTuning is { } tuning
            ? confirmed.GpuClockOffsets?.Current == tuning.EffectiveOffsets
            : confirmed.GpuClockOffsets is null);

    private static bool ConfirmedStateMatchesHyperBoostRecoveryBaseline(
        ConfirmedDeviceState confirmed,
        HyperBoostRecoveryBaseline baseline) =>
        confirmed.Performance == baseline.Performance
        && confirmed.Fans == baseline.Fans
        && confirmed.HyperBoostActive == baseline.HyperBoostActive
        && confirmed.CustomPerformance == baseline.CustomPerformance
        && confirmed.GpuClockOffsets?.Current == baseline.GpuClockOffsets;

    private static HyperBoostRecoveryBaseline?
        CreateExactHyperBoostRecoveryBaseline(
            ConfirmedDeviceState confirmed,
            HyperBoostOwnershipJournal journal)
    {
        bool exactCustomTransitionBaseline =
            journal.BaseMode == PerformancePreset.Custom
            && journal.RestoreIntent.Performance == PerformancePreset.Custom
            && confirmed.Performance == PerformancePreset.Custom
            && confirmed.Fans == FanMode.Automatic
            && CustomPerformanceSettings.IsWellFormed(
                confirmed.CustomPerformance);
        bool exactManualTransitionBaseline =
            confirmed.Performance is null
            && confirmed.Fans is { } fans
            && DeviceSubsystemOrder.IsManualFanMode(fans);
        if (confirmed.HyperBoostActive
            || (!exactCustomTransitionBaseline
                && !exactManualTransitionBaseline))
        {
            return null;
        }

        var baseline = new HyperBoostRecoveryBaseline(
            confirmed.Performance,
            confirmed.Fans!.Value,
            confirmed.HyperBoostActive,
            confirmed.CustomPerformance,
            confirmed.GpuClockOffsets?.Current);
        return baseline.IsWellFormed ? baseline : null;
    }

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

    private Task<ScopedApplyOutcome> ResolveIndeterminateHyperBoostManualFanRestorationAsync(
        IBladeDevice device,
        FanControlSettings previous,
        FanControlSettings requested,
        ScopedApplyOutcome outcome,
        CancellationToken cancellationToken) =>
        HyperBoostManualFanRestorationReconciler.ReconcileAsync(
            device.ReadPerformanceFanStateAsync,
            previous,
            requested,
            outcome,
            transitionRetryDelay,
            cancellationToken);

    private async Task<ControlOperationResult>
        FinishFailedHyperBoostBaselineTransitionAsync(
            ServiceStatusSnapshot baseline,
            ScopedApplyOutcome outcome,
            PerformancePreset baseMode,
            bool requiresCoolingPad,
            IBladeDevice device,
            HyperBoostRestoreIntent intent,
            bool baselineChanged,
            HyperBoostOwnershipJournal expectedJournal,
            string expectedJournalSha256,
            ThermalTransitionStepRunner transitionRunner)
    {
        ControlOperationStatus status;
        string message;
        if (outcome.WriteAttempted)
        {
            pendingIndeterminateHyperBoostRecoveryState =
                transitionRunner.CaptureRecoveryState();
            LatchPendingHyperBoost(
                baseline,
                enabled: true,
                baseMode: baseMode,
                expectedJournal,
                expectedJournalSha256);
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
                        baseMode,
                        requiresCoolingPad,
                        expectedJournal,
                        expectedJournalSha256,
                        transitionRunner)
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
                        baseMode: baseMode,
                        expectedJournal,
                        expectedJournalSha256)
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
            CancellationToken cancellationToken,
            ThermalTransitionStepRunner transitionRunner,
            bool confirmIndeterminateManualRestore = false)
    {
        ControlCoordinator coordinator = transitionRunner.CreateRecipeCoordinator();
        async Task<ScopedApplyOutcome> ApplyStepAsync(
            DeviceSubsystem component,
            ThermalTransitionStepPurpose purpose,
            Func<ControlCoordinator, CancellationToken, Task<ScopedApplyOutcome>> apply)
        {
            ScopedApplyOutcome outcome = await transitionRunner.ApplyStepAsync(
                component,
                purpose,
                apply,
                cancellationToken).ConfigureAwait(false);
            RecordConfirmedResumeWrites(component, outcome);
            return outcome;
        }
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
            if (confirmIndeterminateManualRestore)
            {
                // This evidence is valid only inside the direct exit that
                // began from the confirmed Automatic HyperBoost baseline.
                // Restart recovery cannot distinguish exact manual targets.
                fans = await ResolveIndeterminateHyperBoostManualFanRestorationAsync(
                        transitionRunner.CurrentDevice,
                        FanControlSettings.FromLegacy(FanMode.Automatic),
                        intent.FanControl,
                        fans,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            RecordConfirmedResumeWrites(DeviceSubsystem.Fans, fans);
            UpdateOwnedFanControlSettings(
                intent.FanControl,
                fans.Result,
                fans.WriteAttempted,
                fans.ReadbackMatched,
                writeSubmissionState: fans.WriteSubmissionState,
                confirmationUnavailable: fans.ConfirmationUnavailable);
            return new HyperBoostIntentRestorationResult(
                HyperBoostBaselineStepSucceeded(fans),
                MergeHyperBoostThermalState(preservedState, fans.Readback),
                fans.Result.Message,
                IsDeferredHyperBoostRestoreRetryEligible(fans));
        }

        if (preservedState?.Fans is { } preservedFans
            && DeviceSubsystemOrder.IsManualFanMode(preservedFans))
        {
            ScopedApplyOutcome automaticBaseline = await ApplyStepAsync(
                    DeviceSubsystem.Fans,
                    ThermalTransitionStepPurpose.AutomaticBridge,
                    (stepCoordinator, stepToken) => stepCoordinator
                        .ApplyFanControlOutcomeAsync(
                            FanControlSettings.FromLegacy(FanMode.Automatic),
                            stepToken))
                .ConfigureAwait(false);
            UpdateOwnedFanControlSettings(
                intent.FanControl,
                automaticBaseline.Result,
                automaticBaseline.WriteAttempted,
                automaticBaseline.ReadbackMatched,
                writeSubmissionState: automaticBaseline.WriteSubmissionState,
                confirmationUnavailable: automaticBaseline.ConfirmationUnavailable);
            ConfirmedDeviceState? automaticState =
                MergeHyperBoostThermalState(
                    preservedState,
                    automaticBaseline.Readback);
            if (!HyperBoostBaselineStepSucceeded(automaticBaseline))
            {
                return new HyperBoostIntentRestorationResult(
                    Succeeded: false,
                    automaticState,
                    automaticBaseline.Result.Message,
                    IsDeferredHyperBoostRestoreRetryEligible(automaticBaseline));
            }

            ScopedApplyOutcome restoredPerformance = await ApplyStepAsync(
                    DeviceSubsystem.Performance,
                    ThermalTransitionStepPurpose.Restoration,
                    (stepCoordinator, stepToken) => stepCoordinator
                        .ApplyPerformanceOutcomeAsync(
                            intent.Performance,
                            intent.CustomPerformance,
                            intent.GpuClockTuning,
                            stepToken))
                .ConfigureAwait(false);
            return new HyperBoostIntentRestorationResult(
                HyperBoostBaselineStepSucceeded(restoredPerformance),
                MergeHyperBoostThermalState(
                    automaticState,
                    restoredPerformance.Readback),
                restoredPerformance.Result.Message,
                IsDeferredHyperBoostRestoreRetryEligible(restoredPerformance));
        }

        ScopedApplyOutcome performance = await ApplyStepAsync(
                DeviceSubsystem.Performance,
                ThermalTransitionStepPurpose.Restoration,
                (stepCoordinator, stepToken) => stepCoordinator
                    .ApplyPerformanceOutcomeAsync(
                        intent.Performance,
                        intent.CustomPerformance,
                        intent.GpuClockTuning,
                        stepToken))
            .ConfigureAwait(false);
        if (!HyperBoostBaselineStepSucceeded(performance))
        {
            return new HyperBoostIntentRestorationResult(
                Succeeded: false,
                MergeHyperBoostThermalState(preservedState, performance.Readback),
                performance.Result.Message,
                IsDeferredHyperBoostRestoreRetryEligible(performance));
        }

        ScopedApplyOutcome automaticFans = await ApplyStepAsync(
                DeviceSubsystem.Fans,
                ThermalTransitionStepPurpose.Restoration,
                (stepCoordinator, stepToken) => stepCoordinator
                    .ApplyFanControlOutcomeAsync(intent.FanControl, stepToken))
            .ConfigureAwait(false);
        UpdateOwnedFanControlSettings(
            intent.FanControl,
            automaticFans.Result,
            automaticFans.WriteAttempted,
            automaticFans.ReadbackMatched,
            writeSubmissionState: automaticFans.WriteSubmissionState,
            confirmationUnavailable: automaticFans.ConfirmationUnavailable);
        return new HyperBoostIntentRestorationResult(
            HyperBoostBaselineStepSucceeded(automaticFans),
            MergeHyperBoostThermalState(
                preservedState,
                automaticFans.Readback ?? performance.Readback),
            automaticFans.Result.Message,
            IsDeferredHyperBoostRestoreRetryEligible(automaticFans));
    }

    private async Task<FailedHyperBoostRestoreObservation>
        CaptureFailedHyperBoostRestoreObservationAsync(
            IBladeDevice device,
            HyperBoostRestoreIntent intent,
            HyperBoostIntentRestorationResult restoration,
            ConfirmedDeviceState preservedState)
    {
        ConfirmedDeviceState? confirmedAfterFailure = null;
        try
        {
            confirmedAfterFailure = await ReadHyperBoostThermalStateAsync(
                    device,
                    restoration.Readback ?? preservedState,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the last confirmed state but do not authorize recovery
            // from a baseline that was not freshly observed after the failed
            // restoration write.
        }

        ConfirmedDeviceState state = confirmedAfterFailure
            ?? restoration.Readback
            ?? preservedState;
        HyperBoostRecoveryBaseline? recoveryBaseline =
            confirmedAfterFailure is not null
                ? new HyperBoostRecoveryBaseline(
                    confirmedAfterFailure.Performance,
                    confirmedAfterFailure.Fans ?? FanMode.Automatic,
                    confirmedAfterFailure.HyperBoostActive,
                    confirmedAfterFailure.CustomPerformance,
                    confirmedAfterFailure.GpuClockOffsets?.Current)
                : null;
        if (recoveryBaseline is not { IsWellFormed: true })
        {
            recoveryBaseline = null;
        }

        return new FailedHyperBoostRestoreObservation(
            state,
            recoveryBaseline);
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
        ServiceStatusSnapshot baseline,
        ThermalTransitionStepRunner transitionRunner,
        HyperBoostOwnershipJournal? expectedJournal,
        bool ownedLifecycleRestore)
    {
        device = transitionRunner.CurrentDevice;
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
        if (readback.HyperBoostActive == false)
        {
            ThermalTransitionStepRunner? rebasedRunner =
                await CreateImmediateHyperBoostReconciliationRunnerAsync(
                        enabled,
                        device,
                        powerSource,
                        readback,
                        transitionRunner,
                        expectedJournal,
                        ownedLifecycleRestore,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (rebasedRunner is null)
            {
                return null;
            }
            transitionRunner = rebasedRunner;
            device = transitionRunner.CurrentDevice;
        }
        IndeterminateHyperBoostReconciliation reconciliation = enabled
            ? await ReconcileIndeterminateHyperBoostEntryAsync(
                    readback,
                    powerSource,
                    device,
                    CancellationToken.None,
                    transitionRunner)
                .ConfigureAwait(false)
            : await ReconcileIndeterminateHyperBoostExitAsync(
                    readback,
                    powerSource,
                    device,
                    CancellationToken.None,
                    transitionRunner)
                .ConfigureAwait(false);
        if (reconciliation.Resolution == IndeterminateHyperBoostResolution.None
            || reconciliation.Message is null)
        {
            return null;
        }
        pendingIndeterminateHyperBoostRecoveryState = null;

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
            BatteryPolicyOverride = Automation.BatteryPolicyOverride,
            BatteryProtectionOverride = Automation.BatteryProtectionOverride,
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

    private async Task<ThermalTransitionStepRunner?>
        CreateImmediateHyperBoostReconciliationRunnerAsync(
        bool enabled,
        IBladeDevice device,
        PowerSource powerSource,
        ConfirmedDeviceState confirmed,
        ThermalTransitionStepRunner previousRunner,
        HyperBoostOwnershipJournal? expectedJournal,
        bool ownedLifecycleRestore,
        CancellationToken cancellationToken)
    {
        HyperBoostRestoreIntent? savedIntent = enabled
            ? pendingIndeterminateHyperBoostEntryIntent
            : pendingIndeterminateHyperBoostExitIntent;
        if (savedIntent is null)
        {
            return null;
        }

        HyperBoostOwnershipJournalLoadResult load;
        try
        {
            load = await hyperBoostOwnershipJournalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        HyperBoostOwnershipJournalState expectedState = enabled
            ? HyperBoostOwnershipJournalState.EnablePending
            : HyperBoostOwnershipJournalState.RestorePending;
        if (load.Invalid
            || expectedJournal is null
            || pendingIndeterminateHyperBoostJournal != expectedJournal
            || string.IsNullOrWhiteSpace(
                pendingIndeterminateHyperBoostJournalSha256)
            || load.Journal != expectedJournal
            || load.Sha256 != pendingIndeterminateHyperBoostJournalSha256
            || expectedJournal.State != expectedState
            || expectedJournal.RestoreIntent != savedIntent)
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        ThermalTransitionRecoveryState previousRecoveryState =
            previousRunner.CaptureRecoveryState();
        ThermalTransitionRecoveryState recoveryState =
            previousRecoveryState with
            {
                Consumed = true,
                BlockReason = previousRecoveryState.BlockReason
                    ?? "A possible HyperBoost submission was resolved by immediate readback; compensation remains single-attempt.",
            };
        HyperBoostRestoreIntent destinationIntent =
            ResolveIndeterminateHyperBoostRestoreIntent(
                powerSource,
                savedIntent,
                confirmed);
        if (!ConfigurationIsAdmitted
            && (enabled
                || !ownedLifecycleRestore
                || destinationIntent != savedIntent))
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        return CreateHyperBoostTransitionRunner(
            device,
            enabled,
            baseMode: enabled
                ? pendingIndeterminateHyperBoostEntryBaseMode
                : ownedHyperBoostBaseMode ?? PerformancePreset.Balanced,
            expectedIntent: destinationIntent,
            expectedJournal: expectedJournal,
            expectedJournalSha256:
                pendingIndeterminateHyperBoostJournalSha256,
            expectedOwnershipGeneration: hyperBoostOwnershipGeneration,
            expectedPowerGeneration: powerSourceGeneration,
            expectedPowerSource: powerSource,
            ownedLifecycleRestore: ownedLifecycleRestore,
            allowOwnedJournalCleanup: true,
            validationOnly: false,
            authorityPurpose: !ConfigurationIsAdmitted
                ? ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
                : ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
            allowIntentRebase: true,
            recoveryState: recoveryState);
    }

    private HyperBoostRestoreIntent ResolveIndeterminateHyperBoostRestoreIntent(
        PowerSource powerSource,
        HyperBoostRestoreIntent savedIntent,
        ConfirmedDeviceState confirmed) => !ConfigurationIsAdmitted
            ? savedIntent
            : powerSource switch
            {
                PowerSource.PluggedIn => savedIntent,
                PowerSource.Battery or PowerSource.UsbC =>
                    CreateHyperBoostRestoreIntent(Automation.Resolve(confirmed)),
                _ => HyperBoostRestoreIntent.BalancedAutomatic,
            };

    private async Task<ThermalTransitionStepRunner?>
        CreateDelayedHyperBoostReconciliationRunnerAsync(
        IBladeDevice device,
        PowerSource powerSource,
        CancellationToken cancellationToken)
    {
        HyperBoostRestoreIntent? expectedIntent =
            pendingIndeterminateHyperBoostEntryIntent
            ?? pendingIndeterminateHyperBoostExitIntent;
        if (expectedIntent is null)
        {
            return null;
        }

        HyperBoostOwnershipJournalLoadResult load;
        try
        {
            load = await hyperBoostOwnershipJournalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        HyperBoostOwnershipJournalState expectedState =
            pendingIndeterminateHyperBoostEntryIntent is not null
                ? HyperBoostOwnershipJournalState.EnablePending
                : HyperBoostOwnershipJournalState.RestorePending;
        if (load.Invalid
            || pendingIndeterminateHyperBoostJournal is not { } expectedJournal
            || string.IsNullOrWhiteSpace(
                pendingIndeterminateHyperBoostJournalSha256)
            || load.Journal != expectedJournal
            || load.Sha256 != pendingIndeterminateHyperBoostJournalSha256
            || expectedJournal.State != expectedState
            || expectedJournal.RestoreIntent != expectedIntent)
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        ThermalTransitionRecoveryState recoveryState =
            pendingIndeterminateHyperBoostRecoveryState
            ?? new ThermalTransitionRecoveryState(
                Guid.NewGuid(),
                Consumed: true,
                "A delayed HyperBoost reconciliation cannot create a new transition retry lease.");
        recoveryState = recoveryState with
        {
            Consumed = true,
            BlockReason = recoveryState.BlockReason
                ?? "A possible HyperBoost submission was resolved by delayed readback; compensation remains single-attempt.",
        };

        bool delayedEntry = pendingIndeterminateHyperBoostEntryIntent is not null;
        if (!ConfigurationIsAdmitted && delayedEntry)
        {
            hyperBoostJournalSafetyBlock = true;
            return null;
        }

        return CreateHyperBoostTransitionRunner(
            device,
            enabled: delayedEntry,
            baseMode: delayedEntry
                ? pendingIndeterminateHyperBoostEntryBaseMode
                : ownedHyperBoostBaseMode ?? PerformancePreset.Balanced,
            expectedIntent: expectedIntent,
            expectedJournal: expectedJournal,
            expectedJournalSha256:
                pendingIndeterminateHyperBoostJournalSha256,
            expectedOwnershipGeneration: hyperBoostOwnershipGeneration,
            expectedPowerGeneration: powerSourceGeneration,
            expectedPowerSource: powerSource,
            ownedLifecycleRestore: true,
            allowOwnedJournalCleanup: true,
            validationOnly: false,
            authorityPurpose: !ConfigurationIsAdmitted
                ? ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
                : ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
            recoveryState: recoveryState);
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

    private static bool IsDeferredHyperBoostRestoreRetryEligible(
        ScopedApplyOutcome outcome) =>
        !HyperBoostBaselineStepSucceeded(outcome)
        && !outcome.WriteAttempted
        && outcome.DeviceAccessFailed
        && outcome.WriteSubmissionState == WriteSubmissionState.KnownNotApplied
        && outcome.WriteFailurePhase == BladeWriteFailurePhase.ThermalPreWriteRead;

    private async Task<IndeterminateHyperBoostReconciliation>
        ReconcileIndeterminateHyperBoostExitAsync(
            ConfirmedDeviceState confirmed,
            PowerSource powerSource,
            IBladeDevice device,
            CancellationToken cancellationToken,
            ThermalTransitionStepRunner? transitionRunner = null)
    {
        if (pendingIndeterminateHyperBoostExitIntent is not { } savedIntent)
        {
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                Message: null,
                IndeterminateHyperBoostResolution.None);
        }
        if (pendingIndeterminateHyperBoostJournal is not { } expectedJournal
            || string.IsNullOrWhiteSpace(
                pendingIndeterminateHyperBoostJournalSha256))
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostExitIntent = null;
            const string missingIdentity =
                "The indeterminate HyperBoost disable journal identity is unavailable. OpenBlade will not reconcile or retry the hardware write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, missingIdentity);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                missingIdentity,
                IndeterminateHyperBoostResolution.Unsafe);
        }
        string expectedJournalSha256 =
            pendingIndeterminateHyperBoostJournalSha256;

        if (confirmed.HyperBoostActive)
        {
            var ownedJournal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.OwnedConfirmed,
                ownedHyperBoostBaseMode ?? PerformancePreset.Balanced,
                ownedHyperBoostRequiresCoolingPad,
                timeProvider.GetUtcNow())
            {
                RestoreIntent = savedIntent,
                TransitionId = expectedJournal.TransitionId,
            };
            HyperBoostOwnershipJournalMutationResult recovery =
                await TryReplaceHyperBoostOwnershipJournalAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        ownedJournal,
                        "Fresh readback confirmed that an indeterminate HyperBoost disable did not apply, but its ownership journal could not be returned to owned state.")
                    .ConfigureAwait(false);
            bool journalRecovered = recovery.Applied;
            if (journalRecovered)
            {
                ownedHyperBoostJournal = ownedJournal;
                ownedHyperBoostJournalSha256 = recovery.Sha256;
            }
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
            pendingIndeterminateHyperBoostJournal = null;
            pendingIndeterminateHyperBoostJournalSha256 = null;
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
            || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0)
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

        HyperBoostRestoreIntent restoreIntent =
            ResolveIndeterminateHyperBoostRestoreIntent(
                powerSource,
                savedIntent,
                confirmed);
        PerformancePreset recoveryBaseMode = ownedHyperBoostBaseMode
            ?? PerformancePreset.Balanced;
        bool recoveryRequiresCoolingPad = ownedHyperBoostRequiresCoolingPad;
        if (transitionRunner is null)
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostExitIntent = null;
            ClearOwnedHyperBoostState();
            const string missingRunner =
                "Fresh readback confirmed that HyperBoost is off after an indeterminate disable, but no exact transition runner is available. Control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, missingRunner);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                missingRunner,
                IndeterminateHyperBoostResolution.Unsafe);
        }
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    restoreIntent,
                    confirmed,
                    cancellationToken,
                    transitionRunner: transitionRunner)
                .ConfigureAwait(false);
        device = transitionRunner.CurrentDevice;
        pendingIndeterminateHyperBoostExitIntent = null;
        ConfirmedDeviceState restored = restoration.Readback
            ?? confirmed;
        ClearOwnedHyperBoostState();
        if (!restoration.Succeeded)
        {
            FailedHyperBoostRestoreObservation failureObservation =
                await CaptureFailedHyperBoostRestoreObservationAsync(
                        device,
                        restoreIntent,
                        restoration,
                        restored)
                    .ConfigureAwait(false);
            restored = failureObservation.State;
            HyperBoostRestoreRetryDisposition retryDisposition =
                restoration.DeferredRetryEligible
                && failureObservation.RecoveryBaseline is not null
                    ? HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    : HyperBoostRestoreRetryDisposition.None;
            var recoveryJournal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.RestorePending,
                recoveryBaseMode,
                recoveryRequiresCoolingPad,
                timeProvider.GetUtcNow())
            {
                RestoreIntent = restoreIntent,
                RecoveryBaseline = failureObservation.RecoveryBaseline,
                RestoreRetryDisposition = retryDisposition,
                RestoreRetryTarget = retryDisposition
                    == HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    ? powerSource == PowerSource.PluggedIn
                        ? HyperBoostRestoreRetryTarget.ExactJournalIntent
                        : HyperBoostRestoreRetryTarget.CurrentPowerDestination
                    : HyperBoostRestoreRetryTarget.None,
                TransitionId = expectedJournal.TransitionId,
            };
            bool recoveryRecorded = !hyperBoostJournalSafetyBlock
                && (await TryReplaceHyperBoostOwnershipJournalAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        recoveryJournal,
                        "The indeterminate HyperBoost exit restore could not preserve its fresh recovery baseline.")
                    .ConfigureAwait(false)).Applied;
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostJournal = null;
            pendingIndeterminateHyperBoostJournalSha256 = null;
            string failedMessage =
                $"Fresh readback confirmed that HyperBoost is off after an indeterminate disable, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade did not retry the HyperBoost write; control writes remain blocked."
                + (retryDisposition
                        == HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    ? " One deferred destination retry is permitted only after a fresh matching exact-device, power-source, and controller observation."
                    : string.Empty);
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
                expectedJournal,
                expectedJournalSha256,
                "HyperBoost was confirmed off and the prior intent was restored after an indeterminate disable, but its ownership journal could not be cleared.")
            .ConfigureAwait(false);
        pendingIndeterminateHyperBoostJournal = null;
        pendingIndeterminateHyperBoostJournalSha256 = null;
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
            CancellationToken cancellationToken,
            ThermalTransitionStepRunner? transitionRunner = null)
    {
        if (pendingIndeterminateHyperBoostEntryIntent is not { } savedIntent)
        {
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: false,
                Message: null,
                IndeterminateHyperBoostResolution.None);
        }
        if (pendingIndeterminateHyperBoostJournal is not { } expectedJournal
            || string.IsNullOrWhiteSpace(
                pendingIndeterminateHyperBoostJournalSha256))
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostEntryIntent = null;
            pendingIndeterminateHyperBoostEntryTransitionId = null;
            const string missingIdentity =
                "The indeterminate HyperBoost enable journal identity is unavailable. OpenBlade will not reconcile or retry the hardware write; control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, missingIdentity);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                missingIdentity,
                IndeterminateHyperBoostResolution.Unsafe);
        }
        string expectedJournalSha256 =
            pendingIndeterminateHyperBoostJournalSha256;

        PerformancePreset baseMode = pendingIndeterminateHyperBoostEntryBaseMode;
        if (confirmed.HyperBoostActive)
        {
            var ownedJournal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.OwnedConfirmed,
                baseMode,
                RequiresCoolingPad: expectedJournal.RequiresCoolingPad,
                timeProvider.GetUtcNow())
            {
                RestoreIntent = savedIntent,
                TransitionId = expectedJournal.TransitionId,
            };
            HyperBoostOwnershipJournalMutationResult finalization =
                await TryReplaceHyperBoostOwnershipJournalAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        ownedJournal,
                        "Fresh readback confirmed active HyperBoost after an indeterminate enable, but its ownership journal could not be finalized.")
                    .ConfigureAwait(false);
            bool journalFinalized = finalization.Applied;
            pendingIndeterminateHyperBoostEntryIntent = null;
            if (journalFinalized)
            {
                hyperBoostOwnershipGeneration++;
                ownsHyperBoost = true;
                ownedHyperBoostBaseMode = baseMode;
                ownedHyperBoostRestoreIntent = savedIntent;
                ownedHyperBoostTransitionId =
                    pendingIndeterminateHyperBoostEntryTransitionId;
                ownedHyperBoostJournal = ownedJournal;
                ownedHyperBoostJournalSha256 = finalization.Sha256;
                ownedHyperBoostRequiresCoolingPad = expectedJournal.RequiresCoolingPad;
            }
            pendingIndeterminateHyperBoostEntryTransitionId = null;
            pendingIndeterminateHyperBoostJournal = null;
            pendingIndeterminateHyperBoostJournalSha256 = null;

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
            || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0)
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostEntryIntent = null;
            pendingIndeterminateHyperBoostEntryTransitionId = null;
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

        HyperBoostRestoreIntent restoreIntent =
            ResolveIndeterminateHyperBoostRestoreIntent(
                powerSource,
                savedIntent,
                confirmed);
        if (transitionRunner is null)
        {
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostEntryIntent = null;
            pendingIndeterminateHyperBoostEntryTransitionId = null;
            ClearOwnedHyperBoostState();
            const string missingRunner =
                "Fresh readback confirmed that HyperBoost stayed off after an indeterminate enable, but no exact transition runner is available. Control writes remain blocked.";
            history.RecordSummary(ControlOperationStatus.Failed, missingRunner);
            return new IndeterminateHyperBoostReconciliation(
                confirmed,
                SuppressInactiveJournalRetirement: true,
                missingRunner,
                IndeterminateHyperBoostResolution.Unsafe);
        }
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    restoreIntent,
                    confirmed,
                    cancellationToken,
                    transitionRunner: transitionRunner)
                .ConfigureAwait(false);
        device = transitionRunner.CurrentDevice;
        pendingIndeterminateHyperBoostEntryIntent = null;
        pendingIndeterminateHyperBoostEntryTransitionId = null;
        ConfirmedDeviceState restored = restoration.Readback
            ?? confirmed;
        ClearOwnedHyperBoostState();
        if (!restoration.Succeeded)
        {
            FailedHyperBoostRestoreObservation failureObservation =
                await CaptureFailedHyperBoostRestoreObservationAsync(
                        device,
                        restoreIntent,
                        restoration,
                        restored)
                    .ConfigureAwait(false);
            restored = failureObservation.State;
            var recoveryJournal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.RestorePending,
                baseMode,
                RequiresCoolingPad: expectedJournal.RequiresCoolingPad,
                timeProvider.GetUtcNow())
            {
                RestoreIntent = restoreIntent,
                RecoveryBaseline = failureObservation.RecoveryBaseline,
                TransitionId = expectedJournal.TransitionId,
            };
            bool restoreMarkerWritten = !hyperBoostJournalSafetyBlock
                && powerSource == PowerSource.PluggedIn
                && (await TryReplaceHyperBoostOwnershipJournalAsync(
                        expectedJournal,
                        expectedJournalSha256,
                        recoveryJournal,
                        "The failed HyperBoost entry restore could not be preserved for one explicit recovery.")
                    .ConfigureAwait(false)).Applied;
            hyperBoostJournalSafetyBlock = true;
            pendingIndeterminateHyperBoostJournal = null;
            pendingIndeterminateHyperBoostJournalSha256 = null;
            string failedMessage =
                $"Fresh readback confirmed that HyperBoost stayed off after an indeterminate enable, but the prior performance or fan intent was not fully restored. {restoration.Message} OpenBlade did not retry the HyperBoost write; control writes remain blocked."
                + (restoreMarkerWritten
                    ? " The exact saved intent remains pending for one explicit recovery."
                    : powerSource is PowerSource.Battery or PowerSource.UsbC
                        ? " The pending entry journal remains blocked until full-power AC returns, when OpenBlade can save the current destination intent without a hardware write."
                        : " The exact saved intent could not be recorded for recovery.");
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
                expectedJournal,
                expectedJournalSha256,
                "HyperBoost was confirmed inactive and the prior intent was restored after an indeterminate enable, but its ownership journal could not be cleared.")
            .ConfigureAwait(false);
        pendingIndeterminateHyperBoostJournal = null;
        pendingIndeterminateHyperBoostJournalSha256 = null;
        string restoredMessage = journalRetired
            ? $"Fresh readback confirmed that HyperBoost stayed off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write."
            : $"Fresh readback confirmed that HyperBoost stayed off and the prior {PerformanceLabel(restoreIntent.Performance)} performance and fan intent was restored without retrying the HyperBoost write, but its ownership journal could not be cleared; control writes remain blocked.";
        history.RecordSummary(
            ControlOperationStatus.Failed,
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
        ownedHyperBoostTransitionId = null;
        ownedHyperBoostJournal = null;
        ownedHyperBoostJournalSha256 = null;
        ownedHyperBoostRequiresCoolingPad = false;
        pendingOwnedHyperBoostPowerBoundaryExit = false;
        ownedHyperBoostPowerBoundaryExitSubmitted = false;
    }

    private async Task<HyperBoostIntentRestorationResult>
        RestoreKnownFailedHyperBoostEntryAsync(
        IBladeDevice device,
        HyperBoostRestoreIntent intent,
        PerformancePreset baseMode,
        bool requiresCoolingPad,
        HyperBoostOwnershipJournal expectedJournal,
        string expectedJournalSha256,
        ThermalTransitionStepRunner transitionRunner)
    {
        device = transitionRunner.CurrentDevice;
        HyperBoostIntentRestorationResult restoration =
            await RestoreHyperBoostIntentAsync(
                    device,
                    intent,
                    GetStatus().ConfirmedState,
                    CancellationToken.None,
                    transitionRunner: transitionRunner)
                .ConfigureAwait(false);
        device = transitionRunner.CurrentDevice;
        if (restoration.Succeeded)
        {
            bool journalRecovered = await RestorePreparedHyperBoostJournalAsync(
                    enabled: true,
                    baseMode: baseMode,
                    expectedJournal,
                    expectedJournalSha256)
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

        FailedHyperBoostRestoreObservation failureObservation =
            await CaptureFailedHyperBoostRestoreObservationAsync(
                    device,
                    intent,
                    restoration,
                    restoration.Readback ?? GetStatus().ConfirmedState!)
                .ConfigureAwait(false);
        var recoveryJournal = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.RestorePending,
            baseMode,
            requiresCoolingPad,
            timeProvider.GetUtcNow())
        {
            RestoreIntent = intent,
            RecoveryBaseline = failureObservation.RecoveryBaseline,
            TransitionId = expectedJournal.TransitionId,
        };
        bool recoveryRecorded = !hyperBoostJournalSafetyBlock
            && (await TryReplaceHyperBoostOwnershipJournalAsync(
                    expectedJournal,
                    expectedJournalSha256,
                    recoveryJournal,
                    "The failed HyperBoost entry baseline restore could not preserve its fresh recovery baseline.")
                .ConfigureAwait(false)).Applied;
        hyperBoostJournalSafetyBlock = true;
        string message =
            $"HyperBoost was not submitted, but its entry baseline could not be restored. {restoration.Message} Control writes remain blocked."
            + (recoveryRecorded
                ? " The exact saved intent remains pending for one explicit recovery."
                : " The exact saved intent could not be recorded for recovery.");
        history.RecordSummary(ControlOperationStatus.Failed, message);
        PublishIfChanged(GetStatus() with
        {
            ConfirmedState = failureObservation.State,
            WritesBlocked = true,
            Message = message,
        });
        return restoration with
        {
            Readback = failureObservation.State,
            Message = message,
        };
    }

    private async Task<bool> RestorePreparedHyperBoostJournalAsync(
        bool enabled,
        PerformancePreset baseMode,
        HyperBoostOwnershipJournal expectedJournal,
        string expectedJournalSha256)
    {
        if (hyperBoostJournalSafetyBlock)
        {
            return false;
        }

        bool restored;
        if (enabled)
        {
            restored = await TryClearHyperBoostOwnershipJournalAsync(
                    expectedJournal,
                    expectedJournalSha256,
                    "A non-submitted HyperBoost enable left a pending ownership journal that could not be cleared.")
                .ConfigureAwait(false);
        }
        else
        {
            var ownedJournal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.OwnedConfirmed,
                baseMode,
                ownedHyperBoostRequiresCoolingPad,
                timeProvider.GetUtcNow())
            {
                RestoreIntent = expectedJournal.RestoreIntent,
                TransitionId = expectedJournal.TransitionId,
            };
            HyperBoostOwnershipJournalMutationResult replacement =
                await TryReplaceHyperBoostOwnershipJournalAsync(
                    expectedJournal,
                    expectedJournalSha256,
                    ownedJournal,
                    "A non-submitted HyperBoost restore left a pending ownership journal that could not be returned to owned state.")
                .ConfigureAwait(false);
            restored = replacement.Applied;
            if (restored)
            {
                ownedHyperBoostJournal = ownedJournal;
                ownedHyperBoostJournalSha256 = replacement.Sha256;
            }
        }
        if (!restored)
        {
            PublishIfChanged(GetStatus() with
            {
                WritesBlocked = true,
                Message =
                    "The non-submitted HyperBoost transition could not restore its exact durable ownership journal. No hardware write will be retried; control writes remain blocked.",
            });
        }
        return restored;
    }

    private async Task<bool> IsExpectedHyperBoostJournalCurrentAsync(
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            HyperBoostOwnershipJournalLoadResult load =
                await hyperBoostOwnershipJournalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!load.Invalid
                && load.Journal == expectedJournal
                && load.Sha256 == expectedSha256)
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The caller retains the existing journal and blocks further writes.
        }

        hyperBoostJournalSafetyBlock = true;
        return false;
    }

    private async Task<HyperBoostOwnershipJournalMutationResult>
        TryReplaceHyperBoostOwnershipJournalAsync(
            HyperBoostOwnershipJournal expectedJournal,
            string? expectedSha256,
            HyperBoostOwnershipJournal replacementJournal,
            string failureMessage)
    {
        try
        {
            HyperBoostOwnershipJournalMutationResult result =
                await hyperBoostOwnershipJournalStore.TryReplaceAsync(
                        expectedJournal,
                        expectedSha256,
                        replacementJournal,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (!result.Applied)
            {
                hyperBoostJournalSafetyBlock = true;
                await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                    .ConfigureAwait(false);
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The durable journal changed before replacement.");
                return result;
            }

            hyperBoostJournalSafetyBlock = replacementJournal.State
                == HyperBoostOwnershipJournalState.RestorePending;
            hyperBoostRestorePending = replacementJournal.State
                == HyperBoostOwnershipJournalState.RestorePending;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return result;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                .ConfigureAwait(false);
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} {exception.Message}");
            return new HyperBoostOwnershipJournalMutationResult(false);
        }
    }

    private async Task RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
    {
        try
        {
            HyperBoostOwnershipJournalLoadResult load =
                await hyperBoostOwnershipJournalStore.LoadAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (!load.Invalid)
            {
                hyperBoostRestorePending = load.Journal?.State
                    == HyperBoostOwnershipJournalState.RestorePending;
            }
        }
        catch
        {
            // Preserve the prior presentation when durable state cannot be read.
        }
        hyperBoostJournalSafetyBlock = true;
    }

    private async Task<ControlOperationResult>
        RestoreAfterHyperBoostJournalFailureAsync(
            IBladeDevice device,
            ServiceStatusSnapshot baseline,
            ConfirmedDeviceState activeReadback,
            PerformancePreset baseMode,
            bool requiresCoolingPad,
            HyperBoostRestoreIntent restoreIntent,
            HyperBoostOwnershipJournal expectedJournal,
            string expectedJournalSha256,
            ThermalTransitionStepRunner transitionRunner)
    {
        device = transitionRunner.CurrentDevice;
        const string failurePrefix =
            "HyperBoost was activated, but durable ownership could not be recorded.";
        try
        {
            await transitionRunner.ApplyCommandStepAsync(
                    HyperBoostThermalComponents,
                    ThermalTransitionStepPurpose.HyperBoost,
                    (attemptDevice, cancellationToken) =>
                        ApplyHyperBoostCommandOnceAsync(
                            attemptDevice,
                            enabled: false,
                            baseMode,
                            requiresCoolingPad,
                            lifecycleValidation: null,
                            cancellationToken),
                    IsHyperBoostCommandRetryEligible,
                    CancellationToken.None)
                .ConfigureAwait(false);
            device = transitionRunner.CurrentDevice;
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
                            CancellationToken.None,
                            transitionRunner: transitionRunner)
                        .ConfigureAwait(false);
                device = transitionRunner.CurrentDevice;
                restored = intentRestoration.Readback ?? restored;
                if (!intentRestoration.Succeeded)
                {
                    FailedHyperBoostRestoreObservation failureObservation =
                        await CaptureFailedHyperBoostRestoreObservationAsync(
                                device,
                                restoreIntent,
                                intentRestoration,
                                restored)
                            .ConfigureAwait(false);
                    restored = failureObservation.State;
                    var recoveryJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        baseMode,
                        requiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = restoreIntent,
                        RecoveryBaseline = failureObservation.RecoveryBaseline,
                        TransitionId = expectedJournal.TransitionId,
                    };
                    bool recoveryRecorded =
                        (await TryReplaceHyperBoostOwnershipJournalAsync(
                                expectedJournal,
                                expectedJournalSha256,
                                recoveryJournal,
                                "The failed ownership-journal fallback restore could not preserve its fresh recovery baseline.")
                            .ConfigureAwait(false)).Applied;
                    hyperBoostJournalSafetyBlock = true;
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostTransitionId = null;
                    ownedHyperBoostRequiresCoolingPad = false;
                    string restorationFailed =
                        $"{failurePrefix} HyperBoost is off, but the entry-time performance or fan intent could not be restored. {intentRestoration.Message} Control writes remain blocked."
                        + (recoveryRecorded
                            ? " The exact saved intent remains pending for one explicit recovery."
                            : " The exact saved intent could not be recorded for recovery.");
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
                        "HyperBoost was restored after its ownership journal could not be finalized.",
                        expectedJournal,
                        expectedJournalSha256)
                    .ConfigureAwait(false);
                hyperBoostOwnershipGeneration++;
                ownsHyperBoost = false;
                ownedHyperBoostBaseMode = null;
                ownedHyperBoostRestoreIntent = null;
                ownedHyperBoostTransitionId = null;
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

        pendingIndeterminateHyperBoostRecoveryState =
            transitionRunner.CaptureRecoveryState();
        LatchPendingHyperBoost(
            baseline,
            enabled: false,
            baseMode,
            expectedJournal,
            expectedJournalSha256);
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
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256,
        string failureMessage)
    {
        try
        {
            bool cleared = await hyperBoostOwnershipJournalStore.TryClearAsync(
                    expectedJournal,
                    expectedSha256,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!cleared)
            {
                hyperBoostJournalSafetyBlock = true;
                await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                    .ConfigureAwait(false);
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The durable journal changed before removal.");
                return false;
            }

            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                .ConfigureAwait(false);
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
        string failureMessage,
        HyperBoostOwnershipJournal expectedJournal,
        string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            hyperBoostJournalSafetyBlock = true;
            await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                .ConfigureAwait(false);
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The expected ownership journal identity was unavailable.");
            return false;
        }

        bool relinquished = false;
        var relinquishedJournal = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.Relinquished,
            baseMode,
            requiresCoolingPad,
            timeProvider.GetUtcNow())
        {
            RestoreIntent = restoreIntent,
            TransitionId = expectedJournal.TransitionId,
        };
        string? relinquishedSha256 = null;
        try
        {
            HyperBoostOwnershipJournalMutationResult replacement =
                await hyperBoostOwnershipJournalStore.TryReplaceAsync(
                        expectedJournal,
                        expectedSha256,
                        relinquishedJournal,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (!replacement.Applied)
            {
                hyperBoostJournalSafetyBlock = true;
                await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                    .ConfigureAwait(false);
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The ownership journal changed before retirement.");
                return false;
            }
            relinquishedSha256 = replacement.Sha256;
            relinquished = true;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                .ConfigureAwait(false);
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The relinquished marker could not be written: {exception.Message}");
            return false;
        }

        try
        {
            bool cleared = await hyperBoostOwnershipJournalStore.TryClearAsync(
                    relinquishedJournal,
                    relinquishedSha256,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!cleared)
            {
                hyperBoostJournalSafetyBlock = true;
                await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                    .ConfigureAwait(false);
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The relinquished journal changed before removal.");
                return false;
            }
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return true;
        }
        catch (Exception exception)
        {
            bool exactRelinquishedStillCurrent = relinquished
                && await IsExpectedHyperBoostJournalCurrentAsync(
                        relinquishedJournal,
                        relinquishedSha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The journal could not be removed: {exception.Message}");
            if (exactRelinquishedStillCurrent)
            {
                hyperBoostJournalSafetyBlock = false;
                hyperBoostRestorePending = false;
                return true;
            }

            await RefreshHyperBoostJournalPendingStateAfterMutationMissAsync()
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<InactiveHyperBoostJournalRetirement>
        RetireInactiveHyperBoostJournalIfPresentAsync(
        string failureMessage,
        ConfirmedDeviceState? confirmed = null,
        IBladeDevice? observedDevice = null,
        PowerSource? observedPowerSource = null,
        bool powerSourceConfirmed = false,
        CancellationToken cancellationToken = default)
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
            return new InactiveHyperBoostJournalRetirement(false);
        }

        if (load.Invalid)
        {
            hyperBoostJournalSafetyBlock = true;
            hyperBoostRestorePending = false;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"{failureMessage} The journal is invalid and was preserved for manual inspection.");
            return new InactiveHyperBoostJournalRetirement(false);
        }

        if (load.Journal is not { } journal)
        {
            hyperBoostJournalSafetyBlock = false;
            hyperBoostRestorePending = false;
            lastReportedRetainedHyperBoostRestoreJournal = null;
            return new InactiveHyperBoostJournalRetirement(true);
        }

        if (journal.State == HyperBoostOwnershipJournalState.RestorePending)
        {
            hyperBoostRestorePending = true;
            if (confirmed is { } matchingConfirmed
                && (journal.RestoreRetryDisposition
                        != HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    || journal.RestoreRetryTarget
                        == HyperBoostRestoreRetryTarget.ExactJournalIntent))
            {
                if (ConfirmedStateMatchesHyperBoostRestoreIntent(
                        matchingConfirmed,
                        journal.RestoreIntent))
                {
                    string message =
                        $"Fresh readback confirmed that HyperBoost is off and the saved {PerformanceLabel(journal.RestoreIntent.Performance)} performance and fan intent is restored. The pending ownership journal was retired without another hardware write.";
                    bool cleared = await TryClearHyperBoostOwnershipJournalAsync(
                            journal,
                            load.Sha256,
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
                            message);
                    }
                    return new InactiveHyperBoostJournalRetirement(
                        cleared,
                        cleared ? matchingConfirmed : null,
                        cleared ? message : null);
                }
            }

            if (journal.RestoreRetryDisposition
                    == HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                && confirmed is not null
                && observedDevice is not null
                && observedPowerSource is { } powerSource
                && powerSourceConfirmed)
            {
                return await TryCompleteDeferredHyperBoostRestoreAsync(
                        journal,
                        load.Sha256,
                        confirmed,
                        observedDevice,
                        powerSource,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            hyperBoostJournalSafetyBlock = true;
            if (lastReportedRetainedHyperBoostRestoreJournal != journal)
            {
                history.RecordSummary(
                    ControlOperationStatus.Failed,
                    $"{failureMessage} The pending restore marker was preserved because inactive HyperBoost does not confirm the saved performance and fan intent.");
                lastReportedRetainedHyperBoostRestoreJournal = journal;
            }
            return new InactiveHyperBoostJournalRetirement(false);
        }

        if (hyperBoostJournalSafetyBlock
            && journal.State == HyperBoostOwnershipJournalState.EnablePending)
        {
            if (ConfigurationIsAdmitted
                && confirmed is not null
                && Automation.PowerSource == PowerSource.PluggedIn)
            {
                HyperBoostRestoreIntent destinationIntent =
                    CreateHyperBoostRestoreIntent(Automation.Resolve(confirmed));
                ServiceStatusSnapshot current = GetStatus();
                if (current.Capabilities is not null
                    && IsSupportedFullPowerHyperBoostIntent(
                        current.Capabilities,
                        destinationIntent))
                {
                    var replacementJournal = new HyperBoostOwnershipJournal(
                        HyperBoostOwnershipJournalState.RestorePending,
                        journal.BaseMode,
                        journal.RequiresCoolingPad,
                        timeProvider.GetUtcNow())
                    {
                        RestoreIntent = destinationIntent,
                        TransitionId = journal.TransitionId,
                    };
                    await TryReplaceHyperBoostOwnershipJournalAsync(
                            journal,
                            load.Sha256,
                            replacementJournal,
                            "The pending HyperBoost entry could not be converted to the current full-power destination recovery.")
                        .ConfigureAwait(false);
                    hyperBoostJournalSafetyBlock = true;
                }
            }
            return new InactiveHyperBoostJournalRetirement(false);
        }

        hyperBoostRestorePending = false;
        lastReportedRetainedHyperBoostRestoreJournal = null;

        bool retired = await RetireHyperBoostOwnershipJournalAsync(
                journal.BaseMode,
                journal.RequiresCoolingPad,
                journal.RestoreIntent,
                failureMessage,
                journal,
                load.Sha256)
            .ConfigureAwait(false);
        return new InactiveHyperBoostJournalRetirement(retired);
    }

    private async Task<InactiveHyperBoostJournalRetirement>
        TryCompleteDeferredHyperBoostRestoreAsync(
        HyperBoostOwnershipJournal journal,
        string? journalSha256,
        ConfirmedDeviceState confirmed,
        IBladeDevice observedDevice,
        PowerSource powerSource,
        CancellationToken cancellationToken)
    {
        if (powerSource is PowerSource.ExternalUnknown)
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }
        PowerSource freshPowerSource = await ResolveCurrentPowerSourceAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (freshPowerSource != powerSource
            || Automation.PowerSource != powerSource)
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }

        RefreshControllerPresenceCore(cancellationToken);
        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || pendingWriteConfirmations.HasPending
            || current.ControllerDetectionWarning is not null
            || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0
            || current.Capabilities is null
            || !deviceProvider.TryGetVerifiedDevice(out IBladeDevice verifiedDevice))
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }
        DeviceCapabilities admittedCapabilities = current.Capabilities;

        DesiredDeviceState authoritativeDestination =
            Automation.Resolve(confirmed);

        HyperBoostRestoreIntent destinationIntent;
        switch (journal.RestoreRetryTarget)
        {
            case HyperBoostRestoreRetryTarget.ExactJournalIntent:
                if (powerSource != PowerSource.PluggedIn)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return new InactiveHyperBoostJournalRetirement(false);
                }
                destinationIntent = journal.RestoreIntent;
                break;
            case HyperBoostRestoreRetryTarget.CurrentPowerDestination:
                if (!ConfigurationIsAdmitted)
                {
                    hyperBoostJournalSafetyBlock = true;
                    return new InactiveHyperBoostJournalRetirement(false);
                }
                destinationIntent =
                    CreateHyperBoostRestoreIntent(authoritativeDestination);
                break;
            default:
                hyperBoostJournalSafetyBlock = true;
                return new InactiveHyperBoostJournalRetirement(false);
        }
        if (!IsSupportedHyperBoostRestoreIntent(
                admittedCapabilities,
                destinationIntent))
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }

        if (ConfirmedStateMatchesHyperBoostRestoreIntent(
                confirmed,
                destinationIntent))
        {
            string matchedMessage =
                $"Fresh readback confirmed that HyperBoost is off and the deferred {PerformanceLabel(destinationIntent.Performance)} performance and saved fan destination is already restored. The pending ownership journal was retired without another hardware write.";
            bool cleared = await TryClearHyperBoostOwnershipJournalAsync(
                    journal,
                    journalSha256,
                    "The deferred HyperBoost destination already matched fresh readback, but its ownership journal could not be cleared.")
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
                    matchedMessage);
            }
            return new InactiveHyperBoostJournalRetirement(
                cleared,
                cleared ? confirmed : null,
                cleared ? matchedMessage : null);
        }

        if (journal.RecoveryBaseline is not { } recoveryBaseline
            || !ConfirmedStateMatchesHyperBoostRecoveryBaseline(
                confirmed,
                recoveryBaseline))
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }

        var retryBaseline = new HyperBoostRecoveryBaseline(
            confirmed.Performance,
            confirmed.Fans ?? FanMode.Automatic,
            confirmed.HyperBoostActive,
            confirmed.CustomPerformance,
            confirmed.GpuClockOffsets?.Current);
        if (!retryBaseline.IsWellFormed)
        {
            hyperBoostJournalSafetyBlock = true;
            return new InactiveHyperBoostJournalRetirement(false);
        }

        var claimedJournal = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.RestorePending,
            journal.BaseMode,
            journal.RequiresCoolingPad,
            timeProvider.GetUtcNow())
        {
            RestoreIntent = destinationIntent,
            RecoveryBaseline = retryBaseline,
            RestoreRetryDisposition = HyperBoostRestoreRetryDisposition.None,
            RestoreRetryTarget = HyperBoostRestoreRetryTarget.None,
            TransitionId = journal.TransitionId,
        };
        HyperBoostOwnershipJournalMutationResult retryClaim =
            await TryReplaceHyperBoostOwnershipJournalAsync(
                    journal,
                    journalSha256,
                    claimedJournal,
                    "The deferred HyperBoost restore could not durably consume its one retry before submission.")
                .ConfigureAwait(false);
        hyperBoostJournalSafetyBlock = true;
        if (!retryClaim.Applied)
        {
            return new InactiveHyperBoostJournalRetirement(false);
        }

        // A queued power-source transition advances Automation before this
        // recovery path publishes its fresh observation. The exact journal is
        // now claimed and all admission/baseline checks passed, so stage that
        // destination before the runner enforces a stable, non-rebased intent.
        PublishIfChanged(GetStatus() with
        {
            ActivePowerSource = powerSource,
            DesiredState = authoritativeDestination,
        });

        long transitionOwnershipGeneration = hyperBoostOwnershipGeneration;
        long transitionPowerGeneration = powerSourceGeneration;
        var recoveryState = new ThermalTransitionRecoveryState(
            Guid.NewGuid(),
            Consumed: true,
            "The durable deferred HyperBoost claim consumed this transition's one recovery lease before submission.");
        ThermalTransitionStepRunner transitionRunner =
            CreateHyperBoostTransitionRunner(
                verifiedDevice,
                enabled: false,
                claimedJournal.BaseMode,
                destinationIntent,
                claimedJournal,
                retryClaim.Sha256,
                transitionOwnershipGeneration,
                transitionPowerGeneration,
                powerSource,
                ownedLifecycleRestore: false,
                allowOwnedJournalCleanup: true,
                validationOnly: false,
                authorityPurpose:
                    journal.RestoreRetryTarget ==
                        HyperBoostRestoreRetryTarget.ExactJournalIntent
                        ? ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
                        : ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
                allowIntentRebase: journal.RestoreRetryTarget
                    == HyperBoostRestoreRetryTarget.ExactJournalIntent,
                recoveryState: recoveryState);

        HyperBoostIntentRestorationResult restoration;
        try
        {
            restoration = await RestoreHyperBoostIntentAsync(
                    transitionRunner.CurrentDevice,
                    destinationIntent,
                    confirmed,
                    cancellationToken,
                    transitionRunner: transitionRunner)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            hyperBoostJournalSafetyBlock = true;
            throw;
        }

        ConfirmedDeviceState restored = restoration.Readback ?? confirmed;
        if (!restoration.Succeeded)
        {
            FailedHyperBoostRestoreObservation failureObservation =
                await CaptureFailedHyperBoostRestoreObservationAsync(
                        transitionRunner.CurrentDevice,
                        destinationIntent,
                        restoration,
                        restored)
                    .ConfigureAwait(false);
            restored = failureObservation.State;
            HyperBoostOwnershipJournal failedJournal = claimedJournal with
            {
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                RecoveryBaseline = failureObservation.RecoveryBaseline,
            };
            await TryReplaceHyperBoostOwnershipJournalAsync(
                    claimedJournal,
                    retryClaim.Sha256,
                    failedJournal,
                    "The consumed deferred HyperBoost restore could not preserve its fresh recovery baseline.")
                .ConfigureAwait(false);
            hyperBoostJournalSafetyBlock = true;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"The one deferred HyperBoost destination restore did not complete. {restoration.Message} The saved restore remains blocked and will not be retried automatically.");
            string restoreFailureMessage =
                $"The one deferred HyperBoost destination restore did not complete. {restoration.Message} The saved restore remains blocked and will not be retried automatically.";
            return new InactiveHyperBoostJournalRetirement(
                false,
                restored,
                restoreFailureMessage);
        }

        driftTracker.Observe(restored);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        driftTracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);
        bool retired = await RetireHyperBoostOwnershipJournalAsync(
                claimedJournal.BaseMode,
                claimedJournal.RequiresCoolingPad,
                destinationIntent,
                "The deferred HyperBoost destination restore completed, but its ownership journal could not be retired.",
                claimedJournal,
                retryClaim.Sha256)
            .ConfigureAwait(false);
        string message = retired
            ? $"A fresh matching observation completed the one deferred HyperBoost destination restore to {PerformanceLabel(destinationIntent.Performance)} performance and the saved fan intent."
            : $"The deferred HyperBoost destination restore reached {PerformanceLabel(destinationIntent.Performance)} performance and the saved fan intent, but its ownership journal could not be retired; control writes remain blocked.";
        history.RecordSummary(
            retired
                ? ControlOperationStatus.Applied
                : ControlOperationStatus.Failed,
            message);
        return new InactiveHyperBoostJournalRetirement(
            retired,
            restored,
            message);
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
                },
                pendingBatteryIntentCommit: new(Automation, FullChargeOnly: true));
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
            resumeStateValidation.DiscardExpired();
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
            bool canUseReadOnlyFastPath = !resumeStateValidation.ResumeStarted
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
                RecordResumeValidationResult(
                    resumeStateValidation.RecordPowerSourceTransition(powerSource));
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
            ServiceStatusSnapshot observed = GetStatus();
            if (ownsHyperBoost && ownedHyperBoostRequiresCoolingPad
                && observed.ConfirmedState?.HyperBoostActive == true
                && observed.DeviceVerified && !observed.WritesBlocked
                && observed.ControllerDetectionWarning is null
                && RazerThermalControllerPolicy.GetBlockingControllers(observed).Count == 0
                && coolingPadExitAttemptGeneration != hyperBoostOwnershipGeneration
                && hyperBoostPadDiscoverySource.Observe() == CoolingPadConnectionState.Absent)
            {
                // A failed enumeration is not a disconnect. Confirmed absence
                // permits one owned exit through the existing journaled executor.
                coolingPadExitAttemptGeneration = hyperBoostOwnershipGeneration;
                await SetHyperBoostCoreAsync(
                    enabled: false,
                    transitionId: ownedHyperBoostTransitionId,
                    expectedOwnershipGeneration: hyperBoostOwnershipGeneration,
                    lifecycleValidation: null,
                    restoreUserIntent: true,
                    exitPowerSourceOverride: observed.ActivePowerSource,
                    expectedExitPowerSourceGeneration: powerSourceGeneration,
                    token: cancellationToken,
                    operationGateAlreadyHeld: true).ConfigureAwait(false);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task ObserveAsync(CancellationToken cancellationToken) =>
        ObserveAsync(includeLiveReadings: false, cancellationToken);

    public bool ShouldRunThermalConvergenceObservation() =>
        ThermalConvergencePolicyAdmitted();

    public bool ShouldRunPendingTransitionObservation() =>
        pendingTransitionContinuation is not null
        || pendingResumePerformanceDrift is { } pending
            && timeProvider.GetUtcNow() < pending.ExpiresAt;

    public Task RunThermalConvergenceAsync(CancellationToken cancellationToken) =>
        thermalConvergenceArbiter.RunAsync(
            ExecuteThermalConvergenceAsync,
            cancellationToken);

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
        HyperBoostRestoreIntent? recoveredEntryIntent = null;
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
                    bool retired = (await RetireInactiveHyperBoostJournalIfPresentAsync(
                            "Inactive HyperBoost was confirmed after an unexpected exit, but its stale ownership journal could not be retired.")
                        .ConfigureAwait(false)).Retired;
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
                    recoveredEntryIntent = journal.RestoreIntent;
                    ownedHyperBoostTransitionId = journal.TransitionId;
                    ownedHyperBoostJournal = journal;
                    ownedHyperBoostJournalSha256 = journalLoad.Sha256;
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
            // The unexpected exit discarded in-memory temporary overrides that may have produced the
            // journaled entry intent. Reinstate it, as the user-authorized retry does, only when the
            // recovery actually wrote it: full AC and a confirmed state exactly matching that intent.
            // A battery/USB-C or blocked exit writes a different intent and must not be masked.
            if (recoveredEntryIntent is not null
                && Automation.PowerSource == PowerSource.PluggedIn
                && GetStatus().ConfirmedState is { HyperBoostActive: false } restored
                && ConfirmedStateMatchesRecoveredIntent(restored, recoveredEntryIntent))
            {
                RestoreRecoveredHyperBoostIntentInAutomation(recoveredEntryIntent);
                deferredRecoveredHyperBoostEntryIntent =
                    unexpectedExitRecoveryDeferredForUserConfiguration
                        ? recoveredEntryIntent
                        : null;
                AdvanceFanDesiredRevision();
                ServiceStatusSnapshot recoveredStatus = GetStatus();
                PublishIfChanged(recoveredStatus with
                {
                    DesiredState = Automation.Resolve(restored),
                    PerformanceOverride = Automation.PerformanceOverride,
                    FanOverride = Automation.FanOverride,
                    FanControlOverride = Automation.FanControlOverride,
                });
            }

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

        current = GetStatus();
        if (FanControllerAdmissionUnsafe(current))
        {
            return;
        }

        PowerSource confirmedPowerSource =
            await ResolveCurrentPowerSourceAsync(cancellationToken).ConfigureAwait(false);
        if (confirmedPowerSource != current.ActivePowerSource
            || confirmedPowerSource == PowerSource.ExternalUnknown)
        {
            if (confirmedPowerSource != PowerSource.ExternalUnknown)
            {
                _ = ChangePowerSourceCore(confirmedPowerSource);
            }
            return;
        }

        DeferredManualFanRecoveryAuthority recoveryAuthority =
            CaptureFanRecoveryAuthority(confirmedPowerSource);

        // Consume the bounded attempt immediately before admission. A plain
        // pre-submission cancellation re-arms it; an indeterminate submitted
        // write remains consumed and is never retried automatically.
        manualFanCurveRecoveryPending = false;
        ScopedApplyOutcome? appliedOutcome = null;
        ThermalTransitionTrace? transitionTrace = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                confirmedPowerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyRecoveryFanControlOutcomeAsync(
                        settings,
                        token),
                cancellationToken,
                stillControlsSession: stillControlsSession,
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Fans],
                    additionalWriteBlockReason: () =>
                        FanRecoveryAuthorityBlockReason(recoveryAuthority)),
                observeScopedOutcome: (outcome, _, trace) =>
                {
                    appliedOutcome = outcome;
                    transitionTrace = trace;
                }).ConfigureAwait(false);
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
        if (IsUnspentPreSubmission(appliedOutcome, transitionTrace))
        {
            manualFanCurveRecoveryPending = true;
            return;
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
        if (deferredManualFanSafetyOwnership.State ==
            DeferredManualFanSafetyState.Lost)
        {
            return;
        }

        if (!ConfigurationIsAdmitted)
        {
            if (deferredManualFanSafetyOwnership.State !=
                DeferredManualFanSafetyState.FloorOwned)
            {
                return;
            }

            PowerSource cleanupPowerSource = await ResolveCurrentPowerSourceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (cleanupPowerSource != PowerSource.ExternalUnknown
                && stillControlsSession?.Invoke() != false)
            {
                await HandBackDeferredManualFanSafetyFloorCoreAsync(
                        cleanupPowerSource,
                        cancellationToken,
                        stillControlsSession)
                    .ConfigureAwait(false);
            }
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
            deferredManualFanSafetyOwnership.State ==
                DeferredManualFanSafetyState.PendingUnowned
            && deferredManualFanSafetyOwnership.MatchesPendingBaseline(
                current.ConfirmedState?.Fans);
        bool ownedAutomaticBaseline =
            allowOwnedAutomaticBaseline
            && deferredManualFanSafetyOwnership.State ==
                DeferredManualFanSafetyState.AutomaticOwned
            && current.ConfirmedState?.Fans == FanMode.Automatic;
        if (!manualFanCurveRecoveryPending
            || powerSource is not (PowerSource.PluggedIn or PowerSource.Battery)
            || (!pendingBaseline && !ownedAutomaticBaseline)
            || !current.DeviceVerified
            || current.WritesBlocked
            || current.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            if (deferredManualFanSafetyOwnership.State ==
                    DeferredManualFanSafetyState.PendingUnowned
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
        DeferredManualFanSafetyOwnership priorOwnership =
            deferredManualFanSafetyOwnership;
        ExactFanControlOwnership priorFanOwnership = exactFanControlOwnership;
        var recoveryAuthority = CaptureDeferredManualFanRecoveryAuthority(
            priorOwnership,
            powerSource);
        // Revoke any stale ownership before submission. ApplyScopedIntentAsync
        // records the staged ManualCurve settings again only after an Applied
        // floor write, so shutdown cleanup cannot act on an uncertain attempt.
        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.TransitionPending(
                recoveryAuthority.AttemptId);
        manualFanCurveRecoveryPending = false;
        exactFanControlOwnership = ExactFanControlOwnership.Unowned;
        ScopedApplyOutcome? appliedOutcome = null;
        ThermalTransitionTrace? transitionTrace = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyRecoveryFanControlOutcomeAsync(
                        safetyFloor,
                        token,
                        state => DeferredManualFanSafetyStateBlockReason(
                            priorOwnership,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = FanMode.ManualCurve },
                    FanControl = safetyFloor,
                },
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Fans],
                    additionalWriteBlockReason: () =>
                        DeferredManualFanRecoveryWriteBlockReason(
                            recoveryAuthority)),
                observeScopedOutcome: (outcome, _, trace) =>
                {
                    appliedOutcome = outcome;
                    transitionTrace = trace;
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = true;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = true;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }

        if (IsUnspentPreSubmission(appliedOutcome, transitionTrace))
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = true;
            exactFanControlOwnership = priorFanOwnership;
            return;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return;
        }

        if (!IsExactFanIntentOwned(safetyFloor))
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return;
        }

        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.Owned(
                DeferredManualFanSafetyState.FloorOwned,
                safetyFloor,
                fanAuthorityEpoch,
                fanDesiredRevision);
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
        if (deferredManualFanSafetyOwnership.State !=
                DeferredManualFanSafetyState.FloorOwned
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
        DeferredManualFanSafetyOwnership priorOwnership =
            deferredManualFanSafetyOwnership;
        ExactFanControlOwnership priorFanOwnership = exactFanControlOwnership;
        var recoveryAuthority = CaptureDeferredManualFanRecoveryAuthority(
            priorOwnership,
            powerSource);
        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.TransitionPending(
                recoveryAuthority.AttemptId);
        exactFanControlOwnership = ExactFanControlOwnership.Unowned;
        ScopedApplyOutcome? appliedOutcome = null;
        ThermalTransitionTrace? transitionTrace = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyRecoveryFanControlOutcomeAsync(
                        automatic,
                        token,
                        state => DeferredManualFanSafetyStateBlockReason(
                            priorOwnership,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = FanMode.Automatic },
                    FanControl = automatic,
                },
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Fans],
                    additionalWriteBlockReason: () =>
                        DeferredManualFanRecoveryWriteBlockReason(recoveryAuthority),
                    authorityPurpose:
                        ScopedThermalAuthorityPurpose.PriorAuthorityCleanup),
                observeScopedOutcome: (outcome, _, trace) =>
                {
                    appliedOutcome = outcome;
                    transitionTrace = trace;
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }

        if (IsUnspentPreSubmission(appliedOutcome, transitionTrace))
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            exactFanControlOwnership = priorFanOwnership;
            return;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return;
        }

        if (appliedOutcome is not
            {
                WriteAttempted: true,
                ReadbackMatched: true,
            }
            || exactFanControlOwnership.Disposition !=
                ExactFanControlOwnershipDisposition.Unowned)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return;
        }

        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.Owned(
                DeferredManualFanSafetyState.AutomaticOwned,
                automatic,
                fanAuthorityEpoch,
                fanDesiredRevision);
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
        bool matches = deferredManualFanSafetyOwnership.State switch
        {
            DeferredManualFanSafetyState.FloorOwned =>
                confirmedFans is { } value
                && DeviceSubsystemOrder.IsManualFanMode(value)
                && deferredManualFanSafetyOwnership.AuthorityEpoch ==
                    fanAuthorityEpoch
                && IsDeferredExactFanIntentOwned(
                    deferredManualFanSafetyOwnership),
            DeferredManualFanSafetyState.AutomaticOwned =>
                confirmedFans == FanMode.Automatic
                && deferredManualFanSafetyOwnership.AuthorityEpoch ==
                    fanAuthorityEpoch,
            DeferredManualFanSafetyState.PendingUnowned =>
                deferredManualFanSafetyOwnership.MatchesPendingBaseline(
                    confirmedFans),
            DeferredManualFanSafetyState.Lost => true,
            _ => false,
        };
        if (!matches)
        {
            LoseDeferredManualFanSafetyOwnershipCore();
        }

        return matches;
    }

    private static string? DeferredManualFanSafetyStateBlockReason(
        DeferredManualFanSafetyOwnership expected,
        ConfirmedDeviceState state)
    {
        bool matches = expected.State switch
        {
            DeferredManualFanSafetyState.PendingUnowned =>
                expected.MatchesPendingBaseline(state.Fans),
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
        deferredManualFanSafetyOwnership = DeferredManualFanSafetyOwnership.Lost;
        manualFanCurveRecoveryPending = false;
        exactFanControlOwnership = ExactFanControlOwnership.Unowned;
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

        if (!ConfigurationIsAdmitted)
        {
            if (deferredManualFanSafetyOwnership.State ==
                DeferredManualFanSafetyState.FloorOwned)
            {
                await HandBackDeferredManualFanSafetyFloorCoreAsync(
                        powerSource,
                        cancellationToken,
                        stillControlsSession)
                    .ConfigureAwait(false);
            }

            return deferredManualFanSafetyOwnership.State ==
                DeferredManualFanSafetyState.AutomaticOwned;
        }

        FanControlSettings desired = Automation.Resolve(
                current.ConfirmedState ?? UnknownConfirmedState)
            .FanControl
            ?? FanControlSettings.FromLegacy(
                Automation.Resolve(current.ConfirmedState ?? UnknownConfirmedState)
                    .Fans
                    .Value);
        if (deferredManualFanSafetyOwnership.State ==
            DeferredManualFanSafetyState.PendingUnowned)
        {
            if (!deferredManualFanSafetyOwnership
                .ReestablishAfterUserChange)
            {
                return true;
            }

            await MaintainDeferredManualFanSafetyFloorCoreAsync(
                cancellationToken,
                allowOwnedAutomaticBaseline: true,
                stillControlsSession).ConfigureAwait(false);
            current = GetStatus();
            if (deferredManualFanSafetyOwnership.State !=
                DeferredManualFanSafetyState.FloorOwned)
            {
                return false;
            }
        }

        if (deferredManualFanSafetyOwnership.State ==
            DeferredManualFanSafetyState.Lost)
        {
            return false;
        }

        if (deferredManualFanSafetyOwnership.State ==
            DeferredManualFanSafetyState.FloorOwned)
        {
            if (desired.Mode == FanMode.Automatic)
            {
                await HandBackDeferredManualFanSafetyFloorCoreAsync(
                    powerSource,
                    cancellationToken,
                    stillControlsSession).ConfigureAwait(false);
                return deferredManualFanSafetyOwnership.State ==
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
            return deferredManualFanSafetyOwnership.State ==
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

        DeferredManualFanSafetyOwnership priorOwnership =
            deferredManualFanSafetyOwnership;
        ExactFanControlOwnership priorFanOwnership = exactFanControlOwnership;
        bool priorRecoveryPending = manualFanCurveRecoveryPending;
        var recoveryAuthority = CaptureDeferredManualFanRecoveryAuthority(
            priorOwnership,
            powerSource);
        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.TransitionPending(
                recoveryAuthority.AttemptId);
        manualFanCurveRecoveryPending = false;
        exactFanControlOwnership = ExactFanControlOwnership.Unowned;
        ScopedApplyOutcome? appliedOutcome = null;
        ThermalTransitionTrace? transitionTrace = null;
        ControlOperationResult result;
        try
        {
            result = await ApplyScopedIntentAsync(
                DeviceSubsystem.Fans,
                powerSource,
                (coordinator, _, token) =>
                    coordinator.ApplyRecoveryFanControlOutcomeAsync(
                        settings,
                        token,
                        state => DeferredManualFanSafetyStateBlockReason(
                            priorOwnership,
                            state)),
                cancellationToken,
                desired => desired with
                {
                    Fans = desired.Fans with { Value = settings.Mode },
                    FanControl = settings,
                },
                stillControlsSession: stillControlsSession,
                thermalExecutionOptions: new ScopedThermalExecutionOptions(
                    [DeviceSubsystem.Fans],
                    additionalWriteBlockReason: () =>
                        DeferredManualFanRecoveryWriteBlockReason(recoveryAuthority)),
                observeScopedOutcome: (outcome, _, trace) =>
                {
                    appliedOutcome = outcome;
                    transitionTrace = trace;
                }).ConfigureAwait(false);
        }
        catch (IntentionalWriteCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WriteAuthorityLostException)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = priorRecoveryPending;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = priorRecoveryPending;
            exactFanControlOwnership = priorFanOwnership;
            throw;
        }

        if (IsUnspentPreSubmission(appliedOutcome, transitionTrace))
        {
            deferredManualFanSafetyOwnership = priorOwnership;
            manualFanCurveRecoveryPending = priorRecoveryPending;
            exactFanControlOwnership = priorFanOwnership;
            return false;
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return false;
        }

        bool confirmedAutomatic = appliedOutcome is
        {
            WriteAttempted: true,
            ReadbackMatched: true,
        } && exactFanControlOwnership.Disposition ==
            ExactFanControlOwnershipDisposition.Unowned;
        bool confirmedOwnership = settings.Mode == FanMode.Automatic
            ? confirmedAutomatic
            : IsExactFanIntentOwned(settings);
        if (!confirmedOwnership)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            return false;
        }

        deferredManualFanSafetyOwnership =
            DeferredManualFanSafetyOwnership.Owned(
                settings.Mode == FanMode.Automatic
                    ? DeferredManualFanSafetyState.AutomaticOwned
                    : DeferredManualFanSafetyState.FloorOwned,
                settings,
                fanAuthorityEpoch,
                fanDesiredRevision);
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

        // Loading the user configuration rebuilt automation. Reinstate the entry intent that
        // recovery wrote, before fan reconciliation and drift classification, under the same
        // full-AC exact-match gate; anything else is drift the user must see. Firmware Automatic
        // fans written by that recovery need no ongoing ownership, so they are not drift either.
        bool recoveredAutomaticFansReinstated = false;
        if (deferredRecoveredHyperBoostEntryIntent is { } recoveredEntryIntent)
        {
            deferredRecoveredHyperBoostEntryIntent = null;
            if (confirmedPowerSource == PowerSource.PluggedIn
                && confirmed is { HyperBoostActive: false }
                && ConfirmedStateMatchesRecoveredIntent(confirmed, recoveredEntryIntent))
            {
                RestoreRecoveredHyperBoostIntentInAutomation(recoveredEntryIntent);
                AdvanceFanDesiredRevision();
                recoveredAutomaticFansReinstated =
                    recoveredEntryIntent.FanControl.Mode == FanMode.Automatic;
            }
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
        if (!deferredFanStateConverged && !recoveredAutomaticFansReinstated)
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
            ObservationReason = current.ObservationReason == DeviceObservationReason.RetainedPowerSource
                ? DeviceObservationReason.None
                : current.ObservationReason,
            WritesBlocked = false,
            DesiredState = Automation.Resolve(confirmed),
            Message =
                "The power source changed while crash recovery waits for an authorized active-user request; no hardware profile was applied.",
        });
    }

    public async Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        CloseThermalConvergenceAdmission(
            ThermalConvergenceReason.PowerUnsettled,
            thermalConvergenceShutdown: false);
        Interlocked.Increment(ref powerAdmissionEpoch);
        Interlocked.Increment(ref pendingPowerAdmissions);
        try
        {
            await operationGate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref pendingPowerAdmissions);
        }
        try
        {
            resumeStateValidation.DiscardExpired();
            resumeStateValidation.DiscardUnstarted();
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
                    RecordResumeValidationResult(
                        resumeStateValidation.RecordPowerSourceTransition(powerSource));
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
        if (lowPowerGate.IsActive)
        {
            return;
        }

        // Periodic classification is repeatable maintenance. It must not queue
        // behind an admitted control transition and then invalidate that
        // transition before the classifier can observe anything. Explicit
        // Windows power events retain their queued admission semantics through
        // HandlePowerSourceChangedAsync, and resume performs a fresh refresh.
        if (!await operationGate.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (lowPowerGate.IsActive)
            {
                return;
            }

            Interlocked.Increment(ref powerAdmissionEpoch);
            await RefreshControllerPresenceAndRelinquishLightingAsync(
                cancellationToken).ConfigureAwait(false);
            PowerSource classified =
                await ResolveCurrentPowerSourceAsync(cancellationToken);
            ServiceStatusSnapshot current = GetStatus();
            if (classified == PowerSource.ExternalUnknown)
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.PowerUnsettled,
                    thermalConvergenceShutdown: false);
                PowerSource statusPowerSource = PowerSourceForStatus(classified);
                PublishIfChanged(current with
                {
                    ActivePowerSource = statusPowerSource,
                    ObservationReason = current.ObservationReason is
                        DeviceObservationReason.CheckingConnection or DeviceObservationReason.RefreshingControls
                        ? current.ObservationReason
                        : statusPowerSource == PowerSource.ExternalUnknown
                            ? DeviceObservationReason.None
                            : DeviceObservationReason.RetainedPowerSource,
                    WritesBlocked = true,
                    Message = statusPowerSource == PowerSource.ExternalUnknown
                        ? UnclassifiedExternalPowerMessage
                        : TransientExternalPowerMessage,
                });
                return;
            }

            bool changed = ChangePowerSourceCore(classified);
            if (changed)
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.PowerUnsettled,
                    thermalConvergenceShutdown: false);
            }
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
                || current.ObservationReason == DeviceObservationReason.RetainedPowerSource)
            {
                PublishIfChanged(current with
                {
                    ActivePowerSource = classified,
                    ObservationReason = current.ObservationReason == DeviceObservationReason.RetainedPowerSource
                        ? DeviceObservationReason.None
                        : current.ObservationReason,
                    WritesBlocked = (current.ObservationReason is
                        DeviceObservationReason.CheckingConnection or DeviceObservationReason.RefreshingControls)
                        && current.WritesBlocked,
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

    private async Task PublishLightingOwnershipLostDuringDriverModeMaintenanceAsync(
        IBladeDevice device,
        LightingRendererOwnershipState? ownershipBefore,
        CancellationToken cancellationToken)
    {
        LightingRendererOwnershipState? ownershipAfter =
            await TryReadLightingOwnershipAsync(device, cancellationToken)
                .ConfigureAwait(false);
        if (ownershipBefore is { } previousOwnership
            && IsExplicitLightingOwnership(previousOwnership)
            && ownershipAfter == LightingRendererOwnershipState.None)
        {
            // Maintenance can retire a one-shot frame when firmware has
            // left Driver mode. This is external drift, not a renderer
            // fault that may be replayed without the override policy.
            RelinquishRememberedLightingOwnership();
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);
            ServiceStatusSnapshot current = GetStatus();
            PublishIfChanged(current with
            {
                ConfirmedState = current.ConfirmedState is null
                    ? null
                    : current.ConfirmedState with { Lighting = null },
                LightingRendererOwnership = LightingRendererOwnershipState.None,
                ExternalChanges = driftTracker.ExternalChanges,
            });
        }
    }

    public async Task MaintainDriverModeAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (lowPowerGate.IsActive)
        {
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lowPowerGate.IsActive)
            {
                return;
            }

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

            LightingRendererOwnershipState? ownershipBefore = null;
            try
            {
                ownershipBefore =
                    await TryReadLightingOwnershipAsync(device, cancellationToken)
                        .ConfigureAwait(false);
                bool? active = await device.MaintainDriverModeAsync(cancellationToken)
                    .ConfigureAwait(false);
                await PublishLightingOwnershipLostDuringDriverModeMaintenanceAsync(
                    device, ownershipBefore, cancellationToken).ConfigureAwait(false);
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
                await PublishLightingOwnershipLostDuringDriverModeMaintenanceAsync(
                    device, ownershipBefore, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        Func<bool>? stillPreparing = null,
        long? lowPowerResumeGeneration = null)
    {
        long expectedOwnershipGeneration = 0;
        PowerSource exitPowerSource = PowerSource.ExternalUnknown;
        long expectedExitPowerSourceGeneration = 0;
        Guid? lifecycleTransitionId = null;
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (stillPreparing?.Invoke() == false)
                {
                    return new(ControlOperationStatus.Blocked,
                        "The suspend preparation expired before HyperBoost lifecycle admission.");
                }
                ServiceStatusSnapshot current = GetStatus();
                if (!ownsHyperBoost)
                {
                    return new ControlOperationResult(
                        ControlOperationStatus.Stored,
                        "HyperBoost was left unchanged because OpenBlade did not establish ownership of the active mode.");
                }

                if (current.ConfirmedState?.HyperBoostActive != true)
                {
                    bool retired = (await RetireInactiveHyperBoostJournalIfPresentAsync(
                            "HyperBoost was already inactive at the lifecycle boundary, but its ownership journal could not be retired.")
                        .ConfigureAwait(false)).Retired;
                    hyperBoostOwnershipGeneration++;
                    ownsHyperBoost = false;
                    ownedHyperBoostBaseMode = null;
                    ownedHyperBoostRestoreIntent = null;
                    ownedHyperBoostTransitionId = null;
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
                if (stillPreparing?.Invoke() == false)
                {
                    return new(ControlOperationStatus.Blocked,
                        "The suspend preparation expired during HyperBoost power admission.");
                }
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
                    RebaseLowPowerResumeState(lowPowerResumeGeneration);
                }

                expectedOwnershipGeneration = hyperBoostOwnershipGeneration;
                expectedExitPowerSourceGeneration = powerSourceGeneration;
                lifecycleTransitionId = ownedHyperBoostTransitionId;
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
                $"Attempting to exit OpenBlade-owned HyperBoost and restore its entry-time profile {boundary}.",
                lifecycleTransitionId);

            ControlOperationResult result = await SetHyperBoostCoreAsync(
                    enabled: false,
                    transitionId: lifecycleTransitionId,
                    expectedOwnershipGeneration,
                    lifecycleValidation: null,
                    restoreUserIntent: true,
                    exitPowerSourceOverride: exitPowerSource,
                    expectedExitPowerSourceGeneration,
                    cancellationToken,
                    stillPreparing: stillPreparing,
                    lowPowerResumeGeneration: lowPowerResumeGeneration)
                .ConfigureAwait(false);
            if (result.Status != ControlOperationStatus.Applied)
            {
                return result;
            }

            string restored =
                $"OpenBlade-owned HyperBoost exited and its entry-time profile was restored {boundary}.";
            history.RecordSummary(
                ControlOperationStatus.Applied,
                restored,
                lifecycleTransitionId);
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
        CloseThermalConvergenceAdmission(
            ThermalConvergenceReason.Shutdown,
            thermalConvergenceShutdown: true);
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

    public Task<ControlOperationResult> PrepareForLowPowerAsync(CancellationToken cancellationToken) =>
        PrepareForLowPowerCoreAsync(lowPowerGate.Generation, null, cancellationToken);

    public Task<ControlOperationResult> PrepareForLowPowerAsync(
        long expectedGeneration,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        return PrepareForLowPowerCoreAsync(expectedGeneration, stillAuthorized, cancellationToken);
    }

    private async Task<ControlOperationResult> PrepareForLowPowerCoreAsync(
        long generation,
        Func<bool>? stillAuthorized,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        CloseThermalConvergenceAdmission(
            ThermalConvergenceReason.LowPower,
            thermalConvergenceShutdown: false);
        bool activeBoundary = lowPowerGate.IsActiveForGeneration(generation);
        bool StillPreparing() => lowPowerGate.IsActiveForGeneration(generation)
            && stillAuthorized?.Invoke() != false;
        try
        {
            if (stillAuthorized is not null && !StillPreparing())
            {
                return new(ControlOperationStatus.Blocked,
                    "The original suspend preparation is stale or no longer authorized.");
            }
            ControlOperationResult? lightingResult = activeBoundary
                ? await TurnOffLightingForLowPowerAsync(generation, StillPreparing, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (activeBoundary && !StillPreparing())
            {
                return lightingResult ?? new(ControlOperationStatus.Stored,
                    "The suspend boundary ended before low-power cleanup.");
            }
            ControlOperationResult hyperBoostResult =
                await RestoreOwnedHyperBoostForLifecycleCoreAsync(
                        "low power",
                        cancellationToken,
                        activeBoundary ? StillPreparing : null,
                        lowPowerResumeGeneration: activeBoundary ? generation : null)
                    .ConfigureAwait(false);

            CancellationToken cleanupToken = stillAuthorized is null && cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            ControlOperationResult driverModeResult = await ReleaseDriverModeAsync(
                    preserveLightingIntent: true,
                    reasonLabel: "low power",
                    cleanupToken,
                    lowPowerGeneration: generation,
                    captureResumeState: !activeBoundary,
                    stillAuthorized: stillAuthorized)
                .ConfigureAwait(false);
            ControlOperationResult cleanupResult = CombineLowPowerCleanupResults(
                hyperBoostResult,
                coolingPadFanResult: null,
                coolingPadLightingResult: null,
                driverModeResult);
            return lightingResult is null ? cleanupResult : new ControlOperationResult(
                CombineCleanupStatus(lightingResult, cleanupResult),
                $"{lightingResult.Message} {cleanupResult.Message}");
        }
        finally
        {
            if (activeBoundary)
            {
                await RetireLightingRendererForSuspendAsync(generation).ConfigureAwait(false);
            }
        }
    }

    private async Task<ControlOperationResult> TurnOffLightingForLowPowerAsync(
        long generation,
        Func<bool> stillPreparing,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(LowPowerLightingBudget);
        bool acquired = false;
        try
        {
            await operationGate.WaitAsync(bounded.Token).ConfigureAwait(false);
            acquired = true;
            if (!stillPreparing()
                || lastLightingOffSuspendGeneration == generation)
            {
                return new(ControlOperationStatus.Stored,
                    "Keyboard standby preparation is stale or was already attempted.");
            }
            lastLightingOffSuspendGeneration = generation;
            ServiceStatusSnapshot current = GetStatus();
            ConfirmedDeviceState before = current.ConfirmedState ?? UnknownConfirmedState;
            // Capture the active baseline before a successful Off becomes the
            // confirmed state. Off is temporary hardware state, not user intent.
            CaptureResumeState(generation);
            if (IsKeyboardLightingDelegated || pendingWriteConfirmations.Blocks(DeviceSubsystem.Lighting))
            {
                return new(ControlOperationStatus.Stored,
                    "Keyboard standby Off was skipped because lighting authority is unavailable.");
            }
            RefreshControllerPresenceCore(bounded.Token);
            current = GetStatus();
            if (!current.DeviceVerified || current.WritesBlocked
                || current.ControllerDetectionWarning is not null
                || current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
            {
                return new(ControlOperationStatus.Blocked,
                    "Keyboard standby Off was blocked by device or controller admission.");
            }
            LightingSettings? owned = lastOwnedLightingSettings;
            LightingLowPowerOffResult? result = await deviceProvider
                .TurnOffRetainedLightingForLowPowerAsync(
                    stillPreparing, bounded.Token)
                .ConfigureAwait(false);
            if (result is null)
            {
                return new(ControlOperationStatus.Stored,
                    "No retained keyboard lighting port was available for standby Off.");
            }
            if (owned is not null && IsExplicitLightingOwnership(result.OwnershipBefore)
                && OwnershipMatches(result.OwnershipBefore, owned)
                && (!pendingLightingRestoreFromLifecycle || pendingLightingRestore is null))
            {
                pendingLightingRestore = owned;
                pendingLightingRestoreFromLifecycle = true;
            }
            // The dedicated operation retires the renderer. Preserve its
            // candidate now; later invalidation must not infer ownership from Off.
            lastOwnedLightingSettings = null;
            explicitLightingOwnershipActive = false;
            faultedLightingRecoveryCandidate = null;
            ConfirmedDeviceState? confirmed = current.ConfirmedState;
            ControlOperationStatus outcomeStatus;
            string message;
            if (result.Outcome == LightingLowPowerOffOutcome.ConfirmedOff)
            {
                confirmed = before with { Lighting = LightingSettingsFactory.Off() };
                driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: true);
                outcomeStatus = ControlOperationStatus.Applied;
                message = "Keyboard lighting Off was confirmed for standby.";
            }
            else
            {
                outcomeStatus = result.Outcome == LightingLowPowerOffOutcome.Indeterminate
                    ? ControlOperationStatus.Failed : ControlOperationStatus.Blocked;
                message = $"Keyboard standby Off was not confirmed: {result.Message}";
                if (result.Outcome == LightingLowPowerOffOutcome.Indeterminate)
                {
                    DesiredDeviceState target = Automation.Resolve(before);
                    target = target with
                    {
                        Lighting = target.Lighting with { Value = LightingSettingsFactory.Off() },
                    };
                    SetPendingWriteConfirmation(DeviceSubsystem.Lighting,
                        new PendingWriteConfirmation(current.ActivePowerSource, target, before));
                }
            }
            history.RecordSummary(outcomeStatus, message);
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                DesiredState = Automation.Resolve(confirmed ?? UnknownConfirmedState),
                LightingRendererOwnership = LightingRendererOwnershipState.None,
                ExternalChanges = driftTracker.ExternalChanges,
                Message = message,
            });
            return new(outcomeStatus, message);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            return new(ControlOperationStatus.Blocked,
                "Keyboard standby Off exhausted its preparation budget before confirmation.");
        }
        catch (Exception exception)
        {
            return new(ControlOperationStatus.Failed,
                $"Keyboard standby Off could not be prepared: {exception.Message}");
        }
        finally
        {
            if (acquired)
            {
                operationGate.Release();
            }
        }
    }

    public async Task BeginResumeValidationAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        CloseThermalConvergenceAdmission(
            ThermalConvergenceReason.LowPower,
            thermalConvergenceShutdown: false);
        thermalDeviceAuthorityInvalidated = true;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Resume begins a new hardware-enumeration session. Restore the bounded
            // MCU inventory budget without turning ordinary healthy observations
            // into an unbounded retry loop.
            pendingResumePerformanceDrift = null;
            AdvanceFanAuthorityEpoch();
            mcuVersionReadAttempts = 0;
            bool gpuClockOffsetConfirmationInvalidated = false;
            if (deviceProvider is IGpuClockOffsetConfirmationInvalidator invalidator)
            {
                gpuClockOffsetConfirmationInvalidated =
                    await invalidator.InvalidateGpuClockOffsetConfirmationAsync(
                        cancellationToken).ConfigureAwait(false);
            }

            resumeStateValidation.Begin(gpuClockOffsetConfirmationInvalidated,
                unconfirmedWrites: pendingWriteConfirmations.SnapshotSubsystems());
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void CaptureResumeState(long? generation = null)
    {
        // Every caller holds operationGate so cached state and pending-write
        // attribution describe the same boundary without another device read.
        ServiceStatusSnapshot current = GetStatus();
        resumeStateValidation.Capture(
            current.ActivePowerSource,
            current.DeviceVerified ? current.ConfirmedState : null,
            lowPowerGeneration: generation,
            unconfirmedWrites: pendingWriteConfirmations.SnapshotSubsystems());
    }

    private void RebaseLowPowerResumeState(long? generation)
    {
        if (generation is { } capturedGeneration)
        {
            // Called while thermal cleanup still holds operationGate, including
            // cancellation after confirmed restoration. The tracker admits only
            // the matching, unstarted baseline; this performs no device access.
            ServiceStatusSnapshot current = GetStatus();
            resumeStateValidation.RebaseBeforeResume(
                capturedGeneration,
                current.ActivePowerSource,
                current.DeviceVerified ? current.ConfirmedState : null,
                unconfirmedWrites: pendingWriteConfirmations.SnapshotSubsystems());
        }
    }

    public async Task CancelResumeValidationAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            resumeStateValidation.Cancel();
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
            RecordResumeValidationResult(resumeStateValidation.Complete());
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ControlOperationResult> ReleaseDriverModeAsync(
        bool preserveLightingIntent,
        string reasonLabel,
        CancellationToken cancellationToken,
        long? lowPowerGeneration = null,
        bool captureResumeState = true,
        Func<bool>? stillAuthorized = null)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (preserveLightingIntent && captureResumeState)
        {
            CaptureResumeState();
        }
        try
        {
            if (lowPowerGeneration is { } initialGeneration
                && (lowPowerGate.Generation != initialGeneration
                    || stillAuthorized?.Invoke() == false))
            {
                return new(ControlOperationStatus.Stored,
                    "The low-power boundary changed before Driver-mode cleanup.");
            }
            ControlOperationResult? brightnessResult = displayBrightnessReleaseRequired
                ? await ReleaseDisplayBrightnessUsageCoreAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;
            ControlOperationResult driverModeResult = await ReleaseDriverModeCoreAsync(
                reasonLabel,
                cancellationToken,
                lowPowerGeneration,
                stillAuthorized).ConfigureAwait(false);
            return CombineDisplayBrightnessCleanup(brightnessResult, driverModeResult);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task RetireLightingRendererForSuspendAsync(long generation)
    {
        bool StillRetiring() => lowPowerGate.IsActiveForGeneration(generation);
        if (!StillRetiring())
        {
            return;
        }
        // Firmware authorization may already have expired. This independent,
        // bounded cleanup may only retire the old renderer without any writes.
        using var bounded = new CancellationTokenSource(LowPowerLightingBudget);
        bool acquired = false;
        try
        {
            await operationGate.WaitAsync(bounded.Token).ConfigureAwait(false);
            acquired = true;
            if (!StillRetiring())
            {
                return;
            }
            if (lastLightingOffSuspendGeneration != generation)
            {
                CaptureResumeState(generation);
            }
            await InvalidateLightingRendererCoreAsync(
                preserveForLifecycleRestore: true, bounded.Token,
                lowPower: true, stillRetiring: StillRetiring).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            history.RecordSummary(ControlOperationStatus.Failed,
                "Write-free standby renderer disposal remains pending after its cleanup budget.");
        }
        finally
        {
            if (acquired)
            {
                operationGate.Release();
            }
        }
    }

    private async Task<ControlOperationResult> ReleaseDriverModeCoreAsync(
        string reasonLabel,
        CancellationToken cancellationToken,
        long? lowPowerGeneration = null,
        Func<bool>? stillAuthorized = null)
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

            bool? active;
            if (lowPowerGeneration is { } generation)
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(LowPowerLightingBudget);
                active = await deviceProvider.ReleaseRetainedDriverModeForLowPowerAsync(
                    () => lowPowerGate.Generation == generation
                        && stillAuthorized?.Invoke() != false,
                    bounded.Token).ConfigureAwait(false);
            }
            else if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
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

            else
            {
                active = await device.ReleaseDriverModeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
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
        if (ShutdownFanHandbackBlockReason(requireControllerClear: false) is { } initialBlock)
        {
            history.RecordSummary(ControlOperationStatus.Stored, initialBlock);
            return new ControlOperationResult(ControlOperationStatus.Stored, initialBlock);
        }

        history.RecordSummary(
            ControlOperationStatus.Stored,
            "Attempting to restore firmware-managed Automatic fan control before shutdown.");
        await RefreshControllerPresenceAndRelinquishLightingAsync(
            cancellationToken).ConfigureAwait(false);
        if (ShutdownFanHandbackBlockReason(requireControllerClear: true) is { } refreshedBlock)
        {
            history.RecordSummary(ControlOperationStatus.Stored, refreshedBlock);
            return new ControlOperationResult(ControlOperationStatus.Stored, refreshedBlock);
        }

        ServiceStatusSnapshot current = GetStatus();
        FanControlSettings automatic = FanControlSettings.FromLegacy(FanMode.Automatic);
        ControlOperationResult result = await ApplyScopedIntentAsync(
            DeviceSubsystem.Fans,
            current.ActivePowerSource,
            (coordinator, _, token) => coordinator.ApplyRecoveryFanControlOutcomeAsync(
                automatic,
                token),
            cancellationToken,
            state => state with
            {
                Fans = state.Fans with { Value = FanMode.Automatic },
                FanControl = automatic,
            },
            thermalExecutionOptions: new ScopedThermalExecutionOptions(
                [DeviceSubsystem.Fans],
                ScopedThermalFreshSessionRetryPolicy.BlockDuringBoundedCleanup,
                () => RefreshAndGetShutdownFanHandbackBlockReason(
                    cancellationToken),
                ScopedThermalAuthorityPurpose.PriorAuthorityCleanup)).ConfigureAwait(false);
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

    private string? RefreshAndGetShutdownFanHandbackBlockReason(
        CancellationToken cancellationToken)
    {
        try
        {
            RefreshControllerPresenceCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return "Manual fan control was left unchanged because other Razer software could not be checked.";
        }

        return ShutdownFanHandbackBlockReason(requireControllerClear: true);
    }

    private string? ShutdownFanHandbackBlockReason(bool requireControllerClear)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (current.ExternalChanges.Contains(DeviceSubsystem.Fans))
        {
            return "Fan control was left unchanged because OpenBlade no longer owns the confirmed fan state.";
        }

        FanMode? confirmedFanMode = current.ConfirmedState?.Fans;
        if (confirmedFanMode is null
            || !DeviceSubsystemOrder.IsManualFanMode(confirmedFanMode.Value))
        {
            return confirmedFanMode == FanMode.Automatic
                ? "Fan control already uses firmware-managed Automatic mode."
                : "Fan control was left unchanged because an OpenBlade-owned manual state was not confirmed.";
        }

        if (requireControllerClear)
        {
            if (current.ControllerDetectionWarning is not null)
            {
                return "Manual fan control was left unchanged because other Razer software could not be checked.";
            }

            if (current.PotentialControllers.Any(CompetingControllerProcesses.Contains))
            {
                return "Manual fan control was left unchanged because Razer Synapse is active.";
            }
        }

        if (exactFanControlOwnership is not
            {
                Disposition: ExactFanControlOwnershipDisposition.OwnedExactIntent,
                Settings: { } ownedFanControl,
                AuthorityEpoch: var ownedAuthorityEpoch,
            }
            || !DeviceSubsystemOrder.IsManualFanMode(ownedFanControl.Mode)
            || ownedAuthorityEpoch != fanAuthorityEpoch)
        {
            return "Manual fan control was left unchanged because OpenBlade did not establish ownership of the current state.";
        }

        return null;
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

    public async Task InvalidateLightingRendererForLowPowerAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InvalidateLightingRendererCoreAsync(
                preserveForLifecycleRestore: true, cancellationToken, lowPower: true)
                .ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        bool lowPower = false,
        Func<bool>? stillRetiring = null)
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
                await (lowPower
                    ? deviceProvider.InvalidateLightingRendererForLowPowerAsync(
                        stillRetiring ?? (() => true), cancellationToken)
                    : deviceProvider.InvalidateLightingRendererAsync(cancellationToken))
                    .ConfigureAwait(false);
            if (stillRetiring?.Invoke() == false)
            {
                return;
            }
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
                if (!pendingLightingRestoreFromLifecycle || pendingLightingRestore is null)
                {
                    pendingLightingRestore = owned;
                    pendingLightingRestoreFromLifecycle = true;
                }
            }
            else if (preserveForLifecycleRestore
                && !(lowPower && pendingLightingRestoreFromLifecycle
                    && pendingLightingRestore is not null)
                && (liveOwnership == LightingRendererOwnershipState.Faulted
                    || owned is not null))
            {
                // A live fault, relinquished renderer, or ownership mismatch
                // disproves the cached status and must cancel any stale restore.
                // Write-free retirement cannot disprove separately accepted
                // lifecycle intent; retaining it does not bypass port admission.
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
        if (lowPowerGate.IsActive)
        {
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lowPowerGate.IsActive)
            {
                return;
            }

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
        if (lowPowerGate.IsActive || pendingWriteConfirmations.Blocks(DeviceSubsystem.Lighting))
        {
            return false;
        }
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

        resumeStateValidation.RecordLightingRestore(
            outcome.Result.Status == ControlResultStatus.Applied);

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
        thermalConvergenceArbiter.Dispose();
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
        Action? afterApply = null,
        bool allowFanOwnershipRearm = false,
        Func<bool>? stillControlsSession = null,
        Func<DesiredDeviceState, IReadOnlyList<DeviceSubsystem>>?
            thermalComponentResolver = null)
    {
        EnsureInitialized();
        await operationGate.WaitAsync(token);
        try
        {
            if (subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans
                && !ConfigurationIsAdmitted)
            {
                return ConfigurationQuarantineBlocked(subsystem);
            }

            if (stillControlsSession?.Invoke() == false)
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before the control request started.");
            }

            if (subsystem == DeviceSubsystem.Lighting && lowPowerGate.IsActive)
            {
                try
                {
                    await mutation(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsIntentFailure(exception))
                {
                    return RecordRejectedIntent(exception);
                }
                return DeferLightingIntentForLowPower();
            }

            if (pendingWriteConfirmations.Blocks(subsystem))
            {
                return PendingWriteBlockedResult(subsystem);
            }

            PowerSource powerSource = await ResolveCurrentPowerSourceAsync(token);
            await RefreshControllerPresenceAndRelinquishLightingAsync(token)
                .ConfigureAwait(false);
            if (stillControlsSession?.Invoke() == false)
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed during control admission.");
            }

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
            if (stillControlsSession?.Invoke() == false)
            {
                return SessionAuthorityChanged(
                    "The active Windows session changed before the control intent was stored.");
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
                token,
                allowFanOwnershipRearm: allowFanOwnershipRearm,
                stillControlsSession: stillControlsSession,
                thermalExecutionOptions: thermalComponentResolver is null
                    ? null
                    : new ScopedThermalExecutionOptions(
                        thermalComponentResolver.Invoke(
                            ResolveHardwareDesired(
                                GetStatus().ConfirmedState
                                    ?? UnknownConfirmedState))));
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
            if (pendingWriteConfirmations.Blocks(DeviceSubsystem.Battery))
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
                },
                pendingBatteryIntentCommit: new(Automation, FullChargeOnly: false));
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
        Func<CancellationToken, Task>? commitIntent = null,
        bool allowFanOwnershipRearm = false,
        Func<bool>? stillControlsSession = null,
        ScopedThermalExecutionOptions? thermalExecutionOptions = null,
        Action<
            ScopedApplyOutcome,
            ConfirmedDeviceState?,
            ThermalTransitionTrace?>? observeScopedOutcome = null,
        PendingBatteryIntentCommit? pendingBatteryIntentCommit = null,
        bool gpuClockTuningOnly = false)
    {
        if (subsystem == DeviceSubsystem.Lighting && lowPowerGate.IsActive)
        {
            return DeferLightingIntentForLowPower();
        }
        if (subsystem == DeviceSubsystem.Performance)
        {
            thermalExecutionOptions ??= new ScopedThermalExecutionOptions(
                [DeviceSubsystem.Performance]);
        }
        thermalExecutionOptions?.ValidateFor(subsystem);
        ScopedThermalAuthorityPurpose authorityPurpose =
            thermalExecutionOptions?.AuthorityPurpose
            ?? ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent;
        if (subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans
            && ConfigurationThermalWriteBlockReason(authorityPurpose) is not null)
        {
            return ConfigurationQuarantineBlocked(subsystem);
        }

        if (stillControlsSession?.Invoke() == false)
        {
            return PublishScopedUnavailable(
                subsystem,
                powerSource,
                "The active Windows session changed before the control write started.",
                preserveCurrentAvailability: true);
        }

        if (pendingWriteConfirmations.Blocks(subsystem))
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

        ScopedIntentApplyExecution execution = await ApplyScopedIntentOutcomeAsync(
            subsystem,
            powerSource,
            current,
            cachedConfirmed,
            device,
            desired,
            apply,
            token,
            stageDesired,
            stillControlsSession,
            thermalExecutionOptions);
        ScopedApplyOutcome outcome = execution.Outcome;
        RecordConfirmedResumeWrites(subsystem, outcome);
        bool lightingExternalBeforeReadback =
            driftTracker.ExternalChanges.Contains(DeviceSubsystem.Lighting);
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
                retired = (await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "A confirmed inactive observation relinquished HyperBoost ownership, but its journal could not be retired.")
                    .ConfigureAwait(false)).Retired;
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostTransitionId = null;
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
            pendingWriteConfirmations.Remove(subsystem);

            // A scoped lighting write whose readback mismatch is only a lost
            // renderer ownership or a firmware Off/unknown reading, with no
            // competing controller detected, is a renderer fault or hardware
            // behavior rather than an external change; it must not latch. A
            // full readback of a different, non-Off effect is still positive
            // evidence and must still latch.
            bool skipUnattributedLightingLatch =
                subsystem == DeviceSubsystem.Lighting
                && !IsLightingReadbackEvidenceOfExternalChange(
                    outcome.Readback is null ? null : confirmed,
                    desired.Lighting.Value);
            if (skipUnattributedLightingLatch)
            {
                // Observe() above may have flagged the Off delta of our own
                // write, and nothing downstream re-confirms a subsystem whose
                // result was reported Failed, so resolve it here. A latch that
                // predates this write is kept: a write that did not take has
                // not corrected anything.
                if (!lightingExternalBeforeReadback)
                {
                    driftTracker.ConfirmIntentional(subsystem, matched: true);
                }
            }
            else
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }

        if (subsystem == DeviceSubsystem.Fans)
        {
            UpdateOwnedFanControlSettings(
                desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                outcome.Result,
                outcome.WriteAttempted,
                outcome.ReadbackMatched,
                allowFanOwnershipRearm,
                outcome.WriteSubmissionState,
                outcome.ConfirmationUnavailable);
        }

        observeScopedOutcome?.Invoke(
            outcome,
            confirmed,
            execution.ThermalTrace);

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

        bool reconciliationUnavailable = execution.ThermalTrace?.Disposition
                == ThermalTransitionDisposition.ReconciliationUnavailable
            || execution.ThermalTrace is null && outcome.ConfirmationUnavailable;
        if (reconciliationUnavailable
            && outcome.PreWriteState is { } preWriteState)
        {
            SetPendingWriteConfirmation(subsystem, new PendingWriteConfirmation(
                powerSource,
                outcome.EffectiveDesired ?? desired,
                preWriteState,
                BatteryIntentCommit: pendingBatteryIntentCommit,
                GpuClockTuningOnly: gpuClockTuningOnly));
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

    private void RecordConfirmedResumeWrites(
        DeviceSubsystem subsystem,
        ScopedApplyOutcome outcome)
    {
        // The hardware outcome is authoritative even if a later ownership or
        // persistence check fails. Matching a no-write read is not attribution.
        if (outcome.WriteAttempted
            && (outcome.ReadbackMatched
                || outcome.Result.Status == ControlResultStatus.Applied))
        {
            resumeStateValidation.RecordConfirmedWrite(subsystem);
        }
        if (outcome.ConfirmedCoupledWrites is { } coupledWrites)
        {
            foreach (DeviceSubsystem coupled in coupledWrites)
            {
                resumeStateValidation.RecordConfirmedWrite(coupled);
            }
        }
    }

    private void RecordConfirmedResumeWrites(FullTransitionApplyOutcome outcome)
    {
        foreach (DeviceSubsystem subsystem in outcome.WriteAttempted)
        {
            if (outcome.ReadbackMatched.Contains(subsystem)
                || ResultFor(outcome.Results, subsystem).Status == ControlResultStatus.Applied)
            {
                resumeStateValidation.RecordConfirmedWrite(subsystem);
            }
        }
    }

    private async Task<ScopedIntentApplyExecution> ApplyScopedIntentOutcomeAsync(
        DeviceSubsystem subsystem,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? cachedConfirmed,
        IBladeDevice device,
        DesiredDeviceState desired,
        Func<ControlCoordinator, DesiredDeviceState, CancellationToken, Task<ScopedApplyOutcome>> apply,
        CancellationToken token,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        Func<bool>? stillControlsSession,
        ScopedThermalExecutionOptions? thermalExecutionOptions)
    {
        try
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                ScopedThermalExecutionOptions options = thermalExecutionOptions
                    ?? throw new InvalidOperationException(
                        "Scoped performance execution options are unavailable.");
                ThermalTransitionExecution thermal = await ThermalTransitionExecutor
                    .ExecuteScopedPerformanceAsync(
                        device,
                        async (attemptDevice, cancellationToken) =>
                        {
                            var coordinator = new ControlCoordinator(
                                attemptDevice,
                                attemptDevice.ReadPerformanceFanStateAsync,
                                resolveIndeterminatePerformanceWrites: true,
                                resolveIndeterminateAutomaticFanWrites: true,
                                singleAttemptPerformanceWrites: true,
                                performanceWriteBlockReason: () =>
                                     ScopedPerformanceWriteBlockReason(
                                         desired,
                                         stageDesired,
                                         stillControlsSession,
                                         options.AdditionalWriteBlockReason,
                                         options.AuthorityPurpose));
                            return await apply(
                                    coordinator,
                                    desired,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        },
                        (_, cancellationToken) =>
                            RevalidateScopedThermalRetryWithPolicyAsync(
                                powerSource,
                                desired,
                                stageDesired,
                                stillControlsSession,
                                options,
                                ThermalPreWriteRetryDelay,
                                cancellationToken),
                        token).ConfigureAwait(false);
                return new ScopedIntentApplyExecution(thermal.Outcome, thermal.Trace);
            }

            if (subsystem == DeviceSubsystem.Fans && thermalExecutionOptions is not null)
            {
                ScopedThermalExecutionOptions options = thermalExecutionOptions;
                ThermalTransitionExecution thermal = await ThermalTransitionExecutor
                    .ExecuteAsync(
                        options.Components,
                        device,
                        (attemptDevice, runner) => new ControlCoordinator(
                            attemptDevice,
                            attemptDevice.ReadPerformanceFanStateAsync,
                            resolveIndeterminatePerformanceWrites: true,
                            resolveIndeterminateAutomaticFanWrites: true,
                            singleAttemptPerformanceWrites: true,
                            singleAttemptFanWrites: true,
                            thermalStepRunner: runner,
                            performanceWriteBlockReason: () =>
                                     ScopedThermalWriteBlockReason(
                                    desired,
                                    stageDesired,
                                         stillControlsSession,
                                         options.Components,
                                         options.AdditionalWriteBlockReason,
                                         options.AuthorityPurpose),
                            fanWriteBlockReason: () =>
                                     ScopedThermalWriteBlockReason(
                                    desired,
                                    stageDesired,
                                         stillControlsSession,
                                         options.Components,
                                         options.AdditionalWriteBlockReason,
                                         options.AuthorityPurpose)),
                        (runner, cancellationToken) => apply(
                            runner.CreateRecipeCoordinator(),
                            desired,
                            cancellationToken),
                        (_, cancellationToken) =>
                            RevalidateScopedThermalRetryWithPolicyAsync(
                                powerSource,
                                desired,
                                stageDesired,
                                stillControlsSession,
                                options,
                                ThermalPreWriteRetryDelay,
                                cancellationToken),
                        token).ConfigureAwait(false);
                return new ScopedIntentApplyExecution(thermal.Outcome, thermal.Trace);
            }

            ControlCoordinator coordinator = subsystem is
                DeviceSubsystem.Fans
                    ? new ControlCoordinator(
                        device,
                        device.ReadPerformanceFanStateAsync,
                        resolveIndeterminatePerformanceWrites: true,
                        resolveIndeterminateAutomaticFanWrites: true)
                    : new ControlCoordinator(device);
            return new ScopedIntentApplyExecution(
                await apply(coordinator, desired, token));
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

    private Task<ThermalRetryAdmission>
        RevalidateScopedThermalRetryWithPolicyAsync(
        PowerSource expectedPowerSource,
        DesiredDeviceState expectedDesired,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        Func<bool>? stillControlsSession,
        ScopedThermalExecutionOptions options,
        TimeSpan recoveryDelay,
        CancellationToken cancellationToken) =>
        options.FreshSessionRetryPolicy ==
            ScopedThermalFreshSessionRetryPolicy.Allow
            ? RevalidateScopedThermalRetryAsync(
                expectedPowerSource,
                expectedDesired,
                stageDesired,
                stillControlsSession,
                options.Components,
                recoveryDelay,
                cancellationToken,
                options.AdditionalWriteBlockReason,
                options.AuthorityPurpose)
            : Task.FromResult(ThermalRetryAdmission.Block(
                "Fresh-session retry is unavailable during bounded shutdown cleanup."));

    private async Task<ThermalRetryAdmission>
        RevalidateScopedThermalRetryAsync(
        PowerSource expectedPowerSource,
        DesiredDeviceState expectedDesired,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        Func<bool>? stillControlsSession,
        IReadOnlyList<DeviceSubsystem> components,
        TimeSpan recoveryDelay,
        CancellationToken cancellationToken,
        Func<string?>? additionalThermalWriteBlockReason = null,
        ScopedThermalAuthorityPurpose authorityPurpose =
            ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConfigurationThermalWriteBlockReason(authorityPurpose) is { } quarantineBlock)
        {
            return ThermalRetryAdmission.Block(quarantineBlock);
        }
        if (stillControlsSession?.Invoke() == false)
        {
            return ThermalRetryAdmission.Block(
                "The active Windows session changed before the fresh-session retry.");
        }

        if (lowPowerGate.IsActive)
        {
            return ThermalRetryAdmission.Block(
                "The laptop entered a low-power transition before the fresh-session retry.");
        }

        if (pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans))
        {
            return ThermalRetryAdmission.Block(
                "A thermal write requires read-only reconciliation before another submission.");
        }

        ServiceStatusSnapshot current = GetStatus();
        if (hyperBoostJournalSafetyBlock
            || current is { DeviceVerified: true, WritesBlocked: true })
        {
            return ThermalRetryAdmission.Block(
                "Thermal writes became blocked before the fresh-session retry.");
        }

        // A failed thermal exchange can leave the EC busy for the protocol
        // timeout. Preserve the established settling window, but keep its
        // cancellation inside the executor's already-consumed retry lease.
        if (recoveryDelay > TimeSpan.Zero)
        {
            await transitionRetryDelay(
                    recoveryDelay,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stillControlsSession?.Invoke() == false)
        {
            return ThermalRetryAdmission.Block(
                "The active Windows session changed while waiting for the fresh-session retry.");
        }

        current = GetStatus();
        if (lowPowerGate.IsActive
            || hyperBoostJournalSafetyBlock
            || current is { DeviceVerified: true, WritesBlocked: true }
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans))
        {
            return ThermalRetryAdmission.Block(
                "Thermal write admission closed while waiting for the fresh-session retry.");
        }

        PowerSource confirmedPowerSource;
        try
        {
            confirmedPowerSource = await ResolveCurrentPowerSourceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            // Controller presence is advisory for an explicit Strict write.
            // Refresh only the read-only observation here; retry admission
            // must not trigger unrelated lighting handback.
            RefreshControllerPresenceCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ThermalRetryAdmission.Block(
                $"Read-only retry admission failed: {exception.Message}");
        }

        if (confirmedPowerSource != expectedPowerSource
            || confirmedPowerSource == PowerSource.ExternalUnknown)
        {
            return ThermalRetryAdmission.Block(
                "The confirmed power destination changed before the fresh-session retry.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (stillControlsSession?.Invoke() == false)
        {
            return ThermalRetryAdmission.Block(
                "The active Windows session changed during retry admission.");
        }

        current = GetStatus();
        if (lowPowerGate.IsActive
            || hyperBoostJournalSafetyBlock
            || current is { DeviceVerified: true, WritesBlocked: true }
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans))
        {
            return ThermalRetryAdmission.Block(
                "Thermal write admission closed before the fresh-session retry.");
        }

        if (!ScopedThermalIntentMatchesCurrent(
                expectedDesired,
                stageDesired,
                components))
        {
            return ThermalRetryAdmission.Block(
                "The authoritative thermal intent changed before the fresh-session retry.");
        }

        string? additionalBlock = additionalThermalWriteBlockReason?.Invoke();
        if (additionalBlock is not null)
        {
            return ThermalRetryAdmission.Block(additionalBlock);
        }

        try
        {
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice retryDevice))
            {
                return ThermalRetryAdmission.Block(
                    "The exact laptop identity could not be re-admitted for the fresh-session retry.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ThermalRetryAdmission.Allow(retryDevice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ThermalRetryAdmission.Block(
                $"Exact-device retry admission failed: {exception.Message}");
        }
    }

    private string? ScopedPerformanceWriteBlockReason(
        DesiredDeviceState expected,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        Func<bool>? stillControlsSession,
        Func<string?>? additionalWriteBlockReason,
        ScopedThermalAuthorityPurpose authorityPurpose)
    {
        if (ConfigurationThermalWriteBlockReason(authorityPurpose) is { } quarantineBlock)
        {
            return quarantineBlock;
        }
        if (stillControlsSession?.Invoke() == false)
        {
            return "The active Windows session changed before the performance command was submitted.";
        }

        if (lowPowerGate.IsActive)
        {
            return "The laptop entered a low-power transition before the performance command was submitted.";
        }

        if (pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans))
        {
            return "A thermal write requires read-only reconciliation before the performance command can be submitted.";
        }

        ServiceStatusSnapshot current = GetStatus();
        if (hyperBoostJournalSafetyBlock
            || current is { DeviceVerified: true, WritesBlocked: true })
        {
            return "Thermal write authority closed before the performance command was submitted.";
        }

        return ScopedPerformanceIntentMatchesCurrent(expected, stageDesired)
            ? additionalWriteBlockReason?.Invoke()
            : "The authoritative performance intent changed before the command was submitted.";
    }

    private string? ScopedThermalWriteBlockReason(
        DesiredDeviceState expected,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        Func<bool>? stillControlsSession,
        IReadOnlyList<DeviceSubsystem> components,
        Func<string?>? additionalThermalWriteBlockReason = null,
        ScopedThermalAuthorityPurpose authorityPurpose =
            ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent)
    {
        if (ConfigurationThermalWriteBlockReason(authorityPurpose) is { } quarantineBlock)
        {
            return quarantineBlock;
        }
        if (stillControlsSession?.Invoke() == false)
        {
            return "The active Windows session changed before the thermal command was submitted.";
        }
        if (lowPowerGate.IsActive)
        {
            return "The laptop entered a low-power transition before the thermal command was submitted.";
        }
        if (pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            || pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans))
        {
            return "A thermal write requires read-only reconciliation before another command can be submitted.";
        }

        ServiceStatusSnapshot current = GetStatus();
        if (hyperBoostJournalSafetyBlock
            || current is { DeviceVerified: true, WritesBlocked: true })
        {
            return "Thermal write authority closed before the command was submitted.";
        }

        if (!ScopedThermalIntentMatchesCurrent(expected, stageDesired, components))
        {
            return "The authoritative thermal intent changed before the command was submitted.";
        }

        return additionalThermalWriteBlockReason?.Invoke();
    }

    private bool ScopedPerformanceIntentMatchesCurrent(
        DesiredDeviceState expected,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired)
    {
        ServiceStatusSnapshot current = GetStatus();
        DesiredDeviceState latest = ResolveHardwareDesired(
            current.ConfirmedState ?? UnknownConfirmedState);
        if (stageDesired is not null)
        {
            latest = stageDesired(latest);
        }

        return latest.Performance == expected.Performance
            && latest.CustomPerformance == expected.CustomPerformance
            && latest.GpuClockTuning == expected.GpuClockTuning;
    }

    private bool ScopedThermalIntentMatchesCurrent(
        DesiredDeviceState expected,
        Func<DesiredDeviceState, DesiredDeviceState>? stageDesired,
        IReadOnlyList<DeviceSubsystem> components)
    {
        ServiceStatusSnapshot current = GetStatus();
        DesiredDeviceState latest = ResolveHardwareDesired(
            current.ConfirmedState ?? UnknownConfirmedState);
        if (stageDesired is not null)
        {
            latest = stageDesired(latest);
        }

        bool performanceMatches = !components.Contains(DeviceSubsystem.Performance)
            || latest.Performance == expected.Performance
                && latest.CustomPerformance == expected.CustomPerformance
                && latest.GpuClockTuning == expected.GpuClockTuning;
        bool fansMatch = !components.Contains(DeviceSubsystem.Fans)
            || latest.Fans == expected.Fans
                && latest.FanControl == expected.FanControl;
        return performanceMatches && fansMatch;
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
            if (requested.Effect != LightingEffect.Off
                && !ControllerMayOwnLighting(GetStatus()))
            {
                // A renderer fault with no competing controller: let the bounded
                // recovery retry the effect that was just requested, not the
                // effect owned before it.
                faultedLightingRecoveryCandidate = requested;
            }
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
                writesBlocked: outcome.DeviceAccessFailed,
                observationReason: DeviceObservationReason.None);
            throw;
        }
        catch (ConfigurationQuarantinedException)
        {
            return ConfigurationQuarantineBlocked(subsystem);
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
                writesBlocked: outcome.DeviceAccessFailed,
                observationReason: DeviceObservationReason.None);
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
            pendingWriteConfirmations.Remove(subsystem);
            driftTracker.ConfirmIntentional(subsystem, matched: true);
        }
        else if (outcome.Result.Status == ControlResultStatus.Applied)
        {
            pendingWriteConfirmations.Remove(subsystem);
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
                ? current.WritesBlocked || pendingWriteConfirmations.HasPending
                : outcome.DeviceAccessFailed || pendingWriteConfirmations.HasPending,
            observationReason: !outcome.DeviceAccessFailed
                && !outcome.ConfirmationUnavailable
                && (outcome.Readback is not null || outcome.PreWriteState is not null)
                    ? DeviceObservationReason.None
                    : null);
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

        ServiceStatusSnapshot prior = GetStatus();
        bool controllerPreviouslyUnsafe = FanControllerAdmissionUnsafe(prior);
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
        bool controllerNowUnsafe = observation.DetectionWarning is not null
            || potentialControllers.Any(CompetingControllerProcesses.Contains);
        if (!controllerPreviouslyUnsafe && controllerNowUnsafe)
        {
            AdvanceFanAuthorityEpoch();
        }
        if (controllerNowUnsafe)
        {
            CloseThermalConvergenceAdmission(
                observation.DetectionWarning is null
                    ? ThermalConvergenceReason.CompetingControllerPresent
                    : ThermalConvergenceReason.CompetingControllerUncertain,
                thermalConvergenceShutdown: false);
        }
        PublishIfChanged(GetStatus() with
        {
            PotentialControllers = potentialControllers,
            ControllerDetectionWarning = observation.DetectionWarning,
            HyperBoostBlockingControllers = observation.HyperBoostBlockingControllers?
                .Where(controller => potentialControllers.Contains(
                    controller, StringComparer.OrdinalIgnoreCase))
                .Select(controller => potentialControllers.First(name =>
                    name.Equals(controller, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(controller => controller, StringComparer.Ordinal)
                .Take(16)
                .ToArray(),
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
            ObservationReason = DeviceObservationReason.None,
            DesiredState = destinationIntent,
            ExternalChanges = driftTracker.ExternalChanges,
            WritesBlocked = pendingWriteConfirmations.HasPending
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
                    powerSource,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ControlOperationResult result = await SetHyperBoostCoreAsync(
                enabled: false,
                transitionId: ownedHyperBoostTransitionId,
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
            PowerSource powerSource,
            CancellationToken cancellationToken)
    {
        HyperBoostRestoreIntent restoreIntent = !ConfigurationIsAdmitted
            ? ownedHyperBoostRestoreIntent
                ?? HyperBoostRestoreIntent.BalancedAutomatic
            : CreateHyperBoostRestoreIntent(destinationIntent);
        PerformancePreset baseMode = ownedHyperBoostBaseMode
            ?? PerformancePreset.Balanced;
        bool requiresCoolingPad = ownedHyperBoostRequiresCoolingPad;
        HyperBoostOwnershipJournalLoadResult journalLoad;
        try
        {
            journalLoad = await hyperBoostOwnershipJournalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            hyperBoostJournalSafetyBlock = true;
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"Firmware exited HyperBoost at the power-source boundary, but its ownership journal could not be inspected: {exception.Message}");
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message =
                    "Firmware exited HyperBoost at the power-source boundary, but its ownership journal could not be inspected. Control writes remain blocked.",
            });
            return false;
        }

        if (journalLoad.Invalid
            || journalLoad.Journal is not
            {
                State: HyperBoostOwnershipJournalState.OwnedConfirmed,
            } ownedJournal
            || ownedJournal.BaseMode != baseMode
            || ownedJournal.RequiresCoolingPad != requiresCoolingPad
            || ownedHyperBoostRestoreIntent is null
            || ownedJournal.RestoreIntent != ownedHyperBoostRestoreIntent)
        {
            hyperBoostJournalSafetyBlock = true;
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = confirmed,
                WritesBlocked = true,
                Message =
                    "Firmware exited HyperBoost at the power-source boundary, but its exact owned journal is unavailable. Control writes remain blocked.",
            });
            return false;
        }

        var preparedJournal = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.RestorePending,
            baseMode,
            requiresCoolingPad,
            timeProvider.GetUtcNow())
        {
            RestoreIntent = restoreIntent,
            TransitionId = ownedJournal.TransitionId,
        };
        HyperBoostOwnershipJournalMutationResult journalPreparation =
            await TryReplaceHyperBoostOwnershipJournalAsync(
                    ownedJournal,
                    journalLoad.Sha256,
                    preparedJournal,
                    "Firmware exited HyperBoost at the power-source boundary, but the destination restore journal could not be prepared.")
                .ConfigureAwait(false);
        if (!journalPreparation.Applied)
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

        // Keep the global RestorePending latch asserted. Only the exact runner
        // below receives the narrow journal-cleanup admission exception.
        hyperBoostRestorePending = true;

        // The durable destination intent now protects every following outcome.
        // Disarm automatic reconciliation before any restoration write so an
        // indeterminate submission is never retried.
        pendingOwnedHyperBoostPowerBoundaryExit = false;

        ServiceStatusSnapshot current = GetStatus();
        if (!current.DeviceVerified
            || pendingWriteConfirmations.HasPending
            || current.ControllerDetectionWarning is not null
            || RazerThermalControllerPolicy.GetBlockingControllers(current).Count > 0)
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

        long transitionOwnershipGeneration = hyperBoostOwnershipGeneration;
        long transitionPowerGeneration = powerSourceGeneration;
        ThermalTransitionStepRunner transitionRunner =
            CreateHyperBoostTransitionRunner(
                device,
                enabled: false,
                baseMode,
                restoreIntent,
                preparedJournal,
                journalPreparation.Sha256,
                transitionOwnershipGeneration,
                transitionPowerGeneration,
                powerSource,
                ownedLifecycleRestore: false,
                allowOwnedJournalCleanup: true,
                validationOnly: false,
                authorityPurpose: !ConfigurationIsAdmitted
                    ? ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
                    : ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent,
                allowIntentRebase: false);
        HyperBoostIntentRestorationResult restoration;
        try
        {
            restoration = await RestoreHyperBoostIntentAsync(
                    transitionRunner.CurrentDevice,
                    restoreIntent,
                    confirmed,
                    cancellationToken,
                    transitionRunner: transitionRunner)
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
            FailedHyperBoostRestoreObservation failureObservation =
                await CaptureFailedHyperBoostRestoreObservationAsync(
                        transitionRunner.CurrentDevice,
                        restoreIntent,
                        restoration,
                        restored)
                    .ConfigureAwait(false);
            restored = failureObservation.State;
            HyperBoostRestoreRetryDisposition retryDisposition =
                restoration.DeferredRetryEligible
                && failureObservation.RecoveryBaseline is not null
                    ? HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    : HyperBoostRestoreRetryDisposition.None;
            HyperBoostOwnershipJournal failedJournal = preparedJournal with
            {
                UpdatedAtUtc = timeProvider.GetUtcNow(),
                RecoveryBaseline = failureObservation.RecoveryBaseline,
                RestoreRetryDisposition = retryDisposition,
                RestoreRetryTarget = retryDisposition
                    == HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                        ? HyperBoostRestoreRetryTarget.CurrentPowerDestination
                        : HyperBoostRestoreRetryTarget.None,
            };
            await TryReplaceHyperBoostOwnershipJournalAsync(
                    preparedJournal,
                    journalPreparation.Sha256,
                    failedJournal,
                    "The failed HyperBoost power-boundary restore could not preserve its fresh recovery baseline.")
                .ConfigureAwait(false);
            hyperBoostJournalSafetyBlock = true;
            string failure =
                $"Firmware exited HyperBoost, but the destination performance or fan intent was not fully restored. {restoration.Message} The pending restore journal was preserved and no immediate write will be retried."
                + (retryDisposition
                        == HyperBoostRestoreRetryDisposition.DeferredPreSubmission
                    ? " One deferred retry is permitted only after a fresh matching exact-device, power-source, and controller observation."
                    : string.Empty);
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
        bool journalCleared = await RetireHyperBoostOwnershipJournalAsync(
                preparedJournal.BaseMode,
                preparedJournal.RequiresCoolingPad,
                restoreIntent,
                "Firmware exited HyperBoost and the destination intent was restored, but its pending journal could not be retired.",
                preparedJournal,
                journalPreparation.Sha256)
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
        bool preserveObservedExternalChanges = true,
        Func<bool>? stillControlsSession = null,
        PendingTransitionContinuation? continuation = null)
    {
        if (lowPowerGate.IsActive)
        {
            _ = DeferLightingIntentForLowPower();
            return;
        }
        pendingTransitionContinuation = null;
        ServiceStatusSnapshot current = GetStatus();
        if (hyperBoostJournalSafetyBlock && !ownsHyperBoost)
        {
            LiveReadings? retainedLiveReadings = preserveLiveReadings
                ? current.LiveReadings
                : null;
            await ObserveCoreAsync(
                    PowerSourceForStatus(powerSource),
                    powerSource != PowerSource.ExternalUnknown,
                    requestedLiveReadings: LiveReadingSensors.None,
                    batteryPowerFlowMilliwatts: null,
                    token)
                .ConfigureAwait(false);
            if (preserveLiveReadings)
            {
                PublishIfChanged(GetStatus() with
                {
                    LiveReadings = retainedLiveReadings,
                });
            }
            return;
        }

        if (powerSource is PowerSource.Battery or PowerSource.UsbC
            && ownsHyperBoost
            && current.ActivePowerSource != powerSource
            && !ownedHyperBoostPowerBoundaryExitSubmitted)
        {
            pendingOwnedHyperBoostPowerBoundaryExit = true;
        }
        ConfirmedDeviceState? cachedConfirmed = current.ConfirmedState;
        DesiredDeviceState? priorDesired = continuation?.PriorDesired ?? current.DesiredState;
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

        bool hyperBoostBoundaryWorkRequired =
            powerSource is PowerSource.Battery or PowerSource.UsbC
            && ownsHyperBoost
            && pendingOwnedHyperBoostPowerBoundaryExit;
        if (!await ExitOwnedHyperBoostForPowerSourceTransitionAsync(
                device,
                powerSource,
                token)
            .ConfigureAwait(false))
        {
            return;
        }

        if (hyperBoostBoundaryWorkRequired)
        {
            // The typed HyperBoost executor may have renewed the verified
            // device session for its one pre-submission retry. Do not continue
            // the wider power transition through the superseded instance.
            try
            {
                if (!deviceProvider.TryGetVerifiedDevice(out device))
                {
                    PublishFullUnavailable(
                        powerSource,
                        "The verified device session changed after HyperBoost restoration.");
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
                    $"Verified device re-admission failed after HyperBoost restoration: {exception.Message}",
                    failed: true);
                return;
            }
        }

        current = GetStatus();
        cachedConfirmed = current.ConfirmedState;
        if (stillControlsSession?.Invoke() == false)
        {
            return;
        }

        DesiredDeviceState desired = ResolveHardwareDesired(
            cachedConfirmed ?? UnknownConfirmedState);
        ThermalFullTransitionExecution transitionExecution =
            await ApplyDesiredTransitionWithRetryAsync(
                device,
                desired,
                priorDesired,
                powerSource,
                current,
                cachedConfirmed,
                preserveObservedExternalChanges,
                token,
                stillControlsSession,
                continuation);
        FullTransitionApplyOutcome outcome = transitionExecution.Outcome;
        if (outcome.WriteAttempted.Contains(DeviceSubsystem.Performance)
            || outcome.ConfirmationUnavailable?.Contains(DeviceSubsystem.Performance) == true)
        {
            pendingResumePerformanceDrift = null;
        }
        RecordConfirmedResumeWrites(outcome);
        device = transitionExecution.Device;

        if (outcome.DeferredNotSubmitted is { Count: > 0 } deferred
            && outcome.PreWriteState is { } deferredBaseline)
        {
            pendingTransitionContinuation = new(
                Automation,
                powerSourceGeneration,
                powerSource,
                desired,
                continuation?.PriorDesired ?? priorDesired ?? desired,
                outcome.EffectiveDesired ?? desired,
                deferredBaseline,
                new HashSet<DeviceSubsystem>(deferred),
                new HashSet<DeviceSubsystem>(outcome.WriteAttempted.Concat(
                    continuation?.Dependencies ?? new HashSet<DeviceSubsystem>())),
                continuation?.ExpiresAt ?? timeProvider.GetUtcNow().AddMinutes(2));
        }

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
        bool lightingExternalBeforeReadback =
            driftTracker.ExternalChanges.Contains(DeviceSubsystem.Lighting);
        if (outcome.Readback is not null)
        {
            driftTracker.Observe(outcome.Readback);
            confirmed = outcome.Readback;
        }

        string? laptopBehaviorMessage = continuation is not null
            || current.WritesBlocked
            || outcome.ConfirmationUnavailable?.Count > 0
            ? null
            : await ApplyProfileLaptopBehaviorAsync(
                device,
                powerSource,
                outcome.Capabilities ?? current.Capabilities,
                token).ConfigureAwait(false);

        LightingRendererOwnershipState observedLightingOwnership = current.LightingRendererOwnership;
        if (continuation is null || continuation.Subsystems.Contains(DeviceSubsystem.Lighting))
        {
            (outcome, observedLightingOwnership) = await ReconcileFullTransitionLightingAsync(
                    device,
                    desired,
                    outcome,
                    token).ConfigureAwait(false);
        }

        await PublishFullTransitionOutcomeAsync(
            desired,
            outcome,
            observedLightingOwnership,
            powerSource,
            confirmed,
            current.Capabilities,
            laptopBehaviorMessage,
            preserveLiveReadings,
            lightingExternalBeforeReadback,
            token,
            continuation?.Subsystems).ConfigureAwait(false);
    }

    private bool ShouldRetryPendingOwnedHyperBoostPowerBoundaryExit(
        PowerSource powerSource) =>
        pendingOwnedHyperBoostPowerBoundaryExit
        && ownsHyperBoost
        && powerSource is PowerSource.Battery or PowerSource.UsbC;

    private async Task<ThermalFullTransitionExecution>
        ApplyDesiredTransitionWithRetryAsync(
        IBladeDevice device,
        DesiredDeviceState desired,
        DesiredDeviceState? priorDesired,
        PowerSource powerSource,
        ServiceStatusSnapshot current,
        ConfirmedDeviceState? cachedConfirmed,
        bool preserveObservedExternalChanges,
        CancellationToken token,
        Func<bool>? stillControlsSession = null,
        PendingTransitionContinuation? continuation = null)
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

        Func<IBladeDevice, CancellationToken,
            Task<PowerTransitionDeviceStateReadResult>>?
            transitionStateReader = ShouldConfirmTransientLightingOff()
                ? static (attemptDevice, cancellationToken) =>
                    attemptDevice.ReadStateForPowerTransitionAsync(cancellationToken)
                : null;

        IReadOnlySet<DeviceSubsystem> ResolveAutomaticWriteExclusions(
            ConfirmedDeviceState confirmed)
        {
            if (preserveObservedExternalChanges)
            {
                bool performanceWasExternal = driftTracker.ExternalChanges.Contains(
                    DeviceSubsystem.Performance);
                ObserveAutomaticState(confirmed, priorDesired, desired);
                if (!performanceWasExternal
                    && driftTracker.ExternalChanges.Contains(DeviceSubsystem.Performance)
                    && resumePowerSourceTransitionGeneration == powerSourceGeneration
                    && current.ActivePowerSource != powerSource
                    && priorDesired is not null
                    && confirmed.Performance is not null
                    && !PerformanceMatchesDesired(confirmed, priorDesired)
                    && !PerformanceMatchesDesired(confirmed, desired)
                    && !pendingWriteConfirmations.HasPending
                    && !confirmed.HyperBoostActive
                    && current.PotentialControllers.Count == 0
                    && current.ControllerDetectionWarning is null)
                {
                    // Firmware can settle into the destination mode after the
                    // first read on resume. Keep writes paused until a fresh
                    // observation proves that the destination took effect.
                    pendingResumePerformanceDrift = new(
                        powerSourceGeneration,
                        powerSource,
                        desired,
                        timeProvider.GetUtcNow().Add(
                            ResumeStateValidationTracker.ObservationWindow));
                }
            }
            else
            {
                driftTracker.Observe(confirmed);
            }
            var exclusions = new HashSet<DeviceSubsystem>(
                preserveObservedExternalChanges
                    ? GetCoupledAutomaticWriteExclusions()
                    : pendingWriteConfirmations.GetWriteExclusions());
            if (confirmed.HyperBoostActive)
            {
                // Owned Battery and USB-C transitions exit HyperBoost before
                // reaching this resolver. Preserve every other active session:
                // unowned state is advisory-only, and full-power transitions do
                // not issue an implicit thermal write.
                exclusions.Add(DeviceSubsystem.Performance);
                exclusions.Add(DeviceSubsystem.Fans);
            }
            if (!ConfigurationIsAdmitted)
            {
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
            DeviceSubsystem[] thermalComponents =
                [DeviceSubsystem.Performance, DeviceSubsystem.Fans];
            return await ThermalTransitionExecutor.ExecuteFullTransitionAsync(
                    thermalComponents,
                    device,
                    (attemptDevice, runner) => new ControlCoordinator(
                        attemptDevice,
                        scopedStateReader: null,
                        resolveIndeterminatePerformanceWrites: false,
                        resolveIndeterminateAutomaticFanWrites: false,
                        singleAttemptPerformanceWrites: true,
                        singleAttemptFanWrites: true,
                        thermalStepRunner: runner,
                        performanceWriteBlockReason: () =>
                            ScopedThermalWriteBlockReason(
                                 desired,
                                 stageDesired: null,
                                 stillControlsSession,
                                 thermalComponents),
                        fanWriteBlockReason: () =>
                            ScopedThermalWriteBlockReason(
                                 desired,
                                 stageDesired: null,
                                 stillControlsSession,
                                 thermalComponents)),
                    (coordinator, cancellationToken) => coordinator
                        .ApplyDesiredStateOutcomeAsync(
                            desired,
                            cancellationToken,
                            automaticWriteExclusionResolver:
                                ResolveAutomaticWriteExclusions,
                            deviceStateReader: transitionStateReader,
                            continuationScopeResolver: continuation is null
                                ? null
                                : (attemptDevice, confirmed) =>
                                    IsCurrentTransitionContinuation(continuation, confirmed)
                                    && TransitionDependenciesMatch(continuation, confirmed, attemptDevice)
                                        ? continuation.Subsystems
                                        : new HashSet<DeviceSubsystem>()),
                    (_, cancellationToken) => RevalidateScopedThermalRetryAsync(
                        powerSource,
                         desired,
                         stageDesired: null,
                         stillControlsSession,
                        thermalComponents,
                        PowerTransitionAccessRetryDelay,
                        cancellationToken),
                    (_, cancellationToken) => RevalidateScopedThermalRetryAsync(
                        powerSource,
                         desired,
                         stageDesired: null,
                         stillControlsSession,
                        thermalComponents,
                        ThermalPreWriteRetryDelay,
                        cancellationToken),
                    token).ConfigureAwait(false);
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
            if (outcome.Readback is { } readback
                && IsLightingOffExplainedByDisplayOff(readback))
            {
                // The lid can stop the renderer immediately after our write.
                // Retain the destination effect for the bounded display-on retry
                // even though no current renderer ownership remains.
                if (desired.Lighting.Value.Effect != LightingEffect.Off)
                {
                    faultedLightingRecoveryCandidate = desired.Lighting.Value;
                }
                return (outcome, observedLightingOwnership);
            }

            lightingResult = new ControlResult(
                ControlResultStatus.Failed,
                "Keyboard lighting did not remain active after the profile change.");
            var matched = new HashSet<DeviceSubsystem>(outcome.ReadbackMatched);
            matched.Remove(DeviceSubsystem.Lighting);
            var contradicted = new HashSet<DeviceSubsystem>(outcome.ReadbackContradicted);
            if (ControllerMayOwnLighting(GetStatus()))
            {
                // A competing controller may genuinely have taken the renderer;
                // that is positive evidence of an external change.
                contradicted.Add(DeviceSubsystem.Lighting);
            }
            else if (desired.Lighting.Value.Effect != LightingEffect.Off)
            {
                // Without a controller this is a renderer fault or hardware
                // behavior, not evidence of an external change.
                // PublishFullTransitionOutcomeAsync leaves the drift latch
                // alone for it, and the bounded renderer recovery reapplies the
                // destination effect once on a later observation. The candidate
                // is set explicitly because a Faulted observation clears the
                // owned snapshot that recovery would otherwise start from.
                faultedLightingRecoveryCandidate = desired.Lighting.Value;
            }
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
        bool lightingExternalBeforeReadback,
        CancellationToken token,
        IReadOnlySet<DeviceSubsystem>? continuationScope = null)
    {
        bool hyperBoostJournalRetired = true;
        if (confirmed is { HyperBoostActive: false })
        {
            hyperBoostJournalRetired =
                (await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "HyperBoost was confirmed inactive, but its ownership journal could not be retired.")
                    .ConfigureAwait(false)).Retired;
            if (ownsHyperBoost || ownedHyperBoostBaseMode is not null)
            {
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostTransitionId = null;
            ownedHyperBoostRequiresCoolingPad = false;
        }

        IReadOnlySet<DeviceSubsystem> confirmationUnavailable =
            outcome.ConfirmationUnavailable ?? new HashSet<DeviceSubsystem>();
        foreach (DeviceSubsystem subsystem in DeviceSubsystemOrder.Default)
        {
            if (continuationScope is not null && !continuationScope.Contains(subsystem))
            {
                continue;
            }
            if (outcome.ReadbackMatched.Contains(subsystem))
            {
                pendingWriteConfirmations.Remove(subsystem);
                driftTracker.ConfirmIntentional(subsystem, matched: true);
            }
            else if (ResultFor(outcome.Results, subsystem).Status == ControlResultStatus.Applied)
            {
                pendingWriteConfirmations.Remove(subsystem);
                driftTracker.AcknowledgeIntentionalWithoutReadback(subsystem);
            }
            else if (outcome.WriteAttempted.Contains(subsystem)
                && !confirmationUnavailable.Contains(subsystem))
            {
                pendingWriteConfirmations.Remove(subsystem);
                // Only the final readback of this transition can be evidence;
                // the cached pre-write state that stands in for it when the
                // final read failed describes what the write replaced, not what
                // another controller did.
                if (subsystem == DeviceSubsystem.Lighting
                    && !IsLightingReadbackEvidenceOfExternalChange(
                        outcome.Readback,
                        desired.Lighting.Value))
                {
                    // An attempted write that ends here without a contradicting
                    // readback (a stopped renderer, a lost ownership check, an
                    // Off or unreadable final state) carries no evidence of
                    // another controller. Leave the latch as it was before this
                    // transition; do not add one, and do not clear one that
                    // predates it.
                    if (!lightingExternalBeforeReadback)
                    {
                        driftTracker.ConfirmIntentional(subsystem, matched: true);
                    }
                }
                else
                {
                    driftTracker.ConfirmIntentional(subsystem, matched: false);
                }
            }
            else if (outcome.ReadbackContradicted.Contains(subsystem))
            {
                pendingWriteConfirmations.Remove(subsystem);
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }

        if (continuationScope is null || continuationScope.Contains(DeviceSubsystem.Fans))
        {
            UpdateOwnedFanControlSettings(
                desired.FanControl
                    ?? FanControlSettings.FromLegacy(desired.Fans.Value),
                ResultFor(outcome.Results, DeviceSubsystem.Fans),
                outcome.WriteAttempted.Contains(DeviceSubsystem.Fans),
                outcome.ReadbackMatched.Contains(DeviceSubsystem.Fans),
                confirmationUnavailable: confirmationUnavailable.Contains(
                    DeviceSubsystem.Fans));
        }

        ControlResult lightingResult = ResultFor(
            outcome.Results,
            DeviceSubsystem.Lighting);
        if (continuationScope is null || continuationScope.Contains(DeviceSubsystem.Lighting))
        {
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
                || pendingWriteConfirmations.HasPending
                || !hyperBoostJournalRetired,
            observationReason: !outcome.DeviceAccessFailed
                && confirmationUnavailable.Count == 0
                && (outcome.Readback is not null || outcome.PreWriteState is not null)
                    ? DeviceObservationReason.None
                    : null);
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
        if (cached is null)
        {
            return readback;
        }

        return subsystem switch
        {
            DeviceSubsystem.Performance or DeviceSubsystem.Fans =>
                cached with
                {
                    Performance = readback.Performance,
                    Fans = readback.Fans,
                    Battery = readback.Battery ?? cached.Battery,
                    Lighting = readback.Lighting ?? cached.Lighting,
                    HyperBoostActive = readback.HyperBoostActive,
                    CustomPerformance = readback.CustomPerformance,
                    GpuClockOffsets = readback.GpuClockOffsets,
                    LogoLighting = readback.LogoLighting ?? cached.LogoLighting,
                },
            DeviceSubsystem.Lighting => cached with
            {
                Lighting = readback.Lighting,
                LogoLighting = readback.LogoLighting,
            },
            _ => readback,
        };
    }

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
                AdapterPowerState.ExperimentalExternal
                    when deviceProvider.IsExperimentalDevice => PowerSource.PluggedIn,
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
            // An unclassified adapter cannot resolve a pending submission. Defer
            // only its battery drift comparison; publish the actual readback and
            // keep the pending write block, existing latches, and other drift.
            ConfirmedDeviceState driftObservation = !powerSourceConfirmed
                && pendingWriteConfirmations.Blocks(DeviceSubsystem.Battery)
                    ? observation.Confirmed with { Battery = null }
                    : observation.Confirmed;
            if (startupProfileReconciliationPending
                && (!unexpectedExitRecoveryPending
                    || unexpectedExitRecoveryDeferredForUserConfiguration))
            {
                // A first sample has no preceding OpenBlade observation, so it
                // cannot prove an external change. Establish a baseline; the
                // startup reconciliation path will then apply saved intent.
                driftTracker.Observe(driftObservation);
            }
            else
            {
                ObserveAutomaticState(driftObservation);
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
            ReconcileResumePerformanceDrift(
                observation.Confirmed,
                powerSource,
                powerSourceConfirmed);
            bool suppressInactiveHyperBoostJournalRetirement = false;
            string? pendingHyperBoostMessage = null;
            if (powerSourceConfirmed)
            {
                ThermalTransitionStepRunner? reconciliationRunner =
                    await CreateDelayedHyperBoostReconciliationRunnerAsync(
                        device,
                        powerSource,
                        cancellationToken)
                    .ConfigureAwait(false);
                IndeterminateHyperBoostReconciliation entry =
                    await ReconcileIndeterminateHyperBoostEntryAsync(
                            observation.Confirmed,
                            powerSource,
                            device,
                            cancellationToken,
                            reconciliationRunner)
                        .ConfigureAwait(false);
                observation = observation with { Confirmed = entry.Confirmed };
                IndeterminateHyperBoostReconciliation exit =
                    await ReconcileIndeterminateHyperBoostExitAsync(
                            observation.Confirmed,
                            powerSource,
                            device,
                            cancellationToken,
                            reconciliationRunner)
                        .ConfigureAwait(false);
                observation = observation with { Confirmed = exit.Confirmed };
                if (entry.Resolution != IndeterminateHyperBoostResolution.None
                    || exit.Resolution != IndeterminateHyperBoostResolution.None)
                {
                    pendingIndeterminateHyperBoostRecoveryState = null;
                }
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
            string? journalRetirementMessage = null;
            if (!observation.Confirmed.HyperBoostActive
                && !suppressInactiveHyperBoostJournalRetirement)
            {
                InactiveHyperBoostJournalRetirement retirement =
                    await RetireInactiveHyperBoostJournalIfPresentAsync(
                        "A fresh inactive HyperBoost observation relinquished ownership, but its journal could not be retired.",
                        observation.Confirmed,
                        device,
                        powerSource,
                        powerSourceConfirmed,
                        cancellationToken)
                    .ConfigureAwait(false);
                journalRetired = retirement.Retired;
                journalRetirementMessage = retirement.Message;
                if (retirement.ConfirmedState is { } restored)
                {
                    observation = observation with { Confirmed = restored };
                    pendingHyperBoostMessage = retirement.Message
                        ?? pendingHyperBoostMessage;
                }
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
                    Message = journalRetirementMessage
                        ?? "HyperBoost is inactive, but its ownership journal could not be retired. Control writes remain blocked.",
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

            // Status can retain the previous source while adapter classification
            // is unavailable; that presentation is not resume-comparison evidence.
            if (powerSourceConfirmed)
            {
                RecordResumeValidationResult(resumeStateValidation.Observe(
                    powerSource,
                    observation.Confirmed));
            }
            await EvaluateThermalConvergenceObservationAsync(
                    powerSource,
                    powerSourceConfirmed,
                    observation.Confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
            await ContinueDeferredTransitionAsync(
                    powerSource,
                    powerSourceConfirmed,
                    observation.Confirmed,
                    device,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private bool IsCurrentTransitionContinuation(
        PendingTransitionContinuation pending,
        ConfirmedDeviceState confirmed) =>
        ReferenceEquals(pending.Owner, Automation)
        && pending.PowerGeneration == powerSourceGeneration
        && pending.PowerSource == Automation.PowerSource
        && timeProvider.GetUtcNow() < pending.ExpiresAt
        && pending.RequestedDesired == ResolveHardwareDesired(confirmed);

    private static bool TransitionDependenciesMatch(
        PendingTransitionContinuation pending,
        ConfirmedDeviceState confirmed,
        IBladeDevice device) => TransitionDependencyResult(pending, confirmed, device)
            == PendingConfirmationResult.Match;

    private static PendingConfirmationResult TransitionDependencyResult(
        PendingTransitionContinuation pending,
        ConfirmedDeviceState confirmed,
        IBladeDevice device)
    {
        bool unavailable = false;
        foreach (DeviceSubsystem subsystem in pending.Dependencies)
        {
            PendingConfirmationResult result = MatchWriteConfirmation(
                subsystem,
                new WriteConfirmationExpectation(
                    pending.EffectiveDesired,
                    pending.PreWriteState),
                confirmed,
                device);
            if (result == PendingConfirmationResult.Mismatch)
            {
                return result;
            }
            unavailable |= result == PendingConfirmationResult.Unavailable;
        }
        return unavailable ? PendingConfirmationResult.Unavailable : PendingConfirmationResult.Match;
    }

    private async Task ContinueDeferredTransitionAsync(
        PowerSource powerSource,
        bool powerSourceConfirmed,
        ConfirmedDeviceState confirmed,
        IBladeDevice device,
        CancellationToken cancellationToken)
    {
        if (pendingTransitionContinuation is not { } pending)
        {
            return;
        }
        if (!IsCurrentTransitionContinuation(pending, confirmed))
        {
            pendingTransitionContinuation = null;
            return;
        }
        if (TransitionDependencyResult(pending, confirmed, device)
            == PendingConfirmationResult.Mismatch)
        {
            pendingTransitionContinuation = null;
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!powerSourceConfirmed
            || powerSource != pending.PowerSource
            || lowPowerGate.IsActive
            || !ConfigurationIsAdmitted
            || !current.DeviceVerified
            || current.WritesBlocked
            || current.ControlAccess != SessionControlAccess.Active
            || current.PotentialControllers.Count > 0
            || current.ControllerDetectionWarning is not null
            || pendingWriteConfirmations.HasPending
            || !TransitionDependenciesMatch(pending, confirmed, device))
        {
            return;
        }

        await RefreshControllerPresenceAndRelinquishLightingAsync(cancellationToken)
            .ConfigureAwait(false);
        PowerSource freshPower = await ResolveCurrentPowerSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if (freshPower != pending.PowerSource
            || GetStatus().WritesBlocked
            || GetStatus().PotentialControllers.Count > 0
            || GetStatus().ControllerDetectionWarning is not null)
        {
            return;
        }

        // Consume before execution. Only steps proven never submitted are eligible;
        // the coordinator rechecks dependencies on its fresh pre-write read. Any
        // new ambiguity follows ordinary pending confirmation, never setter replay.
        pendingTransitionContinuation = null;
        await ApplyFullTransitionAsync(
                freshPower,
                cancellationToken,
                preserveLiveReadings: true,
                stillControlsSession: () => ReferenceEquals(pending.Owner, Automation)
                    && pending.PowerGeneration == powerSourceGeneration
                    && !lowPowerGate.IsActive,
                continuation: pending)
            .ConfigureAwait(false);
    }

    private void RecordResumeValidationResult(ResumeStateValidationResult? result)
    {
        if (result is not null)
        {
            history.RecordResumeValidation(result.Status, result.Message);
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
        (FanRpmReadings? fans, FanTemperatureReadings temperatures) =
            await ReadOptionalLiveSensorsAsync(
                device,
                includeLiveReadings ? requestedLiveReadings : LiveReadingSensors.None,
                cancellationToken)
                .ConfigureAwait(false);

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
        if (observation.Confirmed.HyperBoostActive
            && current.ConfirmedState?.HyperBoostActive != true
            && !ownsHyperBoost
            && exactFanControlOwnership.Disposition ==
                ExactFanControlOwnershipDisposition.OwnedExactIntent)
        {
            // An external HyperBoost interval can transiently force a
            // composite performance/fan state without changing generic manual
            // readback. Its end must not revive exact RPM authority from the
            // pre-HyperBoost epoch. OpenBlade-owned lifecycle transitions keep
            // their direct journaled restoration path.
            AdvanceFanAuthorityEpoch();
        }
        if (!observation.Confirmed.HyperBoostActive)
        {
            if (ownsHyperBoost || ownedHyperBoostBaseMode is not null)
            {
                hyperBoostOwnershipGeneration++;
            }
            ownsHyperBoost = false;
            ownedHyperBoostBaseMode = null;
            ownedHyperBoostRestoreIntent = null;
            ownedHyperBoostTransitionId = null;
            ownedHyperBoostRequiresCoolingPad = false;
        }
        ClassifyObservedFanTopology(observation.Confirmed.Fans);

        PublishIfChanged(current with
        {
            DeviceVerified = true,
            WritesBlocked = !powerSourceConfirmed || hyperBoostJournalSafetyBlock,
            ObservationReason = !powerSourceConfirmed && powerSource != PowerSource.ExternalUnknown
                ? DeviceObservationReason.RetainedPowerSource
                : DeviceObservationReason.None,
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
            BatteryPolicyOverride = Automation.BatteryPolicyOverride,
            BatteryProtectionOverride = Automation.BatteryProtectionOverride,
            Firmware = observation.Firmware,
            LightingRendererOwnership = observation.LightingOwnership,
            StartupAnimationEnabled = observation.StartupAnimation,
            FunctionKeyPrimary = observation.FunctionKeyPrimary,
            GamingModeEnabled = observation.GamingModeEnabled,
            BatteryHealth = batteryHealth,
            IsExperimentalDevice = deviceProvider.IsExperimentalDevice,
            ExperimentalSupportSource = deviceProvider.ExperimentalSupportSource,
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
        (FanRpmReadings? fans, FanTemperatureReadings temperatures) =
            await ReadOptionalLiveSensorsAsync(device, requestedLiveReadings, cancellationToken)
                .ConfigureAwait(false);

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

    private async Task<(FanRpmReadings? Fans, FanTemperatureReadings Temperatures)>
        ReadOptionalLiveSensorsAsync(
            IBladeDevice device,
            LiveReadingSensors requestedLiveReadings,
            CancellationToken cancellationToken)
    {
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

        return (fans, temperatures);
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
            deferredRecoveredHyperBoostEntryIntent = null;
            AdvanceFanDesiredRevision();
            powerSourceGeneration++;
            pendingResumePerformanceDrift = null;
            resumePowerSourceTransitionGeneration = resumeStateValidation.ResumeStarted
                && !pendingWriteConfirmations.HasPending
                ? powerSourceGeneration : -1;
            InvalidatePendingWriteConfirmations(markExternal: true);
            explicitLightingOwnershipActive = false;
            lightingRecoveryPauseMessage = null;
            AdoptDisplayOffSuppressionForPowerSource();
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
            ObservationReason = current.ObservationReason == DeviceObservationReason.RetainedPowerSource
                ? DeviceObservationReason.None
                : current.ObservationReason,
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
        bool lightingExternalBeforeObservation =
            driftTracker.ExternalChanges.Contains(DeviceSubsystem.Lighting);
        driftTracker.Observe(confirmed);

        // Neither branch below re-examines Lighting once the initial comparison
        // has run (CompareInitialAutomaticValue/CompareTransitionAutomaticValue
        // only ever compare once), so a later periodic observation that reads an
        // unattributed Off must be un-latched here, since driftTracker.Observe
        // above may just have added it for the Off delta on full-readback
        // laptops.
        if (!lightingExternalBeforeObservation
            && driftTracker.ExternalChanges.Contains(DeviceSubsystem.Lighting)
            && IsUnattributedLightingOff(confirmed))
        {
            driftTracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: true);
        }

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
            if (IsUnattributedLightingOff(confirmed))
            {
                // A dark keyboard under a closed lid, in Modern Standby, or after
                // a renderer fault is what can happen without any outside actor;
                // firmware brightness zero with no competing controller detected
                // is not evidence that another controller replaced the profile
                // effect. It is equally not evidence that an earlier confirmed
                // external change was undone, so an existing latch is left in
                // place.
                initialAutomaticComparisons.Add(DeviceSubsystem.Lighting);
                if (!lightingExternalBeforeObservation)
                {
                    driftTracker.ConfirmIntentional(
                        DeviceSubsystem.Lighting,
                        matched: true);
                }
            }
            else
            {
                CompareTransitionAutomaticValue(
                    DeviceSubsystem.Lighting,
                    confirmed.Lighting,
                    desired.Lighting.Value,
                    alternateExpected.Lighting.Value);
            }
            if (confirmed.Battery is { } transitionBattery)
            {
                initialAutomaticComparisons.Add(DeviceSubsystem.Battery);
                bool matches = transitionBattery.TemporaryFullCharge is true
                    || WriteConfirmationPolicy.BatteryMatchesDesired(transitionBattery, desired.Battery)
                    || WriteConfirmationPolicy.BatteryMatchesDesired(
                        transitionBattery,
                        alternateExpected.Battery);
                driftTracker.ConfirmIntentional(DeviceSubsystem.Battery, matches);
            }

            return;
        }

        CompareInitialAutomaticPerformance(confirmed, desired);
        CompareInitialAutomaticFans(confirmed.Fans, desired.Fans.Value);
        if (IsUnattributedLightingOff(confirmed))
        {
            // Same reasoning as the transition branch: an unattributed Off is
            // not evidence of an external change, so it never enters the
            // comparison as a mismatch.
            initialAutomaticComparisons.Add(DeviceSubsystem.Lighting);
            if (!lightingExternalBeforeObservation)
            {
                driftTracker.ConfirmIntentional(
                    DeviceSubsystem.Lighting,
                    matched: true);
            }
        }
        else
        {
            CompareInitialAutomaticValue(
                DeviceSubsystem.Lighting,
                confirmed.Lighting,
                desired.Lighting.Value);
        }

        if (confirmed.Battery is { } battery
            && initialAutomaticComparisons.Add(DeviceSubsystem.Battery))
        {
            bool matches = battery.TemporaryFullCharge is true
                || WriteConfirmationPolicy.BatteryMatchesDesired(battery, desired.Battery);
            driftTracker.ConfirmIntentional(DeviceSubsystem.Battery, matches);
        }
    }

    private void LatchPendingHyperBoost(
        ServiceStatusSnapshot? baseline,
        bool enabled,
        PerformancePreset baseMode,
        HyperBoostOwnershipJournal expectedJournal,
        string expectedJournalSha256)
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
            pendingIndeterminateHyperBoostEntryTransitionId =
                expectedJournal.TransitionId;
        }
        pendingIndeterminateHyperBoostJournal = expectedJournal;
        pendingIndeterminateHyperBoostJournalSha256 = expectedJournalSha256;
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
        pendingWriteConfirmations.Set(subsystem, pending);
        // The caller holds operationGate. Retain attribution even if a later
        // observation resolves this write before resume validation begins.
        resumeStateValidation.RecordPendingWrite(subsystem);
    }

    private ControlOperationResult PendingWriteBlockedResult(DeviceSubsystem subsystem)
    {
        string message =
            $"{subsystem} was not written because OpenBlade must confirm the current laptop state first.";
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        PublishIfChanged(GetStatus() with { Message = message });
        return new ControlOperationResult(ControlOperationStatus.Blocked, message);
    }

    private ControlOperationResult SessionAuthorityChanged(string message)
    {
        var blocked = new ControlOperationResult(
            ControlOperationStatus.Blocked,
            message);
        history.RecordSummary(blocked.Status, blocked.Message);
        PublishIfChanged(GetStatus() with { Message = blocked.Message });
        return blocked;
    }

    private void InvalidatePendingWriteConfirmations(bool markExternal)
    {
        KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] pending =
            pendingWriteConfirmations.Clear();

        if (markExternal)
        {
            foreach ((DeviceSubsystem subsystem, _) in pending)
            {
                if (subsystem == DeviceSubsystem.Lighting
                    && !ControllerMayOwnLighting(GetStatus()))
                {
                    // An unconfirmed lighting write abandoned at a power-source
                    // change is not evidence that another controller acted; the
                    // transition that follows writes the destination effect.
                    continue;
                }

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
            pendingWriteConfirmations.Snapshot())
        {
            // A stale power source or battery intent owner must not query device
            // management state or let matching firmware recommit abandoned intent.
            GpuClockOffsetManagementState? managementState = pending.CanConfirm(powerSource, Automation)
                ? GetWriteConfirmationManagementState(subsystem, pending.Expectation, confirmed, device)
                : null;
            PendingConfirmationResult result = pending.Match(
                subsystem, confirmed, powerSource, Automation, managementState);
            if (result == PendingConfirmationResult.Unavailable)
            {
                continue;
            }

            if (result == PendingConfirmationResult.Mismatch
                && pendingTransitionContinuation?.Dependencies.Contains(subsystem) == true)
            {
                pendingTransitionContinuation = null;
            }

            if (result == PendingConfirmationResult.Match
                && pending.BatteryIntentCommit is { } batteryIntent)
            {
                // Only an explicit request carries this metadata. Restore its
                // in-memory intent before releasing the pending block; automatic
                // transitions must not create new overrides or replay setters.
                if (!batteryIntent.FullChargeOnly)
                {
                    Automation.CommitBatteryState(pending.Target.Battery);
                }
                else if (pending.Target.Battery.TemporaryFullCharge)
                {
                    Automation.EnableTemporaryFullCharge();
                }
                else
                {
                    Automation.CancelTemporaryFullCharge();
                }
            }

            // ObserveAutomaticState has already compared the old baseline with this
            // observation. Clear both latches on a causal match; retain the drift
            // latch on a mismatch. No write is scheduled from this path. A lighting
            // mismatch that rests only on a firmware Off or unknown reading (the
            // cross-power-source case reads nothing at all) with no competing
            // controller is not evidence of an external change and resolves as
            // matched.
            driftTracker.ConfirmIntentional(
                subsystem,
                matched: result == PendingConfirmationResult.Match
                    || (subsystem == DeviceSubsystem.Lighting
                        && confirmed.Lighting is null or { Effect: LightingEffect.Off }
                        && !ControllerMayOwnLighting(GetStatus())));
            pendingWriteConfirmations.Remove(subsystem);
        }
    }

    private static PendingConfirmationResult MatchWriteConfirmation(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed,
        IBladeDevice device)
    {
        return WriteConfirmationPolicy.Match(
            subsystem,
            expectation,
            confirmed,
            GetWriteConfirmationManagementState(subsystem, expectation, confirmed, device));
    }

    private static GpuClockOffsetManagementState? GetWriteConfirmationManagementState(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState confirmed,
        IBladeDevice device) =>
        WriteConfirmationPolicy.RequiresGpuClockOffsetManagementState(
            subsystem,
            expectation,
            confirmed)
        && device is IGpuClockOffsetManagementDevice managedDevice
            ? managedDevice.GpuClockOffsetManagementState
            : null;

    private IReadOnlySet<DeviceSubsystem> GetCoupledAutomaticWriteExclusions()
    {
        var exclusions = driftTracker.ExternalChanges.ToHashSet();
        if (preferences.AlwaysOverrideExternalLightingChanges)
        {
            exclusions.Remove(DeviceSubsystem.Lighting);
        }
        return pendingWriteConfirmations.GetWriteExclusions(exclusions);
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

    private void ReconcileResumePerformanceDrift(
        ConfirmedDeviceState confirmed,
        PowerSource powerSource,
        bool powerSourceConfirmed)
    {
        if (pendingResumePerformanceDrift is not { } pending)
        {
            return;
        }

        if (timeProvider.GetUtcNow() >= pending.ExpiresAt
            || pending.PowerGeneration != powerSourceGeneration
            || pending.PowerSource != powerSource
            || Automation.PowerSource != powerSource
            || lowPowerGate.IsActive
            || !driftTracker.ExternalChanges.Contains(DeviceSubsystem.Performance))
        {
            pendingResumePerformanceDrift = null;
            return;
        }

        // Ambiguous reads and competing-controller observations never revoke
        // an external-change latch. The bounded observation continues only
        // while the original power and performance intent still apply.
        if (!powerSourceConfirmed || pendingWriteConfirmations.HasPending)
        {
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (current.PotentialControllers.Count > 0
            || current.ControllerDetectionWarning is not null)
        {
            pendingResumePerformanceDrift = null;
            return;
        }

        DesiredDeviceState desired = ResolveHardwareDesired(confirmed);
        if (desired.Performance != pending.Desired.Performance
            || desired.CustomPerformance != pending.Desired.CustomPerformance
            || desired.GpuClockTuning != pending.Desired.GpuClockTuning)
        {
            pendingResumePerformanceDrift = null;
            return;
        }

        if (confirmed.Performance is null
            || confirmed.Performance == PerformancePreset.Custom
                && confirmed.CustomPerformance is null
            || desired.GpuClockTuning is not null
                && confirmed.GpuClockOffsets is null
            || !PerformanceMatchesDesired(confirmed, desired))
        {
            return;
        }

        pendingResumePerformanceDrift = null;
        driftTracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
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
        bool wasDeviceVerified = GetStatus().DeviceVerified;
        if (!deviceVerified)
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.DeviceUnavailable,
                thermalConvergenceShutdown: false);
            thermalDeviceAuthorityInvalidated = true;
            // A confirmed loss of the verified device ends the current hardware
            // session. A later re-verification receives a fresh bounded budget.
            mcuVersionReadAttempts = 0;
            if (wasDeviceVerified)
            {
                AdvanceFanAuthorityEpoch();
            }
        }

        ServiceStatusSnapshot current = GetStatus();

        ConfirmedDeviceState confirmed = current.ConfirmedState ?? UnknownConfirmedState;
        PublishIfChanged(current with
        {
            DeviceVerified = deviceVerified,
            WritesBlocked = true,
            ObservationReason = DeviceObservationReason.None,
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
            IsExperimentalDevice = deviceProvider.IsExperimentalDevice,
            ExperimentalSupportSource = deviceProvider.ExperimentalSupportSource,
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
            ObservationReason = DeviceObservationReason.CheckingConnection,
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
            ObservationReason = DeviceObservationReason.RefreshingControls,
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
            AllowExperimentalFeatures = preferences.AllowExperimentalFeatures,
            IsExperimentalDevice = deviceProvider.IsExperimentalDevice,
            ExperimentalSupportSource = deviceProvider.ExperimentalSupportSource,
            FanControlOverride = Automation.FanControlOverride,
            BatteryPolicyOverride = Automation.BatteryPolicyOverride,
            BatteryProtectionOverride = Automation.BatteryProtectionOverride,
            ThermalConvergence = new ThermalConvergenceStatus(
                ThermalControlPolicy.Strict,
                preferences.ThermalPolicyUpgradeDisclosure,
                ThermalConvergenceState.Paused,
                [],
                ThermalConvergenceReason.ConfigurationUnavailable,
                ExplicitReapplyRequired: false),
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
        bool writesBlocked,
        DeviceObservationReason? observationReason = null)
    {
        ServiceStatusSnapshot current = GetStatus();
        PublishIfChanged(current with
        {
            DeviceVerified = deviceVerified,
            WritesBlocked = writesBlocked || hyperBoostJournalSafetyBlock,
            // Stored intent and failed pre-write admission carry no new device
            // evidence. Presentation text alone must not retire the reason.
            ObservationReason = !deviceVerified
                ? DeviceObservationReason.None
                : observationReason ?? current.ObservationReason,
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

    private ControlOperationResult DeferLightingIntentForLowPower()
    {
        ServiceStatusSnapshot current = GetStatus();
        DesiredDeviceState desired = Automation.Resolve(current.ConfirmedState ?? UnknownConfirmedState);
        pendingLightingRestore = desired.Lighting.Value;
        pendingLightingRestoreFromLifecycle = true;
        const string message = "Lighting intent was stored and will be applied after standby.";
        history.RecordSummary(ControlOperationStatus.Stored, message);
        PublishIfChanged(current with { DesiredState = desired, Message = message });
        return new(ControlOperationStatus.Stored, message);
    }

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

    public void ReportConsoleDisplayState(bool displayOn)
    {
        // Only the Windows console display source reports here. The Session
        // Agent's suppression requests are policy decisions and say "off" only
        // while the active profile enables the display-off policy, so they
        // cannot be trusted as a display-state signal.
        Volatile.Write(ref displayReportedOff, !displayOn);
    }

    private bool IsLightingOffExplainedByDisplayOff(ConfirmedDeviceState confirmed) =>
        Volatile.Read(ref displayReportedOff)
        && confirmed.Lighting is { Effect: LightingEffect.Off };

    // Firmware Off is the only non-null lighting reading some laptops can produce
    // and it has many benign causes (lid, standby, a stopped renderer, a
    // transient brightness zero). Without a detected competing controller it is
    // not evidence that anything outside OpenBlade changed the lighting.
    private bool IsUnattributedLightingOff(ConfirmedDeviceState confirmed) =>
        confirmed.Lighting is { Effect: LightingEffect.Off }
        && !ControllerMayOwnLighting(GetStatus());

    // After an attempted lighting write, only a readback that positively
    // contradicts it counts as evidence of another controller: a different
    // non-Off keyboard effect, a logo mode other than the one requested, or a
    // competing controller detected. Off, unknown, or matching content with a
    // lost renderer ownership is a fault, not an external change.
    private bool IsLightingReadbackEvidenceOfExternalChange(
        ConfirmedDeviceState? confirmed,
        LightingSettings desired)
    {
        if (ControllerMayOwnLighting(GetStatus()))
        {
            return true;
        }

        if (confirmed?.Lighting is { Effect: not LightingEffect.Off } observed
            && (observed with { LogoMode = null }) != (desired with { LogoMode = null }))
        {
            return true;
        }

        return desired.LogoMode is { } logo
            && confirmed?.LogoLighting is { } observedLogo
            && observedLogo != logo;
    }

    private void AdoptDisplayOffSuppressionForPowerSource()
    {
        // A display-off report that arrived while the previous profile kept the
        // keyboard lit was refused as "not enabled". When the destination
        // profile does enable the policy, the display is still off, so the
        // transition must target Off rather than relight the keyboard and wait
        // for the Session Agent to switch it off again.
        if (!Volatile.Read(ref displayReportedOff)
            || lightingSuppressions.Contains(LightingSuppressionReason.DisplayOff)
            || !IsLightingSuppressionConfigured(LightingSuppressionReason.DisplayOff))
        {
            return;
        }

        UpdateLightingSuppressionState(LightingSuppressionReason.DisplayOff, suppressed: true);
    }

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
        if (exception is ConfigurationQuarantinedException)
        {
            return ConfigurationQuarantineBlocked();
        }

        var rejected = new ControlOperationResult(
            ControlOperationStatus.Failed,
            $"The requested intent was rejected: {exception.Message}");
        history.RecordSummary(rejected.Status, rejected.Message);
        return rejected;
    }

    private bool ThermalConvergencePolicyAdmitted() =>
        ThermalConvergenceReconciliationAdmitted()
        && preferences.ThermalControlPolicy
            == ThermalControlPolicy.MaintainDesiredState
        && preferences.ThermalPolicyUpgradeDisclosure
            != ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending;

    private bool ThermalConvergenceReconciliationAdmitted()
    {
        Func<bool>? controlsSession = activeUserStillControlsSession;
        bool sessionActive;
        try
        {
            sessionActive = controlsSession?.Invoke() == true;
        }
        catch
        {
            sessionActive = false;
        }

        return ConfigurationIsAdmitted
            && thermalConvergenceLedgerAuthoritative
            && thermalConvergenceStore is not null
            && thermalConvergenceLedger is not null
            && !string.IsNullOrWhiteSpace(Volatile.Read(ref activeUserSid))
            && sessionActive
            && Volatile.Read(ref protocolCompatibilityClients) == 0
            && !thermalConvergenceShutdown
            && !lowPowerGate.IsActive;
    }

    private void CloseThermalConvergenceAdmission(
        ThermalConvergenceReason reason,
        bool thermalConvergenceShutdown)
    {
        thermalConvergenceArbiter.CloseAdmission();
        if (thermalConvergenceShutdown)
        {
            this.thermalConvergenceShutdown = true;
        }
        if (Volatile.Read(ref status) is not null)
        {
            reason = ResolveThermalConvergenceClosureReason(reason);
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.Paused,
                reason,
                []);
        }
    }

    private ThermalConvergenceReason ResolveThermalConvergenceClosureReason(
        ThermalConvergenceReason requested)
    {
        if (string.IsNullOrWhiteSpace(Volatile.Read(ref activeUserSid)))
        {
            return ThermalConvergenceReason.ConfigurationUnavailable;
        }
        if (!ConfigurationIsAdmitted || !thermalConvergenceLedgerAuthoritative)
        {
            return ThermalConvergenceReason.ConfigurationQuarantined;
        }
        if (preferences.ThermalControlPolicy == ThermalControlPolicy.Strict)
        {
            return ThermalConvergenceReason.StrictPolicy;
        }
        if (preferences.ThermalPolicyUpgradeDisclosure
            == ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending)
        {
            return ThermalConvergenceReason.UpgradeDisclosurePending;
        }
        if (Volatile.Read(ref protocolCompatibilityClients) > 0)
        {
            return ThermalConvergenceReason.ProtocolCompatibilityClient;
        }

        return requested;
    }

    private void PublishThermalConvergenceStatus(
        ThermalConvergenceState state,
        ThermalConvergenceReason reason,
        IReadOnlyList<ThermalComponent> affectedComponents,
        bool explicitReapplyRequired = false)
    {
        if (Volatile.Read(ref status) is null)
        {
            return;
        }

        ThermalControlPolicy policy = thermalConvergenceLedgerAuthoritative
            ? preferences.ThermalControlPolicy
            : ThermalControlPolicy.Strict;
        ThermalPolicyUpgradeDisclosureState disclosure =
            preferences.ThermalPolicyUpgradeDisclosure;
        PublishIfChanged(GetStatus() with
        {
            ThermalConvergence = new ThermalConvergenceStatus(
                policy,
                disclosure,
                state,
                [.. affectedComponents.OrderBy(component => component)],
                reason,
                explicitReapplyRequired),
        });
    }

    private void RefreshThermalConvergenceIntentState(
        ConfirmedDeviceState confirmed,
        bool policyChanged = false)
    {
        if (thermalConvergenceLedger is not { InFlight: null } prior
            || string.IsNullOrWhiteSpace(Volatile.Read(ref activeUserSid)))
        {
            // Preserve possibly submitted reservations and device invalidation
            // until fresh read-only reconciliation has retired the reservation.
            return;
        }

        DesiredDeviceState desired = ResolveHardwareDesired(confirmed);
        ServiceStatusSnapshot current = GetStatus();
        PowerSource? currentDestination = current.ActivePowerSource is
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                ? current.ActivePowerSource
                : null;
        string model = string.IsNullOrWhiteSpace(current.Firmware?.ModelNumber)
            ? "verified-device"
            : current.Firmware.ModelNumber;
        thermalConvergenceLedger = ThermalConvergenceLedgerTransitions.RefreshIntent(
            prior,
            desired,
            preferences.ThermalControlPolicy,
            preferences.ThermalPolicyUpgradeDisclosure,
            currentDestination,
            $"OpenBlade:{model}",
            thermalDeviceAuthorityInvalidated,
            policyChanged);
        thermalDeviceAuthorityInvalidated = false;
    }

    private static ThermalComponent[] ThermalComponentsFor(
        ThermalConvergenceContradiction contradiction)
    {
        var components = new List<ThermalComponent>(2);
        if (contradiction.HasFlag(ThermalConvergenceContradiction.Performance))
        {
            components.Add(ThermalComponent.Performance);
        }
        if (contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology))
        {
            components.Add(ThermalComponent.Fans);
        }
        return [.. components];
    }

    private static DeviceSubsystem[] DeviceSubsystemsFor(
        ThermalConvergenceContradiction contradiction)
    {
        var components = new List<DeviceSubsystem>(2);
        if (contradiction.HasFlag(ThermalConvergenceContradiction.Performance))
        {
            components.Add(DeviceSubsystem.Performance);
        }
        if (contradiction.HasFlag(ThermalConvergenceContradiction.FanTopology))
        {
            components.Add(DeviceSubsystem.Fans);
        }
        return [.. components];
    }

    private async Task EvaluateThermalConvergenceObservationAsync(
        PowerSource powerSource,
        bool powerSourceConfirmed,
        ConfirmedDeviceState confirmed,
        CancellationToken cancellationToken)
    {
        bool reservedReconciliationAdmitted =
            thermalConvergenceLedger?.InFlight is not null
            && ThermalConvergenceReconciliationAdmitted();
        if (!ThermalConvergencePolicyAdmitted()
            && !reservedReconciliationAdmitted)
        {
            ThermalConvergenceReason reason =
                preferences.ThermalControlPolicy == ThermalControlPolicy.Strict
                    ? ThermalConvergenceReason.StrictPolicy
                    : preferences.ThermalPolicyUpgradeDisclosure ==
                        ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending
                        ? ThermalConvergenceReason.UpgradeDisclosurePending
                        : !thermalConvergenceLedgerAuthoritative
                            ? ThermalConvergenceReason.ConfigurationQuarantined
                            : Volatile.Read(ref protocolCompatibilityClients) > 0
                                ? ThermalConvergenceReason.ProtocolCompatibilityClient
                                : thermalConvergenceShutdown
                                    ? ThermalConvergenceReason.Shutdown
                                    : lowPowerGate.IsActive
                                        ? ThermalConvergenceReason.LowPower
                                        : ThermalConvergenceReason.InactiveSession;
            CloseThermalConvergenceAdmission(reason, thermalConvergenceShutdown: false);
            return;
        }
        if (!powerSourceConfirmed
            || powerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.PowerUnsettled,
                thermalConvergenceShutdown: false);
            return;
        }

        if (thermalConvergenceLedger?.InFlight is not null)
        {
            await ReconcileReservedThermalConvergenceAsync(
                    powerSource,
                    confirmed,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RefreshThermalConvergenceIntentState(confirmed);
        if (thermalConvergenceLedger is not
            {
                EffectiveIntent: { } desired,
                PerformanceDesiredRevision: { } performanceRevision,
                FanDesiredRevision: { } fanRevision,
                PolicyRevision: { } policyRevision,
            } ledger)
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.InternalStateUnavailable,
                thermalConvergenceShutdown: false);
            return;
        }

        ServiceStatusSnapshot current = GetStatus();
        if (!controllerPresenceObserved
            || current.ControllerDetectionWarning is not null)
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.CompetingControllerUncertain,
                thermalConvergenceShutdown: false);
            return;
        }
        if (FanControllerAdmissionUnsafe(current))
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.CompetingControllerPresent,
                thermalConvergenceShutdown: false);
            return;
        }
        if (hyperBoostJournalSafetyBlock || hyperBoostRestorePending
            || pendingIndeterminateHyperBoostEntryIntent is not null
            || pendingIndeterminateHyperBoostExitIntent is not null)
        {
            thermalConvergenceArbiter.CloseAdmission();
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.RestorePending,
                ThermalConvergenceReason.HyperBoostPending,
                [ThermalComponent.Performance, ThermalComponent.Fans]);
            return;
        }
        if (pendingWriteConfirmations.HasPending)
        {
            CloseThermalConvergenceAdmission(
                ThermalConvergenceReason.PendingReconciliation,
                thermalConvergenceShutdown: false);
            return;
        }

        ThermalConvergenceAssessment assessment =
            ThermalConvergenceClassifier.Classify(
                desired,
                confirmed,
                exactManualIntentOwned: IsExactFanIntentOwned(
                    desired.FanControl!));
        switch (assessment.Kind)
        {
            case ThermalConvergenceObservationKind.Matched:
                thermalConvergenceArbiter.OpenAdmission();
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.Matched,
                    ThermalConvergenceReason.None,
                    []);
                return;
            case ThermalConvergenceObservationKind.AwaitingObservation:
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.AwaitingObservation,
                    ThermalConvergenceReason.AwaitingFreshObservation,
                    []);
                return;
            case ThermalConvergenceObservationKind.ManualTopologyOnly:
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.Paused,
                    ThermalConvergenceReason.ManualFanReadbackAmbiguous,
                    [ThermalComponent.Fans],
                    explicitReapplyRequired: true);
                return;
            case ThermalConvergenceObservationKind.ExternalHyperBoost:
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.HyperBoostActive,
                    thermalConvergenceShutdown: false);
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.External,
                    ThermalConvergenceReason.HyperBoostActive,
                    [ThermalComponent.Performance, ThermalComponent.Fans]);
                return;
        }

        ThermalComponent[] affected = ThermalComponentsFor(assessment.Contradiction);
        bool tombstoned = ledger.Tombstones.Any(tombstone =>
            tombstone.Destination == powerSource
            && tombstone.Contradiction == assessment.Contradiction
            && StringComparer.Ordinal.Equals(tombstone.PolicyRevision, policyRevision)
            && (!assessment.Contradiction.HasFlag(
                    ThermalConvergenceContradiction.Performance)
                || StringComparer.Ordinal.Equals(
                    tombstone.PerformanceDesiredRevision,
                    performanceRevision))
            && (!assessment.Contradiction.HasFlag(
                    ThermalConvergenceContradiction.FanTopology)
                || StringComparer.Ordinal.Equals(
                    tombstone.FanDesiredRevision,
                    fanRevision)));
        if (tombstoned)
        {
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.Suppressed,
                ThermalConvergenceReason.Tombstoned,
                affected,
                explicitReapplyRequired: true);
            return;
        }
        bool budgetExhausted = affected.Any(component =>
            ledger.Budgets.Any(budget =>
                budget.Destination == powerSource
                && budget.Component == component
                && StringComparer.Ordinal.Equals(
                    budget.DesiredRevision,
                    component == ThermalComponent.Performance
                        ? performanceRevision
                        : fanRevision)
                && StringComparer.Ordinal.Equals(budget.PolicyRevision, policyRevision)
                && budget.ConsumedClaims >= budget.MaximumClaims));
        if (budgetExhausted)
        {
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.Suppressed,
                ThermalConvergenceReason.BudgetExhausted,
                affected,
                explicitReapplyRequired: true);
            return;
        }

        thermalConvergenceLedger = ledger with
        {
            SettledDestination = powerSource,
            Contradiction = assessment.Contradiction,
        };
        long generation = thermalConvergenceArbiter.EnsureAdmissionOpen();
        var candidate = new ThermalConvergenceCandidate(
            Volatile.Read(ref activeUserSid)!,
            generation,
            powerSource,
            ledger.DeviceAuthorityEpoch.ToString("D"),
            performanceRevision,
            fanRevision,
            assessment.Contradiction);
        ThermalConvergenceNominationResult nomination =
            thermalConvergenceArbiter.TryNominate(candidate);
        PublishThermalConvergenceStatus(
            nomination == ThermalConvergenceNominationResult.AdmissionClosed
                ? ThermalConvergenceState.Paused
                : ThermalConvergenceState.Converging,
            nomination == ThermalConvergenceNominationResult.AdmissionClosed
                ? ThermalConvergenceReason.PendingReconciliation
                : ThermalConvergenceReason.None,
            affected);
    }

    private async Task ReconcileReservedThermalConvergenceAsync(
        PowerSource powerSource,
        ConfirmedDeviceState confirmed,
        CancellationToken cancellationToken)
    {
        if (thermalConvergenceLedger is not
            {
                InFlight: { } reservation,
                PolicyRevision: { } policyRevision,
            } ledger)
        {
            return;
        }

        ThermalConvergenceAssessment assessment =
            ThermalConvergenceClassifier.Classify(
                reservation.DesiredTarget,
                confirmed,
                exactManualIntentOwned:
                    reservation.DesiredTarget.FanControl is { } fanIntent
                    && IsExactFanIntentOwned(fanIntent));
        ThermalTransitionDisposition disposition = assessment.Kind ==
            ThermalConvergenceObservationKind.Matched
                ? ThermalTransitionDisposition.Confirmed
                : ThermalTransitionDisposition.External;
        if (ledger.SettledDestination is not { } reservedDestination)
        {
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.ReconciliationUnavailable,
                ThermalConvergenceReason.InternalStateUnavailable,
                reservation.DeclaredComponents,
                explicitReapplyRequired: true);
            return;
        }

        ThermalConvergenceLedgerState reconciled =
            ThermalConvergenceLedgerTransitions.Complete(
                ledger,
                reservedDestination,
                ledger.Contradiction,
                reservation.DeclaredComponents,
                disposition);
        if (!await TryPersistThermalConvergenceLedgerAsync(
                reconciled,
                cancellationToken).ConfigureAwait(false))
        {
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.ReconciliationUnavailable,
                ThermalConvergenceReason.InternalStateUnavailable,
                reservation.DeclaredComponents,
                explicitReapplyRequired: true);
            return;
        }

        PublishThermalConvergenceStatus(
            ThermalConvergenceState.Suppressed,
            ThermalConvergenceReason.Tombstoned,
            reservation.DeclaredComponents,
            explicitReapplyRequired: true);
    }

    private async Task ExecuteThermalConvergenceAsync(
        ThermalConvergenceCandidate candidate,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ThermalConvergenceWriteBlockReason(candidate) is not null
                || thermalConvergenceLedger is not
                {
                    EffectiveIntent: { } desired,
                } ledger)
            {
                if (candidate.AdmissionGeneration
                    == thermalConvergenceArbiter.CurrentAdmissionGeneration)
                {
                    PublishThermalConvergenceStatus(
                        ThermalConvergenceState.Paused,
                        ThermalConvergenceReason.PendingReconciliation,
                        ThermalComponentsFor(candidate.Contradiction));
                }
                return;
            }

            PowerSource freshPowerSource = await ResolveCurrentPowerSourceAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            RefreshControllerPresenceCore(cancellationToken);
            ServiceStatusSnapshot current = GetStatus();
            if (!controllerPresenceObserved
                || current.ControllerDetectionWarning is not null)
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.CompetingControllerUncertain,
                    thermalConvergenceShutdown: false);
                return;
            }
            if (FanControllerAdmissionUnsafe(current))
            {
                CloseThermalConvergenceAdmission(
                    ThermalConvergenceReason.CompetingControllerPresent,
                    thermalConvergenceShutdown: false);
                return;
            }
            if (freshPowerSource != candidate.Destination)
            {
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.Paused,
                    ThermalConvergenceReason.PowerUnsettled,
                    ThermalComponentsFor(candidate.Contradiction));
                return;
            }
            if (ThermalConvergenceWriteBlockReason(candidate) is not null)
            {
                bool externalHyperBoost = current.ConfirmedState?.HyperBoostActive == true;
                bool hyperBoostPending = hyperBoostJournalSafetyBlock
                    || hyperBoostRestorePending;
                PublishThermalConvergenceStatus(
                    externalHyperBoost
                        ? ThermalConvergenceState.External
                        : hyperBoostPending
                            ? ThermalConvergenceState.RestorePending
                            : ThermalConvergenceState.Paused,
                    externalHyperBoost
                        ? ThermalConvergenceReason.HyperBoostActive
                        : hyperBoostPending
                            ? ThermalConvergenceReason.HyperBoostPending
                            : ThermalConvergenceReason.PendingReconciliation,
                    externalHyperBoost || hyperBoostPending
                        ? [ThermalComponent.Performance, ThermalComponent.Fans]
                        : ThermalComponentsFor(candidate.Contradiction));
                return;
            }
            if (!deviceProvider.TryGetVerifiedDevice(out IBladeDevice device))
            {
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.Paused,
                    ThermalConvergenceReason.DeviceUnavailable,
                    ThermalComponentsFor(candidate.Contradiction));
                return;
            }

            ConfirmedDeviceState fresh = await ReadObservedStateAsync(
                    device,
                    cancellationToken)
                .ConfigureAwait(false);
            ThermalConvergenceAssessment assessment =
                ThermalConvergenceClassifier.Classify(
                    desired,
                    fresh,
                    exactManualIntentOwned: IsExactFanIntentOwned(
                        desired.FanControl!));
            if (assessment.Kind != ThermalConvergenceObservationKind.Candidate
                || assessment.Contradiction != candidate.Contradiction)
            {
                switch (assessment.Kind)
                {
                    case ThermalConvergenceObservationKind.Matched:
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.Matched,
                            ThermalConvergenceReason.None,
                            []);
                        break;
                    case ThermalConvergenceObservationKind.AwaitingObservation:
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.AwaitingObservation,
                            ThermalConvergenceReason.AwaitingFreshObservation,
                            []);
                        break;
                    case ThermalConvergenceObservationKind.ManualTopologyOnly:
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.Paused,
                            ThermalConvergenceReason.ManualFanReadbackAmbiguous,
                            [ThermalComponent.Fans],
                            explicitReapplyRequired: true);
                        break;
                    case ThermalConvergenceObservationKind.ExternalHyperBoost:
                        CloseThermalConvergenceAdmission(
                            ThermalConvergenceReason.HyperBoostActive,
                            thermalConvergenceShutdown: false);
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.External,
                            ThermalConvergenceReason.HyperBoostActive,
                            [ThermalComponent.Performance, ThermalComponent.Fans]);
                        break;
                    case ThermalConvergenceObservationKind.Candidate:
                        PublishThermalConvergenceStatus(
                            ThermalConvergenceState.AwaitingObservation,
                            ThermalConvergenceReason.AwaitingFreshObservation,
                            ThermalComponentsFor(assessment.Contradiction));
                        break;
                }
                return;
            }

            // A lifecycle boundary can close admission while the fresh scoped
            // read is in flight. Recheck immediately before consuming durable
            // budget or persisting a possibly-submitted reservation.
            if (ThermalConvergenceWriteBlockReason(candidate) is not null)
            {
                return;
            }

            HyperBoostOwnershipJournalLoadResult expectedHyperBoostJournal;
            try
            {
                expectedHyperBoostJournal = await hyperBoostOwnershipJournalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CloseThermalConvergenceForHyperBoostJournal(
                    ThermalComponentsFor(candidate.Contradiction),
                    $"The durable HyperBoost journal could not be inspected before Maintain reservation: {exception.Message}");
                return;
            }
            if (!IsSafeMaintainHyperBoostJournalBaseline(
                    expectedHyperBoostJournal))
            {
                CloseThermalConvergenceForHyperBoostJournal(
                    [ThermalComponent.Performance, ThermalComponent.Fans],
                    "The durable HyperBoost journal is not in a safe terminal state for Maintain reservation.");
                return;
            }

            ThermalComponent[] affected = ThermalComponentsFor(candidate.Contradiction);
            Guid transitionAttemptId = Guid.NewGuid();
            ThermalConvergenceLedgerState reserved = ThermalConvergenceLedgerTransitions.Reserve(
                ledger,
                candidate,
                transitionAttemptId,
                fresh,
                desired,
                affected);
            if (!await TryPersistThermalConvergenceLedgerAsync(
                    reserved,
                    cancellationToken).ConfigureAwait(false))
            {
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.Paused,
                    ThermalConvergenceReason.InternalStateUnavailable,
                    affected);
                return;
            }
            string? reservedSha256 = thermalConvergenceLedgerSha256;
            if (string.IsNullOrWhiteSpace(reservedSha256))
            {
                thermalConvergenceLedgerAuthoritative = false;
                thermalConvergenceArbiter.CloseAdmission();
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.ReconciliationUnavailable,
                    ThermalConvergenceReason.InternalStateUnavailable,
                    affected,
                    explicitReapplyRequired: true);
                return;
            }
            history.RecordSummary(
                ControlOperationStatus.Stored,
                $"Maintain thermal convergence reserved attempt {transitionAttemptId:D} for {string.Join(", ", affected)} at {candidate.Destination}; no setter was admitted before this durable reservation.");

            PublishThermalConvergenceStatus(
                ThermalConvergenceState.Converging,
                ThermalConvergenceReason.None,
                affected);
            DeviceSubsystem[] declared = DeviceSubsystemsFor(candidate.Contradiction);
            IReadOnlySet<DeviceSubsystem> ResolveExclusions(ConfirmedDeviceState _) =>
                Enum.GetValues<DeviceSubsystem>()
                    .Where(component => !declared.Contains(component))
                    .ToHashSet();
            ThermalFullTransitionExecution execution =
                await ThermalTransitionExecutor.ExecuteFullTransitionAsync(
                    transitionAttemptId,
                    declared,
                    device,
                    (attemptDevice, runner) => new ControlCoordinator(
                        attemptDevice,
                        scopedStateReader: null,
                        resolveIndeterminatePerformanceWrites: false,
                        resolveIndeterminateAutomaticFanWrites: false,
                        singleAttemptPerformanceWrites: true,
                        singleAttemptFanWrites: true,
                        thermalStepRunner: runner,
                        performanceWriteBlockReason: () =>
                            ScopedThermalWriteBlockReason(
                                desired,
                                stageDesired: null,
                                activeUserStillControlsSession,
                                declared,
                                () => ThermalConvergenceWriteBlockReason(candidate)),
                        fanWriteBlockReason: () =>
                            ScopedThermalWriteBlockReason(
                                desired,
                                stageDesired: null,
                                activeUserStillControlsSession,
                                declared,
                                () => ThermalConvergenceWriteBlockReason(candidate))),
                    (coordinator, token) => coordinator.ApplyDesiredStateOutcomeAsync(
                        desired,
                        token,
                        automaticWriteExclusionResolver: ResolveExclusions),
                    (_, token) => RevalidateThermalConvergenceAdmissionAsync(
                        candidate,
                        reserved,
                        reservedSha256,
                        expectedHyperBoostJournal,
                        desired,
                        declared,
                        PowerTransitionAccessRetryDelay,
                        token),
                    (_, token) => RevalidateThermalConvergenceAdmissionAsync(
                        candidate,
                        reserved,
                        reservedSha256,
                        expectedHyperBoostJournal,
                        desired,
                        declared,
                        ThermalPreWriteRetryDelay,
                        token),
                    (purpose, components, token) =>
                        RevalidateThermalConvergenceSubmissionAsync(
                            candidate,
                            reserved,
                            reservedSha256,
                            expectedHyperBoostJournal,
                            desired,
                            declared,
                            purpose,
                            components,
                            token),
                    cancellationToken).ConfigureAwait(false);

            RecordThermalConvergenceTrace(execution.Trace);

            await CompleteThermalConvergenceExecutionAsync(
                    candidate,
                    reserved,
                    desired,
                    execution,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (candidate.AdmissionGeneration
                == thermalConvergenceArbiter.CurrentAdmissionGeneration)
            {
                PublishThermalConvergenceStatus(
                    ThermalConvergenceState.ReconciliationUnavailable,
                    ThermalConvergenceReason.PendingReconciliation,
                    ThermalComponentsFor(candidate.Contradiction),
                    explicitReapplyRequired: true);
            }
        }
        catch (Exception exception)
        {
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"Maintain thermal convergence stopped safely: {exception.Message}");
            PublishThermalConvergenceStatus(
                ThermalConvergenceState.ReconciliationUnavailable,
                ThermalConvergenceReason.InternalStateUnavailable,
                ThermalComponentsFor(candidate.Contradiction),
                explicitReapplyRequired: true);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void RecordThermalConvergenceTrace(ThermalTransitionTrace trace)
    {
        string steps = trace.Steps is null || trace.Steps.Count == 0
            ? "none"
            : string.Join(
                "; ",
                trace.Steps.Select((step, index) =>
                    $"{index + 1}:{step.Purpose}[{string.Join(",", step.Components)}]="
                    + string.Join(
                        ",",
                        step.Attempts.Select(attempt =>
                            $"{attempt.Stage}/{attempt.SubmissionProgress}/{attempt.Outcome.WriteSubmissionState}/{attempt.Disposition}"))));
        ControlOperationStatus status = trace.Disposition switch
        {
            ThermalTransitionDisposition.Confirmed => ControlOperationStatus.Applied,
            ThermalTransitionDisposition.Acknowledged => ControlOperationStatus.Stored,
            ThermalTransitionDisposition.Blocked => ControlOperationStatus.Blocked,
            _ => ControlOperationStatus.Failed,
        };
        history.RecordSummary(
            status,
            $"Maintain thermal convergence attempt {trace.AttemptId:D}: components={string.Join(",", trace.Components)}; stage={trace.Stage}; submission={trace.SubmissionProgress}; certainty={trace.Certainty?.ToString() ?? "None"}; disposition={trace.Disposition}; attempts={trace.AttemptCount}; retryConsumed={trace.RetryConsumed}; retryBlock={trace.RetryBlockReason ?? "None"}; steps={steps}.");
    }

    private string? ThermalConvergenceWriteBlockReason(
        ThermalConvergenceCandidate candidate)
    {
        if (candidate.AdmissionGeneration
                != thermalConvergenceArbiter.CurrentAdmissionGeneration
            || Volatile.Read(ref pendingPowerAdmissions) > 0
            || Volatile.Read(ref pendingSessionAdmissions) > 0)
        {
            return "Maintain thermal convergence authority changed before submission.";
        }
        if (!ThermalConvergencePolicyAdmitted())
        {
            return "Maintain thermal convergence authority is closed.";
        }
        ServiceStatusSnapshot current = GetStatus();
        if (!controllerPresenceObserved || FanControllerAdmissionUnsafe(current))
        {
            return "Maintain thermal convergence yielded to another Razer controller or an incomplete controller check.";
        }
        if (current.ConfirmedState?.HyperBoostActive == true
            || hyperBoostJournalSafetyBlock
            || hyperBoostRestorePending)
        {
            return "HyperBoost owns or is reconciling the transient thermal state.";
        }
        if (thermalConvergenceLedger is not
            {
                DeviceAuthorityEpoch: var deviceEpoch,
                PerformanceDesiredRevision: { } performanceRevision,
                FanDesiredRevision: { } fanRevision,
            }
            || !string.Equals(
                Volatile.Read(ref activeUserSid),
                candidate.ActiveUserSid,
                StringComparison.OrdinalIgnoreCase)
            || deviceEpoch.ToString("D") != candidate.DeviceAuthorityEpoch
            || performanceRevision != candidate.PerformanceDesiredRevision
            || fanRevision != candidate.FanDesiredRevision
            || current.ActivePowerSource != candidate.Destination)
        {
            return "The authoritative Maintain thermal intent changed before submission.";
        }
        return null;
    }

    private Task<ThermalRetryAdmission>
        RevalidateThermalConvergenceSubmissionAsync(
        ThermalConvergenceCandidate candidate,
        ThermalConvergenceLedgerState expectedReservation,
        string expectedReservationSha256,
        HyperBoostOwnershipJournalLoadResult expectedHyperBoostJournal,
        DesiredDeviceState expectedDesired,
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        ThermalTransitionStepPurpose purpose,
        IReadOnlyList<DeviceSubsystem> submissionComponents,
        CancellationToken cancellationToken)
    {
        if (purpose == ThermalTransitionStepPurpose.HyperBoost
            || submissionComponents.Count == 0
            || submissionComponents.Any(component =>
                !declaredComponents.Contains(component)))
        {
            return Task.FromResult(ThermalRetryAdmission.Block(
                "The requested thermal step is outside the durable Maintain reservation."));
        }

        return RevalidateThermalConvergenceAdmissionAsync(
            candidate,
            expectedReservation,
            expectedReservationSha256,
            expectedHyperBoostJournal,
            expectedDesired,
            declaredComponents,
            TimeSpan.Zero,
            cancellationToken);
    }

    private async Task<ThermalRetryAdmission>
        RevalidateThermalConvergenceAdmissionAsync(
        ThermalConvergenceCandidate candidate,
        ThermalConvergenceLedgerState expectedReservation,
        string expectedReservationSha256,
        HyperBoostOwnershipJournalLoadResult expectedHyperBoostJournal,
        DesiredDeviceState expectedDesired,
        IReadOnlyList<DeviceSubsystem> declaredComponents,
        TimeSpan recoveryDelay,
        CancellationToken cancellationToken)
    {
        string? durableBlock =
            await RevalidateThermalConvergenceReservationAsync(
                    expectedReservation,
                    expectedReservationSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        if (durableBlock is not null)
        {
            return ThermalRetryAdmission.Block(durableBlock);
        }
        durableBlock = await RevalidateThermalConvergenceHyperBoostJournalAsync(
                expectedHyperBoostJournal,
                expectedReservation.InFlight?.DeclaredComponents ?? [],
                cancellationToken)
            .ConfigureAwait(false);
        if (durableBlock is not null)
        {
            return ThermalRetryAdmission.Block(durableBlock);
        }

        ThermalRetryAdmission scopedAdmission =
            await RevalidateScopedThermalRetryAsync(
                    candidate.Destination,
                    expectedDesired,
                    stageDesired: null,
                    activeUserStillControlsSession,
                    declaredComponents,
                    recoveryDelay,
                    cancellationToken,
                    () => ThermalConvergenceWriteBlockReason(candidate))
                .ConfigureAwait(false);
        if (!scopedAdmission.Allowed || scopedAdmission.Device is null)
        {
            return scopedAdmission;
        }

        durableBlock = await RevalidateThermalConvergenceReservationAsync(
                expectedReservation,
                expectedReservationSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (durableBlock is not null)
        {
            return ThermalRetryAdmission.Block(durableBlock);
        }
        durableBlock = await RevalidateThermalConvergenceHyperBoostJournalAsync(
                expectedHyperBoostJournal,
                expectedReservation.InFlight?.DeclaredComponents ?? [],
                cancellationToken)
            .ConfigureAwait(false);
        if (durableBlock is not null)
        {
            return ThermalRetryAdmission.Block(durableBlock);
        }

        string? finalBlock = ThermalConvergenceWriteBlockReason(candidate);
        return finalBlock is null
            ? ThermalRetryAdmission.Allow(scopedAdmission.Device)
            : ThermalRetryAdmission.Block(finalBlock);
    }

    private async Task<string?>
        RevalidateThermalConvergenceReservationAsync(
        ThermalConvergenceLedgerState expectedReservation,
        string expectedReservationSha256,
        CancellationToken cancellationToken)
    {
        if (!thermalConvergenceLedgerAuthoritative
            || thermalConvergenceStore is null)
        {
            return "The durable Maintain reservation is unavailable before submission.";
        }

        try
        {
            ThermalConvergenceLoadResult load = await thermalConvergenceStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!load.IsAuthoritative
                || !StringComparer.Ordinal.Equals(
                    load.Sha256,
                    expectedReservationSha256)
                || !ThermalConvergenceLedgerTransitions.IsExpectedReservation(
                    load.State,
                    expectedReservation))
            {
                CloseThermalConvergenceReservationAuthority(
                    expectedReservation,
                    "The durable Maintain reservation changed before submission.");
                return "The durable Maintain reservation changed before submission.";
            }

            return null;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ConfigurationQuarantinedException)
        {
            CloseThermalConvergenceReservationAuthority(
                expectedReservation,
                $"The durable Maintain reservation could not be inspected before submission: {exception.Message}");
            return $"The durable Maintain reservation could not be inspected before submission: {exception.Message}";
        }
    }

    private void CloseThermalConvergenceReservationAuthority(
        ThermalConvergenceLedgerState expectedReservation,
        string message)
    {
        thermalConvergenceLedgerAuthoritative = false;
        thermalConvergenceArbiter.CloseAdmission();
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        PublishThermalConvergenceStatus(
            ThermalConvergenceState.ReconciliationUnavailable,
            ThermalConvergenceReason.InternalStateUnavailable,
            expectedReservation.InFlight?.DeclaredComponents ?? [],
            explicitReapplyRequired: true);
    }

    private async Task<string?>
        RevalidateThermalConvergenceHyperBoostJournalAsync(
        HyperBoostOwnershipJournalLoadResult expected,
        IReadOnlyList<ThermalComponent> affected,
        CancellationToken cancellationToken)
    {
        try
        {
            HyperBoostOwnershipJournalLoadResult current =
                await hyperBoostOwnershipJournalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (current.Invalid
                || current.Journal != expected.Journal
                || !StringComparer.Ordinal.Equals(current.Sha256, expected.Sha256))
            {
                const string message =
                    "The durable HyperBoost journal changed before Maintain submission.";
                CloseThermalConvergenceForHyperBoostJournal(affected, message);
                return message;
            }

            return null;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string message =
                $"The durable HyperBoost journal could not be inspected before Maintain submission: {exception.Message}";
            CloseThermalConvergenceForHyperBoostJournal(affected, message);
            return message;
        }
    }

    private void CloseThermalConvergenceForHyperBoostJournal(
        IReadOnlyList<ThermalComponent> affected,
        string message)
    {
        hyperBoostJournalSafetyBlock = true;
        thermalConvergenceArbiter.CloseAdmission();
        history.RecordSummary(ControlOperationStatus.Blocked, message);
        PublishThermalConvergenceStatus(
            ThermalConvergenceState.RestorePending,
            ThermalConvergenceReason.HyperBoostPending,
            affected,
            explicitReapplyRequired: true);
    }

    private static bool IsSafeMaintainHyperBoostJournalBaseline(
        HyperBoostOwnershipJournalLoadResult load) =>
        !load.Invalid
        && (load.Journal is null
                && load.Sha256 is null
            || load.Journal is { State: HyperBoostOwnershipJournalState.Relinquished }
                && !string.IsNullOrWhiteSpace(load.Sha256));

    private async Task CompleteThermalConvergenceExecutionAsync(
        ThermalConvergenceCandidate candidate,
        ThermalConvergenceLedgerState reserved,
        DesiredDeviceState desired,
        ThermalFullTransitionExecution execution,
        CancellationToken cancellationToken)
    {
        FullTransitionApplyOutcome outcome = execution.Outcome;
        ThermalComponent[] affected = ThermalComponentsFor(candidate.Contradiction);
        RecordConfirmedResumeWrites(outcome);
        IReadOnlySet<DeviceSubsystem> unavailable = outcome.ConfirmationUnavailable
            ?? new HashSet<DeviceSubsystem>();
        if (outcome.PreWriteState is { } preWrite)
        {
            foreach (DeviceSubsystem subsystem in unavailable.Where(
                subsystem => subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans))
            {
                SetPendingWriteConfirmation(
                    subsystem,
                    new PendingWriteConfirmation(
                        candidate.Destination,
                        outcome.EffectiveDesired ?? desired,
                        preWrite));
            }
        }
        if (outcome.Readback is { } readback)
        {
            driftTracker.Observe(readback);
            PublishIfChanged(GetStatus() with
            {
                ConfirmedState = readback,
                DesiredState = Automation.Resolve(readback),
                ExternalChanges = driftTracker.ExternalChanges,
            });
        }
        foreach (DeviceSubsystem subsystem in DeviceSubsystemsFor(candidate.Contradiction))
        {
            ControlResult result = ResultFor(outcome.Results, subsystem);
            history.RecordSubsystemResult(subsystem, result);
            if (outcome.ReadbackMatched.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: true);
                pendingWriteConfirmations.Remove(subsystem);
            }
            else if (result.Status == ControlResultStatus.Applied)
            {
                pendingWriteConfirmations.Remove(subsystem);
                driftTracker.AcknowledgeIntentionalWithoutReadback(subsystem);
            }
            else if (outcome.WriteAttempted.Contains(subsystem)
                && !unavailable.Contains(subsystem))
            {
                driftTracker.ConfirmIntentional(subsystem, matched: false);
            }
        }
        if (candidate.Contradiction.HasFlag(
            ThermalConvergenceContradiction.FanTopology))
        {
            UpdateOwnedFanControlSettings(
                desired.FanControl!,
                ResultFor(outcome.Results, DeviceSubsystem.Fans),
                outcome.WriteAttempted.Contains(DeviceSubsystem.Fans),
                outcome.ReadbackMatched.Contains(DeviceSubsystem.Fans),
                confirmationUnavailable: unavailable.Contains(DeviceSubsystem.Fans));
        }

        ThermalTransitionDisposition disposition = execution.Trace.Disposition;
        ThermalConvergenceLedgerState completed =
            ThermalConvergenceLedgerTransitions.Complete(
                reserved,
                candidate.Destination,
                candidate.Contradiction,
                affected,
                disposition);
        bool persisted = await TryPersistThermalConvergenceLedgerAsync(
                completed,
                cancellationToken)
            .ConfigureAwait(false);
        if (candidate.AdmissionGeneration
            != thermalConvergenceArbiter.CurrentAdmissionGeneration)
        {
            // A lifecycle, policy, session, controller, or power boundary that
            // closed admission owns the public status. Preserve its reason
            // after retiring this possibly submitted attempt durably.
            return;
        }
        ThermalConvergenceState state = persisted && disposition ==
                ThermalTransitionDisposition.Confirmed
            ? ThermalConvergenceState.Matched
            : disposition is ThermalTransitionDisposition.External
                ? ThermalConvergenceState.External
                : ThermalConvergenceState.ReconciliationUnavailable;
        PublishThermalConvergenceStatus(
            state,
            persisted && state == ThermalConvergenceState.Matched
                ? ThermalConvergenceReason.None
                : persisted
                    ? ThermalConvergenceReason.Tombstoned
                    : ThermalConvergenceReason.InternalStateUnavailable,
            affected,
            explicitReapplyRequired: state != ThermalConvergenceState.Matched);
    }

    private async Task<bool> TryPersistThermalConvergenceLedgerAsync(
        ThermalConvergenceLedgerState replacement,
        CancellationToken cancellationToken)
    {
        if (!thermalConvergenceLedgerAuthoritative
            || thermalConvergenceStore is null)
        {
            return false;
        }
        try
        {
            ThermalConvergenceMutationResult saved =
                await thermalConvergenceStore.TrySaveAsync(
                        thermalConvergenceLedgerSha256,
                        replacement,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!saved.Applied)
            {
                thermalConvergenceLedgerAuthoritative = false;
                thermalConvergenceArbiter.CloseAdmission();
                return false;
            }
            thermalConvergenceLedger = replacement;
            thermalConvergenceLedgerSha256 = saved.Sha256;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ConfigurationQuarantinedException)
        {
            thermalConvergenceLedgerAuthoritative = false;
            thermalConvergenceArbiter.CloseAdmission();
            history.RecordSummary(
                ControlOperationStatus.Failed,
                $"Maintain thermal convergence state could not be preserved: {exception.Message}");
            return false;
        }
    }

    private bool ConfigurationIsAdmitted =>
        (ConfigurationLoadDisposition)Volatile.Read(ref activeConfigurationDisposition)
            == ConfigurationLoadDisposition.Admitted;

    private static string StripConfigurationQuarantineAdvisory(string message) =>
        message.StartsWith(
            ConfigurationQuarantineAdvisory,
            StringComparison.Ordinal)
            ? message[ConfigurationQuarantineAdvisory.Length..].TrimStart()
            : message;

    private ControlOperationResult ConfigurationQuarantineBlocked(
        DeviceSubsystem? subsystem = null)
    {
        Volatile.Write(
            ref activeConfigurationDisposition,
            (int)ConfigurationLoadDisposition.Quarantined);
        var blocked = new ControlOperationResult(
            ControlOperationStatus.Blocked,
            ConfigurationQuarantineBlockedMessage);
        if (subsystem is { } blockedSubsystem)
        {
            history.RecordSubsystemResult(
                blockedSubsystem,
                new ControlResult(ControlResultStatus.Blocked, blocked.Message));
        }
        else
        {
            history.RecordSummary(blocked.Status, blocked.Message);
        }
        PublishIfChanged(GetStatus() with { Message = blocked.Message });
        return blocked;
    }

    private string? ConfigurationThermalWriteBlockReason(
        ScopedThermalAuthorityPurpose authorityPurpose) =>
        ConfigurationIsAdmitted
        || authorityPurpose == ScopedThermalAuthorityPurpose.PriorAuthorityCleanup
            ? null
            : ConfigurationQuarantineBlockedMessage;

    private static ControlOperationResult ToOperationResult(ControlResult result) => new(
        result.Status switch
        {
            ControlResultStatus.Blocked => ControlOperationStatus.Blocked,
            ControlResultStatus.Failed => ControlOperationStatus.Failed,
            _ => ControlOperationStatus.Applied,
        },
        result.Message);

    private FanControlSettings ResolveCurrentFanIntent()
    {
        DesiredDeviceState desired = Automation.Resolve(
            GetStatus().ConfirmedState ?? UnknownConfirmedState);
        return desired.FanControl
            ?? FanControlSettings.FromLegacy(desired.Fans.Value);
    }

    private bool CanMaintainFanCurve(
        ServiceStatusSnapshot current,
        FanControlSettings settings)
    {
        FanControlSettings currentSettings = current.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(current.DesiredState.Fans.Value);
        return settings.Mode == FanMode.ManualCurve
            && currentSettings == settings
            && current.ConfirmedState?.HyperBoostActive != true
            && current.ConfirmedState?.Fans is { } confirmedFans
            && DeviceSubsystemOrder.IsManualFanMode(confirmedFans)
            && current.DeviceVerified
            && !current.WritesBlocked
            && !lowPowerGate.IsActive
            && !unexpectedExitRecoveryDeferredForUserConfiguration
            && !manualFanCurveRecoveryPending
            && controllerPresenceObserved
            && !current.ExternalChanges.Contains(DeviceSubsystem.Fans)
            && !pendingWriteConfirmations.Blocks(DeviceSubsystem.Performance)
            && !pendingWriteConfirmations.Blocks(DeviceSubsystem.Fans)
            && IsExactFanIntentOwned(settings);
    }

    private static bool FanControllerAdmissionUnsafe(ServiceStatusSnapshot current) =>
        current.ControllerDetectionWarning is not null
        || current.PotentialControllers.Any(CompetingControllerProcesses.Contains);

    private string? ManualFanCurveWriteBlockReason(
        ManualFanCurveAuthority authority)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (!CanMaintainFanCurve(current, authority.Settings))
        {
            return "Manual fan-curve authority changed before target submission.";
        }

        if (FanControllerAdmissionUnsafe(current))
        {
            return "Manual fan-curve maintenance yielded to another Razer controller or an incomplete controller check.";
        }

        if (Volatile.Read(ref pendingPowerAdmissions) > 0
            || Volatile.Read(ref pendingSessionAdmissions) > 0
            || fanAuthorityEpoch != authority.AuthorityEpoch
            || fanDesiredRevision != authority.DesiredRevision
            || powerSourceGeneration != authority.PowerSourceGeneration
            || Interlocked.Read(ref sessionAdmissionEpoch)
                != authority.SessionAdmissionEpoch
            || current.ActivePowerSource != authority.PowerSource
            || !string.Equals(
                Volatile.Read(ref activeUserSid),
                authority.ActiveUserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Manual fan-curve session authority changed before target submission.";
        }

        return null;
    }

    private async Task<ThermalRetryAdmission> RevalidateManualFanCurveRetryAsync(
        ManualFanCurveAuthority authority,
        CancellationToken cancellationToken)
    {
        ThermalRetryAdmission admission = await RevalidateScopedThermalRetryAsync(
                authority.PowerSource,
                authority.Desired,
                stageDesired: null,
                stillControlsSession: () => string.Equals(
                    Volatile.Read(ref activeUserSid),
                    authority.ActiveUserSid,
                    StringComparison.OrdinalIgnoreCase),
                [DeviceSubsystem.Fans],
                ThermalPreWriteRetryDelay,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Allowed)
        {
            return admission;
        }

        string? blockReason = ManualFanCurveWriteBlockReason(authority);
        if (blockReason is not null)
        {
            return ThermalRetryAdmission.Block(blockReason);
        }

        FanTemperatureReadings temperatures = await fanTemperatureSource
            .ReadAsync(cancellationToken);
        ServiceStatusSnapshot current = GetStatus();
        FanTargetSpeeds freshTargets = FanCurveCalculator.Calculate(
            authority.Settings.ManualCurve,
            temperatures,
            current.Capabilities?.FanSpeedRange?.MaximumRpm
                ?? FanControlSettings.MaximumSpeedRpm);
        if (freshTargets != authority.Targets)
        {
            return ThermalRetryAdmission.Block(
                ManualFanCurveTargetsChangedRetryBlockReason);
        }

        return admission;
    }

    private void AdvanceFanAuthorityEpoch(
        bool preserveDeferredRecoveryBaseline = false)
    {
        if (preserveDeferredRecoveryBaseline
            && unexpectedExitRecoveryDeferredForUserConfiguration)
        {
            deferredManualFanSafetyOwnership =
                deferredManualFanSafetyOwnership.State switch
                {
                    DeferredManualFanSafetyState.FloorOwned =>
                        DeferredManualFanSafetyOwnership.Pending(
                            FanMode.ManualCurve,
                            reestablishAfterUserChange: true),
                    DeferredManualFanSafetyState.AutomaticOwned =>
                        DeferredManualFanSafetyOwnership.Pending(
                            FanMode.Automatic,
                            reestablishAfterUserChange: true),
                    DeferredManualFanSafetyState.PendingUnowned =>
                        deferredManualFanSafetyOwnership,
                    _ => DeferredManualFanSafetyOwnership.Lost,
                };
            manualFanCurveRecoveryPending =
                deferredManualFanSafetyOwnership.State ==
                    DeferredManualFanSafetyState.PendingUnowned;
        }
        else if (deferredManualFanSafetyOwnership.State !=
            DeferredManualFanSafetyState.Lost)
        {
            deferredManualFanSafetyOwnership =
                DeferredManualFanSafetyOwnership.Lost;
            manualFanCurveRecoveryPending = false;
        }

        fanAuthorityEpoch++;
        exactFanControlOwnership = ExactFanControlOwnership.Unowned;
        ClearLastFanCurveTargets();
    }

    private void AdvanceFanDesiredRevision()
    {
        fanDesiredRevision++;
        ClearLastFanCurveTargets();
    }

    private DeferredManualFanRecoveryAuthority
        CaptureDeferredManualFanRecoveryAuthority(
        DeferredManualFanSafetyOwnership expected,
        PowerSource powerSource)
    {
        if (expected.State is not (
            DeferredManualFanSafetyState.PendingUnowned or
            DeferredManualFanSafetyState.FloorOwned or
            DeferredManualFanSafetyState.AutomaticOwned))
        {
            throw new InvalidOperationException(
                "Deferred fan recovery cannot claim an inactive ownership state.");
        }

        return CaptureFanRecoveryAuthority(powerSource);
    }

    private DeferredManualFanRecoveryAuthority CaptureFanRecoveryAuthority(
        PowerSource powerSource) => new(
            Guid.NewGuid(),
            powerSource,
            powerSourceGeneration,
            Interlocked.Read(ref powerAdmissionEpoch),
            Interlocked.Read(ref sessionAdmissionEpoch),
            fanAuthorityEpoch,
            fanDesiredRevision,
            Volatile.Read(ref activeUserSid));

    private string? DeferredManualFanRecoveryWriteBlockReason(
        DeferredManualFanRecoveryAuthority authority)
    {
        if (deferredManualFanSafetyOwnership is not
            {
                State: DeferredManualFanSafetyState.TransitionPending,
                AttemptId: var attemptId,
            }
            || attemptId != authority.AttemptId)
        {
            return "Deferred fan-recovery ownership changed before submission.";
        }

        return FanRecoveryAuthorityBlockReason(authority);
    }

    private string? FanRecoveryAuthorityBlockReason(
        DeferredManualFanRecoveryAuthority authority)
    {
        ServiceStatusSnapshot current = GetStatus();
        if (!controllerPresenceObserved || FanControllerAdmissionUnsafe(current))
        {
            return "Unexpected-exit fan recovery yielded to another Razer controller or an incomplete controller check.";
        }

        if (Volatile.Read(ref pendingPowerAdmissions) > 0
            || Volatile.Read(ref pendingSessionAdmissions) > 0
            || fanAuthorityEpoch != authority.FanAuthorityEpoch
            || fanDesiredRevision != authority.FanDesiredRevision
            || powerSourceGeneration != authority.PowerSourceGeneration
            || Interlocked.Read(ref powerAdmissionEpoch) !=
                authority.PowerAdmissionEpoch
            || Interlocked.Read(ref sessionAdmissionEpoch) !=
                authority.SessionAdmissionEpoch
            || current.ActivePowerSource != authority.PowerSource
            || !string.Equals(
                Volatile.Read(ref activeUserSid),
                authority.ActiveUserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Unexpected-exit fan-recovery authority changed before submission.";
        }

        return null;
    }

    private bool IsExactFanIntentOwned(FanControlSettings settings) =>
        exactFanControlOwnership.Matches(
            settings,
            fanAuthorityEpoch,
            fanDesiredRevision);

    private bool IsDeferredExactFanIntentOwned(
        DeferredManualFanSafetyOwnership ownership) =>
        ownership.Intent is { } expected
        && exactFanControlOwnership.Matches(
            expected,
            ownership.AuthorityEpoch,
            ownership.DesiredRevision);

    private static bool IsUnspentPreSubmission(
        ScopedApplyOutcome? outcome,
        ThermalTransitionTrace? trace) => outcome is
        {
            Result.Status: ControlResultStatus.Blocked,
            WriteAttempted: false,
            WriteSubmissionState: WriteSubmissionState.KnownNotApplied,
        }
        && trace is { RetryConsumed: false };

    private void OwnExactFanIntent(FanControlSettings settings)
    {
        exactFanControlOwnership = DeviceSubsystemOrder.IsManualFanMode(settings.Mode)
            && !FanControllerAdmissionUnsafe(GetStatus())
                ? ExactFanControlOwnership.Owned(
                    settings,
                    fanAuthorityEpoch,
                    fanDesiredRevision)
                : ExactFanControlOwnership.Unowned;
    }

    private void ClassifyObservedFanTopology(FanMode? observed)
    {
        ExactFanOwnershipTransition transition =
            exactFanControlOwnership.ObserveTopology(observed);
        if (transition.Ownership is { } ownership)
        {
            exactFanControlOwnership = ownership;
        }
        if (transition.ClearCurveTargets)
        {
            ClearLastFanCurveTargets();
        }
    }

    private void ClearLastFanCurveTargets()
    {
        lastCurveFan1Rpm = null;
        lastCurveFan2Rpm = null;
    }

    private void UpdateOwnedFanControlSettings(
        FanControlSettings requested,
        ControlResult result,
        bool writeAttempted,
        bool readbackMatched,
        bool allowFanOwnershipRearm = false,
        WriteSubmissionState? writeSubmissionState = null,
        bool confirmationUnavailable = false)
    {
        ExactFanOwnershipTransition transition =
            exactFanControlOwnership.EvaluateOutcome(
                requested.Mode,
                result.Status,
                writeAttempted,
                readbackMatched,
                allowFanOwnershipRearm,
                writeSubmissionState,
                confirmationUnavailable);
        if (transition.RequiresLiveAdmission)
        {
            OwnExactFanIntent(requested);
            return;
        }

        if (transition.Ownership is { } ownership)
        {
            exactFanControlOwnership = ownership;
        }
        if (transition.ClearCurveTargets)
        {
            ClearLastFanCurveTargets();
        }
    }

    private static bool IsIntentFailure(Exception exception) => exception is
        ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException
            or ConfigurationQuarantinedException;

    private ProfileAutomationState Automation => automation
        ?? throw new InvalidOperationException("The service runtime is not initialized.");

    private BatteryConfigurationStore ConfigurationStore =>
        Volatile.Read(ref userStores).Configuration;

    private PersistentOverrideStore OverrideStore =>
        Volatile.Read(ref userStores).Overrides;

    private sealed record UserConfigurationStores(
        BatteryConfigurationStore Configuration,
        PersistentOverrideStore Overrides);

    private sealed record PendingTransitionContinuation(
        ProfileAutomationState Owner,
        long PowerGeneration,
        PowerSource PowerSource,
        DesiredDeviceState RequestedDesired,
        DesiredDeviceState PriorDesired,
        DesiredDeviceState EffectiveDesired,
        ConfirmedDeviceState PreWriteState,
        IReadOnlySet<DeviceSubsystem> Subsystems,
        IReadOnlySet<DeviceSubsystem> Dependencies,
        DateTimeOffset ExpiresAt);

    private sealed record PendingResumePerformanceDrift(
        long PowerGeneration,
        PowerSource PowerSource,
        DesiredDeviceState Desired,
        DateTimeOffset ExpiresAt);

    private sealed record ManualFanCurveAuthority(
        FanControlSettings Settings,
        DesiredDeviceState Desired,
        FanTargetSpeeds Targets,
        long AuthorityEpoch,
        long DesiredRevision,
        long PowerSourceGeneration,
        PowerSource PowerSource,
        long SessionAdmissionEpoch,
        string? ActiveUserSid);

    internal ExactFanControlOwnershipDisposition FanControlOwnershipDisposition =>
        exactFanControlOwnership.Disposition;

    private enum DeferredManualFanSafetyState
    {
        PendingUnowned,
        FloorOwned,
        AutomaticOwned,
        TransitionPending,
        Lost,
    }

    private readonly record struct DeferredManualFanSafetyOwnership(
        DeferredManualFanSafetyState State,
        FanMode? PendingBaseline,
        FanControlSettings? Intent,
        long AuthorityEpoch,
        long DesiredRevision,
        Guid? AttemptId,
        bool ReestablishAfterUserChange)
    {
        public static DeferredManualFanSafetyOwnership Lost { get; } = new(
            DeferredManualFanSafetyState.Lost,
            PendingBaseline: null,
            Intent: null,
            AuthorityEpoch: -1,
            DesiredRevision: -1,
            AttemptId: null,
            ReestablishAfterUserChange: false);

        public static DeferredManualFanSafetyOwnership Pending(
            FanMode baseline,
            bool reestablishAfterUserChange) => new(
                DeferredManualFanSafetyState.PendingUnowned,
                baseline,
                Intent: null,
                AuthorityEpoch: -1,
                DesiredRevision: -1,
                AttemptId: null,
                reestablishAfterUserChange);

        public static DeferredManualFanSafetyOwnership Owned(
            DeferredManualFanSafetyState state,
            FanControlSettings intent,
            long authorityEpoch,
            long desiredRevision)
        {
            if (state is not (
                DeferredManualFanSafetyState.FloorOwned or
                DeferredManualFanSafetyState.AutomaticOwned))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            return new DeferredManualFanSafetyOwnership(
                state,
                PendingBaseline: null,
                intent,
                authorityEpoch,
                desiredRevision,
                AttemptId: null,
                ReestablishAfterUserChange: false);
        }

        public static DeferredManualFanSafetyOwnership TransitionPending(
            Guid attemptId) => new(
                DeferredManualFanSafetyState.TransitionPending,
                PendingBaseline: null,
                Intent: null,
                AuthorityEpoch: -1,
                DesiredRevision: -1,
                attemptId,
                ReestablishAfterUserChange: false);

        public bool MatchesPendingBaseline(FanMode? observed) =>
            PendingBaseline switch
            {
                FanMode.Automatic => observed == FanMode.Automatic,
                { } baseline when DeviceSubsystemOrder.IsManualFanMode(baseline) =>
                    observed is { } value
                    && DeviceSubsystemOrder.IsManualFanMode(value),
                _ => false,
            };
    }

    private sealed record DeferredManualFanRecoveryAuthority(
        Guid AttemptId,
        PowerSource PowerSource,
        long PowerSourceGeneration,
        long PowerAdmissionEpoch,
        long SessionAdmissionEpoch,
        long FanAuthorityEpoch,
        long FanDesiredRevision,
        string? ActiveUserSid);

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
                pendingWriteConfirmations.SnapshotSubsystems();
            ServiceStatusSnapshot candidate = updated with
            {
                WritesBlocked = updated.WritesBlocked
                    || hyperBoostJournalSafetyBlock
                    || unconfirmedWrites.Count > 0,
                UnconfirmedWrites = unconfirmedWrites,
                HyperBoostRestorePending = hyperBoostRestorePending,
            };
            candidate = ApplyConfigurationQuarantineAdvisory(candidate);
            if (!previous.ExternalChanges.Contains(DeviceSubsystem.Fans)
                && candidate.ExternalChanges.Contains(DeviceSubsystem.Fans)
                && exactFanControlOwnership.Disposition ==
                    ExactFanControlOwnershipDisposition.OwnedExactIntent)
            {
                AdvanceFanAuthorityEpoch();
            }
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
        ServiceStatusSnapshotComparer.Equivalent(left, right);

    private static string RefreshRateLabel(LightingRefreshRate value) => value switch
    {
        LightingRefreshRate.ThirtyHz => "30 Hz",
        LightingRefreshRate.SixtyHz => "60 Hz",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static async Task<LightingRendererOwnershipState> ReadLightingOwnershipAsync(
        IBladeDevice device,
        CancellationToken cancellationToken) =>
        await TryReadLightingOwnershipAsync(device, cancellationToken).ConfigureAwait(false)
            ?? LightingRendererOwnershipState.None;

    private static async Task<LightingRendererOwnershipState?> TryReadLightingOwnershipAsync(
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
            return null;
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

        if (lightingSuppressions.Count > 0 || Volatile.Read(ref displayReportedOff))
        {
            // A renderer that stopped while the display is off most likely
            // stopped because the lid or standby darkened the keyboard; a
            // restart now would be undone the same way and its readback would
            // read as a contradiction. The candidate is kept for display-on.
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
        catch (ConfigurationQuarantinedException)
        {
            _ = ConfigurationQuarantineBlocked(DeviceSubsystem.Lighting);
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
        if (appliedSettings is not null && OwnershipMatches(ownership, appliedSettings))
        {
            // A successful newer effect or explicit Off supersedes any queued
            // recovery. Observation alone must not consume a deferred retry.
            faultedLightingRecoveryCandidate = null;
            pendingLightingRestore = null;
            pendingLightingRestoreFromLifecycle = false;
        }

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
        if (capabilities.HyperBoostAccess is null
            && capabilities.HyperBoostWithoutCoolingPadAccess is null)
        {
            return PadlessHyperBoostEligibilityState.Unknown;
        }

        return hyperBoostPadDiscoverySource.Observe() switch
        {
            CoolingPadConnectionState.Absent =>
                PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            CoolingPadConnectionState.PresentExact =>
                PadlessHyperBoostEligibilityState.CoolingPadPresent,
            // For a pad-less-only model an ambiguous pad blocks entry like a present one;
            // where the pad-present path is admitted, "present" would mean ready instead.
            CoolingPadConnectionState.Ambiguous
                when capabilities.HyperBoostWithoutCoolingPadAccess is not null
                    && HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities) is null =>
                PadlessHyperBoostEligibilityState.CoolingPadPresent,
            _ => PadlessHyperBoostEligibilityState.Unknown,
        };
    }

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

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
