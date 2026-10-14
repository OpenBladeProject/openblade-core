using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

/// <summary>
/// Owns the retained exact-device lighting session and its renderer lifecycle.
/// Ordinary operations enter the device provider's hardware gate; core reads,
/// Driver-mode port access and cleanup require the caller to hold that gate.
/// Renderer pulses and frames for an already owned renderer deliberately bypass
/// that gate and rely on the port's writer synchronization.
/// </summary>
internal sealed class ProductionLightingSession
{
    private readonly IProductionLightingPortFactory lightingFactory;
    private readonly ILogger<WindowsVerifiedBladeDeviceProvider>? logger;
    private readonly LowPowerGate? lowPowerGate;
    private readonly Func<CancellationToken, Task> enterHardwareOperation;
    private readonly Action releaseHardwareOperation;
    private readonly object lightingPortOpenDiagnosticSync = new();
    private readonly string?[] lastLightingPortOpenDiagnostics =
        new string?[Enum.GetValues<LightingPortOperation>().Length];

    private IProductionLightingPort? lightingPort;
    private Task<LightingRendererOwnershipState>? lightingPortRetirement;
    private IProductionLightingPort? lightingRefreshRateReconciledPort;
    private int reconciledLightingRefreshRate = -1;
    // Desired Windows playback state, retained independently of any one
    // renderer port so a recycled exact-device session can reapply it.
    private int desiredPlaybackMuteState = -1;
    private int desiredLightingRefreshRate = (int)LightingRefreshRate.ThirtyHz;

    internal ProductionLightingSession(
        IProductionLightingPortFactory lightingFactory,
        ILogger<WindowsVerifiedBladeDeviceProvider>? logger,
        LowPowerGate? lowPowerGate,
        Func<CancellationToken, Task> enterHardwareOperation,
        Action releaseHardwareOperation)
    {
        this.lightingFactory = lightingFactory;
        this.logger = logger;
        this.lowPowerGate = lowPowerGate;
        this.enterHardwareOperation = enterHardwareOperation;
        this.releaseHardwareOperation = releaseHardwareOperation;
    }

    internal bool HasOwnedKeyTriggeredRenderer =>
        Volatile.Read(ref lightingPort)?.OwnershipState is
            LightingRendererOwnershipState.Reactive or
            LightingRendererOwnershipState.Ripple;

    internal bool HasOwnedAmbientRenderer =>
        Volatile.Read(ref lightingPort)?.OwnershipState is
            LightingRendererOwnershipState.AmbientAwareness or
            LightingRendererOwnershipState.AudioMeter;

    // Only service Driver-mode orchestration accesses the retained port directly,
    // with the provider hardware gate held. It must not open a port in low power.
    internal IProductionLightingPort? RetainedPort => lightingPort;

    internal readonly record struct TransitionLightingReadResult(
        LightingSettings? State,
        bool OffConfirmationUnavailable);

    internal Task SetLightingAsync(
        LightingSettings value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!LightingSettingsValidation.IsWellFormed(value)
            || !lightingFactory.Capabilities.LightingEffects.Contains(value.Effect)
            || value.LogoMode is { } logoMode
                && !lightingFactory.Capabilities.LogoLightingModes.Contains(logoMode)
            || value.Parameters is SequenceLightingParameters sequence
                && !string.Equals(
                    sequence.LayoutId,
                    lightingFactory.Capabilities.KeyboardLightingLayoutId,
                    StringComparison.Ordinal))
        {
            return RejectProductionWrite("The requested lighting setting is not admitted.");
        }

