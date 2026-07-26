using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Observes the default Windows playback endpoint in the signed-in session and
/// sends only its mute Boolean to the service-owned keyboard renderer.
/// </summary>
public sealed class ServicePlaybackMuteIndicatorLoop
{
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IPlaybackMuteStateSource muteStateSource;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<Exception>? errorHandler;
    private readonly Channel<byte> stateChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private int connected;
    // Latest state observed from Core Audio, independent of service delivery.
    private int latestMuteState = -1;
    // Successful IPC delivery cache only; failures and reconnects invalidate it.
    private int lastSentState = -1;

    public ServicePlaybackMuteIndicatorLoop(
        IServiceClient serviceClient,
        IPlaybackMuteStateSource muteStateSource,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IServiceStatusWatcher? statusWatcher = null)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.muteStateSource = muteStateSource
            ?? throw new ArgumentNullException(nameof(muteStateSource));
        this.statusWatcher = statusWatcher ?? serviceClient;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task watch = WatchStatusAsync(linked.Token);
        Task observe = WatchMuteStateAsync(linked.Token);
        Task send = SendChangesAsync(linked.Token);
        Task completed = await Task.WhenAny(watch, observe, send).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watch, observe, send).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }

        if (completed.IsFaulted)
        {
            await completed.ConfigureAwait(false);
        }
    }

    private async Task WatchStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            switch (update)
            {
                case ServiceWatchUpdate.Connected:
                    if (Interlocked.Exchange(ref connected, 1) == 0)
                    {
                        Interlocked.Exchange(ref lastSentState, -1);
                    }
                    SignalStateChanged();
                    break;
                case ServiceWatchUpdate.Reconnecting:
                    Volatile.Write(ref connected, 0);
                    Interlocked.Exchange(ref lastSentState, -1);
                    break;
            }
        }

        throw new IOException(
            "The playback mute indicator status subscription ended unexpectedly.");
    }

    private async Task WatchMuteStateAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await foreach (bool? muted in muteStateSource
                    .WatchMutedAsync(cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    Interlocked.Exchange(
                        ref latestMuteState,
                        muted is null ? -1 : muted.Value ? 1 : 0);
                    SignalStateChanged();
                }

                throw new IOException(
                    "The Windows playback mute subscription ended unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                errorHandler?.Invoke(exception);
                await delay(FailureRetryInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task SendChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (byte _ in stateChanges.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            int encoded = Volatile.Read(ref latestMuteState);
            if (Volatile.Read(ref connected) == 0
                || encoded is not (0 or 1)
                || Volatile.Read(ref lastSentState) == encoded)
            {
                continue;
            }

            try
            {
                ControlOperationResult result =
                    await serviceClient.SetPlaybackMuteIndicatorAsync(
                        encoded == 1,
                        cancellationToken).ConfigureAwait(false);
                if (result.Status is
                    ControlOperationStatus.Blocked or
                    ControlOperationStatus.Failed)
                {
                    throw new InvalidOperationException(result.Message);
                }

                Volatile.Write(ref lastSentState, encoded);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                errorHandler?.Invoke(exception);
                Interlocked.Exchange(ref lastSentState, -1);
                await delay(FailureRetryInterval, cancellationToken)
                    .ConfigureAwait(false);
                SignalStateChanged();
            }
        }
    }

    private void SignalStateChanged() => stateChanges.Writer.TryWrite(0);
}
