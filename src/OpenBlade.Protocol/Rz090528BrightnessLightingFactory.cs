using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record BrightnessRoundTripResult(
    byte BaselineRaw,
    byte TargetRaw,
    byte AppliedRaw,
    byte RestoredRaw);

internal sealed class Rz090528BrightnessLightingFactory : IProductionLightingPortFactory
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultCleanupDrainTimeout =
        TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly bool staticEffectAdmitted;
    private readonly bool spectrumEffectAdmitted;
    private readonly bool allSynapseEffectsAdmitted;

    internal Rz090528BrightnessLightingFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        bool staticEffectAdmitted = false,
        bool spectrumEffectAdmitted = false,
        bool allSynapseEffectsAdmitted = false,
        TimeProvider? timeProvider = null,
        TimeSpan? cleanupDrainTimeout = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        this.staticEffectAdmitted = staticEffectAdmitted;
        this.spectrumEffectAdmitted = spectrumEffectAdmitted;
        this.allSynapseEffectsAdmitted = allSynapseEffectsAdmitted;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.cleanupDrainTimeout =
            cleanupDrainTimeout ?? DefaultCleanupDrainTimeout;
        RazerCommandSessionTimeout.Validate(this.cleanupDrainTimeout);
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
        Capabilities = Rz090528BrightnessLightingPort.CreateCapabilities(
            staticEffectAdmitted,
            spectrumEffectAdmitted,
            allSynapseEffectsAdmitted);
    }

    public DeviceCapabilities Capabilities { get; }

    public async Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(null, HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return new(null, HidSessionFailureKind.NativeIoFailure);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return new(null, HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Value is not { } actor || opened.Failure is not null)
        {
            if (opened.Value is { } unexpected)
            {
                await unexpected.DisposeAsync().ConfigureAwait(false);
            }

            return new(null, opened.Failure ?? HidSessionFailureKind.NativeIoFailure)
            {
                Diagnostic = opened.Diagnostic,
            };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await actor.DisposeAsync().ConfigureAwait(false);
            return new(null, HidSessionFailureKind.Canceled);
        }

        return new(
            new Rz090528BrightnessLightingPort(
                actor,
                ExchangeTimeout,
                staticEffectAdmitted,
                spectrumEffectAdmitted,
                allSynapseEffectsAdmitted,
                timeProvider,
                cleanupDrainTimeout,
                pendingDisposals),
            null);
    }
}

internal sealed class Rz090528BrightnessLightingPort : IProductionLightingPort
{
    private const byte StepPercent = 10;
    private const byte LogoStorageSelector = 0x00;
    private readonly ValidationActor actor;
    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim featureIoGate = new(1, 1);
    private readonly SemaphoreSlim streamWake = new(0, 1);
    private readonly object effectSync = new();
    private readonly object lifecycleSync = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private IRz090528LightingEffect? activeEffect;
    private LightingSettings? lastDeliveredState;
    private CancellationTokenSource? streamCancellation;
    private Task? streamTask;
    private long effectStartedTimestamp;
    private LightingRendererOwnershipState ownershipState;
    private int nextTransaction = 1;
    private int driverModeRestoreOutcome;
    private bool ownsDriverMode;
    private bool closing;
    private Task<LightingRendererOwnershipState>? closeTask;
    private int disposed;

    internal Rz090528BrightnessLightingPort(
        ValidationActor actor,
        TimeSpan exchangeTimeout,
        bool staticEffectAdmitted = false,
        bool spectrumEffectAdmitted = false,
        bool allSynapseEffectsAdmitted = false,
        TimeProvider? timeProvider = null,
        TimeSpan? cleanupDrainTimeout = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.actor = actor;
        session = new Rz090528CommandSessionGate(
            actor.Session,
            Rz090528CommandSessionGate.Shared);
        this.exchangeTimeout = exchangeTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.cleanupDrainTimeout =
            cleanupDrainTimeout ?? TimeSpan.FromSeconds(5);
        RazerCommandSessionTimeout.Validate(this.cleanupDrainTimeout);
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
        Capabilities = CreateCapabilities(
            staticEffectAdmitted,
            spectrumEffectAdmitted,
            allSynapseEffectsAdmitted);
    }

    public DeviceCapabilities Capabilities { get; }

    public LightingRendererOwnershipState OwnershipState
    {
        get
        {
            lock (effectSync)
            {
                return ownershipState;
            }
        }
    }

    public DriverModeRestoreOutcome DriverModeRestoreOutcome =>
        (DriverModeRestoreOutcome)Volatile.Read(ref driverModeRestoreOutcome);

    internal static DeviceCapabilities CreateCapabilities(
        bool staticEffectAdmitted = false,
        bool spectrumEffectAdmitted = false,
        bool allSynapseEffectsAdmitted = false) => new(
        new HashSet<PerformancePreset>(),
        new HashSet<FanMode>(),
        CreateAdmittedEffects(
            staticEffectAdmitted,
            spectrumEffectAdmitted,
            allSynapseEffectsAdmitted),
        new HashSet<int>(),
        SupportsBatteryProtectionOff: false,
        SupportsTemporaryFullCharge: false)
        {
            SupportsKeyboardBrightness = true,
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
            WritableSpecialKeySlots =
            SpecialKeySlotSet.M3
            | SpecialKeySlotSet.M4
            | SpecialKeySlotSet.M5,
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
            ObservedLightingEffects = CreateObservedEffects(
                staticEffectAdmitted,
                spectrumEffectAdmitted,
                allSynapseEffectsAdmitted),
            LogoLightingModes = new HashSet<LogoLightingMode>(
            Enum.GetValues<LogoLightingMode>()),
        };

