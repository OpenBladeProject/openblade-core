using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ProfileAutomationState
{
    private ProfileSet profiles;
    private PerformanceOverride? recoveredHyperBoostPerformance;
    private PerformanceOverride? temporaryPerformance;
    private PerformanceOverride? persistentPerformance;
    private ManualOverride<FanMode>? recoveredHyperBoostFans;
    private ManualOverride<FanMode>? temporaryFans;
    private ManualOverride<FanMode>? persistentFans;
    private FanControlSettings? recoveredHyperBoostFanControl;
    private FanControlSettings? temporaryFanControl;
    private FanControlSettings? persistentFanControl;
    private ManualOverride<LightingSettings>? temporaryLighting;
    private ManualOverride<LightingSettings>? persistentLighting;
    private BatteryPolicy? temporaryBatteryPolicy;
    private bool? temporaryBatteryProtectionEnabled;
    private bool temporaryFullCharge;

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
        persistentOverrides = PersistentOverrideStore.NormalizeLegacyGpuClockTuning(
            PersistentOverrideStore.NormalizeLegacyFanControl(
                PersistentOverrideStore.NormalizeLegacyLighting(persistentOverrides)));
        PersistentOverrideStore.Validate(persistentOverrides);
        PowerSource = powerSource;
        persistentPerformance = persistentOverrides.Performance is { } performance
            ? new PerformanceOverride(
                performance,
                OverrideLifetime.Persistent,
                performance == PerformancePreset.Custom
                    ? persistentOverrides.CustomPerformance
                    : null)
            {
                GpuClockTuning = persistentOverrides.GpuClockTuning,
            }
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
    }

    public PowerSource PowerSource { get; private set; }

    public BatteryPolicy BatteryPolicy => temporaryBatteryPolicy ?? profiles.BatteryPolicy;

    public DesiredBatteryState DesiredBattery => new(
        BatteryPolicy,
        temporaryBatteryProtectionEnabled
            ?? GetActiveProfile()?.EffectiveBatteryProtectionEnabled
            ?? true,
        temporaryFullCharge);

    public BatteryPolicy? BatteryPolicyOverride => temporaryBatteryPolicy;

    public bool? BatteryProtectionOverride => temporaryBatteryProtectionEnabled;

    public ProfileSet Profiles => profiles;

    public PerformanceOverride? PerformanceOverride =>
        recoveredHyperBoostPerformance
            ?? persistentPerformance
            ?? temporaryPerformance;

    public ManualOverride<FanMode>? FanOverride =>
        recoveredHyperBoostFans ?? persistentFans ?? temporaryFans;

    public FanControlSettings? FanControlOverride =>
        recoveredHyperBoostFans is not null
            ? recoveredHyperBoostFanControl
            : persistentFans is not null
                ? persistentFanControl
                : temporaryFanControl;

    public ManualOverride<LightingSettings>? LightingOverride =>
        persistentLighting ?? temporaryLighting;

    public void UpdateProfiles(ProfileSet value)
    {
        ArgumentNullException.ThrowIfNull(value);
        profiles = value;
        if (temporaryBatteryPolicy == value.BatteryPolicy)
        {
            temporaryBatteryPolicy = null;
        }
        if (temporaryBatteryProtectionEnabled
            == GetActiveProfile()?.EffectiveBatteryProtectionEnabled)
        {
            temporaryBatteryProtectionEnabled = null;
        }
    }

    public void SetBatteryPolicy(BatteryPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CommitBatteryState(new DesiredBatteryState(
            value,
            DesiredBattery.ProtectionEnabled,
            DesiredBattery.TemporaryFullCharge));
    }

    public void CommitBatteryState(DesiredBatteryState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        temporaryBatteryPolicy = value.ConfiguredPolicy == profiles.BatteryPolicy
            ? null
            : value.ConfiguredPolicy;
        temporaryBatteryProtectionEnabled = value.ProtectionEnabled
            == GetActiveProfile()?.EffectiveBatteryProtectionEnabled
                ? null
                : value.ProtectionEnabled;
        temporaryFullCharge = value.TemporaryFullCharge;
    }

    public bool ChangePowerSource(PowerSource powerSource)
    {
        if (PowerSource == powerSource)
        {
            return false;
        }

        PowerSource = powerSource;
        recoveredHyperBoostPerformance = null;
        recoveredHyperBoostFans = null;
        recoveredHyperBoostFanControl = null;
        temporaryPerformance = null;
        temporaryFans = null;
        temporaryFanControl = null;
        temporaryLighting = null;
        temporaryBatteryProtectionEnabled = null;
        if (powerSource == PowerSource.Battery && temporaryFullCharge)
        {
            CancelTemporaryFullCharge();
        }

        return true;
    }

    public void SetPerformanceOverride(PerformanceOverride? value)
    {
        recoveredHyperBoostPerformance = null;
        SetOverride(value, ref temporaryPerformance, ref persistentPerformance);
    }

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

        recoveredHyperBoostFans = null;
        recoveredHyperBoostFanControl = null;
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

    internal void RestoreHyperBoostIntentUntilPowerSourceChange(
        HyperBoostRestoreIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        recoveredHyperBoostPerformance = new PerformanceOverride(
            intent.Performance,
            OverrideLifetime.UntilPowerSourceChange,
            intent.CustomPerformance)
        {
            GpuClockTuning = intent.GpuClockTuning,
        };
        recoveredHyperBoostFans = new ManualOverride<FanMode>(
            intent.FanControl.Mode,
            OverrideLifetime.UntilPowerSourceChange);
        recoveredHyperBoostFanControl = intent.FanControl;
    }

    public void SetLightingOverride(ManualOverride<LightingSettings>? value) =>
        SetOverride(value, ref temporaryLighting, ref persistentLighting);

    public void EnableTemporaryFullCharge()
    {
        if (PowerSource == PowerSource.Battery || !DesiredBattery.ProtectionEnabled)
        {
            return;
        }

        temporaryFullCharge = true;
    }

    public void CancelTemporaryFullCharge() => temporaryFullCharge = false;

    public DesiredDeviceState Resolve(ConfirmedDeviceState confirmed) =>
        Resolve(confirmed, supportsManualFansOnLimitedPower: true);

    public DesiredDeviceState Resolve(ConfirmedDeviceState confirmed,
        bool supportsManualFansOnLimitedPower)
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
        PerformanceOverride? activeRecoveredPerformance =
            recoveredHyperBoostPerformance is { } recovered
            && IsPerformanceCompatibleWithSource(recovered.Value, PowerSource)
                ? recovered
                : null;
        ManualOverride<FanMode>? activeRecoveredFans =
            recoveredHyperBoostFans is { } recoveredFan
            && IsFanCompatibleWithSource(recoveredFan.Value, PowerSource, supportsManualFansOnLimitedPower)
                ? recoveredFan
                : null;
        ManualOverride<FanMode>? activeTemporaryFans =
            temporaryFans is { } temporaryFan
            && IsFanCompatibleWithSource(temporaryFan.Value, PowerSource, supportsManualFansOnLimitedPower)
                ? temporaryFan
                : null;
        ManualOverride<FanMode>? activePersistentFans =
            persistentFans is { } persistentFan
            && IsFanCompatibleWithSource(persistentFan.Value, PowerSource, supportsManualFansOnLimitedPower)
                ? persistentFan
                : null;
        ResolvedSetting<PerformancePreset> resolvedPerformance =
            activeRecoveredPerformance is { } recoveredPerformance
                ? new ResolvedSetting<PerformancePreset>(
                    recoveredPerformance.Value,
                    SettingValueSource.TemporaryOverride)
                : ResolvePerformance(
                    profile.Performance,
                    activeTemporaryPerformance,
                    activePersistentPerformance);
        CustomPerformanceSettings? customPerformance = resolvedPerformance.Value
            == PerformancePreset.Custom
                ? activeRecoveredPerformance?.CustomPerformance
                    ?? activePersistentPerformance?.CustomPerformance
                    ?? activeTemporaryPerformance?.CustomPerformance
                    ?? profile.CustomPerformance
                    ?? confirmed.CustomPerformance
                    ?? CustomPerformanceSettings.Default
                : null;
        GpuClockTuningSettings? gpuClockTuning = PowerSource == PowerSource.PluggedIn
            ? activeRecoveredPerformance?.GpuClockTuning
                ?? activePersistentPerformance?.GpuClockTuning
                ?? activeTemporaryPerformance?.GpuClockTuning
                ?? profile.GpuClockTuning
            : null;
        if (PowerSource == PowerSource.PluggedIn
            && gpuClockTuning is null
            && resolvedPerformance.Value == PerformancePreset.Custom
            && customPerformance?.GpuClockOffsets is { } legacyOffsets)
        {
            gpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets);
            customPerformance = customPerformance with { GpuClockOffsets = null };
        }

        ResolvedSetting<FanMode> resolvedFans = activeRecoveredFans is { } recoveredFans
            ? new ResolvedSetting<FanMode>(
                recoveredFans.Value,
                SettingValueSource.TemporaryOverride)
            : OverrideResolver.Resolve<FanMode>(
                profile.Fans,
                confirmed.Fans.GetValueOrDefault(),
                activeTemporaryFans,
                activePersistentFans);
        if (PowerSource == PowerSource.UsbC
            && !FanControlPowerPolicy.IsAvailable(PowerSource, resolvedFans.Value,
                supportsManualFansOnLimitedPower))
        {
            // Retain saved manual intent for a compatible source. This exact
            // backend can only hand fans to firmware on limited external power.
            resolvedFans = new(FanMode.Automatic, SettingValueSource.PowerProfile);
        }
        ResolvedSetting<LightingSettings> resolvedLighting =
            ResolveLighting(profile, confirmed);
        FanControlSettings profileFanControl = profile.FanControl
            ?? FanControlSettings.FromLegacy(profile.Fans);
        FanControlSettings selectedFanControl = resolvedFans.Source switch
        {
            SettingValueSource.TemporaryOverride
                when activeRecoveredFans is not null =>
                    recoveredHyperBoostFanControl
                        ?? WithMode(profileFanControl, resolvedFans.Value),
            SettingValueSource.PersistentOverride => persistentFanControl
                ?? WithMode(profileFanControl, resolvedFans.Value),
            SettingValueSource.TemporaryOverride => temporaryFanControl
                ?? WithMode(profileFanControl, resolvedFans.Value),
            _ => WithMode(profileFanControl, resolvedFans.Value),
        };

        return new DesiredDeviceState(
            resolvedPerformance,
            resolvedFans,
            resolvedLighting,
            DesiredBattery)
        {
            CustomPerformance = customPerformance,
            GpuClockTuning = gpuClockTuning,
            FanControl = selectedFanControl,
        };
    }

    private ResolvedSetting<LightingSettings> ResolveLighting(
        PowerProfile profile,
        ConfirmedDeviceState confirmed)
    {
        if (PowerSource == PowerSource.ExternalUnknown)
        {
            return confirmed.Lighting is { } observed
                ? new ResolvedSetting<LightingSettings>(
                    observed,
                    SettingValueSource.ConfirmedFirmwareState)
                : new ResolvedSetting<LightingSettings>(
                    profile.Lighting,
                    SettingValueSource.PowerProfile);
        }

        return OverrideResolver.Resolve(
            profile.Lighting,
            confirmed.Lighting ?? profile.Lighting,
            temporaryLighting,
            persistentLighting);
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
        BatteryProtectionEnabled = DesiredBattery.ProtectionEnabled,
    };

    private PowerProfile? GetActiveProfile() => PowerSource switch
    {
        PowerSource.PluggedIn => profiles.PluggedIn,
        PowerSource.Battery => profiles.Battery,
        PowerSource.UsbC => profiles.UsbC,
        _ => null,
    };

    private static bool IsFanCompatibleWithSource(FanMode fans, PowerSource source,
        bool supportsManualFansOnLimitedPower) =>
        FanControlPowerPolicy.IsAvailable(source, fans, supportsManualFansOnLimitedPower);

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
