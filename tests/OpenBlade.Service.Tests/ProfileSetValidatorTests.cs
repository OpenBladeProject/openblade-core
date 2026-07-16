using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileSetValidatorTests
{
    [Fact]
    public void Validate_RejectsUndefinedPerformancePreset()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = (PerformancePreset)999,
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_RejectsUndefinedFanMode()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                Fans = (FanMode)999,
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_RejectsUndefinedLightingEffect()
    {
        ProfileSet profiles = WithPluggedInLighting(
            new LightingSettings((LightingEffect)999, new RgbColor(0, 0, 0), 0));

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_RejectsBrightnessAboveOneHundredPercent()
    {
        ProfileSet profiles = WithPluggedInLighting(
            new LightingSettings(LightingEffect.Static, new RgbColor(12, 34, 56), 101));

        Assert.Throws<InvalidDataException>(() => CreateValidator(
            lightingEffects: new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            }).Validate(profiles));
    }

    [Fact]
    public void Validate_RejectsColorForEffectThatDoesNotConsumeColor()
    {
        ProfileSet profiles = WithPluggedInLighting(
            new LightingSettings(LightingEffect.Off, new RgbColor(1, 2, 3), 0));

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_AcceptsBatteryPolicyWithoutTransientState()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        CreateValidator().Validate(profiles);
    }

    [Fact]
    public void Validate_RejectsCombinationMissingFromExplicitPolicy()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = new PowerProfile(
                PerformancePreset.Performance,
                FanMode.Maximum,
                ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting),
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_AllowsCombinationExplicitlyProvidedByFutureVerifiedPolicy()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = new PowerProfile(
                PerformancePreset.Performance,
                FanMode.Maximum,
                ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting),
        };
        ProfileSetValidator validator = CreateValidator(
            combinations: new HashSet<PerformanceFanCombination>
            {
                new(PerformancePreset.Balanced, FanMode.Automatic),
                new(PerformancePreset.Silent, FanMode.Automatic),
                new(PerformancePreset.Performance, FanMode.Maximum),
            });

        validator.Validate(profiles);
    }

    private static ProfileSet WithPluggedInLighting(LightingSettings lighting) =>
        ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Lighting = lighting,
            },
        };

    private static ProfileSetValidator CreateValidator(
        IReadOnlySet<PerformanceFanCombination>? combinations = null,
        IReadOnlySet<LightingEffect>? lightingEffects = null) => new(new ProfileValidationPolicy(
            combinations ?? new HashSet<PerformanceFanCombination>
            {
                new(PerformancePreset.Balanced, FanMode.Automatic),
                new(PerformancePreset.Silent, FanMode.Automatic),
            },
            lightingEffects ?? new HashSet<LightingEffect> { LightingEffect.Off }));
}
