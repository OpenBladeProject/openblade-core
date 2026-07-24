namespace OpenBlade.Contracts;

public static class PerformanceShortcutSelection
{
    public static IReadOnlyList<PerformancePreset> GetCycle(
        PowerSource powerSource,
        IReadOnlySet<PerformancePreset>? admitted)
    {
        PerformancePreset[] ordered = powerSource switch
        {
            PowerSource.PluggedIn =>
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            ],
            PowerSource.Battery =>
            [
                PerformancePreset.BatteryBalanced,
                PerformancePreset.BatterySaver,
            ],
            _ => [],
        };
        if (admitted is null || admitted.Count == 0)
        {
            return ordered;
        }

        return ordered.Where(admitted.Contains).ToArray();
    }

    public static bool TrySelectNext(
        PowerSource powerSource,
        IReadOnlySet<PerformancePreset>? admitted,
        PerformancePreset current,
        out PerformancePreset next)
    {
        IReadOnlyList<PerformancePreset> cycle = GetCycle(powerSource, admitted);
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
