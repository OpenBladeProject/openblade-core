using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal enum CoolingPadLightingSinkFailure
{
    None,
    KnownNotApplied,
    Indeterminate,
}

internal readonly record struct CoolingPadLightingSinkResult(
    CoolingPadLightingSinkFailure Failure)
{
    internal bool Success => Failure == CoolingPadLightingSinkFailure.None;
}

internal interface ICoolingPadLightingSink
{
    Task<HidSessionResult<CoolingPadBrightnessReading>> QueryBrightnessAsync(
        CancellationToken cancellationToken);

    Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
        CoolingPadLightingFrame frame,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
        byte percent,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
        byte raw,
        CancellationToken cancellationToken);

    Task<CoolingPadLightingSinkResult> SetBrightnessRawForLifecycleAsync(
        byte raw,
        CancellationToken cancellationToken) =>
        SetBrightnessRawAsync(raw, cancellationToken);
}

/// <summary>
/// Testable lifecycle and coalescing logic for an admitted cooling-pad lighting
/// sink. The Windows composition never constructs this type while the current
/// evidence gates remain closed.
/// </summary>
internal sealed class CoolingPadLightingLifecycleCoordinator
    : ICoolingPadLightingCoordinator, IAsyncDisposable
{
    // This is a conservative software ceiling, not hardware cadence admission.
    // FrameCadenceValidated remains false until a sustained physical run is
    // reviewed.
    internal static readonly TimeSpan DefaultMinimumFrameInterval =
        TimeSpan.FromMilliseconds(50);

    private readonly object sync = new();
    private readonly ICoolingPadKeyboardMirrorMapper mapper;
    private readonly ICoolingPadLightingSink sink;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private readonly SemaphoreSlim frameReady = new(0, 1);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan minimumFrameInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? pumpCancellation;
    private Task? pump;
    private CoolingPadLightingFrame? pendingFrame;
    private CoolingPadLightingFrame? retainedFrame;
    private byte? pendingBrightness;
    private byte? submittingBrightness;
    private byte? restoreBrightness;
    private byte? restoreBrightnessRaw;
    private byte? baselineBrightness;
    private byte? lastSubmittedBrightness;
    private long lastFrameSubmissionTimestamp;
    private CoolingPadLightingSyncState state = CoolingPadLightingSyncState.Disabled;
    private string reason = "Admitted";
    private CoolingPadLightingCoordinatorSnapshot? lastNotifiedSnapshot;
    private bool enabled;
    private bool connected;
    private bool suppressed;
    private bool delegated;
    private bool lightingSubmitted;
    private volatile bool faulted;
    private int interested;
    private int disposed;

    internal CoolingPadLightingLifecycleCoordinator(
        CoolingPadLightingAdmission admission,
        ICoolingPadKeyboardMirrorMapper mapper,
        ICoolingPadLightingSink sink,
        TimeProvider? timeProvider = null,
        TimeSpan? minimumFrameInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (!admission.IsAdmitted)
        {
            throw new UnvalidatedCommandException(
                "Cooling-pad lighting lifecycle requires complete admission evidence.");
        }

        this.mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.minimumFrameInterval = minimumFrameInterval
            ?? DefaultMinimumFrameInterval;
        if (this.minimumFrameInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFrameInterval));
        }
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        if (mapper.Availability != CoolingPadMirrorMapAvailability.Resolved)
        {
            throw new UnvalidatedCommandException(
                "Cooling-pad lighting lifecycle requires a resolved mapper.");
        }

        Admission = admission;
    }

    public DeviceFeatureAccess LightingSyncAccess => DeviceFeatureAccess.ReadWrite;

    public event Action<CoolingPadLightingCoordinatorSnapshot>? SnapshotChanged;

    public CoolingPadLightingCoordinatorSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return new CoolingPadLightingCoordinatorSnapshot(
                    LightingSyncAccess,
                    enabled,
                    state,
                    reason);
            }
        }
    }

    public CoolingPadLightingSyncState State
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    public CoolingPadLightingAdmission Admission { get; }

    public string Reason
    {
        get
        {
            lock (sync)
            {
                return reason;
            }
        }
    }

    public bool IsInterested => Volatile.Read(ref interested) != 0;

    public bool IsBrightnessInterested
    {
        get
        {
            lock (sync)
            {
                return enabled && !faulted;
            }
        }
    }

    public void TryPublish(AmbientLightingFrame frame)
    {
        if (!IsInterested)
        {
            return;
        }

        CoolingPadLightingFrame mapped;
        try
        {
            if (!mapper.TryMap(frame, out mapped))
            {
                return;
            }
        }
        catch
        {
            EnterFaulted(
                "FrameMapping",
                CoolingPadLightingSinkFailure.KnownNotApplied);
            return;
        }

        lock (sync)
        {
            if (!CanAcceptFramesCore())
            {
                return;
            }

            retainedFrame = mapped;
            pendingFrame = mapped;
            state = CoolingPadLightingSyncState.Mirroring;
        }

        NotifySnapshotChanged();
        SignalFrame();
    }

    public void TryPublishBrightness(byte percent)
    {
        if (percent > 100 || !IsBrightnessInterested)
        {
            return;
        }

        bool signal = false;
        lock (sync)
        {
            if (!enabled || faulted)
            {
                return;
            }

            restoreBrightness = percent;
            restoreBrightnessRaw = null;
            if (CanAcceptFramesCore())
            {
                if (pendingBrightness is { })
                {
                    pendingBrightness = submittingBrightness == percent
                        || submittingBrightness is null
                            && lastSubmittedBrightness == percent
                        ? null
                        : percent;
                }
                else if (submittingBrightness != percent
                    && lastSubmittedBrightness != percent)
                {
                    pendingBrightness = percent;
                    signal = true;
                }
            }
        }

        if (signal)
        {
            SignalFrame();
        }
    }

    public async Task SetEnabledAsync(
        bool value,
        CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!value)
            {
                enabled = false;
                Volatile.Write(ref interested, 0);
                await StopPumpAsync().ConfigureAwait(false);
                bool hadWriteFault = HasWriteFault();
                bool restored = hadWriteFault
                    || await RestoreBaselineBrightnessIfNeededAsync(
                        lifecycleCleanup: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                lock (sync)
                {
                    pendingFrame = null;
                    retainedFrame = null;
                    pendingBrightness = null;
                    submittingBrightness = null;
                    restoreBrightness = null;
                    restoreBrightnessRaw = null;
                    baselineBrightness = null;
                    lastSubmittedBrightness = null;
                    lastFrameSubmissionTimestamp = 0;
                    lightingSubmitted = hadWriteFault;
                    state = hadWriteFault
                        ? CoolingPadLightingSyncState.Faulted
                        : CoolingPadLightingSyncState.Disabled;
                }
                if (!restored)
                {
                    throw new InvalidOperationException(
                        "Cooling-pad lighting baseline brightness could not be restored; no retry was attempted.");
                }
                return;
            }

            lock (sync)
            {
                if (faulted)
                {
                    throw new InvalidOperationException(
                        $"Cooling-pad lighting recovery is required after {reason}; enabling another write epoch is blocked.");
                }
                enabled = true;
                Volatile.Write(ref interested, 0);
                state = connected
                    ? delegated
                        ? CoolingPadLightingSyncState.Delegated
                        : suppressed
                            ? CoolingPadLightingSyncState.Suppressed
                            : CoolingPadLightingSyncState.WaitingForFrame
                    : CoolingPadLightingSyncState.WaitingForPad;
            }
            if (connected && !faulted
                && !await EnsureBaselineBrightnessAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Cooling-pad lighting could not capture its baseline brightness; no frame or brightness write was attempted.");
            }
            lock (sync)
            {
                UpdateInterestCore();
            }
            StartPumpIfNeeded();
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
        }
    }

    public async Task SetConnectedAsync(
        bool value,
        CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            connected = value;
            if (!value)
            {
                Volatile.Write(ref interested, 0);
                await StopPumpAsync().ConfigureAwait(false);
                lock (sync)
                {
                    bool lostOwnedState = lightingSubmitted && !faulted;
                    pendingFrame = null;
                    retainedFrame = null;
                    pendingBrightness = null;
                    submittingBrightness = null;
                    lastSubmittedBrightness = null;
                    lastFrameSubmissionTimestamp = 0;
                    if (lostOwnedState)
                    {
                        faulted = true;
                        reason = "DisconnectAfterWrite:Indeterminate";
                    }
                    else if (!faulted)
                    {
                        baselineBrightness = null;
                        lightingSubmitted = false;
                    }
                    state = faulted
                        ? CoolingPadLightingSyncState.Faulted
                        : !enabled
                            ? CoolingPadLightingSyncState.Disabled
                            : CoolingPadLightingSyncState.WaitingForPad;
                }
                return;
            }

            Volatile.Write(ref interested, 0);
            if (enabled && !faulted
                && !await EnsureBaselineBrightnessAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            bool replayRetainedFrame;
            bool restoreRetainedBrightness;
            lock (sync)
            {
                replayRetainedFrame = !faulted
                    && enabled
                    && !suppressed
                    && !delegated
                    && retainedFrame is not null;
                pendingFrame = replayRetainedFrame ? retainedFrame : null;
                restoreRetainedBrightness = !faulted
                    && enabled
                    && !suppressed
                    && !delegated
                    && restoreBrightness is not null;
                pendingBrightness = restoreRetainedBrightness
                    ? restoreBrightness
                    : null;
                state = faulted
                    ? CoolingPadLightingSyncState.Faulted
                    : !enabled
                        ? CoolingPadLightingSyncState.Disabled
                        : delegated
                            ? CoolingPadLightingSyncState.Delegated
                            : suppressed
                                ? CoolingPadLightingSyncState.Suppressed
                                : replayRetainedFrame
                                    ? CoolingPadLightingSyncState.Mirroring
                                    : CoolingPadLightingSyncState.WaitingForFrame;
                UpdateInterestCore();
            }
            StartPumpIfNeeded();
            if (replayRetainedFrame || restoreRetainedBrightness)
            {
                SignalFrame();
            }
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
        }
    }

    public async Task AcknowledgeRecoveryAsync(
        CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (sync)
            {
                if (enabled)
                {
                    throw new InvalidOperationException(
                        "Cooling-pad lighting recovery cannot be acknowledged while sync is enabled.");
                }

                faulted = false;
                lightingSubmitted = false;
                reason = "Admitted";
                state = CoolingPadLightingSyncState.Disabled;
            }
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
        }
    }

    public Task SetSuppressedAsync(
        bool value,
        CancellationToken cancellationToken) => SetPausedAsync(
            value,
            delegatedPause: false,
            cancellationToken);

    public Task SetDelegatedAsync(
        bool value,
        CancellationToken cancellationToken) => SetPausedAsync(
            value,
            delegatedPause: true,
            cancellationToken);

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Volatile.Write(ref interested, 0);
            await StopPumpAsync().ConfigureAwait(false);
            bool hadWriteFault = HasWriteFault();
            bool restored = hadWriteFault
                || await RestoreBaselineBrightnessIfNeededAsync(
                    lifecycleCleanup: true,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (sync)
            {
                connected = false;
                pendingFrame = null;
                pendingBrightness = null;
                submittingBrightness = null;
                lastSubmittedBrightness = null;
                retainedFrame = null;
                baselineBrightness = null;
                lastFrameSubmissionTimestamp = 0;
                lightingSubmitted = hadWriteFault;
                state = faulted
                    ? CoolingPadLightingSyncState.Faulted
                    : !enabled
                        ? CoolingPadLightingSyncState.Disabled
                        : CoolingPadLightingSyncState.WaitingForPad;
            }
            if (!restored)
            {
                throw new InvalidOperationException(
                    "Cooling-pad lighting could not restore its baseline brightness during invalidation; no retry was attempted.");
            }
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref interested, 0);
        await controlGate.WaitAsync().ConfigureAwait(false);
        bool restored = true;
        try
        {
            await StopPumpAsync().ConfigureAwait(false);
            bool hadWriteFault = HasWriteFault();
            restored = hadWriteFault
                || await RestoreBaselineBrightnessIfNeededAsync(
                    lifecycleCleanup: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            lock (sync)
            {
                enabled = false;
                pendingFrame = null;
                pendingBrightness = null;
                submittingBrightness = null;
                retainedFrame = null;
                restoreBrightness = null;
                restoreBrightnessRaw = null;
                baselineBrightness = null;
                lastSubmittedBrightness = null;
                lastFrameSubmissionTimestamp = 0;
                lightingSubmitted = false;
                state = CoolingPadLightingSyncState.Unavailable;
            }
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
            controlGate.Dispose();
            frameReady.Dispose();
        }
        if (!restored)
        {
            throw new InvalidOperationException(
                "Cooling-pad lighting baseline brightness could not be restored during disposal; no retry was attempted.");
        }
    }

    private async Task SetPausedAsync(
        bool value,
        bool delegatedPause,
        CancellationToken cancellationToken)
    {
        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (faulted)
            {
                return;
            }

            if (value)
            {
                bool alreadyPaused = delegatedPause ? delegated : suppressed;
                if (delegatedPause)
                {
                    delegated = true;
                }
                else
                {
                    suppressed = true;
                }

                Volatile.Write(ref interested, 0);
                await StopPumpAsync().ConfigureAwait(false);
                if (HasWriteFault())
                {
                    return;
                }
                lock (sync)
                {
                    pendingFrame = null;
                    pendingBrightness = null;
                    submittingBrightness = null;
                    if (!enabled || !connected)
                    {
                        state = enabled
                            ? CoolingPadLightingSyncState.WaitingForPad
                            : CoolingPadLightingSyncState.Disabled;
                        return;
                    }

                    if (alreadyPaused || suppressed && delegated)
                    {
                        state = delegated
                            ? CoolingPadLightingSyncState.Delegated
                            : CoolingPadLightingSyncState.Suppressed;
                        return;
                    }
                }

                CoolingPadLightingSinkResult blackout =
                    await sink.SetBrightnessAsync(0, cancellationToken)
                        .ConfigureAwait(false);
                if (!blackout.Success)
                {
                    EnterFaulted("BlackoutWrite", blackout.Failure);
                    return;
                }

                lock (sync)
                {
                    lastSubmittedBrightness = 0;
                    lightingSubmitted = true;
                    state = delegatedPause
                        ? CoolingPadLightingSyncState.Delegated
                        : CoolingPadLightingSyncState.Suppressed;
                }
                return;
            }

            if (delegatedPause)
            {
                delegated = false;
            }
            else
            {
                suppressed = false;
            }

            if (!enabled || !connected || suppressed || delegated)
            {
                lock (sync)
                {
                    state = !enabled
                        ? CoolingPadLightingSyncState.Disabled
                        : !connected
                            ? CoolingPadLightingSyncState.WaitingForPad
                            : delegated
                                ? CoolingPadLightingSyncState.Delegated
                                : CoolingPadLightingSyncState.Suppressed;
                    UpdateInterestCore();
                }
                return;
            }

            byte? brightnessToRestore;
            byte? rawBrightnessToRestore;
            lock (sync)
            {
                brightnessToRestore = restoreBrightness;
                rawBrightnessToRestore = restoreBrightnessRaw;
            }
            if (rawBrightnessToRestore is { } rawBrightness)
            {
                CoolingPadLightingSinkResult restore =
                    await sink.SetBrightnessRawAsync(rawBrightness, cancellationToken)
                        .ConfigureAwait(false);
                if (!restore.Success)
                {
                    EnterFaulted("BrightnessRestore", restore.Failure);
                    return;
                }

                lock (sync)
                {
                    lastSubmittedBrightness = null;
                    lightingSubmitted = true;
                }
            }
            else if (brightnessToRestore is { } brightness)
            {
                CoolingPadLightingSinkResult restore =
                    await sink.SetBrightnessAsync(brightness, cancellationToken)
                        .ConfigureAwait(false);
                if (!restore.Success)
                {
                    EnterFaulted("BrightnessRestore", restore.Failure);
                    return;
                }

                lock (sync)
                {
                    lastSubmittedBrightness = brightness;
                    lightingSubmitted = true;
                }
            }

            lock (sync)
            {
                pendingFrame = retainedFrame;
                state = retainedFrame is null
                    ? CoolingPadLightingSyncState.WaitingForFrame
                    : CoolingPadLightingSyncState.Mirroring;
                UpdateInterestCore();
            }
            StartPumpIfNeeded();
            if (retainedFrame is not null)
            {
                SignalFrame();
            }
        }
        finally
        {
            controlGate.Release();
            NotifySnapshotChanged();
        }
    }

    private bool CanAcceptFramesCore() =>
        enabled && connected && !suppressed && !delegated && !faulted;

    private void UpdateInterestCore() => Volatile.Write(
        ref interested,
        CanAcceptFramesCore() ? 1 : 0);

    private void StartPumpIfNeeded()
    {
        lock (sync)
        {
            if (!CanAcceptFramesCore() || pump is { IsCompleted: false })
            {
                return;
            }

            pumpCancellation = new CancellationTokenSource();
            pump = RunPumpAsync(pumpCancellation.Token);
        }
    }

    private async Task StopPumpAsync()
    {
        CancellationTokenSource? cancellation;
        Task? running;
        lock (sync)
        {
            cancellation = pumpCancellation;
            running = pump;
            pumpCancellation = null;
            pump = null;
        }

        if (cancellation is null || running is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            lock (sync)
            {
                submittingBrightness = null;
                lastSubmittedBrightness = null;
            }
        }
    }

    private async Task RunPumpAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await frameReady.WaitAsync(cancellationToken).ConfigureAwait(false);
            CoolingPadLightingFrame? frame;
            byte? brightness;
            lock (sync)
            {
                brightness = pendingBrightness;
                pendingBrightness = null;
                submittingBrightness = brightness;
                frame = pendingFrame;
                pendingFrame = null;
            }

            if (brightness is { } pending)
            {
                CoolingPadLightingSinkResult brightnessResult =
                    await sink.SetBrightnessAsync(pending, cancellationToken)
                        .ConfigureAwait(false);
                if (!brightnessResult.Success)
                {
                    EnterFaulted("BrightnessWrite", brightnessResult.Failure);
                    return;
                }

                lock (sync)
                {
                    lastSubmittedBrightness = pending;
                    lightingSubmitted = true;
                    if (submittingBrightness == pending)
                    {
                        submittingBrightness = null;
                    }
                }
            }

            if (frame is null)
            {
                continue;
            }

            await WaitForFrameCadenceAsync(cancellationToken).ConfigureAwait(false);

            CoolingPadLightingSinkResult result =
                await sink.SubmitFrameAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
            if (!result.Success)
            {
                EnterFaulted("FrameWrite", result.Failure);
                return;
            }
            lock (sync)
            {
                lightingSubmitted = true;
                lastFrameSubmissionTimestamp = timeProvider.GetTimestamp();
            }
        }
    }

    private async Task<bool> EnsureBaselineBrightnessAsync(
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (baselineBrightness is not null)
            {
                return true;
            }
        }

        HidSessionResult<CoolingPadBrightnessReading> result =
            await sink.QueryBrightnessAsync(cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            lock (sync)
            {
                state = CoolingPadLightingSyncState.Faulted;
                reason = "BrightnessQueryFailed";
                Volatile.Write(ref interested, 0);
            }
            NotifySnapshotChanged();
            return false;
        }

        CoolingPadBrightnessReading reading = result.Value;
        lock (sync)
        {
            baselineBrightness = reading.Raw;
            if (restoreBrightness is null)
            {
                restoreBrightnessRaw = reading.Raw;
            }
            reason = "Admitted";
        }
        return true;
    }

    private async Task<bool> RestoreBaselineBrightnessIfNeededAsync(
        bool lifecycleCleanup,
        CancellationToken cancellationToken)
    {
        byte? baseline;
        bool shouldRestore;
        lock (sync)
        {
            baseline = baselineBrightness;
            shouldRestore = connected && lightingSubmitted && baseline is not null;
        }
        if (!shouldRestore)
        {
            return true;
        }

        CoolingPadLightingSinkResult result = await (lifecycleCleanup
                ? sink.SetBrightnessRawForLifecycleAsync(
                    baseline!.Value,
                    cancellationToken)
                : sink.SetBrightnessRawAsync(
                    baseline!.Value,
                    cancellationToken))
            .ConfigureAwait(false);
        if (!result.Success)
        {
            EnterFaulted("BaselineRestore", result.Failure);
            return false;
        }

        lock (sync)
        {
            lastSubmittedBrightness = baseline.Value;
            lightingSubmitted = false;
        }
        return true;
    }

    private async Task WaitForFrameCadenceAsync(
        CancellationToken cancellationToken)
    {
        long previous;
        lock (sync)
        {
            previous = lastFrameSubmissionTimestamp;
        }
        if (previous == 0 || minimumFrameInterval == TimeSpan.Zero)
        {
            return;
        }

        TimeSpan elapsed = timeProvider.GetElapsedTime(
            previous,
            timeProvider.GetTimestamp());
        TimeSpan remaining = minimumFrameInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalFrame()
    {
        if (frameReady.CurrentCount == 0)
        {
            frameReady.Release();
        }
    }

    private bool HasWriteFault()
    {
        lock (sync)
        {
            return faulted;
        }
    }

    private void EnterFaulted(
        string phase,
        CoolingPadLightingSinkFailure failure)
    {
        lock (sync)
        {
            faulted = true;
            state = CoolingPadLightingSyncState.Faulted;
            reason = $"{phase}:{failure}";
            pendingFrame = null;
            pendingBrightness = null;
            submittingBrightness = null;
            retainedFrame = null;
            Volatile.Write(ref interested, 0);
        }
        NotifySnapshotChanged();
    }

    private void NotifySnapshotChanged()
    {
        CoolingPadLightingCoordinatorSnapshot snapshot = Snapshot;
        Action<CoolingPadLightingCoordinatorSnapshot>? handlers;
        lock (sync)
        {
            if (lastNotifiedSnapshot == snapshot)
            {
                return;
            }

            lastNotifiedSnapshot = snapshot;
            handlers = SnapshotChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<CoolingPadLightingCoordinatorSnapshot> handler
            in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // Status observers are optional and cannot affect lighting I/O.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref disposed) != 0,
        this);
}
