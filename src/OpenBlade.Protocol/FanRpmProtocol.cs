namespace OpenBlade.Protocol;

internal enum FanRpmSelector : byte
{
    First = 0x01,
    Second = 0x02,
}

internal static class FanRpmProtocol
{
    public static RazerReport CreateQuery(
        byte transactionId,
        FanRpmSelector selector)
    {
        ValidateSelector(selector);
        return RazerReport.Create(
            transactionId,
            0x0D,
            0x88,
            [0x01, (byte)selector, 0x00]);
    }

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        FanRpmSelector selector,
        out int rpm)
    {
        rpm = 0;
        if (!Enum.IsDefined(selector)
            || payload.Length != 3
            || payload[0] != 0x01
            || payload[1] != (byte)selector)
        {
            return false;
        }

        rpm = payload[2] * 100;
        return true;
    }

    private static void ValidateSelector(FanRpmSelector selector)
    {
        if (!Enum.IsDefined(selector))
        {
            throw new ArgumentOutOfRangeException(nameof(selector));
        }
    }
}
