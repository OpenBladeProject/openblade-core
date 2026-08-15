using System.Buffers.Binary;

namespace OpenBlade.Contracts;

public static class KeyboardLightingLayoutIds
{
    public const string Rz09_0581 = "openblade.razer.rz09-0581.keyboard.6x17.v1";
    public const string Rz09_0528 = "openblade.razer.rz09-0528.keyboard.6x17.v1";

    public static bool IsKnown(string? layoutId) =>
        string.Equals(layoutId, Rz09_0581, StringComparison.Ordinal)
        || string.Equals(layoutId, Rz09_0528, StringComparison.Ordinal);
}

public sealed record KeyboardLightingSequenceFrame(
    int DurationMilliseconds,
    IReadOnlyList<RgbColor> Slots);

public sealed record KeyboardLightingSequence(
    IReadOnlyList<KeyboardLightingSequenceFrame> Frames)
{
    public bool IsWellFormed() => KeyboardLightingSequenceCodec.IsWellFormed(this);
}

public static class KeyboardLightingSequenceCodec
{
    public const int MaximumFrameCount = 1800;
    public const int MinimumFrameDurationMilliseconds = 1;
    public const int MaximumFrameDurationMilliseconds = 60_000;
    public const int MaximumTotalDurationMilliseconds = 10 * 60 * 1000;
    public const int MaximumDecodedByteCount = 4096;

    private const uint Magic = 0x5153424f;
    private const byte FormatVersion = 1;
    private const int HeaderByteCount = 7;
    private const int MaximumBase64CharacterCount =
        ((MaximumDecodedByteCount + 2) / 3) * 4;

    public static string Encode(IEnumerable<KeyboardLightingSequenceFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var materialized = new List<KeyboardLightingSequenceFrame>();
        foreach (KeyboardLightingSequenceFrame frame in frames)
        {
            if (materialized.Count == MaximumFrameCount)
            {
                throw new InvalidDataException("The sequence contains too many frames.");
            }

            materialized.Add(frame);
        }

        ValidateFrameCount(materialized.Count);

        byte[] encoded = new byte[MaximumDecodedByteCount];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, Magic);
        encoded[4] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(
            encoded.AsSpan(5),
            checked((ushort)materialized.Count));

        int offset = HeaderByteCount;
        int totalDurationMilliseconds = 0;
        var previous = new RgbColor[AmbientLightingFrame.SlotCount];

        foreach (KeyboardLightingSequenceFrame frame in materialized)
        {
            ValidateFrame(frame, ref totalDurationMilliseconds);
            EnsureCapacity(offset, 3);
            BinaryPrimitives.WriteUInt16LittleEndian(
                encoded.AsSpan(offset),
                checked((ushort)frame.DurationMilliseconds));
            offset += 2;
            int deltaCountOffset = offset++;
            byte deltaCount = 0;

            for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
            {
                RgbColor color = frame.Slots[slot];
                if (color == previous[slot])
                {
                    continue;
                }

                EnsureCapacity(offset, 4);
                encoded[offset++] = checked((byte)slot);
                encoded[offset++] = color.Red;
                encoded[offset++] = color.Green;
                encoded[offset++] = color.Blue;
                deltaCount++;
            }

            encoded[deltaCountOffset] = deltaCount;
            for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
            {
                previous[slot] = frame.Slots[slot];
            }
        }

