using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class FanControlSettingsTests
{
    [Fact]
    public void FanSpeedRange_ValidatesAndQuantizesExactDeviceBounds()
    {
        var range = new FanSpeedRange(2000, 5000, 100);

        Assert.True(range.IsWellFormed);
        Assert.True(range.Supports(2000));
        Assert.True(range.Supports(3800));
        Assert.True(range.Supports(5000));
        Assert.False(range.Supports(5100));
        Assert.Equal(2000, range.Quantize(1950));
        Assert.Equal(3800, range.Quantize(3760));
        Assert.Equal(5000, range.Quantize(5400));
    }

    [Fact]
    public void SourceReviewed5500RpmValuesRetainStandardAndLegacyMaximumBounds()
    {
        var range = new FanSpeedRange(4300, 5500, 100);
        Assert.True(range.IsWellFormed);
        Assert.True(range.Supports(5500));
        Assert.False(FanSpeedRange.Standard.Supports(5500));
        Assert.Equal(5400, FanSpeedRange.Standard.MaximumRpm);
        Assert.Equal(5400, FanControlSettings.FromLegacy(FanMode.Maximum).FixedSpeedRpm);
        Assert.Equal(5500, new FanControlSettings(FanMode.Fixed, 5500, ManualFanCurve.Default).FixedSpeedRpm);
        var points = new[] { new FanCurvePoint(40, 4300), new FanCurvePoint(90, 5500) };
        var curve = new ManualFanCurve(points, points, true);
        Assert.Equal(5500, curve.InterpolateCpu(89));
        Assert.False((range with { MaximumRpm = 5600 }).IsWellFormed);
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2050)]
    [InlineData(5600)]
    public void FixedSpeedRejectsValuesOutsideRepresentableRangeOrStep(int rpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanControlSettings(
            FanMode.Fixed,
            rpm,
            ManualFanCurve.Default));
    }

    [Theory]
    [InlineData(40, 2000)]
    [InlineData(55, 2400)]
    [InlineData(75, 3900)]
    [InlineData(95, 5400)]
    public void CpuCurveInterpolatesAndQuantizesToFirmwareStep(
        int temperature,
        int expectedRpm)
    {
        Assert.Equal(expectedRpm, ManualFanCurve.Default.InterpolateCpu(temperature));
    }

    [Fact]
    public void EquivalentCurvesUseStructuralEquality()
    {
        var copy = new ManualFanCurve(
            ManualFanCurve.Default.Cpu.Select(point =>
                new FanCurvePoint(point.TemperatureCelsius, point.SpeedRpm)).ToArray(),
            ManualFanCurve.Default.Gpu.Select(point =>
                new FanCurvePoint(point.TemperatureCelsius, point.SpeedRpm)).ToArray(),
            useHigherTemperatureForBoth: true);

        Assert.Equal(ManualFanCurve.Default, copy);
        Assert.Equal(
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default),
            new FanControlSettings(FanMode.Fixed, 3700, copy));
    }
}
