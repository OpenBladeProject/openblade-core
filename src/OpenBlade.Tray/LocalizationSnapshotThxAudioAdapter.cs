#if OPENBLADE_DEVELOPER_HARNESS
using OpenBlade.Session;

namespace OpenBlade.Tray;

/// <summary>Disconnected UI data; never contacts a pipe or native component.</summary>
internal sealed class LocalizationSnapshotThxAudioAdapter : IThxAudioAdapter
{
    public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Response(null));

    public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken) =>
        Task.FromResult(Response(new ThxAudioState(endpoint, true, true, "music",
            [new("music", "Music"), new("game", "Game"), new("movie", "Movie")])));

    public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Audio mutations are disabled in the disconnected preview.");

    private static SessionAudioResponse Response(ThxAudioState? state) =>
        new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, state);
}
#endif
