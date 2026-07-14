namespace OpenSynapse.Protocol;

public enum RazerCommandStatus : byte
{
    New = 0x00,
    Successful = 0x02,
    NotSupported = 0x05,
}

public sealed class RazerProtocolException : Exception
{
    public RazerProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class RazerReport
{
    public const int ReportLength = 90;
    public const int MaximumPayloadLength = 80;

    private readonly byte[] _payload;

    private RazerReport(byte transactionId, byte commandClass, byte commandId, byte[] payload)
    {
        TransactionId = transactionId;
        CommandClass = commandClass;
        CommandId = commandId;
        _payload = payload;
    }

    public byte TransactionId { get; }

    public byte CommandClass { get; }

    public byte CommandId { get; }

    public static RazerReport Create(
        byte transactionId,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Razer payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        return new RazerReport(transactionId, commandClass, commandId, payload.ToArray());
    }

    public byte[] Encode()
    {
        byte[] bytes = new byte[ReportLength];
        bytes[0] = (byte)RazerCommandStatus.New;
        bytes[1] = TransactionId;
        bytes[5] = checked((byte)_payload.Length);
        bytes[6] = CommandClass;
        bytes[7] = CommandId;
        _payload.CopyTo(bytes, 8);
        bytes[88] = CalculateChecksum(bytes);
        return bytes;
    }

    public ReadOnlyMemory<byte> ValidateResponse(
        ReadOnlySpan<byte> response,
        int? expectedPayloadLength = null)
    {
        if (response.Length != ReportLength)
        {
            throw new RazerProtocolException($"Expected a {ReportLength}-byte response.");
        }

        if (response[88] != CalculateChecksum(response))
        {
            throw new RazerProtocolException("Response checksum is invalid.");
        }

        if (response[1] != TransactionId)
        {
            throw new RazerProtocolException("Response transaction does not match the request.");
        }

        if (response[6] != CommandClass || response[7] != CommandId)
        {
            throw new RazerProtocolException("Response command does not match the request.");
        }

        RazerCommandStatus status = (RazerCommandStatus)response[0];
        if (status != RazerCommandStatus.Successful)
        {
            throw new RazerProtocolException($"Razer command failed with status 0x{response[0]:X2}.");
        }

        int payloadLength = response[5];
        if (payloadLength > MaximumPayloadLength)
        {
            throw new RazerProtocolException("Response payload length is invalid.");
        }

        if (expectedPayloadLength is not null && payloadLength != expectedPayloadLength)
        {
            throw new RazerProtocolException("Response payload length does not match the command contract.");
        }

        return response.Slice(8, payloadLength).ToArray();
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (int index = 2; index <= 87; index++)
        {
            checksum ^= report[index];
        }

        return checksum;
    }
}
