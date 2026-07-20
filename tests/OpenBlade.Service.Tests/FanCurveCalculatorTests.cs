using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanCurveCalculatorTests
{
    [Fact]
    public void SeparateCurvesUseTheirOwnSensors()
    {
        ManualFanCurve curve = new(
            [new(40, 2000), new(80, 4000)],
            [new(40, 2000), new(80, 5000)],
            useHigherTemperatureForBoth: false);

        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            curve,
            new FanTemperatureReadings(CpuCelsius: 60, GpuCelsius: 70));

        Assert.Equal(new FanTargetSpeeds(3000, 4300, FailSafe: false), targets);
    }

    [Fact]
    public void HotterSensorOptionFeedsBothCurves()
    {
        ManualFanCurve curve = new(
            [new(40, 2000), new(80, 4000)],
            [new(40, 2000), new(80, 5000)],
            useHigherTemperatureForBoth: true);

        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            curve,
            new FanTemperatureReadings(CpuCelsius: 60, GpuCelsius: 70));

        Assert.Equal(new FanTargetSpeeds(3500, 4300, FailSafe: false), targets);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(60, null)]
    [InlineData(95, 40)]
    [InlineData(40, 87)]
    public void MissingOrCriticalTemperatureForcesMaximum(
        int? cpu,
        int? gpu)
    {
        Assert.Equal(
            new FanTargetSpeeds(5400, 5400, FailSafe: true),
            FanCurveCalculator.Calculate(
                ManualFanCurve.Default,
                new FanTemperatureReadings(cpu, gpu)));
    }

    [Theory]
    [InlineData(2732, 0)]
    [InlineData(3010, 28)]
    [InlineData(3182, 45)]
    [InlineData(3881, 115)]
    public void RazerThermalZoneConvertsTenthsKelvinToCelsius(
        double highPrecisionKelvin,
        int expectedCelsius)
    {
        Assert.Equal(
            expectedCelsius,
            SafeWindowsFanTemperatureSource.WindowsThermalZone
                .TryConvertHighPrecisionKelvin(highPrecisionKelvin));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2500)]
    [InlineData(3890)]
    public void RazerThermalZoneRejectsInvalidTemperature(double value)
    {
        Assert.Null(
            SafeWindowsFanTemperatureSource.WindowsThermalZone
                .TryConvertHighPrecisionKelvin(value));
    }
}
