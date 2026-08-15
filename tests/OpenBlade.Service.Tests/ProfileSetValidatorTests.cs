using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileSetValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(WindowsPowerMode.BestPowerEfficiency)]
    [InlineData(WindowsPowerMode.Balanced)]
    [InlineData(WindowsPowerMode.BestPerformance)]
    public void Validate_AcceptsSupportedWindowsPowerMode(WindowsPowerMode? mode)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        ProfileSetValidator.CreateUnverifiedSafeDefaults().Validate(profiles with
        {
            PluggedIn = profiles.PluggedIn with { WindowsPowerMode = mode },
        });
    }

    [Fact]
    public void Validate_RejectsUndefinedWindowsPowerMode()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults().Validate(profiles with
            {
                Battery = profiles.Battery with
                {
                    WindowsPowerMode = (WindowsPowerMode)999,
                },
            }));

        Assert.Contains("Windows power mode is undefined", exception.Message);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Maximum)]
    [InlineData(PerformancePreset.Silent, FanMode.Fixed)]
    [InlineData(PerformancePreset.Performance, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Custom, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Automatic)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Automatic)]
    public void DefaultPolicy_AllowsIndependentAcIntentAndBatteryAutomatic(
        PerformancePreset performance,
        FanMode fans)
    {
        ProfileSetValidator.CreateUnverifiedSafeDefaults()
            .ValidatePerformanceFanCombination(performance, fans);
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Fixed)]
    public void DefaultPolicy_RejectsManualBatteryFanPairs(
        PerformancePreset performance,
        FanMode fans)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults()
                .ValidatePerformanceFanCombination(performance, fans));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Fixed)]
    [InlineData(PerformancePreset.Balanced, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Performance, FanMode.Maximum)]
    [InlineData(PerformancePreset.Custom, FanMode.Fixed)]
    public void SourceAwareCombinationValidation_AllowsIndependentAcFanIntent(
        PerformancePreset performance,
        FanMode fans)
    {
        ProfileSetValidator.CreateUnverifiedSafeDefaults()
            .ValidatePerformanceFanCombination(
                performance,
                fans,
                PowerSource.PluggedIn);
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
                ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting)
            {
                BatteryProtectionEnabled = true,
            },
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
                ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting)
            {
                BatteryProtectionEnabled = true,
            },
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
    public void Validate_RejectsFixedFanSpeedAboveDeviceRange()
    {
        ProfileSet profiles = WithPluggedInFixedFanSpeed(5400);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator(
                fanSpeedRange: new FanSpeedRange(2000, 5000, 100))
                .Validate(profiles));

        Assert.Contains("outside the device range", exception.Message);
    }

    [Fact]
    public void Validate_AcceptsFixedFanSpeedAtDeviceMaximum()
    {
        ProfileSet profiles = WithPluggedInFixedFanSpeed(5000);

        CreateValidator(
            fanSpeedRange: new FanSpeedRange(2000, 5000, 100))
            .Validate(profiles);
    }

    [Fact]
    public void Validate_AcceptsMaximumFanModeWithDeviceSpecificFixedRange()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Maximum,
                FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
            },
        };
        ProfileSetValidator validator = CreateValidator(
            combinations: new HashSet<PerformanceFanCombination>
            {
                new(PerformancePreset.Balanced, FanMode.Maximum),
                new(PerformancePreset.Balanced, FanMode.Automatic),
                new(PerformancePreset.BatterySaver, FanMode.Automatic),
                new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
            },
            fanSpeedRange: new FanSpeedRange(2000, 5000, 100));

        validator.Validate(profiles);
    }

    [Fact]
    public void Validate_RejectsCustomPerformanceCombinationOutsideDevicePolicy()
    {
        ProfileSet profiles = WithPluggedInCustomPerformance(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator(customPerformance: Rz090528CustomPerformance())
                .Validate(profiles));

        Assert.Contains("not supported by this device", exception.Message);
    }

    [Fact]
    public void Validate_AcceptsCustomPerformanceCombinationInDevicePolicy()
    {
        ProfileSet profiles = WithPluggedInCustomPerformance(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Low);

        CreateValidator(customPerformance: Rz090528CustomPerformance())
            .Validate(profiles);
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public void Validate_RejectsGpuClockTuningOutsidePluggedInProfile(
        PowerSource source)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = source switch
        {
            PowerSource.Battery => defaults with
            {
                Battery = defaults.Battery with
                {
                    GpuClockTuning = GpuClockTuningSettings.Disabled,
                },
            },
            PowerSource.UsbC => defaults with
            {
                UsbC = defaults.UsbC with
                {
                    GpuClockTuning = GpuClockTuningSettings.Disabled,
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator().Validate(profiles));

        Assert.Contains("cannot configure GPU clock tuning", exception.Message);
    }

    [Fact]
    public void ValidatePreferences_RejectsMissingPreferences()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CreateValidator().ValidatePreferences(null!));
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public void Validate_RejectsMissingBatteryProtectionForEveryProfile(PowerSource source)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = source switch
        {
            PowerSource.PluggedIn => defaults with
            {
                PluggedIn = defaults.PluggedIn with { BatteryProtectionEnabled = null },
            },
            PowerSource.Battery => defaults with
            {
                Battery = defaults.Battery with { BatteryProtectionEnabled = null },
            },
            PowerSource.UsbC => defaults with
            {
                UsbC = defaults.UsbC with { BatteryProtectionEnabled = null },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator().Validate(profiles));

        Assert.Contains("battery protection", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProfileSet WithPluggedInLighting(LightingSettings lighting) =>
        ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Lighting = lighting,
            },
        };

    private static ProfileSet WithPluggedInFixedFanSpeed(int speedRpm)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Fixed,
                FanControl = new FanControlSettings(
                    FanMode.Fixed,
                    speedRpm,
                    ManualFanCurve.Default),
            },
        };
    }

    private static ProfileSet WithPluggedInCustomPerformance(
        CustomPerformanceLevel cpu,
        CustomPerformanceLevel gpu)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(cpu, gpu),
            },
        };
    }

    private static CustomPerformanceCapabilities Rz090528CustomPerformance() => new(
        new HashSet<CustomPerformanceCombination>
        {
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
            new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
        });

    private static ProfileSetValidator CreateValidator(
        IReadOnlySet<PerformanceFanCombination>? combinations = null,
        IReadOnlySet<LightingEffect>? lightingEffects = null,
        CustomPerformanceCapabilities? customPerformance = null,
        FanSpeedRange? fanSpeedRange = null) => new(new ProfileValidationPolicy(
            combinations ?? new HashSet<PerformanceFanCombination>
            {
                new(PerformancePreset.Balanced, FanMode.Automatic),
                new(PerformancePreset.Balanced, FanMode.Fixed),
                new(PerformancePreset.Custom, FanMode.Automatic),
                new(PerformancePreset.Silent, FanMode.Automatic),
                new(PerformancePreset.BatterySaver, FanMode.Automatic),
                new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
            },
            lightingEffects ?? new HashSet<LightingEffect> { LightingEffect.Off },
            customPerformance,
            fanSpeedRange));
}
