using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ProfileAutomationState
{
    private ProfileSet profiles;
    private PerformanceOverride? temporaryPerformance;
    private PerformanceOverride? persistentPerformance;
    private ManualOverride<FanMode>? temporaryFans;
    private ManualOverride<FanMode>? persistentFans;
    private FanControlSettings? temporaryFanControl;
    private FanControlSettings? persistentFanControl;
    private ManualOverride<LightingSettings>? temporaryLighting;
    private ManualOverride<LightingSettings>? persistentLighting;
    private DesiredBatteryState desiredBattery;

    public ProfileAutomationState(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        PowerSource powerSource)
        : this(profiles, preferences, powerSource, PersistentOverrideState.Empty)
    {
    }

    public ProfileAutomationState(
        ProfileSet profiles,
        BatteryAutomationPreferences preferences,
        PowerSource powerSource,
        PersistentOverrideState persistentOverrides)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        ArgumentNullException.ThrowIfNull(preferences);
        persistentOverrides = PersistentOverrideStore.NormalizeLegacyFanControl(
            PersistentOverrideStore.NormalizeLegacyLighting(persistentOverrides));
        PersistentOverrideStore.Validate(persistentOverrides);
        PowerSource = powerSource;
        persistentPerformance = persistentOverrides.Performance is { } performance
            ? new PerformanceOverride(
                performance,
                OverrideLifetime.Persistent,
                performance == PerformancePreset.Custom
                    ? persistentOverrides.CustomPerformance
                    : null)
            : null;
        persistentFans = persistentOverrides.Fans is { } fans
            ? new ManualOverride<FanMode>(fans, OverrideLifetime.Persistent)
            : null;
        persistentFanControl = persistentOverrides.FanControl;
        persistentLighting = persistentOverrides.Lighting is { } lighting
            ? new ManualOverride<LightingSettings>(lighting, OverrideLifetime.Persistent)
            : null;
        PowerProfile? activeProfile = powerSource switch
        {
            PowerSource.PluggedIn => profiles.PluggedIn,
            PowerSource.Battery => profiles.Battery,
            PowerSource.UsbC => profiles.UsbC,
            _ => null,
        };
        temporaryLighting = preferences.LastKnownLighting is { } lastKnownLighting
            && lastKnownLighting != activeProfile?.Lighting
                ? new ManualOverride<LightingSettings>(
                    lastKnownLighting,
                    OverrideLifetime.UntilPowerSourceChange)
                : null;
        desiredBattery = new DesiredBatteryState(
            profiles.BatteryPolicy,
            preferences.ProtectionEnabled,
            temporaryFullCharge: false);
    }

    public PowerSource PowerSource { get; private set; }

    public BatteryPolicy BatteryPolicy => desiredBattery.ConfiguredPolicy;

    public DesiredBatteryState DesiredBattery => desiredBattery;

    public ProfileSet Profiles => profiles;

    public PerformanceOverride? PerformanceOverride =>
        persistentPerformance ?? temporaryPerformance;

    public ManualOverride<FanMode>? FanOverride => persistentFans ?? temporaryFans;

    public FanControlSettings? FanControlOverride =>
        persistentFans is not null ? persistentFanControl : temporaryFanControl;

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
        temporaryFanControl = null;
        temporaryLighting = null;
        if (powerSource == PowerSource.Battery && desiredBattery.TemporaryFullCharge)
        {
            CancelTemporaryFullCharge();
        }

        return true;
    }

    public void SetPerformanceOverride(PerformanceOverride? value) =>
        SetOverride(value, ref temporaryPerformance, ref persistentPerformance);

    public void SetPerformanceOverride(ManualOverride<PerformancePreset>? value) =>
        SetPerformanceOverride(
            value is null
                ? null
                : new PerformanceOverride(value.Value, value.Lifetime));

    public void SetFanOverride(
        ManualOverride<FanMode>? value,
        FanControlSettings? settings = null)
    {
        if (value is not null)
        {
            settings ??= FanControlSettings.FromLegacy(value.Value);
            if (settings.Mode != value.Value)
            {
                throw new ArgumentException(
                    "The fan override mode and fan settings do not match.",
                    nameof(settings));
            }
        }

        SetOverride(value, ref temporaryFans, ref persistentFans);
        switch (value?.Lifetime)
        {
            case OverrideLifetime.UntilPowerSourceChange:
                temporaryFanControl = settings;
                break;
            case OverrideLifetime.Persistent:
                persistentFanControl = settings;
                break;
            case null:
                temporaryFanControl = null;
                persistentFanControl = null;
                break;
        }
    }

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
        PowerProfile profile = PowerSource switch
        {
            PowerSource.PluggedIn => profiles.PluggedIn,
            PowerSource.Battery => profiles.Battery,
            PowerSource.UsbC => profiles.UsbC,
            PowerSource.ExternalUnknown => CreateObservedFallbackProfile(confirmed),
            _ => throw new InvalidOperationException("The active power source is undefined."),
        };
        PerformanceOverride? activePersistentPerformance =
            persistentPerformance is { } persistent
            && IsPerformanceCompatibleWithSource(persistent.Value, PowerSource)
                ? persistent
                : null;
        PerformanceOverride? activeTemporaryPerformance =
            temporaryPerformance is { } temporary
            && IsPerformanceCompatibleWithSource(temporary.Value, PowerSource)
                ? temporary
                : null;
        ManualOverride<FanMode>? activeTemporaryFans =
            temporaryFans is { } temporaryFan
            && IsFanCompatibleWithSource(temporaryFan.Value, PowerSource)
                ? temporaryFan
                : null;
        ManualOverride<FanMode>? activePersistentFans =
            persistentFans is { } persistentFan
            && IsFanCompatibleWithSource(persistentFan.Value, PowerSource)
                ? persistentFan
                : null;
        bool allowLightingOverride = PowerSource != PowerSource.ExternalUnknown;

        ResolvedSetting<PerformancePreset> resolvedPerformance = ResolvePerformance(
            profile.Performance,
            activeTemporaryPerformance,
            activePersistentPerformance);
        CustomPerformanceSettings? customPerformance = resolvedPerformance.Value
            == PerformancePreset.Custom
                ? activePersistentPerformance?.CustomPerformance
                    ?? activeTemporaryPerformance?.CustomPerformance
                    ?? profile.CustomPerformance
                    ?? confirmed.CustomPerformance
                    ?? CustomPerformanceSettings.Default
                : null;

        ResolvedSetting<FanMode> resolvedFans = OverrideResolver.Resolve<FanMode>(
            profile.Fans,
            confirmed.Fans.GetValueOrDefault(),
            activeTemporaryFans,
            activePersistentFans);
        FanControlSettings profileFanControl = profile.FanControl
            ?? FanControlSettings.FromLegacy(profile.Fans);
        FanControlSettings selectedFanControl = resolvedFans.Source switch
        {
            SettingValueSource.PersistentOverride => persistentFanControl
                ?? WithMode(profileFanControl, resolvedFans.Value),
            SettingValueSource.TemporaryOverride => temporaryFanControl
                ?? WithMode(profileFanControl, resolvedFans.Value),
            _ => WithMode(profileFanControl, resolvedFans.Value),
        };

        return new DesiredDeviceState(
            resolvedPerformance,
            resolvedFans,
            OverrideResolver.Resolve<LightingSettings>(profile.Lighting, confirmed.Lighting ?? profile.Lighting, allowLightingOverride ? temporaryLighting : null, allowLightingOverride ? persistentLighting : null),
            DesiredBattery)
        {
            CustomPerformance = customPerformance,
            FanControl = selectedFanControl,
        };
    }

    private static FanControlSettings WithMode(
        FanControlSettings settings,
        FanMode mode) => settings.Mode == mode
            ? settings
            : new FanControlSettings(
                mode,
                mode == FanMode.Maximum
                    ? FanControlSettings.MaximumSpeedRpm
                    : settings.FixedSpeedRpm,
                settings.ManualCurve);

    private static bool IsPerformanceCompatibleWithSource(
        PerformancePreset performance,
        PowerSource source) => source switch
        {
            PowerSource.PluggedIn => performance is
                PerformancePreset.Silent
                or PerformancePreset.Balanced
                or PerformancePreset.Performance
                or PerformancePreset.Custom,
            PowerSource.Battery => performance is
                PerformancePreset.BatterySaver
                or PerformancePreset.BatteryBalanced,
            PowerSource.UsbC => performance is PerformancePreset.Balanced,
            _ => false,
        };

    private PowerProfile CreateObservedFallbackProfile(ConfirmedDeviceState confirmed) => new(
        confirmed.Performance ?? profiles.UsbC.Performance,
        confirmed.Fans ?? profiles.UsbC.Fans,
        confirmed.Lighting ?? profiles.UsbC.Lighting)
    {
        CustomPerformance = confirmed.CustomPerformance,
    };

    private static bool IsFanCompatibleWithSource(FanMode fans, PowerSource source) =>
        source switch
        {
            PowerSource.PluggedIn => fans is FanMode.Automatic
                or FanMode.Fixed
                or FanMode.ManualCurve
                or FanMode.Maximum,
            PowerSource.Battery => Enum.IsDefined(fans),
            PowerSource.UsbC => fans is FanMode.Automatic,
            _ => false,
        };

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
            temporary = null;
            persistent = value;
        }
        else
        {
            temporary = value;
            persistent = null;
        }
    }

    private static ResolvedSetting<PerformancePreset> ResolvePerformance(
        PerformancePreset profileValue,
        PerformanceOverride? temporaryOverride,
        PerformanceOverride? persistentOverride)
    {
        if (persistentOverride is not null)
        {
            return new ResolvedSetting<PerformancePreset>(
                persistentOverride.Value,
                SettingValueSource.PersistentOverride);
        }

        if (temporaryOverride is not null)
        {
            return new ResolvedSetting<PerformancePreset>(
                temporaryOverride.Value,
                SettingValueSource.TemporaryOverride);
        }

        return new ResolvedSetting<PerformancePreset>(
            profileValue,
            SettingValueSource.PowerProfile);
    }

    private static void SetOverride(
        PerformanceOverride? value,
        ref PerformanceOverride? temporary,
        ref PerformanceOverride? persistent)
    {
        if (value is null)
        {
            temporary = null;
            persistent = null;
        }
        else if (value.Lifetime == OverrideLifetime.Persistent)
        {
            temporary = null;
            persistent = value;
        }
        else
        {
            temporary = value;
            persistent = null;
        }
    }
}
