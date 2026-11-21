using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private byte? blade18KeyboardLayoutId;
    private byte? blade14KeyboardEdition;
    private ExperimentalKeyboardLayoutMetadata? keyboardLayoutMetadata;
    private ExperimentalKeyboardLayoutMetadata.Key keyboardLayoutMetadataKey;

    // The factory binds its identity-scoped result so that later battery, lighting
    // and recovery sessions reuse one read, including a completed failure. A
    // directly constructed port has no binding and always reads.
    internal void BindKeyboardLayoutMetadata(
        ExperimentalKeyboardLayoutMetadata metadata, ExperimentalKeyboardLayoutMetadata.Key key)
    {
        keyboardLayoutMetadata = metadata;
        keyboardLayoutMetadataKey = key;
    }

    private bool TryGetSettledKeyboardLayout(out byte? value)
    {
        value = null;
        return keyboardLayoutMetadata?.TryGetSettled(keyboardLayoutMetadataKey, out value) == true;
    }

    private void SettleKeyboardLayout(byte? value) =>
        keyboardLayoutMetadata?.Settle(keyboardLayoutMetadataKey, value);

    // Only a failure that says something about this device is a result for its identity:
    // firmware that answered with a rejection or an invalid payload, or whose GET
    // poisoned the session, would do so again. A refusal before submission, a
    // shared-gate timeout, a no-response timeout that left the session healthy, and
    // cancellation never reached the device, so the next session reads again.
    private void SettleFailedKeyboardLayout(IOException failure, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested
            && KeyboardLayoutFailureIsDeviceResult(failure, session.Disposition))
        {
            SettleKeyboardLayout(null);
        }
    }

    internal static bool KeyboardLayoutFailureIsDeviceResult(
        IOException failure, RazerCommandSessionDisposition disposition)
    {
        if (failure is not NativeDeviceReadException read)
        {
            // The reply arrived and failed this port's own contract check.
            return true;
        }
        if (read.Diagnostic is { Phase: HidSessionFailurePhase.ProtocolValidation } diagnostic
            && diagnostic.ProtocolValidationFailure is HidProtocolValidationFailure.Status
                or HidProtocolValidationFailure.PayloadLength or HidProtocolValidationFailure.PayloadValidator)
        {
            return true;
        }
        if (read.Diagnostic?.Phase is HidSessionFailurePhase.Guard or HidSessionFailurePhase.SharedGateAdmission
            || read.Failure is HidSessionFailureKind.IdentityBlocked or HidSessionFailureKind.ConflictBlocked
                or HidSessionFailureKind.ConflictDetectionFailed or HidSessionFailureKind.QueuedCanceled
                or HidSessionFailureKind.Canceled or HidSessionFailureKind.Exhausted
                or HidSessionFailureKind.Disposed)
        {
            return false;
        }
        // A submitted GET that retired the session would retire the next one too.
        return disposition != RazerCommandSessionDisposition.Healthy;
    }

    // Exact RZ09-0530 PI4110 1.04.00 image (decoded SHA256 1AC3F054...8F11):
    // evidence/source-reviewed-blades/2026-11-12-pi4110-keymap.json editionGetter.
    // Class 00 command 86 takes two data bytes and returns RAM 0x10001870 at
    // payload 0, the byte selector 0x5D24 uses for the descriptor table. Payload 1
    // stays opaque. This read is optional metadata: any failure keeps the common
    // map. It never sends a setter. The device identity repeats it only until the
    // device settles it, before the first port for that identity is published.
    internal async Task InitializeBlade14KeyboardEditionAsync(CancellationToken cancellationToken)
    {
        if (model.ProductId != 0x02C5)
        {
            return;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            blade14KeyboardEdition = null;
            if (!exactHostStillPresent())
            {
                throw new CapabilityUnavailableException("The exact Blade 14 is no longer verified.");
            }
            if (TryGetSettledKeyboardLayout(out byte? settled))
            {
                blade14KeyboardEdition = settled;
                return;
            }
            ReadOnlyMemory<byte> reply = await ExchangeAsync("KeyboardLayout", 0x00, 0x86,
                [0, 0], false, cancellationToken,
                payloadValidator: payload => payload.Length == 2).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Firmware's selector switch classifies every byte, including its default family.
            byte edition = reply.Span[0];
            blade14KeyboardEdition = edition;
            SettleKeyboardLayout(edition);
            RecordQuery("KeyboardLayout", "Confirmed",
                $"Edition={edition};Map={Blade14KeyboardLightingLayout.GetMapId(edition)}");
        }
        catch (IOException failure)
        {
            SettleFailedKeyboardLayout(failure, cancellationToken);
            if (session.Disposition != RazerCommandSessionDisposition.Healthy)
            {
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Keep unrelated controls and the common map; never guess a regional table.
            RecordQuery("KeyboardLayout", "Unavailable", failure: "RegionalLayoutReadUnavailable");
        }
        finally
        {
            operationGate.Release();
        }
    }

    // Exact product 672: Blade -> LedMatrix -> rzDevice25 getEdition.
    // SDK SHA256 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24:
    // modules 73932/70915 define the 00/86 getter and layout/edition/firmwareId reply.
    // Its separate setter is 00/06, which this implementation never sends.
    // See docs/blade18-spatial-lighting.md for the reviewed inheritance chain.
    internal async Task InitializeBlade18KeyboardLayoutAsync(CancellationToken cancellationToken)
    {
        if (model.ProductId != 0x02A0)
        {
            return;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            blade18KeyboardLayoutId = null;
            if (!exactHostStillPresent())
            {
                throw new CapabilityUnavailableException("The exact Blade 18 is no longer verified.");
            }
            if (TryGetSettledKeyboardLayout(out byte? settled))
            {
                blade18KeyboardLayoutId = settled;
                return;
            }
            ReadOnlyMemory<byte> reply = await ExchangeAsync("KeyboardLayout", 0x00, 0x86,
                [0, 0, 0], false, cancellationToken,
                payloadValidator: payload => payload.Length == 3).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte layout = reply.Span[0];
            if (Blade18KeyboardLightingLayout.IsKnownLayout(layout))
            {
                blade18KeyboardLayoutId = layout;
                SettleKeyboardLayout(layout);
                RecordQuery("KeyboardLayout", "Confirmed", $"Layout={layout}");
            }
            else
            {
                // The same firmware reports the same unknown layout on every read.
                SettleKeyboardLayout(null);
                RecordQuery("KeyboardLayout", "UnsupportedState", failure: "UnknownRegionalLayout");
            }
        }
        catch (IOException failure)
        {
            SettleFailedKeyboardLayout(failure, cancellationToken);
            if (session.Disposition != RazerCommandSessionDisposition.Healthy)
            {
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Do not disable unrelated controls or silently choose the US layout.
            RecordQuery("KeyboardLayout", "Unavailable", failure: "RegionalLayoutReadUnavailable");
        }
        finally
        {
            operationGate.Release();
        }
    }
}
