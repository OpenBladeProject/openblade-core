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

    public static bool IsWellFormed(CustomPerformanceSettings? value) =>
        value is not null && IsWellFormed(value.Cpu, value.Gpu);

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
    Maximum,
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
    [System.Text.Json.Serialization.JsonIgnore]
    public LightingPowerPolicy EffectiveLightingPowerPolicy =>
        LightingPowerPolicy ?? OpenBlade.Contracts.LightingPowerPolicy.Disabled;
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
        },
        new PowerProfile(
            PerformancePreset.BatterySaver,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured()),
        new PowerProfile(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured()),
        BatteryPolicy.Create(80),
        LightingPowerPolicy.Disabled);
}
