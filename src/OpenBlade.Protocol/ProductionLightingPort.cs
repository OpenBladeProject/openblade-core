using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ProductionLightingPort : IProductionLightingPort
{
    private static readonly IReadOnlySet<LightingEffect> AdmittedEffects =
        new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>());

    private readonly ValidationActor actor;
    private readonly IRazerCommandSession session;
    private readonly IPid02E0MatrixFeatureWriter? matrixWriter;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object effectSync = new();
    private readonly object lifecycleSync = new();
    private CancellationTokenSource? streamCancellation;
    private Task? streamTask;
    private IPid02E0LightingEffect? activeEffect;
    private Pid02E0ReactiveEffect? activeReactiveEffect;
    private long effectStartedTimestamp;
    private LightingSettings? lastDeliveredState;
    private int nextCommandTransaction = 1;
    private int nextMatrixTransaction = 1;
    private int ownershipState;
    private bool closing;
    private bool disposed;

    internal ProductionLightingPort(
        ValidationActor actor,
        IPid02E0MatrixFeatureWriter? matrixWriter,
        bool admitted,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout)
    {
        this.actor = actor ?? throw new ArgumentNullException(nameof(actor));
        session = actor.Session;
        this.matrixWriter = matrixWriter;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
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
        SupportsTemporaryFullCharge: false);

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
                activeReactiveEffect = null;
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

    public async Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = LightingSettingsFactory.NormalizeLegacyOff(settings);
        if (!LightingSettingsValidation.IsWellFormed(settings)
            || !Capabilities.LightingEffects.Contains(settings.Effect))
        {
            throw KnownNotApplied("The requested lighting setting is not admitted.");
        }

        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryBrightnessRawCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            byte requestedRaw = LightingBrightnessProtocol.PercentToRaw(settings.Brightness);
            OwnedEffectSnapshot? previous = CaptureOwnedEffectCore();

            if (CanRetainRenderer(previous, settings))
            {
                if (baselineRaw != requestedRaw)
                {
                    try
                    {
                        await SetBrightnessAndVerifyCoreAsync(requestedRaw, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        if (!await TryRestoreBrightnessCoreAsync(baselineRaw)
                            .ConfigureAwait(false))
                        {
                            throw RollbackIndeterminate(
                                "Lighting brightness failed and the previous brightness could not be restored.",
                                exception);
                        }

                        throw;
                    }
                }

                lock (effectSync)
                {
                    lastDeliveredState = settings;
                }

                return;
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
                        activeReactiveEffect = null;
                        lastDeliveredState = LightingSettingsFactory.Off();
                    }
                    SetOwnership(LightingRendererOwnershipState.None);

                    return;
                }

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
                    activeReactiveEffect = effect as Pid02E0ReactiveEffect;
                    lastDeliveredState = settings;
                }

                SetOwnership(OwnershipFor(settings.Effect));
                StartStreamCore(effect);
            }
            catch (Exception exception)
            {
                bool brightnessRestored = baselineRaw == requestedRaw
                    || await TryRestoreBrightnessCoreAsync(baselineRaw).ConfigureAwait(false);
                bool rendererRestored = previous is not null
                    && await TryRestoreOwnedEffectCoreAsync(previous).ConfigureAwait(false);

                if (previous is null || !rendererRestored)
                {
                    lock (effectSync)
                    {
                        activeEffect = null;
                        activeReactiveEffect = null;
                        lastDeliveredState = null;
                    }
                    SetOwnership(LightingRendererOwnershipState.None);
                }

                if (!brightnessRestored || (previous is not null && !rendererRestored))
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

    public async Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken)
    {
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Pid02E0ReactiveEffect effect;
            lock (effectSync)
            {
                if (activeReactiveEffect is null)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "Reactive lighting input was rejected because the live renderer is not Reactive.");
                }

                effect = activeReactiveEffect;
            }

            await StopStreamCoreAsync().ConfigureAwait(false);
            try
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                Pid02E0MatrixFrame frame;
                lock (effectSync)
                {
                    effect.Pulse(pulse.MatrixSlot, elapsed);
                    frame = effect.RenderFrame(elapsed);
                }

                await WriteMatrixCoreAsync(frame, cancellationToken).ConfigureAwait(false);
                StartStreamCore(effect);
            }
            catch
            {
                lock (effectSync)
                {
                    activeEffect = null;
                    activeReactiveEffect = null;
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

    public async ValueTask DisposeAsync() =>
        _ = await CloseAsync().ConfigureAwait(false);

    private async Task<LightingRendererOwnershipState> CloseAsync()
    {
        lock (lifecycleSync)
        {
            if (closing || disposed)
            {
                return OwnershipState;
            }

            closing = true;
        }

        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopStreamCoreAsync().ConfigureAwait(false);
            LightingRendererOwnershipState observed = OwnershipState;
            SetOwnership(LightingRendererOwnershipState.None);
            if (matrixWriter is not null)
            {
                await matrixWriter.DisposeAsync().ConfigureAwait(false);
            }

            await actor.DisposeAsync().ConfigureAwait(false);
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

        return raw;
    }

    private async Task SetBrightnessAndVerifyCoreAsync(
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
        await ExecuteWriteCoreAsync(command, cancellationToken).ConfigureAwait(false);
        byte confirmed = await QueryBrightnessRawCoreAsync(cancellationToken).ConfigureAwait(false);
        if (confirmed != raw)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Lighting brightness did not match independent readback.");
        }
    }

    private async Task ExecuteTransitionHandshakeCoreAsync(
        CancellationToken cancellationToken)
    {
        await ExecuteWriteCoreAsync(
            Spec(0x00, 0x86, [0x00, 0x00, 0x00], [0x01, 0x00, 0x00]),
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> transitionState = await ExecuteWriteCoreAsync(
            new RazerCommandSpec(0x00, 0x84, [0x00, 0x00], 2),
            cancellationToken).ConfigureAwait(false);
        // Synapse capture returned 03 00, while the bounded exact-device
        // OpenBlade validation returned 00 00 with the same successful,
        // fully validated response envelope. Treat only those two observed
        // states as acknowledgements; the operation's meaning remains
        // unresolved and every other payload stays fail-closed.
        if (!transitionState.Span.SequenceEqual(new byte[] { 0x03, 0x00 })
            && !transitionState.Span.SequenceEqual(new byte[] { 0x00, 0x00 }))
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                $"The lighting command 00/84 returned " +
                $"{Convert.ToHexString(transitionState.Span)}.");
        }
        await ExecuteWriteCoreAsync(
            Spec(0x03, 0x0A, [0x05, 0x00], [0x05, 0x00]),
            cancellationToken).ConfigureAwait(false);
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
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(AllocateCommandTransaction()),
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
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
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(AllocateCommandTransaction()),
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
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
            throw KnownNotApplied("The matrix writer is not admitted.");
        }

        HidSessionResult<HidUnit> result = await matrixWriter.WriteAsync(
            new Pid02E0MatrixReport(AllocateMatrixTransaction(), frame),
            cancellationToken).ConfigureAwait(false);
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
        throw new BladeWriteException(state, $"The matrix write failed ({result.Failure}).");
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
            _ => throw KnownNotApplied("The requested lighting parameters are unsupported."),
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

    private void StartStreamCore(IPid02E0LightingEffect effect)
    {
        if (effect.GetNextFrameDelay(TimeSpan.Zero) is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        streamCancellation = cancellation;
        streamTask = RunStreamAsync(effect, cancellation.Token);
    }

    private async Task RunStreamAsync(
        IPid02E0LightingEffect effect,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                TimeSpan? delay = effect.GetNextFrameDelay(elapsed);
                if (delay is null)
                {
                    return;
                }

                await Task.Delay(delay.Value, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                Pid02E0MatrixFrame frame;
                lock (effectSync)
                {
                    frame = effect.RenderFrame(elapsed);
                }

                // Normal stream replacement cancels only the inter-frame delay.
                // A native feature write that has already been submitted must drain,
                // otherwise the Windows writer correctly marks its handle poisoned.
                cancellationToken.ThrowIfCancellationRequested();
                await WriteMatrixCoreAsync(frame, CancellationToken.None).ConfigureAwait(false);
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
                    activeReactiveEffect = null;
                    lastDeliveredState = null;
                    SetOwnership(LightingRendererOwnershipState.Faulted);
                }
            }
        }
    }

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

    private OwnedEffectSnapshot? CaptureOwnedEffectCore()
    {
        lock (effectSync)
        {
            return activeEffect is null || lastDeliveredState is null
                ? null
                : new OwnedEffectSnapshot(
                    activeEffect,
                    activeReactiveEffect,
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
                activeReactiveEffect = previous.ReactiveEffect;
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
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

    private sealed record OwnedEffectSnapshot(
        IPid02E0LightingEffect Effect,
        Pid02E0ReactiveEffect? ReactiveEffect,
        long StartedTimestamp,
        LightingSettings Settings,
        LightingRendererOwnershipState Ownership);
}
