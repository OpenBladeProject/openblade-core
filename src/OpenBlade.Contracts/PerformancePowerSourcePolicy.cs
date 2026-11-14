namespace OpenBlade.Contracts;

/// <summary>Distinguishes confirmed external power from battery and unknown observations.</summary>
public static class PowerSourcePolicy
{
    public static bool IsExternal(PowerSource source) =>
        source is PowerSource.PluggedIn or PowerSource.UsbC;
}

/// <summary>Power-source eligibility; model capability admission remains independent.</summary>
public static class PerformancePowerSourcePolicy
{
    public static bool IsAllowed(PowerSource source, PerformancePreset preset) =>
        Enum.IsDefined(preset) && (PowerSourcePolicy.IsExternal(source)
            || source == PowerSource.Battery
                && preset is PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced);
}
