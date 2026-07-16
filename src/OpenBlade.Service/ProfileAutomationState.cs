using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ProfileAutomationState
{
    private ProfileSet profiles;
    private ManualOverride<PerformancePreset>? temporaryPerformance;
    private ManualOverride<PerformancePreset>? persistentPerformance;
    private ManualOverride<FanMode>? temporaryFans;
    private ManualOverride<FanMode>? persistentFans;
    private ManualOverride<LightingSettings>? temporaryLighting;
    private ManualOverride<LightingSettings>? persistentLighting;

    public ProfileAutomationState(ProfileSet profiles, PowerSource powerSource)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        PowerSource = powerSource;
        BatteryPolicy = profiles.BatteryPolicy;
    }

    public PowerSource PowerSource { get; private set; }

    public BatteryPolicy BatteryPolicy { get; private set; }

    public ProfileSet Profiles => profiles;

    public ManualOverride<PerformancePreset>? PerformanceOverride =>
        persistentPerformance ?? temporaryPerformance;

    public ManualOverride<FanMode>? FanOverride => persistentFans ?? temporaryFans;

    public ManualOverride<LightingSettings>? LightingOverride =>
        persistentLighting ?? temporaryLighting;

    public void UpdateProfiles(ProfileSet value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool temporaryFullCharge = BatteryPolicy.TemporaryFullCharge;
        profiles = value;
        BatteryPolicy = BatteryPolicy.Create(
            value.BatteryPolicy.ChargeLimitPercent,
            temporaryFullCharge);
    }

    public void SetBatteryPolicy(BatteryPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TemporaryFullCharge)
        {
            throw new ArgumentException(
                "Temporary full-charge state cannot be persisted.",
                nameof(value));
        }

        profiles = profiles with { BatteryPolicy = value };
        BatteryPolicy = BatteryPolicy.Create(
            value.ChargeLimitPercent,
            BatteryPolicy.TemporaryFullCharge);
    }

    public bool ChangePowerSource(PowerSource powerSource)
    {
        if (PowerSource == powerSource)
        {
            return false;
        }

        PowerSource = powerSource;
        temporaryPerformance = null;
        temporaryFans = null;
        temporaryLighting = null;
        if (powerSource == PowerSource.Battery && BatteryPolicy.TemporaryFullCharge)
        {
            BatteryPolicy = BatteryPolicy.Create(BatteryPolicy.ChargeLimitPercent);
        }

        return true;
    }

    public void SetPerformanceOverride(ManualOverride<PerformancePreset>? value) =>
        SetOverride(value, ref temporaryPerformance, ref persistentPerformance);

    public void SetFanOverride(ManualOverride<FanMode>? value) =>
        SetOverride(value, ref temporaryFans, ref persistentFans);

    public void SetLightingOverride(ManualOverride<LightingSettings>? value) =>
        SetOverride(value, ref temporaryLighting, ref persistentLighting);

    public void EnableTemporaryFullCharge()
    {
        if (PowerSource == PowerSource.Battery)
        {
            return;
        }

        BatteryPolicy = BatteryPolicy.Create(
            BatteryPolicy.ChargeLimitPercent,
            temporaryFullCharge: true);
    }

    public void CancelTemporaryFullCharge() =>
        BatteryPolicy = BatteryPolicy.Create(BatteryPolicy.ChargeLimitPercent);

    public DesiredDeviceState Resolve(ConfirmedDeviceState confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        PowerProfile profile = PowerSource == PowerSource.PluggedIn
            ? profiles.PluggedIn
            : profiles.Battery;

        return new DesiredDeviceState(
            OverrideResolver.Resolve<PerformancePreset>(profile.Performance, confirmed.Performance.GetValueOrDefault(), temporaryPerformance, persistentPerformance),
            OverrideResolver.Resolve<FanMode>(profile.Fans, confirmed.Fans.GetValueOrDefault(), temporaryFans, persistentFans),
            OverrideResolver.Resolve<LightingSettings>(profile.Lighting, confirmed.Lighting ?? profile.Lighting, temporaryLighting, persistentLighting),
            BatteryPolicy);
    }

    private static void SetOverride<T>(
        ManualOverride<T>? value,
        ref ManualOverride<T>? temporary,
        ref ManualOverride<T>? persistent)
    {
        if (value is null)
        {
            temporary = null;
            persistent = null;
        }
        else if (value.Lifetime == OverrideLifetime.Persistent)
        {
            persistent = value;
        }
        else
        {
            temporary = value;
        }
    }
}
