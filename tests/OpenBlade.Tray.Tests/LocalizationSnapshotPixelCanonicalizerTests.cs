namespace OpenBlade.Tray.Tests;

public sealed class LocalizationSnapshotPixelCanonicalizerTests
{
    private const byte Selected = 0xF7;
    private const byte Background = 0xFF;

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
}
