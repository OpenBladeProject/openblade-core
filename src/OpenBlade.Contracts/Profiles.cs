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
}

public readonly record struct RgbColor(byte Red, byte Green, byte Blue);

public sealed record LightingSettings(
    LightingEffect Effect,
    RgbColor Color,
    byte Brightness);

public sealed record PowerProfile(
    PerformancePreset Performance,
    FanMode Fans,
    LightingSettings Lighting);

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
    private static readonly LightingSettings LightingOff = new(
        LightingEffect.Off,
        new RgbColor(0, 0, 0),
        Brightness: 0);

    public static ProfileSet CreateSafeDefaults() => new(
        new PowerProfile(PerformancePreset.Balanced, FanMode.Automatic, LightingOff),
        new PowerProfile(PerformancePreset.BatterySaver, FanMode.Automatic, LightingOff),
        new PowerProfile(PerformancePreset.Balanced, FanMode.Automatic, LightingOff),
        BatteryPolicy.Create(80));
}
