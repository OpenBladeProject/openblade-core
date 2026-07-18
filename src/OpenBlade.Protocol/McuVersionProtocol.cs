using System.Globalization;

namespace OpenBlade.Protocol;

internal static class McuVersionProtocol
{
    internal const byte CommandClass = 0x00;
    internal const byte CommandId = 0x87;
    internal const int PayloadLength = 4;

    private static readonly byte[] QueryPayload = [0x00, 0x00, 0x00, 0x00];

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, CommandClass, CommandId, QueryPayload);

    public static bool TryDecodeQuery(ReadOnlySpan<byte> payload, out string version)
    {
        if (payload.Length != PayloadLength)
        {
            version = string.Empty;
            return false;
        }

        version = string.Create(
            CultureInfo.InvariantCulture,
            $"{payload[0]}.{payload[1]}.{payload[2]}.{payload[3]}");
        return true;
    }
}
