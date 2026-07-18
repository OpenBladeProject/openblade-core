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
[JsonDerivedType(typeof(SetFanOverrideRequest), "setFanOverride")]
[JsonDerivedType(typeof(SetLightingOverrideRequest), "setLightingOverride")]
[JsonDerivedType(typeof(SetBatteryPolicyRequest), "setBatteryPolicy")]
[JsonDerivedType(typeof(SetBatteryProtectionRequest), "setBatteryProtection")]
[JsonDerivedType(typeof(SetFullChargeOverrideRequest), "setFullChargeOverride")]
public abstract record ServiceRequest(int Version, Guid RequestId);

public sealed record GetStatusRequest(int Version, Guid RequestId)
    : ServiceRequest(Version, RequestId);

public sealed record ObserveStateRequest(
    int Version,
    Guid RequestId,
    bool IncludeLiveReadings)
    : ServiceRequest(Version, RequestId);

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
    ManualOverride<PerformancePreset>? Value)
    : ServiceRequest(Version, RequestId);

public sealed record SetFanOverrideRequest(
    int Version,
    Guid RequestId,
    ManualOverride<FanMode>? Value)
    : ServiceRequest(Version, RequestId);

public sealed record SetLightingOverrideRequest(
    int Version,
    Guid RequestId,
    ManualOverride<LightingSettings>? Value)
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
    ManualOverride<PerformancePreset>? PerformanceOverride,
    ManualOverride<FanMode>? FanOverride,
    ManualOverride<LightingSettings>? LightingOverride,
    bool FullChargeOverride,
    FirmwareVersions? Firmware = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "frame")]
[JsonDerivedType(typeof(ServiceResponse), "response")]
[JsonDerivedType(typeof(ServiceStatusEvent), "statusEvent")]
public abstract record ServiceServerFrame(int Version);

public sealed record DiagnosticArchive(string SuggestedFileName, byte[] ZipBytes);

public sealed record ServiceResponse(
    int Version,
    Guid RequestId,
    bool Success,
    string? Error,
    ServiceStatusSnapshot? Status,
    ControlOperationResult? Result,
    DiagnosticArchive? Archive = null)
    : ServiceServerFrame(Version);

public sealed record ServiceStatusEvent(int Version, ServiceStatusSnapshot Status)
    : ServiceServerFrame(Version);
