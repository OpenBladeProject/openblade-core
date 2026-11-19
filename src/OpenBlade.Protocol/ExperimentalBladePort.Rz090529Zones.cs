using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Product 711's selected Blade2 implementation and KI8110 1.07.00 agree:
    // VC 03/00 and 03/80 both access the same inverted global, not saved banks.
    // Touchpad 00/59 and 00/D9 access the same boolean status-indicator global.
    // Decoded image SHA256 c0ebb3b40d3ac763a50d7301457d73f1a84a3e2c951730454d848b13ca3f3578:
    // VC 8A08..8A5C, 8AD8..8ADC; touchpad 79C2..79EA, 788A..788E.
    // Neither setter persists state or changes keyboard effects/sleep policy.
    // Selected source: products/711/mw/rzDevice25LedMatrixBlade2.d678cb5b99e6382aebe0.js
    // SHA256 62356152B59E6D172AF8D07677534EBA0BF9DBFEBA973AFE51BEC8437D031FD8;
    // selected tasks: products/711/mw/6737.c63928b0e4fd1b7673b4.js
    // SHA256 3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88.
    // Both are hosted under https://apps.razer.com/synapse/.
    private bool SupportsRz090529Zones => model.ProductId == 0x02C7 && model.ModelNumber == "RZ09-0529";

    private async Task<bool?> ReadOptionalRz090529ZoneAsync(bool touchpadIndicator, CancellationToken token)
    {
        try
        {
            return await ReadRz090529ZoneAsync(touchpadIndicator, token).ConfigureAwait(false);
        }
        catch (IOException exception) when (session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            RecordQuery(touchpadIndicator ? "TouchpadStatusIndicator" : "VaporChamberLighting",
                "Unavailable", failure: exception.GetType().Name);
            return null;
        }
    }

    private async Task<bool> ReadRz090529ZoneAsync(bool touchpadIndicator, CancellationToken token)
    {
        byte[] arguments = touchpadIndicator ? [0, 1, 0] : [0, 38, 0];
        ReadOnlyMemory<byte> response = await ExchangeAsync(
            touchpadIndicator ? "TouchpadStatusIndicator" : "VaporChamberLighting",
            touchpadIndicator ? (byte)0 : (byte)3,
            touchpadIndicator ? (byte)0xD9 : (byte)0x80, arguments, false, token,
            payloadValidator: value => value.Length == 3 && value.Span[0] == 0
                && value.Span[1] == arguments[1] && value.Span[2] <= 1,
            requireEcho: false).ConfigureAwait(false);
        if (response.Length != 3 || response.Span[0] != 0 || response.Span[1] != arguments[1] || response.Span[2] > 1)
        {
            throw new IOException("Independent lighting indicator returned an invalid boolean state.");
        }
        return response.Span[2] == 1;
    }

    private async Task SetRz090529ZoneAsync(bool touchpadIndicator, bool enabled, Action submitted, CancellationToken token)
    {
        // A confirmed baseline is required even for an absolute setting. Never
        // invent restoration after ambiguous submission: the shared gate pauses.
        bool baseline = await ReadRz090529ZoneAsync(touchpadIndicator, token).ConfigureAwait(false);
        if (baseline == enabled)
        {
            return;
        }
        RequireWriteAdmission();
        byte[] arguments = touchpadIndicator ? [0, 1, enabled ? (byte)1 : (byte)0]
            : [1, 38, enabled ? (byte)1 : (byte)0];
        await ExchangeAsync(touchpadIndicator ? "TouchpadStatusIndicator" : "VaporChamberLighting",
            touchpadIndicator ? (byte)0 : (byte)3, touchpadIndicator ? (byte)0x59 : (byte)0,
            arguments, true, token, variableResponse: true,
            payloadValidator: value => value.Length <= 3
                && value.Span.SequenceEqual(arguments.AsSpan(0, value.Length)), requireEcho: false).ConfigureAwait(false);
        submitted();
        if (await ReadRz090529ZoneAsync(touchpadIndicator, token).ConfigureAwait(false) != enabled)
        {
            throw new IOException("Independent lighting indicator readback did not confirm the requested state.");
        }
    }
}
