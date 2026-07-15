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
        },
        new HashSet<LightingEffect> { LightingEffect.Off });
}

public sealed class ProfileSetValidator(ProfileValidationPolicy policy)
{
    private static readonly RgbColor NeutralColor = new(0, 0, 0);
    private readonly ProfileValidationPolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    public static ProfileSetValidator CreateUnverifiedSafeDefaults() =>
        new(ProfileValidationPolicy.CreateUnverifiedSafeDefaults());

    public void Validate(ProfileSet profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateProfile(profiles.PluggedIn, "plugged-in");
        ValidateProfile(profiles.Battery, "battery");
        ValidateBatteryPolicy(profiles.BatteryPolicy);
    }

    private void ValidateProfile(PowerProfile? profile, string name)
    {
        if (profile is null)
        {
            throw Invalid($"The {name} profile is missing.");
        }

        if (!Enum.IsDefined(profile.Performance))
        {
            throw Invalid($"The {name} performance preset is undefined.");
        }

        if (!Enum.IsDefined(profile.Fans))
        {
            throw Invalid($"The {name} fan mode is undefined.");
        }

        var combination = new PerformanceFanCombination(profile.Performance, profile.Fans);
        if (!_policy.SupportedPerformanceFanCombinations.Contains(combination))
        {
            throw Invalid($"The {name} performance and fan combination is not supported by the active validation policy.");
        }

        ValidateLighting(profile.Lighting, name);
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

        if (batteryPolicy.TemporaryFullCharge)
        {
            throw Invalid("Temporary full-charge state cannot be persisted in machine profiles.");
        }
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
