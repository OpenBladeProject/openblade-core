using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionAudioIpc
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    // The admitted rznb12 stack returned 218 profile presets during runtime
    // validation. Retain a bounded list alongside the independent 64 KiB frame cap.
    public const int MaximumPresets = 256;
    public const string PipeNamePrefix = "OpenBlade.Session.Audio.v1";
}

public enum ThxAudioEndpoint
{
    Speakers,
    Headphones,
}

public enum ThxAudioAvailability
{
    Disabled,
    UnsupportedModel,
    ComponentsUnavailable,
    ControllerActive,
    Available,
}

public enum ThxAudioOperationStatus
{
    Succeeded,
    Unavailable,
    Failed,
    Indeterminate,
}

public sealed record ThxAudioPreset(string Id, string Name);

public sealed record ThxAudioState(
    ThxAudioEndpoint Endpoint,
    bool SpatialEnabled,
    bool EqualizerEnabled,
    string? SelectedPresetId,
    IReadOnlyList<ThxAudioPreset> Presets);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(DiscoverThxAudioRequest), "discover")]
[JsonDerivedType(typeof(ReadThxAudioRequest), "read")]
[JsonDerivedType(typeof(SetThxSpatialRequest), "setSpatial")]
[JsonDerivedType(typeof(SetThxEqualizerRequest), "setEqualizer")]
[JsonDerivedType(typeof(SelectThxPresetRequest), "selectPreset")]
public abstract record SessionAudioRequest(int Version, Guid RequestId);

public sealed record DiscoverThxAudioRequest(int Version, Guid RequestId)
    : SessionAudioRequest(Version, RequestId);

public sealed record ReadThxAudioRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint)
    : SessionAudioRequest(Version, RequestId);

public sealed record SetThxSpatialRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint,
    bool Enabled)
    : SessionAudioRequest(Version, RequestId);

public sealed record SetThxEqualizerRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint,
    bool Enabled)
    : SessionAudioRequest(Version, RequestId);

public sealed record SelectThxPresetRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint,
    string PresetId)
    : SessionAudioRequest(Version, RequestId);

public sealed record SessionAudioResponse(
    int Version,
    Guid RequestId,
    ThxAudioOperationStatus Status,
    ThxAudioAvailability Availability,
    string? Error,
    ThxAudioState? State);
