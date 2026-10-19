using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionAudioIpc
{
    public const int ProtocolVersion = 2;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumBaseAudioDetailCharacters = 1024;
    // The admitted rznb12 stack returned 218 profile presets during runtime
    // validation. Retain a bounded list alongside the independent 64 KiB frame cap.
    public const int MaximumPresets = 256;
    public const int MaximumApplicationProfiles = 256;
    public const string PipeNamePrefix = "OpenBlade.Session.Audio.v2";
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
    AvailableReadOnly,
    LegacyComponentsUnavailable,
}

public enum ThxAudioOperationStatus
{
    Succeeded,
    Unavailable,
    Failed,
    Indeterminate,
}

public sealed record ThxAudioPreset(string Id, string Name);

public sealed record ThxAudioPresetCurve(
    string PresetId,
    IReadOnlyList<double> GainsDb);

public sealed record ThxAudioEqualizerBand(int FrequencyHz, double GainDb);

public enum ThxAudioEnhancementKind
{
    SoundNormalization,
    VoiceClarity,
}

public sealed record ThxAudioEnhancement(bool Enabled, int LevelPercent);

public enum ThxAudioFeatureAccess
{
    Available,
    ObservedOnly,
    Unavailable,
}

public enum ThxAudioApplicationMode
{
    Immersive,
    Competitive,
}

public sealed record ThxAudioApplicationProfile(
    string ExecutableName,
    bool SupportsImmersive,
    bool SupportsCompetitive);

public sealed record ThxAudioState(
    ThxAudioEndpoint Endpoint,
    bool SpatialEnabled,
    bool EqualizerEnabled,
    string? SelectedPresetId,
    IReadOnlyList<ThxAudioPreset> Presets)
{
    // Some backends expose EQ data without a separately verified on/off control.
    public ThxAudioFeatureAccess Equalizer { get; init; } = ThxAudioFeatureAccess.Available;

    public string? SelectedNativeProfileId { get; init; }

    public IReadOnlyList<ThxAudioEqualizerBand> EqualizerBands { get; init; } = [];

    public IReadOnlyList<ThxAudioPresetCurve> FactoryPresetCurves { get; init; } = [];

    public ThxAudioEnhancement? SoundNormalization { get; init; }

    public ThxAudioEnhancement? VoiceClarity { get; init; }

    public ThxAudioFeatureAccess ApplicationProfiles { get; init; } =
        ThxAudioFeatureAccess.ObservedOnly;

    public IReadOnlyList<ThxAudioApplicationProfile> ApplicationProfileCatalog { get; init; } = [];

    public bool ApplicationAutomationPaused { get; init; }

    public bool? SystemEffectsDisabled { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(DiscoverThxAudioRequest), "discover")]
[JsonDerivedType(typeof(ReadThxAudioRequest), "read")]
[JsonDerivedType(typeof(ConnectThxAudioRequest), "connect")]
[JsonDerivedType(typeof(SetThxSpatialRequest), "setSpatial")]
[JsonDerivedType(typeof(SetThxEqualizerRequest), "setEqualizer")]
[JsonDerivedType(typeof(SelectThxPresetRequest), "selectPreset")]
[JsonDerivedType(typeof(SetThxEnhancementRequest), "setEnhancement")]
[JsonDerivedType(typeof(ApplyThxApplicationProfileRequest), "applyApplicationProfile")]
public abstract record SessionAudioRequest(int Version, Guid RequestId);

public sealed record DiscoverThxAudioRequest(int Version, Guid RequestId)
    : SessionAudioRequest(Version, RequestId);

public sealed record ReadThxAudioRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint)
    : SessionAudioRequest(Version, RequestId);

public sealed record ConnectThxAudioRequest(
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
    string PresetId,
    IReadOnlyList<double> GainsDb)
    : SessionAudioRequest(Version, RequestId);

public sealed record SetThxEnhancementRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint,
    ThxAudioEnhancementKind Kind,
    bool Enabled,
    int? LevelPercent)
    : SessionAudioRequest(Version, RequestId);

public sealed record ApplyThxApplicationProfileRequest(
    int Version,
    Guid RequestId,
    ThxAudioEndpoint Endpoint,
    string ExecutableName,
    ThxAudioApplicationMode Mode)
    : SessionAudioRequest(Version, RequestId);

public sealed record SessionAudioResponse(
    int Version,
    Guid RequestId,
    ThxAudioOperationStatus Status,
    ThxAudioAvailability Availability,
    string? Error,
    ThxAudioState? State)
{
    // This optional discovery field completes the unreleased v2 shape. Omission
    // preserves existing response JSON and permits older responses without it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThxBaseAudioDiagnosticProof? BaseAudio { get; init; }
}

// Observational diagnostics only; these flags never authorize an operation.
public sealed record ThxBaseAudioDiagnosticProof(
    bool ExactRoot,
    bool Ready,
    bool CanInstall,
    string Detail);
