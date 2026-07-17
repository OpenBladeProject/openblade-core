namespace OpenBlade.Contracts;

public enum PowerSource
{
    PluggedIn,
    Battery,
}

public sealed record DesiredDeviceState(
    ResolvedSetting<PerformancePreset> Performance,
    ResolvedSetting<FanMode> Fans,
    ResolvedSetting<LightingSettings> Lighting,
    DesiredBatteryState Battery);

public sealed record LiveReadings(
    int? BatteryPowerFlowMilliwatts,
    int? Fan1Rpm,
    int? Fan2Rpm);
