using System.Diagnostics;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public enum Blade18FnDiagnosticStage
{
    FnReportObserved,
    HookRawInputConfirmed,
    ActionBlocked,
    ActionQueued,
    PendingActionsReset,
    LatestActionSuperseded,
    ExecutorStarted,
    ExecutorCompleted,
    ExecutorCanceled,
    ExecutorFailed,
}

public sealed record Blade18FnDiagnostic(
    Blade18FnDiagnosticStage Stage,
    Blade18FnShortcut? Shortcut = null,
    FunctionRowInputPhase? Phase = null,
    string? FailureType = null);

public interface IReactiveKeyboardSource : IDisposable
{
    Task Completion { get; }

    bool HasActiveFunctionRowKeys => false;
    bool CanTransferFunctionRowOwnership => false;

    void ResetFunctionPInput() { }
}

public sealed record ReactiveKeyboardSourceOptions(
    Action<ReactiveKeyPulse> PublishPulse,
    Action<PerformanceShortcutSource> PublishPerformanceShortcut,
    Action<SpecialKeySlot> PublishSpecialKey,
    Func<SpecialKeySlot, bool> ShouldOverrideSpecialKey,
    Action<SpecialKeySlot> PublishDefaultLaptopKey,
    Action<FunctionRowInputEvent> PublishFunctionRowInput,
    Func<FunctionKeyPrimaryMode?> ReadFunctionKeyPrimaryMode,
    Func<KeyboardDriverModeObservation> ReadDriverModeObservation,
    Func<GamingModeKeyPolicy> ReadGamingModeKeyPolicy,
    Func<RazerBladeInputDeviceKind> ReadVerifiedInputDeviceKind,
    Func<SpecialKeySlot, bool> IsSpecialKeyInputAdmitted)
{
    public bool ObserveOnly { get; init; }

    public Func<KeyboardInputBackend> ReadFunctionRowBackend { get; init; } = () => KeyboardInputBackend.WindowsHook;

    public Action<Blade18FnShortcut> PublishBlade18FnShortcut { get; init; } = _ => { };

    public Func<bool> IsBlade18FnInputAdmitted { get; init; } = () => false;

    public Action ResetFunctionRowInput { get; init; } = () => { };

    public Func<byte?> ReadBlade18KeyboardLayoutId { get; init; } = () => null;

    public Action<Blade18FnDiagnostic> ReportBlade18FnDiagnostic { get; init; } = _ => { };
}

public interface IReactiveKeyboardSourceFactory
{
    bool CanTransferFunctionRowOwnership => false;
    IReactiveKeyboardSource Create(ReactiveKeyboardSourceOptions options);
}

public sealed record ServiceReactiveLightingLoopOptions(
    Action<Exception>? ErrorHandler = null,
    IPerformanceShortcutNotificationSink? ShortcutNotifications = null,
    IServiceStatusWatcher? StatusWatcher = null,
    Func<SpecialKeySlot, bool>? ShouldOverrideSpecialKey = null,
    Func<SpecialKeySlot, CancellationToken, Task>? ExecuteSpecialKeyAsync = null,
    Func<SpecialKeySlot, CancellationToken, Task>? ExecuteDefaultLaptopKeyAsync = null,
    Func<FunctionRowInputEvent, CancellationToken, Task>? ExecuteFunctionRowInputAsync = null,
    Func<CancellationToken, Task>? ResetFunctionRowInputAsync = null,
    TimeSpan? PerformanceShortcutQuietPeriod = null,
    Func<Blade18FnShortcut, CancellationToken, Task>? ExecuteBlade18FnShortcutAsync = null,
    Action<Blade18FnDiagnostic>? Blade18FnDiagnostic = null,
    Func<KeyboardInputBackend>? ReadFunctionRowBackend = null,
    Action<SnapTapRuntimeStatus>? PublishFunctionRowStatus = null,
    Func<SnapTapRuntimeStatus?>? ReadFunctionRowStatusOverride = null);

/// <summary>
/// Owns the one session-local Raw Input registration used by key-triggered
/// lighting and the captured performance shortcuts. The SessionAgent remains
/// running when the visible tray exits, so neither behavior depends on the UI.
/// </summary>
public sealed class ServiceReactiveLightingLoop : IAsyncDisposable
{
    public bool CanTransferFunctionRowInput => Volatile.Read(ref source)?.CanTransferFunctionRowOwnership
        ?? sourceFactory.CanTransferFunctionRowOwnership;

