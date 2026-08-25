using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class ServiceMuteIndicatorLoop
{
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceStatusWatcher statusWatcher;
    private readonly Func<CancellationToken, IAsyncEnumerable<bool?>> watchMuted;
    private readonly Func<ServiceStatusSnapshot, bool> canSend;
    private readonly Func<bool, CancellationToken, Task<ControlOperationResult>> setIndicator;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<Exception>? errorHandler;
    private readonly string statusEndedMessage;
    private readonly string muteSubscriptionEndedMessage;
    private readonly Channel<byte> stateChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private int connected;
    private int deliveryEnabled;
    private int statusGeneration;
    private int blockedStatusGeneration = -1;
    private int latestMuteState = -1;
    private int lastSentState = -1;

    public ServiceMuteIndicatorLoop(
        IServiceStatusWatcher statusWatcher,
        Func<CancellationToken, IAsyncEnumerable<bool?>> watchMuted,
        Func<ServiceStatusSnapshot, bool> canSend,
        Func<bool, CancellationToken, Task<ControlOperationResult>> setIndicator,
        string statusEndedMessage,
        string muteSubscriptionEndedMessage,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.statusWatcher = statusWatcher
            ?? throw new ArgumentNullException(nameof(statusWatcher));
        this.watchMuted = watchMuted
            ?? throw new ArgumentNullException(nameof(watchMuted));
        this.canSend = canSend
            ?? throw new ArgumentNullException(nameof(canSend));
        this.setIndicator = setIndicator
            ?? throw new ArgumentNullException(nameof(setIndicator));
        this.statusEndedMessage = statusEndedMessage
            ?? throw new ArgumentNullException(nameof(statusEndedMessage));
        this.muteSubscriptionEndedMessage = muteSubscriptionEndedMessage
            ?? throw new ArgumentNullException(nameof(muteSubscriptionEndedMessage));
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
                case ServiceWatchUpdate.Connected connectedUpdate:
                    Interlocked.Increment(ref statusGeneration);
                    int nextEnabled = canSend(connectedUpdate.Status)
                        ? 1
                        : 0;
                    bool connectionChanged =
                        Interlocked.Exchange(ref connected, 1) == 0;
                    bool deliveryChanged =
                        Interlocked.Exchange(ref deliveryEnabled, nextEnabled)
                            != nextEnabled;
                    if (connectionChanged
                        || deliveryChanged
                        || connectedUpdate.SubscriptionOpened)
                    {
                        Interlocked.Exchange(ref lastSentState, -1);
                    }
                    SignalStateChanged();
                    break;
                case ServiceWatchUpdate.Reconnecting:
                    Interlocked.Increment(ref statusGeneration);
                    Volatile.Write(ref connected, 0);
                    Volatile.Write(ref deliveryEnabled, 0);
                    Interlocked.Exchange(ref lastSentState, -1);
                    break;
            }
        }

        throw new IOException(statusEndedMessage);
    }

    private async Task WatchMuteStateAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await foreach (bool? muted in watchMuted(cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    Interlocked.Exchange(
                        ref latestMuteState,
                        muted is null ? -1 : muted.Value ? 1 : 0);
                    SignalStateChanged();
                }

                throw new IOException(muteSubscriptionEndedMessage);
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
            int currentStatusGeneration = Volatile.Read(ref statusGeneration);
            if (Volatile.Read(ref connected) == 0
                || Volatile.Read(ref deliveryEnabled) == 0
                || Volatile.Read(ref blockedStatusGeneration)
                    == currentStatusGeneration
                || encoded is not (0 or 1)
                || Volatile.Read(ref lastSentState) == encoded)
            {
                continue;
            }

            try
            {
                ControlOperationResult result = await setIndicator(
                    encoded == 1,
                    cancellationToken).ConfigureAwait(false);
                if (result.Status == ControlOperationStatus.Blocked)
                {
                    Volatile.Write(
                        ref blockedStatusGeneration,
                        currentStatusGeneration);
                    errorHandler?.Invoke(new InvalidOperationException(result.Message));
                    continue;
                }
                if (result.Status == ControlOperationStatus.Failed)
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