        return Convert.ToBase64String(encoded, 0, offset);
    }

    public static KeyboardLightingSequence Decode(string timelineBase64)
    {
        if (string.IsNullOrEmpty(timelineBase64)
            || timelineBase64.Length > MaximumBase64CharacterCount)
        {
            throw new InvalidDataException("The lighting timeline is not valid canonical Base64.");
        }

        byte[] buffer = new byte[MaximumDecodedByteCount];
        if (!Convert.TryFromBase64String(timelineBase64, buffer, out int byteCount)
            || byteCount > MaximumDecodedByteCount
            || !string.Equals(
                Convert.ToBase64String(buffer, 0, byteCount),
                timelineBase64,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The lighting timeline is not valid canonical Base64.");
        }

        ReadOnlySpan<byte> encoded = buffer.AsSpan(0, byteCount);
        if (encoded.Length < HeaderByteCount
            || BinaryPrimitives.ReadUInt32LittleEndian(encoded) != Magic
            || encoded[4] != FormatVersion)
        {
            throw new InvalidDataException("The lighting timeline header is invalid.");
        }

        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(encoded[5..]);
        ValidateFrameCount(frameCount);

        int offset = HeaderByteCount;
        int totalDurationMilliseconds = 0;
        var previous = new RgbColor[AmbientLightingFrame.SlotCount];
        var frames = new KeyboardLightingSequenceFrame[frameCount];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            if (encoded.Length - offset < 3)
            {
                throw new InvalidDataException("The lighting timeline is truncated.");
            }

            int durationMilliseconds =
                BinaryPrimitives.ReadUInt16LittleEndian(encoded[offset..]);
            offset += 2;
            ValidateDuration(durationMilliseconds, ref totalDurationMilliseconds);

            int deltaCount = encoded[offset++];
            if (deltaCount > AmbientLightingFrame.SlotCount
                || encoded.Length - offset < deltaCount * 4)
            {
                throw new InvalidDataException("The lighting timeline delta list is invalid.");
            }

            var slots = (RgbColor[])previous.Clone();
            int previousDeltaSlot = -1;
            for (int deltaIndex = 0; deltaIndex < deltaCount; deltaIndex++)
            {
                int slot = encoded[offset++];
                var color = new RgbColor(
                    encoded[offset++],
                    encoded[offset++],
                    encoded[offset++]);
                if (slot >= AmbientLightingFrame.SlotCount
                    || AmbientLightingFrame.IsHole(slot)
                    || slot <= previousDeltaSlot
                    || color == previous[slot])
                {
                    throw new InvalidDataException("The lighting timeline delta list is not canonical.");
                }

                slots[slot] = color;
                previousDeltaSlot = slot;
            }

            frames[frameIndex] = new KeyboardLightingSequenceFrame(
                durationMilliseconds,
                Array.AsReadOnly(slots));
            previous = slots;
        }

        if (offset != encoded.Length)
        {
            throw new InvalidDataException("The lighting timeline contains trailing data.");
        }

        return new KeyboardLightingSequence(Array.AsReadOnly(frames));
    }

    public static bool IsWellFormed(string? timelineBase64)
    {
        if (timelineBase64 is null)
        {
            return false;
        }

        try
        {
            _ = Decode(timelineBase64);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool IsWellFormed(KeyboardLightingSequence? sequence)
    {
        if (sequence is null || sequence.Frames is null)
        {
            return false;
        }

        try
        {
            _ = Encode(sequence.Frames);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateFrameCount(int frameCount)
    {
        if (frameCount is < 1 or > MaximumFrameCount)
        {
            throw new InvalidDataException("The sequence frame count is outside the supported range.");
        }
    }

    private static void ValidateFrame(
        KeyboardLightingSequenceFrame? frame,
        ref int totalDurationMilliseconds)
    {
        if (frame?.Slots is null
            || frame.Slots.Count != AmbientLightingFrame.SlotCount)
        {
            throw new InvalidDataException("Each sequence frame must contain exactly 102 slots.");
        }

        ValidateDuration(frame.DurationMilliseconds, ref totalDurationMilliseconds);
        for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
        {
            if (AmbientLightingFrame.IsHole(slot) && frame.Slots[slot] != default)
            {
                throw new InvalidDataException("Keyboard layout holes must remain black.");
            }
        }
    }

    private static void ValidateDuration(
        int durationMilliseconds,
        ref int totalDurationMilliseconds)
    {
        if (durationMilliseconds is
            < MinimumFrameDurationMilliseconds
            or > MaximumFrameDurationMilliseconds)
        {
            throw new InvalidDataException("A sequence frame duration is outside the supported range.");
        }

        totalDurationMilliseconds = checked(
            totalDurationMilliseconds + durationMilliseconds);
        if (totalDurationMilliseconds > MaximumTotalDurationMilliseconds)
        {
            throw new InvalidDataException("The sequence duration exceeds ten minutes.");
        }
    }

    private static void EnsureCapacity(int offset, int additionalByteCount)
    {
        if (offset > MaximumDecodedByteCount - additionalByteCount)
        {
            throw new InvalidDataException("The encoded sequence exceeds 4096 bytes.");
        }
    }
}
