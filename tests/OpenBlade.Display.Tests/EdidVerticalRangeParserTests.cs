using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class EdidVerticalRangeParserTests
{
    [Fact]
    public void TryParse_ReadsPanelVerticalRangeDescriptor()
    {
        byte[] edid = CreateEdid(48, 240);

        bool parsed = EdidVerticalRangeParser.TryParse(edid, out EdidVerticalRange range);

        Assert.True(parsed);
        Assert.Equal(new EdidVerticalRange(48, 240), range);
    }

    [Fact]
    public void TryParse_RejectsCorruptEdid()
    {
        byte[] edid = CreateEdid(48, 240);
        edid[20] ^= 0x01;

        Assert.False(EdidVerticalRangeParser.TryParse(edid, out _));
    }

    private static byte[] CreateEdid(byte minimumHertz, byte maximumHertz)
    {
        byte[] edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        const int descriptor = 54;
        edid[descriptor + 3] = 0xFD;
        edid[descriptor + 5] = minimumHertz;
        edid[descriptor + 6] = maximumHertz;
        edid[127] = checked((byte)(0x100 - (edid.Take(127).Sum(value => value) & 0xFF)));
        return edid;
    }
}
