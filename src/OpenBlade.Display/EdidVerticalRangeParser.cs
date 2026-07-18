namespace OpenBlade.Display;

internal static class EdidVerticalRangeParser
{
    private static ReadOnlySpan<byte> Header => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    internal static bool TryParse(ReadOnlySpan<byte> edid, out EdidVerticalRange range)
    {
        range = default;
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header) || !HasValidChecksum(edid[..128]))
        {
            return false;
        }

        for (int offset = 54; offset <= 108; offset += 18)
        {
            ReadOnlySpan<byte> descriptor = edid.Slice(offset, 18);
            if (descriptor[0] != 0 || descriptor[1] != 0 || descriptor[2] != 0 || descriptor[3] != 0xFD)
            {
                continue;
            }

            int minimumHertz = descriptor[5];
            int maximumHertz = descriptor[6];
            if (minimumHertz >= 20 && maximumHertz > minimumHertz)
            {
                range = new EdidVerticalRange(minimumHertz, maximumHertz);
                return true;
            }
        }

        return false;
    }

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

internal readonly record struct EdidVerticalRange(int MinimumHertz, int MaximumHertz);
