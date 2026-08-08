namespace OpenBlade.Contracts;

public enum PerformancePreset
{
    Silent,
    Balanced,
    Performance,
    BatterySaver,
    BatteryBalanced,
    Custom,
}

public enum CustomPerformanceLevel
{
    Low,
    Medium,
    High,
}

public sealed record GpuClockOffsetSettings
{
    public const int MinimumAbsoluteOffsetMegahertz = -10_000;
    public const int MaximumAbsoluteOffsetMegahertz = 10_000;

    public GpuClockOffsetSettings(
        int coreMegahertz,
        int memoryMegahertz)
    {
        if (!IsWellFormed(coreMegahertz, memoryMegahertz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coreMegahertz),
                "GPU clock offsets exceed the supported contract bounds.");
        }

        CoreMegahertz = coreMegahertz;
        MemoryMegahertz = memoryMegahertz;
    }

    public int CoreMegahertz { get; }

    public int MemoryMegahertz { get; }

    public static GpuClockOffsetSettings Default { get; } = new(0, 0);

    public static bool IsWellFormed(GpuClockOffsetSettings? value) =>
        value is not null
        && IsWellFormed(value.CoreMegahertz, value.MemoryMegahertz);

    private static bool IsWellFormed(int coreMegahertz, int memoryMegahertz) =>
        coreMegahertz is
            >= MinimumAbsoluteOffsetMegahertz and <= MaximumAbsoluteOffsetMegahertz
        && memoryMegahertz is
            >= MinimumAbsoluteOffsetMegahertz and <= MaximumAbsoluteOffsetMegahertz;
}

public sealed record GpuClockTuningSettings(
    bool Enabled,
    GpuClockOffsetSettings Offsets)
{
    public GpuClockOffsetSettings EffectiveOffsets => Enabled
        ? Offsets
        : GpuClockOffsetSettings.Default;

    public static GpuClockTuningSettings Disabled { get; } = new(
        Enabled: false,
        GpuClockOffsetSettings.Default);

    public static bool IsWellFormed(GpuClockTuningSettings? value) =>
        value is not null
        && GpuClockOffsetSettings.IsWellFormed(value.Offsets);
}

public sealed record CustomPerformanceSettings
{
    public CustomPerformanceSettings(
        CustomPerformanceLevel cpu,
        CustomPerformanceLevel gpu)
    {
        if (!IsWellFormed(cpu, gpu))
        {
            throw new ArgumentException(
                "Custom performance levels are invalid; CPU High and GPU High cannot be combined.");
        }

        Cpu = cpu;
        Gpu = gpu;
    }

    public CustomPerformanceLevel Cpu { get; }

    public CustomPerformanceLevel Gpu { get; }

    public static CustomPerformanceSettings Default { get; } = new(
        CustomPerformanceLevel.Medium,
        CustomPerformanceLevel.High);

    // Retained for backward-compatible profile and IPC migration. New code
    // stores GPU tuning beside the power preset on PowerProfile.
    public GpuClockOffsetSettings? GpuClockOffsets { get; init; }

    public static bool IsWellFormed(CustomPerformanceSettings? value) =>
        value is not null
        && IsWellFormed(value.Cpu, value.Gpu)
        && (value.GpuClockOffsets is null
            || GpuClockOffsetSettings.IsWellFormed(value.GpuClockOffsets));

    private static bool IsWellFormed(
        CustomPerformanceLevel cpu,
        CustomPerformanceLevel gpu) =>
        Enum.IsDefined(cpu)
        && Enum.IsDefined(gpu)
        && (cpu, gpu) is not (CustomPerformanceLevel.High, CustomPerformanceLevel.High);
}

public enum FanMode
{
    Automatic,
    Fixed,
    ManualCurve,

    // Kept for profiles written before adjustable fixed speed was added.
    // It is equivalent to Fixed at 5,400 RPM.
    Maximum,
}

