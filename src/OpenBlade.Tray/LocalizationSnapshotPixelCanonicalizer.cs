namespace OpenBlade.Tray;

internal static class LocalizationSnapshotPixelCanonicalizer
{
    public const string RenderProfileIdentifier =
        "bgra8-4-bit-midpoint-local-majority-v2";

    public static void Canonicalize(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 ||
            pixels.Length != checked(width * height * 4))
        {
            throw new InvalidOperationException(
                "The localization snapshot pixel buffer is not BGRA8 data.");
        }

        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            for (int channel = 0; channel < 3; channel++)
            {
                byte value = pixels[index + channel];
                byte canonical = value is 0 or 255
                    ? value
                    : (byte)((value & 0xF0) | 0x07);
                pixels[index + channel] = Math.Min(canonical, alpha);
            }
        }

        byte[] quantized = (byte[])pixels.Clone();
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int centerIndex = ((y * width) + x) * 4;
                uint center = ReadPixel(quantized, centerIndex);
                uint majority = FindNeighborMajority(quantized, width, x, y);
                int majorityCount = CountNeighborColor(
                    quantized,
                    width,
                    x,
                    y,
                    majority);

                if (majorityCount >= 6 && IsAdjacentPaletteColor(center, majority))
                {
                    WritePixel(pixels, centerIndex, majority);
                }
                else if (TryResolveAdjacentPaletteTie(
                    quantized,
                    width,
                    x,
                    y,
                    center,
                    out uint tieWinner))
                {
                    WritePixel(pixels, centerIndex, tieWinner);
                }
            }
        }
    }

    private static uint FindNeighborMajority(
        byte[] pixels,
        int width,
        int centerX,
        int centerY)
    {
        uint majority = 0;
        int majorityBalance = 0;
        for (int y = centerY - 1; y <= centerY + 1; y++)
        {
            for (int x = centerX - 1; x <= centerX + 1; x++)
            {
                if (x == centerX && y == centerY)
                {
                    continue;
                }

                uint candidate = ReadPixel(pixels, ((y * width) + x) * 4);
                if (majorityBalance == 0)
                {
                    majority = candidate;
                    majorityBalance = 1;
                }
                else
                {
                    majorityBalance += candidate == majority ? 1 : -1;
                }
            }
        }

        return majority;
    }

    private static int CountNeighborColor(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        uint color)
    {
        int count = 0;
        for (int y = centerY - 1; y <= centerY + 1; y++)
        {
            for (int x = centerX - 1; x <= centerX + 1; x++)
            {
                if ((x != centerX || y != centerY) &&
                    ReadPixel(pixels, ((y * width) + x) * 4) == color)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool TryResolveAdjacentPaletteTie(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        uint center,
        out uint winner)
    {
        uint first = 0;
        uint second = 0;
        int firstCount = 0;
        int secondCount = 0;
        bool hasFirst = false;
        bool hasSecond = false;

        for (int y = centerY - 1; y <= centerY + 1; y++)
        {
            for (int x = centerX - 1; x <= centerX + 1; x++)
            {
                if (x == centerX && y == centerY)
                {
                    continue;
                }

                uint candidate = ReadPixel(pixels, ((y * width) + x) * 4);
                if (!hasFirst || candidate == first)
                {
                    first = candidate;
                    firstCount++;
                    hasFirst = true;
                }
                else if (!hasSecond || candidate == second)
                {
                    second = candidate;
                    secondCount++;
                    hasSecond = true;
                }
                else
                {
                    winner = 0;
                    return false;
                }
            }
        }

        if (!hasSecond || firstCount != 4 || secondCount != 4 ||
            (center != first && center != second) ||
            !IsAdjacentPaletteColor(first, second) ||
            !HasTwoColorArcTopology(pixels, width, centerX, centerY))
        {
            winner = 0;
            return false;
        }

        int firstBrightness = GetRgbBrightness(first);
        int secondBrightness = GetRgbBrightness(second);
        winner = firstBrightness == secondBrightness
            ? Math.Max(first, second)
            : firstBrightness > secondBrightness ? first : second;
        return true;
    }

    private static bool HasTwoColorArcTopology(
        byte[] pixels,
        int width,
        int centerX,
        int centerY)
    {
        int transitions = 0;
        uint previous = ReadRingPixel(pixels, width, centerX, centerY, 7);
        for (int ringIndex = 0; ringIndex < 8; ringIndex++)
        {
            uint current = ReadRingPixel(
                pixels,
                width,
                centerX,
                centerY,
                ringIndex);
            if (current != previous)
            {
                transitions++;
            }

            previous = current;
        }

        return transitions == 2;
    }

    private static uint ReadRingPixel(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        int ringIndex)
    {
        (int offsetX, int offsetY) = ringIndex switch
        {
            0 => (-1, -1),
            1 => (0, -1),
            2 => (1, -1),
            3 => (1, 0),
            4 => (1, 1),
            5 => (0, 1),
            6 => (-1, 1),
            7 => (-1, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(ringIndex)),
        };
        return ReadPixel(
            pixels,
            (((centerY + offsetY) * width) + centerX + offsetX) * 4);
    }

    private static int GetRgbBrightness(uint value)
    {
        return (byte)value +
            (byte)(value >> 8) +
            (byte)(value >> 16);
    }

    private static uint ReadPixel(byte[] pixels, int index)
    {
        return (uint)(pixels[index] |
            (pixels[index + 1] << 8) |
            (pixels[index + 2] << 16) |
            (pixels[index + 3] << 24));
    }

    private static void WritePixel(byte[] pixels, int index, uint value)
    {
        pixels[index] = (byte)value;
        pixels[index + 1] = (byte)(value >> 8);
        pixels[index + 2] = (byte)(value >> 16);
        pixels[index + 3] = (byte)(value >> 24);
    }

    private static bool IsAdjacentPaletteColor(uint value, uint candidate)
    {
        if ((byte)(value >> 24) != (byte)(candidate >> 24))
        {
            return false;
        }

        bool differs = false;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int channelDelta = Math.Abs(
                (byte)(value >> shift) - (byte)(candidate >> shift));
            if (channelDelta > 16)
            {
                return false;
            }

            differs |= channelDelta != 0;
        }

        return differs;
    }
}
