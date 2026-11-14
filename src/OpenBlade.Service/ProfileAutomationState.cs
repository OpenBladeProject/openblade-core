using OpenBlade.Contracts;
using OpenBlade.Core.Native;

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
    private Guid? activeApplicationProfileId;
    private string? activeApplicationExecutablePath;

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

    public Guid? ActiveApplicationProfileId => activeApplicationProfileId;

    public LaptopBehaviorProfileSettings EffectiveLaptopBehavior
    {
        get
        {
            PowerProfile? baseline = GetActiveProfile();
            return baseline is null
                ? LaptopBehaviorProfileSettings.Unconfigured()
                : ApplicationProfileResolution.Apply(
                    baseline,
                    GetApplicationOverride()).EffectiveLaptopBehavior;
        }
    }

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
        if (activeApplicationProfileId.HasValue
            && !profiles.ApplicationProfiles.Any(profile =>
                profile.Enabled
                && profile.Id == activeApplicationProfileId.Value
                && string.Equals(
                    profile.ExecutablePath,
                    activeApplicationExecutablePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            activeApplicationProfileId = null;
            activeApplicationExecutablePath = null;
        }
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

    public bool ChangePowerSource(PowerSource powerSource, bool preserveUnclassifiedExternalOverrides = false)
    {
        if (PowerSource == powerSource)
        {
            return false;
        }

        bool preserve = preserveUnclassifiedExternalOverrides
            && powerSource is PowerSource.PluggedIn or PowerSource.UsbC;
        PowerSource = powerSource;
        if (preserve)
        {
            return true;
        }
        ExpireTransientOverridesForPowerBoundary();
        if (powerSource == PowerSource.Battery && temporaryFullCharge)
        {
            CancelTemporaryFullCharge();
        }

        return true;
    }

    // A physical boundary can retire transient intent before its external
    // wattage is known. Keep the classified profile slot as retained data.
    internal void ExpireTransientOverridesForPowerBoundary()
    {
        recoveredHyperBoostPerformance = null;
        recoveredHyperBoostFans = null;
        recoveredHyperBoostFanControl = null;
        temporaryPerformance = null;
        temporaryFans = null;
        temporaryFanControl = null;
        temporaryLighting = null;
        temporaryBatteryProtectionEnabled = null;
    }

    public bool SetApplicationContext(string? executablePath)
    {
        Guid? next = null;
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            normalized = ApplicationProfileDefinition.NormalizeExecutablePath(executablePath);
            next = profiles.ApplicationProfiles.FirstOrDefault(profile =>
                profile.Enabled
                && string.Equals(profile.ExecutablePath, normalized,
                    StringComparison.OrdinalIgnoreCase))?.Id;
        }
        if (next == activeApplicationProfileId
            && (!next.HasValue
                || string.Equals(
                    normalized,
                    activeApplicationExecutablePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        activeApplicationProfileId = next;
        activeApplicationExecutablePath = next.HasValue ? normalized : null;
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
        bool supportsManualFansOnLimitedPower,
        PowerSource? observedPowerSource = null,
        bool allowUnclassifiedExternalOrdinaryOverrides = false,
        bool allowUnclassifiedExternalLightingOverrides = false)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        PowerSource source = observedPowerSource ?? PowerSource;
        bool ordinaryExternalOverrides = source == PowerSource.ExternalUnknown
            && allowUnclassifiedExternalOrdinaryOverrides;
        PowerProfile baseProfile = source switch
        {
            PowerSource.PluggedIn => profiles.PluggedIn,
            PowerSource.Battery => profiles.Battery,
            PowerSource.UsbC => profiles.UsbC,
            PowerSource.ExternalUnknown => CreateObservedFallbackProfile(confirmed),
            _ => throw new InvalidOperationException("The active power source is undefined."),
        };
        ApplicationPowerProfileOverride? applicationOverride = source == PowerSource.ExternalUnknown
            ? null : GetApplicationOverride();
        PowerProfile profile = ApplicationProfileResolution.Apply(
            baseProfile,
            applicationOverride);
        PerformanceOverride? activePersistentPerformance =
            persistentPerformance is { } persistent
            && (IsPerformanceCompatibleWithSource(persistent.Value, source) || ordinaryExternalOverrides && IsOrdinaryExternalOverride(persistent))
                ? persistent
                : null;
        PerformanceOverride? activeTemporaryPerformance =
            temporaryPerformance is { } temporary
            && (IsPerformanceCompatibleWithSource(temporary.Value, source) || ordinaryExternalOverrides && IsOrdinaryExternalOverride(temporary))
                ? temporary
                : null;
        PerformanceOverride? activeRecoveredPerformance =
            recoveredHyperBoostPerformance is { } recovered
            && IsPerformanceCompatibleWithSource(recovered.Value, source)
                ? recovered
                : null;
        ManualOverride<FanMode>? activeRecoveredFans =
            recoveredHyperBoostFans is { } recoveredFan
            && IsFanCompatibleWithSource(recoveredFan.Value, source, supportsManualFansOnLimitedPower)
                ? recoveredFan
                : null;
        ManualOverride<FanMode>? activeTemporaryFans =
            temporaryFans is { } temporaryFan
            && (IsFanCompatibleWithSource(temporaryFan.Value, source, supportsManualFansOnLimitedPower)
                || ordinaryExternalOverrides && temporaryFan.Value != FanMode.Stop)
                ? temporaryFan
                : null;
        ManualOverride<FanMode>? activePersistentFans =
            persistentFans is { } persistentFan
            && (IsFanCompatibleWithSource(persistentFan.Value, source, supportsManualFansOnLimitedPower)
                || ordinaryExternalOverrides && persistentFan.Value != FanMode.Stop)
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
                    activePersistentPerformance,
                    applicationOverride is { Performance: not null }
                        or { CustomPerformance: not null }
                        or { GpuClockTuning: not null }
                        ? SettingValueSource.ApplicationProfile
                        : SettingValueSource.PowerProfile);
        CustomPerformanceSettings? customPerformance = resolvedPerformance.Value
            == PerformancePreset.Custom
                ? activeRecoveredPerformance?.CustomPerformance
                    ?? activePersistentPerformance?.CustomPerformance
                    ?? activeTemporaryPerformance?.CustomPerformance
                    ?? profile.CustomPerformance
                    ?? confirmed.CustomPerformance
                    ?? CustomPerformanceSettings.Default
                : null;
        GpuClockTuningSettings? gpuClockTuning = source == PowerSource.PluggedIn
            ? activeRecoveredPerformance?.GpuClockTuning
                ?? activePersistentPerformance?.GpuClockTuning
                ?? activeTemporaryPerformance?.GpuClockTuning
                ?? profile.GpuClockTuning
            : null;
        if (source == PowerSource.PluggedIn
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
                activePersistentFans,
                applicationOverride is { Fans: not null } or { FanControl: not null }
                    ? SettingValueSource.ApplicationProfile
                    : SettingValueSource.PowerProfile);
        if (source == PowerSource.ExternalUnknown)
        {
            if (resolvedPerformance.Source == SettingValueSource.PowerProfile && confirmed.Performance.HasValue)
            {
                resolvedPerformance = resolvedPerformance with { Source = SettingValueSource.ConfirmedFirmwareState };
            }
            if (resolvedFans.Source == SettingValueSource.PowerProfile && confirmed.Fans.HasValue)
            {
                resolvedFans = resolvedFans with { Source = SettingValueSource.ConfirmedFirmwareState };
            }
        }
        if (source == PowerSource.UsbC
            && !FanControlPowerPolicy.IsAvailable(source, resolvedFans.Value,
                supportsManualFansOnLimitedPower))
        {
            // Retain saved manual intent for a compatible source. This exact
            // backend can only hand fans to firmware on limited external power.
            resolvedFans = new(FanMode.Automatic, SettingValueSource.PowerProfile);
        }
        ResolvedSetting<LightingSettings> resolvedLighting =
            ResolveLightingWithPowerContext(
                profile,
                confirmed,
                applicationOverride?.Lighting is not null,
                source, allowUnclassifiedExternalLightingOverrides);
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
        PowerProfile profile, ConfirmedDeviceState confirmed, bool fromApplication) =>
        ResolveLightingWithPowerContext(profile, confirmed, fromApplication, PowerSource,
            allowUnclassifiedExternalOverrides: false);

    private ResolvedSetting<LightingSettings> ResolveLightingWithPowerContext(
        PowerProfile profile,
        ConfirmedDeviceState confirmed,
        bool fromApplication, PowerSource source, bool allowUnclassifiedExternalOverrides)
    {
        bool externalUnknown = source == PowerSource.ExternalUnknown;
        // Preserve the original unknown-source branch's lazy profile access:
        // observed firmware lighting does not consume saved profile intent.
        LightingSettings? observed = externalUnknown ? confirmed.Lighting : null;
        LightingSettings? profileValue = externalUnknown && observed is not null
            ? null
            : profile.Lighting;
        observed ??= confirmed.Lighting;
        NativeAutomationSelection selection = RequireSelection(
            externalUnknown && allowUnclassifiedExternalOverrides
                && (temporaryLighting is not null || persistentLighting is not null)
                ? ProductNativeRuntime.Automation.SelectExplicitOverride(
                    temporaryLighting is not null, persistentLighting is not null)
                : ProductNativeRuntime.Automation.SelectLighting(
                profileValue is not null,
                observed is not null,
                temporaryLighting is not null,
                persistentLighting is not null,
                externalUnknown,
                fromApplication));
        LightingSettings? value = selection.Origin switch
        {
            AutomationValueOrigin.Profile => profileValue,
            AutomationValueOrigin.Confirmed => observed ?? profileValue,
            AutomationValueOrigin.Temporary => temporaryLighting!.Value,
            AutomationValueOrigin.Persistent => persistentLighting!.Value,
            _ => throw new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult),
        };
        return new(value!, selection.Source);
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

    private static bool IsOrdinaryExternalOverride(PerformanceOverride value) =>
        value.GpuClockTuning is null && value.CustomPerformance?.GpuClockOffsets is null
        && IsOrdinaryExternalPreset(value.Value);

    private static bool IsOrdinaryExternalPreset(PerformancePreset value) => value is
        PerformancePreset.Silent or PerformancePreset.Balanced or PerformancePreset.Performance
        or PerformancePreset.Custom or PerformancePreset.Turbo or PerformancePreset.MaximumPower
        or PerformancePreset.Gaming or PerformancePreset.Creator;

    private static bool IsPerformanceCompatibleWithSource(
        PerformancePreset performance,
        PowerSource source)
    {
        var (eligible, error) = ProductNativeRuntime.Automation.CheckPerformanceSource(performance, source);
        if (error is not null)
        {
            throw new NativeComponentException(error);
        }
        return eligible;
    }

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
        PerformanceOverride? persistentOverride,
        SettingValueSource profileSource)
    {
        NativeAutomationSelection selection = RequireSelection(
            ProductNativeRuntime.Automation.SelectPerformance(
                temporaryOverride is not null,
                persistentOverride is not null,
                profileSource == SettingValueSource.ApplicationProfile));
        PerformancePreset value = selection.Origin switch
        {
            AutomationValueOrigin.Profile => profileValue,
            AutomationValueOrigin.Temporary => temporaryOverride!.Value,
            AutomationValueOrigin.Persistent => persistentOverride!.Value,
            _ => throw new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult),
        };
        // The profile source is host data, including legacy/undefined values;
        // only its origin participates in the portable selection.
        return new(value, selection.Origin == AutomationValueOrigin.Profile
            ? profileSource
            : selection.Source);
    }

    private static NativeAutomationSelection RequireSelection(NativeAutomationResult result)
    {
        if (result.Error is not null)
        {
            throw new NativeComponentException(result.Error);
        }
        return result.Selection
            ?? throw new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
    }

    private ApplicationPowerProfileOverride? GetApplicationOverride()
    {
        if (!activeApplicationProfileId.HasValue)
        {
            return null;
        }
        return profiles.ApplicationProfiles.FirstOrDefault(profile =>
                profile.Enabled && profile.Id == activeApplicationProfileId.Value)
            ?.Overrides.ForPowerSource(PowerSource);
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
