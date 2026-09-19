using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal enum ProfileDraftGroup
{
    Thermal,
    Lighting,
    Display,
    Laptop,
}

// Values already admitted by the current device capabilities and confirmed UI state.
internal readonly record struct ActiveProfileDraftValues(
    LogoLightingMode? LogoMode,
    bool? StartupAnimationEnabled,
    FunctionKeyPrimaryMode? FunctionKeyPrimary,
    bool? GamingModeEnabled);

/// <summary>Owns unsaved active-profile values and their original restore baselines.</summary>
internal sealed class ActiveProfileDraftState
{
    public PowerSource? Source { get; private set; }
    public PerformancePreset? Performance { get; private set; }
    public CustomPerformanceSettings? CustomPerformance { get; private set; }
    public GpuClockTuningSettings? GpuClockTuning { get; private set; }
    public FanMode? Fans { get; private set; }
    public FanControlSettings? FanControl { get; private set; }
    public LightingSettings? Lighting { get; private set; }
    public LightingPowerPolicy? LightingPowerPolicy { get; private set; }
    public DisplayProfileSettings? Display { get; private set; }
    public LaptopBehaviorProfileSettings? LaptopBehavior { get; private set; }
    public bool? BatteryProtectionEnabled { get; private set; }
    public WindowsPowerModeAssociations? WindowsPowerModes { get; private set; }
    public LaptopBehaviorProfileSettings? OriginalLaptopBehavior { get; private set; }
    public DisplayProfileSettings? OriginalDisplay { get; private set; }

    public bool HasActiveProfileDraft => Source is not null;

    public bool HasThermalProfileDraft =>
        Performance is not null
        || CustomPerformance is not null
        || GpuClockTuning is not null
        || Fans is not null
        || FanControl is not null
        || WindowsPowerModes is not null;

    public bool HasLightingProfileDraft => Lighting is not null
        || LightingPowerPolicy is not null;

    public bool HasDisplayProfileDraft => Display is not null;

    public bool HasLaptopProfileDraft => LaptopBehavior is not null;

    public bool HasBatteryProtectionProfileDraft =>
        BatteryProtectionEnabled is not null;

    public PowerProfile? GetDraft(ProfileSet profiles)
    {
        if (Source is not { } source)
        {
            return null;
        }

        PowerProfile profile = GetProfile(profiles, source);
        PowerProfile draft = profile with
        {
            Performance = Performance ?? profile.Performance,
            CustomPerformance = CustomPerformance
                ?? profile.CustomPerformance,
            GpuClockTuning = GpuClockTuning
                ?? profile.GpuClockTuning,
            Fans = Fans ?? profile.Fans,
            FanControl = FanControl ?? profile.FanControl,
            Lighting = Lighting ?? profile.Lighting,
            LightingPowerPolicy = LightingPowerPolicy
                ?? profile.LightingPowerPolicy,
            Display = Display ?? profile.Display,
            LaptopBehavior = LaptopBehavior
                ?? profile.LaptopBehavior,
            BatteryProtectionEnabled = BatteryProtectionEnabled
                ?? profile.BatteryProtectionEnabled,
        };
        return NormalizeLegacyGpuClockTuning(draft);
    }

