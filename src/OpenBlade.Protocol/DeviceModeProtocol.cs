namespace OpenBlade.Protocol;

internal enum RazerDeviceMode : byte
{
    Normal = 0x00,
    Driver = 0x03,
}

internal static class DeviceModeProtocol
{
    internal const byte OperationClass = 0x00;
    internal const byte SetOperationId = 0x04;
    internal const byte QueryOperationId = 0x84;
    internal static ReadOnlySpan<byte> QueryArguments => [0x00, 0x00];

    internal static RazerReport CreateQuery(byte transactionId) =>
        RazerReport.Create(
            transactionId,
            OperationClass,
            QueryOperationId,
            QueryArguments);

    internal static byte[] CreateSetArguments(RazerDeviceMode mode) =>
        mode switch
        {
            RazerDeviceMode.Normal => [0x00, 0x00],
            RazerDeviceMode.Driver => [0x03, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    internal static bool TryDecodeQuery(
        ReadOnlySpan<byte> payload,
        out RazerDeviceMode mode)
    {
        mode = default;
        if (payload.Length != 2 || payload[1] != 0x00)
        {
            return false;
        }

        mode = payload[0] switch
        {
            (byte)RazerDeviceMode.Normal => RazerDeviceMode.Normal,
            (byte)RazerDeviceMode.Driver => RazerDeviceMode.Driver,
            _ => default,
        };
        return payload[0] is
            (byte)RazerDeviceMode.Normal or
            (byte)RazerDeviceMode.Driver;
    }

    internal static bool TryDecodeSet(
        ReadOnlySpan<byte> payload,
        RazerDeviceMode expectedMode) =>
        payload.SequenceEqual(CreateSetArguments(expectedMode));
}
