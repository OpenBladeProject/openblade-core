using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileSetValidatorTests
{
    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Automatic)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Automatic)]
    public void DefaultPolicy_AllowsOnlyVerifiedThermalPairs(
        PerformancePreset performance,
        FanMode fans)
    {
        ProfileSetValidator.CreateUnverifiedSafeDefaults()
            .ValidatePerformanceFanCombination(performance, fans);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Maximum)]
    [InlineData(PerformancePreset.Performance, FanMode.Maximum)]
    public void DefaultPolicy_RejectsUnverifiedMaximumFanPairs(
        PerformancePreset performance,
        FanMode fans)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults()
                .ValidatePerformanceFanCombination(performance, fans));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.Balanced, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.Performance, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.BatterySaver, PowerSource.Battery)]
    [InlineData(PerformancePreset.BatteryBalanced, PowerSource.Battery)]
    public void SourceAwareCombinationValidation_AcceptsCompatiblePreset(
        PerformancePreset performance,
        PowerSource source)
    {
        ProfileSetValidator.CreateUnverifiedSafeDefaults()
            .ValidatePerformanceFanCombination(
                performance,
                FanMode.Automatic,
                source);
    }

    [Fact]
    public void SourceAwareCombinationValidation_UsbCAllowsBalancedAutomaticOnly()
    {
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();

        validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            PowerSource.UsbC);
        Assert.Throws<InvalidDataException>(() =>
            validator.ValidatePerformanceFanCombination(
                PerformancePreset.Balanced,
                FanMode.Maximum,
                PowerSource.UsbC));
        Assert.Throws<InvalidDataException>(() =>
            validator.ValidatePerformanceFanCombination(
                PerformancePreset.Silent,
                FanMode.Automatic,
                PowerSource.UsbC));
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.BatteryBalanced, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.Silent, PowerSource.Battery)]
    [InlineData(PerformancePreset.Balanced, PowerSource.Battery)]
    [InlineData(PerformancePreset.Performance, PowerSource.Battery)]
    public void SourceAwareCombinationValidation_RejectsIncompatiblePreset(
        PerformancePreset performance,
        PowerSource source)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults()
                .ValidatePerformanceFanCombination(
                    performance,
                    FanMode.Automatic,
                    source));
    }

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

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public void Validate_RejectsBatteryPresetForPluggedInProfile(
        PerformancePreset performance)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = performance,
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Performance)]
    public void Validate_RejectsPluggedInPresetForBatteryProfile(
        PerformancePreset performance)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                Performance = performance,
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public void Validate_AcceptsBatterySpecificPresetForBatteryProfile(
        PerformancePreset performance)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                Performance = performance,
            },
        };

        CreateValidator().Validate(profiles);
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
    public void Validate_AcceptsUnconfiguredDisplaySettings()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Display = DisplayProfileSettings.Unconfigured(),
            },
        };

        CreateValidator().Validate(profiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fixed-240\nunsafe")]
    public void Validate_RejectsInvalidDisplayIdentifiers(string key)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Display = new DisplayProfileSettings(key, false, null),
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
    }

    [Fact]
    public void Validate_RejectsOversizedDisplayIdentifier()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Display = new DisplayProfileSettings(new string('x', 1025), false, null),
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator().Validate(profiles));
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
                new(PerformancePreset.BatterySaver, FanMode.Automatic),
                new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
            });

        validator.Validate(profiles);
    }

    [Fact]
    public void ValidatePreferences_RejectsMissingPreferences()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CreateValidator().ValidatePreferences(null!));
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
                new(PerformancePreset.BatterySaver, FanMode.Automatic),
                new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
            },
            lightingEffects ?? new HashSet<LightingEffect> { LightingEffect.Off }));
}
