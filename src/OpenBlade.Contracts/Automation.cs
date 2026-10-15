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
    FullPower330W = 3,
    UsbCPower45W = 4,
    UsbCPower60W = 5,
    UsbCPower65W = 6,
    UsbCPower80W = 7,
    UsbCPower90W = 8,
    Battery = 9,
    ExperimentalExternal = 10,
    SufficientExternal = 11,
    LimitedExternal = 12,
}

public sealed record DesiredDeviceState(
    ResolvedSetting<PerformancePreset> Performance,
    ResolvedSetting<FanMode> Fans,
    ResolvedSetting<LightingSettings> Lighting,
    DesiredBatteryState Battery)
{
    public CustomPerformanceSettings? CustomPerformance { get; init; }

    public GpuClockTuningSettings? GpuClockTuning { get; init; }

    public FanControlSettings? FanControl { get; init; }
}

public sealed record LiveReadings(
    int? BatteryPowerFlowMilliwatts,
    int? Fan1Rpm,
    int? Fan2Rpm,
    int? CpuTemperatureCelsius = null,
    int? GpuTemperatureCelsius = null);
