using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class AdapterPowerProtocol
{
    internal const byte CommandClass = 0x07;
    internal const byte CommandId = 0x8C;

    // Synapse's shared 07/8C decoder defines both bytes as wattage levels.
    // See docs/synapse-adapter-wattage.md for pinned vendor sources. 0x10 is
    // intentionally absent; wattage order is not the numeric code order.
    internal static int? GetWatts(byte level) => level switch
    {
        0x00 => 0,
        0x01 => 40,
        0x02 => 45,
        0x03 => 60,
        0x04 => 65,
        0x05 => 80,
        0x06 => 90,
        0x07 => 100,
        0x08 => 120,
        0x09 => 130,
        0x0A => 150,
        0x0B => 180,
        0x0C => 200,
        0x0D => 230,
        0x0E => 250,
        0x0F => 310,
        0x11 => 280,
        0x12 => 330,
        0x13 => 400,
        _ => null,
    };

    internal static IReadOnlySet<AdapterPowerState> SynapseAdapterStates { get; } =
        new HashSet<AdapterPowerState>
        {
            AdapterPowerState.Battery,
            AdapterPowerState.UsbCPower45W,
            AdapterPowerState.UsbCPower60W,
            AdapterPowerState.UsbCPower65W,
            AdapterPowerState.UsbCPower80W,
            AdapterPowerState.UsbCPower90W,
            AdapterPowerState.UsbCPower100W,
            AdapterPowerState.FullPower280W,
            AdapterPowerState.FullPower330W,
            AdapterPowerState.SufficientExternal,
            AdapterPowerState.LimitedExternal,
        };

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, CommandClass, CommandId, [0x00, 0x00]);

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out AdapterPowerState state)
    {
        state = AdapterPowerState.Unknown;
        if (payload.Length != 2)
        {
            return false;
        }

        int? actualWatts = GetWatts(payload[0]);
        int? recommendedWatts = GetWatts(payload[1]);
        if (actualWatts is null || recommendedWatts is not > 0)
        {
            return true;
        }

        state = actualWatts.Value switch
        {
            0 => AdapterPowerState.Battery,
            // Preserve existing enum identities for callers; their historical
            // USB-C names describe low power, not evidence of a connector.
            45 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower45W,
            60 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower60W,
            65 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower65W,
            80 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower80W,
            90 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower90W,
            100 when actualWatts < recommendedWatts => AdapterPowerState.UsbCPower100W,
            _ when actualWatts < recommendedWatts => AdapterPowerState.LimitedExternal,
            280 => AdapterPowerState.FullPower280W,
            330 => AdapterPowerState.FullPower330W,
            _ => AdapterPowerState.SufficientExternal,
        };
        return true;
    }
}
