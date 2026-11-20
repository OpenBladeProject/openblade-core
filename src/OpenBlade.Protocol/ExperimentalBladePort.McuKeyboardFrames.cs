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

    // A GPIO bit-8 rising edge copies the saved records over the active ones
    // without clearing the custom flag: 0F/82 then reports the saved mode
    // while the old frame stays visible. Another controller can also select an
    // effect. Any mode other than 8 therefore ends this owner's frames with no
    // further write; a restore could overwrite another controller's choice.
    // A later explicit built-in selection uses 03/0A, which clears the flag.
    private async Task RequireReviewedMcuCustomFrameSelectionAsync(CancellationToken token)
    {
        byte mode;
        try
        {
            mode = (await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false)).Span[2];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetLightingFaulted();
            policy.Record("KeyboardAnimationFrame", "Ownership", "Unavailable",
                failure: "CustomSelectionReadFailed");
            throw;
        }
        if (mode != 8)
        {
            SetLightingFaulted();
            policy.Record("KeyboardAnimationFrame", "Ownership", "ExternallyChanged",
                confirmed: $"Mode{mode}", failure: "CustomSelectionLost");
            throw new IOException(
                "The keyboard left custom lighting outside OpenBlade, so host lighting stopped. Choose a lighting effect to continue.");
        }
    }

    // Ownership is re-read once per frame batch, before any of its rows, so
    // after an external change at most the batch already admitted can land.
    // Batches are at least ExperimentalGridLightingEffect.FrameInterval apart.
    private async Task SubmitReviewedMcuKeyboardFrameAsync(IReadOnlyList<RgbColor> colors, bool activate,
        CancellationToken token, Action onSubmitted)
    {
        byte[][] rows = Enumerable.Range(0, ExperimentalBladeLightingFrame.RowCount)
            .Select(row => ExperimentalBladeLightingFrame.CreateRow(row, colors, padded: false)).ToArray();
        if (!activate)
        {
            await RequireReviewedMcuCustomFrameSelectionAsync(token).ConfigureAwait(false);
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
