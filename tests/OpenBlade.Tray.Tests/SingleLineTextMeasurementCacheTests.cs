using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class SingleLineTextMeasurementCacheTests
{
    [Fact]
    public void GetOrMeasure_CachesOneMeasurementForRepeatedLayoutEvents()
    {
        var cache = new SingleLineTextMeasurementCache();
        int measurementCount = 0;

        for (int index = 0; index < 15; index++)
        {
            double height = cache.GetOrMeasure(1.0, () =>
            {
                measurementCount++;
                return 20;
            });

            Assert.Equal(20, height);
        }

        Assert.Equal(1, measurementCount);
    }

    [Fact]
    public void GetOrMeasure_RemeasuresWhenTextScaleChanges()
    {
        var cache = new SingleLineTextMeasurementCache();
        int measurementCount = 0;

        Assert.Equal(20, cache.GetOrMeasure(1.0, Measure));
        Assert.Equal(20, cache.GetOrMeasure(1.0, Measure));
        Assert.Equal(24, cache.GetOrMeasure(1.2, Measure));
        Assert.Equal(2, measurementCount);

        double Measure()
        {
            measurementCount++;
            return measurementCount == 1 ? 20 : 24;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetOrMeasure_InvalidTextScaleReturnsNonfatalFallback(
        double textScaleFactor)
    {
        var cache = new SingleLineTextMeasurementCache();
        bool measured = false;

        double height = cache.GetOrMeasure(textScaleFactor, () =>
        {
            measured = true;
            return 20;
        });

        Assert.True(double.IsNaN(height));
        Assert.False(measured);
        Assert.False(SingleLineTextMeasurementCache.IsWrapped(40, height));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetOrMeasure_InvalidHeightReturnsUncachedNonfatalFallback(
        double invalidHeight)
    {
        var cache = new SingleLineTextMeasurementCache();
        int measurementCount = 0;

        double fallback = cache.GetOrMeasure(1.0, () =>
        {
            measurementCount++;
            return invalidHeight;
        });
        double recovered = cache.GetOrMeasure(1.0, () =>
        {
            measurementCount++;
            return 20;
        });

        Assert.True(double.IsNaN(fallback));
        Assert.False(SingleLineTextMeasurementCache.IsWrapped(40, fallback));
        Assert.Equal(20, recovered);
        Assert.Equal(2, measurementCount);
    }

    [Theory]
    [InlineData(20, 20, false)]
    [InlineData(20.5, 20, false)]
    [InlineData(20.51, 20, true)]
    [InlineData(40, 20, true)]
    public void IsWrapped_UsesMeasuredSingleLineHeight(
        double actualHeight,
        double singleLineHeight,
        bool expected) =>
        Assert.Equal(
            expected,
            SingleLineTextMeasurementCache.IsWrapped(actualHeight, singleLineHeight));
}
