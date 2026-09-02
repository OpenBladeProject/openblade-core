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

    [Fact]
    public void MissingTemperatureUsesTheAdmittedDeviceMaximum()
    {
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            new FanTemperatureReadings(null, null),
            maximumRpm: 5000);

        Assert.Equal(new FanTargetSpeeds(5000, 5000, FailSafe: true), targets);
    }

    [Fact]
    public void InactiveGpuUsesCpuTemperatureWithoutFailSafe()
    {
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            new FanTemperatureReadings(
                CpuCelsius: 60,
                GpuCelsius: null,
                GpuInactive: true));

        Assert.False(targets.FailSafe);
        Assert.Equal(ManualFanCurve.Default.InterpolateCpu(60), targets.Fan1Rpm);
        Assert.Equal(ManualFanCurve.Default.InterpolateGpu(60), targets.Fan2Rpm);
    }

    [Fact]
    public async Task InactiveGpuDoesNotInvokeNvmlTemperatureReader()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: false),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: true,
            includeGpu: true,
            CancellationToken.None);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.True(readings.GpuInactive);
        Assert.Equal(0, gpuReads);
    }

    [Fact]
    public async Task ActiveGpuInvokesNvmlTemperatureReader()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: true),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: false,
            includeGpu: true,
            CancellationToken.None);

        Assert.Null(readings.CpuCelsius);
        Assert.Equal(52, readings.GpuCelsius);
        Assert.False(readings.GpuInactive);
        Assert.Equal(1, gpuReads);
    }

    [Fact]
    public async Task UnknownGpuDoesNotInvokeNvmlAndRetainsFanFailSafe()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: null),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: true,
            includeGpu: true,
            CancellationToken.None);
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            readings,
            maximumRpm: 5000);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.False(readings.GpuInactive);
        Assert.Equal(0, gpuReads);
        Assert.Equal(new FanTargetSpeeds(5000, 5000, FailSafe: true), targets);
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

    [Fact]
    public void RazerThermalZoneRetainsBothExactModelCounterPaths()
    {
        Assert.Equal(
            [
                @"\Thermal Zone Information(\_SB.PCI0.SBRG.EC0.TZRZ)\High Precision Temperature",
                @"\Thermal Zone Information(\_SB.PC00.LPCB.EC0.TZRZ)\High Precision Temperature",
            ],
            SafeWindowsFanTemperatureSource.WindowsThermalZone.RazerEcCounterPaths);
    }

    private sealed record FixedGpuActivitySource(bool? IsActive) : IGpuActivitySource
    {
        bool? IGpuActivitySource.IsActive(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return IsActive;
        }
    }
}
