namespace OpenBlade.Protocol;

internal static class TemporaryFullChargeProtocol
{
    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, 0x07, 0x8F, [0x00]);

    public static bool TryDecodeQuery(ReadOnlySpan<byte> payload, out bool active)
    {
        active = false;
        if (payload.Length != 1)
        {
            return false;
        }

        return payload[0] switch
        {
            0x00 => true,
            0x04 => Assign(true, out active),
            _ => false,
        };
    }

    private static bool Assign(bool value, out bool active)
    {
        active = value;
        return true;
    }
}
