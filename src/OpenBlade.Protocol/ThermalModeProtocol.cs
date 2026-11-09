namespace OpenBlade.Protocol;

internal enum ThermalChannel : byte
{
    First = 0x01,
    Second = 0x02,
}

internal enum ThermalModeState
{
    BalancedAutomatic,
    SilentAutomatic,
    PerformanceAutomatic,

    // This is a decoded firmware state, not production-write authorization.
    // Publishing the confirmed fan state does not admit a corresponding setter.
    FixedMaximum,

    // These battery-profile states are read-only until their setters complete
    // the production admission process.
    BatterySaverAutomatic,
    BatteryBalancedAutomatic,
    CustomAutomatic,
    HyperBoostObserved,

    // Synapse preserves the selected battery profile when manual fans are
    // enabled. Keep these distinct so readback retains the active selector.
    BatterySaverManual,
    BatteryBalancedManual,
    SilentManual,
    PerformanceManual,
    CustomManual,
}

internal readonly record struct ThermalModeReading(
    ThermalChannel Channel,
    ThermalModeState State);

internal static class ThermalModeProtocol
{
    internal const byte CommandClass = 0x0D;
    internal const byte SetCommandId = 0x02;
    internal const byte QueryCommandId = 0x82;
    private const byte ThermalClass = 0x01;

    public static byte[] CreateQueryPayload(ThermalChannel channel)
    {
        ValidateChannel(channel);
        return [ThermalClass, (byte)channel, 0x00, 0x00];
    }

    public static RazerReport CreateQuery(
        byte transactionId,
        ThermalChannel channel)
    {
        ValidateChannel(channel);
        return RazerReport.Create(
            transactionId,
            CommandClass,
            QueryCommandId,
            CreateQueryPayload(channel));
    }

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        ThermalChannel expectedChannel,
        out ThermalModeReading reading,
        bool decodePresetManualStates = false)
    {
        reading = default;
        if (!Enum.IsDefined(expectedChannel)
            || payload.Length != 4
            || payload[0] != ThermalClass
            || payload[1] != (byte)expectedChannel
            || !TryDecodeState(payload[2], payload[3], decodePresetManualStates, out ThermalModeState state))
        {
            return false;
        }

        reading = new ThermalModeReading(expectedChannel, state);
        return true;
    }

    public static bool TryDecodeMatchingPair(
        ReadOnlySpan<byte> firstPayload,
        ReadOnlySpan<byte> secondPayload,
        out ThermalModeState state,
        bool decodePresetManualStates = false)
    {
        state = default;
        if (!TryDecodeQuery(firstPayload, ThermalChannel.First, out ThermalModeReading first, decodePresetManualStates)
            || !TryDecodeQuery(secondPayload, ThermalChannel.Second, out ThermalModeReading second, decodePresetManualStates)
            || first.State != second.State)
        {
            return false;
        }

        state = first.State;
        return true;
    }

    private static bool TryDecodeState(
        byte mode,
        byte fanValue,
        bool decodePresetManualStates,
        out ThermalModeState state)
    {
        state = default;
        return (mode, fanValue) switch
        {
            (0x00, 0x00) => Assign(ThermalModeState.BalancedAutomatic, out state),
            (0x05, 0x00) => Assign(ThermalModeState.SilentAutomatic, out state),
            (0x05, 0x01) => Assign(ThermalModeState.SilentManual, out state),
            (0x02, 0x00) => Assign(ThermalModeState.PerformanceAutomatic, out state),
            (0x04, 0x00) => Assign(ThermalModeState.CustomAutomatic, out state),
            (0x03, 0x00) => Assign(ThermalModeState.BatterySaverAutomatic, out state),
            (0x06, 0x00) => Assign(ThermalModeState.BatteryBalancedAutomatic, out state),
            (0x03, 0x01) => Assign(ThermalModeState.BatterySaverManual, out state),
            (0x06, 0x01) => Assign(ThermalModeState.BatteryBalancedManual, out state),
            (0x07, 0x00) => Assign(ThermalModeState.HyperBoostObserved, out state),
            // Only the RZ09-0581 and RZ09-0528 production paths distinguish Performance
            // and Custom manual selectors. Legacy validators retain their vocabulary.
            (0x02, 0x01) when decodePresetManualStates => Assign(ThermalModeState.PerformanceManual, out state),
            (0x04, 0x01) when decodePresetManualStates => Assign(ThermalModeState.CustomManual, out state),
            (0x00, 0x01) => Assign(ThermalModeState.FixedMaximum, out state),
            (0x04, 0x01) => Assign(ThermalModeState.FixedMaximum, out state),
            _ => false,
        };
    }

    private static bool Assign(
        ThermalModeState value,
        out ThermalModeState state)
    {
        state = value;
        return true;
    }

    private static void ValidateChannel(ThermalChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}
