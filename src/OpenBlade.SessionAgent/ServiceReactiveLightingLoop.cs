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

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut,
        Action<SpecialKeySlot> publishSpecialKey,
        Func<SpecialKeySlot, bool> shouldOverrideSpecialKey) =>
        Create(publish, publishPerformanceShortcut);

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut,
        Action<SpecialKeySlot> publishSpecialKey,
        Func<SpecialKeySlot, bool> shouldOverrideSpecialKey,
        Action<FunctionRowAction> publishFunctionRowAction,
        Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode,
        Func<bool> isDriverModeActive) =>
        Create(
            publish,
            publishPerformanceShortcut,
            publishSpecialKey,
            shouldOverrideSpecialKey);
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
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IReactiveKeyboardSourceFactory sourceFactory;
    private readonly ReactiveKeyPulsePump pulsePump;
    private readonly PerformanceShortcutTriggerPump shortcutPump;
    private readonly SpecialKeyTriggerPump? specialKeyPump;
    private readonly Func<SpecialKeySlot, bool> shouldOverrideSpecialKey;
    private readonly IPerformanceShortcutNotificationSink shortcutNotifications;
    private readonly Action<Exception>? errorHandler;
    private readonly FunctionRowActionPump? functionRowActionPump;
    private IReactiveKeyboardSource? source;
    private int inputConnected;
    private int keyTriggeredActive;
    private int functionKeyPrimaryMode = -1;
    private int driverModeActive;
    private int disposed;

    public ServiceReactiveLightingLoop(
        IServiceClient serviceClient,
        IReactiveKeyboardSourceFactory sourceFactory,
        Action<Exception>? errorHandler = null,
        IPerformanceShortcutNotificationSink? shortcutNotifications = null,
        IServiceStatusWatcher? statusWatcher = null,
        Func<SpecialKeySlot, bool>? shouldOverrideSpecialKey = null,
        Func<SpecialKeySlot, CancellationToken, Task>? executeSpecialKeyAsync = null,
        Func<FunctionRowAction, CancellationToken, Task>? executeFunctionRowActionAsync = null)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        this.statusWatcher = statusWatcher ?? serviceClient;
        this.errorHandler = errorHandler;
        this.shortcutNotifications = shortcutNotifications
            ?? NullPerformanceShortcutNotificationSink.Instance;
        pulsePump = new ReactiveKeyPulsePump(SendPulseAsync, errorHandler: ReportFailure);
        shortcutPump = new PerformanceShortcutTriggerPump(
            SendPerformanceShortcutAsync,
            errorHandler: ReportFailure);
        this.shouldOverrideSpecialKey = shouldOverrideSpecialKey ?? (_ => false);
        if (executeFunctionRowActionAsync is not null)
        {
            functionRowActionPump = new FunctionRowActionPump(
                executeFunctionRowActionAsync,
                errorHandler: ReportFailure);
        }
        if (executeSpecialKeyAsync is not null)
        {
            specialKeyPump = new SpecialKeyTriggerPump(
                executeSpecialKeyAsync,
                errorHandler: ReportFailure);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Raw Input is session-local and is also responsible for user-configured
        // M1-M5 actions that do not require the service. Start it immediately
        // rather than making ordinary key bindings depend on the first service
        // status message.
        SetInputState(connected: true, keyTriggered: false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await foreach (ServiceWatchUpdate update in statusWatcher
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

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IOException(
                        "The service status subscription ended unexpectedly.");
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
        if (specialKeyPump is not null)
        {
            await specialKeyPump.DisposeAsync().ConfigureAwait(false);
        }
        if (functionRowActionPump is not null)
        {
            await functionRowActionPump.DisposeAsync().ConfigureAwait(false);
        }
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
            specialKeyPump?.ClearPending();
            functionRowActionPump?.ClearPending();
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
                },
                specialKey =>
                {
                    if (specialKeyPump is null || !specialKeyPump.TryEnqueue(specialKey))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Special-key input could not keep up and skipped a button press."));
                    }
                },
                shouldOverrideSpecialKey,
                action =>
                {
                    if (functionRowActionPump is null
                        || !functionRowActionPump.TryEnqueue(action))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Function-row input could not keep up and skipped a key press."));
                    }
                },
                ReadFunctionKeyPrimaryMode,
                () => Volatile.Read(ref driverModeActive) != 0);
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
        Interlocked.Exchange(
            ref functionKeyPrimaryMode,
            status.FunctionKeyPrimary is { } primaryMode
                ? (int)primaryMode
                : -1);
        Interlocked.Exchange(
            ref driverModeActive,
            status.KeyboardDriverModeActive == true ? 1 : 0);
        if (status.KeyboardDriverModeActive != true)
        {
            functionRowActionPump?.ClearPending();
        }

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

    private FunctionKeyPrimaryMode? ReadFunctionKeyPrimaryMode()
    {
        int value = Volatile.Read(ref functionKeyPrimaryMode);
        return Enum.IsDefined(typeof(FunctionKeyPrimaryMode), value)
            ? (FunctionKeyPrimaryMode)value
            : null;
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
