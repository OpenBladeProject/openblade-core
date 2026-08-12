using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Observes the default Windows playback endpoint in the signed-in session and
/// sends only its mute Boolean to the service-owned keyboard renderer.
/// </summary>
public sealed class ServicePlaybackMuteIndicatorLoop
{
    private readonly ServiceMuteIndicatorLoop inner;

    public ServicePlaybackMuteIndicatorLoop(
        IServiceClient serviceClient,
        IPlaybackMuteStateSource muteStateSource,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentNullException.ThrowIfNull(muteStateSource);
        inner = new ServiceMuteIndicatorLoop(
            statusWatcher ?? serviceClient,
            muteStateSource.WatchMutedAsync,
            capabilities => capabilities?.SupportsPlaybackMuteIndicator == true,
            serviceClient.SetPlaybackMuteIndicatorAsync,
            "The playback mute indicator status subscription ended unexpectedly.",
            "The Windows playback mute subscription ended unexpectedly.",
            errorHandler,
            delay);
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        inner.RunAsync(cancellationToken);
}
