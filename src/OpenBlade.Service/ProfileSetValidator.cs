using OpenBlade.Contracts;

namespace OpenBlade.Service;

public readonly record struct PerformanceFanCombination(
    PerformancePreset Performance,
    FanMode Fans);

public sealed class ProfileValidationPolicy
{
    public ProfileValidationPolicy(
        IReadOnlySet<PerformanceFanCombination> supportedPerformanceFanCombinations,
        IReadOnlySet<LightingEffect> supportedLightingEffects,
        CustomPerformanceCapabilities? customPerformance = null,
        FanSpeedRange? fanSpeedRange = null,
        string? keyboardLightingLayoutId = null,
        bool supportsBreathingColorSelection = true,
        bool supportsWaveDirectionSelection = true,
        IReadOnlySet<BreathingColorMode>? supportedBreathingColorModes = null)
    {
        ArgumentNullException.ThrowIfNull(supportedPerformanceFanCombinations);
        ArgumentNullException.ThrowIfNull(supportedLightingEffects);
        SupportedPerformanceFanCombinations =
            new HashSet<PerformanceFanCombination>(supportedPerformanceFanCombinations);
        SupportedLightingEffects = new HashSet<LightingEffect>(supportedLightingEffects);
        CustomPerformance = customPerformance is null
            ? null
            : new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>(
                    customPerformance.AllowedCombinations))
            {
                SupportsMaximumFans = customPerformance.SupportsMaximumFans,
            };
        FanSpeedRange = fanSpeedRange;
        KeyboardLightingLayoutId = keyboardLightingLayoutId;
        SupportsBreathingColorSelection = supportsBreathingColorSelection;
        SupportedBreathingColorModes = supportedBreathingColorModes is null
            ? supportsBreathingColorSelection
                ? Enum.GetValues<BreathingColorMode>().ToHashSet()
                : new HashSet<BreathingColorMode> { BreathingColorMode.Random }
            : new HashSet<BreathingColorMode>(supportedBreathingColorModes);
        SupportsWaveDirectionSelection = supportsWaveDirectionSelection;
    }

    public IReadOnlySet<PerformanceFanCombination> SupportedPerformanceFanCombinations { get; }

    public IReadOnlySet<LightingEffect> SupportedLightingEffects { get; }

    public CustomPerformanceCapabilities? CustomPerformance { get; }

    public FanSpeedRange? FanSpeedRange { get; }

    public string? KeyboardLightingLayoutId { get; }

    public bool SupportsBreathingColorSelection { get; }

    public IReadOnlySet<BreathingColorMode> SupportedBreathingColorModes { get; }

    public bool SupportsWaveDirectionSelection { get; }

    public static ProfileValidationPolicy CreateUnverifiedSafeDefaults() => new(
        new HashSet<PerformanceFanCombination>
        {
            new(PerformancePreset.Balanced, FanMode.Automatic),
            new(PerformancePreset.Silent, FanMode.Automatic),
            new(PerformancePreset.Performance, FanMode.Automatic),
            new(PerformancePreset.Custom, FanMode.Automatic),
            new(PerformancePreset.Balanced, FanMode.Maximum),
            new(PerformancePreset.Balanced, FanMode.Fixed),
            new(PerformancePreset.Balanced, FanMode.ManualCurve),
            new(PerformancePreset.Silent, FanMode.Maximum),
            new(PerformancePreset.Silent, FanMode.Fixed),
            new(PerformancePreset.Silent, FanMode.ManualCurve),
            new(PerformancePreset.Performance, FanMode.Maximum),
            new(PerformancePreset.Performance, FanMode.Fixed),
            new(PerformancePreset.Performance, FanMode.ManualCurve),
            new(PerformancePreset.Custom, FanMode.Maximum),
            new(PerformancePreset.Custom, FanMode.Fixed),
            new(PerformancePreset.Custom, FanMode.ManualCurve),
            new(PerformancePreset.BatterySaver, FanMode.Automatic),
            new(PerformancePreset.BatterySaver, FanMode.Maximum),
            new(PerformancePreset.BatterySaver, FanMode.Fixed),
            new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
            new(PerformancePreset.BatteryBalanced, FanMode.Maximum),
            new(PerformancePreset.BatteryBalanced, FanMode.Fixed),
        },
        new HashSet<LightingEffect> { LightingEffect.Off },
        // Offline storage validates intent structurally, including capabilities
        // from a temporarily unavailable model. It never supplies write authority.
        new CustomPerformanceCapabilities(
            (from cpu in Enum.GetValues<CustomPerformanceLevel>()
             from gpu in new[] { CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium, CustomPerformanceLevel.High }
             select new CustomPerformanceCombination(cpu, gpu)).ToHashSet())
        {
            SupportsMaximumFans = true,
        });

    public static ProfileValidationPolicy CreateForDeviceCapabilities(
        DeviceCapabilities batteryCapabilities,
        DeviceCapabilities lightingCapabilities)
    {
        ArgumentNullException.ThrowIfNull(batteryCapabilities);
        ArgumentNullException.ThrowIfNull(lightingCapabilities);
        ProfileValidationPolicy safeDefaults = CreateUnverifiedSafeDefaults();
        var supportedLightingEffects = new HashSet<LightingEffect>
        {
            LightingEffect.Off,
        };
        supportedLightingEffects.UnionWith(
            lightingCapabilities.LightingEffects.Where(Enum.IsDefined));
        return new ProfileValidationPolicy(
            safeDefaults.SupportedPerformanceFanCombinations,
            supportedLightingEffects,
            batteryCapabilities.CustomPerformance
                ?? new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>()),
            batteryCapabilities.FanSpeedRange,
            lightingCapabilities.KeyboardLightingLayoutId,
            lightingCapabilities.SupportsBreathingColorSelection,
            lightingCapabilities.SupportsWaveDirectionSelection,
            lightingCapabilities.SupportedBreathingColorModes);
    }
}

