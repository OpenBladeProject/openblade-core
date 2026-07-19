namespace OpenBlade.Protocol;

internal static class StartupAnimationProtocol
{
    internal const byte CommandClass = 0x0F;
    internal const byte CommandId = 0x98;

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, CommandClass, CommandId, [0x00]);

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out bool enabled)
    {
        enabled = false;
        if (payload.Length != 1)
        {
            return false;
        }

        switch (payload[0])
        {
            case 0x00:
                enabled = true;
                return true;
            case 0x01:
                enabled = false;
                return true;
            default:
                return false;
        }
    }
}
