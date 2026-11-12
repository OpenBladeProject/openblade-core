using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // OpenRazer 477f4d6d independently declares each exact model's 6x16 matrix
    // and standard custom-row dispatch. No physical-key mapping is inferred.
    // See docs/blade14-lighting-parity.md for the pinned encoders and scope.
    private static bool SupportsExperimentalGridAnimation(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5;

    // The new Blade 14 contracts declare the full 70-byte standard row payload.
    // Keep 029F/02A0's separately established 52-byte uniform-row contracts.
    private static bool UsesPaddedExperimentalLightingRows(ExperimentalBladeModel model) =>
        model.ProductId is 0x029D or 0x02C5;

    private static byte? ExperimentalLightingFrameTransactionId(ExperimentalBladeModel model) =>
        UsesPaddedExperimentalLightingRows(model) ? (byte)0xFF : null;

    private async Task SubmitExperimentalGridFrameAsync(
        IReadOnlyList<RgbColor> colors,
        bool activate,
        CancellationToken token,
        Action onSubmitted)
    {
        if (!SupportsExperimentalGridAnimation(model))
        {
            throw new CapabilityUnavailableException("Grid lighting is unavailable for this model.");
        }
        // Serialize acknowledged row exchanges. These exact models do not inherit
        // Product 672's SET-only batch transport. New Blade 14 production frames
        // retain the source FF transaction; 029F retains its existing allocator.
        // Developer runs always consume their finite, unique transaction IDs.
        bool submitted = false;
        try
        {
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
