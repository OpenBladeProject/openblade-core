using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsPowerStateProviderTests
{
    [Theory]
    [InlineData(1, PowerSource.ExternalUnknown)]
    [InlineData(0, PowerSource.Battery)]
    public void MapPowerSource_ConvertsWindowsPowerStatus(
        byte acLineStatus,
        PowerSource expected)
    {
        Assert.Equal(expected, WindowsPowerStateProvider.MapPowerSource(acLineStatus));
    }

    [Fact]
    public void MapPowerSource_RejectsUnknownStatus()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsPowerStateProvider.MapPowerSource(byte.MaxValue));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(38_400u, 38_400)]
    [InlineData(0xFFFFA240u, -24_000)]
    [InlineData(1_000_000u, 1_000_000)]
    [InlineData(0xFFF0BDC0u, -1_000_000)]
    public void MapBatteryPowerFlow_PreservesSignedNetRate(uint rawRate, int expected)
    {
        Assert.Equal(
            expected,
            WindowsPowerStateProvider.MapBatteryPowerFlow(
                batteryPresent: true,
                rawRate));
    }

    [Theory]
    [InlineData(false, 25_000u)]
    [InlineData(true, 0x80000000u)]
    [InlineData(true, 1_000_001u)]
    [InlineData(true, 0xFFF0BDBFu)]
    public void MapBatteryPowerFlow_RejectsMissingUnknownOrImplausibleRate(
        bool batteryPresent,
        uint rawRate)
    {
        Assert.Null(WindowsPowerStateProvider.MapBatteryPowerFlow(batteryPresent, rawRate));
    }

    [Fact]
    public void BatteryPowerFlowFailure_ReturnsUnavailableWithoutAffectingPowerSource()
    {
        var provider = new WindowsPowerStateProvider(
            () => PowerSource.PluggedIn,
            () => throw new InvalidOperationException("native failure"));

        Assert.Equal(PowerSource.PluggedIn, provider.CapturePowerSource());
        Assert.Null(provider.CaptureBatteryPowerFlowMilliwatts());
    }

    [Fact]
    public void MapBatteryHealth_AggregatesValidAbsoluteCapacities()
    {
        BatteryHealthStatus? health = WindowsPowerStateProvider.MapBatteryHealth(
        [
            (80_000, 72_000),
            (15_200, 14_440),
        ]);

        Assert.Equal(new BatteryHealthStatus(95_200, 86_440), health);
    }

    [Fact]
    public void MapBatteryHealth_IgnoresUnknownEntriesAndRejectsImplausibleTotals()
    {
        Assert.Equal(
            new BatteryHealthStatus(95_200, 90_440),
            WindowsPowerStateProvider.MapBatteryHealth(
            [
                (0, 0),
                (uint.MaxValue, uint.MaxValue),
                (95_200, 90_440),
            ]));
        Assert.Null(WindowsPowerStateProvider.MapBatteryHealth([(95_200, 200_000)]));
        Assert.Null(WindowsPowerStateProvider.MapBatteryHealth([]));
    }

    [Fact]
    public void BatteryHealthFailure_ReturnsUnavailable()
    {
        var history = new DiagnosticHistory();
        var provider = new WindowsPowerStateProvider(
            () => PowerSource.PluggedIn,
            () => null,
            () => throw new InvalidOperationException("native failure"),
            history.CreateLogger("OpenBlade.Service.WindowsPowerStateProvider"));

        Assert.Null(provider.CaptureBatteryHealth());
        Assert.Contains(
            history.Snapshot().LogLines,
            line => line.Contains(
                "Warning OpenBlade.Service.WindowsPowerStateProvider: Windows battery health capture failed; capacity is unavailable.",
                StringComparison.Ordinal));
    }
}
