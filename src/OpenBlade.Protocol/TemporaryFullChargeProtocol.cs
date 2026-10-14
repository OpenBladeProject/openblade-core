namespace OpenBlade.Protocol;

internal static class TemporaryFullChargeProtocol
{
    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, 0x07, 0x8F, [0x00]);

    public static RazerReport CreateSet(byte transactionId, bool enabled) =>
        RazerReport.Create(transactionId, 0x07, 0x0F, [Encode(enabled)]);

    internal static byte Encode(bool enabled) => enabled ? (byte)0x04 : (byte)0x00;

    /// <summary>
    /// Decodes 07/8F. <paramref name="acceptCompletedMarker"/> is set only for the
    /// exact RZ09-0581 (1532:02E0), where firmware also reports 0x08 while Synapse
    /// shows temporary full charge as off (see docs/firmware-validation-rz09-0581-mcu-1.7.md).
    /// Exactly 0x08 decodes as inactive; every other value stays rejected.
    /// </summary>
    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out bool active,
        bool acceptCompletedMarker = false)
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
            0x08 => acceptCompletedMarker,
            _ => false,
        };
    }

    private static bool Assign(bool value, out bool active)
    {
        active = value;
        return true;
    }
}
