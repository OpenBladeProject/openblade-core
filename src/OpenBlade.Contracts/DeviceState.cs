namespace OpenBlade.Contracts;

public enum DeviceSubsystem
{
    Performance,
    Fans,
    Battery,
    Lighting,
}

public sealed record FanSpeedRange(
    int MinimumRpm,
    int MaximumRpm,
    int StepRpm)
{
    public bool IsWellFormed =>
        MinimumRpm >= FanControlSettings.MinimumSpeedRpm
        && MaximumRpm <= FanControlSettings.MaximumSpeedRpm
        && MinimumRpm <= MaximumRpm
        && StepRpm == FanControlSettings.SpeedStepRpm
        && MinimumRpm % StepRpm == 0
        && MaximumRpm % StepRpm == 0;

    public bool Supports(int speedRpm) =>
        IsWellFormed
        && speedRpm >= MinimumRpm
        && speedRpm <= MaximumRpm
        && speedRpm % StepRpm == 0;

    public int Quantize(int speedRpm) => Math.Clamp(
        (int)Math.Round(
            speedRpm / (double)StepRpm,
            MidpointRounding.AwayFromZero) * StepRpm,
        MinimumRpm,
        MaximumRpm);

    public static FanSpeedRange Standard { get; } = new(
        FanControlSettings.MinimumSpeedRpm,
        FanControlSettings.MaximumSpeedRpm,
        FanControlSettings.SpeedStepRpm);
}

public sealed record CustomPerformanceCombination(
    CustomPerformanceLevel Cpu,
    CustomPerformanceLevel Gpu)
{
    public bool IsWellFormed =>
        Enum.IsDefined(Cpu)
        && Enum.IsDefined(Gpu)
        && (Cpu, Gpu) is not (CustomPerformanceLevel.High, CustomPerformanceLevel.High);
}

public sealed record CustomPerformanceCapabilities(
    IReadOnlySet<CustomPerformanceCombination> AllowedCombinations)
{
    public bool IsWellFormed =>
        AllowedCombinations is { Count: > 0 }
        && AllowedCombinations.All(combination => combination is not null && combination.IsWellFormed);

    public bool Supports(CustomPerformanceSettings? settings) =>
        IsWellFormed
        && CustomPerformanceSettings.IsWellFormed(settings)
        && AllowedCombinations.Contains(new(settings!.Cpu, settings.Gpu));
}

public enum DeviceFeatureAccess
{
    ObservedOnly,
    ReadOnly,
    ReadWrite,
}

[Flags]
public enum SpecialKeySlotSet
{
    None = 0,
    M1 = 1 << 0,
    M2 = 1 << 1,
    M3 = 1 << 2,
    M4 = 1 << 3,
    M5 = 1 << 4,
    All = M1 | M2 | M3 | M4 | M5,
}

public sealed record DeviceNumericRange(
    int Minimum,
    int Maximum,
    int? Step)
{
    public bool IsWellFormed =>
        Minimum <= Maximum
        && (!Step.HasValue
            || Step.Value > 0
            && (Maximum - Minimum) % Step.Value == 0);

    public bool Supports(int value) =>
        IsWellFormed
        && Step.HasValue
        && value >= Minimum
        && value <= Maximum
        && (value - Minimum) % Step.Value == 0;
}

public sealed record CpuPowerLimitCapabilities(
    DeviceNumericRange Watts,
    DeviceFeatureAccess Access)
{
    public bool IsWellFormed =>
        Watts is { IsWellFormed: true }
        && Watts.Minimum > 0
        && Enum.IsDefined(Access);
}

public sealed record CpuVoltageOptimizerCapabilities(
    DeviceNumericRange Value,
    DeviceFeatureAccess Access)
{
    public bool SupportsEnableDisable { get; init; }

    public bool IsWellFormed =>
        Value is { IsWellFormed: true }
        && Enum.IsDefined(Access);
}

public sealed record GpuOverclockCapabilities(
    DeviceNumericRange CoreOffsetMegahertz,
    DeviceNumericRange MemoryOffsetMegahertz,
    DeviceFeatureAccess Access)
{
    public bool SupportsEnableDisable { get; init; }

    public bool IsWellFormed =>
        CoreOffsetMegahertz is { IsWellFormed: true }
        && MemoryOffsetMegahertz is { IsWellFormed: true }
        && Enum.IsDefined(Access);
}

public sealed record PowerSourceBehaviorObservation(
    PowerSource Source,
    PerformancePreset SelectedPerformance,
    FanMode SelectedFans,
    bool? FixedFanControlAvailable,
    bool? PerformanceModeAvailable,
    bool? CustomModeAvailable,
    bool? InadequatePowerWarningVisible)
{
    public bool IsWellFormed =>
        Enum.IsDefined(Source)
        && Enum.IsDefined(SelectedPerformance)
        && Enum.IsDefined(SelectedFans);
}

