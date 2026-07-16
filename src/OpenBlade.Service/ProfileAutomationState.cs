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
    private DesiredBatteryState desiredBattery;

    public ProfileAutomationState(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        PowerSource powerSource)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        ArgumentNullException.ThrowIfNull(preferences);
        PowerSource = powerSource;
        desiredBattery = new DesiredBatteryState(
            profiles.BatteryPolicy,
            preferences.ProtectionEnabled,
            temporaryFullCharge: false);
    }

    public PowerSource PowerSource { get; private set; }

    public BatteryPolicy BatteryPolicy => desiredBattery.ConfiguredPolicy;

    public DesiredBatteryState DesiredBattery => desiredBattery;

    public ProfileSet Profiles => profiles;

    public ManualOverride<PerformancePreset>? PerformanceOverride =>
        persistentPerformance ?? temporaryPerformance;

    public ManualOverride<FanMode>? FanOverride => persistentFans ?? temporaryFans;

    public ManualOverride<LightingSettings>? LightingOverride =>
        persistentLighting ?? temporaryLighting;

    public void UpdateProfiles(ProfileSet value)
    {
        ArgumentNullException.ThrowIfNull(value);
        profiles = value;
        desiredBattery = new DesiredBatteryState(
            value.BatteryPolicy,
            desiredBattery.ProtectionEnabled,
            desiredBattery.TemporaryFullCharge);
    }

    public void SetBatteryPolicy(BatteryPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CommitBatteryState(new DesiredBatteryState(
            value,
            desiredBattery.ProtectionEnabled,
            desiredBattery.TemporaryFullCharge));
    }

    public void CommitBatteryState(DesiredBatteryState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        profiles = profiles with { BatteryPolicy = value.ConfiguredPolicy };
        desiredBattery = value;
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
        if (powerSource == PowerSource.Battery && desiredBattery.TemporaryFullCharge)
        {
            CancelTemporaryFullCharge();
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
        if (PowerSource == PowerSource.Battery || !desiredBattery.ProtectionEnabled)
        {
            return;
        }

        desiredBattery = new DesiredBatteryState(
            desiredBattery.ConfiguredPolicy,
            desiredBattery.ProtectionEnabled,
            temporaryFullCharge: true);
    }

    public void CancelTemporaryFullCharge() => desiredBattery = new DesiredBatteryState(
        desiredBattery.ConfiguredPolicy,
        desiredBattery.ProtectionEnabled,
        temporaryFullCharge: false);

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
            DesiredBattery);
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
