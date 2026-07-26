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
    public IReadOnlySet<LogoLightingMode> LogoLightingModes { get; init; } =
        new HashSet<LogoLightingMode>();

    public IReadOnlySet<OneShotLaptopControl> OneShotLaptopControls { get; init; } =
        new HashSet<OneShotLaptopControl>();

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
    LightingSettings? Lighting)
{
    public CustomPerformanceSettings? CustomPerformance { get; init; }

    public GpuClockOffsetCapabilities? GpuClockOffsets { get; init; }

    public LogoLightingMode? LogoLighting { get; init; }
}

public sealed record GpuClockOffsetRange(
    int MinimumMegahertz,
    int MaximumMegahertz,
    int CurrentMegahertz)
{
    public bool IsWellFormed =>
        MinimumMegahertz >= GpuClockOffsetSettings.MinimumAbsoluteOffsetMegahertz
        && MaximumMegahertz <= GpuClockOffsetSettings.MaximumAbsoluteOffsetMegahertz
        && MinimumMegahertz <= MaximumMegahertz
        && CurrentMegahertz >= MinimumMegahertz
        && CurrentMegahertz <= MaximumMegahertz;
}

public sealed record GpuClockOffsetCapabilities(
    string DeviceName,
    GpuClockOffsetRange Core,
    GpuClockOffsetRange Memory)
{
    public GpuClockOffsetSettings Current => new(
        Core.CurrentMegahertz,
        Memory.CurrentMegahertz);

    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(DeviceName)
        && DeviceName.Length <= 128
        && Core is not null
        && Core.IsWellFormed
        && Memory is not null
        && Memory.IsWellFormed;

    public bool Supports(GpuClockOffsetSettings settings) =>
        GpuClockOffsetSettings.IsWellFormed(settings)
        && settings.CoreMegahertz >= Core.MinimumMegahertz
        && settings.CoreMegahertz <= Core.MaximumMegahertz
        && settings.MemoryMegahertz >= Memory.MinimumMegahertz
        && settings.MemoryMegahertz <= Memory.MaximumMegahertz;
}

public sealed record FirmwareVersions(
    string BiosVersion,
    string EcVersion,
    string McuVersion,
    string VbiosVersion,
    string ModelNumber = "Unavailable")
{
    public static FirmwareVersions Unavailable() => new(
        "Unavailable",
        "Unavailable",
        "Unavailable",
        "Unavailable",
        "Unavailable");
}

public sealed record BatteryHealthStatus(
    int DesignCapacityMilliwattHours,
    int FullChargeCapacityMilliwattHours);

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
