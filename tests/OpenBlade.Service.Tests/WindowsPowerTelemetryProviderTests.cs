using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsPowerTelemetryProviderTests
{
    [Fact]
    public void Capture_PreservesPowerTelemetryAndPublishesGpuTemperature()
    {
        using var gpu = new FakeGpuTemperatureSource { TemperatureCelsius = 49 };
        var provider = new WindowsPowerTelemetryProvider(
            gpu,
            () => new PowerTelemetry(PowerSource.PluggedIn, 73));

        TelemetrySnapshot telemetry = provider.Capture();

        Assert.Equal(PowerSource.PluggedIn, telemetry.PowerSource);
        Assert.Equal(73, telemetry.BatteryPercent);
        Assert.Equal(49, telemetry.GpuTemperatureCelsius);
        Assert.Null(telemetry.CpuTemperatureCelsius);
        Assert.Null(telemetry.FanRpm);
    }

    [Fact]
    public void Capture_GpuExceptionPreservesPowerTelemetryAndReturnsUnavailableTemperature()
    {
        using var gpu = new FakeGpuTemperatureSource { Throws = true };
        var provider = new WindowsPowerTelemetryProvider(
            gpu,
            () => new PowerTelemetry(PowerSource.Battery, 61));

        Exception? exception = Record.Exception(provider.Capture);
        TelemetrySnapshot telemetry = provider.Capture();

        Assert.Null(exception);
        Assert.Equal(PowerSource.Battery, telemetry.PowerSource);
        Assert.Equal(61, telemetry.BatteryPercent);
        Assert.Null(telemetry.GpuTemperatureCelsius);
    }

    [Theory]
    [InlineData(1, 65, PowerSource.PluggedIn, 65)]
    [InlineData(0, 42, PowerSource.Battery, 42)]
    [InlineData(0, 255, PowerSource.Battery, null)]
    public void Map_ConvertsWindowsPowerStatus(
        byte acLineStatus,
        byte batteryPercent,
        PowerSource expectedSource,
        int? expectedPercent)
    {
        PowerTelemetry telemetry = WindowsPowerTelemetryProvider.Map(acLineStatus, batteryPercent);

        Assert.Equal(expectedSource, telemetry.PowerSource);
        Assert.Equal(expectedPercent, telemetry.BatteryPercent);
    }

    [Fact]
    public void Map_RejectsUnknownAcLineStatus()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsPowerTelemetryProvider.Map(byte.MaxValue, 50));
    }

    private sealed class FakeGpuTemperatureSource : IGpuTemperatureSource
    {
        public int? TemperatureCelsius { get; init; }
        public bool Throws { get; init; }

        public int? CaptureCelsius()
        {
            if (Throws)
            {
                throw new InvalidOperationException("Injected GPU telemetry failure.");
            }

            return TemperatureCelsius;
        }

        public void Dispose()
        {
        }
    }
}
