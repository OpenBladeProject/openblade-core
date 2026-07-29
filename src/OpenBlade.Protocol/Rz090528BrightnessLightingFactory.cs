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

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;

    internal Rz090528BrightnessLightingFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory)
    {
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        Capabilities = Rz090528BrightnessLightingPort.CreateCapabilities();
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
            new Rz090528BrightnessLightingPort(actor, ExchangeTimeout),
            null);
    }
}

internal sealed class Rz090528BrightnessLightingPort : IProductionLightingPort
{
    private const byte StepPercent = 10;
    private readonly ValidationActor actor;
    private readonly IRazerCommandSession session;
    private readonly TimeSpan exchangeTimeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int nextTransaction = 1;
    private int disposed;

    internal Rz090528BrightnessLightingPort(
        ValidationActor actor,
        TimeSpan exchangeTimeout)
    {
        this.actor = actor;
        session = actor.Session;
        this.exchangeTimeout = exchangeTimeout;
        Capabilities = CreateCapabilities();
    }

    public DeviceCapabilities Capabilities { get; }

    public LightingRendererOwnershipState OwnershipState =>
        LightingRendererOwnershipState.None;

    internal static DeviceCapabilities CreateCapabilities() => new(
        new HashSet<PerformancePreset>(),
        new HashSet<FanMode>(),
        new HashSet<LightingEffect>(),
        new HashSet<int>(),
        SupportsBatteryProtectionOff: false,
        SupportsTemporaryFullCharge: false)
    {
        SupportsKeyboardBrightness = true,
    };

    public async Task<LightingSettings?> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte raw = await QueryRawCoreAsync(cancellationToken).ConfigureAwait(false);
            return raw == 0 ? LightingSettingsFactory.Off() : null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<LogoLightingMode?> ReadLogoStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<LogoLightingMode?>(null);
    }

    public Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new CapabilityUnavailableException(
            "PID 02C6 lighting effects and logo control are not admitted."));
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
        CancellationToken cancellationToken) =>
        Reject(cancellationToken);

    public Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) =>
        Reject(cancellationToken);

    public Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) =>
        Reject(cancellationToken);

    public Task<LightingRendererOwnershipState> InvalidateAsync() =>
        Task.FromResult(LightingRendererOwnershipState.None);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
        }
    }

    private static Task Reject(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new CapabilityUnavailableException(
            "PID 02C6 matrix lighting is not admitted."));
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
            throw applyFailure;
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
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(AllocateTransaction()),
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
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
                    "PID 02C6 brightness write was canceled after its session opened.");
            }

            throw new OperationCanceledException(cancellationToken);
        }

        if (!write)
        {
            throw result.Failure == HidSessionFailureKind.Timeout
                ? new TimeoutException("PID 02C6 brightness query timed out.")
                : new IOException($"PID 02C6 brightness query failed ({result.Failure}).");
        }

        WriteSubmissionState state = result.Failure is
            HidSessionFailureKind.QueuedCanceled or
            HidSessionFailureKind.ConflictBlocked or
            HidSessionFailureKind.ConflictDetectionFailed
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate;
        throw new BladeWriteException(
            state,
            $"PID 02C6 brightness write failed ({result.Failure}).");
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(Rz090528BrightnessLightingPort));
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref disposed) != 0)
        {
            operationGate.Release();
            throw new ObjectDisposedException(nameof(Rz090528BrightnessLightingPort));
        }
    }

    private byte AllocateTransaction()
    {
        int value = nextTransaction;
        nextTransaction = value == byte.MaxValue ? 1 : value + 1;
        return checked((byte)value);
    }
}
