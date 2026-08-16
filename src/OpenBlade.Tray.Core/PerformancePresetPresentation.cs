using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record PerformancePresetOption(
    PerformancePreset Value,
    string Label)
{
    public override string ToString() => Label;
}

public static class PerformancePresetPresentation
{
    private static readonly PerformancePreset[] PluggedInOrder =
    [
        PerformancePreset.Balanced,
        PerformancePreset.Silent,
        PerformancePreset.Performance,
        PerformancePreset.Custom,
    ];

    private static readonly PerformancePreset[] BatteryOrder =
    [
        PerformancePreset.BatteryBalanced,
        PerformancePreset.BatterySaver,
    ];

    private static readonly PerformancePreset[] UsbCOrder =
    [
        PerformancePreset.Balanced,
    ];

    public static string Label(PerformancePreset value) => value switch
    {
        PerformancePreset.Balanced => TrayText.Get("Balanced"),
        PerformancePreset.Silent => TrayText.Get("Silent"),
        PerformancePreset.Performance => TrayText.Get("Performance"),
        PerformancePreset.BatteryBalanced => TrayText.Get("Balanced"),
        PerformancePreset.BatterySaver => TrayText.Get("Battery Saver"),
        PerformancePreset.Custom => TrayText.Get("Custom"),
        _ => value.ToString(),
    };

    public static bool IsAvailable(PowerSource source, PerformancePreset value) => source switch
    {
        PowerSource.PluggedIn => value is PerformancePreset.Balanced
            or PerformancePreset.Silent
            or PerformancePreset.Performance
            or PerformancePreset.Custom,
        PowerSource.Battery => value is PerformancePreset.BatteryBalanced
            or PerformancePreset.BatterySaver,
        PowerSource.UsbC => value == PerformancePreset.Balanced,
        PowerSource.ExternalUnknown => false,
        _ => false,
    };

    public static IReadOnlyList<PerformancePresetOption> CreateOptions(
        PowerSource source,
        IReadOnlySet<PerformancePreset> admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        PerformancePreset[] order = source switch
        {
            PowerSource.PluggedIn => PluggedInOrder,
            PowerSource.Battery => BatteryOrder,
            PowerSource.UsbC => UsbCOrder,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };
        return order
            .Where(admitted.Contains)
            .Select(value => new PerformancePresetOption(value, Label(value)))
            .ToArray();
    }
}
