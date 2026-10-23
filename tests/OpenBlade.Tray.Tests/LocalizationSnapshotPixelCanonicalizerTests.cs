namespace OpenBlade.Tray.Tests;

public sealed class LocalizationSnapshotPixelCanonicalizerTests
{
    private const byte Selected = 0xF7;
    private const byte Background = 0xFF;
    private const byte CornerFill = 0xC7;
    private const byte CornerFringe = 0xD7;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CanonicalizeRepairsRoundedCornerRasterizationNoise(int rotations)
    {
        string[] rows =
        [
            "WWW",
            "GGW",
            "GGG",
        ];
        byte[] pixels = CreatePixels(Rotate(rows, rotations));

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            3,
            3);

        AssertPixel(pixels, 3, 1, 1, Background);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void CanonicalizeConvergesRetainedFilledRoundedCornerFringe(
        int rotations,
        bool mirrored)
    {
        string[] referenceRows =
        [
            "WWCCCCCCC",
            "WWFCCCCCC",
            "WWFCCCCCC",
            "WWWCCCCCC",
            "WWWWFCCCC",
            "WWWWWWWWW",
            "WWWWWWWWW",
            "WWWWWWWWW",
            "WWWWWWWWW",
        ];
        string[] actualRows =
        {
            "WWCCCCCCC",
            "WWFCCCCCC",
            "WWWCCCCCC",
            "WWWCCCCCC",
            "WWWWWCCCC",
            "WWWWWWWWW",
            "WWWWWWWWW",
            "WWWWWWWWW",
            "WWWWWWWWW",
        };
        string[] transformedReference = Rotate(
            mirrored ? Mirror(referenceRows) : referenceRows,
            rotations);
        string[] transformedActual = Rotate(
            mirrored ? Mirror(actualRows) : actualRows,
            rotations);
        byte[] reference = CreatePixels(transformedReference);
        byte[] actual = CreatePixels(transformedActual);
        byte[] originalReference = (byte[])reference.Clone();

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            reference,
            9,
            9);
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            actual,
            9,
            9);

        Assert.Equal(reference, actual);
        Assert.False(originalReference.AsSpan().SequenceEqual(reference));
        byte[] once = (byte[])reference.Clone();
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            reference,
            9,
            9);
        Assert.Equal(once, reference);
    }

    [Fact]
    public void CanonicalizePreservesThinAchromaticGlyphStroke()
    {
        string[] rows = Pad(
        [
            "WWCWW",
            "WWCWW",
            "WWFWW",
            "WWCWW",
            "WWCWW",
        ]);
        byte[] pixels = CreatePixels(rows);
        byte[] original = (byte[])pixels.Clone();

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            9,
            9);

        Assert.Equal(original, pixels);
    }

    [Fact]
    public void CanonicalizePreservesColoredRoundedChartDetail()
    {
        string[] rows = Pad(
        [
            "WWCCC",
            "WWFCC",
            "WWFCC",
            "WWWCC",
            "WWWWF",
        ]);
        byte[] pixels = CreateColoredPixels(rows);
        byte[] original = (byte[])pixels.Clone();

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            9,
            9);

        Assert.Equal(original, pixels);
    }

    [Fact]
    public void CanonicalizePreservesMeaningfulSinglePixelDetail()
    {
        string[] rows =
        [
            "WWW",
            "GDW",
            "GGG",
        ];
        byte[] pixels = CreatePixels(rows);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            3,
            3);

        AssertPixel(pixels, 3, 1, 1, 0x77);
    }

    [Fact]
    public void CanonicalizePreservesTiesWithMoreThanTwoNeighborColors()
    {
        string[] rows =
        [
            "WWD",
            "GGW",
            "GGG",
        ];
        byte[] pixels = CreatePixels(rows);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            3,
            3);

        AssertPixel(pixels, 3, 1, 1, Selected);
    }

    [Fact]
    public void CanonicalizePreservesNonAdjacentPaletteTies()
    {
        string[] rows =
        [
            "DDD",
            "GGD",
            "GGG",
        ];
        byte[] pixels = CreatePixels(rows);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            3,
            3);

        AssertPixel(pixels, 3, 1, 1, Selected);
    }

    [Fact]
    public void CanonicalizePreservesDisconnectedAdjacentPaletteTies()
    {
        string[] rows =
        [
            "WGW",
            "GGG",
            "WGW",
        ];
        byte[] pixels = CreatePixels(rows);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels,
            3,
            3);

        AssertPixel(pixels, 3, 1, 1, Selected);
    }

    [Fact]
    public void CanonicalizeRepairsFreshProcessFlyoutEdgeAlphaNoise()
    {
        const int width = 6;
        const int height = 8;
        byte[] reference = CreateSolidPixels(width, height, Background, 0xFF);
        byte[] actual = (byte[])reference.Clone();

        // The retained PNGs decoded to gray/alpha pairs 231/96 vs 233/95,
        // 178/10 vs 0/9, 137/13 vs 0/12, and 111/16 vs 0/15.
        // These fixtures are their corresponding premultiplied BGRA bytes.
        SetPixel(reference, width, 4, 1, 87, 96);
        SetPixel(actual, width, 4, 1, 87, 95);
        SetPixel(reference, width, 5, 2, 7, 10);
        SetPixel(actual, width, 5, 2, 0, 9);
        SetPixel(reference, width, 5, 3, 7, 13);
        SetPixel(actual, width, 5, 3, 0, 12);
        SetPixel(reference, width, 5, 4, 7, 16);
        SetPixel(actual, width, 5, 4, 0, 15);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            reference, width, height);
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            actual, width, height);

        Assert.Equal(reference, actual);
        AssertPixel(reference, width, 4, 1, 87, 96);
        AssertPixel(reference, width, 5, 2, 7, 16);
        AssertPixel(reference, width, 5, 3, 7, 16);
        AssertPixel(reference, width, 5, 4, 7, 16);
        AssertPremultiplied(reference);

        byte[] once = (byte[])reference.Clone();
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            reference, width, height);
        Assert.Equal(once, reference);
    }

    [Fact]
    public void CanonicalizeLimitsEdgeRepairToPartialAlphaPerimeterPixels()
    {
        const int width = 7;
        const int height = 7;
        byte[] pixels = CreateSolidPixels(width, height, Background, 0xFF);
        SetPixel(pixels, width, 0, 0, 0, 0);
        SetPixel(pixels, width, 1, 0, Background, 0xFF);
        SetPixel(pixels, width, 3, 3, 87, 95);

        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixels, width, height);

        AssertPixel(pixels, width, 0, 0, 0, 0);
        AssertPixel(pixels, width, 1, 0, Background, 0xFF);
        AssertPixel(pixels, width, 3, 3, 87, 95);
        AssertPremultiplied(pixels);
    }

    [Fact]
    public void RequireVisibleDetailRejectsTransparentPopupEvenWithHiddenRgbData()
    {
        byte[] pixels = [255, 100, 50, 0, 10, 20, 30, 0];
        Assert.Throws<InvalidOperationException>(() =>
            OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(pixels));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void RequireVisibleDetailRejectsUniformSurfaceWithTransparentBorder(byte value)
    {
        byte[] pixels = [0, 0, 0, 0, value, value, value, 255, value, value, value, 255];
        Assert.Throws<InvalidOperationException>(() =>
            OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(pixels));
    }

    [Fact]
    public void RequireVisibleDetailAcceptsVisibleTextOnSurface()
    {
        byte[] pixels = CreatePixels(["WDW"]);
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(pixels);
    }

    [Fact]
    public void FlyoutSnapshotRejectsAntialiasedTextWithoutPresenterSurface()
    {
        byte[] pixels = [0, 0, 0, 0, 0, 0, 0, 158, 0, 0, 0, 73];
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(pixels);
        Assert.Throws<InvalidOperationException>(() =>
            OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(
                pixels, requireOpaqueSurface: true));
    }

    [Fact]
    public void FlyoutSnapshotAcceptsTextAndSurfaceWithTransparentRoundedCorner()
    {
        byte[] pixels = [0, 0, 0, 0, 247, 247, 247, 255, 135, 135, 135, 255];
        OpenBlade.Tray.LocalizationSnapshotPixelCanonicalizer.RequireVisibleDetail(
            pixels, requireOpaqueSurface: true);
    }

    private static byte[] CreatePixels(IReadOnlyList<string> rows)
    {
        int width = rows[0].Length;
        byte[] pixels = new byte[checked(width * rows.Count * 4)];
        for (int y = 0; y < rows.Count; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = rows[y][x] switch
                {
                    'W' => Background,
                    'G' => Selected,
                    'D' => 0x77,
                    'C' => CornerFill,
                    'F' => CornerFringe,
                    _ => throw new InvalidOperationException(),
                };
                int index = ((y * width) + x) * 4;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 0xFF;
            }
        }

        return pixels;
    }

    private static byte[] CreateColoredPixels(IReadOnlyList<string> rows)
    {
        int width = rows[0].Length;
        byte[] pixels = new byte[checked(width * rows.Count * 4)];
        for (int y = 0; y < rows.Count; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte blue, byte green, byte red) = rows[y][x] switch
                {
                    'W' => (Background, Background, Background),
                    'C' => ((byte)0x17, (byte)0x67, (byte)0x97),
                    'F' => ((byte)0x27, (byte)0x77, (byte)0xA7),
                    _ => throw new InvalidOperationException(),
                };
                int index = ((y * width) + x) * 4;
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
                pixels[index + 3] = 0xFF;
            }
        }

        return pixels;
    }

    private static byte[] CreateSolidPixels(
        int width,
        int height,
        byte value,
        byte alpha)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = alpha;
        }

        return pixels;
    }

    private static void SetPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte value,
        byte alpha)
    {
        int index = ((y * width) + x) * 4;
        pixels[index] = value;
        pixels[index + 1] = value;
        pixels[index + 2] = value;
        pixels[index + 3] = alpha;
    }

    private static void AssertPremultiplied(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            Assert.True(pixels[index] <= alpha);
            Assert.True(pixels[index + 1] <= alpha);
            Assert.True(pixels[index + 2] <= alpha);
        }
    }

    private static string[] Rotate(IReadOnlyList<string> rows, int rotations)
    {
        string[] result = rows.ToArray();
        for (int rotation = 0; rotation < rotations; rotation++)
        {
            int size = result.Length;
            result = Enumerable.Range(0, size)
                .Select(y => new string(Enumerable.Range(0, size)
                    .Select(x => result[size - x - 1][y])
                    .ToArray()))
                .ToArray();
        }

        return result;
    }

    private static string[] Mirror(IReadOnlyList<string> rows)
    {
        return rows.Select(row => new string(row.Reverse().ToArray())).ToArray();
    }

    private static string[] Pad(IReadOnlyList<string> rows)
    {
        string blank = new('W', rows.Count + 4);
        return
        [
            blank,
            blank,
            .. rows.Select(row => $"WW{row}WW"),
            blank,
            blank,
        ];
    }

    private static void AssertPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte expected)
    {
        int index = ((y * width) + x) * 4;
        Assert.Equal(expected, pixels[index]);
        Assert.Equal(expected, pixels[index + 1]);
        Assert.Equal(expected, pixels[index + 2]);
        Assert.Equal(0xFF, pixels[index + 3]);
    }

    private static void AssertPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte expected,
        byte expectedAlpha)
    {
        int index = ((y * width) + x) * 4;
        Assert.Equal(expected, pixels[index]);
        Assert.Equal(expected, pixels[index + 1]);
        Assert.Equal(expected, pixels[index + 2]);
        Assert.Equal(expectedAlpha, pixels[index + 3]);
    }
}
