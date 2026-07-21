using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface IReactiveKeyboardSource : IDisposable
{
    Task Completion { get; }
}

public interface IReactiveKeyboardSourceFactory
{
    IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish);

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut) =>
        Create(publish);
}

/// <summary>
/// Owns the one session-local Raw Input registration used by key-triggered
/// lighting and the captured performance shortcuts. The SessionAgent remains
/// running when the visible tray exits, so neither behavior depends on the UI.
/// </summary>
public sealed class ServiceReactiveLightingLoop : IAsyncDisposable
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IReactiveKeyboardSourceFactory sourceFactory;
    private readonly ReactiveKeyPulsePump pulsePump;
    private readonly PerformanceShortcutTriggerPump shortcutPump;
    private readonly IPerformanceShortcutNotificationSink shortcutNotifications;
    private readonly Action<Exception>? errorHandler;
    private IReactiveKeyboardSource? source;
    private int inputConnected;
    private int keyTriggeredActive;
    private int disposed;

    public ServiceReactiveLightingLoop(
        IServiceClient serviceClient,
        IReactiveKeyboardSourceFactory sourceFactory,
        Action<Exception>? errorHandler = null,
        IPerformanceShortcutNotificationSink? shortcutNotifications = null)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        this.errorHandler = errorHandler;
        this.shortcutNotifications = shortcutNotifications
            ?? NullPerformanceShortcutNotificationSink.Instance;
        pulsePump = new ReactiveKeyPulsePump(SendPulseAsync, errorHandler: ReportFailure);
        shortcutPump = new PerformanceShortcutTriggerPump(
            SendPerformanceShortcutAsync,
            errorHandler: ReportFailure);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await foreach (ServiceWatchUpdate update in serviceClient
                        .WatchStatusAsync(cancellationToken)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        switch (update)
                        {
                            case ServiceWatchUpdate.Connected connected:
                                ApplyAuthoritativeStatus(connected.Status);
                                break;
                            case ServiceWatchUpdate.Reconnecting:
                                // A subscription-open retry is not authoritative
                                // device or renderer state. Keep the session Raw
                                // Input window and last ownership active until the
                                // service publishes a replacement status; tearing
                                // it down here creates a visible dead zone on every
                                // short pipe reconnect.
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The status stream is advisory for renderer ownership. A
                    // transient pipe/read failure is not evidence that the
                    // session-local Raw Input registration or the last observed
                    // renderer stopped. Keep both until an authoritative status
                    // replaces them; otherwise every retry creates a one-second
                    // interval in which physical key presses are lost.
                    ReportFailure(exception);
                    await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            SetInputState(connected: false, keyTriggered: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        SetInputState(connected: false, keyTriggered: false);
        await pulsePump.DisposeAsync().ConfigureAwait(false);
        await shortcutPump.DisposeAsync().ConfigureAwait(false);
    }

    private void SetInputState(bool connected, bool keyTriggered)
    {
        Interlocked.Exchange(ref inputConnected, connected ? 1 : 0);
        Interlocked.Exchange(ref keyTriggeredActive, keyTriggered ? 1 : 0);
        if (!keyTriggered)
        {
            pulsePump.ClearPending();
        }

        if (!connected || Volatile.Read(ref disposed) != 0)
        {
            IReactiveKeyboardSource? previous = Interlocked.Exchange(ref source, null);
            previous?.Dispose();
            pulsePump.ClearPending();
            shortcutPump.ClearPending();
            return;
        }

        if (Volatile.Read(ref source) is not null)
        {
            return;
        }

        try
        {
            IReactiveKeyboardSource created = sourceFactory.Create(
                pulse =>
                {
                    if (Volatile.Read(ref keyTriggeredActive) != 0
                        && !pulsePump.TryEnqueue(pulse))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Key-triggered lighting input could not keep up and skipped a key press."));
                    }
                },
                shortcut =>
                {
                    if (!shortcutPump.TryEnqueue(shortcut))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Performance shortcut input could not keep up and skipped a button press."));
                    }
                });
            if (Volatile.Read(ref disposed) != 0
                || Interlocked.CompareExchange(ref source, created, null) is not null)
            {
                created.Dispose();
            }
            else
            {
                _ = ObserveSourceCompletionAsync(created);
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task ObserveSourceCompletionAsync(IReactiveKeyboardSource observed)
    {
        Exception? failure = null;
        try
        {
            await observed.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (!ReferenceEquals(
            Interlocked.CompareExchange(ref source, null, observed),
            observed))
        {
            return;
        }

        observed.Dispose();
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref inputConnected) == 0)
        {
            return;
        }

        ReportFailure(failure ?? new InvalidOperationException(
            "Reactive input observation stopped unexpectedly."));
        await Task.Delay(FailureRetryDelay).ConfigureAwait(false);
        if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref inputConnected) != 0)
        {
            SetInputState(
                connected: true,
                keyTriggered: Volatile.Read(ref keyTriggeredActive) != 0);
        }
    }

    private void ApplyAuthoritativeStatus(ServiceStatusSnapshot status)
    {
        bool ownedKeyTriggered = status.LightingRendererOwnership is
            LightingRendererOwnershipState.Reactive or
            LightingRendererOwnershipState.Ripple;
        if (ownedKeyTriggered)
        {
            SetInputState(connected: true, keyTriggered: true);
            return;
        }

        bool desiredKeyTriggered = status.DesiredState.Lighting.Value.Effect is
            LightingEffect.Reactive or LightingEffect.Ripple;
        bool previouslyKeyTriggered = Volatile.Read(ref keyTriggeredActive) != 0;
        bool transientAvailability = (desiredKeyTriggered
            || previouslyKeyTriggered && IsTemporaryDeviceAccessStatus(status))
            && status.PotentialControllers.Count == 0
            && (!status.DeviceVerified || status.WritesBlocked)
            && status.LightingRendererOwnership != LightingRendererOwnershipState.Faulted;
        if (transientAvailability)
        {
            // A short HID/readback or power-classification miss is not evidence
            // that the renderer stopped. Preserve queued input; the service
            // validates every pulse against its current ownership before use.
            SetInputState(
                connected: true,
                keyTriggered: previouslyKeyTriggered);
            return;
        }

        SetInputState(connected: true, keyTriggered: false);
    }

    private static bool IsTemporaryDeviceAccessStatus(ServiceStatusSnapshot status) =>
        status.Message.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)
            && status.Message.Contains("laptop connection", StringComparison.OrdinalIgnoreCase)
        || status.Message.StartsWith(
            "Refreshing laptop controls after a temporary device-access failure",
            StringComparison.OrdinalIgnoreCase);

    private async Task SendPulseAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .PulseReactiveKeyAsync(pulse, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != ControlOperationStatus.Applied)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private async Task SendPerformanceShortcutAsync(
        PerformanceShortcutSource source,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .CyclePerformanceShortcutAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == ControlOperationStatus.Failed)
        {
            throw new InvalidOperationException(result.Message);
        }

        if (result.Status == ControlOperationStatus.Applied)
        {
            try
            {
                shortcutNotifications.ShowApplied(result.Message);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }
}