    /// <summary>Called by preference/ownership notifications, never the keyboard callback.</summary>
    public void InvalidateFunctionRowBackendStatus()
    {
        Interlocked.Increment(ref functionRowStatusGeneration);
        PublishWindowsFunctionRowStatus();
    }

    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceClient serviceClient;
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly IReactiveKeyboardSourceFactory sourceFactory;
    private readonly ReactiveKeyPulsePump pulsePump;
    private readonly PerformanceShortcutTriggerPump shortcutPump;
    private readonly BoundedEnumActionPump<SpecialKeySlot>? specialKeyPump;
    private readonly BoundedEnumActionPump<SpecialKeySlot>? defaultLaptopKeyPump;
    private readonly BoundedEnumActionPump<Blade18FnShortcut>? blade18FnShortcutPump;
    private readonly Func<Blade18FnShortcut, CancellationToken, Task>?
        executeBlade18FnShortcutAsync;
    private readonly object blade18FnActionSync = new();
    private readonly Func<SpecialKeySlot, bool> shouldOverrideSpecialKey;
    private readonly IPerformanceShortcutNotificationSink shortcutNotifications;
    private readonly Action<Exception>? errorHandler;
    private readonly Action<Blade18FnDiagnostic> reportBlade18FnDiagnostic;
    private readonly FunctionRowInputPump? functionRowInputPump;
    private readonly Func<KeyboardInputBackend> readFunctionRowBackend;
    private readonly object functionRowStatusSync = new();
    private SnapTapRuntimeStatus? lastWindowsFunctionRowStatus;
    private int functionRowStatusGeneration;
    private int lastWindowsFunctionRowGeneration = -1;
    private readonly Action<SnapTapRuntimeStatus> publishFunctionRowStatus;
    private readonly Func<SnapTapRuntimeStatus?> readFunctionRowStatusOverride;
    private readonly object performanceShortcutStateSync = new();
    private readonly SemaphoreSlim performanceShortcutPreviewGate = new(1, 1);
    private readonly Guid performanceShortcutSessionId = Guid.NewGuid();
    private IReactiveKeyboardSource? source;
    private CancellationTokenSource? blade18FnActionGeneration = new();
    private ServiceStatusSnapshot? latestStatus;
    private SessionControlAccess? previousControlAccess;
    private PowerSource? previousPowerSource;
    private PerformancePreset? optimisticPerformancePreset;
    private long performanceShortcutGeneration;
    private long blade18PerformanceShortcutGeneration;
    private bool performanceShortcutInFlight;
    private int inputConnected;
    private int keyTriggeredActive;
    private int functionKeyPrimaryMode = -1;
    private int driverModeKnowledge;
    private long driverModeLastConfirmedTimestamp;
    private int gamingModeKeyPolicyBits;
    private int hostManagedGamingMode;
    private int verifiedInputDeviceKind;
    private int writableSpecialKeySlots;
    private int blade18FnInputAdmitted;
    private int disposed;

