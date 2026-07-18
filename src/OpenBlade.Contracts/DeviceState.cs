namespace OpenBlade.Contracts;

public enum DeviceSubsystem
{
    Performance,
    Fans,
    Battery,
    Lighting,
}

public sealed record DeviceCapabilities(
    IReadOnlySet<PerformancePreset> PerformancePresets,
    IReadOnlySet<FanMode> FanModes,
    IReadOnlySet<LightingEffect> LightingEffects,
    IReadOnlySet<int> ChargeLimits,
    bool SupportsBatteryProtectionOff,
    bool SupportsTemporaryFullCharge)
{
    public static DeviceCapabilities Unavailable() => new(
        new HashSet<PerformancePreset>(),
        new HashSet<FanMode>(),
        new HashSet<LightingEffect>(),
        new HashSet<int>(),
        SupportsBatteryProtectionOff: false,
        SupportsTemporaryFullCharge: false);

    public static DeviceCapabilities SafeTargetDefaults() => new(
        new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>()),
        new HashSet<FanMode>(Enum.GetValues<FanMode>()),
        new HashSet<LightingEffect> { LightingEffect.Off },
        new HashSet<int>(),
        SupportsBatteryProtectionOff: false,
        SupportsTemporaryFullCharge: false);
}

public sealed record ConfirmedDeviceState(
    PerformancePreset? Performance,
    FanMode? Fans,
    ConfirmedBatteryState? Battery,
    LightingSettings? Lighting);

public sealed record FirmwareVersions(
    string BiosVersion,
    string EcVersion,
    string McuVersion,
    string VbiosVersion)
{
    public static FirmwareVersions Unavailable() => new(
        "Unavailable",
        "Unavailable",
        "Unavailable",
        "Unavailable");
}

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
