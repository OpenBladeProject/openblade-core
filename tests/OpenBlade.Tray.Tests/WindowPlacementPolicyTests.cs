namespace OpenBlade.Tray.Tests;

public sealed class WindowPlacementPolicyTests
{
    [Theory]
    [InlineData(600, 400, 96, 720, 540)]
    [InlineData(900, 600, 96, 900, 600)]
    [InlineData(900, 700, 144, 1080, 810)]
    public void EnforceMinimumSize_ClampsEachDimensionInDpiAwarePixels(
        int width,
        int height,
        int dpi,
        int expectedWidth,
        int expectedHeight)
    {
        PixelRect result = WindowPlacementPolicy.EnforceMinimumSize(
            new PixelRect(10, 20, width, height),
            720,
            540,
            dpi);

        Assert.Equal(new PixelRect(10, 20, expectedWidth, expectedHeight), result);
    }

    [Fact]
    public void Center_ConvertsDipsThroughDpiAndCentersInsideWorkArea()
    {
        PixelRect result = WindowPlacementPolicy.Center(
            520,
            760,
            144,
            new PixelRect(100, 50, 1800, 1200));

        Assert.Equal(new PixelRect(610, 80, 780, 1140), result);
    }

    [Theory]
    [InlineData(96, 520, 760)]
    [InlineData(120, 650, 950)]
    [InlineData(144, 780, 1140)]
    [InlineData(192, 1040, 1520)]
    public void Center_ScalesAtCommonDpiValues(int dpi, int expectedWidth, int expectedHeight)
    {
        PixelRect result = WindowPlacementPolicy.Center(
            520,
            760,
            dpi,
            new PixelRect(0, 0, 4000, 4000));

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void Center_RoundsMidpointsAwayFromZero()
    {
        PixelRect result = WindowPlacementPolicy.Center(
            1,
            1,
            144,
            new PixelRect(0, 0, 10, 10));

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
    }

    [Fact]
    public void Center_PositiveDipThatRoundsBelowOnePixel_RemainsUsable()
    {
        PixelRect result = WindowPlacementPolicy.Center(
            0.1,
            0.1,
            96,
            new PixelRect(0, 0, 10, 10));

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Theory]
    [InlineData(2000, 200, 100, 200, 1000, 200)]
    [InlineData(200, 2000, 500, 50, 200, 500)]
    [InlineData(2000, 2000, 100, 50, 1000, 500)]
    public void Center_ClampsEachDimensionIndependently(
        double widthDip,
        double heightDip,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        PixelRect result = WindowPlacementPolicy.Center(
            widthDip,
            heightDip,
            96,
            new PixelRect(100, 50, 1000, 500));

        Assert.Equal(
            new PixelRect(expectedX, expectedY, expectedWidth, expectedHeight),
            result);
    }

    [Theory]
    [InlineData(0, 100, 96, 100, 100)]
    [InlineData(-1, 100, 96, 100, 100)]
    [InlineData(double.NaN, 100, 96, 100, 100)]
    [InlineData(double.PositiveInfinity, 100, 96, 100, 100)]
    [InlineData(100, 0, 96, 100, 100)]
    [InlineData(100, -1, 96, 100, 100)]
    [InlineData(100, double.NegativeInfinity, 96, 100, 100)]
    [InlineData(100, 100, 0, 100, 100)]
    [InlineData(100, 100, -1, 100, 100)]
    [InlineData(100, 100, 96, 0, 100)]
    [InlineData(100, 100, 96, -1, 100)]
    [InlineData(100, 100, 96, 100, 0)]
    [InlineData(100, 100, 96, 100, -1)]
    public void Center_RejectsInvalidGeometry(
        double widthDip,
        double heightDip,
        int dpi,
        int workAreaWidth,
        int workAreaHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowPlacementPolicy.Center(
                widthDip,
                heightDip,
                dpi,
                new PixelRect(-500, -200, workAreaWidth, workAreaHeight)));

    [Fact]
    public void Center_RejectsScaledDimensionOverflow() =>
        Assert.Throws<OverflowException>(() =>
            WindowPlacementPolicy.Center(
                double.MaxValue,
                100,
                int.MaxValue,
                new PixelRect(0, 0, 100, 100)));

    [Fact]
    public void Center_RejectsCoordinateOverflowInsteadOfWrapping() =>
        Assert.Throws<OverflowException>(() =>
            WindowPlacementPolicy.Center(
                50,
                50,
                96,
                new PixelRect(int.MaxValue, int.MaxValue, 100, 100)));
}
