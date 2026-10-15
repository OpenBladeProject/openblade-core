namespace OpenBlade.Protocol;

internal enum RazerCommandStatus : byte
{
    New = 0x00,
    Successful = 0x02,
    NotSupported = 0x05,
}

internal enum DeviceStateQuery
{
    BatteryProtection,
    TemporaryFullCharge,
    PerformanceAndFans,
    ThermalChannelOne,
    ThermalChannelTwo,
    ThermalChannelAgreement,
    CustomCpuPerformance,
    CustomGpuPerformance,
    CustomPerformanceCombination,
    KeyboardBrightness,
    LogoLightingState,
    LogoLightingEffect,
    LogoLightingCombination,
    KeyboardLightingEffect,
    KeyboardLightingCombination,
}

public sealed class RazerProtocolException : Exception
{
    public RazerProtocolException(
        string message,
        HidProtocolValidationFailure validationFailure)
        : base(message)
    {
        ValidationFailure = validationFailure;
    }

    public HidProtocolValidationFailure ValidationFailure { get; }

    internal DeviceStateQuery? StateQuery { get; init; }

    internal string? RejectedPayloadHex { get; init; }

    // Present only when the combined state read completed every other getter
    // after a content rejection. Callers that do not consume it keep the
    // fail-closed whole-read behavior of an ordinary protocol exception.
    internal PartialDeviceStateRead? PartialState { get; init; }
}

internal sealed class RazerReport
{
    public const int ReportLength = 90;
    public const int MaximumPayloadLength = 80;

    private readonly byte[] payloadBytes;

    private RazerReport(byte transactionId, byte commandClass, byte commandId, byte[] payload)
    {
        TransactionId = transactionId;
        CommandClass = commandClass;
        CommandId = commandId;
        payloadBytes = payload;
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
        Encode(bytes);
        return bytes;
    }

    internal void Encode(Span<byte> destination)
    {
        if (destination.Length != ReportLength)
        {
            throw new ArgumentException(
                $"A Razer report destination must contain exactly {ReportLength} bytes.",
                nameof(destination));
        }

        destination.Clear();
        destination[0] = (byte)RazerCommandStatus.New;
        destination[1] = TransactionId;
        destination[5] = checked((byte)payloadBytes.Length);
        destination[6] = CommandClass;
        destination[7] = CommandId;
        payloadBytes.CopyTo(destination[8..]);
        destination[88] = CalculateChecksum(destination);
    }

    internal static void Encode(
        byte transactionId,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Razer payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        if (destination.Length != ReportLength)
        {
            throw new ArgumentException(
                $"A Razer report destination must contain exactly {ReportLength} bytes.",
                nameof(destination));
        }

        destination.Clear();
        destination[0] = (byte)RazerCommandStatus.New;
        destination[1] = transactionId;
        destination[5] = checked((byte)payload.Length);
        destination[6] = commandClass;
        destination[7] = commandId;
        payload.CopyTo(destination[8..]);
        destination[88] = CalculateChecksum(destination);
    }

    public ReadOnlyMemory<byte> ValidateResponse(
        ReadOnlySpan<byte> response,
        int? expectedPayloadLength = null)
    {
        if (response.Length != ReportLength)
        {
            throw new RazerProtocolException(
                $"Expected a {ReportLength}-byte response.",
                HidProtocolValidationFailure.EnvelopeLength);
        }

        if (response[88] != CalculateChecksum(response))
        {
            throw new RazerProtocolException(
                "Response checksum is invalid.",
                HidProtocolValidationFailure.Checksum);
        }

        if (response[1] != TransactionId)
        {
            throw new RazerProtocolException(
                "Response transaction does not match the request.",
                HidProtocolValidationFailure.TransactionMismatch);
        }

        if (response[6] != CommandClass)
        {
            throw new RazerProtocolException(
                "Response command class does not match the request.",
                HidProtocolValidationFailure.CommandClassMismatch);
        }

        if (response[7] != CommandId)
        {
            throw new RazerProtocolException(
                "Response command ID does not match the request.",
                HidProtocolValidationFailure.CommandIdMismatch);
        }

        RazerCommandStatus status = (RazerCommandStatus)response[0];
        if (status != RazerCommandStatus.Successful)
        {
            throw new RazerProtocolException(
                $"Razer command failed with status 0x{response[0]:X2}.",
                HidProtocolValidationFailure.Status);
        }

        int payloadLength = response[5];
        if (payloadLength > MaximumPayloadLength)
        {
            throw new RazerProtocolException(
                "Response payload length is invalid.",
                HidProtocolValidationFailure.PayloadLength);
        }

        if (expectedPayloadLength is not null && payloadLength != expectedPayloadLength)
        {
            throw new RazerProtocolException(
                "Response payload length does not match the command contract.",
                HidProtocolValidationFailure.PayloadLength);
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
