using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private byte? blade18KeyboardLayoutId;

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