public sealed record FanCurvePoint
{
    public FanCurvePoint(int temperatureCelsius, int speedRpm)
    {
        if (temperatureCelsius is < 20 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureCelsius),
                temperatureCelsius,
                "Fan-curve temperature must be from 20 to 100 degrees Celsius.");
        }

        FanControlSettings.ValidateSpeed(speedRpm, nameof(speedRpm));
        TemperatureCelsius = temperatureCelsius;
        SpeedRpm = speedRpm;
    }

    public int TemperatureCelsius { get; }

    public int SpeedRpm { get; }
}

public sealed class ManualFanCurve : IEquatable<ManualFanCurve>
{
    public ManualFanCurve(
        IReadOnlyList<FanCurvePoint> cpu,
        IReadOnlyList<FanCurvePoint> gpu,
        bool useHigherTemperatureForBoth)
    {
        Cpu = ValidateCurve(cpu, nameof(cpu));
        Gpu = ValidateCurve(gpu, nameof(gpu));
        UseHigherTemperatureForBoth = useHigherTemperatureForBoth;
    }

    public IReadOnlyList<FanCurvePoint> Cpu { get; }

    public IReadOnlyList<FanCurvePoint> Gpu { get; }

    public bool UseHigherTemperatureForBoth { get; }

    public static ManualFanCurve Default { get; } = new(
        [
            new(40, 2000),
            new(50, 2100),
            new(60, 2600),
            new(70, 3400),
            new(80, 4400),
            new(90, 5400),
        ],
        [
            new(35, 2000),
            new(45, 2100),
            new(55, 2700),
            new(65, 3600),
            new(75, 4500),
            new(85, 5400),
        ],
        useHigherTemperatureForBoth: true);

    public int InterpolateCpu(int temperatureCelsius) =>
        Interpolate(Cpu, temperatureCelsius);

    public int InterpolateGpu(int temperatureCelsius) =>
        Interpolate(Gpu, temperatureCelsius);

    public bool Equals(ManualFanCurve? other) =>
        other is not null
        && UseHigherTemperatureForBoth == other.UseHigherTemperatureForBoth
        && Cpu.SequenceEqual(other.Cpu)
        && Gpu.SequenceEqual(other.Gpu);

    public override bool Equals(object? obj) => Equals(obj as ManualFanCurve);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(UseHigherTemperatureForBoth);
        foreach (FanCurvePoint point in Cpu)
        {
            hash.Add(point);
        }
        foreach (FanCurvePoint point in Gpu)
        {
            hash.Add(point);
        }
        return hash.ToHashCode();
    }

    private static IReadOnlyList<FanCurvePoint> ValidateCurve(
        IReadOnlyList<FanCurvePoint>? points,
        string parameterName)
    {
        if (points is null || points.Count is < 2 or > 10)
        {
            throw new ArgumentException(
                "A fan curve must contain from 2 to 10 points.",
                parameterName);
        }

        var copy = points.ToArray();
        for (int index = 0; index < copy.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(copy[index]);
            if (index > 0
                && (copy[index].TemperatureCelsius <= copy[index - 1].TemperatureCelsius
                    || copy[index].SpeedRpm < copy[index - 1].SpeedRpm))
            {
                throw new ArgumentException(
                    "Fan-curve temperatures must increase and speed must not decrease.",
                    parameterName);
            }
        }

        return copy;
    }

    private static int Interpolate(
        IReadOnlyList<FanCurvePoint> points,
        int temperatureCelsius)
    {
        if (temperatureCelsius <= points[0].TemperatureCelsius)
        {
            return points[0].SpeedRpm;
        }

        for (int index = 1; index < points.Count; index++)
        {
            FanCurvePoint upper = points[index];
            if (temperatureCelsius <= upper.TemperatureCelsius)
            {
                FanCurvePoint lower = points[index - 1];
                double position = (double)(temperatureCelsius - lower.TemperatureCelsius)
                    / (upper.TemperatureCelsius - lower.TemperatureCelsius);
                int interpolated = lower.SpeedRpm
                    + (int)Math.Round((upper.SpeedRpm - lower.SpeedRpm) * position);
                return FanControlSettings.QuantizeSpeed(interpolated);
            }
        }

        return points[^1].SpeedRpm;
    }
}

