using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record PerformancePresetOption(
    PerformancePreset Value,
    string Label)
{
    public override string ToString() => Label;
}

public sealed record LaptopPerformanceOption(
    PerformancePreset? Value,
    string Label)
{
    public bool IsHyperBoost => Value is null;

    public override string ToString() => Label;
}

public static class PerformancePresetPresentation
{
    private static readonly PerformancePreset[] ExternalOrder =
    [
        PerformancePreset.Balanced,
        PerformancePreset.Silent,
        PerformancePreset.Performance,
        PerformancePreset.Turbo,
        PerformancePreset.MaximumPower,
        PerformancePreset.Gaming,
        PerformancePreset.Creator,
        PerformancePreset.Custom,
        PerformancePreset.BatteryBalanced,
        PerformancePreset.BatterySaver,
    ];

    private static readonly PerformancePreset[] BatteryOrder =
    [
        PerformancePreset.BatteryBalanced,
        PerformancePreset.BatterySaver,
    ];

    public static string Label(PerformancePreset value) => value switch
    {
        PerformancePreset.Balanced => TrayText.Get("Balanced"),
        PerformancePreset.Silent => TrayText.Get("Silent"),
        PerformancePreset.Performance => TrayText.Get("Performance"),
        PerformancePreset.Turbo => TrayText.Get("Turbo"),
        PerformancePreset.MaximumPower => TrayText.Get("Maximum Power"),
        PerformancePreset.Gaming => TrayText.Get("Gaming"),
        PerformancePreset.Creator => TrayText.Get("Creator"),
        PerformancePreset.BatteryBalanced => TrayText.Get("Balanced"),
        PerformancePreset.BatterySaver => TrayText.Get("Battery Saver"),
        PerformancePreset.Custom => TrayText.Get("Custom"),
        _ => value.ToString(),
    };

    public static string Label(PerformancePreset value, PowerSource source) =>
        value == PerformancePreset.BatteryBalanced && PowerSourcePolicy.IsExternal(source)
            ? $"{Label(value)} ({TrayText.Get("Battery")})"
            : Label(value);

    public static bool IsAvailable(PowerSource source, PerformancePreset value) =>
        PerformancePowerSourcePolicy.IsAllowed(source, value);

    public static IReadOnlyList<PerformancePresetOption> CreateOptions(
        PowerSource source,
        IReadOnlySet<PerformancePreset> admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        PerformancePreset[] order = source switch
        {
            PowerSource.PluggedIn or PowerSource.UsbC => ExternalOrder,
            PowerSource.Battery => BatteryOrder,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };
        return order
            .Where(admitted.Contains)
            .Select(value => new PerformancePresetOption(value, Label(value, source)))
            .ToArray();
    }

    public static IReadOnlyList<LaptopPerformanceOption> CreateLaptopOptions(
        IReadOnlyList<PerformancePresetOption> presets,
        bool includeHyperBoost)
    {
        ArgumentNullException.ThrowIfNull(presets);
        IEnumerable<LaptopPerformanceOption> options = presets.Select(
            option => new LaptopPerformanceOption(option.Value, option.Label));
        if (includeHyperBoost)
        {
            options = options.Append(new LaptopPerformanceOption(
                Value: null,
                TrayText.Get("HyperBoost")));
        }

        return options.ToArray();
    }
}
