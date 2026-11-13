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
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Performance
                or PerformancePreset.Turbo or PerformancePreset.MaximumPower
                or PerformancePreset.Gaming or PerformancePreset.Creator) =>
                Asset("ac-performance"),
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Silent) =>
                Asset("ac-silent"),
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.Custom) =>
                Asset("ac-custom"),
            (PowerSource.UsbC, PerformancePreset.Balanced) =>
                Asset("usb-c-balanced"),
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.BatteryBalanced) =>
                Asset("ac-balanced"),
            (PowerSource.PluggedIn or PowerSource.UsbC, PerformancePreset.BatterySaver) =>
                Asset("ac-silent"),
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
