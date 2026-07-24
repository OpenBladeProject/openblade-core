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

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut,
        Action<SpecialKeySlot> publishSpecialKey,
        Func<SpecialKeySlot, bool> shouldOverrideSpecialKey,
        Action<SpecialKeySlot> publishDefaultLaptopKey,
        Action<FunctionRowAction> publishFunctionRowAction,
        Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode,
        Func<bool> isDriverModeActive) =>
        Create(
            publish,
            publishPerformanceShortcut,
            publishSpecialKey,
            shouldOverrideSpecialKey,
            publishFunctionRowAction,
            readFunctionKeyPrimaryMode,
            isDriverModeActive);

    IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut,
        Action<SpecialKeySlot> publishSpecialKey,
        Func<SpecialKeySlot, bool> shouldOverrideSpecialKey,
        Action<SpecialKeySlot> publishDefaultLaptopKey,
        Action<FunctionRowAction> publishFunctionRowAction,
        Func<FunctionKeyPrimaryMode?> readFunctionKeyPrimaryMode,
        Func<bool> isDriverModeActive,
        Func<GamingModeKeyPolicy> readGamingModeKeyPolicy) =>
        Create(
            publish,
            publishPerformanceShortcut,
            publishSpecialKey,
            shouldOverrideSpecialKey,
            publishDefaultLaptopKey,
            publishFunctionRowAction,
            readFunctionKeyPrimaryMode,
            isDriverModeActive);
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
    private readonly SpecialKeyTriggerPump? defaultLaptopKeyPump;
    private readonly Func<SpecialKeySlot, bool> shouldOverrideSpecialKey;
    private readonly IPerformanceShortcutNotificationSink shortcutNotifications;
    private readonly Action<Exception>? errorHandler;
    private readonly FunctionRowActionPump? functionRowActionPump;
    private readonly object performanceShortcutStateSync = new();
    private readonly SemaphoreSlim performanceShortcutPreviewGate = new(1, 1);
    private readonly Guid performanceShortcutSessionId = Guid.NewGuid();
    private IReactiveKeyboardSource? source;
    private ServiceStatusSnapshot? latestStatus;
    private PerformancePreset? optimisticPerformancePreset;
    private long performanceShortcutGeneration;
    private bool performanceShortcutInFlight;
    private int inputConnected;
    private int keyTriggeredActive;
    private int functionKeyPrimaryMode = -1;
    private int driverModeActive;
    private int gamingModeKeyPolicyBits;
    private int disposed;

    public ServiceReactiveLightingLoop(
        IServiceClient serviceClient,
        IReactiveKeyboardSourceFactory sourceFactory,
        Action<Exception>? errorHandler = null,
        IPerformanceShortcutNotificationSink? shortcutNotifications = null,
        IServiceStatusWatcher? statusWatcher = null,
        Func<SpecialKeySlot, bool>? shouldOverrideSpecialKey = null,
        Func<SpecialKeySlot, CancellationToken, Task>? executeSpecialKeyAsync = null,
        Func<SpecialKeySlot, CancellationToken, Task>? executeDefaultLaptopKeyAsync = null,
        Func<FunctionRowAction, CancellationToken, Task>? executeFunctionRowActionAsync = null,
        TimeSpan? performanceShortcutQuietPeriod = null)
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
            performanceShortcutQuietPeriod,
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
        if (executeDefaultLaptopKeyAsync is not null)
        {
            defaultLaptopKeyPump = new SpecialKeyTriggerPump(
                executeDefaultLaptopKeyAsync,
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
        if (defaultLaptopKeyPump is not null)
        {
            await defaultLaptopKeyPump.DisposeAsync().ConfigureAwait(false);
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
            CancelPendingPerformanceShortcut();
            specialKeyPump?.ClearPending();
            defaultLaptopKeyPump?.ClearPending();
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
                QueuePerformanceShortcut,
                specialKey =>
                {
                    if (specialKeyPump is null || !specialKeyPump.TryEnqueue(specialKey))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Special-key input could not keep up and skipped a button press."));
                    }
                },
                shouldOverrideSpecialKey,
                defaultLaptopKey =>
                {
                    if (defaultLaptopKeyPump is null
                        || !defaultLaptopKeyPump.TryEnqueue(defaultLaptopKey))
                    {
                        ReportFailure(new InvalidOperationException(
                            "Default laptop-key input could not keep up and skipped a button press."));
                    }
                },
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
                () => Volatile.Read(ref driverModeActive) != 0,
                ReadGamingModeKeyPolicy);
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
        lock (performanceShortcutStateSync)
        {
            latestStatus = status;
            if (!performanceShortcutInFlight)
            {
                optimisticPerformancePreset = null;
            }
        }

        if (!status.DeviceVerified)
        {
            Interlocked.Exchange(ref functionKeyPrimaryMode, -1);
            Interlocked.Exchange(ref driverModeActive, 0);
            functionRowActionPump?.ClearPending();
            Interlocked.Exchange(ref gamingModeKeyPolicyBits, 0);
        }
        else
        {
            // A verified laptop can briefly report either readback as unknown
            // while the service replaces a stale HID command session. Preserve
            // the last confirmed translation state through that gap. A newly
            // started Agent has no last readback to preserve, so use the active
            // profile's saved behavior until firmware readback recovers. Without
            // that fallback, Driver mode remains loaded but F1-F12 translation
            // silently stays disabled after a transient device-verification loss.
            FunctionKeyPrimaryMode? effectivePrimaryMode =
                status.FunctionKeyPrimary
                ?? FunctionKeyPrimaryFromActiveProfile(status);
            if (effectivePrimaryMode is { } primaryMode)
            {
                Interlocked.Exchange(ref functionKeyPrimaryMode, (int)primaryMode);
            }

            if (status.KeyboardDriverModeActive is { } active)
            {
                Interlocked.Exchange(ref driverModeActive, active ? 1 : 0);
                if (!active)
                {
                    functionRowActionPump?.ClearPending();
                }
            }

            LaptopBehaviorProfileSettings behavior =
                status.ActivePowerSource is
                    PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                    ? status.Profiles
                        .ForPowerSource(status.ActivePowerSource)
                        .EffectiveLaptopBehavior
                    : LaptopBehaviorProfileSettings.Unconfigured();
            bool gamingModeActive =
                status.GamingModeEnabled ?? behavior.GamingModeEnabled is true;
            GamingModeKeyPolicy policy = gamingModeActive
                ? behavior.EffectiveGamingModeKeyPolicy
                : GamingModeKeyPolicy.Disabled;
            Interlocked.Exchange(
                ref gamingModeKeyPolicyBits,
                (policy.BlockWindowsKey ? 1 : 0)
                | (policy.BlockAltTab ? 2 : 0));
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

    private static FunctionKeyPrimaryMode? FunctionKeyPrimaryFromActiveProfile(
        ServiceStatusSnapshot status)
    {
        if (status.ActivePowerSource is not (
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return null;
        }

        return status.Profiles
            .ForPowerSource(status.ActivePowerSource)
            .EffectiveLaptopBehavior
            .FunctionKeyPrimary;
    }

    private FunctionKeyPrimaryMode? ReadFunctionKeyPrimaryMode()
    {
        int value = Volatile.Read(ref functionKeyPrimaryMode);
        return Enum.IsDefined(typeof(FunctionKeyPrimaryMode), value)
            ? (FunctionKeyPrimaryMode)value
            : null;
    }

    private GamingModeKeyPolicy ReadGamingModeKeyPolicy()
    {
        int bits = Volatile.Read(ref gamingModeKeyPolicyBits);
        return new GamingModeKeyPolicy(
            BlockWindowsKey: (bits & 1) != 0,
            BlockAltTab: (bits & 2) != 0);
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

    private void QueuePerformanceShortcut(PerformanceShortcutSource source)
    {
        PerformanceShortcutIntent intent = BeginPerformanceShortcut(source);
        if (intent.Target is { } selected)
        {
            _ = PublishPerformanceShortcutPreviewAsync(
                intent.Generation,
                selected,
                CancellationToken.None);
            try
            {
                shortcutNotifications.ShowSelected(selected);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }

        if (!shortcutPump.TryEnqueue(intent))
        {
            CancelPerformanceShortcut(intent);
            ReportFailure(new InvalidOperationException(
                "Performance shortcut input could not be queued."));
        }
    }

    private void CancelPendingPerformanceShortcut()
    {
        if (shortcutPump.ClearPending() is { } discarded)
        {
            CancelPerformanceShortcut(discarded);
        }
    }

    private void CancelPerformanceShortcut(PerformanceShortcutIntent intent)
    {
        _ = CompletePerformanceShortcut(intent.Generation, applied: false);
        if (intent.Target is not null)
        {
            _ = PublishPerformanceShortcutPreviewAsync(
                intent.Generation,
                preset: null,
                CancellationToken.None);
        }
    }

    private async Task SendPerformanceShortcutAsync(
        PerformanceShortcutIntent intent,
        CancellationToken cancellationToken)
    {
        ControlOperationResult result;
        bool refreshConfirmedState = false;
        try
        {
            result = intent.Target is { } selected
                ? await serviceClient.SetPerformanceConfigurationOverrideAsync(
                    CreatePerformanceShortcutOverride(intent, selected),
                    cancellationToken).ConfigureAwait(false)
                : await serviceClient.CyclePerformanceShortcutAsync(
                    intent.Source,
                    cancellationToken).ConfigureAwait(false);
            refreshConfirmedState = result.Status != ControlOperationStatus.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = CompletePerformanceShortcut(intent.Generation, applied: false);
            throw;
        }
        catch (Exception exception)
        {
            refreshConfirmedState = true;
            if (CompletePerformanceShortcut(intent.Generation, applied: false))
            {
                TryShowPerformanceShortcutFailure(exception.Message);
            }
            throw;
        }
        finally
        {
            if (refreshConfirmedState)
            {
                await RefreshConfirmedPerformanceAfterFailureAsync()
                    .ConfigureAwait(false);
            }
            await PublishPerformanceShortcutPreviewAsync(
                intent.Generation,
                preset: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        bool latestIntent = CompletePerformanceShortcut(
            intent.Generation,
            result.Status == ControlOperationStatus.Applied);
        if (result.Status == ControlOperationStatus.Failed)
        {
            if (latestIntent)
            {
                TryShowPerformanceShortcutFailure(result.Message);
            }
            throw new InvalidOperationException(result.Message);
        }

        if (result.Status != ControlOperationStatus.Applied)
        {
            if (latestIntent && intent.Target is not null)
            {
                TryShowPerformanceShortcutFailure(result.Message);
            }
            return;
        }

        bool predictionMatched = intent.Target is not null;
        if (!predictionMatched)
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

    private async Task RefreshConfirmedPerformanceAfterFailureAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await serviceClient.ObserveStateAsync(
                    LiveReadingSensors.None,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async Task PublishPerformanceShortcutPreviewAsync(
        long generation,
        PerformancePreset? preset,
        CancellationToken cancellationToken)
    {
        try
        {
            await performanceShortcutPreviewGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                ControlOperationResult result = await serviceClient
                    .SetPerformanceShortcutPreviewAsync(
                        performanceShortcutSessionId,
                        generation,
                        preset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status is ControlOperationStatus.Failed
                    or ControlOperationStatus.Blocked)
                {
                    throw new InvalidOperationException(result.Message);
                }
            }
            finally
            {
                performanceShortcutPreviewGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private PerformanceShortcutIntent BeginPerformanceShortcut(
        PerformanceShortcutSource source)
    {
        lock (performanceShortcutStateSync)
        {
            long generation = ++performanceShortcutGeneration;
            performanceShortcutInFlight = true;
            ServiceStatusSnapshot? status = latestStatus;
            if (status is null || !status.DeviceVerified || status.WritesBlocked)
            {
                return new PerformanceShortcutIntent(source, null, null, generation);
            }

            PerformancePreset current = optimisticPerformancePreset
                ?? (status.ExternalChanges.Contains(DeviceSubsystem.Performance)
                    && status.ConfirmedState?.Performance is { } observed
                        ? observed
                        : status.DesiredState.Performance.Value);
            if (!PerformanceShortcutSelection.TrySelectNext(
                status.ActivePowerSource,
                status.Capabilities?.PerformancePresets,
                current,
                out PerformancePreset next))
            {
                return new PerformanceShortcutIntent(
                    source,
                    status.ActivePowerSource,
                    null,
                    generation);
            }

            optimisticPerformancePreset = next;
            return new PerformanceShortcutIntent(
                source,
                status.ActivePowerSource,
                next,
                generation);
        }
    }

    private PerformanceOverride CreatePerformanceShortcutOverride(
        PerformanceShortcutIntent intent,
        PerformancePreset selected)
    {
        lock (performanceShortcutStateSync)
        {
            ServiceStatusSnapshot status = latestStatus
                ?? throw new InvalidOperationException(
                    "The current laptop state is unavailable.");
            if (intent.PowerSource is { } expectedPowerSource
                && status.ActivePowerSource != expectedPowerSource)
            {
                throw new InvalidOperationException(
                    "The power source changed before the selected mode could be applied.");
            }

            CustomPerformanceSettings? custom = selected == PerformancePreset.Custom
                ? status.Profiles.PluggedIn.CustomPerformance
                    ?? CustomPerformanceSettings.Default
                : null;
            return new PerformanceOverride(
                selected,
                OverrideLifetime.UntilPowerSourceChange,
                custom);
        }
    }

    private bool CompletePerformanceShortcut(long generation, bool applied)
    {
        lock (performanceShortcutStateSync)
        {
            if (generation != performanceShortcutGeneration)
            {
                return false;
            }

            performanceShortcutInFlight = false;
            if (!applied)
            {
                optimisticPerformancePreset = null;
            }

            return true;
        }
    }

    private void TryShowPerformanceShortcutFailure(string message)
    {
        try
        {
            shortcutNotifications.ShowFailed(message);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
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
