using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // OpenRazer 477f4d6d independently declares each exact model's 6x16 matrix
    // and standard custom-row dispatch. No physical-key mapping is inferred.
    // See docs/blade14-lighting-parity.md for the pinned encoders and scope.
    private static bool SupportsExperimentalGridAnimation(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x02C7;

    // The new Blade 14 contracts declare the full 70-byte standard row payload.
    // Keep 029F/02A0's separately established 52-byte uniform-row contracts.
    private static bool UsesPaddedExperimentalLightingRows(ExperimentalBladeModel model) =>
        model.ProductId is 0x029D or 0x02C5;

    private static byte? ExperimentalLightingFrameTransactionId(ExperimentalBladeModel model) =>
        UsesPaddedExperimentalLightingRows(model) ? (byte)0xFF : null;

    // Last LED38 row submitted with the keyboard rows; null for keyboard-only frames.
    private IReadOnlyList<RgbColor>? vaporChamberMirrorFrame;

    private async Task SubmitExperimentalGridFrameAsync(
        IReadOnlyList<RgbColor> colors,
        IReadOnlyList<RgbColor>? vaporChamber,
        bool activate,
        CancellationToken token,
        Action onSubmitted)
    {
        if (!SupportsExperimentalGridAnimation(model)
            || vaporChamber is not null && !SupportsRz090529VaporChamberFrames(model))
        {
            throw new CapabilityUnavailableException("Grid lighting is unavailable for this model.");
        }
        byte[]? vaporChamberRow = vaporChamber is null
            ? null : Rz090529KeyboardLightingFrame.EncodeVaporChamberRow(vaporChamber);
        // Serialize acknowledged row exchanges. These exact models do not inherit
        // Product 672's SET-only batch transport. New Blade 14 production frames
        // retain the source FF transaction; 029F retains its existing allocator.
        // Developer runs always consume their finite, unique transaction IDs.
        bool submitted = false;
        try
        {
            if (model.ProductId == 0x02C7)
            {
                for (int row = 0; row < Rz090529KeyboardLightingFrame.Rows; row++)
                {
                    RequireWriteAdmission();
                    await ExchangeAsync("KeyboardAnimationFrame", 0x03, 0x0B,
                        Rz090529KeyboardLightingFrame.EncodeRow(row, colors),
                        true, token, variableResponse: true,
                        payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                        requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                    submitted = true;
                    onSubmitted();
                }
                if (vaporChamberRow is not null)
                {
                    // KI8110 copies transport row 6 into LED38's separate buffer.
                    RequireWriteAdmission();
                    await ExchangeAsync("KeyboardAnimationFrame", 0x03, 0x0B, vaporChamberRow,
                        true, token, variableResponse: true,
                        payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                        requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                    submitted = true;
                    onSubmitted();
                }
                if (activate)
                {
                    RequireWriteAdmission();
                    // Legacy 03/0A [5,0] activates BOTH LED5 and LED38. The
                    // separately reviewed 0F setter selects only active LED5.
                    await ExchangeAsync("KeyboardAnimationFrame", 0x0F, 0x02,
                        [0, 5, 8], true, token, variableResponse: true,
                        payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                        requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                    submitted = true;
                    onSubmitted();
                    ReadOnlyMemory<byte> actual = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
                    if (actual.Span[2] != 8)
                    {
                        throw new IOException("Independent keyboard readback did not confirm custom-frame selection.");
                    }
                    if (vaporChamberRow is not null)
                    {
                        // [0,0,8] would also switch the LED4 logo; select LED38 alone.
                        RequireWriteAdmission();
                        RecordRz090529VaporChamberSelection();
                        await ExchangeAsync("KeyboardAnimationFrame", 0x0F, 0x02,
                            [0, VaporChamberRegion, 8], true, token, variableResponse: true,
                            payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                            requireEcho: false, recordAcknowledgedSubmission: false).ConfigureAwait(false);
                        onSubmitted();
                        if ((await ReadReviewedVaporChamberEffectAsync(token).ConfigureAwait(false)).Mode != 8)
                        {
                            throw new IOException("Independent Vapor Chamber readback did not confirm custom-frame selection.");
                        }
                    }
                }
                vaporChamberMirrorFrame = vaporChamber;
                RecordKeyboardMirrorFrame(colors, Rz090529KeyboardLightingFrame.Columns,
                    Rz090529KeyboardLightingFrame.Rows);
                return;
            }
            for (int row = 0; row < ExperimentalBladeLightingFrame.RowCount; row++)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardAnimationFrame", 0x03, 0x0B,
                    ExperimentalBladeLightingFrame.CreateRow(row, colors,
                        UsesPaddedExperimentalLightingRows(model)),
                    true, token, variableResponse: true,
                    payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false, recordAcknowledgedSubmission: false,
                    fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
                submitted = true;
                onSubmitted();
            }
            if (activate)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardAnimationFrame", 0x03, 0x0A,
                    [5, 0], true, token, variableResponse: true,
                    payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false, recordAcknowledgedSubmission: false,
                    fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
                submitted = true;
                onSubmitted();
            }
        }
        catch (Exception error) when (submitted)
        {
            // Even a later guard refusal cannot erase rows already submitted.
            // Retire partial frames instead of deferring or replaying them.
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "The grid frame may have been partially applied.", error);
        }
        RecordKeyboardMirrorFrame(colors, ExperimentalBladeLightingFrame.ColumnCount,
            ExperimentalBladeLightingFrame.RowCount);
    }
}