public sealed record FanControlSettings
{
    public const int MinimumSpeedRpm = 2000;
    public const int MaximumSpeedRpm = 5400;
    public const int SpeedStepRpm = 100;

    public FanControlSettings(
        FanMode mode,
        int fixedSpeedRpm,
        ManualFanCurve manualCurve)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ValidateSpeed(fixedSpeedRpm, nameof(fixedSpeedRpm));
        ManualCurve = manualCurve ?? throw new ArgumentNullException(nameof(manualCurve));
        Mode = mode;
        FixedSpeedRpm = mode == FanMode.Maximum ? MaximumSpeedRpm : fixedSpeedRpm;
    }

    public FanMode Mode { get; }

    public int FixedSpeedRpm { get; }

    public ManualFanCurve ManualCurve { get; }

    public static FanControlSettings Default { get; } = new(
        FanMode.Automatic,
        3700,
        ManualFanCurve.Default);

    public static FanControlSettings FromLegacy(FanMode mode) => new(
        mode,
        mode == FanMode.Maximum ? MaximumSpeedRpm : Default.FixedSpeedRpm,
        Default.ManualCurve);

    public static int QuantizeSpeed(int speedRpm) => Math.Clamp(
        (int)Math.Round(
            speedRpm / (double)SpeedStepRpm,
            MidpointRounding.AwayFromZero) * SpeedStepRpm,
        MinimumSpeedRpm,
        MaximumSpeedRpm);

    internal static void ValidateSpeed(int speedRpm, string parameterName)
    {
        if (speedRpm is < MinimumSpeedRpm or > MaximumSpeedRpm
            || speedRpm % SpeedStepRpm != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                speedRpm,
                $"Fan speed must be from {MinimumSpeedRpm} to {MaximumSpeedRpm} RPM in {SpeedStepRpm} RPM steps.");
        }
    }
}

public enum LightingEffect
{
    Off,
    Static,
    Spectrum,
    Breathing,
    Reactive,
    Tidal,
    Starlight,
    Wave,
    Wheel,
    Ripple,
    AmbientAwareness,
    AudioMeter,
    Fire,
}

public enum AmbientAwarenessRegion
{
    Full,
    Left,
    Top,
    Right,
    Bottom,
}

public enum LightingDirection
{
    Leftward,
    Rightward,
}

public enum LightingRotationDirection
{
    Clockwise,
    Counterclockwise,
}

public enum LogoLightingMode
{
    Static,
    Breathing,
    Off,
}

/// <summary>
/// The host-rendered lighting effect currently owned by the running OpenBlade
/// service. This is separate from desired profile state because PID 02E0 cannot
/// report its active matrix effect.
/// </summary>
public enum LightingRendererOwnershipState
{
    None,
    Static,
    Spectrum,
    Breathing,
    Reactive,
    Tidal,
    Starlight,
    Wave,
    Wheel,
    Faulted,
    Ripple,
    AmbientAwareness,
    AudioMeter,
    Fire,
}

public enum KeyboardBrightnessDirection
{
    Decrease,
    Increase,
}

public readonly record struct RgbColor(byte Red, byte Green, byte Blue);

public sealed record LightingSettings(
    LightingEffect Effect,
    RgbColor Color,
    byte Brightness)
{
    // Optional so profiles and IPC messages written before effect-specific
    // parameters were introduced remain readable without migration.
    public LightingEffectParameters? Parameters { get; init; }

    // Null keeps profiles written before logo control support non-destructive:
    // OpenBlade leaves the firmware-persistent lid logo setting unchanged until
    // the user explicitly chooses a mode.
    public LogoLightingMode? LogoMode { get; init; }
}

