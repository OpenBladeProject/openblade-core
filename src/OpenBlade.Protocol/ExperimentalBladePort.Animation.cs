using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private readonly TimeProvider animationTime = lightingTimeProvider ?? TimeProvider.System;
    private CancellationTokenSource? animationCancellation;
    private Task? animationTask;
    private readonly object animationEffectSync = new();
    private Blade18SpatialLightingEffect? spatialAnimation;
    private long animationStartedTimestamp;

    private static bool SupportsBlade18Animation(ExperimentalBladeModel model) =>
        model.ProductId == 0x02A0;

    private static bool IsBlade18Animation(LightingEffect effect) =>
        effect is LightingEffect.Fire or LightingEffect.Wheel or LightingEffect.Starlight
            or LightingEffect.Reactive or LightingEffect.Ripple or LightingEffect.Tidal;

    private static bool IsBlade18SpatialEffect(LightingEffect effect) =>
        effect is LightingEffect.Reactive or LightingEffect.Ripple or LightingEffect.Tidal;

    // Called with operationGate held. The loop uses cancellable waits for both
    // its timer and this gate, so it cannot be stranded behind the retiring caller.
    // A frame that already acquired the gate drains before retirement can enter.
    private async Task StopAnimationCoreAsync()
    {
        CancellationTokenSource? cancellation = animationCancellation;
        if (cancellation is null)
        {
            return;
        }
        cancellation.Cancel();
        lock (animationEffectSync)
        {
            spatialAnimation = null;
        }
        if (animationTask is { } task)
        {
            await task.ConfigureAwait(false);
        }
        animationCancellation = null;
        animationTask = null;
        cancellation.Dispose();
    }

    private void StartAnimationCore(
        LightingSettings settings, Blade18SpatialLightingEffect? spatialEffect)
    {
        var cancellation = new CancellationTokenSource();
        animationCancellation = cancellation;
        long started = animationTime.GetTimestamp();
        lock (animationEffectSync)
        {
            animationStartedTimestamp = started;
            spatialAnimation = spatialEffect;
        }
        animationTask = RunAnimationAsync(settings with { LogoMode = null },
            cancellation, started, spatialEffect);
    }

    public Task PulseReactiveKeyAsync(ReactiveKeyPulse pulse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (animationEffectSync)
        {
            if (disposed || !policy.WritesAllowed || model.ProductId != 0x02A0
                || spatialAnimation?.IsKeyTriggered != true
                || OwnershipState is not (LightingRendererOwnershipState.Reactive
                    or LightingRendererOwnershipState.Ripple)
                || !Blade18KeyboardLightingLayout.IsKeySlot(pulse.MatrixSlot))
            {
                throw new CapabilityUnavailableException(
                    "Key-triggered lighting input is not admitted for this slot and renderer.");
            }
            TimeSpan elapsed = animationTime.GetElapsedTime(animationStartedTimestamp);
            spatialAnimation.Pulse(pulse.MatrixSlot, LightingKeyPulseTiming.ResolveElapsed(
                pulse, animationStartedTimestamp, elapsed, animationTime));
        }
        return Task.CompletedTask;
    }

    private async Task RunAnimationAsync(
        LightingSettings settings,
        CancellationTokenSource owner,
        long started,
        Blade18SpatialLightingEffect? spatialEffect)
    {
        CancellationToken token = owner.Token;
        TimeSpan deadline = Blade18LightingAnimation.FrameInterval;
        try
        {
            while (true)
            {
                TimeSpan elapsed = animationTime.GetElapsedTime(started);
                if (deadline > elapsed)
                {
                    await Task.Delay(deadline - elapsed, animationTime, token).ConfigureAwait(false);
                }
                await operationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(animationCancellation, owner)
                        || lightingOwnership.Intent != settings)
                    {
                        return;
                    }
                    try
                    {
                        elapsed = animationTime.GetElapsedTime(started);
                        // Normal replacement never cancels an in-flight native frame.
                        // Admission is checked independently at every row submission.
                        Blade18LightingFrame frame;
                        lock (animationEffectSync)
                        {
                            frame = spatialEffect?.RenderFrame(elapsed)
                                ?? Blade18LightingAnimation.Render(settings, elapsed);
                        }
                        await SubmitBlade18FrameAsync(
                            frame,
                            activate: false,
                            CancellationToken.None).ConfigureAwait(false);
                        policy.RecordAnimationFrameApplied(settings.Effect);
                    }
                    catch (Exception error)
                    {
                        SetLightingFaulted();
                        _ = MapWriteFailure("KeyboardAnimationFrame", false, error);
                        return;
                    }
                }
                finally
                {
                    operationGate.Release();
                }
                elapsed = animationTime.GetElapsedTime(started);
                // Skip missed frames instead of building a queue or catch-up burst.
                long interval = Blade18LightingAnimation.FrameInterval.Ticks;
                deadline = TimeSpan.FromTicks((elapsed.Ticks / interval + 1) * interval);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task SubmitBlade18FrameAsync(
        Blade18LightingFrame frame,
        bool activate,
        CancellationToken token)
    {
        if (!SupportsBlade18Animation(model)
            || session is not IRazerSetFeatureCommandSession submitter)
        {
            throw new CapabilityUnavailableException(
                "This model has no admitted animation stream transport.");
        }
        RequireWriteAdmission();
        var commands = new RazerCommandSpec[Blade18LightingFrame.Rows + (activate ? 1 : 0)];
        bool StillAdmitted() => policy.WritesAllowed && exactHostStillPresent()
            && additionalWriteAdmission?.Invoke() != false;
        for (int row = 0; row < Blade18LightingFrame.Rows; row++)
        {
            commands[row] = new(0x03, 0x0B, frame.EncodeRow(row),
                ExperimentalBladeLightingFrame.RowPayloadLength)
            {
                SubmissionAllowed = StillAdmitted,
            };
        }
        if (activate)
        {
            commands[^1] = new(0x03, 0x0A, new byte[] { 5, 0 }, 2)
            {
                SubmissionAllowed = StillAdmitted,
            };
        }
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            // Product 672 streams SET-only rows, with activation on effect entry.
            // There is no firmware RGB readback or per-frame GET/latch sequence.
            result = await submitter.SubmitBatchAsync(commands, AllocateTransaction,
                TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "The animation frame may have been partially submitted.", error);
        }
        if (result.Failure is null)
        {
            return;
        }
        bool knownNotApplied = result.Diagnostic?.Phase != HidSessionFailurePhase.SetCompletion
            && (result.Diagnostic?.Phase == HidSessionFailurePhase.Guard
            || result.Failure is HidSessionFailureKind.QueuedCanceled
                or HidSessionFailureKind.Exhausted or HidSessionFailureKind.Disposed
                or HidSessionFailureKind.Poisoned);
        throw new BladeWriteException(knownNotApplied
            ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate,
            "The animation frame could not be submitted.")
        {
            Diagnostic = result.Diagnostic,
        };
    }
}