    public ServiceReactiveLightingLoop(
        IServiceClient serviceClient,
        IReactiveKeyboardSourceFactory sourceFactory,
        ServiceReactiveLightingLoopOptions? options = null)
    {
        options ??= new();
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        statusWatcher = options.StatusWatcher ?? serviceClient;
        errorHandler = options.ErrorHandler;
        readFunctionRowBackend = options.ReadFunctionRowBackend ?? (() => KeyboardInputBackend.WindowsHook);
        publishFunctionRowStatus = options.PublishFunctionRowStatus ?? (_ => { });
        readFunctionRowStatusOverride = options.ReadFunctionRowStatusOverride ?? (() => null);
        reportBlade18FnDiagnostic = options.Blade18FnDiagnostic ?? (_ => { });
        shortcutNotifications = options.ShortcutNotifications
            ?? NullPerformanceShortcutNotificationSink.Instance;
        pulsePump = new ReactiveKeyPulsePump(SendPulseAsync, errorHandler: ReportFailure);
        shortcutPump = new PerformanceShortcutTriggerPump(
            SendPerformanceShortcutAsync,
            options.PerformanceShortcutQuietPeriod,
            errorHandler: ReportFailure);
        shouldOverrideSpecialKey = options.ShouldOverrideSpecialKey ?? (_ => false);
        if (options.ExecuteFunctionRowInputAsync is not null)
        {
            functionRowInputPump = new FunctionRowInputPump(
                options.ExecuteFunctionRowInputAsync,
                options.ResetFunctionRowInputAsync ?? (_ => Task.CompletedTask),
                errorHandler: ReportFailure);
        }
        if (options.ExecuteSpecialKeyAsync is not null)
        {
            specialKeyPump = new BoundedEnumActionPump<SpecialKeySlot>(
                options.ExecuteSpecialKeyAsync,
                errorHandler: ReportFailure);
        }
        if (options.ExecuteDefaultLaptopKeyAsync is not null)
        {
            defaultLaptopKeyPump = new BoundedEnumActionPump<SpecialKeySlot>(
                options.ExecuteDefaultLaptopKeyAsync,
                errorHandler: ReportFailure);
        }
        if (options.ExecuteBlade18FnShortcutAsync is not null)
        {
            executeBlade18FnShortcutAsync = options.ExecuteBlade18FnShortcutAsync;
            blade18FnShortcutPump = new BoundedEnumActionPump<Blade18FnShortcut>(
                ExecuteBlade18FnShortcutAsync,
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
                                InvalidateBlade18FnInput();
                                InvalidateHostGamingMode();
                                SetDriverModeUnknown(
                                    "The service status connection is reconnecting.",
                                    resetInput: false);
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
                    InvalidateBlade18FnInput();
                    InvalidateHostGamingMode();
                    SetDriverModeUnknown(
                        "The service status connection failed.",
                        resetInput: false);
                    ReportFailure(exception);
                    await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            InvalidateBlade18FnInput();
            InvalidateHostGamingMode();
            SetDriverModeUnknown(
                "Reactive input observation is stopping.",
                warning: false,
                resetInput: false);
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
        if (blade18FnShortcutPump is not null)
        {
            await blade18FnShortcutPump.DisposeAsync().ConfigureAwait(false);
        }
        CancellationTokenSource? blade18FnActionCancellation;
        lock (blade18FnActionSync)
        {
            blade18FnActionCancellation = blade18FnActionGeneration;
            blade18FnActionGeneration = null;
        }
        blade18FnActionCancellation?.Cancel();
        blade18FnActionCancellation?.Dispose();
        if (functionRowInputPump is not null)
        {
            await functionRowInputPump.DisposeAsync().ConfigureAwait(false);
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
            CancelBlade18FnActions();
            ClearPendingBlade18FnActions();
            functionRowInputPump?.Reset();
            PublishWindowsFunctionRowStatus();
            return;
        }

        if (Volatile.Read(ref source) is not null)
        {
            PublishWindowsFunctionRowStatus();
            return;
        }

        try
        {
            IReactiveKeyboardSource created = sourceFactory.Create(
                new ReactiveKeyboardSourceOptions(
                    PublishPulse: pulse =>
                    {
                        if (Volatile.Read(ref keyTriggeredActive) != 0
                            && !pulsePump.TryEnqueue(pulse))
                        {
                            ReportFailure(new InvalidOperationException(
                                "Key-triggered lighting input could not keep up and skipped a key press."));
                        }
                    },
                    PublishPerformanceShortcut: QueuePerformanceShortcut,
                    PublishSpecialKey: specialKey =>
                    {
                        if (specialKeyPump is null
                            || !specialKeyPump.TryEnqueue(specialKey))
                        {
                            ReportFailure(new InvalidOperationException(
                                "Special-key input could not keep up and skipped a button press."));
                        }
                    },
                    ShouldOverrideSpecialKey: shouldOverrideSpecialKey,
                    PublishDefaultLaptopKey: defaultLaptopKey =>
                    {
                        if (defaultLaptopKeyPump is null
                            || !defaultLaptopKeyPump.TryEnqueue(defaultLaptopKey))
                        {
                            ReportFailure(new InvalidOperationException(
                                "Default laptop-key input could not keep up and skipped a button press."));
                        }
                    },
                    PublishFunctionRowInput: inputEvent =>
                    {
                        if (functionRowInputPump is null
                            || !functionRowInputPump.TryEnqueue(inputEvent))
                        {
                            ReportFailure(new InvalidOperationException(
                                "Function-row input could not keep up and skipped a key press."));
                        }
                    },
                    ReadFunctionKeyPrimaryMode: ReadFunctionKeyPrimaryMode,
                    ReadDriverModeObservation: ReadDriverModeObservation,
                    ReadGamingModeKeyPolicy: ReadGamingModeKeyPolicy,
                    ReadVerifiedInputDeviceKind: ReadVerifiedInputDeviceKind,
                    IsSpecialKeyInputAdmitted: IsSpecialKeyInputAdmitted)
                {
                    PublishBlade18FnShortcut = shortcut =>
                    {
                        if (shortcut == Blade18FnShortcut.Performance
                            || !IsBlade18FnInputAdmitted())
                        {
                            ReportBlade18FnDiagnostic(new(
                                Blade18FnDiagnosticStage.ActionBlocked,
                                shortcut));
                            return;
                        }
                        if (blade18FnShortcutPump!.TryEnqueue(shortcut))
                        {
                            ReportBlade18FnDiagnostic(new(
                                Blade18FnDiagnosticStage.ActionQueued,
                                shortcut));
                        }
                        else
                        {
                            ReportBlade18FnDiagnostic(new(
                                Blade18FnDiagnosticStage.ExecutorFailed,
                                shortcut,
                                FailureType: "QueueRejected"));
                            ReportFailure(new InvalidOperationException(
                                "Blade 18 Fn shortcut input could not keep up and skipped a key press."));
                        }
                    },
                    IsBlade18FnInputAdmitted = IsBlade18FnInputAdmitted,
                    ReadBlade18KeyboardLayoutId = ReadBlade18KeyboardLayoutId,
                    ReadFunctionRowBackend = () => readFunctionRowBackend().Normalize(),
                    ResetFunctionRowInput = () => functionRowInputPump?.Reset(),
                    ReportBlade18FnDiagnostic = ReportBlade18FnDiagnostic,
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
            PublishWindowsFunctionRowStatus();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void PublishWindowsFunctionRowStatus()
    {
        lock (functionRowStatusSync)
        {
            SnapTapRuntimeStatus? status = readFunctionRowStatusOverride();
            if (status is null)
            {
                if (readFunctionRowBackend().Normalize() != KeyboardInputBackend.WindowsHook)
                {
                    lastWindowsFunctionRowStatus = null;
                    return;
                }
                SnapTapRuntimeState state = Volatile.Read(ref source) is { } current
                    && !current.Completion.IsCompleted
                    && ReadDriverModeObservation().AllowsFunctionRowInterception
                    && ReadFunctionKeyPrimaryMode() is not null
                    && RazerBladeFunctionRowMediaInput.SupportsDevice(ReadVerifiedInputDeviceKind())
                        ? SnapTapRuntimeState.Armed : SnapTapRuntimeState.Paused;
                status = new(state, DateTimeOffset.UtcNow, Environment.ProcessId)
                { Backend = KeyboardInputBackend.WindowsHook };
            }
            int generation = Volatile.Read(ref functionRowStatusGeneration);
            if (lastWindowsFunctionRowStatus is { } previous && lastWindowsFunctionRowGeneration == generation
                && status with { UpdatedAt = previous.UpdatedAt } == previous)
            {
                return;
            }
            try
            {
                publishFunctionRowStatus(status);
                lastWindowsFunctionRowStatus = status;
                lastWindowsFunctionRowGeneration = generation;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                ReportFailure(exception);
            }
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
        PublishWindowsFunctionRowStatus();
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
        bool hostGaming = status.Capabilities?.HostManagedLaptopControls.Contains(
            OneShotLaptopControl.GamingMode) is true;
        Interlocked.Exchange(ref hostManagedGamingMode, hostGaming ? 1 : 0);
        bool blade18Admitted = IsBlade18FnInputAdmitted(status);
        bool resetBlade18Input = previousControlAccess != status.ControlAccess
            || previousPowerSource != status.ActivePowerSource
            || blade18Admitted != (Volatile.Read(ref blade18FnInputAdmitted) != 0);
        if (!blade18Admitted)
        {
            Interlocked.Exchange(ref blade18FnInputAdmitted, 0);
        }
        if (resetBlade18Input)
        {
            ResetFunctionPInput();
        }
        previousControlAccess = status.ControlAccess;
        previousPowerSource = status.ActivePowerSource;
        if (blade18Admitted)
        {
            Interlocked.Exchange(ref blade18FnInputAdmitted, 1);
        }
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
            Interlocked.Exchange(
                ref verifiedInputDeviceKind,
                (int)RazerBladeInputDeviceKind.Unsupported);
            Interlocked.Exchange(
                ref writableSpecialKeySlots,
                (int)SpecialKeySlotSet.None);
            Interlocked.Exchange(ref functionKeyPrimaryMode, -1);
            SetDriverModeUnknown("The laptop is not currently verified.");
            functionRowInputPump?.Reset();
            Interlocked.Exchange(ref gamingModeKeyPolicyBits, 0);
        }
        else
        {
            Interlocked.Exchange(
                ref verifiedInputDeviceKind,
                (int)RazerBladeReactiveInputDevice.ClassifyModel(
                    status.Firmware?.ModelNumber));
            SpecialKeySlotSet writableSlots =
                status.Capabilities?.SpecialKeyBindingsAccess
                    == DeviceFeatureAccess.ReadWrite
                    ? status.Capabilities.WritableSpecialKeySlots
                    : SpecialKeySlotSet.None;
            Interlocked.Exchange(
                ref writableSpecialKeySlots,
                (int)writableSlots);
            // Function-key preference can safely fall back to the active profile.
            // Driver-mode ownership cannot: an unknown firmware readback must
            // pass keys through instead of risking a stale hook decision.
            FunctionKeyPrimaryMode? effectivePrimaryMode =
                status.FunctionKeyPrimary
                ?? FunctionKeyPrimaryFromActiveProfile(status);
            if (effectivePrimaryMode is { } primaryMode)
            {
                Interlocked.Exchange(ref functionKeyPrimaryMode, (int)primaryMode);
            }

            bool blade18Input = IsBlade18Model(status);
            if (status.KeyboardDriverModeActive is { } active)
            {
                SetDriverModeConfirmed(active, resetInput: !blade18Input);
                if (!active && !blade18Input)
                {
                    functionRowInputPump?.Reset();
                }
            }
            else
            {
                SetDriverModeUnknown(
                    "The service could not confirm keyboard Driver mode.",
                    resetInput: !blade18Input);
                if (!blade18Input)
                {
                    functionRowInputPump?.Reset();
                }
            }

            LaptopBehaviorProfileSettings behavior =
                LaptopBehaviorFromStatus(status);
            bool gamingModeActive = hostGaming
                ? status.ControlAccess == SessionControlAccess.Active
                    && !status.WritesBlocked
                    && behavior.GamingModeEnabled is true
                : status.GamingModeEnabled
                    ?? (status.Capabilities?.OneShotLaptopControls.Contains(
                            OneShotLaptopControl.GamingMode) is true
                        && behavior.GamingModeEnabled is true);
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

    private KeyboardDriverModeObservation ReadDriverModeObservation() => new(
        (KeyboardDriverModeKnowledge)Volatile.Read(ref driverModeKnowledge),
        Interlocked.Read(ref driverModeLastConfirmedTimestamp));

    private void SetDriverModeConfirmed(bool active, bool resetInput = true)
    {
        if (!active && resetInput)
        {
            ResetFunctionPInput();
        }
        KeyboardDriverModeKnowledge knowledge = active
            ? KeyboardDriverModeKnowledge.Active
            : KeyboardDriverModeKnowledge.Inactive;
        Interlocked.Exchange(
            ref driverModeLastConfirmedTimestamp,
            Stopwatch.GetTimestamp());
        int previous = Interlocked.Exchange(ref driverModeKnowledge, (int)knowledge);
        if (previous == (int)KeyboardDriverModeKnowledge.Unknown)
        {
            Trace.TraceInformation(
                "Keyboard Driver mode readback recovered as {0}.",
                active ? "active" : "inactive");
        }
    }

    private void SetDriverModeUnknown(
        string reason,
        bool warning = true,
        bool resetInput = true)
    {
        if (resetInput)
        {
            ResetFunctionPInput();
        }
        int previous = Interlocked.Exchange(
            ref driverModeKnowledge,
            (int)KeyboardDriverModeKnowledge.Unknown);
        if (previous == (int)KeyboardDriverModeKnowledge.Unknown)
        {
            return;
        }

        long confirmedAt = Interlocked.Read(ref driverModeLastConfirmedTimestamp);
        double ageSeconds = confirmedAt <= 0
            ? 0
            : Stopwatch.GetElapsedTime(confirmedAt).TotalSeconds;
        const string message =
            "Keyboard Driver mode state became unknown after {0:F1}s. "
            + "Function-row interception will pass through until readback recovers. {1}";
        if (warning)
        {
            Trace.TraceWarning(message, ageSeconds, reason);
        }
        else
        {
            Trace.TraceInformation(message, ageSeconds, reason);
        }
    }

    internal void ResetFunctionPInput()
    {
        bool resetBlade18Performance = ReadVerifiedInputDeviceKind()
            == RazerBladeInputDeviceKind.Rz09_0484;
        Interlocked.Exchange(ref blade18FnInputAdmitted, 0);
        Volatile.Read(ref source)?.ResetFunctionPInput();
        CancelBlade18FnActions();
        ClearPendingBlade18FnActions();
        functionRowInputPump?.Reset();
        if (resetBlade18Performance)
        {
            CancelPendingPerformanceShortcut();
        }
    }

    private void ClearPendingBlade18FnActions()
    {
        blade18FnShortcutPump?.ClearPending();
        // The pump does not reveal whether it held an item; report only that
        // the reset may have cleared queued work.
        ReportBlade18FnDiagnostic(new(
            Blade18FnDiagnosticStage.PendingActionsReset));
    }

    private bool IsBlade18FnInputAdmitted() =>
        blade18FnShortcutPump is not null
        && Volatile.Read(ref blade18FnInputAdmitted) != 0;

    private static bool IsBlade18FnInputAdmitted(ServiceStatusSnapshot status) =>
        status.DeviceVerified
        && !status.WritesBlocked
        && status.ControlAccess == SessionControlAccess.Active
        && status.ActivePowerSource is
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
        && string.Equals(
            status.Firmware?.ModelNumber,
            "RZ09-0484",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsBlade18Model(ServiceStatusSnapshot status) =>
        string.Equals(
            status.Firmware?.ModelNumber,
            "RZ09-0484",
            StringComparison.OrdinalIgnoreCase);

    private void InvalidateBlade18FnInput()
    {
        ResetFunctionPInput();
    }

    private async Task ExecuteBlade18FnShortcutAsync(
        Blade18FnShortcut shortcut,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linkedCancellation;
        lock (blade18FnActionSync)
        {
            if (blade18FnActionGeneration is null)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                blade18FnActionGeneration.Token);
        }
        using (linkedCancellation)
        {
            if (!IsBlade18FnInputAdmitted())
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ActionBlocked,
                    shortcut));
                return;
            }
            ReportBlade18FnDiagnostic(new(
                Blade18FnDiagnosticStage.ExecutorStarted,
                shortcut));
            try
            {
                await executeBlade18FnShortcutAsync!(shortcut, linkedCancellation.Token)
                    .ConfigureAwait(false);
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ExecutorCompleted,
                    shortcut));
            }
            catch (OperationCanceledException)
                when (linkedCancellation.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ExecutorCanceled,
                    shortcut));
            }
            catch (OperationCanceledException)
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ExecutorCanceled,
                    shortcut));
                throw;
            }
            catch (Exception exception)
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ExecutorFailed,
                    shortcut,
                    FailureType: exception.GetType().Name));
                throw;
            }
        }
    }

    private void CancelBlade18FnActions()
    {
        CancellationTokenSource? previous;
        lock (blade18FnActionSync)
        {
            previous = blade18FnActionGeneration;
            if (previous is null)
            {
                return;
            }
            blade18FnActionGeneration = new();
        }
        previous.Cancel();
        previous.Dispose();
    }

    private static FunctionKeyPrimaryMode? FunctionKeyPrimaryFromActiveProfile(
        ServiceStatusSnapshot status)
    {
        if (status.ActivePowerSource is not (
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return null;
        }

        return LaptopBehaviorFromStatus(status).FunctionKeyPrimary;
    }

    private static LaptopBehaviorProfileSettings LaptopBehaviorFromStatus(
        ServiceStatusSnapshot status)
    {
        if (status.ActivePowerSource is not (
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return LaptopBehaviorProfileSettings.Unconfigured();
        }

        // EffectiveLaptopBehavior carries application-profile overrides. Older
        // snapshots did not populate it, so preserve their per-power profile
        // fallback without replacing a configured effective value.
        LaptopBehaviorProfileSettings? effective = status.EffectiveLaptopBehavior;
        return effective?.IsConfigured is true
            ? effective
            : status.Profiles
                .ForPowerSource(status.ActivePowerSource)
                .EffectiveLaptopBehavior;
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

    private void InvalidateHostGamingMode()
    {
        if (Volatile.Read(ref hostManagedGamingMode) != 0)
        {
            Interlocked.Exchange(ref gamingModeKeyPolicyBits, 0);
        }
    }

    private RazerBladeInputDeviceKind ReadVerifiedInputDeviceKind() =>
        (RazerBladeInputDeviceKind)Volatile.Read(ref verifiedInputDeviceKind);

    private byte? ReadBlade18KeyboardLayoutId()
    {
        lock (performanceShortcutStateSync)
        {
            return latestStatus is { DeviceVerified: true } status && IsBlade18Model(status)
                ? status.Capabilities?.Blade18KeyboardLayoutId : null;
        }
    }

    private bool IsSpecialKeyInputAdmitted(SpecialKeySlot slot)
    {
        SpecialKeySlotSet required = slot switch
        {
            SpecialKeySlot.M1 => SpecialKeySlotSet.M1,
            SpecialKeySlot.M2 => SpecialKeySlotSet.M2,
            SpecialKeySlot.M3 => SpecialKeySlotSet.M3,
            SpecialKeySlot.M4 => SpecialKeySlotSet.M4,
            SpecialKeySlot.M5 => SpecialKeySlotSet.M5,
            _ => SpecialKeySlotSet.None,
        };
        return required != SpecialKeySlotSet.None
            && ((SpecialKeySlotSet)Volatile.Read(ref writableSpecialKeySlots)
                & required) != 0;
    }

    private static bool IsTemporaryDeviceAccessStatus(ServiceStatusSnapshot status) =>
        status.ObservationReason is
            DeviceObservationReason.CheckingConnection or DeviceObservationReason.RefreshingControls;

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
        bool blade18FnPerformance = source == PerformanceShortcutSource.FunctionP
            && ReadVerifiedInputDeviceKind() == RazerBladeInputDeviceKind.Rz09_0484;
        if (blade18FnPerformance && !IsBlade18FnInputAdmitted())
        {
            ReportBlade18FnDiagnostic(new(
                Blade18FnDiagnosticStage.ActionBlocked,
                Blade18FnShortcut.Performance));
            return;
        }
        PerformanceShortcutIntent intent = BeginPerformanceShortcut(source);
        if (blade18FnPerformance)
        {
            long supersededGeneration = Interlocked.Exchange(
                ref blade18PerformanceShortcutGeneration,
                intent.Generation);
            if (supersededGeneration != 0)
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.LatestActionSuperseded,
                    Blade18FnShortcut.Performance));
            }
        }
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
            CancelPerformanceShortcut(intent, reportCancellation: false);
            if (TryCompleteBlade18PerformanceDiagnostic(intent.Generation))
            {
                ReportBlade18FnDiagnostic(new(
                    Blade18FnDiagnosticStage.ExecutorFailed,
                    Blade18FnShortcut.Performance,
                    FailureType: "QueueRejected"));
            }
            ReportFailure(new InvalidOperationException(
                "Performance shortcut input could not be queued."));
        }
        else if (blade18FnPerformance)
        {
            ReportBlade18FnDiagnostic(new(
                Blade18FnDiagnosticStage.ActionQueued,
                Blade18FnShortcut.Performance));
        }
    }

    private void CancelPendingPerformanceShortcut()
    {
        if (shortcutPump.ClearPending() is { } discarded)
        {
            CancelPerformanceShortcut(discarded);
        }
    }

    private void CancelPerformanceShortcut(
        PerformanceShortcutIntent intent,
        bool reportCancellation = true)
    {
        _ = CompletePerformanceShortcut(intent.Generation, applied: false);
        if (reportCancellation
            && TryCompleteBlade18PerformanceDiagnostic(intent.Generation))
        {
            ReportBlade18FnDiagnostic(new(
                Blade18FnDiagnosticStage.ExecutorCanceled,
                Blade18FnShortcut.Performance));
        }
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
        bool blade18FnPerformance =
            Volatile.Read(ref blade18PerformanceShortcutGeneration) == intent.Generation;
        if (blade18FnPerformance)
        {
            ReportBlade18FnDiagnostic(new(
                Blade18FnDiagnosticStage.ExecutorStarted,
                Blade18FnShortcut.Performance));
        }
        ControlOperationResult? result = null;
        Exception? operationException = null;
        ServiceStatusSnapshot? refreshedStatus = null;
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
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                Blade18FnDiagnosticStage.ExecutorCanceled);
            throw;
        }
        catch (Exception exception)
        {
            refreshConfirmedState = true;
            operationException = exception;
        }
        finally
        {
            if (refreshConfirmedState)
            {
                refreshedStatus = await RefreshConfirmedPerformanceAfterFailureAsync()
                    .ConfigureAwait(false);
            }
            await PublishPerformanceShortcutPreviewAsync(
                intent.Generation,
                preset: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        bool confirmedApplied = PerformanceShortcutReadbackMatches(
            intent,
            refreshedStatus);
        bool latestIntent = CompletePerformanceShortcut(
            intent.Generation,
            result?.Status == ControlOperationStatus.Applied || confirmedApplied);
        if (confirmedApplied)
        {
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                Blade18FnDiagnosticStage.ExecutorCompleted);
            if (operationException is not null)
            {
                ReportFailure(operationException);
            }
            return;
        }
        if (operationException is not null)
        {
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                Blade18FnDiagnosticStage.ExecutorFailed,
                operationException.GetType().Name);
            if (latestIntent)
            {
                TryShowPerformanceShortcutFailure(operationException.Message);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(operationException)
                .Throw();
        }

        if (result is null)
        {
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                Blade18FnDiagnosticStage.ExecutorFailed,
                "MissingResult");
            throw new InvalidOperationException(
                "The performance shortcut completed without a result.");
        }
        ControlOperationResult completed = result;
        if (completed.Status == ControlOperationStatus.Failed)
        {
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                Blade18FnDiagnosticStage.ExecutorFailed,
                "ControlOperationFailed");
            if (latestIntent)
            {
                TryShowPerformanceShortcutFailure(completed.Message);
            }
            throw new InvalidOperationException(completed.Message);
        }

        if (completed.Status != ControlOperationStatus.Applied)
        {
            ReportBlade18PerformanceTerminal(
                intent.Generation,
                completed.Status == ControlOperationStatus.Blocked
                    ? Blade18FnDiagnosticStage.ActionBlocked
                    : Blade18FnDiagnosticStage.ExecutorCompleted,
                $"ControlOperation{completed.Status}");
            if (latestIntent && intent.Target is not null)
            {
                TryShowPerformanceShortcutFailure(completed.Message);
            }
            return;
        }

        ReportBlade18PerformanceTerminal(
            intent.Generation,
            Blade18FnDiagnosticStage.ExecutorCompleted);
        bool predictionMatched = intent.Target is not null;
        if (!predictionMatched)
        {
            try
            {
                shortcutNotifications.ShowApplied(completed.Message);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private bool TryCompleteBlade18PerformanceDiagnostic(long generation) =>
        Interlocked.CompareExchange(
            ref blade18PerformanceShortcutGeneration,
            0,
            generation) == generation;

    private void ReportBlade18PerformanceTerminal(
        long generation,
        Blade18FnDiagnosticStage stage,
        string? failureType = null)
    {
        if (TryCompleteBlade18PerformanceDiagnostic(generation))
        {
            ReportBlade18FnDiagnostic(new(
                stage,
                Blade18FnShortcut.Performance,
                FailureType: failureType));
        }
    }

    private void ReportBlade18FnDiagnostic(Blade18FnDiagnostic diagnostic)
    {
        try
        {
            reportBlade18FnDiagnostic(diagnostic);
        }
        catch
        {
            // Diagnostics must not affect input gating or action execution.
        }
    }

    private async Task<ServiceStatusSnapshot?> RefreshConfirmedPerformanceAfterFailureAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await serviceClient.ObserveStateAsync(
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

        return null;
    }

    private static bool PerformanceShortcutReadbackMatches(
        PerformanceShortcutIntent intent,
        ServiceStatusSnapshot? status) =>
        intent.Target is { } target
        && status is
        {
            DeviceVerified: true,
            WritesBlocked: false,
            ConfirmedState.Performance: { } confirmed,
        }
        && confirmed == target
        && (intent.PowerSource is null
            || status.ActivePowerSource == intent.PowerSource);

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
