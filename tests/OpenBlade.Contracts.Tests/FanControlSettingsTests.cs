using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class FanControlSettingsTests
{
    [Theory]
    [InlineData(1999)]
    [InlineData(2050)]
    [InlineData(5500)]
    public void FixedSpeedRejectsValuesOutsideCapturedRangeOrStep(int rpm)
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
