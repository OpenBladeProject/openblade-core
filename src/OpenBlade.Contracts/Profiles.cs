namespace OpenBlade.Contracts;

public enum PerformancePreset
{
    Silent,
    Balanced,
    Performance,
    BatterySaver,
    BatteryBalanced,
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
}

public enum LightingDirection
{
    Leftward,
    Rightward,
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
    DisplayProfileSettings? Display = null);

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
    BatteryPolicy BatteryPolicy);

public static class ProfileDefaults
{
    private static readonly LightingSettings LightingOff = LightingSettingsFactory.Off();

    public static ProfileSet CreateSafeDefaults() => new(
        new PowerProfile(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            LightingOff,
            DisplayProfileSettings.Unconfigured()),
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
        BatteryPolicy.Create(80));
}
