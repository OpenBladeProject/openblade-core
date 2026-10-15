namespace OpenBlade.Protocol;

internal static class TemporaryFullChargeProtocol
{
    // 07/8F and 07/0F carry Synapse's Power Mode Control bitmask on every model
    // that uses them. Read-only inspection of Synapse 4 found this reply decoder
    // in its shared Blade device code, not in model-specific code, and Synapse
    // writes 07/0F read-modify-write. See docs/developer-guide.md
    // ("Power Mode Control and local dimming").
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
    /// Decodes 07/8F as temporary full charge. The byte is a Power Mode Control
    /// bitmask: any one-byte subset of 0x0F is accepted and only bit 0x04 is
    /// temporary full charge. Undefined bits or another length are rejected.
    /// </summary>
    public static bool TryDecodeQuery(ReadOnlySpan<byte> payload, out bool active) =>
        TryDecodeQuery(payload, out active, out _);

    /// <param name="otherSettings">
    /// The non-temporary-full-charge bits (0x01, 0x02, 0x08). A 07/0F write
    /// replaces the whole byte, so it is admitted only while these are clear.
    /// </param>
    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out bool active,
        out byte otherSettings)
    {
        active = false;
        otherSettings = 0;
        if (payload.Length != 1)
        {
            return false;
        }

        byte value = payload[0];
        if ((value & ~PowerModeControlDefinedBits) != 0)
        {
            return false;
        }

        active = (value & TemporaryFullChargeBit) != 0;
        otherSettings = (byte)(value & ~TemporaryFullChargeBit);
        return true;
    }

    /// <summary>
    /// The refusal shown when a 07/0F write would also replace other Power Mode
    /// Control settings that are on.
    /// </summary>
    internal static string OtherSettingsBlockMessage(byte otherSettings) =>
        "OpenBlade won't change temporary full charge because it would also change "
        + "other laptop settings that are on ("
        + string.Join(", ", DescribeOtherSettings(otherSettings))
        + ").";

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
