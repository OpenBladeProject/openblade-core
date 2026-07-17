namespace OpenBlade.Contracts;

public enum PowerSource
{
    PluggedIn = 0,
    Battery = 1,
    UsbC = 2,
    ExternalUnknown = 3,
}

public enum AdapterPowerState
{
    Unknown = 0,
    UsbCPower100W = 1,
    FullPower280W = 2,
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
