namespace OpenBlade.Tray;

internal static class LocalizationSnapshotPixelCanonicalizer
{
    public const string RenderProfileIdentifier =
        "bgra8-4-bit-midpoint-local-majority-edge-alpha-filled-corner-v4";

    private const int RoundedEdgeBandWidth = 2;

    public static void RequireVisibleDetail(byte[] pixels, bool requireOpaqueSurface = false)
    {
        if (requireOpaqueSurface)
        {
            int opaquePixels = 0;
            for (int index = 3; index < pixels.Length; index += 4)
            {
                opaquePixels += pixels[index] == 255 ? 1 : 0;
            }

            // A flyout has a solid surface apart from its rounded corners.
            // Content-only captures contain antialiased text on transparency:
            // they have varying pixels but still omit the actual flyout.
            int pixelCount = pixels.Length / 4;
            if (opaquePixels <= pixelCount / 2)
            {
                throw new InvalidOperationException(
                    "The flyout snapshot is missing its opaque presenter surface.");
            }
        }

        uint? firstVisiblePixel = null;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 0)
            {
                continue;
            }

            uint pixel = ReadPixel(pixels, index);
            firstVisiblePixel ??= pixel;
            if (pixel != firstVisiblePixel.Value)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The localization snapshot is blank: no visible detail was rendered.");
    }

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

        CanonicalizeRoundedEdges(pixels, width, height);

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

        CanonicalizeOpaqueFilledRoundedCorners(pixels, quantized, width, height);
    }

    private static void CanonicalizeOpaqueFilledRoundedCorners(
        byte[] pixels,
        byte[] quantized,
        int width,
        int height)
    {
        for (int y = 2; y < height - 2; y++)
        {
            for (int x = 2; x < width - 2; x++)
            {
                int centerIndex = ((y * width) + x) * 4;
                uint center = ReadPixel(quantized, centerIndex);
                if (TryResolveOpaqueFilledRoundedCorner(
                    quantized,
                    width,
                    x,
                    y,
                    center,
                    out uint winner))
                {
                    WritePixel(pixels, centerIndex, winner);
                }
            }
        }
    }

    private static bool TryResolveOpaqueFilledRoundedCorner(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        uint center,
        out uint winner)
    {
        // A native filled rounded corner can gain or lose a tiny opaque blend
        // fringe between captures. Require both substantial flat surfaces in
        // a 5x5 neighborhood so thin glyphs and chart strokes remain intact.
        uint first = 0;
        uint second = 0;
        uint third = 0;
        int firstCount = 0;
        int secondCount = 0;
        int thirdCount = 0;
        int colorCount = 0;

        for (int ringIndex = 0; ringIndex < 8; ringIndex++)
        {
            uint candidate = ReadRingPixel(
                pixels,
                width,
                centerX,
                centerY,
                ringIndex);
            if (colorCount > 0 && candidate == first)
            {
                firstCount++;
            }
            else if (colorCount > 1 && candidate == second)
            {
                secondCount++;
            }
            else if (colorCount > 2 && candidate == third)
            {
                thirdCount++;
            }
            else if (colorCount == 0)
            {
                first = candidate;
                firstCount = 1;
                colorCount = 1;
            }
            else if (colorCount == 1)
            {
                second = candidate;
                secondCount = 1;
                colorCount = 2;
            }
            else if (colorCount == 2)
            {
                third = candidate;
                thirdCount = 1;
                colorCount = 3;
            }
            else
            {
                winner = 0;
                return false;
            }
        }

        uint dark;
        uint bright;
        if (colorCount == 2 && firstCount == 4 && secondCount == 4 &&
            center != first && center != second &&
            CountRingTransitions(pixels, width, centerX, centerY) == 2)
        {
            (dark, bright) = OrderByBrightness(first, second);
        }
        else
        {
            if (colorCount != 3 ||
                CountRingTransitions(pixels, width, centerX, centerY) != 3 ||
                !TryGetThreeColorRoundedCornerBases(
                    center,
                    first,
                    firstCount,
                    second,
                    secondCount,
                    third,
                    thirdCount,
                    out dark,
                    out bright))
            {
                winner = 0;
                return false;
            }
        }

        if (!TryGetOpaqueAchromaticValue(dark, out byte darkValue) ||
            !TryGetOpaqueAchromaticValue(center, out byte centerValue) ||
            !TryGetOpaqueAchromaticValue(bright, out byte brightValue) ||
            centerValue != darkValue + 16 ||
            centerValue >= brightValue ||
            brightValue - darkValue < 32 ||
            !HasFilledRoundedCornerNeighborhood(
                pixels,
                width,
                centerX,
                centerY,
                dark,
                center,
                bright))
        {
            winner = 0;
            return false;
        }

        winner = bright;
        return true;
    }

    private static bool TryGetThreeColorRoundedCornerBases(
        uint center,
        uint first,
        int firstCount,
        uint second,
        int secondCount,
        uint third,
        int thirdCount,
        out uint dark,
        out uint bright)
    {
        if (first == center && firstCount == 1 &&
            AreThreeAndFour(secondCount, thirdCount))
        {
            (dark, bright) = OrderByBrightness(second, third);
            return true;
        }

        if (second == center && secondCount == 1 &&
            AreThreeAndFour(firstCount, thirdCount))
        {
            (dark, bright) = OrderByBrightness(first, third);
            return true;
        }

        if (third == center && thirdCount == 1 &&
            AreThreeAndFour(firstCount, secondCount))
        {
            (dark, bright) = OrderByBrightness(first, second);
            return true;
        }

        dark = 0;
        bright = 0;
        return false;
    }

    private static bool AreThreeAndFour(int first, int second)
    {
        return first is 3 or 4 && second is 3 or 4 && first != second;
    }

    private static (uint Dark, uint Bright) OrderByBrightness(uint first, uint second)
    {
        return GetRgbBrightness(first) <= GetRgbBrightness(second)
            ? (first, second)
            : (second, first);
    }

    private static bool TryGetOpaqueAchromaticValue(uint color, out byte value)
    {
        byte blue = (byte)color;
        byte green = (byte)(color >> 8);
        byte red = (byte)(color >> 16);
        byte alpha = (byte)(color >> 24);
        value = blue;
        return alpha == 255 && blue == green && green == red;
    }

    private static bool HasFilledRoundedCornerNeighborhood(
        byte[] pixels,
        int width,
        int centerX,
        int centerY,
        uint dark,
        uint blend,
        uint bright)
    {
        int darkCount = 0;
        int blendCount = 0;
        int brightCount = 0;
        for (int y = centerY - 2; y <= centerY + 2; y++)
        {
            for (int x = centerX - 2; x <= centerX + 2; x++)
            {
                uint candidate = ReadPixel(pixels, ((y * width) + x) * 4);
                if (candidate == dark)
                {
                    darkCount++;
                }
                else if (candidate == blend)
                {
                    blendCount++;
                }
                else if (candidate == bright)
                {
                    brightCount++;
                }
                else
                {
                    return false;
                }
            }
        }

        return darkCount >= 9 && brightCount >= 9 && blendCount <= 3;
    }

    private static void CanonicalizeRoundedEdges(
        byte[] pixels,
        int width,
        int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x >= RoundedEdgeBandWidth &&
                    x < width - RoundedEdgeBandWidth &&
                    y >= RoundedEdgeBandWidth &&
                    y < height - RoundedEdgeBandWidth)
                {
                    continue;
                }

                int index = ((y * width) + x) * 4;
                byte alpha = CanonicalizeRoundedEdgeAlpha(pixels[index + 3]);
                pixels[index + 3] = alpha;
                for (int channel = 0; channel < 3; channel++)
                {
                    pixels[index + channel] = alpha <= 16
                        ? Math.Min((byte)0x07, alpha)
                        : Math.Min(pixels[index + channel], alpha);
                }
            }
        }
    }

    private static byte CanonicalizeRoundedEdgeAlpha(byte alpha)
    {
        if (alpha is 0 or 255)
        {
            return alpha;
        }

        // RenderTargetBitmap can move a rounded presenter edge by one alpha
        // value between otherwise identical processes. Round only the image
        // perimeter, retaining partial-alpha corner coverage and stable extrema.
        if (alpha <= 8)
        {
            return 8;
        }

        if (alpha >= 248)
        {
            return 248;
        }

        return (byte)(((alpha + 8) / 16) * 16);
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
        return CountRingTransitions(pixels, width, centerX, centerY) == 2;
    }

    private static int CountRingTransitions(
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

        return transitions;
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
