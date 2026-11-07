using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PerformanceNotificationIconAssetTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Balanced, "ac-balanced")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance, "ac-performance")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Turbo, "ac-performance")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.MaximumPower, "ac-performance")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Gaming, "ac-performance")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Creator, "ac-performance")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Silent, "ac-silent")]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Custom, "ac-custom")]
    [InlineData(PowerSource.UsbC, PerformancePreset.Balanced, "usb-c-balanced")]
    [InlineData(PowerSource.Battery, PerformancePreset.BatteryBalanced, "battery-balanced")]
    [InlineData(PowerSource.Battery, PerformancePreset.BatterySaver, "battery-saver")]
    public void Resolve_ReturnsExpectedConfirmedModeAsset(
        PowerSource powerSource,
        PerformancePreset preset,
        string assetKey)
    {
        Assert.Equal(
            Path.Combine("Notifications", $"{assetKey}-256.png"),
            PerformanceNotificationIconAsset.Resolve(powerSource, preset));
    }

    [Theory]
    [InlineData(PowerSource.UsbC, PerformancePreset.Performance)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Turbo)]
    [InlineData(PowerSource.Battery, PerformancePreset.MaximumPower)]
    [InlineData(PowerSource.Battery, PerformancePreset.Gaming)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Creator)]
    [InlineData(PowerSource.ExternalUnknown, PerformancePreset.Balanced)]
    public void Resolve_ReturnsNullForUnsupportedCombination(
        PowerSource powerSource,
        PerformancePreset preset)
    {
        Assert.Null(
            PerformanceNotificationIconAsset.Resolve(powerSource, preset));
    }

    [Theory]
    [InlineData(PerformancePreset.BatteryBalanced, "battery-balanced")]
    [InlineData(PerformancePreset.BatterySaver, "battery-saver")]
    public void Resolve_UsesBatterySpecificPresetWhenPowerSourceIsStale(
        PerformancePreset preset,
        string assetKey)
    {
        Assert.Equal(
            Path.Combine("Notifications", $"{assetKey}-256.png"),
            PerformanceNotificationIconAsset.Resolve(
                PowerSource.ExternalUnknown,
                preset));
    }
}
