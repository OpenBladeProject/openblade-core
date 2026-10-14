using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum ControlOperationStatus
{
    Stored,
    Applied,
    Blocked,
    Failed,
}

public sealed record ControlOperationResult(ControlOperationStatus Status, string Message)
{
    // Optional so an omitted result remains safe: the tray performs the requested
    // performance write when the authoritative HyperBoost restoration is unknown.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerformancePreset? HyperBoostRestoredPerformance { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(ObserveStateRequest), "observeState")]
[JsonDerivedType(typeof(SubscribeStatusRequest), "subscribeStatus")]
[JsonDerivedType(typeof(ExportDiagnosticsRequest), "exportDiagnostics")]
[JsonDerivedType(typeof(UpdateProfilesRequest), "updateProfiles")]
[JsonDerivedType(typeof(SetPerformanceOverrideRequest), "setPerformanceOverride")]
[JsonDerivedType(typeof(SetGpuClockTuningRequest), "setGpuClockTuning")]
[JsonDerivedType(typeof(SetHyperBoostRequest), "setHyperBoost")]
[JsonDerivedType(typeof(ValidatePadlessHyperBoostLifecycleRequest), "validatePadlessHyperBoostLifecycle")]
[JsonDerivedType(typeof(ValidateThermalExternalDriftRequest), "validateThermalExternalDrift")]
[JsonDerivedType(typeof(CyclePerformanceShortcutRequest), "cyclePerformanceShortcut")]
[JsonDerivedType(typeof(SetPerformanceShortcutPreviewRequest), "setPerformanceShortcutPreview")]
[JsonDerivedType(typeof(SetFanOverrideRequest), "setFanOverride")]
[JsonDerivedType(typeof(SetLightingOverrideRequest), "setLightingOverride")]
[JsonDerivedType(typeof(StepKeyboardBrightnessRequest), "stepKeyboardBrightness")]
[JsonDerivedType(typeof(SetDisplayBrightnessHidUsageRequest), "setDisplayBrightnessHidUsage")]
[JsonDerivedType(typeof(SetKeyboardLightingControlModeRequest), "setKeyboardLightingControlMode")]
[JsonDerivedType(typeof(SetExternalLightingOverridePolicyRequest), "setExternalLightingOverridePolicy")]
[JsonDerivedType(typeof(SetLightingRefreshRateRequest), "setLightingRefreshRate")]
[JsonDerivedType(typeof(SetFirmwareCompatibilityOverrideRequest), "setFirmwareCompatibilityOverride")]
[JsonDerivedType(typeof(SetExperimentalFeaturesRequest), "setExperimentalFeatures")]
[JsonDerivedType(typeof(ReapplyChangedSettingsRequest), "reapplyChangedSettings")]
[JsonDerivedType(typeof(RecoverHyperBoostRestoreRequest), "recoverHyperBoostRestore")]
[JsonDerivedType(typeof(SetThermalControlPolicyRequest), "setThermalControlPolicy")]
[JsonDerivedType(typeof(AcknowledgeThermalPolicyUpgradeDisclosureRequest), "acknowledgeThermalPolicyUpgradeDisclosure")]
[JsonDerivedType(typeof(SetLightingSuppressionRequest), "setLightingSuppression")]
[JsonDerivedType(typeof(PulseReactiveKeyRequest), "pulseReactiveKey")]
[JsonDerivedType(typeof(PushAmbientLightingFrameRequest), "pushAmbientLightingFrame")]
[JsonDerivedType(typeof(SetPlaybackMuteIndicatorRequest), "setPlaybackMuteIndicator")]
[JsonDerivedType(typeof(SetBatteryPolicyRequest), "setBatteryPolicy")]
[JsonDerivedType(typeof(SetBatteryProtectionRequest), "setBatteryProtection")]
[JsonDerivedType(typeof(SetFullChargeOverrideRequest), "setFullChargeOverride")]
[JsonDerivedType(typeof(ApplyOneShotLaptopControlRequest), "applyOneShotLaptopControl")]
[JsonDerivedType(typeof(ShutdownOpenBladeRequest), "shutdownOpenBlade")]
[JsonDerivedType(typeof(ReportSessionPowerTransitionRequest), "reportSessionPowerTransition")]
public abstract record ServiceRequest(int Version, Guid RequestId);

public sealed record GetStatusRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

[Flags]
public enum LiveReadingSensors
{
    None = 0,
    BatteryPower = 1 << 0,
    FanSpeeds = 1 << 1,
    CpuTemperature = 1 << 2,
    GpuTemperature = 1 << 3,
    All = BatteryPower | FanSpeeds | CpuTemperature | GpuTemperature,
}

public sealed record ObserveStateRequest(
    int Version,
    Guid RequestId,
    bool IncludeLiveReadings)
    : ServiceRequest(Version, RequestId)
{
    // Added as an optional property so older clients continue to request the
    // original all-sensor observation when the property is absent.
    public LiveReadingSensors RequestedLiveReadings { get; init; } = LiveReadingSensors.All;

    public LiveReadingSensors EffectiveLiveReadings => IncludeLiveReadings
        ? RequestedLiveReadings
        : LiveReadingSensors.None;
}

public sealed record SubscribeStatusRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

public sealed record ExportDiagnosticsRequest(
    int Version,
    Guid RequestId,
    bool LiveReadingsEnabled)
    : ServiceRequest(Version, RequestId);

public sealed record UpdateProfilesRequest(int Version, Guid RequestId, ProfileSet Profiles)
    : ServiceRequest(Version, RequestId);

public sealed record SetPerformanceOverrideRequest(
    int Version,
    Guid RequestId,
    PerformanceOverride? Value)
    : ServiceRequest(Version, RequestId);

public sealed record SetHyperBoostRequest(
    int Version,
    Guid RequestId,
    bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record SetGpuClockTuningRequest(
    int Version,
    Guid RequestId,
    GpuClockTuningSettings Value,
    OverrideLifetime Lifetime)
    : ServiceRequest(Version, RequestId);

public enum PadlessHyperBoostLifecycleScenario
{
    LowPower,
    UnexpectedExit,
}

public sealed record ValidatePadlessHyperBoostLifecycleRequest(
    int Version,
    Guid RequestId,
    PadlessHyperBoostLifecycleScenario Scenario,
    Guid ValidationRunId,
    string ExpectedSourceRevision,
    string ExpectedServiceAssemblySha256,
    string ExpectedAdmissionManifestSha256)
    : ServiceRequest(Version, RequestId);

public sealed record ValidateThermalExternalDriftRequest(
    int Version,
    Guid RequestId,
    Guid ValidationRunId,
    string ExpectedSourceRevision,
    string ExpectedServiceAssemblySha256,
    string ExpectedProtocolAssemblySha256,
    string ExpectedProductionFixtureSha256)
    : ServiceRequest(Version, RequestId);

public enum PadlessHyperBoostEligibilityState
{
    Unknown,
    CoolingPadAbsent,
    CoolingPadPresent,
}

public sealed record CyclePerformanceShortcutRequest(
    int Version,
    Guid RequestId,
    PerformanceShortcutSource Source)
    : ServiceRequest(Version, RequestId);

public sealed record SetPerformanceShortcutPreviewRequest(
    int Version,
    Guid RequestId,
    Guid SessionId,
    long Generation,
    PerformancePreset? Preset)
    : ServiceRequest(Version, RequestId);

public sealed record PerformanceShortcutPreview(
    Guid SessionId,
    long Generation,
    PerformancePreset Preset);

public sealed record SetFanOverrideRequest(
    int Version,
    Guid RequestId,
    ManualOverride<FanMode>? Value)
    : ServiceRequest(Version, RequestId)
{
    public FanControlSettings? Settings { get; init; }
}

public sealed record SetLightingOverrideRequest(
    int Version,
    Guid RequestId,
    ManualOverride<LightingSettings>? Value)
    : ServiceRequest(Version, RequestId);

public sealed record StepKeyboardBrightnessRequest(
    int Version,
    Guid RequestId,
    KeyboardBrightnessDirection Direction,
    OverrideLifetime Lifetime)
    : ServiceRequest(Version, RequestId);

public enum DisplayBrightnessHidUsage : ushort
{
    Released = 0x0000,
    Increment = 0x006F,
    Decrement = 0x0070,
}

public sealed record SetDisplayBrightnessHidUsageRequest(
    int Version,
    Guid RequestId,
    DisplayBrightnessHidUsage Usage)
    : ServiceRequest(Version, RequestId);

public enum KeyboardLightingControlMode
{
    OpenBlade,
    WindowsDynamicLighting,
}

public sealed record SetKeyboardLightingControlModeRequest(
    int Version,
    Guid RequestId,
    KeyboardLightingControlMode Mode)
    : ServiceRequest(Version, RequestId);

public sealed record SetExternalLightingOverridePolicyRequest(
    int Version,
    Guid RequestId,
    bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record SetLightingRefreshRateRequest(
    int Version,
    Guid RequestId,
    LightingRefreshRate RefreshRate)
    : ServiceRequest(Version, RequestId);

public sealed record SetFirmwareCompatibilityOverrideRequest(
    int Version,
    Guid RequestId,
    bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record SetExperimentalFeaturesRequest(
    int Version,
    Guid RequestId,
    bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record ReapplyChangedSettingsRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

public sealed record RecoverHyperBoostRestoreRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

public enum ThermalControlPolicy
{
    MaintainDesiredState,
    Strict,
}

public enum ThermalPolicyUpgradeDisclosureState
{
    NotRequired,
    UpgradeDisclosurePending,
    Acknowledged,
}

public sealed record SetThermalControlPolicyRequest(
    int Version,
    Guid RequestId,
    ThermalControlPolicy Policy)
    : ServiceRequest(Version, RequestId);

public sealed record AcknowledgeThermalPolicyUpgradeDisclosureRequest(
    int Version,
    Guid RequestId)
    : ServiceRequest(Version, RequestId);

public enum ThermalConvergenceState
{
    Matched,
    AwaitingObservation,
    Converging,
    Paused,
    Suppressed,
    External,
    RestorePending,
    ReconciliationUnavailable,
}

public enum ThermalComponent
{
    Performance,
    Fans,
}

public enum ThermalConvergenceReason
{
    None,
    StrictPolicy,
    UpgradeDisclosurePending,
    ConfigurationUnavailable,
    ConfigurationQuarantined,
    InactiveSession,
    ProtocolCompatibilityClient,
    AwaitingFreshObservation,
    DeviceUnavailable,
    DeviceIdentityBlocked,
    PowerUnsettled,
    LowPower,
    CompetingControllerPresent,
    CompetingControllerUncertain,
    HyperBoostActive,
    HyperBoostPending,
    ManualFanReadbackAmbiguous,
    PendingReconciliation,
    BudgetExhausted,
    Tombstoned,
    Shutdown,
    InternalStateUnavailable,
}

public sealed record ThermalConvergenceStatus(
    ThermalControlPolicy Policy,
    ThermalPolicyUpgradeDisclosureState Disclosure,
    ThermalConvergenceState State,
    IReadOnlyList<ThermalComponent> AffectedComponents,
    ThermalConvergenceReason Reason,
    bool ExplicitReapplyRequired);

public sealed record SetLightingSuppressionRequest(
    int Version,
    Guid RequestId,
    LightingSuppressionReason Reason,
    bool Suppressed)
    : ServiceRequest(Version, RequestId);

public sealed record PulseReactiveKeyRequest(
    int Version,
    Guid RequestId,
    ReactiveKeyPulse Pulse)
    : ServiceRequest(Version, RequestId);

public sealed record PushAmbientLightingFrameRequest(
    int Version,
    Guid RequestId,
    byte[] RgbBytes)
    : ServiceRequest(Version, RequestId);

public sealed record SetPlaybackMuteIndicatorRequest(
    int Version,
    Guid RequestId,
    bool Muted)
    : ServiceRequest(Version, RequestId);

public sealed record SetBatteryPolicyRequest(
    int Version,
    Guid RequestId,
    BatteryPolicy Value)
    : ServiceRequest(Version, RequestId);

public sealed record SetBatteryProtectionRequest(int Version, Guid RequestId, bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record SetFullChargeOverrideRequest(int Version, Guid RequestId, bool Enabled)
    : ServiceRequest(Version, RequestId);

public sealed record ApplyOneShotLaptopControlRequest(
    int Version,
    Guid RequestId,
    ApplyOneShotLaptopControl Value)
    : ServiceRequest(Version, RequestId);

public sealed record ShutdownOpenBladeRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

public enum SessionControlAccess
{
    Active,
    InactiveSession,
    NotInteractive,
}

public enum FirmwareCompatibilityStatus
{
    Unknown,
    Tested,
    UntestedBlocked,
    UntestedAllowed,
    ModelScoped,
}

public enum DisplayBrightnessHidAvailability
{
    Unknown,
    Available,
    Unavailable,
}

// Describes retained observation evidence, never authority to submit a write.
public enum DeviceObservationReason
{
    None = 0,
    CheckingConnection = 1,
    RefreshingControls = 2,
    RetainedPowerSource = 3,
}

public sealed record ServiceStatusSnapshot(
    long Revision,
    bool DeviceVerified,
    bool WritesBlocked,
    IReadOnlyList<string> PotentialControllers,
    string? ControllerDetectionWarning,
    IReadOnlyList<DeviceSubsystem> ExternalChanges,
    string Message,
    DeviceCapabilities? Capabilities,
    ConfirmedDeviceState? ConfirmedState,
    ProfileSet Profiles,
    DesiredDeviceState DesiredState,
    PowerSource ActivePowerSource,
    LiveReadings? LiveReadings,
    PerformanceOverride? PerformanceOverride,
    ManualOverride<FanMode>? FanOverride,
    ManualOverride<LightingSettings>? LightingOverride,
    bool FullChargeOverride,
    FirmwareVersions? Firmware = null,
    string? ServiceVersion = null,
    LightingRendererOwnershipState LightingRendererOwnership =
        LightingRendererOwnershipState.None,
    IReadOnlyList<LightingSuppressionReason>? LightingSuppressions = null,
    bool? StartupAnimationEnabled = null,
    FunctionKeyPrimaryMode? FunctionKeyPrimary = null,
    bool? GamingModeEnabled = null,
    bool AlwaysOverrideExternalLightingChanges = false,
    bool? KeyboardDriverModeActive = null,
    BatteryHealthStatus? BatteryHealth = null,
    KeyboardLightingControlMode KeyboardLightingControlMode =
        KeyboardLightingControlMode.OpenBlade,
    SessionControlAccess ControlAccess = SessionControlAccess.Active,
    bool AllowUntestedFirmware = false,
    FirmwareCompatibilityStatus FirmwareCompatibility =
        FirmwareCompatibilityStatus.Unknown,
    DisplayBrightnessHidAvailability DisplayBrightnessHidAvailability =
        DisplayBrightnessHidAvailability.Unknown,
    LightingRefreshRate LightingRefreshRate = LightingRefreshRate.ThirtyHz)
{
    // Missing values mean no transient-observation exception. Diagnostic text
    // must never be interpreted as a substitute for this typed state.
    public DeviceObservationReason ObservationReason { get; init; }

    public IReadOnlyList<DeviceSubsystem> UnconfirmedWrites { get; init; } = [];

    public FanControlSettings? FanControlOverride { get; init; }

    public PerformanceShortcutPreview? PerformanceShortcutPreview { get; init; }

    public BatteryPolicy? BatteryPolicyOverride { get; init; }

    public bool? BatteryProtectionOverride { get; init; }

    public PadlessHyperBoostEligibilityState PadlessHyperBoostEligibility { get; init; }

    public bool HyperBoostRestorePending { get; init; }

    // Null means the sender did not provide source-aware classification.
    // Consumers then use the conservative name-based policy.
    public IReadOnlyList<string>? HyperBoostBlockingControllers { get; init; }

    public ThermalConvergenceStatus? ThermalConvergence { get; init; }

    public bool AllowExperimentalFeatures { get; init; }

    public bool IsExperimentalDevice { get; init; }

    public string? ExperimentalSupportSource { get; init; }
    // True only when the latest device check read this laptop's manufacturer
    // or product name and it matches no admitted model. Read failures and
    // other identity blocks leave it false. It never grants hardware access.
    public bool UnsupportedLaptop { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "frame")]
[JsonDerivedType(typeof(ServiceResponse), "response")]
[JsonDerivedType(typeof(ServiceStatusEvent), "statusEvent")]
public abstract record ServiceServerFrame(int Version);

public sealed record DiagnosticArchive(string SuggestedFileName, byte[] ZipBytes);

public enum ServiceResponseErrorCode
{
    CapacityUnavailable,
}

public sealed record ServiceResponse(
    int Version,
    Guid RequestId,
    bool Success,
    string? Error,
    ServiceStatusSnapshot? Status,
    ControlOperationResult? Result,
    DiagnosticArchive? Archive = null)
    : ServiceServerFrame(Version)
{
    // Optional because most failures have no machine-actionable category.
    // Adding this member advanced the closed IPC protocol to version 41.
    public ServiceResponseErrorCode? ErrorCode { get; init; }
}

public sealed record ServiceStatusEvent(int Version, ServiceStatusSnapshot Status)
    : ServiceServerFrame(Version);
