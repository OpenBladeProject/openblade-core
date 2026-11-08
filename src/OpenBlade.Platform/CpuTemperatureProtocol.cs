using System.Buffers.Binary;

namespace OpenBlade.Platform;

public enum CpuTemperatureReplyStatus
{
    Unavailable = 0,
    Value = 1,
    Ready = 2,
    TerminalUnavailable = 3,
}

/// <summary>Fixed, private standard-input/output framing for the CPU sensor worker.</summary>
public static class CpuTemperatureProtocol
{
    public const int RequestLength = 8;
    public const int ReplyLength = 20;
    public const uint Magic = 0x31545043;
    public const int MinimumMilliCelsius = 10_000;
    public const int MaximumMilliCelsius = 125_000;

    public static byte[] EncodeRequest(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        byte[] frame = new byte[RequestLength];
        BinaryPrimitives.WriteInt64LittleEndian(frame, sequence);
        return frame;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> frame, out long sequence)
    {
        sequence = frame.Length == RequestLength ? BinaryPrimitives.ReadInt64LittleEndian(frame) : 0;
        return sequence > 0;
    }

    public static byte[] EncodeReply(long sequence, int? milliCelsius)
        => EncodeReply(sequence, milliCelsius.HasValue ? CpuTemperatureReplyStatus.Value
            : CpuTemperatureReplyStatus.Unavailable, milliCelsius);

    public static byte[] EncodeReadyReply(long sequence) => EncodeReply(sequence, CpuTemperatureReplyStatus.Ready, null);

    public static byte[] EncodeTerminalReply(long sequence) => EncodeReply(sequence, CpuTemperatureReplyStatus.TerminalUnavailable, null);

    private static byte[] EncodeReply(long sequence, CpuTemperatureReplyStatus status, int? milliCelsius)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        if (milliCelsius is { } temperature && !IsValid(temperature))
        {
            throw new ArgumentOutOfRangeException(nameof(milliCelsius));
        }
        byte[] frame = new byte[ReplyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(4), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), (int)status);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), milliCelsius ?? 0);
        return frame;
    }

    public static bool TryDecodeReply(ReadOnlySpan<byte> frame, long expectedSequence, out int? celsius)
        => TryDecodeReply(frame, expectedSequence, out celsius, out CpuTemperatureReplyStatus status)
            && status is CpuTemperatureReplyStatus.Value or CpuTemperatureReplyStatus.Unavailable;

    public static bool TryDecodeReply(ReadOnlySpan<byte> frame, long expectedSequence, out int? celsius,
        out CpuTemperatureReplyStatus status)
    {
        celsius = null;
        status = CpuTemperatureReplyStatus.Unavailable;
        if (frame.Length != ReplyLength || expectedSequence <= 0
            || BinaryPrimitives.ReadUInt32LittleEndian(frame) != Magic
            || BinaryPrimitives.ReadInt64LittleEndian(frame[4..]) != expectedSequence)
        {
            return false;
        }
        int encodedStatus = BinaryPrimitives.ReadInt32LittleEndian(frame[12..]);
        int temperature = BinaryPrimitives.ReadInt32LittleEndian(frame[16..]);
        if (encodedStatus is 0 or 2 or 3)
        {
            status = (CpuTemperatureReplyStatus)encodedStatus;
            return temperature == 0;
        }
        if (encodedStatus != 1 || !IsValid(temperature)) { return false; }
        status = CpuTemperatureReplyStatus.Value;
        // Round up: truncating would under-report the safety input.
        celsius = (temperature + 999) / 1000;
        return true;
    }

    private static bool IsValid(int value) => value is >= MinimumMilliCelsius and <= MaximumMilliCelsius;
}
