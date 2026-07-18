namespace OpenBlade.Protocol;

internal static class LightingBrightnessProtocol
{
    internal const byte OperationClass = 0x03;
    internal const byte OperationId = 0x83;
    internal static ReadOnlySpan<byte> Arguments => [0x01, 0x05, 0x00];

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, OperationClass, OperationId, Arguments);

    public static bool TryDecodeQuery(ReadOnlySpan<byte> payload, out byte brightness)
    {
        brightness = 0;
        if (payload.Length != 3 || payload[0] != 0x01 || payload[1] != 0x05)
        {
            return false;
        }

        brightness = payload[2];
        return true;
    }
}
