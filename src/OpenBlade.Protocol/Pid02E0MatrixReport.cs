using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// One RGB value in the PID 02E0 host-rendered keyboard matrix.
/// </summary>
internal readonly record struct MatrixRgb(byte Red, byte Green, byte Blue)
{
    internal static MatrixRgb Black => default;

    internal MatrixRgb Scale(byte intensity) => new(
        ScaleChannel(Red, intensity),
        ScaleChannel(Green, intensity),
        ScaleChannel(Blue, intensity));

    private static byte ScaleChannel(byte channel, byte intensity) =>
        checked((byte)(((channel * intensity) + 127) / 255));
}

/// <summary>
/// The 102 captured RGB positions, including the sixteen positions that are
/// always black on the RZ09-0581 keyboard layout.
/// </summary>
internal sealed class Pid02E0MatrixFrame
{
    internal const int SlotCount = 102;

    private readonly MatrixRgb[] _slots;

    internal Pid02E0MatrixFrame(ReadOnlySpan<MatrixRgb> slots)
    {
        if (slots.Length != SlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slots),
                slots.Length,
                $"A PID 02E0 matrix frame must contain exactly {SlotCount} RGB slots.");
        }

        _slots = slots.ToArray();
    }

    internal MatrixRgb this[int index] => _slots[index];

    internal MatrixRgb[] ToArray() => (MatrixRgb[])_slots.Clone();

    internal Pid02E0MatrixFrame WithSlot(int index, MatrixRgb color)
    {
        if ((uint)index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        MatrixRgb[] slots = ToArray();
        slots[index] = color;
        return new Pid02E0MatrixFrame(slots);
    }
}

/// <summary>
/// Captured layout mask for the RZ09-0581 / PID 02E0 matrix stream. The slot
/// numbering is protocol order, not a claimed physical key map.
/// </summary>
internal static class Pid02E0MatrixLayout
{
    private static readonly bool[] HoleMask = CreateHoleMask();

    internal static IReadOnlyList<int> CapturedHoleSlots => AmbientLightingFrame.LayoutHoleSlots;

    internal static bool IsHole(int slot)
    {
        if ((uint)slot >= Pid02E0MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return HoleMask[slot];
    }

    internal static Pid02E0MatrixFrame RenderUniform(MatrixRgb color)
    {
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = HoleMask[slot] ? MatrixRgb.Black : color;
        }

        return new Pid02E0MatrixFrame(slots);
    }

    private static bool[] CreateHoleMask()
    {
        bool[] mask = new bool[Pid02E0MatrixFrame.SlotCount];
        foreach (int slot in AmbientLightingFrame.LayoutHoleSlots)
        {
            mask[slot] = true;
        }

        return mask;
    }
}

internal readonly record struct Pid02E0MatrixReport(
    byte TransactionId,
    Pid02E0MatrixFrame Frame);

/// <summary>
/// Offline codec for already-understood PID 02E0 matrix envelopes. This type
/// deliberately has no HID handle, transport, capability, or retry surface.
/// </summary>
internal static class Pid02E0MatrixReportCodec
{
    internal const int EnvelopeLength = 374;
    internal const int FeatureReportBufferLength = 375;
    internal const int PayloadLength = 0x0136;
    internal const byte ReportId = 0x00;
    internal const byte CommandClass = 0x03;
    internal const byte CommandId = 0x0B;

    private const int RgbOffset = 12;
    private const int ChecksumOffset = 372;
    private const int TerminatorOffset = 373;

    internal static byte[] EncodeEnvelope(Pid02E0MatrixReport report)
    {
        byte[] envelope = new byte[EnvelopeLength];
        EncodeEnvelope(report, envelope);
        return envelope;
    }

    internal static byte[] EncodeFeatureReportBuffer(Pid02E0MatrixReport report)
    {
        byte[] buffer = new byte[FeatureReportBufferLength];
        buffer[0] = ReportId;
        EncodeEnvelope(report, buffer.AsSpan(1));
        return buffer;
    }

    private static void EncodeEnvelope(Pid02E0MatrixReport report, Span<byte> envelope)
    {
        ArgumentNullException.ThrowIfNull(report.Frame);
        if (envelope.Length != EnvelopeLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(envelope),
                envelope.Length,
                $"A PID 02E0 matrix envelope must contain exactly {EnvelopeLength} bytes.");
        }

        envelope.Clear();
        envelope[0] = (byte)RazerCommandStatus.New;
        envelope[1] = report.TransactionId;
        envelope[4] = 0x01;
        envelope[5] = 0x36;
        envelope[6] = CommandClass;
        envelope[7] = CommandId;
        envelope[8] = 0x00;
        envelope[9] = 0x00;
        envelope[10] = 0x00;
        envelope[11] = 0x65;

        for (int slot = 0; slot < Pid02E0MatrixFrame.SlotCount; slot++)
        {
            MatrixRgb color = report.Frame[slot];
            int offset = RgbOffset + (slot * 3);
            envelope[offset] = color.Red;
            envelope[offset + 1] = color.Green;
            envelope[offset + 2] = color.Blue;
        }

        envelope[ChecksumOffset] = CalculateChecksum(envelope);
    }

    internal static Pid02E0MatrixReport DecodeEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length != EnvelopeLength)
        {
            throw new FormatException($"Expected a {EnvelopeLength}-byte PID 02E0 matrix envelope.");
        }

        if (envelope[0] != (byte)RazerCommandStatus.New
            || envelope[2] != 0
            || envelope[3] != 0
            || envelope[4] != 0x01
            || envelope[5] != 0x36
            || envelope[6] != CommandClass
            || envelope[7] != CommandId
            || envelope[8] != 0
            || envelope[9] != 0
            || envelope[10] != 0
            || envelope[11] != 0x65
            || envelope[TerminatorOffset] != 0)
        {
            throw new FormatException("The PID 02E0 matrix envelope header or terminator is invalid.");
        }

        if (!IsZeroFilled(envelope.Slice(RgbOffset + (Pid02E0MatrixFrame.SlotCount * 3), 54)))
        {
            throw new FormatException("The PID 02E0 matrix envelope padding is not zero-filled.");
        }

        if (envelope[ChecksumOffset] != CalculateChecksum(envelope))
        {
            throw new FormatException("The PID 02E0 matrix envelope checksum is invalid.");
        }

        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            int offset = RgbOffset + (slot * 3);
            slots[slot] = new MatrixRgb(
                envelope[offset],
                envelope[offset + 1],
                envelope[offset + 2]);
        }

        return new Pid02E0MatrixReport(
            envelope[1],
            new Pid02E0MatrixFrame(slots));
    }

    internal static Pid02E0MatrixReport DecodeFeatureReportBuffer(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != FeatureReportBufferLength)
        {
            throw new FormatException($"Expected a {FeatureReportBufferLength}-byte feature-report buffer.");
        }

        if (buffer[0] != ReportId)
        {
            throw new FormatException("The PID 02E0 matrix feature report ID must be zero.");
        }

        return DecodeEnvelope(buffer[1..]);
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> envelope)
    {
        byte checksum = 0;
        for (int index = 2; index < ChecksumOffset; index++)
        {
            checksum ^= envelope[index];
        }

        return checksum;
    }

    private static bool IsZeroFilled(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
