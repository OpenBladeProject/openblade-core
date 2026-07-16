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

public sealed record TelemetrySnapshot(
    DateTimeOffset CapturedAt,
    PowerSource PowerSource,
    int? BatteryPercent,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    int? FanRpm);
