using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class EdidDetailedTimingParserTests
{
    [Fact]
    public void Parse_ReadsDisplayIdType7DetailedTiming()
    {
        byte[] edid = CreateEdidWithType7Timing();

        IReadOnlyList<EdidDetailedTiming> timings = EdidDetailedTimingParser.Parse(edid);

        Assert.Equal([new EdidDetailedTiming(2560, 1600, 240)], timings);
    }

    [Fact]
    public void Parse_IgnoresExtensionWithInvalidChecksum()
    {
        byte[] edid = CreateEdidWithType7Timing();
        edid[140] ^= 0x01;

        Assert.Empty(EdidDetailedTimingParser.Parse(edid));
    }

    private static byte[] CreateEdidWithType7Timing()
    {
        byte[] edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[126] = 1;
        SetChecksum(edid.AsSpan(0, 128));

        Span<byte> extension = edid.AsSpan(128, 128);
        extension[0] = 0x70;
        extension[1] = 0x20;
        extension[2] = 23;
        extension[5] = 0x22;
        extension[6] = 0;
        extension[7] = 20;
        Span<byte> timing = extension.Slice(8, 20);
        timing[0] = 0x00;
        timing[1] = 0x67;
        timing[2] = 0x11;
        timing[3] = 0x85;
        WriteUInt16(timing[4..], 2559);
        WriteUInt16(timing[6..], 79);
        WriteUInt16(timing[8..], 7);
        WriteUInt16(timing[10..], 31);
        WriteUInt16(timing[12..], 1599);
        WriteUInt16(timing[14..], 199);
        WriteUInt16(timing[16..], 164);
        WriteUInt16(timing[18..], 7);
        SetChecksum(extension);
        return edid;
    }

    private static void WriteUInt16(Span<byte> destination, int value)
    {
        destination[0] = checked((byte)(value & 0xFF));
        destination[1] = checked((byte)(value >> 8));
    }

    private static void SetChecksum(Span<byte> block)
    {
        block[127] = 0;
        int sum = 0;
        foreach (byte value in block[..127])
        {
            sum += value;
        }

        block[127] = (byte)((-sum) & 0xFF);
    }
}
