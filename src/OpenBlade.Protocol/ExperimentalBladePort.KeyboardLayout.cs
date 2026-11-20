using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private byte? blade18KeyboardLayoutId;
    private byte? blade14KeyboardEdition;

    // Exact RZ09-0530 PI4110 1.04.00 image (decoded SHA256 1AC3F054...8F11):
    // evidence/source-reviewed-blades/2026-11-12-pi4110-keymap.json editionGetter.
    // Class 00 command 86 takes two data bytes and returns RAM 0x10001870 at
    // payload 0, the byte selector 0x5D24 uses for the descriptor table. Payload 1
    // stays opaque. This read is optional metadata: any failure keeps the common
    // map. It never sends a setter, and it runs once before the port is published.
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
            ReadOnlyMemory<byte> reply = await ExchangeAsync("KeyboardLayout", 0x00, 0x86,
                [0, 0], false, cancellationToken,
                payloadValidator: payload => payload.Length == 2).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Firmware's selector switch classifies every byte, including its default family.
            byte edition = reply.Span[0];
            blade14KeyboardEdition = edition;
            RecordQuery("KeyboardLayout", "Confirmed",
                $"Edition={edition};Map={Blade14KeyboardLightingLayout.GetMapId(edition)}");
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
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
            ReadOnlyMemory<byte> reply = await ExchangeAsync("KeyboardLayout", 0x00, 0x86,
                [0, 0, 0], false, cancellationToken,
                payloadValidator: payload => payload.Length == 3).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte layout = reply.Span[0];
            if (Blade18KeyboardLightingLayout.IsKnownLayout(layout))
            {
                blade18KeyboardLayoutId = layout;
                RecordQuery("KeyboardLayout", "Confirmed", $"Layout={layout}");
            }
            else
            {
                RecordQuery("KeyboardLayout", "UnsupportedState", failure: "UnknownRegionalLayout");
            }
        }
        catch (IOException) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
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