    public ProfileSet CreateProfileSet(ProfileSet savedProfiles)
    {
        PowerSource source = Source
            ?? throw new InvalidOperationException("No active profile draft exists.");
        PowerProfile draft = GetDraft(savedProfiles)
            ?? throw new InvalidOperationException("No active profile draft exists.");
        ProfileSet profiles = source switch
        {
            PowerSource.PluggedIn => savedProfiles with { PluggedIn = draft },
            PowerSource.Battery => savedProfiles with { Battery = draft },
            PowerSource.UsbC => savedProfiles with { UsbC = draft },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        return profiles with { WindowsPowerModes = WindowsPowerModes ?? savedProfiles.WindowsPowerModes };
    }

    public ProfileSet CreateProfileSet(ProfileSet savedProfiles, ProfileDraftGroup group)
    {
        PowerSource source = Source
            ?? throw new InvalidOperationException("No active profile draft exists.");
        PowerProfile saved = GetProfile(savedProfiles, source);
        PowerProfile draft = group switch
        {
            ProfileDraftGroup.Thermal => saved with
            {
                Performance = Performance ?? saved.Performance,
                CustomPerformance = CustomPerformance
                    ?? saved.CustomPerformance,
                GpuClockTuning = GpuClockTuning
                    ?? saved.GpuClockTuning,
                Fans = Fans ?? saved.Fans,
                FanControl = FanControl ?? saved.FanControl,
            },
            ProfileDraftGroup.Lighting => saved with
            {
                Lighting = Lighting ?? saved.Lighting,
                LightingPowerPolicy = LightingPowerPolicy
                    ?? saved.LightingPowerPolicy,
            },
            ProfileDraftGroup.Display => saved with
            {
                Display = Display ?? saved.Display,
            },
            ProfileDraftGroup.Laptop => saved with
            {
                Performance = Performance ?? saved.Performance,
                CustomPerformance = CustomPerformance
                    ?? saved.CustomPerformance,
                GpuClockTuning = GpuClockTuning
                    ?? saved.GpuClockTuning,
                Fans = Fans ?? saved.Fans,
                FanControl = FanControl ?? saved.FanControl,
                LaptopBehavior = LaptopBehavior
                    ?? saved.LaptopBehavior,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };
        draft = NormalizeLegacyGpuClockTuning(draft);
        ProfileSet profiles = source switch
        {
            PowerSource.PluggedIn => savedProfiles with { PluggedIn = draft },
            PowerSource.Battery => savedProfiles with { Battery = draft },
            PowerSource.UsbC => savedProfiles with { UsbC = draft },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        return group is ProfileDraftGroup.Thermal or ProfileDraftGroup.Laptop
            ? profiles with { WindowsPowerModes = WindowsPowerModes ?? savedProfiles.WindowsPowerModes }
            : profiles;
    }

    public bool Stage(
        ProfileSet profiles,
        PowerSource activePowerSource,
        PowerSource source,
        PerformancePreset? performance = null,
        CustomPerformanceSettings? customPerformance = null,
        GpuClockTuningSettings? gpuClockTuning = null,
        FanMode? fans = null,
        FanControlSettings? fanControl = null,
        LightingSettings? lighting = null,
        LightingPowerPolicy? lightingPowerPolicy = null,
        DisplayProfileSettings? display = null,
        DisplayProfileSettings? previousDisplay = null,
        LaptopBehaviorProfileSettings? laptopBehavior = null,
        LaptopBehaviorProfileSettings? previousLaptopBehavior = null,
        bool? batteryProtectionEnabled = null,
        WindowsPowerModeAssociations? windowsPowerModes = null)
    {
        if (source != activePowerSource
            || source is not (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return false;
        }

        if (Source != source)
        {
            // Windows associations are global and survive a per-source draft reset.
            Source = source;
            Performance = null;
            CustomPerformance = null;
            GpuClockTuning = null;
            Fans = null;
            FanControl = null;
            Lighting = null;
            LightingPowerPolicy = null;
            Display = null;
            LaptopBehavior = null;
            BatteryProtectionEnabled = null;
            OriginalDisplay = previousDisplay;
            OriginalLaptopBehavior = previousLaptopBehavior;
        }
        else if (display is not null && Display is null)
        {
            OriginalDisplay = previousDisplay;
        }
        else if (laptopBehavior is not null
            && LaptopBehavior is null)
        {
            OriginalLaptopBehavior = previousLaptopBehavior;
        }

        Performance = performance ?? Performance;
        CustomPerformance = customPerformance
            ?? CustomPerformance;
        GpuClockTuning = gpuClockTuning
            ?? GpuClockTuning;
        Fans = fans ?? Fans;
        FanControl = fanControl ?? FanControl;
        Lighting = lighting ?? Lighting;
        LightingPowerPolicy = lightingPowerPolicy
            ?? LightingPowerPolicy;
        Display = display ?? Display;
        LaptopBehavior = laptopBehavior
            ?? LaptopBehavior;
        BatteryProtectionEnabled = batteryProtectionEnabled
            ?? BatteryProtectionEnabled;
        WindowsPowerModes = windowsPowerModes
            ?? WindowsPowerModes;

        PowerProfile saved = GetProfile(profiles, source);
        if (Performance == saved.Performance)
        {
            Performance = null;
        }
        if (CustomPerformance == saved.CustomPerformance)
        {
            CustomPerformance = null;
        }
        if (GpuClockTuning == saved.GpuClockTuning)
        {
            GpuClockTuning = null;
        }
        if (Fans == saved.Fans)
        {
            Fans = null;
        }
        if (FanControl == saved.FanControl)
        {
            FanControl = null;
        }
        if (Lighting == saved.Lighting)
        {
            Lighting = null;
        }
        if (LightingPowerPolicy
            == saved.EffectiveLightingPowerPolicy)
        {
            LightingPowerPolicy = null;
        }
        if (BatteryProtectionEnabled
            == saved.EffectiveBatteryProtectionEnabled)
        {
            BatteryProtectionEnabled = null;
        }
        if (WindowsPowerModes == profiles.WindowsPowerModes)
        {
            WindowsPowerModes = null;
        }
        DisplayProfileSettings savedDisplay =
            saved.Display ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings displayBaseline = savedDisplay.IsConfigured
            ? savedDisplay
            : OriginalDisplay ?? savedDisplay;
        if (Display == displayBaseline)
        {
            Display = null;
            OriginalDisplay = null;
        }
        LaptopBehaviorProfileSettings savedLaptopBehavior =
            saved.EffectiveLaptopBehavior;
        LaptopBehaviorProfileSettings laptopBehaviorBaseline =
            savedLaptopBehavior.IsConfigured
                ? savedLaptopBehavior
                : OriginalLaptopBehavior ?? savedLaptopBehavior;
        if (LaptopBehavior == laptopBehaviorBaseline)
        {
            LaptopBehavior = null;
            OriginalLaptopBehavior = null;
        }
        if (Performance is null
            && CustomPerformance is null
            && GpuClockTuning is null
            && Fans is null
            && FanControl is null
            && Lighting is null
            && LightingPowerPolicy is null
            && Display is null
            && LaptopBehavior is null
            && BatteryProtectionEnabled is null
            && WindowsPowerModes is null)
        {
            Source = null;
        }

        return true;
    }

    public void Clear()
    {
        Source = null;
        Performance = null;
        CustomPerformance = null;
        GpuClockTuning = null;
        Fans = null;
        FanControl = null;
        Lighting = null;
        LightingPowerPolicy = null;
        Display = null;
        LaptopBehavior = null;
        BatteryProtectionEnabled = null;
        WindowsPowerModes = null;
        OriginalDisplay = null;
        OriginalLaptopBehavior = null;
    }

    public void Clear(ProfileDraftGroup group)
    {
        switch (group)
        {
            case ProfileDraftGroup.Thermal:
                Performance = null;
                CustomPerformance = null;
                GpuClockTuning = null;
                Fans = null;
                FanControl = null;
                WindowsPowerModes = null;
                break;
            case ProfileDraftGroup.Lighting:
                Lighting = null;
                LightingPowerPolicy = null;
                break;
            case ProfileDraftGroup.Display:
                Display = null;
                OriginalDisplay = null;
                break;
            case ProfileDraftGroup.Laptop:
                Performance = null;
                CustomPerformance = null;
                GpuClockTuning = null;
                Fans = null;
                FanControl = null;
                WindowsPowerModes = null;
                LaptopBehavior = null;
                OriginalLaptopBehavior = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }

        if (!HasThermalProfileDraft
            && !HasLightingProfileDraft
            && !HasDisplayProfileDraft
            && !HasLaptopProfileDraft
            && !HasBatteryProtectionProfileDraft)
        {
            Source = null;
        }
    }

    public bool ChangePowerSource(PowerSource source)
    {
        if (source is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            && Source is { } draftSource
            && draftSource != source)
        {
            WindowsPowerModeAssociations? windowsPowerModes =
                WindowsPowerModes;
            Clear();
            if (windowsPowerModes is not null)
            {
                Source = source;
                WindowsPowerModes = windowsPowerModes;
            }
            return true;
        }

        return false;
    }

    public LaptopBehaviorProfileSettings RebaseGamingModeKeyPolicy(
        LaptopBehaviorProfileSettings draft, GamingModeKeyPolicy policy)
    {
        if (OriginalLaptopBehavior is not null)
        {
            OriginalLaptopBehavior = OriginalLaptopBehavior with { GamingModeKeyPolicy = policy };
        }
        LaptopBehavior = draft with { GamingModeKeyPolicy = policy };
        return LaptopBehavior;
    }

    public static bool HasSaveableUnsetField(
        ProfileDraftGroup group, PowerProfile saved, ActiveProfileDraftValues values) => group switch
    {
        ProfileDraftGroup.Lighting => saved.Lighting.LogoMode is null && values.LogoMode.HasValue,
        ProfileDraftGroup.Laptop =>
            saved.EffectiveLaptopBehavior.StartupAnimationEnabled is null
                && values.StartupAnimationEnabled.HasValue
            || saved.EffectiveLaptopBehavior.FunctionKeyPrimary is null
                && values.FunctionKeyPrimary.HasValue
            || saved.EffectiveLaptopBehavior.GamingModeEnabled is null
                && values.GamingModeEnabled.HasValue,
        _ => false,
    };

    public bool CaptureSaveableUnsetFields(
        ProfileSet profiles, PowerSource source, ProfileDraftGroup group, ActiveProfileDraftValues values)
    {
        PowerProfile saved = GetProfile(profiles, source);
        if (group == ProfileDraftGroup.Lighting
            && saved.Lighting.LogoMode is null && values.LogoMode is { } logoMode)
        {
            LightingSettings draft = Lighting ?? saved.Lighting;
            return Stage(profiles, source, source, lighting: draft with { LogoMode = logoMode });
        }
        if (group != ProfileDraftGroup.Laptop)
        {
            return false;
        }
        LaptopBehaviorProfileSettings baseline = LaptopBehavior ?? saved.EffectiveLaptopBehavior;
        var draftBehavior = new LaptopBehaviorProfileSettings(
            baseline.StartupAnimationEnabled ?? values.StartupAnimationEnabled,
            baseline.FunctionKeyPrimary ?? values.FunctionKeyPrimary,
            baseline.GamingModeEnabled ?? values.GamingModeEnabled)
        {
            GamingModeKeyPolicy = baseline.GamingModeKeyPolicy,
        };
        return draftBehavior != baseline
            && Stage(profiles, source, source, laptopBehavior: draftBehavior);
    }

    public static (
        CustomPerformanceSettings? CustomPerformance,
        GpuClockTuningSettings? GpuClockTuning) NormalizeLegacyGpuClockTuning(
            CustomPerformanceSettings? customPerformance,
            GpuClockTuningSettings? gpuClockTuning)
    {
        if (customPerformance?.GpuClockOffsets is not { } legacyOffsets)
        {
            return (customPerformance, gpuClockTuning);
        }

        return (
            customPerformance with { GpuClockOffsets = null },
            gpuClockTuning ?? new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets));
    }

    private static PowerProfile NormalizeLegacyGpuClockTuning(PowerProfile profile)
    {
        if (profile.Performance != PerformancePreset.Custom
            && profile.GpuClockTuning is null)
        {
            return profile;
        }

        var normalized = NormalizeLegacyGpuClockTuning(
            profile.CustomPerformance,
            profile.GpuClockTuning);
        return profile with
        {
            CustomPerformance = normalized.CustomPerformance,
            GpuClockTuning = normalized.GpuClockTuning,
        };
    }

    private static PowerProfile GetProfile(ProfileSet profiles, PowerSource source) => source switch
    {
        PowerSource.PluggedIn => profiles.PluggedIn,
        PowerSource.Battery => profiles.Battery,
        PowerSource.UsbC => profiles.UsbC,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
