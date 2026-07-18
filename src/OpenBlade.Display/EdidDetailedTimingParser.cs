namespace OpenBlade.Display;

internal static class EdidDetailedTimingParser
{
    private const int EdidBlockLength = 128;
    private const byte DisplayIdExtensionTag = 0x70;
    private const byte Type7DetailedTimingBlockTag = 0x22;
    private const int Type7TimingLength = 20;

    internal static IReadOnlyList<EdidDetailedTiming> Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < EdidBlockLength || !HasValidChecksum(edid[..EdidBlockLength]))
        {
            return [];
        }

        List<EdidDetailedTiming> timings = [];
        int extensionCount = Math.Min(edid[126], (edid.Length / EdidBlockLength) - 1);
        for (int extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
        {
            ReadOnlySpan<byte> extension = edid.Slice(
                (extensionIndex + 1) * EdidBlockLength,
                EdidBlockLength);
            if (extension[0] != DisplayIdExtensionTag || !HasValidChecksum(extension))
            {
                continue;
            }

            int payloadEnd = Math.Min(5 + extension[2], 127);
            for (int offset = 5; offset + 3 <= payloadEnd;)
            {
                byte tag = extension[offset];
                int payloadLength = extension[offset + 2];
                int dataOffset = offset + 3;
                if (payloadLength == 0 || dataOffset + payloadLength > payloadEnd)
                {
                    break;
                }

                if (tag == Type7DetailedTimingBlockTag)
                {
                    ParseType7Timings(extension.Slice(dataOffset, payloadLength), timings);
                }

                offset = dataOffset + payloadLength;
            }
        }

        return timings
            .Distinct()
            .OrderBy(timing => timing.Hertz)
            .ToArray();
    }

    private static void ParseType7Timings(
        ReadOnlySpan<byte> data,
        ICollection<EdidDetailedTiming> timings)
    {
        for (int offset = 0; offset + Type7TimingLength <= data.Length; offset += Type7TimingLength)
        {
            ReadOnlySpan<byte> timing = data.Slice(offset, Type7TimingLength);
            uint pixelClockKilohertz = ReadUInt24(timing);
            int width = ReadUInt16(timing[4..]) + 1;
            int horizontalBlank = ReadUInt16(timing[6..]) + 1;
            int height = ReadUInt16(timing[12..]) + 1;
            int verticalBlank = ReadUInt16(timing[14..]) + 1;
            long totalPixels = (long)(width + horizontalBlank) * (height + verticalBlank);
            if (pixelClockKilohertz == 0 || width <= 0 || height <= 0 || totalPixels <= 0)
            {
                continue;
            }

            int hertz = checked((int)Math.Round(
                pixelClockKilohertz * 1000d / totalPixels,
                MidpointRounding.AwayFromZero));
            if (hertz is >= 20 and <= 1000)
            {
                timings.Add(new EdidDetailedTiming(width, height, hertz));
            }
        }
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> value) =>
        (uint)(value[0] | (value[1] << 8) | (value[2] << 16));

    private static int ReadUInt16(ReadOnlySpan<byte> value) => value[0] | (value[1] << 8);

    private static bool HasValidChecksum(ReadOnlySpan<byte> block)
    {
        int sum = 0;
        foreach (byte value in block)
        {
            sum += value;
        }

        return (sum & 0xFF) == 0;
    }
}

internal readonly record struct EdidDetailedTiming(int Width, int Height, int Hertz);
