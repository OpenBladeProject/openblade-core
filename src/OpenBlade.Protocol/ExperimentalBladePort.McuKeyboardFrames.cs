using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Exact keyboard MCU reviews of RZ09-0301, RZ09-0421, RZ09-0423 and
    // RZ09-0427 (evidence/source-reviewed-blades/2026-11-19-rz09-*-keyboard-frames.json)
    // share one keyboard-only frame contract: 03/0B rows 0..5 [FF,row,0,15,RGB*16]
    // into keyboard staging, volatile 0F/02 [0,5,8] selection confirmed only by
    // 0F/82 mode 8, and restoration through the flag-clearing legacy 03/0A
    // setters these images already use. 0F/03 is never sent: its LED4 rows
    // overflow the logo buffer. RZ09-0509/0510 share the transport, but their
    // LampArray AutonomousMode selects the displayed buffer, so they stay excluded.
    internal static bool SupportsReviewedMcuKeyboardFrames(ExperimentalBladeModel model) =>
        model.ProductId is 0x0245 or 0x028A or 0x028B or 0x028C;

    // Grid patterns need only the reviewed 6x16 bounds. Breathing, Wave and
    // Spectrum keep their exact built-in setters. Reactive, Ripple and
    // sequences need an exact input-to-light map that these models lack.
    internal static bool IsReviewedMcuKeyboardGridEffect(LightingSettings? settings) =>
        ExperimentalGridLightingEffect.IsSupported(settings)
        && settings!.Parameters is FireLightingParameters or WheelLightingParameters
            or StarlightLightingParameters or TidalLightingParameters;

    // Whether this owner has selected custom mode 8. Sampled renderers own
    // the keyboard from entry, but select mode 8 only with their first frame.
    private bool ReviewedMcuCustomFrameSelected() =>
        OwnershipState is not (LightingRendererOwnershipState.AmbientAwareness
            or LightingRendererOwnershipState.AudioMeter)
        || lightingOwnership.UniformFrameActivated != 0;

    // The operation gate, transaction budget or session lifetime refused the
    // read before it reached the MCU. Nothing about ownership was observed.
    private static bool IsUnobservedSelectionReadFailure(Exception error) =>
        error is ObjectDisposedException
        || error is NativeDeviceReadException
        {
            Failure: HidSessionFailureKind.QueuedCanceled or HidSessionFailureKind.Exhausted
                or HidSessionFailureKind.Disposed,
        };

    // A refused read skips one frame. Persistent refusal must not keep an owner
    // that is never re-checked, so stop after this long without a fresh read.
    internal static readonly TimeSpan ReviewedMcuSelectionDeferralLimit = TimeSpan.FromSeconds(5);

    private long? reviewedMcuSelectionDeferredSince;

    private async Task<ReadOnlyMemory<byte>> ReadReviewedMcuSelectionAsync(CancellationToken token)
    {
        try
        {
            ReadOnlyMemory<byte> payload = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
            reviewedMcuSelectionDeferredSince = null;
            return payload;
        }
        catch (Exception error) when (IsUnobservedSelectionReadFailure(error))
        {
            long now = animationTime.GetTimestamp();
            if (reviewedMcuSelectionDeferredSince is not { } since)
            {
                reviewedMcuSelectionDeferredSince = now;
            }
            else if (animationTime.GetElapsedTime(since, now)
                >= ReviewedMcuSelectionDeferralLimit)
            {
                // Nothing was written. Stop like any other refused frame.
                reviewedMcuSelectionDeferredSince = null;
                SetLightingFaulted();
                policy.Record("KeyboardAnimationFrame", "Ownership", "Unavailable",
                    failure: "SelectionReadDeferralLimit");
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The keyboard lighting state could not be read for 5 seconds, so host lighting stopped. Choose a lighting effect to continue.",
                    error);
            }
            // Skip this frame. No row was sent, so ownership and the journal stay.
            policy.Record("KeyboardAnimationFrame", "Ownership", "Deferred",
                failure: error is NativeDeviceReadException read ? read.Failure.ToString() : error.GetType().Name);
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                BladeWriteFailurePhase.LightingSelectionRead,
                "The keyboard lighting state could not be read, so this frame was skipped.", error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            reviewedMcuSelectionDeferredSince = null;
            SetLightingFaulted();
            policy.Record("KeyboardAnimationFrame", "Ownership", "Unavailable",
                failure: "CustomSelectionReadFailed");
            throw;
        }
    }

    // Mode and parameters only. Brightness is a separate field that OpenBlade
    // itself adjusts after entry on these legacy setters.
    private bool ReviewedMcuEffectMatchesBaseline(ReadOnlySpan<byte> payload, ExperimentalLightingBaseline baseline) =>
        DecodeReviewedMcuEffect(payload, baseline.Settings.Brightness) is { } actual
        && KeyboardSettingsMatch(actual, baseline.Settings);

    // A GPIO bit-8 rising edge or an MCU reset copies the saved records over
    // the active ones, so 0F/82 then reports the saved effect. After a reset
    // the custom flag is clear; after the GPIO edge it stays set and staging
    // stays visible, so the reported mode never proves the frame is gone.
    // When the reported effect equals the journaled baseline, the flag-clearing
    // legacy setter can still complete restoration, and a new renderer can
    // take over with that same baseline: neither overwrites another choice.
    private async Task<bool> FaultedReviewedMcuBaselineStillReportedAsync(bool hostFrameRequest,
        CancellationToken token)
    {
        if (!hostFrameRequest || !SupportsReviewedMcuKeyboardFrames(model)
            || OwnershipState != LightingRendererOwnershipState.Faulted
            || lightingOwnership.Journal is null || lightingOwnership.Baseline is not { } baseline)
        {
            return false;
        }
        // Paused or unadmitted writes keep their own refusal and spend no read.
        RequireWriteAdmission();
        try
        {
            ReadOnlyMemory<byte> payload = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
            if (!ReviewedMcuEffectMatchesBaseline(payload.Span, baseline))
            {
                return false;
            }
            policy.Record("KeyboardLighting", "Baseline", "ExternallyRestoredToBaseline",
                requested: baseline.Settings.Effect.ToString(), confirmed: $"Mode{payload.Span[2]}");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Unreadable state keeps the existing fail-closed refusal.
            return false;
        }
    }

    // Any mode other than 8 ends this owner's frames with no further write; a
    // restore could overwrite another controller's choice. A later explicit
    // built-in selection uses 03/0A, which clears the custom flag.
    private async Task RequireReviewedMcuCustomFrameSelectionAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ReadReviewedMcuSelectionAsync(token).ConfigureAwait(false);
        byte mode = payload.Span[2];
        if (mode == 8)
        {
            return;
        }
        bool baselineReported = lightingOwnership.Baseline is { } baseline
            && ReviewedMcuEffectMatchesBaseline(payload.Span, baseline);
        SetLightingFaulted();
        policy.Record("KeyboardAnimationFrame", "Ownership",
            baselineReported ? "ExternallyRestoredToBaseline" : "ExternallyChanged",
            confirmed: $"Mode{mode}", failure: "CustomSelectionLost");
        throw new IOException(
            "The keyboard left custom lighting outside OpenBlade, so host lighting stopped. Choose a lighting effect to continue.");
    }

    // A sampled owner selects mode 8 only with its first frame. Until then the
    // keyboard must still show the journaled baseline effect; anything else is
    // another controller's change, so stop without writing or restoring.
    private async Task RequireReviewedMcuBaselineStillShownAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ReadReviewedMcuSelectionAsync(token).ConfigureAwait(false);
        if (lightingOwnership.Baseline is { } baseline && ReviewedMcuEffectMatchesBaseline(payload.Span, baseline))
        {
            return;
        }
        SetLightingFaulted();
        policy.Record("KeyboardAnimationFrame", "Ownership", "ExternallyChanged",
            confirmed: $"Mode{payload.Span[2]}", failure: "BaselineChangedBeforeFirstFrame");
        throw new IOException(
            "Another app changed the keyboard lighting, so host lighting stopped. Choose a lighting effect to continue.");
    }

    // Ownership is re-read once per frame batch, before any of its rows, so
    // after an external change at most the batch already admitted can land.
    // Batches are at least ExperimentalGridLightingEffect.FrameInterval apart.
    private async Task SubmitReviewedMcuKeyboardFrameAsync(IReadOnlyList<RgbColor> colors, bool activate,
        CancellationToken token, Action onSubmitted)
    {
        byte[][] rows = Enumerable.Range(0, ExperimentalBladeLightingFrame.RowCount)
            .Select(row => ExperimentalBladeLightingFrame.CreateRow(row, colors, padded: false)).ToArray();
        // Admission precedes the ownership read: a refused write spends no
        // device read, and a read never stands in for admission.
        RequireWriteAdmission();
        if (!activate)
        {
            await RequireReviewedMcuCustomFrameSelectionAsync(token).ConfigureAwait(false);
        }
        else if (!ReviewedMcuCustomFrameSelected())
        {
            await RequireReviewedMcuBaselineStillShownAsync(token).ConfigureAwait(false);
        }
        bool submitted = false;
        try
        {
            foreach (byte[] row in rows)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardAnimationFrame", 0x03, 0x0B, row,
                    true, token, variableResponse: true,
                    payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                submitted = true;
                onSubmitted();
            }
            if (activate)
            {
                // [0,5,8] arms LED5 only and persists nothing. [0,0,8] would
                // also arm the LED4 logo, and storage 1 persists the saved bank.
                RequireWriteAdmission();
                // Write-ahead: from here the custom frame may remain displayed.
                lightingOwnership.McuCustomFrameMayRemain = true;
                await ExchangeAsync("KeyboardAnimationFrame", 0x0F, 0x02,
                    [0, 5, 8], true, token, variableResponse: true,
                    payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                onSubmitted();
                ReadOnlyMemory<byte> actual = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
                if (actual.Span[2] != 8)
                {
                    throw new IOException("Independent keyboard readback did not confirm custom-frame selection.");
                }
            }
        }
        catch (Exception error) when (submitted)
        {
            // Rows already reached staging. Retire the frame; never replay it.
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "The grid frame may have been partially applied.", error);
        }
        RecordKeyboardMirrorFrame(colors, ExperimentalBladeLightingFrame.ColumnCount,
            ExperimentalBladeLightingFrame.RowCount);
    }
}
