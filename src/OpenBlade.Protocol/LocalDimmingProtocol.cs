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
/// getSKUHardwareConfiguration (0D/8F, the same getter as
/// <see cref="Rz090581CapabilityDiagnosticsProtocol"/>) reports the panel with
/// the module 83379 mask {DDS:1, Mini_LED_Resolution:2, Illegal_Battery_Support:4}.
/// See docs/developer-guide.md ("Power Mode Control and local dimming").
/// </summary>
internal static class LocalDimmingProtocol
{
    internal const byte SkuHardwareConfigurationCommandClass = Rz090581CapabilityDiagnosticsProtocol.CommandClass;
    internal const byte SkuHardwareConfigurationCommandId =
        Rz090581CapabilityDiagnosticsProtocol.SkuHardwareConfigurationCommandId;
    internal const byte MiniLedPanelBit = 0x02;

    // DDS 0x01, Mini_LED_Resolution 0x02 and Illegal_Battery_Support 0x04.
    internal const byte SkuHardwareConfigurationDefinedBits = 0x07;

    private const byte ThermalPowerModeBits =
        TemporaryFullChargeProtocol.CpuBoostBit | TemporaryFullChargeProtocol.MaximumFanSpeedBit;

    /// <summary>
    /// Decodes the one-byte SKU hardware configuration reply. A byte with any
    /// bit outside the reviewed mask is rejected rather than read as a panel.
    /// </summary>
    public static bool TryDecodeMiniLedPanel(ReadOnlySpan<byte> payload, out bool miniLedPanel)
    {
        miniLedPanel = false;
        if (!Rz090581CapabilityDiagnosticsProtocol.TryDecodeRawByte(payload, out byte value)
            || (value & ~SkuHardwareConfigurationDefinedBits) != 0)
        {
            return false;
        }

        miniLedPanel = (value & MiniLedPanelBit) != 0;
        return true;
    }

    /// <summary>
    /// Plans Clear from a fresh 07/8F mask. The write replaces the whole byte,
    /// so it keeps only temporary full charge (0x04) and refuses while the
    /// firmware CPU boost flag (0x01) or Maximum Fans (0x02) is on. OpenBlade
    /// never writes 0x01, and its own Maximum Fans write refuses while local
    /// dimming is on, so both must be turned off in Synapse. Undefined bits
    /// are never planned.
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
            return new(LocalDimmingClearKind.Blocked, 0, DescribeThermalRefusal(otherSettings));
        }

        return new(LocalDimmingClearKind.Clear,
            (byte)(powerModeControl & TemporaryFullChargeProtocol.TemporaryFullChargeBit), null);
    }

    // Bit 0x01 is a firmware flag OpenBlade never writes; it is not the Custom
    // CPU Boost level OpenBlade offers. Both bits must be cleared in Synapse.
    private static string DescribeThermalRefusal(byte otherSettings) =>
        (otherSettings & ThermalPowerModeBits) switch
        {
            TemporaryFullChargeProtocol.MaximumFanSpeedBit =>
                "OpenBlade can’t turn off local dimming while Maximum Fans is on. Turn off Maximum Fans in Synapse, then try again.",
            TemporaryFullChargeProtocol.CpuBoostBit =>
                "OpenBlade can’t turn off local dimming while Synapse’s CPU boost is on. Turn it off in Synapse, then try again.",
            _ =>
                "OpenBlade can’t turn off local dimming while Maximum Fans and Synapse’s CPU boost are on. Turn them off in Synapse, then try again.",
        };
}
