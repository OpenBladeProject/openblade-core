using OpenBlade.Contracts;

namespace OpenBlade.Service;

public readonly record struct PerformanceFanCombination(
    PerformancePreset Performance,
    FanMode Fans);

public sealed class ProfileValidationPolicy
{
    public ProfileValidationPolicy(
        IReadOnlySet<PerformanceFanCombination> supportedPerformanceFanCombinations,
        IReadOnlySet<LightingEffect> supportedLightingEffects)
    {
        ArgumentNullException.ThrowIfNull(supportedPerformanceFanCombinations);
        ArgumentNullException.ThrowIfNull(supportedLightingEffects);
        SupportedPerformanceFanCombinations =
            new HashSet<PerformanceFanCombination>(supportedPerformanceFanCombinations);
        SupportedLightingEffects = new HashSet<LightingEffect>(supportedLightingEffects);
    }

    public IReadOnlySet<PerformanceFanCombination> SupportedPerformanceFanCombinations { get; }

    public IReadOnlySet<LightingEffect> SupportedLightingEffects { get; }

    public static ProfileValidationPolicy CreateUnverifiedSafeDefaults() => new(
        new HashSet<PerformanceFanCombination>
        {
            new(PerformancePreset.Balanced, FanMode.Automatic),
            new(PerformancePreset.Silent, FanMode.Automatic),
            new(PerformancePreset.Performance, FanMode.Automatic),
            new(PerformancePreset.Balanced, FanMode.Maximum),
            new(PerformancePreset.BatterySaver, FanMode.Automatic),
            new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
        },
        new HashSet<LightingEffect> { LightingEffect.Off });

    public static ProfileValidationPolicy CreateForLightingCapabilities(
        DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ProfileValidationPolicy safeDefaults = CreateUnverifiedSafeDefaults();
        var supportedLightingEffects = new HashSet<LightingEffect>
        {
            LightingEffect.Off,
        };
        supportedLightingEffects.UnionWith(
            capabilities.LightingEffects.Where(Enum.IsDefined));
        return new ProfileValidationPolicy(
            safeDefaults.SupportedPerformanceFanCombinations,
            supportedLightingEffects);
    }
}

public sealed class ProfileSetValidator(ProfileValidationPolicy policy)
{
    private static readonly RgbColor NeutralColor = new(0, 0, 0);
    private readonly ProfileValidationPolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    public static ProfileSetValidator CreateUnverifiedSafeDefaults() =>
        new(ProfileValidationPolicy.CreateUnverifiedSafeDefaults());

    public static ProfileSetValidator CreateForLightingCapabilities(
        DeviceCapabilities capabilities) =>
        new(ProfileValidationPolicy.CreateForLightingCapabilities(capabilities));

    public void Validate(ProfileSet profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateProfile(profiles.PluggedIn, "plugged-in", PowerSource.PluggedIn);
        ValidateProfile(profiles.Battery, "battery", PowerSource.Battery);
        ValidateProfile(profiles.UsbC, "USB-C", PowerSource.UsbC);
        ValidateBatteryPolicy(profiles.BatteryPolicy);
    }

    public void ValidatePreferences(BatteryAutomationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
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

        if (!_policy.SupportedPerformanceFanCombinations.Contains(
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
        if (source == PowerSource.UsbC && fans != FanMode.Automatic)
        {
            throw Invalid("USB-C power supports automatic fans only.");
        }

        ValidatePerformanceFanCombination(performance, fans);
    }

    private void ValidateProfile(
        PowerProfile? profile,
        string name,
        PowerSource source)
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
        ValidateDisplay(profile.Display, name);
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
                or PerformancePreset.Performance,
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

        if (!_policy.SupportedLightingEffects.Contains(lighting.Effect))
        {
            throw Invalid($"The {name} lighting effect is not supported by the active validation policy.");
        }

        if (lighting.Brightness > 100)
        {
            throw Invalid($"The {name} lighting brightness must be between 0 and 100 percent.");
        }

        bool consumesColor = lighting.Effect is
            LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Reactive;
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
    }

    private static void ValidateBatteryPolicy(BatteryPolicy? batteryPolicy)
    {
        if (batteryPolicy is null)
        {
            throw Invalid("The battery policy is missing.");
        }

        if (batteryPolicy.ChargeLimitPercent is < 50 or > 80
            || batteryPolicy.ChargeLimitPercent % 5 != 0)
        {
            throw Invalid("The battery charge limit must be from 50 to 80 percent in 5 percent steps.");
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