public sealed record PowerProfile(
    PerformancePreset Performance,
    FanMode Fans,
    LightingSettings Lighting,
    DisplayProfileSettings? Display = null)
{
    // Retained while another preset is selected so switching back to Custom
    // restores the user's last valid CPU/GPU pair.
    public CustomPerformanceSettings? CustomPerformance { get; init; }

    // Null leaves NVIDIA clock state unmanaged. A non-null setting opts this
    // profile into independent GPU tuning for any admitted power preset.
    public GpuClockTuningSettings? GpuClockTuning { get; init; }

    // Optional for JSON compatibility with profiles written before adjustable
    // fixed speed and manual curves were introduced.
    public FanControlSettings? FanControl { get; init; }

    // Optional so schema 4 profiles remain readable. Profile normalization
    // fills this from the former ProfileSet-wide policy during migration.
    public LightingPowerPolicy? LightingPowerPolicy { get; init; }

    // Optional so profiles written before laptop-behavior synchronization
    // remain non-destructive. Null fields are deliberately left unchanged
    // until the user saves a value for that power source.
    public LaptopBehaviorProfileSettings? LaptopBehavior { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public LightingPowerPolicy EffectiveLightingPowerPolicy =>
        LightingPowerPolicy ?? OpenBlade.Contracts.LightingPowerPolicy.Disabled;

    [System.Text.Json.Serialization.JsonIgnore]
    public LaptopBehaviorProfileSettings EffectiveLaptopBehavior =>
        LaptopBehavior ?? LaptopBehaviorProfileSettings.Unconfigured();
}

public sealed record LaptopBehaviorProfileSettings(
    bool? StartupAnimationEnabled,
    FunctionKeyPrimaryMode? FunctionKeyPrimary,
    bool? GamingModeEnabled)
{
    // Optional for compatibility with profiles written before host-side Gaming
    // mode key blocking was configurable.
    public GamingModeKeyPolicy? GamingModeKeyPolicy { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public GamingModeKeyPolicy EffectiveGamingModeKeyPolicy =>
        GamingModeKeyPolicy ?? OpenBlade.Contracts.GamingModeKeyPolicy.Disabled;

    public bool IsConfigured =>
        StartupAnimationEnabled.HasValue
        || FunctionKeyPrimary.HasValue
        || GamingModeEnabled.HasValue
        || GamingModeKeyPolicy is not null;

    public static LaptopBehaviorProfileSettings Unconfigured() =>
        new(null, null, null);
}

public sealed record GamingModeKeyPolicy(bool BlockWindowsKey, bool BlockAltTab)
{
    public static GamingModeKeyPolicy Disabled { get; } = new(false, false);
}

public sealed record LightingPowerPolicy
{
    public LightingPowerPolicy(
        bool turnOffWhenDisplayTurnsOff,
        int? idleTimeoutMinutes)
    {
        if (idleTimeoutMinutes is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeoutMinutes),
                idleTimeoutMinutes,
                "Lighting idle timeout must be from 1 to 60 minutes.");
        }

        TurnOffWhenDisplayTurnsOff = turnOffWhenDisplayTurnsOff;
        IdleTimeoutMinutes = idleTimeoutMinutes;
    }

    public bool TurnOffWhenDisplayTurnsOff { get; }

    public int? IdleTimeoutMinutes { get; }

    public static LightingPowerPolicy Disabled { get; } = new(false, null);
}

public enum LightingSuppressionReason
{
    DisplayOff,
    UserIdle,
}

public sealed record DisplayProfileSettings(
    string? RefreshRateKey,
    bool? HdrEnabled,
    string? ColorProfileKey)
{
    public bool IsConfigured =>
        RefreshRateKey is not null || HdrEnabled.HasValue || ColorProfileKey is not null;

    public static DisplayProfileSettings Unconfigured() => new(null, null, null);
}

public sealed record BatteryPolicy
{
    public BatteryPolicy(int chargeLimitPercent)
    {
        if (chargeLimitPercent is < 50 or > 80 || chargeLimitPercent % 5 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chargeLimitPercent),
                chargeLimitPercent,
                "Charge limit must be from 50 to 80 percent in 5 percent steps.");
        }

        ChargeLimitPercent = chargeLimitPercent;
    }

    public int ChargeLimitPercent { get; }

    public static BatteryPolicy Create(int chargeLimitPercent) => new(chargeLimitPercent);
}

