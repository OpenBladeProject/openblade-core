namespace OpenBlade.Contracts;

public sealed record DeviceCapabilities(
    IReadOnlySet<PerformancePreset> PerformancePresets,
    IReadOnlySet<FanMode> FanModes,
    IReadOnlySet<LightingEffect> LightingEffects,
    int MinimumChargeLimit,
    int MaximumChargeLimit,
    int ChargeLimitStep)
{
    public static DeviceCapabilities Unavailable() => new(
        new HashSet<PerformancePreset>(),
        new HashSet<FanMode>(),
        new HashSet<LightingEffect>(),
        MinimumChargeLimit: 0,
        MaximumChargeLimit: 0,
        ChargeLimitStep: 0);

    public static DeviceCapabilities SafeTargetDefaults() => new(
        new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>()),
        new HashSet<FanMode>(Enum.GetValues<FanMode>()),
        new HashSet<LightingEffect> { LightingEffect.Off },
        MinimumChargeLimit: 50,
        MaximumChargeLimit: 80,
        ChargeLimitStep: 5);
}

public sealed record ConfirmedDeviceState(
    PerformancePreset? Performance,
    FanMode? Fans,
    BatteryPolicy? Battery,
    LightingSettings? Lighting);

public enum ControlResultStatus
{
    Applied,
    Unchanged,
    Blocked,
    Failed,
}

public sealed record ControlResult(ControlResultStatus Status, string Message);

public sealed record SubsystemApplyResults(
    ControlResult Performance,
    ControlResult Fans,
    ControlResult Battery,
    ControlResult Lighting);
