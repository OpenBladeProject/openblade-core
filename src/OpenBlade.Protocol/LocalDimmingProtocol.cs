namespace OpenBlade.Protocol;

internal enum LocalDimmingClearKind
{
    AlreadyClear,
    Blocked,
    Clear,
}

internal readonly record struct LocalDimmingClearPlan(
    LocalDimmingClearKind Kind,
    byte Value,
    string? Refusal);

/// <summary>
/// Shared local dimming Clear rules. Local dimming is bit 0x08 of the shared
/// Power Mode Control mask (07/8F read, 07/0F whole-byte write) decoded by
/// <see cref="TemporaryFullChargeProtocol"/>. Synapse's rzDevice25
/// getSKUHardwareConfiguration (0D/8F) reports the panel: DDS 0x01,
/// Mini_LED_Resolution 0x02 (Mini_LED_Supported) and Illegal_Battery 0x04.
/// See docs/developer-guide.md ("Power Mode Control and local dimming").
/// </summary>
internal static class LocalDimmingProtocol
{
    internal const byte SkuHardwareConfigurationCommandClass = 0x0D;
    internal const byte SkuHardwareConfigurationCommandId = 0x8F;
    internal const byte MiniLedPanelBit = 0x02;

    private const byte ThermalPowerModeBits =
        TemporaryFullChargeProtocol.CpuBoostBit | TemporaryFullChargeProtocol.MaximumFanSpeedBit;

    /// <summary>Decodes the one-byte SKU hardware configuration reply.</summary>
    public static bool TryDecodeMiniLedPanel(ReadOnlySpan<byte> payload, out bool miniLedPanel)
    {
        miniLedPanel = false;
        if (payload.Length != 1)
        {
            return false;
        }

        miniLedPanel = (payload[0] & MiniLedPanelBit) != 0;
        return true;
    }

    /// <summary>
    /// Plans Clear from a fresh 07/8F mask. The write replaces the whole byte,
    /// so it keeps only temporary full charge (0x04) and refuses while CPU boost
    /// (0x01) or Maximum Fans (0x02) is on. Undefined bits are never planned.
    /// </summary>
    public static LocalDimmingClearPlan PlanClear(byte powerModeControl)
    {
        if ((powerModeControl & ~TemporaryFullChargeProtocol.PowerModeControlDefinedBits) != 0)
        {
            return new(LocalDimmingClearKind.Blocked, 0,
                "OpenBlade won't change local dimming because the laptop reported unknown display settings.");
        }

        if ((powerModeControl & TemporaryFullChargeProtocol.LocalDimmingBit) == 0)
        {
            return new(LocalDimmingClearKind.AlreadyClear, powerModeControl, null);
        }

        byte otherSettings = (byte)(powerModeControl & ThermalPowerModeBits);
        if (otherSettings != 0)
        {
            return new(LocalDimmingClearKind.Blocked, 0,
                "OpenBlade won't turn off local dimming because it would also change other laptop settings that are on ("
                + string.Join(", ", TemporaryFullChargeProtocol.DescribeOtherSettings(otherSettings))
                + "). Turn them off first.");
        }

        return new(LocalDimmingClearKind.Clear,
            (byte)(powerModeControl & TemporaryFullChargeProtocol.TemporaryFullChargeBit), null);
    }
}