public sealed record DeviceCapabilities(
    IReadOnlySet<PerformancePreset> PerformancePresets,
    IReadOnlySet<FanMode> FanModes,
    IReadOnlySet<LightingEffect> LightingEffects,
    IReadOnlySet<int> ChargeLimits,
    bool SupportsBatteryProtectionOff,
    bool SupportsTemporaryFullCharge)
{
    public bool SupportsKeyboardBrightness { get; init; }

    public bool SupportsPlaybackMuteIndicator { get; init; }

    public FanSpeedRange? FanSpeedRange { get; init; }

    public CustomPerformanceCapabilities? CustomPerformance { get; init; }

    public CpuPowerLimitCapabilities? CpuPowerLimit { get; init; }

    public CpuVoltageOptimizerCapabilities? CpuVoltageOptimizer { get; init; }

    public GpuOverclockCapabilities? GpuOverclock { get; init; }

    public DeviceFeatureAccess? SpecialKeyBindingsAccess { get; init; }

    public SpecialKeySlotSet WritableSpecialKeySlots { get; init; }

    public DeviceFeatureAccess? LampArrayLightingAccess { get; init; }

    public IReadOnlySet<LightingEffect> ObservedLightingEffects { get; init; } =
        new HashSet<LightingEffect>();

    public IReadOnlyList<PowerSourceBehaviorObservation> ObservedPowerSourceBehaviors
    {
        get;
        init;
    } = [];

    public IReadOnlySet<LogoLightingMode> LogoLightingModes { get; init; } =
        new HashSet<LogoLightingMode>();

    public IReadOnlySet<OneShotLaptopControl> OneShotLaptopControls { get; init; } =
        new HashSet<OneShotLaptopControl>();

    internal bool IsWellFormed =>
        IsValidEnumSet(PerformancePresets)
        && IsValidEnumSet(FanModes)
        && IsValidEnumSet(LightingEffects)
        && AreChargeLimitsWellFormed(ChargeLimits)
        && (FanSpeedRange is null || FanSpeedRange.IsWellFormed)
        && (!FanModes.Any(IsManualFanMode) || FanSpeedRange is not null)
        && (CustomPerformance is null || CustomPerformance.IsWellFormed)
        && (CpuPowerLimit is null || CpuPowerLimit.IsWellFormed)
        && (CpuVoltageOptimizer is null || CpuVoltageOptimizer.IsWellFormed)
        && (GpuOverclock is null || GpuOverclock.IsWellFormed)
        && (SpecialKeyBindingsAccess is null
            || Enum.IsDefined(SpecialKeyBindingsAccess.Value))
        && HasValidSpecialKeySlots()
        && (LampArrayLightingAccess is null
            || Enum.IsDefined(LampArrayLightingAccess.Value))
        && IsValidEnumSet(ObservedLightingEffects)
        && IsValidEnumSet(LogoLightingModes)
        && IsValidEnumSet(OneShotLaptopControls)
        && ObservedPowerSourceBehaviors is not null
        && ObservedPowerSourceBehaviors.All(
            observation => observation is not null && observation.IsWellFormed)
        && ObservedPowerSourceBehaviors
            .Select(observation => observation.Source)
            .Distinct()
            .Count() == ObservedPowerSourceBehaviors.Count;

    internal static bool AreChargeLimitsWellFormed(IEnumerable<int>? limits)
    {
        if (limits is null)
        {
            return false;
        }

        int[] values = limits.ToArray();
        return values.Length <= 7
            && values.Distinct().Count() == values.Length
            && values.All(value => value is >= 50 and <= 80 && value % 5 == 0);
    }

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
        SupportsTemporaryFullCharge: false)
    {
        SupportsKeyboardBrightness = true,
        FanSpeedRange = FanSpeedRange.Standard,
    };

    private bool HasValidSpecialKeySlots()
    {
        bool onlyKnownSlots =
            (WritableSpecialKeySlots & ~SpecialKeySlotSet.All) == 0;
        return onlyKnownSlots
            && (SpecialKeyBindingsAccess == DeviceFeatureAccess.ReadWrite
                ? WritableSpecialKeySlots != SpecialKeySlotSet.None
                : WritableSpecialKeySlots == SpecialKeySlotSet.None);
    }

    private static bool IsManualFanMode(FanMode mode) =>
        mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum;

    private static bool IsValidEnumSet<TEnum>(IReadOnlySet<TEnum>? values)
        where TEnum : struct, Enum =>
        values is not null
        && values.All(Enum.IsDefined);
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
