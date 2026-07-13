namespace OpenSynapse.Contracts;

public enum PerformancePreset
{
    Silent,
    Balanced,
    Performance,
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
    private BatteryPolicy(int chargeLimitPercent, bool temporaryFullCharge)
    {
        ChargeLimitPercent = chargeLimitPercent;
        TemporaryFullCharge = temporaryFullCharge;
    }

    public int ChargeLimitPercent { get; }

    public bool TemporaryFullCharge { get; }

    public static BatteryPolicy Create(int chargeLimitPercent, bool temporaryFullCharge = false)
    {
        if (chargeLimitPercent is < 50 or > 80 || chargeLimitPercent % 5 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chargeLimitPercent),
                chargeLimitPercent,
                "Charge limit must be from 50 to 80 percent in 5 percent steps.");
        }

        return new BatteryPolicy(chargeLimitPercent, temporaryFullCharge);
    }
}

public sealed record ProfileSet(
    PowerProfile PluggedIn,
    PowerProfile Battery,
    BatteryPolicy BatteryPolicy);

public static class ProfileDefaults
{
    private static readonly LightingSettings LightingOff = new(
        LightingEffect.Off,
        new RgbColor(0, 0, 0),
        Brightness: 0);

    public static ProfileSet CreateSafeDefaults() => new(
        new PowerProfile(PerformancePreset.Balanced, FanMode.Automatic, LightingOff),
        new PowerProfile(PerformancePreset.Silent, FanMode.Automatic, LightingOff),
        BatteryPolicy.Create(80));
}