public sealed class ProfileSetValidator(ProfileValidationPolicy policy)
{
    private static readonly RgbColor NeutralColor = new(0, 0, 0);
    private readonly ProfileValidationPolicy validationPolicy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    public static ProfileSetValidator CreateUnverifiedSafeDefaults() =>
        new(ProfileValidationPolicy.CreateUnverifiedSafeDefaults());

    public static ProfileSetValidator CreateForDeviceCapabilities(
        DeviceCapabilities capabilities) =>
        CreateForDeviceCapabilities(capabilities, capabilities);

    public static ProfileSetValidator CreateForLightingCapabilities(
        DeviceCapabilities capabilities) =>
        CreateForDeviceCapabilities(capabilities);

    public static ProfileSetValidator CreateForDeviceCapabilities(
        DeviceCapabilities batteryCapabilities,
        DeviceCapabilities lightingCapabilities) =>
        new(ProfileValidationPolicy.CreateForDeviceCapabilities(
            batteryCapabilities,
            lightingCapabilities));

    public void Validate(ProfileSet profiles)
        => ValidateCore(profiles, validateCustomCapabilities: true);

    // The runtime separately admits changed Custom intent against current device
    // capabilities, while unrelated edits preserve existing unavailable intent.
    internal void ValidateStoredCustomIntent(ProfileSet profiles)
        => ValidateCore(profiles, validateCustomCapabilities: false);

