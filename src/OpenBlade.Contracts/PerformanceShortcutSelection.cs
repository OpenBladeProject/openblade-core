namespace OpenBlade.Contracts;

public static class PerformanceShortcutSelection
{
    private static readonly PerformancePreset[] OrdinaryOrder =
    [
        PerformancePreset.Balanced,
        PerformancePreset.Silent,
        PerformancePreset.Performance,
        PerformancePreset.Turbo,
        PerformancePreset.MaximumPower,
        PerformancePreset.Gaming,
        PerformancePreset.Creator,
        PerformancePreset.Custom,
    ];

    private static readonly PerformancePreset[] ExternalOrder =
        [.. OrdinaryOrder, PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver];

    public static IReadOnlyList<PerformancePreset> GetCycle(
        PowerSource powerSource,
        IReadOnlySet<PerformancePreset>? admitted)
    {
        PerformancePreset[] ordered = powerSource switch
        {
            PowerSource.PluggedIn or PowerSource.UsbC => ExternalOrder,
            PowerSource.Battery =>
            [
                PerformancePreset.BatteryBalanced,
                PerformancePreset.BatterySaver,
            ],
            _ => [],
        };
        if (admitted is null || admitted.Count == 0)
        {
            // Unverified/default capabilities retain the established cycle.
            return ordered.Where(preset => preset is not
                (PerformancePreset.Turbo or PerformancePreset.MaximumPower
                    or PerformancePreset.Gaming or PerformancePreset.Creator)
                && (powerSource == PowerSource.Battery || preset is not
                    (PerformancePreset.BatteryBalanced or PerformancePreset.BatterySaver))).ToArray();
        }

        return ordered.Where(admitted.Contains).ToArray();
    }

    // The caller admits the physical supply and explicit action. This selector
    // neither classifies an adapter nor selects a saved power-source profile.
    public static IReadOnlyList<PerformancePreset> GetOrdinaryCycle(
        IReadOnlySet<PerformancePreset>? admitted) =>
        admitted is null || admitted.Count == 0
            ? [] : OrdinaryOrder.Where(admitted.Contains).ToArray();

    public static bool TrySelectNext(
        PowerSource powerSource,
        IReadOnlySet<PerformancePreset>? admitted,
        PerformancePreset current,
        out PerformancePreset next) =>
        TrySelectNext(GetCycle(powerSource, admitted), current, out next);

    public static bool TrySelectNextOrdinary(
        IReadOnlySet<PerformancePreset>? admitted,
        PerformancePreset current,
        out PerformancePreset next) =>
        TrySelectNext(GetOrdinaryCycle(admitted), current, out next);

    private static bool TrySelectNext(
        IReadOnlyList<PerformancePreset> cycle,
        PerformancePreset current,
        out PerformancePreset next)
    {
        if (cycle.Count < 2)
        {
            next = default;
            return false;
        }

        int currentIndex = -1;
        for (int index = 0; index < cycle.Count; index++)
        {
            if (cycle[index] == current)
            {
                currentIndex = index;
                break;
            }
        }

        next = cycle[(currentIndex + 1 + cycle.Count) % cycle.Count];
        return true;
    }
}
