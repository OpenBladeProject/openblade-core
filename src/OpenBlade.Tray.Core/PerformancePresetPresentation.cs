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
    ];

    private static readonly PerformancePreset[] BatteryOrder =
    [
        PerformancePreset.BatteryBalanced,
        PerformancePreset.BatterySaver,
    ];

    public static string Label(PerformancePreset value) => value switch
    {
        PerformancePreset.Balanced => "Balanced",
        PerformancePreset.Silent => "Silent",
        PerformancePreset.Performance => "Performance",
        PerformancePreset.BatteryBalanced => "Balanced",
        PerformancePreset.BatterySaver => "Battery Saver",
        _ => value.ToString(),
    };

    public static bool IsAvailable(PowerSource source, PerformancePreset value) =>
        source == PowerSource.PluggedIn
            ? value is PerformancePreset.Balanced
                or PerformancePreset.Silent
                or PerformancePreset.Performance
            : value is PerformancePreset.BatteryBalanced or PerformancePreset.BatterySaver;

    public static IReadOnlyList<PerformancePresetOption> CreateOptions(
        PowerSource source,
        IReadOnlySet<PerformancePreset> admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        PerformancePreset[] order = source == PowerSource.PluggedIn
            ? PluggedInOrder
            : BatteryOrder;
        return order
            .Where(admitted.Contains)
            .Select(value => new PerformancePresetOption(value, Label(value)))
            .ToArray();
    }
}
