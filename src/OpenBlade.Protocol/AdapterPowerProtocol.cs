using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class AdapterPowerProtocol
{
    internal const byte CommandClass = 0x07;
    internal const byte CommandId = 0x8C;

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

        state = (payload[0], payload[1]) switch
        {
            (0x02, 0x11) => AdapterPowerState.UsbCPower45W,
            (0x03, 0x11) => AdapterPowerState.UsbCPower60W,
            (0x04, 0x11) => AdapterPowerState.UsbCPower65W,
            (0x05, 0x11) => AdapterPowerState.UsbCPower80W,
            (0x06, 0x11) => AdapterPowerState.UsbCPower90W,
            (0x07, 0x11) => AdapterPowerState.UsbCPower100W,
            (0x11, 0x11) => AdapterPowerState.FullPower280W,
            (0x12, 0x11) => AdapterPowerState.FullPower330W,
            _ => AdapterPowerState.Unknown,
        };
        return true;
    }
}
