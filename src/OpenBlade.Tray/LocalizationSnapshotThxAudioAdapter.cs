#if OPENBLADE_DEVELOPER_HARNESS
using OpenBlade.Session;

namespace OpenBlade.Tray;

/// <summary>Disconnected UI data; never contacts a pipe or native component.</summary>
internal sealed class LocalizationSnapshotThxAudioAdapter(bool usesRznb11 = false, bool systemEffectsDisabled = false, string? detectionError = null) : IThxAudioAdapter
{
    public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Response(null));

    public Task<SessionAudioResponse> ConnectAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken) =>
        ReadAsync(endpoint, cancellationToken);

    public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken) =>
        detectionError is not null
        ? Task.FromResult(new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, detectionError, null))
        : Task.FromResult(Response(new ThxAudioState(endpoint, true, true, "music",
            [new("game", "Game"), new("movie", "Movie"), new("music", "Music"), new("voice", "Voice"), new("custom", "Custom")])
        {
            SystemEffectsDisabled = systemEffectsDisabled ? true : null,
            EqualizerBands = (endpoint == ThxAudioEndpoint.Speakers && !usesRznb11
                ? new[] { 250, 500, 1000, 2000, 4000, 8000, 16000 }
                : new[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 })
                .Select(frequency => new ThxAudioEqualizerBand(frequency, 0)).ToArray(),
            SoundNormalization = endpoint == ThxAudioEndpoint.Speakers ? new(false, 50) : null,
            VoiceClarity = new(false, 50),
            ApplicationProfiles = ThxAudioFeatureAccess.Available,
            ApplicationProfileCatalog = [new("game.exe", true, true)],
            FactoryPresetCurves = new[] { "game", "movie", "music", "voice", "custom" }
                .Select(id => new ThxAudioPresetCurve(id, new double[endpoint == ThxAudioEndpoint.Speakers && !usesRznb11 ? 7 : 10])).ToArray(),
        }));

    public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, IReadOnlyList<double> gainsDb, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    public Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint endpoint, ThxAudioEnhancementKind kind, bool enabled, int? levelPercent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    private static SessionAudioResponse Response(ThxAudioState? state) =>
        new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, state);
}
#endif
