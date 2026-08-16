using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum ControlOperationStatus
{
    Stored,
    Applied,
    Blocked,
    Failed,
}

public sealed record ControlOperationResult(ControlOperationStatus Status, string Message);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(ObserveStateRequest), "observeState")]
[JsonDerivedType(typeof(SubscribeStatusRequest), "subscribeStatus")]
[JsonDerivedType(typeof(ExportDiagnosticsRequest), "exportDiagnostics")]
[JsonDerivedType(typeof(UpdateProfilesRequest), "updateProfiles")]
[JsonDerivedType(typeof(SetPerformanceOverrideRequest), "setPerformanceOverride")]
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
[JsonDerivedType(typeof(ReapplyChangedSettingsRequest), "reapplyChangedSettings")]
[JsonDerivedType(typeof(SetLightingSuppressionRequest), "setLightingSuppression")]
[JsonDerivedType(typeof(PulseReactiveKeyRequest), "pulseReactiveKey")]
[JsonDerivedType(typeof(PushAmbientLightingFrameRequest), "pushAmbientLightingFrame")]
[JsonDerivedType(typeof(SetPlaybackMuteIndicatorRequest), "setPlaybackMuteIndicator")]
[JsonDerivedType(typeof(SetBatteryPolicyRequest), "setBatteryPolicy")]
[JsonDerivedType(typeof(SetBatteryProtectionRequest), "setBatteryProtection")]
[JsonDerivedType(typeof(SetFullChargeOverrideRequest), "setFullChargeOverride")]
[JsonDerivedType(typeof(ApplyOneShotLaptopControlRequest), "applyOneShotLaptopControl")]
[JsonDerivedType(typeof(ShutdownOpenBladeRequest), "shutdownOpenBlade")]
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

public sealed record ReapplyChangedSettingsRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

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
}

public enum DisplayBrightnessHidAvailability
{
    Unknown,
    Available,
    Unavailable,
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
    public IReadOnlyList<DeviceSubsystem> UnconfirmedWrites { get; init; } = [];

    public FanControlSettings? FanControlOverride { get; init; }

    public PerformanceShortcutPreview? PerformanceShortcutPreview { get; init; }

    public BatteryPolicy? BatteryPolicyOverride { get; init; }

    public bool? BatteryProtectionOverride { get; init; }
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
