using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ProductionLightingPort : IProductionLightingPort
{
    private const byte KeyboardBrightnessRawStep = 0x10;
    private const int MaximumPendingKeyTriggeredPulses = 1024;
    private const int PendingKeyTriggeredPulseTrimTarget = 256;
    private const int PlaybackMuteKeyMatrixSlot = 2;
    private static readonly MatrixRgb PlaybackMuteIndicatorColor = new(0xFF, 0x00, 0x00);
    private static readonly TimeSpan MaximumKeyPulsePreAge =
        TimeSpan.FromMilliseconds(75);
    private static readonly IReadOnlySet<LightingEffect> AdmittedEffects =
        new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>());

    private readonly ValidationActor actor;
    private readonly IRazerCommandSession session;
    private readonly IPid02E0MatrixFeatureWriter? matrixWriter;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim featureIoGate = new(1, 1);
    private readonly object effectSync = new();
    private readonly object lifecycleSync = new();
    private TaskCompletionSource<bool> streamWake = NewStreamWake();
    private CancellationTokenSource? streamCancellation;
    private Task? streamTask;
    private IPid02E0LightingEffect? activeEffect;
    private IPid02E0KeyTriggeredLightingEffect? activeKeyTriggeredEffect;
    private readonly Queue<ReactiveKeyPulse> pendingKeyTriggeredPulses = [];
    private long effectStartedTimestamp;
    private LightingSettings? lastDeliveredState;
    private byte? lastKnownBrightnessRaw;
    private int nextCommandTransaction = 1;
    private int nextMatrixTransaction = 1;
    private int ownershipState;
    // Renderer-local overlay state. This is rolled back on a failed matrix
    // submission; the provider separately retains the desired Windows state
    // across renderer replacement.
    private bool renderPlaybackMuted;
    private bool ownsDriverMode;
    private bool closing;
    private bool disposed;
    private Task<LightingRendererOwnershipState>? closeTask;

    internal ProductionLightingPort(
        ValidationActor actor,
        IPid02E0MatrixFeatureWriter? matrixWriter,
        bool admitted,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        this.actor = actor ?? throw new ArgumentNullException(nameof(actor));
        session = actor.Session;
        this.matrixWriter = matrixWriter;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals
            ?? throw new ArgumentNullException(nameof(pendingDisposals));
        Capabilities = CreateCapabilities(admitted && matrixWriter is not null);
    }

    public DeviceCapabilities Capabilities { get; }

    public LightingRendererOwnershipState OwnershipState =>
        (LightingRendererOwnershipState)Volatile.Read(ref ownershipState);

    internal static DeviceCapabilities CreateCapabilities(bool admitted) => new(
        new HashSet<PerformancePreset>(),
        new HashSet<FanMode>(),
        admitted
            ? new HashSet<LightingEffect>(AdmittedEffects)
            : new HashSet<LightingEffect>(),
        new HashSet<int>(),
        SupportsBatteryProtectionOff: false,
        SupportsTemporaryFullCharge: false)
    {
        LogoLightingModes = admitted
            ? new HashSet<LogoLightingMode>(Enum.GetValues<LogoLightingMode>())
            : new HashSet<LogoLightingMode>(),
    };

    public async Task<LightingSettings?> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte raw = await QueryBrightnessRawCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            // PID 02E0 exposes an independent brightness query but no matrix-effect
            // query. Only zero brightness proves Off; returning cached renderer
            // intent here would incorrectly place it in ConfirmedDeviceState.
            if (raw != 0)
            {
                return null;
            }

            // An independently observed Off means OpenBlade no longer owns the
            // visible effect. Stop its host renderer so a later brightness change
            // by another controller cannot resurrect stale OpenBlade frames.
            await StopStreamCoreAsync().ConfigureAwait(false);
            lock (effectSync)
            {
                activeEffect = null;
                activeKeyTriggeredEffect = null;
                pendingKeyTriggeredPulses.Clear();
                lastDeliveredState = LightingSettingsFactory.Off();
            }
            SetOwnership(LightingRendererOwnershipState.None);

            return LightingSettingsFactory.Off();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LogoLightingMode?> ReadLogoStateAsync(
        CancellationToken cancellationToken)
    {
        if (Capabilities.LogoLightingModes.Count == 0)
        {
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryLogoModeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = LightingSettingsFactory.NormalizeLegacyOff(settings);
        if (!LightingSettingsValidation.IsWellFormed(settings))
        {
            throw KnownNotApplied("The requested lighting setting is invalid.");
        }

        if (!Capabilities.LightingEffects.Contains(settings.Effect)
            || settings.LogoMode is { } logoMode
                && !Capabilities.LogoLightingModes.Contains(logoMode))
        {
            throw Unavailable("The requested lighting setting is not admitted.");
        }

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryBrightnessRawCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            LogoLightingMode? baselineLogo = settings.LogoMode is null
                ? null
                : await QueryLogoModeCoreAsync(cancellationToken).ConfigureAwait(false);
            byte requestedRaw = LightingBrightnessProtocol.PercentToRaw(settings.Brightness);
            OwnedEffectSnapshot? previous = CaptureOwnedEffectCore();

            if (CanRetainRenderer(previous, settings))
            {
                try
                {
                    if (baselineRaw != requestedRaw)
                    {
                        await SetBrightnessAndVerifyCoreAsync(requestedRaw, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (settings.LogoMode is { } requestedLogo
                        && baselineLogo != requestedLogo)
                    {
                        await SetLogoModeAndVerifyCoreAsync(requestedLogo, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    lock (effectSync)
                    {
                        lastDeliveredState = settings;
                    }

                    return;
                }
                catch (Exception exception)
                {
                    bool brightnessRestored = baselineRaw == requestedRaw
                        || await TryRestoreBrightnessCoreAsync(baselineRaw)
                            .ConfigureAwait(false);
                    bool logoRestored = baselineLogo is null
                        || settings.LogoMode == baselineLogo
                        || await TryRestoreLogoCoreAsync(baselineLogo.Value)
                            .ConfigureAwait(false);
                    if (!brightnessRestored || !logoRestored)
                    {
                        throw RollbackIndeterminate(
                            "The lighting update failed and the previous brightness or logo mode could not be restored.",
                            exception);
                    }

                    throw;
                }
            }

            await StopStreamCoreAsync().ConfigureAwait(false);

            try
            {
                if (baselineRaw != requestedRaw)
                {
                    await SetBrightnessAndVerifyCoreAsync(requestedRaw, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (settings.Effect == LightingEffect.Off)
                {
                    lock (effectSync)
                    {
                        activeEffect = null;
                        activeKeyTriggeredEffect = null;
                        pendingKeyTriggeredPulses.Clear();
                        lastDeliveredState = settings;
                    }
                    SetOwnership(LightingRendererOwnershipState.None);
                }
                else
                {
                    IPid02E0LightingEffect effect = CreateEffect(settings);
                    await ExecuteTransitionHandshakeCoreAsync(cancellationToken)
                        .ConfigureAwait(false);

                    effectStartedTimestamp = timeProvider.GetTimestamp();
                    Pid02E0MatrixFrame initialFrame;
                    lock (effectSync)
                    {
                        initialFrame = effect.RenderFrame(TimeSpan.Zero);
                    }

                    await WriteMatrixCoreAsync(initialFrame, cancellationToken)
                        .ConfigureAwait(false);

                    lock (effectSync)
                    {
                        activeEffect = effect;
                        activeKeyTriggeredEffect = effect as IPid02E0KeyTriggeredLightingEffect;
                        pendingKeyTriggeredPulses.Clear();
                        lastDeliveredState = settings;
                    }

                    SetOwnership(OwnershipFor(settings.Effect));
                    StartStreamCore(effect);
                }

                if (settings.LogoMode is { } requestedLogo
                    && baselineLogo != requestedLogo)
                {
                    await SetLogoModeAndVerifyCoreAsync(requestedLogo, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                bool brightnessRestored = baselineRaw == requestedRaw
                    || await TryRestoreBrightnessCoreAsync(baselineRaw).ConfigureAwait(false);
                bool rendererRestored = previous is not null
                    && await TryRestoreOwnedEffectCoreAsync(previous).ConfigureAwait(false);
                bool logoRestored = baselineLogo is null
                    || settings.LogoMode == baselineLogo
                    || await TryRestoreLogoCoreAsync(baselineLogo.Value)
                        .ConfigureAwait(false);

                if (previous is null || !rendererRestored)
                {
                    lock (effectSync)
                    {
                        activeEffect = null;
                        activeKeyTriggeredEffect = null;
                        pendingKeyTriggeredPulses.Clear();
                        lastDeliveredState = null;
                    }
                    SetOwnership(LightingRendererOwnershipState.None);
                }

                if (!brightnessRestored
                    || !logoRestored
                    || (previous is not null && !rendererRestored))
                {
                    throw RollbackIndeterminate(
                        "The lighting change failed and the previous lighting state could not be fully restored.",
                        exception);
                }

                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<byte> StepBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baseline = lastKnownBrightnessRaw
                ?? await QueryBrightnessRawCoreAsync(cancellationToken).ConfigureAwait(false);
            byte requested = direction == KeyboardBrightnessDirection.Increase
                ? (byte)Math.Min(byte.MaxValue, baseline + KeyboardBrightnessRawStep)
                : (byte)Math.Max(byte.MinValue, baseline - KeyboardBrightnessRawStep);
            if (requested == baseline)
            {
                return checked((byte)LightingBrightnessProtocol.RawToPercent(requested));
            }

            await SetBrightnessFastCoreAsync(requested, cancellationToken)
                .ConfigureAwait(false);

            byte resultingPercent = checked(
                (byte)LightingBrightnessProtocol.RawToPercent(requested));
            if (requested == 0)
            {
                await StopStreamCoreAsync().ConfigureAwait(false);
                lock (effectSync)
                {
                    activeEffect = null;
                    activeKeyTriggeredEffect = null;
                    pendingKeyTriggeredPulses.Clear();
                    lastDeliveredState = LightingSettingsFactory.Off();
                }
                SetOwnership(LightingRendererOwnershipState.None);
            }
            else
            {
                lock (effectSync)
                {
                    if (lastDeliveredState is { Effect: not LightingEffect.Off } current)
                    {
                        lastDeliveredState = current with
                        {
                            Brightness = resultingPercent,
                        };
                    }
                }
            }

            return resultingPercent;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleSync)
        {
            if (closing || disposed)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The lighting port is closing.");
            }
        }

        lock (effectSync)
        {
            if (activeKeyTriggeredEffect is null)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Key-triggered lighting input was rejected because no key-triggered renderer is active.");
            }

            if (pendingKeyTriggeredPulses.Count >= MaximumPendingKeyTriggeredPulses)
            {
                while (pendingKeyTriggeredPulses.Count > PendingKeyTriggeredPulseTrimTarget)
                {
                    _ = pendingKeyTriggeredPulses.Dequeue();
                }
            }

            pendingKeyTriggeredPulses.Enqueue(pulse);

            // A pulse is an in-memory renderer update, not a firmware operation.
            // The session stamps Raw Input pulses when Windows delivers them;
            // the renderer falls back to consume-time for old clients.
            streamWake.TrySetResult(true);
        }

        return Task.CompletedTask;
    }

    public async Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsWellFormed())
        {
            throw KnownNotApplied("The Ambient Awareness frame is invalid.");
        }

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Pid02E0MatrixFrame matrixFrame;
            lock (effectSync)
            {
                if (activeEffect is not Pid02E0AmbientAwarenessEffect ambient)
                {
                    throw KnownNotApplied(
                        "Ambient Awareness input was rejected because its renderer is not active.");
                }

                ambient.Update(frame);
                matrixFrame = ambient.RenderFrame(
                    timeProvider.GetElapsedTime(effectStartedTimestamp));
            }

            try
            {
                await WriteMatrixCoreAsync(matrixFrame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (effectSync)
                {
                    activeEffect = null;
                    activeKeyTriggeredEffect = null;
                    pendingKeyTriggeredPulses.Clear();
                    lastDeliveredState = null;
                }
                SetOwnership(LightingRendererOwnershipState.Faulted);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken)
    {
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Pid02E0MatrixFrame? frame = null;
            bool previousMuted;
            lock (effectSync)
            {
                previousMuted = renderPlaybackMuted;
                if (renderPlaybackMuted == muted)
                {
                    return;
                }

                renderPlaybackMuted = muted;
                if (activeEffect is { } effect)
                {
                    frame = effect.RenderFrame(
                        timeProvider.GetElapsedTime(effectStartedTimestamp));
                }
            }

            if (frame is null)
            {
                return;
            }

            try
            {
                await WriteMatrixCoreAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (effectSync)
                {
                    renderPlaybackMuted = previousMuted;
                    activeEffect = null;
                    activeKeyTriggeredEffect = null;
                    pendingKeyTriggeredPulses.Clear();
                    lastDeliveredState = null;
                }
                SetOwnership(LightingRendererOwnershipState.Faulted);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LightingRendererOwnershipState> InvalidateAsync() =>
        await CloseAsync().ConfigureAwait(false);

    public ValueTask DisposeAsync() => new(CloseAsync());

    public async Task<bool> DisposeWithinCleanupDeadlineAsync()
    {
        Task<LightingRendererOwnershipState> closingTask = CloseAsync();
        await BoundedValidationActorDisposal.DisposeAsync(
            this,
            timeProvider,
            cleanupDrainTimeout,
            pendingDisposals).ConfigureAwait(false);
        return closingTask.IsCompleted;
    }

    public async Task EnsureDriverModeAsync(CancellationToken cancellationToken) =>
        _ = await MaintainDriverModeAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool?> MaintainDriverModeAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RazerDeviceMode current = await QueryDeviceModeCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current == RazerDeviceMode.Driver)
            {
                return true;
            }

            // Driver mode enables the validated extended Fn layer and distinct
            // M1-M5 input reports. Mark the transition as ours before submission
            // so an indeterminate reply still receives a conservative cleanup.
            ownsDriverMode = true;
            try
            {
                await SetDeviceModeCoreAsync(
                    RazerDeviceMode.Driver,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                ownsDriverMode = false;
                throw;
            }

            RazerDeviceMode confirmed = await QueryDeviceModeCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (confirmed != RazerDeviceMode.Driver)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Driver mode did not match independent readback.");
            }
            return true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> ReleaseDriverModeAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopStreamCoreAsync().ConfigureAwait(false);
            RazerDeviceMode current = await QueryDeviceModeCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current == RazerDeviceMode.Driver)
            {
                await SetDeviceModeCoreAsync(
                    RazerDeviceMode.Normal,
                    cancellationToken).ConfigureAwait(false);
            }

            RazerDeviceMode confirmed = await QueryDeviceModeCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (confirmed != RazerDeviceMode.Normal)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Normal mode did not match independent readback.");
            }

            ownsDriverMode = false;
            return false;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryDeviceModeCoreAsync(cancellationToken).ConfigureAwait(false)
                == RazerDeviceMode.Driver;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private Task<LightingRendererOwnershipState> CloseAsync()
    {
        lock (lifecycleSync)
        {
            closing = true;
            return closeTask ??= CloseCoreAsync();
        }
    }

    private async Task<LightingRendererOwnershipState> CloseCoreAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopStreamCoreAsync().ConfigureAwait(false);
            LightingRendererOwnershipState observed = OwnershipState;
            SetOwnership(LightingRendererOwnershipState.None);
            await TryRestoreOwnedDeviceModeCoreAsync().ConfigureAwait(false);
            if (matrixWriter is not null)
            {
                await matrixWriter.DisposeAsync().ConfigureAwait(false);
            }

            await actor.DisposeAsync().ConfigureAwait(false);
            featureIoGate.Dispose();
            lock (lifecycleSync)
            {
                disposed = true;
            }

            return observed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<byte> QueryBrightnessRawCoreAsync(
        CancellationToken cancellationToken)
    {
        var command = new RazerCommandSpec(
            LightingBrightnessProtocol.OperationClass,
            LightingBrightnessProtocol.QueryOperationId,
            LightingBrightnessProtocol.QueryArguments.ToArray(),
            3,
            payload => LightingBrightnessProtocol.TryDecodeQuery(payload.Span, out _));
        ReadOnlyMemory<byte> response = await ExecuteReadCoreAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (!LightingBrightnessProtocol.TryDecodeQuery(response.Span, out byte raw))
        {
            throw new IOException("Lighting brightness response was malformed.");
        }

        lastKnownBrightnessRaw = raw;
        return raw;
    }

    private async Task SetBrightnessFastCoreAsync(
        byte raw,
        CancellationToken cancellationToken)
    {
        byte[] arguments = LightingBrightnessProtocol.CreateSetArguments(raw);
        var command = new RazerCommandSpec(
            LightingBrightnessProtocol.OperationClass,
            LightingBrightnessProtocol.SetOperationId,
            arguments,
            3,
            payload => LightingBrightnessProtocol.TryDecodeSet(payload.Span, raw));
        try
        {
            await ExecuteWriteCoreAsync(command, cancellationToken).ConfigureAwait(false);
            lastKnownBrightnessRaw = raw;
        }
        catch
        {
            lastKnownBrightnessRaw = null;
            throw;
        }
    }

    private async Task SetBrightnessAndVerifyCoreAsync(
        byte raw,
        CancellationToken cancellationToken)
    {
        await SetBrightnessFastCoreAsync(raw, cancellationToken).ConfigureAwait(false);
        byte confirmed = await QueryBrightnessRawCoreAsync(cancellationToken).ConfigureAwait(false);
        if (confirmed != raw)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Lighting brightness did not match independent readback.");
        }
    }

    private async Task<LogoLightingMode> QueryLogoModeCoreAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> state = await ExecuteReadCoreAsync(
            new RazerCommandSpec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.QueryStateCommandId,
                LogoLightingProtocol.QueryArguments(),
                3,
                payload => LogoLightingProtocol.TryDecodeState(payload.Span, out _)),
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> effect = await ExecuteReadCoreAsync(
            new RazerCommandSpec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.QueryEffectCommandId,
                LogoLightingProtocol.QueryArguments(),
                3,
                payload => LogoLightingProtocol.TryDecodeEffect(payload.Span, out _)),
            cancellationToken).ConfigureAwait(false);
        if (!LogoLightingProtocol.TryDecodeMode(state.Span, effect.Span, out LogoLightingMode mode))
        {
            throw new IOException("Logo lighting response was malformed or unsupported.");
        }

        return mode;
    }

    private async Task SetLogoModeAndVerifyCoreAsync(
        LogoLightingMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != LogoLightingMode.Off)
        {
            byte[] effect = LogoLightingProtocol.SetEffectArguments(mode);
            await ExecuteWriteCoreAsync(
                Spec(
                    LogoLightingProtocol.CommandClass,
                    LogoLightingProtocol.SetEffectCommandId,
                    effect,
                    effect),
                cancellationToken).ConfigureAwait(false);
        }

        byte[] state = LogoLightingProtocol.SetStateArguments(mode);
        await ExecuteWriteCoreAsync(
            Spec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.SetStateCommandId,
                state,
                state),
            cancellationToken).ConfigureAwait(false);

        LogoLightingMode confirmed = await QueryLogoModeCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (confirmed != mode)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Logo lighting did not match independent readback.");
        }
    }

    private async Task ExecuteTransitionHandshakeCoreAsync(
        CancellationToken cancellationToken)
    {
        await ExecuteWriteCoreAsync(
            Spec(0x00, 0x86, [0x00, 0x00, 0x00], [0x01, 0x00, 0x00]),
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> transitionState = await ExecuteWriteCoreAsync(
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.QueryOperationId,
                DeviceModeProtocol.QueryArguments.ToArray(),
                2,
                payload => DeviceModeProtocol.TryDecodeQuery(payload.Span, out _)),
            cancellationToken).ConfigureAwait(false);
        // This read-only query returned Driver mode (03 00) in the Synapse
        // capture and Normal mode (00 00) in the bounded OpenBlade run.
        // Factory mode (02 00) is deliberately not admitted.
        if (!DeviceModeProtocol.TryDecodeQuery(transitionState.Span, out _))
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                $"The device-mode query 00/84 returned " +
                $"{Convert.ToHexString(transitionState.Span)}.");
        }
        await ExecuteWriteCoreAsync(
            Spec(0x03, 0x0A, [0x05, 0x00], [0x05, 0x00]),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RazerDeviceMode> QueryDeviceModeCoreAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> response = await ExecuteReadCoreAsync(
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.QueryOperationId,
                DeviceModeProtocol.QueryArguments.ToArray(),
                2,
                payload => DeviceModeProtocol.TryDecodeQuery(payload.Span, out _)),
            cancellationToken).ConfigureAwait(false);
        if (!DeviceModeProtocol.TryDecodeQuery(response.Span, out RazerDeviceMode mode))
        {
            throw new IOException("Device-mode response was malformed.");
        }

        return mode;
    }

    private async Task SetDeviceModeCoreAsync(
        RazerDeviceMode mode,
        CancellationToken cancellationToken)
    {
        byte[] arguments = DeviceModeProtocol.CreateSetArguments(mode);
        await ExecuteWriteCoreAsync(
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.SetOperationId,
                arguments,
                arguments.Length,
                payload => DeviceModeProtocol.TryDecodeSet(payload.Span, mode)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRestoreOwnedDeviceModeCoreAsync()
    {
        if (!ownsDriverMode)
        {
            return;
        }

        try
        {
            // Never overwrite a mode another controller already changed.
            if (await QueryDeviceModeCoreAsync(CancellationToken.None).ConfigureAwait(false)
                == RazerDeviceMode.Driver)
            {
                await SetDeviceModeCoreAsync(
                    RazerDeviceMode.Normal,
                    CancellationToken.None).ConfigureAwait(false);
                _ = await QueryDeviceModeCoreAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing must still release both HID handles. This bounded cleanup
            // is never retried in a background fight with another controller.
        }
        finally
        {
            ownsDriverMode = false;
        }
    }

    private static RazerCommandSpec Spec(
        byte commandClass,
        byte commandId,
        byte[] request,
        byte[] response) => new(
            commandClass,
            commandId,
            request,
            response.Length,
            payload => payload.Span.SequenceEqual(response));

    private async Task<ReadOnlyMemory<byte>> ExecuteReadCoreAsync(
        RazerCommandSpec command,
        CancellationToken cancellationToken)
    {
        await featureIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await session.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(AllocateCommandTransaction()),
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            featureIoGate.Release();
        }

        if (result.Failure is null)
        {
            return result.Value;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw result.Failure == HidSessionFailureKind.Timeout
            ? new TimeoutException("Lighting brightness query timed out.")
            : new IOException($"Lighting brightness query failed ({result.Failure}).");
    }

    private async Task<ReadOnlyMemory<byte>> ExecuteWriteCoreAsync(
        RazerCommandSpec command,
        CancellationToken cancellationToken)
    {
        await featureIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await session.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(AllocateCommandTransaction()),
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            featureIoGate.Release();
        }

        if (result.Failure is null)
        {
            return result.Value;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The lighting write was canceled after its session opened.");
        }

        WriteSubmissionState state = result.Failure is
            HidSessionFailureKind.QueuedCanceled or
            HidSessionFailureKind.ConflictBlocked or
            HidSessionFailureKind.ConflictDetectionFailed
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate;
        string validation = result.Diagnostic?.ProtocolValidationFailure?.ToString()
            ?? "None";
        throw new BladeWriteException(
            state,
            $"The lighting command {command.ClassId:X2}/{command.CommandId:X2} " +
            $"failed ({result.Failure}, {validation}).");
    }

    private async Task WriteMatrixCoreAsync(
        Pid02E0MatrixFrame frame,
        CancellationToken cancellationToken)
    {
        if (matrixWriter is null)
        {
            throw Unavailable("The matrix writer is not admitted.");
        }

        await featureIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HidSessionResult<HidUnit> result;
        try
        {
            lock (effectSync)
            {
                if (renderPlaybackMuted)
                {
                    frame = frame.WithSlot(
                        PlaybackMuteKeyMatrixSlot,
                        PlaybackMuteIndicatorColor);
                }
            }

            result = await matrixWriter.WriteAsync(
                new Pid02E0MatrixReport(AllocateMatrixTransaction(), frame),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            featureIoGate.Release();
        }

        if (result.Failure is null)
        {
            return;
        }

        WriteSubmissionState state = result.Failure is
            HidSessionFailureKind.QueuedCanceled or
            HidSessionFailureKind.ConflictBlocked or
            HidSessionFailureKind.ConflictDetectionFailed
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate;
        string phase = result.Diagnostic?.Phase.ToString() ?? "None";
        string error = result.Diagnostic?.NativeWin32Error?.ToString() ?? "None";
        string bytes = result.Diagnostic?.TransferredByteCount?.ToString() ?? "None";
        throw new BladeWriteException(
            state,
            $"The matrix write failed ({result.Failure}, {phase}, {error}, {bytes}).");
    }

    private IPid02E0LightingEffect CreateEffect(LightingSettings settings)
    {
        static MatrixRgb Color(RgbColor color) => new(color.Red, color.Green, color.Blue);

        return settings.Parameters switch
        {
            StaticLightingParameters value => new Pid02E0StaticEffect(Color(value.Color)),
            SpectrumCyclingLightingParameters => new Pid02E0SpectrumEffect(),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Single,
                PrimaryColor: { } primary,
            } => Pid02E0BreathingEffect.Single(Color(primary)),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                PrimaryColor: { } primary,
                SecondaryColor: { } secondary,
            } => Pid02E0BreathingEffect.Dual(Color(primary), Color(secondary)),
            BreathingLightingParameters { ColorMode: BreathingColorMode.Random } =>
                Pid02E0BreathingEffect.Random(
                    new Pid02E0RandomPaletteColorSource()),
            ReactiveLightingParameters value => new Pid02E0ReactiveEffect(
                Color(value.Color),
                ReactiveFadeDuration(value.Duration)),
            RippleLightingParameters value => new Pid02E0RippleEffect(Color(value.Color)),
            AmbientAwarenessLightingParameters => new Pid02E0AmbientAwarenessEffect(),
            TidalLightingParameters value => new Pid02E0TidalEffect(
                Color(value.PrimaryColor),
                Color(value.SecondaryColor),
                value.RandomColors,
                value.Direction),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Single,
                PrimaryColor: { } primary,
            } value => new Pid02E0StarlightEffect(
                    value.ColorMode,
                    Color(primary),
                    default,
                    StarlightDuration(value.Duration)),
            WaveLightingParameters value => new Pid02E0WaveEffect(value.Direction),
            WheelLightingParameters value => new Pid02E0WheelEffect(value.Direction),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                PrimaryColor: { } primary,
                SecondaryColor: { } secondary,
            } value => new Pid02E0StarlightEffect(
                    value.ColorMode,
                    Color(primary),
                    Color(secondary),
                    StarlightDuration(value.Duration)),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } value => new Pid02E0StarlightEffect(
                    value.ColorMode,
                    default,
                    default,
                    StarlightDuration(value.Duration)),
            null when settings.Effect == LightingEffect.Static =>
                new Pid02E0StaticEffect(Color(settings.Color)),
            null when settings.Effect == LightingEffect.Spectrum =>
                new Pid02E0SpectrumEffect(),
            null when settings.Effect == LightingEffect.Breathing =>
                Pid02E0BreathingEffect.Single(Color(settings.Color)),
            null when settings.Effect == LightingEffect.Reactive =>
                new Pid02E0ReactiveEffect(
                    Color(settings.Color),
                    ReactiveFadeDuration(ReactiveLightingDuration.Medium)),
            null when settings.Effect == LightingEffect.Ripple =>
                new Pid02E0RippleEffect(Color(settings.Color)),
            _ => throw Unavailable("The requested lighting parameters are unsupported."),
        };
    }

    internal static TimeSpan ReactiveFadeDuration(ReactiveLightingDuration duration) =>
        duration switch
        {
            // Physical-key captures reach the final nonzero frame just before these
            // nominal endpoints. Keep the renderer on Synapse's 0.5/1.5/2.0 s curve.
            ReactiveLightingDuration.Short => TimeSpan.FromMilliseconds(500),
            ReactiveLightingDuration.Medium => TimeSpan.FromMilliseconds(1500),
            ReactiveLightingDuration.Long => TimeSpan.FromMilliseconds(2000),
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

    internal static TimeSpan StarlightDuration(StarlightLightingDuration duration) =>
        duration switch
        {
            // The focused capture shows the same nominal duration choices as
            // Reactive, with a symmetric Starlight fade-in/fade-out envelope.
            StarlightLightingDuration.Short => TimeSpan.FromMilliseconds(500),
            StarlightLightingDuration.Medium => TimeSpan.FromMilliseconds(1500),
            StarlightLightingDuration.Long => TimeSpan.FromMilliseconds(2000),
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

    private void StartStreamCore(IPid02E0LightingEffect effect)
    {
        if (effect.GetNextFrameDelay(TimeSpan.Zero) is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (effectSync)
        {
            streamWake = NewStreamWake();
        }
        streamCancellation = cancellation;
        streamTask = RunStreamAsync(effect, cancellation.Token);
    }

    private async Task RunStreamAsync(
        IPid02E0LightingEffect effect,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
            TimeSpan? firstDelay = effect.GetNextFrameDelay(elapsed);
            if (firstDelay is null)
            {
                return;
            }

            TimeSpan nextFrameAt = elapsed + firstDelay.Value;
            while (true)
            {
                elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                TimeSpan remaining = nextFrameAt - elapsed;
                bool woke = remaining > TimeSpan.Zero
                    && await WaitForStreamWakeOrDelayAsync(remaining, cancellationToken)
                        .ConfigureAwait(false);
                if (woke)
                {
                    // A key-triggered pulse wakes the single matrix writer. Render
                    // it now rather than enqueueing a competing write from the IPC
                    // request path.
                    elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                    TimeSpan? wakeDelay = effect.GetNextFrameDelay(elapsed);
                    if (wakeDelay is null)
                    {
                        return;
                    }

                    nextFrameAt = elapsed;
                }

                elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                Pid02E0MatrixFrame frame;
                lock (effectSync)
                {
                    if (ReferenceEquals(activeEffect, effect)
                        && activeKeyTriggeredEffect is { } keyTriggered)
                    {
                        while (pendingKeyTriggeredPulses.TryDequeue(out ReactiveKeyPulse pulse))
                        {
                            keyTriggered.Pulse(
                                pulse.MatrixSlot,
                                KeyPulseElapsedOrFallback(pulse, elapsed));
                        }
                    }

                    frame = effect.RenderFrame(elapsed);
                }

                // Normal stream replacement cancels only the inter-frame delay.
                // A native feature write that has already been submitted must drain,
                // otherwise the Windows writer correctly marks its handle poisoned.
                cancellationToken.ThrowIfCancellationRequested();
                await WriteMatrixCoreAsync(frame, CancellationToken.None).ConfigureAwait(false);

                TimeSpan observedAfterWrite = timeProvider.GetElapsedTime(
                    effectStartedTimestamp);
                TimeSpan? advanced = AdvanceFrameDeadline(
                    effect,
                    nextFrameAt,
                    observedAfterWrite);
                if (advanced is null)
                {
                    return;
                }

                nextFrameAt = advanced.Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            lock (effectSync)
            {
                if (ReferenceEquals(activeEffect, effect))
                {
                    activeEffect = null;
                    activeKeyTriggeredEffect = null;
                    pendingKeyTriggeredPulses.Clear();
                    lastDeliveredState = null;
                    SetOwnership(LightingRendererOwnershipState.Faulted);
                }
            }
        }
    }

    internal static TimeSpan? AdvanceFrameDeadline(
        IPid02E0LightingEffect effect,
        TimeSpan currentDeadline,
        TimeSpan observedAfterWrite)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Pid02E0LightingMath.ThrowIfNegative(currentDeadline);
        Pid02E0LightingMath.ThrowIfNegative(observedAfterWrite);
        TimeSpan cursor = currentDeadline;
        while (cursor <= observedAfterWrite)
        {
            TimeSpan? delay = effect.GetNextFrameDelay(cursor);
            if (delay is null)
            {
                return null;
            }

            cursor += delay.Value;
        }

        return cursor;
    }

    private async Task<bool> WaitForStreamWakeOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        Task wake;
        lock (effectSync)
        {
            wake = streamWake.Task;
        }

        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task timer = Task.Delay(delay, timeProvider, timerCancellation.Token);
        Task completed = await Task.WhenAny(timer, wake).ConfigureAwait(false);
        if (ReferenceEquals(completed, timer))
        {
            await timer.ConfigureAwait(false);
            return false;
        }

        lock (effectSync)
        {
            if (ReferenceEquals(streamWake.Task, wake))
            {
                streamWake = NewStreamWake();
            }
        }

        timerCancellation.Cancel();
        try
        {
            await timer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private TimeSpan KeyPulseElapsedOrFallback(
        ReactiveKeyPulse pulse,
        TimeSpan fallbackElapsed)
    {
        if (pulse.ObservedTimestamp is not { } observedTimestamp)
        {
            return fallbackElapsed;
        }

        if (observedTimestamp <= effectStartedTimestamp)
        {
            return fallbackElapsed;
        }

        TimeSpan observedElapsed;
        try
        {
            observedElapsed = timeProvider.GetElapsedTime(
                effectStartedTimestamp,
                observedTimestamp);
        }
        catch
        {
            return fallbackElapsed;
        }

        if (observedElapsed < TimeSpan.Zero)
        {
            return fallbackElapsed;
        }

        if (observedElapsed >= fallbackElapsed)
        {
            return fallbackElapsed;
        }

        TimeSpan earliestVisibleStart = fallbackElapsed > MaximumKeyPulsePreAge
            ? fallbackElapsed - MaximumKeyPulsePreAge
            : TimeSpan.Zero;
        return observedElapsed < earliestVisibleStart
            ? earliestVisibleStart
            : observedElapsed;
    }

    private void SignalStreamCore()
    {
        lock (effectSync)
        {
            streamWake.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> NewStreamWake() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task StopStreamCoreAsync()
    {
        CancellationTokenSource? cancellation = streamCancellation;
        Task? running = streamTask;
        streamCancellation = null;
        streamTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (running is not null)
            {
                await running.ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task<bool> TryRestoreBrightnessCoreAsync(byte raw)
    {
        try
        {
            await SetBrightnessAndVerifyCoreAsync(raw, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryRestoreLogoCoreAsync(LogoLightingMode mode)
    {
        try
        {
            await SetLogoModeAndVerifyCoreAsync(mode, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private OwnedEffectSnapshot? CaptureOwnedEffectCore()
    {
        lock (effectSync)
        {
            return activeEffect is null || lastDeliveredState is null
                ? null
                : new OwnedEffectSnapshot(
                    activeEffect,
                    activeKeyTriggeredEffect,
                    effectStartedTimestamp,
                    lastDeliveredState,
                    OwnershipState);
        }
    }

    private static bool CanRetainRenderer(
        OwnedEffectSnapshot? previous,
        LightingSettings requested) =>
        previous is { } snapshot
        && snapshot.Settings.Effect == requested.Effect
        && snapshot.Settings.Color == requested.Color
        && Equals(snapshot.Settings.Parameters, requested.Parameters);

    private async Task<bool> TryRestoreOwnedEffectCoreAsync(OwnedEffectSnapshot? previous)
    {
        if (previous is null)
        {
            return false;
        }

        try
        {
            await ExecuteTransitionHandshakeCoreAsync(CancellationToken.None)
                .ConfigureAwait(false);
            TimeSpan elapsed = timeProvider.GetElapsedTime(previous.StartedTimestamp);
            Pid02E0MatrixFrame frame;
            lock (effectSync)
            {
                frame = previous.Effect.RenderFrame(elapsed);
            }

            await WriteMatrixCoreAsync(frame, CancellationToken.None).ConfigureAwait(false);
            lock (effectSync)
            {
                activeEffect = previous.Effect;
                activeKeyTriggeredEffect = previous.KeyTriggeredEffect;
                pendingKeyTriggeredPulses.Clear();
                effectStartedTimestamp = previous.StartedTimestamp;
                lastDeliveredState = previous.Settings;
            }

            SetOwnership(previous.Ownership);
            StartStreamCore(previous.Effect);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private byte AllocateCommandTransaction()
    {
        int value = nextCommandTransaction;
        nextCommandTransaction = value == byte.MaxValue ? 1 : value + 1;
        return checked((byte)value);
    }

    private byte AllocateMatrixTransaction()
    {
        int value = nextMatrixTransaction;
        nextMatrixTransaction = value == byte.MaxValue ? 1 : value + 1;
        return checked((byte)value);
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (lifecycleSync)
        {
            if (closing || disposed)
            {
                operationGate.Release();
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }

    private async Task EnterWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw KnownNotApplied("The lighting write was canceled before submission.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The lighting port is closing.",
                exception);
        }
    }

    private static BladeWriteException KnownNotApplied(string message) =>
        new(WriteSubmissionState.KnownNotApplied, message);

    private static CapabilityUnavailableException Unavailable(string message) => new(message);

    private static BladeWriteException RollbackIndeterminate(
        string message,
        Exception originalFailure) =>
        new(WriteSubmissionState.Indeterminate, message, originalFailure);

    private void SetOwnership(LightingRendererOwnershipState value) =>
        Volatile.Write(ref ownershipState, (int)value);

    private static LightingRendererOwnershipState OwnershipFor(LightingEffect effect) =>
        effect switch
        {
            LightingEffect.Off => LightingRendererOwnershipState.None,
            LightingEffect.Static => LightingRendererOwnershipState.Static,
            LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
            LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
            LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
            LightingEffect.Tidal => LightingRendererOwnershipState.Tidal,
            LightingEffect.Starlight => LightingRendererOwnershipState.Starlight,
            LightingEffect.Wave => LightingRendererOwnershipState.Wave,
            LightingEffect.Wheel => LightingRendererOwnershipState.Wheel,
            LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
            LightingEffect.AmbientAwareness => LightingRendererOwnershipState.AmbientAwareness,
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

    private sealed record OwnedEffectSnapshot(
        IPid02E0LightingEffect Effect,
        IPid02E0KeyTriggeredLightingEffect? KeyTriggeredEffect,
        long StartedTimestamp,
        LightingSettings Settings,
        LightingRendererOwnershipState Ownership);
}
