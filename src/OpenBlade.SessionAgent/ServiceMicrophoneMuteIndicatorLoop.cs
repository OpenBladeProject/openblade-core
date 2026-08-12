using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Observes Windows capture mute state in the signed-in session and sends only
/// the authoritative Boolean to the service-owned microphone indicator.
/// </summary>
public sealed class ServiceMicrophoneMuteIndicatorLoop
{
    private readonly ServiceMuteIndicatorLoop inner;

    public ServiceMicrophoneMuteIndicatorLoop(
        IServiceClient serviceClient,
        IMicrophoneMuteStateSource muteStateSource,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentNullException.ThrowIfNull(muteStateSource);
        inner = new ServiceMuteIndicatorLoop(
            statusWatcher ?? serviceClient,
            muteStateSource.WatchMutedAsync,
            status => status.DeviceVerified
                && !status.WritesBlocked
                && status.Capabilities?.OneShotLaptopControls.Contains(
                    OneShotLaptopControl.MicrophoneMuteIndicator) == true,
            (muted, token) => serviceClient.ApplyOneShotLaptopControlAsync(
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.MicrophoneMuteIndicator,
                    Enabled: muted),
                token),
            "The microphone mute indicator status subscription ended unexpectedly.",
            "The Windows microphone mute subscription ended unexpectedly.",
            errorHandler,
            delay);
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        inner.RunAsync(cancellationToken);
}
