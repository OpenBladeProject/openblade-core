namespace OpenBlade.Protocol;

internal static class GamingModeProtocol
{
    internal const byte CommandClass = 0x00;
    internal const byte CommandId = 0x88;

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(
            transactionId,
            CommandClass,
            CommandId,
            [0x00, 0x00, 0x00, 0x00]);

    public static bool TryDecodeQuery(ReadOnlySpan<byte> payload, out bool enabled)
    {
        enabled = false;
        if (payload.Length != 4
            || payload[1] != 0x00
            || payload[2] != 0x00
            || payload[3] != 0x00)
        {
            return false;
        }

        switch (payload[0])
        {
            case 0x00:
                enabled = false;
                return true;
            case 0x01:
                enabled = true;
                return true;
            default:
                return false;
        }
    }
}
