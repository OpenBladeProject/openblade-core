namespace OpenBlade.Protocol;

internal static class TemporaryFullChargeProtocol
{
    // Exact RZ09-0581 only: 07/8F and 07/0F carry a shared power-mode-control
    // bitmask (see docs/firmware-validation-rz09-0581-mcu-1.7.md).
    internal const byte CpuBoostBit = 0x01;
    internal const byte MaximumFanSpeedBit = 0x02;
    internal const byte TemporaryFullChargeBit = 0x04;
    internal const byte LocalDimmingBit = 0x08;
    internal const byte PowerModeControlDefinedBits =
        CpuBoostBit | MaximumFanSpeedBit | TemporaryFullChargeBit | LocalDimmingBit;

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, 0x07, 0x8F, [0x00]);

    public static RazerReport CreateSet(byte transactionId, bool enabled) =>
        RazerReport.Create(transactionId, 0x07, 0x0F, [Encode(enabled)]);

    internal static byte Encode(bool enabled) => enabled ? TemporaryFullChargeBit : (byte)0x00;

    /// <summary>
    /// Decodes 07/8F as temporary full charge. Without
    /// <paramref name="decodesPowerModeControlBitmask"/> only 0x00 and 0x04 are
    /// accepted. The flag is set only for the exact RZ09-0581 (1532:02E0), where
    /// the byte is a power-mode-control bitmask: any subset of 0x0F is accepted
    /// and only bit 0x04 is temporary full charge.
    /// </summary>
    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out bool active,
        bool decodesPowerModeControlBitmask = false) =>
        TryDecodeQuery(
            payload,
            out active,
            out _,
            decodesPowerModeControlBitmask);

    /// <param name="otherSettings">
    /// The non-temporary-full-charge bits (0x01, 0x02, 0x08). A 07/0F write
    /// replaces the whole byte, so it is admitted only while these are clear.
    /// </param>
    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out bool active,
        out byte otherSettings,
        bool decodesPowerModeControlBitmask)
    {
        active = false;
        otherSettings = 0;
        if (payload.Length != 1)
        {
            return false;
        }

        byte value = payload[0];
        bool accepted = decodesPowerModeControlBitmask
            ? (value & ~PowerModeControlDefinedBits) == 0
            : value is 0x00 or TemporaryFullChargeBit;
        if (!accepted)
        {
            return false;
        }

        active = (value & TemporaryFullChargeBit) != 0;
        otherSettings = (byte)(value & ~TemporaryFullChargeBit);
        return true;
    }

    /// <summary>Plain-English names of the set non-temporary-full-charge bits.</summary>
    internal static IReadOnlyList<string> DescribeOtherSettings(byte otherSettings)
    {
        var names = new List<string>(3);
        if ((otherSettings & LocalDimmingBit) != 0)
        {
            names.Add("local dimming");
        }

        if ((otherSettings & CpuBoostBit) != 0)
        {
            names.Add("CPU boost");
        }

        if ((otherSettings & MaximumFanSpeedBit) != 0)
        {
            names.Add("maximum fan speed");
        }

        return names;
    }
}
