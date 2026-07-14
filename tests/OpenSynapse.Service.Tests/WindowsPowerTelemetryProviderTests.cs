using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class WindowsPowerTelemetryProviderTests
{
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
}
