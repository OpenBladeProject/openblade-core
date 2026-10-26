using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Display;

namespace OpenBlade.Session;

public static class SessionDisplayIpc
{
    public const int ProtocolVersion = 2;
    public const int MaximumMessageBytes = 64 * 1024;
    public const string PipeNamePrefix = "OpenBlade.Session.Display.v1";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(QueryDisplayRequest), "query")]
[JsonDerivedType(typeof(SetRefreshRateRequest), "setRefreshRate")]
[JsonDerivedType(typeof(SetHdrRequest), "setHdr")]
[JsonDerivedType(typeof(SetColorProfileRequest), "setColorProfile")]
[JsonDerivedType(typeof(SetBrightnessRequest), "setBrightness")]
[JsonDerivedType(typeof(ApplyDisplayProfileRequest), "applyProfile")]
public abstract record SessionDisplayRequest(int Version, Guid RequestId);

public sealed record QueryDisplayRequest(int Version, Guid RequestId)
    : SessionDisplayRequest(Version, RequestId);

public sealed record SetRefreshRateRequest(
    int Version,
    Guid RequestId,
    PowerSource ExpectedPowerSource,
    string RefreshRateKey)
    : SessionDisplayRequest(Version, RequestId);

public sealed record SetHdrRequest(
    int Version,
    Guid RequestId,
    PowerSource ExpectedPowerSource,
    bool Enabled)
    : SessionDisplayRequest(Version, RequestId);

public sealed record SetColorProfileRequest(
    int Version,
    Guid RequestId,
    PowerSource ExpectedPowerSource,
    string ColorProfileKey)
    : SessionDisplayRequest(Version, RequestId);

public sealed record SetBrightnessRequest(
    int Version,
    Guid RequestId,
    PowerSource ExpectedPowerSource,
    byte Percent)
    : SessionDisplayRequest(Version, RequestId);

public sealed record ApplyDisplayProfileRequest(
    int Version,
    Guid RequestId,
    PowerSource ExpectedPowerSource,
    DisplayProfileSettings Settings)
    : SessionDisplayRequest(Version, RequestId);

public enum SessionDisplayOperationStatus
{
    Succeeded,
    Blocked,
    Failed,
    Indeterminate,
}

public sealed record SessionDisplayResponse(
    int Version,
    Guid RequestId,
    SessionDisplayOperationStatus Status,
    string? Error,
    DisplaySettingsSnapshot State);