    private static IReadOnlySet<LightingEffect> CreateAdmittedEffects(
        bool staticEffectAdmitted,
        bool spectrumEffectAdmitted,
        bool allSynapseEffectsAdmitted)
    {
        var effects = new HashSet<LightingEffect> { LightingEffect.Off };
        if (allSynapseEffectsAdmitted)
        {
            effects.UnionWith(Enum.GetValues<LightingEffect>());
            return effects;
        }

        if (staticEffectAdmitted)
        {
            effects.Add(LightingEffect.Static);
        }
        if (spectrumEffectAdmitted)
        {
            effects.Add(LightingEffect.Spectrum);
        }
        return effects;
    }

    private static IReadOnlySet<LightingEffect> CreateObservedEffects(
        bool staticEffectAdmitted,
        bool spectrumEffectAdmitted,
        bool allSynapseEffectsAdmitted)
    {
        var effects = new HashSet<LightingEffect>
        {
            LightingEffect.Starlight,
            LightingEffect.Ripple,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
        };
        if (allSynapseEffectsAdmitted)
        {
            effects.Clear();
            return effects;
        }
        if (!staticEffectAdmitted)
        {
            effects.Add(LightingEffect.Static);
        }
        if (!spectrumEffectAdmitted)
        {
            effects.Add(LightingEffect.Spectrum);
        }
        return effects;
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

            // Mark the transition as ours before submission so an indeterminate
            // reply still receives a conservative cleanup attempt.
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
                    "PID 02C6 Driver mode did not match independent readback.");
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
            if (await QueryDeviceModeCoreAsync(cancellationToken).ConfigureAwait(false)
                == RazerDeviceMode.Driver)
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
                    "PID 02C6 Normal mode did not match independent readback.");
            }

            ownsDriverMode = false;
            return false;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> ReadDriverModeAsync(
        CancellationToken cancellationToken)
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

    public async Task<LightingSettings?> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<LightingSettings?> ReadStateCoreAsync(
        CancellationToken cancellationToken)
    {
        byte raw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
        if (raw != 0)
        {
            return null;
        }

        if (OwnedRendererOffObservation.RequiresOffConfirmation(OwnershipState))
        {
            await Task.Delay(
                OwnedRendererOffObservation.OffConfirmationDelay,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
            raw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            if (raw != 0)
            {
                return null;
            }
        }

        // While OpenBlade owns the renderer, only the second zero sample reaches
        // this destructive decision. Keeping the confirmation at the port
        // boundary also protects ordinary observations that overlap a power
        // transition before its lifecycle event is processed.
        await StopStreamCoreAsync().ConfigureAwait(false);
        ClearOwnedEffectCore(LightingSettingsFactory.Off());
        return LightingSettingsFactory.Off();
    }

    public async Task<LogoLightingMode?> ReadLogoStateAsync(
        CancellationToken cancellationToken) =>
        await ReadLogoCandidateStateAsync(cancellationToken).ConfigureAwait(false);

    internal async Task<LogoLightingMode> ReadLogoCandidateStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueryLogoCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task SetLogoCandidateStateAsync(
        LogoLightingMode mode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetLogoCoreAsync(mode, cancellationToken).ConfigureAwait(false);
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
            || !Capabilities.LightingEffects.Contains(settings.Effect)
            || settings.LogoMode is { } logoMode
                && !Capabilities.LogoLightingModes.Contains(logoMode))
        {
            throw new CapabilityUnavailableException(
                "The requested PID 02C6 lighting setting is not admitted.");
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryRawCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            LogoLightingMode? baselineLogo = settings.LogoMode is null
                ? null
                : await QueryLogoCoreAsync(cancellationToken).ConfigureAwait(false);
            byte requestedRaw = LightingBrightnessProtocol.PercentToRaw(
                settings.Brightness);
            (IRz090528LightingEffect? Effect, LightingSettings? Settings,
                long StartedTimestamp, LightingRendererOwnershipState Ownership) previous =
                CaptureOwnedEffectCore();

            if (CanRetainRenderer(previous.Settings, settings))
            {
                try
                {
                    if (baselineRaw != requestedRaw)
                    {
                        await SetAndConfirmRawCoreAsync(
                            requestedRaw,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (settings.LogoMode is { } retainedLogo
                        && retainedLogo != baselineLogo)
                    {
                        await SetLogoCoreAsync(retainedLogo, cancellationToken)
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
                        || await TryRestoreCoreAsync(baselineRaw).ConfigureAwait(false);
                    bool logoRestored = baselineLogo is null
                        || settings.LogoMode == baselineLogo
                        || await TryRestoreLogoCoreAsync(baselineLogo.Value)
                            .ConfigureAwait(false);
                    if (!brightnessRestored || !logoRestored)
                    {
                        throw new BladeWriteException(
                            WriteSubmissionState.Indeterminate,
                            "PID 02C6 lighting update failed and restoration was not confirmed.",
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
                    await SetAndConfirmRawCoreAsync(
                        requestedRaw,
                        cancellationToken).ConfigureAwait(false);
                }

                if (settings.Effect == LightingEffect.Off)
                {
                    ClearOwnedEffectCore(settings);
                }
                else
                {
                    IRz090528LightingEffect effect = CreateEffect(settings);
                    effectStartedTimestamp = timeProvider.GetTimestamp();
                    await WriteMatrixFrameCoreAsync(
                        effect.RenderFrame(TimeSpan.Zero),
                        cancellationToken).ConfigureAwait(false);
                    lock (effectSync)
                    {
                        activeEffect = effect;
                        lastDeliveredState = settings;
                    }

                    SetOwnership(OwnershipFor(settings.Effect));
                    if (effect.RequiresContinuousRendering)
                    {
                        StartStreamCore(effect);
                    }
                }

                if (settings.LogoMode is { } requestedLogo
                    && requestedLogo != baselineLogo)
                {
                    await SetLogoCoreAsync(requestedLogo, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                bool brightnessRestored = baselineRaw == requestedRaw
                    || await TryRestoreCoreAsync(baselineRaw).ConfigureAwait(false);
                bool rendererRestored = previous.Effect is not null
                    && await TryRestoreOwnedEffectCoreAsync(previous)
                        .ConfigureAwait(false);
                bool logoRestored = baselineLogo is null
                    || settings.LogoMode == baselineLogo
                    || await TryRestoreLogoCoreAsync(baselineLogo.Value)
                        .ConfigureAwait(false);
                if (previous.Effect is null || !rendererRestored)
                {
                    ClearOwnedEffectCore(settings: null);
                }

                if (!brightnessRestored
                    || !logoRestored
                    || previous.Effect is not null && !rendererRestored)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        "PID 02C6 lighting update failed and the previous state was not fully restored.",
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

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            byte requestedRaw = StepTargetRaw(baselineRaw, direction);
            if (requestedRaw == baselineRaw)
            {
                return checked((byte)LightingBrightnessProtocol.RawToPercent(requestedRaw));
            }

            await SetAndVerifyCoreAsync(
                requestedRaw,
                baselineRaw,
                cancellationToken).ConfigureAwait(false);
            return checked((byte)LightingBrightnessProtocol.RawToPercent(requestedRaw));
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<BrightnessRoundTripResult> ValidateRoundTripAsync(
        byte targetPercent,
        CancellationToken cancellationToken)
    {
        if (targetPercent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPercent));
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            byte targetRaw = LightingBrightnessProtocol.PercentToRaw(targetPercent);
            return await ValidateRawRoundTripCoreAsync(
                baselineRaw,
                targetRaw,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<BrightnessRoundTripResult> ValidateStepRoundTripAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte baselineRaw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            byte targetRaw = StepTargetRaw(baselineRaw, direction);
            return await ValidateRawRoundTripCoreAsync(
                baselineRaw,
                targetRaw,
                cancellationToken).ConfigureAwait(false);
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
            ObjectDisposedException.ThrowIf(
                closing || Volatile.Read(ref disposed) != 0,
                this);
            lock (effectSync)
            {
                if (activeEffect is not IRz090528KeyTriggeredLightingEffect triggered)
                {
                    return Task.FromException(new CapabilityUnavailableException(
                        "PID 02C6 key-triggered lighting is not currently active."));
                }

                TimeSpan elapsed = timeProvider.GetElapsedTime(
                    effectStartedTimestamp);
                triggered.Pulse(pulse.MatrixSlot, elapsed);

                if (streamWake.CurrentCount == 0)
                {
                    streamWake.Release();
                }
            }
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
            throw new CapabilityUnavailableException(
                "The PID 02C6 session-rendered lighting frame is invalid.");
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Rz090528MatrixFrame matrix;
            lock (effectSync)
            {
                switch (activeEffect)
                {
                    case Rz090528AmbientAwarenessEffect ambient:
                        ambient.Update(frame);
                        matrix = ambient.RenderFrame(timeProvider.GetElapsedTime(
                            effectStartedTimestamp));
                        break;
                    case Rz090528AudioMeterEffect audio:
                        audio.Update(frame);
                        matrix = audio.RenderFrame(timeProvider.GetElapsedTime(
                            effectStartedTimestamp));
                        break;
                    default:
                        throw new CapabilityUnavailableException(
                            "PID 02C6 session-rendered lighting is not active.");
                }
            }

            await WriteMatrixFrameCoreAsync(
                matrix,
                cancellationToken,
                expectResponse: false).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) =>
        Reject(cancellationToken);

    public async Task<LightingRendererOwnershipState> RelinquishRendererAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopStreamCoreAsync().ConfigureAwait(false);
            lock (effectSync)
            {
                LightingRendererOwnershipState observed = ownershipState;
                activeEffect = null;
                lastDeliveredState = null;
                ownershipState = LightingRendererOwnershipState.None;
                return observed;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<LightingRendererOwnershipState> InvalidateAsync() => CloseAsync();

    public ValueTask DisposeAsync() => new(CloseAsync());

    public async Task<bool> DisposeWithinCleanupDeadlineAsync()
    {
        Task<LightingRendererOwnershipState> closingTask = CloseAsync();
        await BoundedValidationActorDisposal.DisposeAsync(
            this,
            timeProvider,
            cleanupDrainTimeout,
            pendingDisposals).ConfigureAwait(false);
        return closingTask.IsCompletedSuccessfully;
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
            lock (effectSync)
            {
                activeEffect = null;
                lastDeliveredState = null;
                ownershipState = LightingRendererOwnershipState.None;
            }
            await TryRestoreOwnedDeviceModeCoreAsync().ConfigureAwait(false);
            await actor.DisposeAsync().ConfigureAwait(false);
            featureIoGate.Dispose();
            streamWake.Dispose();
            lock (lifecycleSync)
            {
                Volatile.Write(ref disposed, 1);
            }

            return observed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static Task Reject(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new CapabilityUnavailableException(
            "PID 02C6 matrix lighting is not admitted."));
    }

    private static IRz090528LightingEffect CreateEffect(LightingSettings settings)
    {
        return settings.Parameters switch
        {
            StaticLightingParameters staticParameters
                when settings.Effect == LightingEffect.Static =>
                new Rz090528StaticEffect(staticParameters.Color),
            SpectrumCyclingLightingParameters
                when settings.Effect == LightingEffect.Spectrum =>
                new Rz090528SpectrumEffect(),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Single,
                PrimaryColor: { } primary,
            } when settings.Effect == LightingEffect.Breathing =>
                new Rz090528BreathingEffect(
                    BreathingColorMode.Single,
                    primary,
                    default),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                PrimaryColor: { } primary,
                SecondaryColor: { } secondary,
            } when settings.Effect == LightingEffect.Breathing =>
                new Rz090528BreathingEffect(
                    BreathingColorMode.Dual,
                    primary,
                    secondary),
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Random,
            } when settings.Effect == LightingEffect.Breathing =>
                new Rz090528BreathingEffect(
                    BreathingColorMode.Random,
                    default,
                    default),
            ReactiveLightingParameters value
                when settings.Effect == LightingEffect.Reactive =>
                new Rz090528ReactiveEffect(
                    value.Color,
                    ReactiveDuration(value.Duration)),
            AmbientAwarenessLightingParameters
                when settings.Effect == LightingEffect.AmbientAwareness =>
                new Rz090528AmbientAwarenessEffect(),
            AudioMeterLightingParameters value
                when settings.Effect == LightingEffect.AudioMeter =>
                new Rz090528AudioMeterEffect(value.ColorBoost),
            FireLightingParameters
                when settings.Effect == LightingEffect.Fire =>
                new Rz090528FireEffect(),
            RippleLightingParameters value
                when settings.Effect == LightingEffect.Ripple =>
                new Rz090528RippleEffect(value.Color),
            TidalLightingParameters value
                when settings.Effect == LightingEffect.Tidal =>
                new Rz090528TidalEffect(
                    value.PrimaryColor,
                    value.SecondaryColor,
                    value.RandomColors,
                    value.Direction),
            WaveLightingParameters value
                when settings.Effect == LightingEffect.Wave =>
                new Rz090528WaveEffect(value.Direction),
            WheelLightingParameters value
                when settings.Effect == LightingEffect.Wheel =>
                new Rz090528WheelEffect(value.Direction),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Single,
                PrimaryColor: { } primary,
            } value when settings.Effect == LightingEffect.Starlight =>
                new Rz090528StarlightEffect(
                    value.ColorMode,
                    primary,
                    default,
                    StarlightDuration(value.Duration)),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                PrimaryColor: { } primary,
                SecondaryColor: { } secondary,
            } value when settings.Effect == LightingEffect.Starlight =>
                new Rz090528StarlightEffect(
                    value.ColorMode,
                    primary,
                    secondary,
                    StarlightDuration(value.Duration)),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } value when settings.Effect == LightingEffect.Starlight =>
                new Rz090528StarlightEffect(
                    value.ColorMode,
                    default,
                    default,
                    StarlightDuration(value.Duration)),
            null when settings.Effect == LightingEffect.Static =>
                new Rz090528StaticEffect(settings.Color),
            null when settings.Effect == LightingEffect.Spectrum =>
                new Rz090528SpectrumEffect(),
            null when settings.Effect == LightingEffect.Ripple =>
                new Rz090528RippleEffect(settings.Color),
            _ => throw new CapabilityUnavailableException(
                "The requested PID 02C6 host-rendered effect is not admitted."),
        };
    }

    internal static TimeSpan StarlightDuration(StarlightLightingDuration duration) =>
        duration switch
        {
            StarlightLightingDuration.Short => TimeSpan.FromMilliseconds(500),
            StarlightLightingDuration.Medium => TimeSpan.FromMilliseconds(1500),
            StarlightLightingDuration.Long => TimeSpan.FromMilliseconds(2000),
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

    internal static TimeSpan ReactiveDuration(ReactiveLightingDuration duration) =>
        duration switch
        {
            ReactiveLightingDuration.Short => TimeSpan.FromMilliseconds(500),
            ReactiveLightingDuration.Medium => TimeSpan.FromMilliseconds(1500),
            ReactiveLightingDuration.Long => TimeSpan.FromMilliseconds(2000),
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

    private async Task WriteMatrixFrameCoreAsync(
        Rz090528MatrixFrame frame,
        CancellationToken cancellationToken,
        bool expectResponse = true)
    {
        if (!expectResponse)
        {
            await SubmitMatrixFrameCoreAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        for (int row = 0; row < Rz090528MatrixFrame.RowCount; row++)
        {
            byte[] arguments = Rz090528MatrixRowProtocol.EncodeRow(row, frame);
            var command = new RazerCommandSpec(
                Rz090528MatrixRowProtocol.CommandClass,
                Rz090528MatrixRowProtocol.RowCommandId,
                arguments,
                arguments.Length,
                payload => payload.Span.SequenceEqual(arguments));
            _ = await ExecuteCoreAsync(
                command,
                write: true,
                cancellationToken).ConfigureAwait(false);
        }

        byte[] latch = Rz090528MatrixRowProtocol.LatchArguments.ToArray();
        var latchCommand = new RazerCommandSpec(
            Rz090528MatrixRowProtocol.CommandClass,
            Rz090528MatrixRowProtocol.LatchCommandId,
            latch,
            latch.Length,
            payload => payload.Span.SequenceEqual(latch));
        _ = await ExecuteCoreAsync(
            latchCommand,
            write: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SubmitMatrixFrameCoreAsync(
        Rz090528MatrixFrame frame,
        CancellationToken cancellationToken)
    {
        const int commandCount = Rz090528MatrixFrame.RowCount + 1;
        int rowBytes =
            Rz090528MatrixFrame.RowCount
            * Rz090528MatrixRowProtocol.RowPayloadLength;
        int latchBytes = Rz090528MatrixRowProtocol.LatchArguments.Length;
        byte[] arguments = GC.AllocateUninitializedArray<byte>(
            rowBytes + latchBytes);
        var commands = new RazerCommandSpec[commandCount];
        for (int row = 0; row < Rz090528MatrixFrame.RowCount; row++)
        {
            int offset = row * Rz090528MatrixRowProtocol.RowPayloadLength;
            Memory<byte> payload = arguments.AsMemory(
                offset,
                Rz090528MatrixRowProtocol.RowPayloadLength);
            Rz090528MatrixRowProtocol.EncodeRow(row, frame, payload.Span);
            commands[row] = new(
                Rz090528MatrixRowProtocol.CommandClass,
                Rz090528MatrixRowProtocol.RowCommandId,
                payload,
                payload.Length);
        }

        Memory<byte> latch = arguments.AsMemory(rowBytes, latchBytes);
        Rz090528MatrixRowProtocol.LatchArguments.Span.CopyTo(latch.Span);
        commands[^1] = new(
            Rz090528MatrixRowProtocol.CommandClass,
            Rz090528MatrixRowProtocol.LatchCommandId,
            latch,
            latch.Length);
        await SubmitStreamBatchCoreAsync(commands, cancellationToken)
            .ConfigureAwait(false);
    }

    private void StartStreamCore(IRz090528LightingEffect effect)
    {
        while (streamWake.Wait(0))
        {
        }
        var cancellation = new CancellationTokenSource();
        streamCancellation = cancellation;
        streamTask = RunStreamAsync(effect, cancellation.Token);
    }

    private async Task RunStreamAsync(
        IRz090528LightingEffect effect,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
            TimeSpan nextFrameAt = elapsed + effect.GetNextFrameDelay(elapsed);
            while (true)
            {
                elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                TimeSpan remaining = nextFrameAt - elapsed;
                bool woke = remaining > TimeSpan.Zero
                    && await WaitForStreamWakeOrDelayAsync(
                        remaining,
                        cancellationToken).ConfigureAwait(false);
                if (woke)
                {
                    // Key-triggered effects wake this single writer. Present the
                    // pulse immediately, then resume the 30 Hz deadline sequence.
                    nextFrameAt = timeProvider.GetElapsedTime(effectStartedTimestamp);
                }

                elapsed = timeProvider.GetElapsedTime(effectStartedTimestamp);
                Rz090528MatrixFrame frame;
                lock (effectSync)
                {
                    if (!ReferenceEquals(activeEffect, effect))
                    {
                        return;
                    }

                    frame = effect.RenderFrame(elapsed);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await WriteMatrixFrameCoreAsync(
                    frame,
                    CancellationToken.None,
                    expectResponse: false)
                    .ConfigureAwait(false);

                TimeSpan observedAfterWrite = timeProvider.GetElapsedTime(
                    effectStartedTimestamp);
                nextFrameAt = AdvanceFrameDeadline(
                    effect,
                    nextFrameAt,
                    observedAfterWrite);
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
                    lastDeliveredState = null;
                    ownershipState = LightingRendererOwnershipState.Faulted;
                }
            }
        }
    }

    internal static TimeSpan AdvanceFrameDeadline(
        IRz090528LightingEffect effect,
        TimeSpan currentDeadline,
        TimeSpan observedAfterWrite)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (currentDeadline < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDeadline));
        }
        if (observedAfterWrite < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAfterWrite));
        }

        TimeSpan cursor = currentDeadline;
        while (cursor <= observedAfterWrite)
        {
            TimeSpan delay = effect.GetNextFrameDelay(cursor);
            if (delay <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    "A continuously rendered PID 02C6 lighting effect returned a non-positive frame delay.");
            }
            cursor += delay;
        }

        return cursor;
    }

    private async Task<bool> WaitForStreamWakeOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var iterationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timer = Task.Delay(delay, timeProvider, iterationCancellation.Token);
        Task wake = streamWake.WaitAsync(iterationCancellation.Token);
        Task completed = await Task.WhenAny(timer, wake).ConfigureAwait(false);
        iterationCancellation.Cancel();
        await completed.ConfigureAwait(false);
        return ReferenceEquals(completed, wake);
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

    private (
        IRz090528LightingEffect? Effect,
        LightingSettings? Settings,
        long StartedTimestamp,
        LightingRendererOwnershipState Ownership) CaptureOwnedEffectCore()
    {
        lock (effectSync)
        {
            return (
                activeEffect,
                lastDeliveredState,
                effectStartedTimestamp,
                ownershipState);
        }
    }

    private static bool CanRetainRenderer(
        LightingSettings? previous,
        LightingSettings requested) =>
        previous is not null
        && previous.Effect != LightingEffect.Off
        && previous.Effect == requested.Effect
        && previous.Color == requested.Color
        && Equals(previous.Parameters, requested.Parameters);

    private async Task<bool> TryRestoreOwnedEffectCoreAsync(
        (
            IRz090528LightingEffect? Effect,
            LightingSettings? Settings,
            long StartedTimestamp,
            LightingRendererOwnershipState Ownership) previous)
    {
        if (previous.Effect is null || previous.Settings is null)
        {
            return false;
        }

        try
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(
                previous.StartedTimestamp);
            await WriteMatrixFrameCoreAsync(
                previous.Effect.RenderFrame(elapsed),
                CancellationToken.None).ConfigureAwait(false);
            lock (effectSync)
            {
                activeEffect = previous.Effect;
                lastDeliveredState = previous.Settings;
                effectStartedTimestamp = previous.StartedTimestamp;
                ownershipState = previous.Ownership;
            }

            if (previous.Effect.RequiresContinuousRendering)
            {
                StartStreamCore(previous.Effect);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ClearOwnedEffectCore(LightingSettings? settings)
    {
        lock (effectSync)
        {
            activeEffect = null;
            lastDeliveredState = settings;
            ownershipState = LightingRendererOwnershipState.None;
        }
    }

    private void SetOwnership(LightingRendererOwnershipState ownership)
    {
        lock (effectSync)
        {
            ownershipState = ownership;
        }
    }

    private static LightingRendererOwnershipState OwnershipFor(
        LightingEffect effect) => effect switch
        {
            LightingEffect.Static => LightingRendererOwnershipState.Static,
            LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
            LightingEffect.Starlight => LightingRendererOwnershipState.Starlight,
            LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
            LightingEffect.Tidal => LightingRendererOwnershipState.Tidal,
            LightingEffect.Wave => LightingRendererOwnershipState.Wave,
            LightingEffect.Wheel => LightingRendererOwnershipState.Wheel,
            LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
            LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
            LightingEffect.AmbientAwareness =>
                LightingRendererOwnershipState.AmbientAwareness,
            LightingEffect.AudioMeter => LightingRendererOwnershipState.AudioMeter,
            LightingEffect.Fire => LightingRendererOwnershipState.Fire,
            _ => LightingRendererOwnershipState.None,
        };

    private async Task SetAndConfirmRawCoreAsync(
        byte raw,
        CancellationToken cancellationToken)
    {
        await SetRawCoreAsync(raw, cancellationToken).ConfigureAwait(false);
        if (await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false) != raw)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "PID 02C6 brightness did not match independent readback.");
        }
    }

    private async Task SetAndVerifyCoreAsync(
        byte requestedRaw,
        byte baselineRaw,
        CancellationToken cancellationToken)
    {
        try
        {
            await SetRawCoreAsync(requestedRaw, cancellationToken).ConfigureAwait(false);
            byte confirmed = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed != requestedRaw)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "PID 02C6 brightness did not match independent readback.");
            }
        }
        catch (Exception exception)
        {
            bool restored = await TryRestoreCoreAsync(baselineRaw).ConfigureAwait(false);
            if (!restored)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "PID 02C6 brightness failed and the saved value could not be restored.",
                    exception);
            }

            throw;
        }
    }

    private async Task<RazerDeviceMode> QueryDeviceModeCoreAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> response = await ExecuteCoreAsync(
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.QueryOperationId,
                DeviceModeProtocol.QueryArguments.ToArray(),
                expectedPayloadLength: 2,
                payload => DeviceModeProtocol.TryDecodeQuery(
                    payload.Span,
                    out _)),
            write: false,
            cancellationToken).ConfigureAwait(false);
        if (!DeviceModeProtocol.TryDecodeQuery(
                response.Span,
                out RazerDeviceMode mode))
        {
            throw new IOException("PID 02C6 device-mode response was malformed.");
        }

        return mode;
    }

    private async Task SetDeviceModeCoreAsync(
        RazerDeviceMode mode,
        CancellationToken cancellationToken)
    {
        byte[] arguments = DeviceModeProtocol.CreateSetArguments(mode);
        _ = await ExecuteCoreAsync(
            new RazerCommandSpec(
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.SetOperationId,
                arguments,
                expectedPayloadLength: arguments.Length,
                payload => DeviceModeProtocol.TryDecodeSet(payload.Span, mode)),
            write: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRestoreOwnedDeviceModeCoreAsync()
    {
        if (!ownsDriverMode)
        {
            SetDriverModeRestoreOutcome(DriverModeRestoreOutcome.NotOwned);
            return;
        }

        try
        {
            // Never overwrite a mode another controller already changed.
            if (await QueryDeviceModeCoreAsync(CancellationToken.None).ConfigureAwait(false)
                != RazerDeviceMode.Driver)
            {
                ownsDriverMode = false;
                SetDriverModeRestoreOutcome(DriverModeRestoreOutcome.ForeignChange);
                return;
            }

            await SetDeviceModeCoreAsync(
                RazerDeviceMode.Normal,
                CancellationToken.None).ConfigureAwait(false);
            RazerDeviceMode confirmed = await QueryDeviceModeCoreAsync(
                CancellationToken.None).ConfigureAwait(false);
            if (confirmed != RazerDeviceMode.Normal)
            {
                SetDriverModeRestoreOutcome(DriverModeRestoreOutcome.Indeterminate);
                return;
            }

            ownsDriverMode = false;
            SetDriverModeRestoreOutcome(DriverModeRestoreOutcome.Released);
        }
        catch
        {
            // Closing must still release the HID handle. This bounded cleanup is
            // not retried in a background fight with another controller.
            SetDriverModeRestoreOutcome(DriverModeRestoreOutcome.Failed);
        }
    }

    private void SetDriverModeRestoreOutcome(DriverModeRestoreOutcome outcome) =>
        Volatile.Write(ref driverModeRestoreOutcome, (int)outcome);

    private async Task<BrightnessRoundTripResult> ValidateRawRoundTripCoreAsync(
        byte baselineRaw,
        byte targetRaw,
        CancellationToken cancellationToken)
    {
        byte appliedRaw = baselineRaw;
        Exception? applyFailure = null;
        try
        {
            await SetRawCoreAsync(targetRaw, cancellationToken).ConfigureAwait(false);
            appliedRaw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            if (appliedRaw != targetRaw)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "PID 02C6 brightness did not match the target readback.");
            }
        }
        catch (Exception exception)
        {
            applyFailure = exception;
        }

        byte restoredRaw;
        try
        {
            if (appliedRaw != baselineRaw)
            {
                await SetRawCoreAsync(baselineRaw, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            restoredRaw = await QueryRawCoreAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception restoreFailure)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "PID 02C6 validation could not restore and confirm the saved brightness.",
                restoreFailure);
        }

        if (restoredRaw != baselineRaw)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "PID 02C6 validation restoration readback did not match the saved brightness.");
        }

        if (applyFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(applyFailure)
                .Throw();
        }

        return new(baselineRaw, targetRaw, appliedRaw, restoredRaw);
    }

    private static byte StepTargetRaw(
        byte baselineRaw,
        KeyboardBrightnessDirection direction)
    {
        int baselinePercent = LightingBrightnessProtocol.RawToPercent(baselineRaw);
        byte requestedPercent = checked((byte)(direction
            == KeyboardBrightnessDirection.Increase
                ? Math.Min(100, baselinePercent + StepPercent)
                : Math.Max(0, baselinePercent - StepPercent)));
        return LightingBrightnessProtocol.PercentToRaw(requestedPercent);
    }

    private async Task<bool> TryRestoreCoreAsync(byte baselineRaw)
    {
        try
        {
            await SetRawCoreAsync(baselineRaw, CancellationToken.None).ConfigureAwait(false);
            return await QueryRawCoreAsync(CancellationToken.None).ConfigureAwait(false)
                == baselineRaw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<LogoLightingMode> QueryLogoCoreAsync(
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> state = await ExecuteCoreAsync(
            new RazerCommandSpec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.QueryStateCommandId,
                LogoLightingProtocol.QueryArguments(LogoStorageSelector),
                expectedPayloadLength: 3,
                payload => LogoLightingProtocol.TryDecodeState(
                    payload.Span,
                    LogoStorageSelector,
                    out _)),
            write: false,
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> effect = await ExecuteCoreAsync(
            new RazerCommandSpec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.QueryEffectCommandId,
                LogoLightingProtocol.QueryArguments(LogoStorageSelector),
                expectedPayloadLength: 3,
                payload => LogoLightingProtocol.TryDecodeEffect(
                    payload.Span,
                    LogoStorageSelector,
                    out _)),
            write: false,
            cancellationToken).ConfigureAwait(false);
        if (!LogoLightingProtocol.TryDecodeMode(
                state.Span,
                effect.Span,
                LogoStorageSelector,
                out LogoLightingMode mode))
        {
            throw new IOException(
                "PID 02C6 logo response was malformed or unsupported.");
        }

        return mode;
    }

    private async Task SetLogoCoreAsync(
        LogoLightingMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != LogoLightingMode.Off)
        {
            byte[] effect = LogoLightingProtocol.SetEffectArguments(
                mode,
                LogoStorageSelector);
            _ = await ExecuteCoreAsync(
                new RazerCommandSpec(
                    LogoLightingProtocol.CommandClass,
                    LogoLightingProtocol.SetEffectCommandId,
                    effect,
                    expectedPayloadLength: effect.Length,
                    payload => payload.Span.SequenceEqual(effect)),
                write: true,
                cancellationToken).ConfigureAwait(false);
        }

        byte[] state = LogoLightingProtocol.SetStateArguments(
            mode,
            LogoStorageSelector);
        _ = await ExecuteCoreAsync(
            new RazerCommandSpec(
                LogoLightingProtocol.CommandClass,
                LogoLightingProtocol.SetStateCommandId,
                state,
                expectedPayloadLength: state.Length,
                payload => payload.Span.SequenceEqual(state)),
            write: true,
            cancellationToken).ConfigureAwait(false);

        if (await QueryLogoCoreAsync(cancellationToken).ConfigureAwait(false) != mode)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "PID 02C6 logo mode did not match independent readback.");
        }
    }

    private async Task<bool> TryRestoreLogoCoreAsync(LogoLightingMode mode)
    {
        try
        {
            await SetLogoCoreAsync(mode, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte> QueryRawCoreAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> response = await ExecuteCoreAsync(
            new RazerCommandSpec(
                LightingBrightnessProtocol.OperationClass,
                LightingBrightnessProtocol.QueryOperationId,
                LightingBrightnessProtocol.QueryArguments.ToArray(),
                3,
                payload => LightingBrightnessProtocol.TryDecodeQuery(
                    payload.Span,
                    out _)),
            write: false,
            cancellationToken).ConfigureAwait(false);
        if (!LightingBrightnessProtocol.TryDecodeQuery(response.Span, out byte raw))
        {
            throw new IOException("PID 02C6 brightness response was malformed.");
        }

        return raw;
    }

    private async Task SetRawCoreAsync(
        byte raw,
        CancellationToken cancellationToken)
    {
        byte[] arguments = LightingBrightnessProtocol.CreateSetArguments(raw);
        _ = await ExecuteCoreAsync(
            new RazerCommandSpec(
                LightingBrightnessProtocol.OperationClass,
                LightingBrightnessProtocol.SetOperationId,
                arguments,
                3,
                payload => LightingBrightnessProtocol.TryDecodeSet(
                    payload.Span,
                    raw)),
            write: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> ExecuteCoreAsync(
        RazerCommandSpec command,
        bool write,
        CancellationToken cancellationToken)
    {
        await featureIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await session.ExecuteAsync(
                command,
                () => TransactionIdAssignment.Valid(AllocateTransaction()),
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
            if (write)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "PID 02C6 lighting write was canceled after its session opened.");
            }

            throw new OperationCanceledException(cancellationToken);
        }

        if (!write)
        {
            throw result.Failure == HidSessionFailureKind.Timeout
                ? new TimeoutException(
                    $"PID 02C6 lighting query {command.ClassId:X2}/{command.CommandId:X2} timed out.")
                : new IOException(
                    $"PID 02C6 lighting query {command.ClassId:X2}/{command.CommandId:X2} failed ({result.Failure}).");
        }

        WriteSubmissionState state = result.Failure is
            HidSessionFailureKind.QueuedCanceled or
            HidSessionFailureKind.ConflictBlocked or
            HidSessionFailureKind.ConflictDetectionFailed
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate;
        throw new BladeWriteException(
            state,
            $"PID 02C6 lighting command {command.ClassId:X2}/{command.CommandId:X2} " +
            $"failed ({result.Failure}).");
    }

    private async Task SubmitStreamBatchCoreAsync(
        IReadOnlyList<RazerCommandSpec> commands,
        CancellationToken cancellationToken)
    {
        if (session is not IRazerSetFeatureCommandSession submitter)
        {
            throw new CapabilityUnavailableException(
                "PID 02C6 SET-only matrix streaming is unavailable.");
        }

        await featureIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await submitter.SubmitBatchAsync(
                commands,
                () => TransactionIdAssignment.Valid(AllocateTransaction()),
                exchangeTimeout,
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
        throw new BladeWriteException(
            state,
            $"PID 02C6 SET-only matrix frame failed ({result.Failure}).");
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(
                closing || Volatile.Read(ref disposed) != 0,
                this);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (lifecycleSync)
        {
            if (closing || Volatile.Read(ref disposed) != 0)
            {
                operationGate.Release();
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }

    private byte AllocateTransaction()
    {
        int value = nextTransaction;
        nextTransaction = value == byte.MaxValue ? 1 : value + 1;
        return checked((byte)value);
    }
}