        return ExecuteProductionLightingWriteAsync(value, cancellationToken);
    }

    internal void SetDesiredLightingRefreshRate(LightingRefreshRate refreshRate)
    {
        if (!Enum.IsDefined(refreshRate))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshRate));
        }

        Volatile.Write(ref desiredLightingRefreshRate, (int)refreshRate);
    }

    internal async Task SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken cancellationToken)
    {
        if (!lightingFactory.Capabilities.SupportedLightingRefreshRates.Contains(
                refreshRate))
        {
            await RejectProductionWrite(
                "The requested lighting refresh rate is not admitted.")
                .ConfigureAwait(false);
            return;
        }

        await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        try
        {
            IProductionLightingPort port = await GetLightingPortForWriteCoreAsync(
                LightingPortOperation.LightingChange,
                cancellationToken).ConfigureAwait(false);
            ThrowIfOrdinaryLightingWriteBlocked();
            await port.SetLightingRefreshRateAsync(refreshRate, cancellationToken)
                .ConfigureAwait(false);
            SetDesiredLightingRefreshRate(refreshRate);
            lightingRefreshRateReconciledPort = port;
            reconciledLightingRefreshRate = (int)refreshRate;
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    internal Task<byte> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(direction))
        {
            return Task.FromException<byte>(
                new ArgumentOutOfRangeException(nameof(direction)));
        }

        return ExecuteProductionKeyboardBrightnessStepAsync(
            direction,
            cancellationToken);
    }

    internal Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken) =>
        lightingFactory.Capabilities.LightingEffects.Contains(LightingEffect.Reactive)
            || lightingFactory.Capabilities.LightingEffects.Contains(LightingEffect.Ripple)
            ? ExecuteProductionLightingPulseAsync(pulse, cancellationToken)
            : RejectProductionWrite("Key-triggered lighting is not admitted.");

    internal Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.IsWellFormed()
            && (lightingFactory.Capabilities.LightingEffects.Contains(
                    LightingEffect.AmbientAwareness)
                || lightingFactory.Capabilities.LightingEffects.Contains(
                    LightingEffect.AudioMeter))
                ? ExecuteProductionAmbientLightingFrameAsync(frame.Copy(), cancellationToken)
                : RejectProductionWrite("Ambient Awareness input is not admitted.");
    }

    internal Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) =>
        lightingFactory.Capabilities.SupportsPlaybackMuteIndicator
            ? ExecuteProductionPlaybackMuteIndicatorAsync(
                muted,
                cancellationToken)
            : RejectProductionWrite("Playback mute indication is not admitted.");

    internal async Task<LightingRendererOwnershipState>
        ReadLightingRendererOwnershipAsync(CancellationToken cancellationToken)
    {
        await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        try
        {
            IProductionLightingPort? current = lightingPort;
            LightingRendererOwnershipState observed = current?.OwnershipState
                ?? LightingRendererOwnershipState.None;
            if (observed == LightingRendererOwnershipState.Faulted)
            {
                // Preserve Faulted for this observation, but retire the poisoned
                // writer before a later explicit lighting action opens a new one.
                await ResetLightingPortCoreAsync(current).ConfigureAwait(false);
            }

            return observed;
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    internal async Task<LightingLowPowerOffResult?> TurnOffRetainedLightingForLowPowerAsync(
        Func<bool> freshAdmission,
        Func<bool> stillSuspending,
        CancellationToken cancellationToken)
    {
        await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        try
        {
            IProductionLightingPort? port = lightingPort;
            if (port is null)
            {
                return null;
            }
            if (!freshAdmission() || !stillSuspending())
            {
                return new(port.OwnershipState,
                    LightingLowPowerOffOutcome.KnownNotApplied,
                    "Fresh exact-device admission or the suspend boundary is unavailable.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await port.TurnOffForLowPowerAsync(stillSuspending, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    internal async Task<LightingRendererInvalidationResult>
        InvalidateLightingRendererAsync(
        CancellationToken cancellationToken,
        bool lowPower = false,
        Func<bool>? stillRetiring = null)
    {
        await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stillRetiring?.Invoke() == false)
            {
                return new(LightingRendererOwnershipState.None, DriverModeRestoreOutcome.NotOwned);
            }
            IProductionLightingPort? current = lightingPort;
            if (current is null)
            {
                return new LightingRendererInvalidationResult(
                    LightingRendererOwnershipState.None,
                    DriverModeRestoreOutcome.NotOwned);
            }

            ClearLightingRefreshRateReconciliation(current);
            LightingRendererOwnershipState ownership;
            try
            {
                lightingPortRetirement ??= lowPower
                    ? current.InvalidateForLowPowerAsync()
                    : current.InvalidateAsync();
                ownership = await lightingPortRetirement.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                lightingPortRetirement = null;
                lightingPort = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The close owns its own lifetime. Retain both its task and
                // the port so cancellation neither invents a renderer fault
                // nor admits another hardware owner before it finishes.
                throw;
            }
            catch
            {
                ownership = LightingRendererOwnershipState.Faulted;
            }

            DriverModeRestoreOutcome outcome = current.DriverModeRestoreOutcome;
            if (outcome is DriverModeRestoreOutcome.Indeterminate
                or DriverModeRestoreOutcome.Failed)
            {
                logger?.LogWarning(
                    "Lighting-port cleanup reported keyboard Driver-mode restore outcome {Outcome}.",
                    outcome);
            }
            else
            {
                logger?.LogInformation(
                    "Lighting-port cleanup reported keyboard Driver-mode restore outcome {Outcome}.",
                    outcome);
            }

            return new LightingRendererInvalidationResult(ownership, outcome);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    internal async Task<LightingRendererOwnershipState>
        RelinquishLightingRendererAsync(CancellationToken cancellationToken)
    {
        await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        try
        {
            IProductionLightingPort? current = lightingPort;
            return current is null
                ? LightingRendererOwnershipState.None
                : await current.RelinquishRendererAsync(cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    private async Task ExecuteProductionLightingWriteAsync(
        LightingSettings value,
        CancellationToken cancellationToken)
    {
        IProductionLightingPort? port = null;
        try
        {
            await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The lighting write was canceled before a verified session opened.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The lighting write was blocked because the device provider is closing.",
                exception);
        }

        try
        {
            port = await GetLightingPortForWriteCoreAsync(
                LightingPortOperation.LightingChange,
                cancellationToken).ConfigureAwait(false);
            if (!port.Capabilities.LightingEffects.Contains(value.Effect))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support the requested lighting effect.");
            }

            if (value.LogoMode is { } logoMode
                && !port.Capabilities.LogoLightingModes.Contains(logoMode))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support the requested logo lighting mode.");
            }

            if (value.Parameters is SequenceLightingParameters sequence
                && !string.Equals(
                    sequence.LayoutId,
                    port.Capabilities.KeyboardLightingLayoutId,
                    StringComparison.Ordinal))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support the requested keyboard layout.");
            }

            ThrowIfOrdinaryLightingWriteBlocked();

            await port.SetLightingAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException exception)
        {
            if (exception.SubmissionState == WriteSubmissionState.Indeterminate
                && port?.OwnershipState is
                    LightingRendererOwnershipState.None or
                    LightingRendererOwnershipState.Faulted)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The lighting write was canceled after a verified session opened.");
        }
        catch (Exception exception)
        {
            await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The lighting write outcome is indeterminate.",
                exception);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    private async Task<byte> ExecuteProductionKeyboardBrightnessStepAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken)
    {
        IProductionLightingPort? port = null;
        try
        {
            await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The keyboard brightness change was canceled before a verified session opened.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The keyboard brightness change was blocked because the device provider is closing.",
                exception);
        }

        try
        {
            port = await GetLightingPortForWriteCoreAsync(
                LightingPortOperation.KeyboardBrightnessChange,
                cancellationToken).ConfigureAwait(false);
            if (!port.Capabilities.SupportsKeyboardBrightness)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support keyboard brightness.");
            }

            ThrowIfOrdinaryLightingWriteBlocked();
            return await port.StepBrightnessAsync(direction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException exception)
        {
            if (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The keyboard brightness change was canceled after a verified session opened.");
        }
        catch (Exception exception)
        {
            await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The keyboard brightness change outcome is indeterminate.",
                exception);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    private async Task ExecuteProductionLightingPulseAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken)
    {
        // A pulse now changes only the in-memory key-triggered effect and
        // wakes its already-running matrix stream. It performs no HID I/O,
        // so it must not wait behind unrelated firmware observations on the
        // global hardware gate. The lighting port still serializes this with
        // effect replacement and safely rejects a concurrent close.
        IProductionLightingPort? port = Volatile.Read(ref lightingPort);
        try
        {
            if (port is null
                || port.OwnershipState is not (
                    LightingRendererOwnershipState.Reactive or
                    LightingRendererOwnershipState.Ripple))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The key-triggered lighting renderer is not active.");
            }

            ThrowIfOrdinaryLightingWriteBlocked();

            await port.PulseReactiveKeyAsync(pulse, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The key-triggered lighting input was canceled before it was queued.");
        }
        catch (Exception exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "The key-triggered lighting input could not be queued.",
                exception);
        }
    }

    private async Task ExecuteProductionAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken)
    {
        IProductionLightingPort? ownedPort = Volatile.Read(ref lightingPort);
        if (ownedPort?.OwnershipState is
            LightingRendererOwnershipState.AmbientAwareness or
            LightingRendererOwnershipState.AudioMeter)
        {
            try
            {
                // The exact-device port is already admitted and owns its
                // own writer gate. Avoid re-entering the provider-wide HID
                // gate on every 20 Hz frame.
                ThrowIfOrdinaryLightingWriteBlocked();
                await ownedPort.PushAmbientLightingFrameAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Ambient Awareness was canceled before the next frame was queued.");
            }
            catch (Exception exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The Ambient Awareness frame outcome is indeterminate.",
                    exception);
            }
        }

        IProductionLightingPort? port = null;
        try
        {
            await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Ambient Awareness was canceled before a verified session opened.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Ambient Awareness was blocked because the device provider is closing.",
                exception);
        }

        try
        {
            port = await GetLightingPortForWriteCoreAsync(
                LightingPortOperation.AmbientLightingFrame,
                cancellationToken).ConfigureAwait(false);
            if (!(port.Capabilities.LightingEffects.Contains(
                        LightingEffect.AmbientAwareness)
                    || port.Capabilities.LightingEffects.Contains(
                        LightingEffect.AudioMeter)))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support ambient lighting frames.");
            }

            ThrowIfOrdinaryLightingWriteBlocked();

            await port.PushAmbientLightingFrameAsync(frame, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException exception)
        {
            if (exception.SubmissionState == WriteSubmissionState.Indeterminate)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Ambient Awareness was canceled after a verified session opened.");
        }
        catch (Exception exception)
        {
            await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The Ambient Awareness input outcome is indeterminate.",
                exception);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    private async Task ExecuteProductionPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref desiredPlaybackMuteState, muted ? 1 : 0);
        IProductionLightingPort? port = null;
        try
        {
            await enterHardwareOperation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Playback mute indication was canceled before a verified session opened.");
        }
        catch (ObjectDisposedException exception)
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Playback mute indication was blocked because the device provider is closing.",
                exception);
        }

        try
        {
            port = await GetLightingPortForWriteCoreAsync(
                LightingPortOperation.PlaybackMuteIndication,
                cancellationToken).ConfigureAwait(false);
            if (!port.Capabilities.SupportsPlaybackMuteIndicator)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The exact-device lighting session does not support playback mute indication.");
            }

            ThrowIfOrdinaryLightingWriteBlocked();

            await port.SetPlaybackMuteIndicatorAsync(muted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CapabilityUnavailableException)
        {
            throw;
        }
        catch (BladeWriteException exception)
        {
            if (exception.SubmissionState == WriteSubmissionState.Indeterminate
                || port?.OwnershipState is
                    LightingRendererOwnershipState.None or
                    LightingRendererOwnershipState.Faulted)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Playback mute indication was canceled after a verified session opened.");
        }
        catch (Exception exception)
        {
            await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The playback mute indicator outcome is indeterminate.",
                exception);
        }
        finally
        {
            releaseHardwareOperation();
        }
    }

    internal async Task DisposeLightingPortWithinDeadlineAsync()
    {
        IProductionLightingPort? currentLighting = lightingPort;
        lightingPort = null;
        ClearLightingRefreshRateReconciliation(currentLighting);
        if (currentLighting is null)
        {
            return;
        }

        try
        {
            bool drained = await currentLighting
                .DisposeWithinCleanupDeadlineAsync()
                .ConfigureAwait(false);
            if (!drained)
            {
                logger?.LogError(
                    "The production lighting port did not drain within its cleanup deadline. Native cleanup remains retained and the handle was not force-closed.");
            }
        }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "The production lighting port failed during device-provider shutdown.");
        }
    }

    internal async Task<IProductionLightingPort?> GetLightingPortCoreAsync(
        LightingPortOperation operation,
        CancellationToken cancellationToken)
    {
        await AwaitLightingPortRetirementCoreAsync(cancellationToken).ConfigureAwait(false);
        if (lightingPort is ExperimentalBladePort { IsDisposed: true } experimentalPort)
        {
            if (!experimentalPort.IsRecoverySessionDrained)
            {
                // Keep ownership until native cleanup is confirmed; never reuse the closed port.
                throw new IOException("Experimental session cleanup has not confirmed native operation drain.");
            }
            await DisposeLightingPortWithinDeadlineAsync().ConfigureAwait(false);
        }
        if (lowPowerGate?.IsActive == true)
        {
            // A read during suspend may inspect a retained session, but must
            // not open one or reconcile cadence/playback state as a side effect.
            return lightingPort;
        }
        if (lightingPort is { } retainedPort)
        {
            try
            {
                await ReconcileLightingRefreshRateCoreAsync(
                    retainedPort,
                    cancellationToken).ConfigureAwait(false);
                return retainedPort;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await ResetLightingPortCoreAsync(retainedPort).ConfigureAwait(false);
                throw;
            }
        }

        HidSessionResult<IProductionLightingPort> opened;
        try
        {
            opened = await (lowPowerGate is null
                ? lightingFactory.OpenAsync(cancellationToken)
                : lightingFactory.OpenAsync(() => !lowPowerGate.IsActive, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new LightingPortOpenFailure(
                HidSessionFailureKind.NativeIoFailure,
                Diagnostic: null,
                Origin: LightingPortOpenFailureOrigin.Threw,
                ExceptionType: exception.GetType().Name);
            // Native exception messages may contain host paths or device
            // identifiers, so diagnostic history receives only the type.
            ReportLightingPortOpenDiagnostic(operation, failure);
            return null;
        }

        IProductionLightingPort? port = opened.Value;
        if (port is null || opened.Failure is not null)
        {
            if (port is not null)
            {
                await port.DisposeAsync().ConfigureAwait(false);
            }

            ReportLightingPortOpenDiagnostic(operation, new(
                opened.Failure ?? HidSessionFailureKind.NativeIoFailure,
                opened.Diagnostic,
                LightingPortOpenFailureOrigin.Returned,
                ExceptionType: null));
            return null;
        }

        try
        {
            await ReconcileLightingRefreshRateCoreAsync(port, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ClearLightingRefreshRateReconciliation(port);
            await port.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        int muted = Volatile.Read(ref desiredPlaybackMuteState);
        if (muted >= 0)
        {
            try
            {
                ThrowIfOrdinaryLightingWriteBlocked();
                await port.SetPlaybackMuteIndicatorAsync(
                    muted != 0,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ClearLightingRefreshRateReconciliation(port);
                await port.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        lightingPort = port;
        ClearLightingPortOpenDiagnostics();
        return port;
    }

    private async Task ReconcileLightingRefreshRateCoreAsync(
        IProductionLightingPort port,
        CancellationToken cancellationToken)
    {
        LightingRefreshRate refreshRate = (LightingRefreshRate)Volatile.Read(
            ref desiredLightingRefreshRate);
        if (ReferenceEquals(lightingRefreshRateReconciledPort, port)
            && reconciledLightingRefreshRate == (int)refreshRate)
        {
            return;
        }

        if (port.Capabilities.SupportedLightingRefreshRates.Contains(refreshRate))
        {
            ThrowIfOrdinaryLightingWriteBlocked();
            await port.SetLightingRefreshRateAsync(refreshRate, cancellationToken)
                .ConfigureAwait(false);
        }

        lightingRefreshRateReconciledPort = port;
        reconciledLightingRefreshRate = (int)refreshRate;
    }

    private void ClearLightingRefreshRateReconciliation(
        IProductionLightingPort? port)
    {
        if (!ReferenceEquals(lightingRefreshRateReconciledPort, port))
        {
            return;
        }

        lightingRefreshRateReconciledPort = null;
        reconciledLightingRefreshRate = -1;
    }

    private async Task<IProductionLightingPort> GetLightingPortForWriteCoreAsync(
        LightingPortOperation operation,
        CancellationToken cancellationToken)
    {
        ThrowIfOrdinaryLightingWriteBlocked();
        try
        {
            await AwaitLightingPortRetirementCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "The lighting write was canceled while the previous port was still closing.");
        }
        IProductionLightingPort? port = await GetLightingPortCoreAsync(
            operation,
            cancellationToken).ConfigureAwait(false);
        ThrowIfOrdinaryLightingWriteBlocked();
        if (port is not null)
        {
            return port;
        }

        throw new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            GetLightingPortOpenFailureMessage(operation));
    }

    private async Task AwaitLightingPortRetirementCoreAsync(CancellationToken cancellationToken)
    {
        if (lightingPortRetirement is { } retirement)
        {
            await retirement.WaitAsync(cancellationToken).ConfigureAwait(false);
            lightingPortRetirement = null;
            lightingPort = null;
        }
    }

    private void ReportLightingPortOpenDiagnostic(
        LightingPortOperation operation,
        LightingPortOpenFailure failure)
    {
        if (logger is null)
        {
            return;
        }

        string message = FormatLightingPortOpenDiagnostic(operation, failure);
        lock (lightingPortOpenDiagnosticSync)
        {
            int index = (int)operation;
            if (string.Equals(
                lastLightingPortOpenDiagnostics[index],
                message,
                StringComparison.Ordinal))
            {
                return;
            }

            lastLightingPortOpenDiagnostics[index] = message;
        }

        logger.LogWarning("{LightingPortOpenDiagnostic}", message);
    }

    private void ClearLightingPortOpenDiagnostics()
    {
        lock (lightingPortOpenDiagnosticSync)
        {
            Array.Clear(lastLightingPortOpenDiagnostics);
        }
    }

    private static string GetLightingPortOpenFailureMessage(
        LightingPortOperation operation) =>
        $"The {GetLightingPortOperationText(operation)} could not open an exact-device lighting session.";

    private static string FormatLightingPortOpenDiagnostic(
        LightingPortOperation operation,
        LightingPortOpenFailure failure)
    {
        var fields = new List<string>
        {
            $"origin={GetLightingPortOpenFailureOriginText(failure.Origin)}",
            $"failure={failure.Failure}",
        };
        if (failure.ExceptionType is { } exceptionType)
        {
            fields.Add($"exceptionType={exceptionType}");
        }

        if (failure.Diagnostic is { } diagnostic)
        {
            fields.Add($"diagnosticFailure={diagnostic.Failure}");
            fields.Add($"phase={diagnostic.Phase}");
            if (diagnostic.NativeWin32Error is { } nativeError)
            {
                fields.Add($"nativeError={nativeError}");
            }

            if (diagnostic.TransferredByteCount is { } bytes)
            {
                fields.Add($"bytes={bytes}");
            }

            if (diagnostic.ProtocolValidationFailure is { } validation)
            {
                fields.Add($"validation={validation}");
            }
        }

        return $"{GetLightingPortOpenFailureMessage(operation)} ({string.Join(", ", fields)}).";
    }

    private static string GetLightingPortOperationText(
        LightingPortOperation operation) => operation switch
        {
            LightingPortOperation.LightingChange => "lighting change",
            LightingPortOperation.KeyboardBrightnessChange =>
                "keyboard brightness change",
            LightingPortOperation.AmbientLightingFrame =>
                "ambient lighting frame",
            LightingPortOperation.PlaybackMuteIndication =>
                "playback mute indication",
            LightingPortOperation.CapabilityObservation =>
                "lighting capability observation",
            LightingPortOperation.KeyboardDriverModeAcquisition =>
                "keyboard Driver-mode acquisition",
            LightingPortOperation.KeyboardDriverModeRead =>
                "keyboard Driver-mode read",
            LightingPortOperation.KeyboardDriverModeMaintenance =>
                "keyboard Driver-mode maintenance",
            LightingPortOperation.KeyboardDriverModeRelease =>
                "keyboard Driver-mode release",
            LightingPortOperation.KeyboardLightingStateRead =>
                "keyboard lighting state read",
            LightingPortOperation.LogoLightingStateRead =>
                "logo lighting state read",
            _ => operation.ToString(),
        };

    private static string GetLightingPortOpenFailureOriginText(
        LightingPortOpenFailureOrigin origin) => origin switch
        {
            LightingPortOpenFailureOrigin.Returned => "returned",
            LightingPortOpenFailureOrigin.Threw => "threw",
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

    private readonly record struct LightingPortOpenFailure(
        HidSessionFailureKind Failure,
        HidSessionFailureDiagnostic? Diagnostic,
        LightingPortOpenFailureOrigin Origin,
        string? ExceptionType);

    internal enum LightingPortOperation
    {
        LightingChange,
        KeyboardBrightnessChange,
        AmbientLightingFrame,
        PlaybackMuteIndication,
        CapabilityObservation,
        KeyboardDriverModeAcquisition,
        KeyboardDriverModeRead,
        KeyboardDriverModeMaintenance,
        KeyboardDriverModeRelease,
        KeyboardLightingStateRead,
        LogoLightingStateRead,
    }

    private enum LightingPortOpenFailureOrigin
    {
        Returned,
        Threw,
    }

    internal async Task<LightingSettings?> ReadLightingStateCoreAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IProductionLightingPort? port =
                await GetLightingPortCoreAsync(
                    LightingPortOperation.KeyboardLightingStateRead,
                    cancellationToken).ConfigureAwait(false);
            return port is null
                ? null
                : await port.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            // Brightness uses the command interface while the host renderer
            // writes the matrix through a separate interface. A transient
            // brightness read must not dispose an otherwise healthy renderer.
            // Retire only a port whose renderer has independently reported
            // that it is no longer usable.
            await RetireUnusableLightingPortAfterReadFailureAsync()
                .ConfigureAwait(false);
            return null;
        }
    }

    internal async Task<TransitionLightingReadResult>
        ReadPowerTransitionLightingStateCoreAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IProductionLightingPort? port =
                await GetLightingPortCoreAsync(
                    LightingPortOperation.KeyboardLightingStateRead,
                    cancellationToken).ConfigureAwait(false);
            return port is null
                ? new(null, OffConfirmationUnavailable: true)
                : new(
                    await port.ReadStateAsync(cancellationToken).ConfigureAwait(false),
                    OffConfirmationUnavailable: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            await RetireUnusableLightingPortAfterReadFailureAsync()
                .ConfigureAwait(false);
            return new(null, OffConfirmationUnavailable: true);
        }
    }

    private async Task RetireUnusableLightingPortAfterReadFailureAsync()
    {
        IProductionLightingPort? current = lightingPort;
        if (current?.OwnershipState is
            LightingRendererOwnershipState.None or
            LightingRendererOwnershipState.Faulted)
        {
            await ResetLightingPortCoreAsync(current).ConfigureAwait(false);
        }
    }

    internal async Task<LogoLightingMode?> ReadLogoLightingStateCoreAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IProductionLightingPort? port =
                await GetLightingPortCoreAsync(
                    LightingPortOperation.LogoLightingStateRead,
                    cancellationToken).ConfigureAwait(false);
            return port is null
                ? null
                : await port.ReadLogoStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task ResetLightingPortCoreAsync(IProductionLightingPort? port)
    {
        if (port is null || !ReferenceEquals(lightingPort, port))
        {
            return;
        }

        lightingPort = null;
        ClearLightingRefreshRateReconciliation(port);
        try
        {
            await port.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    internal void ThrowIfOrdinaryLightingWriteBlocked()
    {
        if (lowPowerGate?.IsActive == true)
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Ordinary keyboard lighting writes are deferred during a low-power transition.");
        }
    }

    private static Task RejectProductionWrite(string message) => Task.FromException(
        new CapabilityUnavailableException(message));
}
