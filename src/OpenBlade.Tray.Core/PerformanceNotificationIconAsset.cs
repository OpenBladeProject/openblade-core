using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class PerformanceNotificationIconAsset
{
    private const string Directory = "Notifications";

    public static string? Resolve(
        PowerSource powerSource,
        PerformancePreset preset) =>
        (powerSource, preset) switch
        {
            (PowerSource.PluggedIn, PerformancePreset.Balanced) =>
                Asset("ac-balanced"),
            (PowerSource.PluggedIn, PerformancePreset.Performance) =>
                Asset("ac-performance"),
            (PowerSource.PluggedIn, PerformancePreset.Silent) =>
                Asset("ac-silent"),
            (PowerSource.PluggedIn, PerformancePreset.Custom) =>
                Asset("ac-custom"),
            (PowerSource.UsbC, PerformancePreset.Balanced) =>
                Asset("usb-c-balanced"),
            (PowerSource.Battery, PerformancePreset.Balanced
                or PerformancePreset.BatteryBalanced) =>
                Asset("battery-balanced"),
            (PowerSource.Battery, PerformancePreset.BatterySaver) =>
                Asset("battery-saver"),
            (_, PerformancePreset.BatteryBalanced) =>
                Asset("battery-balanced"),
            (_, PerformancePreset.BatterySaver) =>
                Asset("battery-saver"),
            _ => null,
        };

    private static string Asset(string key) =>
        Path.Combine(Directory, $"{key}-256.png");
}
