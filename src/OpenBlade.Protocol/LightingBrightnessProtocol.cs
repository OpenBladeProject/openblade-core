namespace OpenBlade.Protocol;

internal static class LightingBrightnessProtocol
{
    internal const byte OperationClass = 0x03;
    internal const byte QueryOperationId = 0x83;
    internal const byte SetOperationId = 0x03;
    internal static ReadOnlySpan<byte> QueryArguments => [0x01, 0x05, 0x00];

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, OperationClass, QueryOperationId, QueryArguments);

    internal static byte PercentToRaw(byte percent)
    {
        if (percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        // Matches the captured Synapse 77% -> 0xC5 conversion. Ceil also keeps
        // every non-zero UI step distinguishable from Off.
        return percent == 0
            ? (byte)0
            : checked((byte)Math.Ceiling(percent * byte.MaxValue / 100d));
    }

    internal static int RawToPercent(byte raw) =>
        (int)Math.Round(raw * 100d / byte.MaxValue);

    internal static byte[] CreateSetArguments(byte raw) => [0x01, 0x05, raw];

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

    internal static bool TryDecodeSet(
        ReadOnlySpan<byte> payload,
        byte expectedRaw) =>
        payload.Length == 3
        && payload[0] == 0x01
        && payload[1] == 0x05
        && payload[2] == expectedRaw;
}
