using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class FunctionKeyPrimaryProtocol
{
    internal const byte CommandClass = 0x02;
    internal const byte CommandId = 0x86;

    public static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(transactionId, CommandClass, CommandId, [0x01, 0x00]);

    public static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out FunctionKeyPrimaryMode mode)
    {
        mode = default;
        if (payload.Length != 2 || payload[0] != 0x01)
        {
            return false;
        }

        switch (payload[1])
        {
            case 0x00:
                mode = FunctionKeyPrimaryMode.FunctionKeys;
                return true;
            case 0x01:
                mode = FunctionKeyPrimaryMode.MultimediaKeys;
                return true;
            default:
                return false;
        }
    }
}
