namespace OpenBlade.SessionAgent;

public interface IPlaybackMuteStateSource
{
    IAsyncEnumerable<bool?> WatchMutedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes the default Windows playback endpoint through Core Audio callbacks.
/// Volume notifications deliver mute changes immediately, while endpoint
/// notifications move the subscription when the default output changes.
/// </summary>
public sealed class WindowsPlaybackMuteStateSource : IPlaybackMuteStateSource
{
    public IAsyncEnumerable<bool?> WatchMutedAsync(
        CancellationToken cancellationToken) =>
        WindowsCoreAudioMuteStateSource.WatchMutedAsync(
            CoreAudioDataFlow.Render,
            [CoreAudioRole.Console],
            states => states.Count == 1 ? states.Single() : null,
            cancellationToken);
}