public sealed record DesiredBatteryState
{
    public DesiredBatteryState(
        BatteryPolicy configuredPolicy,
        bool protectionEnabled,
        bool temporaryFullCharge)
    {
        ConfiguredPolicy = configuredPolicy
            ?? throw new ArgumentNullException(nameof(configuredPolicy));
        if (!protectionEnabled && temporaryFullCharge)
        {
            throw new ArgumentException(
                "Protection off and temporary full charge cannot both be active.");
        }

        ProtectionEnabled = protectionEnabled;
        TemporaryFullCharge = temporaryFullCharge;
    }

    public BatteryPolicy ConfiguredPolicy { get; }

    public bool ProtectionEnabled { get; }

    public bool TemporaryFullCharge { get; }
}

public sealed record ConfirmedBatteryState
{
    public ConfirmedBatteryState(
        bool protectionEnabled,
        int? chargeLimitPercent,
        bool? temporaryFullCharge)
    {
        if (protectionEnabled != chargeLimitPercent.HasValue)
        {
            throw new ArgumentException(
                "Enabled protection requires one exact limit; disabled protection requires none.");
        }

        if (chargeLimitPercent.HasValue)
        {
            _ = BatteryPolicy.Create(chargeLimitPercent.Value);
        }

        if (!protectionEnabled && temporaryFullCharge is true)
        {
            throw new ArgumentException(
                "Protection off is distinct from temporary full charge.");
        }

        ProtectionEnabled = protectionEnabled;
        ChargeLimitPercent = chargeLimitPercent;
        TemporaryFullCharge = temporaryFullCharge;
    }

    public bool ProtectionEnabled { get; }

    public int? ChargeLimitPercent { get; }

    public bool? TemporaryFullCharge { get; }
}

public sealed record ProfileSet(
    PowerProfile PluggedIn,
    PowerProfile Battery,
    PowerProfile UsbC,
    BatteryPolicy BatteryPolicy,
    LightingPowerPolicy? LightingPowerPolicy = null)
{
    // Retained only as the schema 4 migration source. New documents store the
    // policy on each PowerProfile and normalize this property to null.
    [System.Text.Json.Serialization.JsonIgnore]
    public LightingPowerPolicy LegacyLightingPowerPolicy =>
        LightingPowerPolicy ?? OpenBlade.Contracts.LightingPowerPolicy.Disabled;

    public PowerProfile ForPowerSource(PowerSource source) => source switch
    {
        PowerSource.PluggedIn => PluggedIn,
        PowerSource.Battery => Battery,
        PowerSource.UsbC => UsbC,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public LightingPowerPolicy LightingPowerPolicyFor(PowerSource source) => source switch
    {
        PowerSource.PluggedIn => PluggedIn.EffectiveLightingPowerPolicy,
        PowerSource.Battery => Battery.EffectiveLightingPowerPolicy,
        PowerSource.UsbC => UsbC.EffectiveLightingPowerPolicy,
        PowerSource.ExternalUnknown => OpenBlade.Contracts.LightingPowerPolicy.Disabled,
        _ => OpenBlade.Contracts.LightingPowerPolicy.Disabled,
    };
}

public static class ProfileDefaults
{
    private static readonly LightingSettings LightingOff = LightingSettingsFactory.Off();

    public static ProfileSet CreateSafeDefaults() => new(
        new PowerProfile(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured())
        {
            CustomPerformance = CustomPerformanceSettings.Default,
            LightingPowerPolicy = OpenBlade.Contracts.LightingPowerPolicy.Disabled,
        },
        new PowerProfile(
            PerformancePreset.BatterySaver,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured())
        {
            LightingPowerPolicy = OpenBlade.Contracts.LightingPowerPolicy.Disabled,
        },
        new PowerProfile(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured())
        {
            LightingPowerPolicy = OpenBlade.Contracts.LightingPowerPolicy.Disabled,
        },
        BatteryPolicy.Create(80));
}