    private void ValidateCore(ProfileSet profiles, bool validateCustomCapabilities)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateProfile(profiles.PluggedIn, "plugged-in", PowerSource.PluggedIn, validateCustomCapabilities);
        ValidateProfile(profiles.Battery, "battery", PowerSource.Battery, validateCustomCapabilities);
        ValidateProfile(profiles.UsbC, "low-power", PowerSource.UsbC, validateCustomCapabilities);
        ValidateBatteryPolicy(profiles.BatteryPolicy);
        ValidateLightingPowerPolicy(profiles.PluggedIn.LightingPowerPolicy);
        ValidateLightingPowerPolicy(profiles.Battery.LightingPowerPolicy);
        ValidateLightingPowerPolicy(profiles.UsbC.LightingPowerPolicy);
        ValidateLightingPowerPolicy(profiles.LightingPowerPolicy);
        ValidateWindowsPowerModes(profiles.WindowsPowerModes);
    }

    public void ValidatePreferences(BatteryAutomationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.KeyboardLightingControlMode))
        {
            throw Invalid("The keyboard lighting control mode is undefined.");
        }

        if (!Enum.IsDefined(preferences.LightingRefreshRate))
        {
            throw Invalid("The lighting refresh rate is undefined.");
        }

        if (!Enum.IsDefined(preferences.ThermalControlPolicy))
        {
            throw Invalid("The thermal control policy is undefined.");
        }

        if (!Enum.IsDefined(preferences.ThermalPolicyUpgradeDisclosure))
        {
            throw Invalid("The thermal policy upgrade disclosure state is undefined.");
        }

        if (preferences.LastKnownLighting is { } lighting)
        {
            ValidateLighting(lighting, "last-known");
        }
    }

    public void ValidatePerformanceFanCombination(
        PerformancePreset performance,
        FanMode fans)
    {
        if (!Enum.IsDefined(performance))
        {
            throw Invalid("The performance preset is undefined.");
        }

        if (!Enum.IsDefined(fans))
        {
            throw Invalid("The fan mode is undefined.");
        }

        if (!validationPolicy.SupportedPerformanceFanCombinations.Contains(
                new PerformanceFanCombination(performance, fans)))
        {
            throw Invalid(
                "The performance and fan combination is not supported by the active validation policy.");
        }
    }

    public void ValidatePerformanceFanCombination(
        PerformancePreset performance,
        FanMode fans,
        PowerSource source)
    {
        ValidatePerformanceSource(performance, source);
        if (!FanControlPowerPolicy.IsAvailable(source, fans))
        {
            throw Invalid("The selected fan mode is unavailable on this power source.");
        }

        ValidatePerformanceFanCombination(performance, fans);
    }

    private void ValidateProfile(
        PowerProfile? profile,
        string name,
        PowerSource source,
        bool validateCustomCapabilities)
    {
        if (profile is null)
        {
            throw Invalid($"The {name} profile is missing.");
        }

        try
        {
            ValidatePerformanceFanCombination(profile.Performance, profile.Fans, source);
        }
        catch (InvalidDataException exception)
        {
            throw Invalid($"The {name} profile is invalid: {exception.Message}");
        }

        ValidateLighting(profile.Lighting, name);
        ValidateCustomPerformance(profile, name, validateCustomCapabilities);
        ValidateGpuClockTuning(profile, name, source);
        ValidateFanControl(profile, name);
        ValidateDisplay(profile.Display, name);
        ValidateLaptopBehavior(profile.LaptopBehavior, name);
        ValidateThxAudio(profile.ThxAudio, name);
        if (!profile.BatteryProtectionEnabled.HasValue)
        {
            throw Invalid($"The {name} profile battery protection setting is missing.");
        }
    }

    private static void ValidateWindowsPowerModes(
        WindowsPowerModeAssociations associations)
    {
        if (associations is null)
        {
            throw Invalid("The Windows power mode associations are missing.");
        }
        foreach (PerformancePreset performance in Enum.GetValues<PerformancePreset>())
        {
            if (associations.ForPerformance(performance) is { } windowsPowerMode
                && !Enum.IsDefined(windowsPowerMode))
            {
                throw Invalid(
                    $"The Windows power mode associated with {performance} is undefined.");
            }
        }
    }

    private static void ValidatePerformanceSource(
        PerformancePreset performance,
        PowerSource source)
    {
        bool valid = source switch
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
        if (!valid)
        {
            throw Invalid("The performance preset is not valid for this power source.");
        }
    }

    private void ValidateCustomPerformance(PowerProfile profile, string name, bool validateCapabilities)
    {
        if (profile.Performance == PerformancePreset.Custom
            && profile.CustomPerformance?.MaximumFans == true && profile.Fans != FanMode.Automatic)
        {
            throw Invalid($"The {name} Custom maximum-fan flag requires Automatic fan mode, not fixed targets or a curve.");
        }
        if (profile.Performance == PerformancePreset.Custom
            && !CustomPerformanceSettings.IsWellFormed(profile.CustomPerformance))
        {
            throw Invalid(
                $"The {name} custom performance CPU/GPU levels are missing or invalid.");
        }

        if (profile.CustomPerformance is not null
            && !CustomPerformanceSettings.IsWellFormed(profile.CustomPerformance))
        {
            throw Invalid($"The {name} saved custom performance levels are invalid.");
        }

        if (validateCapabilities && profile.CustomPerformance is not null
            && (validationPolicy.CustomPerformance is { } admitted
                ? !admitted.Supports(profile.CustomPerformance)
                // Storage-only legacy policies must still load unchanged saved
                // settings. They never admit any new combination or fan flag;
                // hardware setters separately require explicit device support.
                : profile.CustomPerformance.Cpu == CustomPerformanceLevel.Boost
                    || profile.CustomPerformance is { Cpu: CustomPerformanceLevel.High, Gpu: CustomPerformanceLevel.High }
                    || profile.CustomPerformance.MaximumFans))
        {
            throw Invalid(
                $"The {name} custom performance combination is not supported by this device.");
        }
    }

    private static void ValidateGpuClockTuning(
        PowerProfile profile,
        string name,
        PowerSource source)
    {
        if (profile.GpuClockTuning is not null && source != PowerSource.PluggedIn)
        {
            throw Invalid($"The {name} profile cannot configure GPU clock tuning.");
        }

        if (profile.GpuClockTuning is not null
            && !GpuClockTuningSettings.IsWellFormed(profile.GpuClockTuning))
        {
            throw Invalid($"The {name} GPU clock tuning settings are invalid.");
        }

        if (profile.GpuClockTuning is not null
            && profile.CustomPerformance?.GpuClockOffsets is not null)
        {
            throw Invalid(
                $"The {name} profile contains both current and legacy GPU clock tuning settings.");
        }
    }

    private void ValidateFanControl(PowerProfile profile, string name)
    {
        FanControlSettings settings = profile.FanControl
            ?? FanControlSettings.FromLegacy(profile.Fans);
        bool legacyMaximum = profile.Fans == FanMode.Maximum
            && settings.Mode == FanMode.Fixed
            && settings.FixedSpeedRpm == FanControlSettings.MaximumSpeedRpm;
        if (settings.Mode != profile.Fans
            && !legacyMaximum)
        {
            throw Invalid($"The {name} fan settings do not match the selected fan mode.");
        }

        if (validationPolicy.FanSpeedRange is not { } range)
        {
            return;
        }

        bool supported = settings.Mode switch
        {
            FanMode.Fixed => legacyMaximum || range.Supports(settings.FixedSpeedRpm),
            FanMode.ManualCurve => settings.ManualCurve.Cpu
                    .Concat(settings.ManualCurve.Gpu)
                    .All(point => range.Supports(point.SpeedRpm)),
            _ => true,
        };
        if (!supported)
        {
            throw Invalid(
                $"The {name} fan settings contain a speed outside the device range.");
        }
    }

    private void ValidateLighting(LightingSettings? lighting, string name)
    {
        if (lighting is null)
        {
            throw Invalid($"The {name} lighting settings are missing.");
        }

        if (!Enum.IsDefined(lighting.Effect))
        {
            throw Invalid($"The {name} lighting effect is undefined.");
        }

        if (!validationPolicy.SupportedLightingEffects.Contains(lighting.Effect))
        {
            throw Invalid($"The {name} lighting effect is not supported by the active validation policy.");
        }

        if (lighting.Brightness > 100
            || (lighting.Effect != LightingEffect.Off && lighting.Brightness == 0))
        {
            throw Invalid(
                $"The {name} lighting brightness must be between 1 and 100 percent when lighting is on.");
        }

        bool consumesColor = lighting.Effect is
            LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Reactive
            or LightingEffect.Ripple
            or LightingEffect.Tidal or LightingEffect.Starlight;
        if (!consumesColor && lighting.Color != NeutralColor)
        {
            throw Invalid($"The {name} lighting effect does not accept a color.");
        }

        if (lighting.Effect == LightingEffect.Off && lighting.Brightness != 0)
        {
            throw Invalid($"The {name} lighting brightness must be zero when lighting is off.");
        }

        if (!LightingSettingsValidation.IsWellFormed(lighting))
        {
            throw Invalid($"The {name} lighting effect parameters are invalid.");
        }

        if (lighting.Parameters is BreathingLightingParameters breathing
            && !validationPolicy.SupportedBreathingColorModes.Contains(breathing.ColorMode))
        {
            throw Invalid($"The {name} lighting policy does not support this Breathing color mode.");
        }
        if (!validationPolicy.SupportsWaveDirectionSelection
            && lighting.Parameters is WaveLightingParameters { Direction: not LightingDirection.Rightward })
        {
            throw Invalid($"The {name} lighting policy supports the upstream Wave direction only.");
        }

        if (lighting.Parameters is SequenceLightingParameters sequence
            && !string.Equals(
                sequence.LayoutId,
                validationPolicy.KeyboardLightingLayoutId,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"The {name} imported animation targets a different keyboard layout.");
        }
    }

    private static void ValidateBatteryPolicy(BatteryPolicy? batteryPolicy)
    {
        if (batteryPolicy is null)
        {
            throw Invalid("The battery policy is missing.");
        }

        if (batteryPolicy.ChargeLimitPercent is < 50 or > 99)
        {
            throw Invalid("The enabled battery charge limit must be from 50 to 99 percent.");
        }
    }

    private static void ValidateLightingPowerPolicy(LightingPowerPolicy? policy)
    {
        if (policy?.IdleTimeoutMinutes is < 1 or > 60)
        {
            throw Invalid("The lighting idle timeout must be from 1 to 60 minutes.");
        }
    }

    private static void ValidateDisplay(DisplayProfileSettings? display, string name)
    {
        if (display is null)
        {
            // Null is the backward-compatible representation of an
            // unconfigured display profile.
            return;
        }

        ValidateDisplayKey(display.RefreshRateKey, name, "refresh rate");
        ValidateDisplayKey(display.ColorProfileKey, name, "color profile");
    }

    private static void ValidateLaptopBehavior(
        LaptopBehaviorProfileSettings? behavior,
        string name)
    {
        if (behavior?.FunctionKeyPrimary is { } functionKeyMode
            && !Enum.IsDefined(functionKeyMode))
        {
            throw Invalid($"The {name} function key behavior is undefined.");
        }
    }

    private static void ValidateThxAudio(
        ThxAudioProfileSettings? settings,
        string name)
    {
        if (settings is null)
        {
            return;
        }

        if (!ThxAudioProfileSettings.IsWellFormed(settings))
        {
            throw Invalid($"The {name} THX audio settings are invalid.");
        }
    }

    private static void ValidateDisplayKey(string? value, string name, string setting)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length is 0 or > 1024 || value.Any(char.IsControl))
        {
            throw Invalid($"The {name} display {setting} identifier is invalid.");
        }
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
