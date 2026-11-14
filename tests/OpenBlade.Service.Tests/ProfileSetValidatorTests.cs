using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileSetValidatorTests
{
    [Fact]
    public void Validate_AcceptsCompleteThxProfileAndRejectsDuplicateApplicationRule()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ThxAudioProfileSettings settings = CreateThxSettings();
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();

        validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with { ThxAudio = settings },
        });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(defaults with
            {
                PluggedIn = defaults.PluggedIn with
                {
                    ThxAudio = settings with
                    {
                        ApplicationRules =
                        [
                            new("game.exe", ThxApplicationAudioOutput.Game),
                            new("GAME.EXE", ThxApplicationAudioOutput.Competitive),
                        ],
                    },
                },
            }));

        Assert.Contains("THX audio settings are invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RejectsOversizedThxRuleListsBeforePersistence()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ThxAudioProfileSettings settings = CreateThxSettings() with
        {
            ApplicationRules = Enumerable.Range(0, 180).Select(index => new ThxApplicationAudioRule(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('a', 240) + ".exe",
                ThxApplicationAudioOutput.Game)).ToArray(),
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();
        foreach (ProfileSet profiles in new[]
        {
            defaults with { PluggedIn = defaults.PluggedIn with { ThxAudio = settings } },
            defaults with { Battery = defaults.Battery with { ThxAudio = settings } },
            defaults with { UsbC = defaults.UsbC with { ThxAudio = settings } },
        })
        {
            using var requestStream = new MemoryStream();
            await IpcMessageCodec.WriteRequestAsync(requestStream,
                new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles), CancellationToken.None);
            Assert.InRange(requestStream.Length, 1, OpenBladeIpc.MaximumMessageBytes + sizeof(int));
            Assert.Throws<InvalidDataException>(() => validator.Validate(profiles));
        }
    }

    [Fact]
    public void Validate_RejectsWrongThxEndpointGeometryAndHeadphoneNormalization()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ThxAudioProfileSettings settings = CreateThxSettings();
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();

        ThxAudioEndpointSettings invalidSpeakers = settings.Speakers with
        {
            EqualizerCurves = settings.Speakers.EqualizerCurves with { Game = [0, 0] },
        };
        ThxAudioEndpointSettings invalidHeadphones = settings.Headphones with
        {
            SoundNormalization = new ThxAudioEnhancementSettings(false, 0),
        };

        foreach (ThxAudioProfileSettings invalidSettings in new[]
        {
            settings with { Speakers = invalidSpeakers },
            settings with { Headphones = invalidHeadphones },
        })
        {
            Assert.Throws<InvalidDataException>(() => validator.Validate(defaults with
            {
                PluggedIn = defaults.PluggedIn with { ThxAudio = invalidSettings },
            }));
        }
    }

    [Fact]
    public void RestrictedLightingPolicy_RetainsSupportedProfilesAndRejectsUnsupportedParameters()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            CustomPerformance = DeviceCapabilities.SafeTargetDefaults().CustomPerformance,
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Breathing, LightingEffect.Wave },
            SupportsBreathingColorSelection = false,
            SupportsWaveDirectionSelection = false,
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(
            DeviceCapabilities.SafeTargetDefaults(), capabilities);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        foreach (LightingSettings lighting in new[]
        {
            LightingSettingsFactory.BreathingRandom(77),
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 77),
        })
        {
            validator.Validate(profiles with { PluggedIn = profiles.PluggedIn with { Lighting = lighting } });
        }
        foreach (LightingSettings lighting in new[]
        {
            LightingSettingsFactory.BreathingSingle(new RgbColor(1, 2, 3), 77),
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 77),
        })
        {
            Assert.Throws<InvalidDataException>(() =>
                validator.Validate(profiles with { PluggedIn = profiles.PluggedIn with { Lighting = lighting } }));
        }
    }

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
            WindowsPowerModes = profiles.WindowsPowerModes.WithPerformance(
                PerformancePreset.Balanced,
                mode),
        });
    }

    [Fact]
    public void Validate_RejectsUndefinedWindowsPowerMode()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults().Validate(profiles with
            {
                WindowsPowerModes = profiles.WindowsPowerModes.WithPerformance(
                    PerformancePreset.BatterySaver,
                    (WindowsPowerMode)999),
            }));

        Assert.Contains("Windows power mode associated with BatterySaver is undefined", exception.Message);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.Automatic)]
    [InlineData(PerformancePreset.Turbo, FanMode.Automatic)]
    [InlineData(PerformancePreset.MaximumPower, FanMode.Automatic)]
    [InlineData(PerformancePreset.Gaming, FanMode.Automatic)]
    [InlineData(PerformancePreset.Creator, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Maximum)]
    [InlineData(PerformancePreset.Silent, FanMode.Fixed)]
    [InlineData(PerformancePreset.Performance, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Custom, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Automatic)]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatterySaver, FanMode.Fixed)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Maximum)]
    [InlineData(PerformancePreset.BatteryBalanced, FanMode.Fixed)]
    public void DefaultPolicy_AllowsSupportedFanIntent(
        PerformancePreset performance,
        FanMode fans)
    {
        ProfileSetValidator.CreateUnverifiedSafeDefaults()
            .ValidatePerformanceFanCombination(performance, fans);
    }

    [Fact]
    public void DefaultPolicy_RejectsBatteryManualCurve()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateUnverifiedSafeDefaults()
                .ValidatePerformanceFanCombination(
                    PerformancePreset.BatteryBalanced,
                    FanMode.ManualCurve));
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
    [InlineData(PerformancePreset.Turbo, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.MaximumPower, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.Gaming, PowerSource.PluggedIn)]
    [InlineData(PerformancePreset.Creator, PowerSource.PluggedIn)]
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

    [Theory]
    [InlineData(PerformancePreset.Turbo)]
    [InlineData(PerformancePreset.MaximumPower)]
    [InlineData(PerformancePreset.Gaming)]
    [InlineData(PerformancePreset.Creator)]
    public void Validate_PreservesSourceReviewedAcIntentAndRejectsItOnBattery(PerformancePreset performance)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();
        validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with { Performance = performance },
        });

        Assert.Throws<InvalidDataException>(() => validator.Validate(defaults with
        {
            Battery = defaults.Battery with { Performance = performance },
        }));
        Assert.Throws<InvalidDataException>(() => validator.ValidatePerformanceFanCombination(
            performance, FanMode.Automatic, PowerSource.UsbC));
    }

    [Fact]
    public void SourceAwareCombinationValidation_UsbCAllowsSupportedFanModes()
    {
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();

        validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            PowerSource.UsbC);
        validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced,
            FanMode.Fixed,
            PowerSource.UsbC);
        validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced,
            FanMode.ManualCurve,
            PowerSource.UsbC);
        validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            PowerSource.UsbC);
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
    public void Validate_AcceptsSequenceOnlyForTheActiveExactLayout()
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        slots[59] = new RgbColor(1, 2, 3);
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(100, slots),
        ]);
        LightingSettings sequence = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0581,
            timeline,
            loop: true,
            brightness: 100);

        CreateValidator(
            lightingEffects: new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Sequence,
            },
            keyboardLightingLayoutId: KeyboardLightingLayoutIds.Rz09_0581)
            .Validate(WithPluggedInLighting(sequence));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator(
                lightingEffects: new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Sequence,
                },
                keyboardLightingLayoutId: KeyboardLightingLayoutIds.Rz09_0528)
                .Validate(WithPluggedInLighting(sequence)));
        Assert.Contains("different keyboard layout", exception.Message);
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
    public void Validate_PresetFanRangesAcceptSilentFixedMaximum()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
                Fans = FanMode.Fixed,
                FanControl = new(FanMode.Fixed, 5300, ManualFanCurve.Default),
            },
        };

        CreateValidator(
            combinations: Blade18FanCombinations(),
            fanSpeedRange: new FanSpeedRange(2200, 3800, 100),
            fanSpeedRangesByPerformancePreset: Blade18FanRanges()).Validate(profiles);
    }

    [Theory]
    [InlineData(PerformancePreset.Custom, FanMode.Fixed)]
    [InlineData(PerformancePreset.Custom, FanMode.ManualCurve)]
    public void Validate_PresetFanRangesRejectUnsupportedManualPair(
        PerformancePreset performance,
        FanMode mode)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var boundedCurve = new ManualFanCurve(
            [new(40, 2200), new(90, 3800)],
            [new(35, 2200), new(85, 3800)],
            useHigherTemperatureForBoth: true);
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = performance,
                Fans = mode,
                FanControl = new(mode, 3800, boundedCurve),
                CustomPerformance = performance == PerformancePreset.Custom
                    ? CustomPerformanceSettings.Default
                    : null,
            },
        };

        Assert.Throws<InvalidDataException>(() => CreateValidator(
            combinations: Blade18FanCombinations(),
            fanSpeedRange: new FanSpeedRange(2200, 3800, 100),
            fanSpeedRangesByPerformancePreset: Blade18FanRanges()).Validate(profiles));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, 2200, 3800)]
    [InlineData(PerformancePreset.Silent, 3700, 5300)]
    public void Validate_PresetFanCurveUsesItsOwnRange(
        PerformancePreset performance, int minimum, int maximum)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var curve = new ManualFanCurve(
            [new(40, minimum), new(90, maximum)],
            [new(35, minimum), new(85, maximum)], true);
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = performance,
                Fans = FanMode.ManualCurve,
                FanControl = new(FanMode.ManualCurve, maximum, curve),
            },
        };
        ProfileSetValidator validator = CreateValidator(
            combinations: Blade18FanCombinations(),
            fanSpeedRange: new(2200, 3800, 100),
            fanSpeedRangesByPerformancePreset: Blade18FanRanges());
        validator.Validate(profiles);
        Assert.Throws<InvalidDataException>(() => validator.Validate(profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                FanControl = new(FanMode.ManualCurve, maximum,
                    new ManualFanCurve([new(40, minimum), new(90, maximum + 100)],
                        [new(35, minimum), new(85, maximum)], true)),
            },
        }));
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.ManualCurve)]
    public void Validate_ObservedRangeNarrowingPreservesSavedFanIntentDuringUnrelatedEdit(FanMode mode)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanSpeedRange = new(2200, 5200, 100),
            FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Balanced] = new(2200, 5200, 100),
            },
            ObservedFanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Balanced] = new(2600, 5000, 100),
            },
        };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var curve = new ManualFanCurve([new(40, 2200), new(80, 5200)],
            [new(40, 2200), new(80, 5200)], true);
        FanControlSettings settings = new(mode, 5200, curve);
        ProfileSet saved = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Fans = mode, FanControl = settings },
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities);
        validator.Validate(saved);
        ProfileSet edited = saved with { BatteryPolicy = BatteryPolicy.Create(75) };
        validator.Validate(edited);
        Assert.Equal(settings, edited.PluggedIn.FanControl);
        Assert.Equal(5000, capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!.MaximumRpm);
        Assert.Equal(5200, capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced)!.MaximumRpm);
    }

    [Fact]
    public void Validate_PresetFanRangesKeepBalancedManualCurve()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var boundedCurve = new ManualFanCurve(
            [new(40, 2200), new(90, 3800)],
            [new(35, 2200), new(85, 3800)],
            useHigherTemperatureForBoth: true);
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.ManualCurve,
                FanControl = new(FanMode.ManualCurve, 3800, boundedCurve),
            },
        };

        CreateValidator(
            combinations: Blade18FanCombinations(),
            fanSpeedRange: new FanSpeedRange(2200, 3800, 100),
            fanSpeedRangesByPerformancePreset: Blade18FanRanges()).Validate(profiles);
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(5200, true)]
    [InlineData(5400, true)]
    public void Validate_LegacyMaximumUsesOnlyExplicitlyAdmittedMaximumSemantics(
        int maximum, bool accepted)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Maximum,
                FanControl = new(FanMode.Fixed, FanControlSettings.MaximumSpeedRpm, ManualFanCurve.Default),
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
            fanSpeedRange: new FanSpeedRange(2000, maximum, 100));

        if (accepted)
        {
            validator.Validate(profiles);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => validator.Validate(profiles));
        }
    }

    [Theory]
    [InlineData(5000, false)]
    [InlineData(5200, false)]
    [InlineData(5000, true)]
    [InlineData(5200, true)]
    public void Validate_MaximumRequiresAdvertisedModeEvenWhenPresetHasManualRange(int maximum, bool legacyAlias)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve },
            FanSpeedRange = new(2000, maximum, 100),
            FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Balanced] = new(2000, maximum, 100),
            },
        };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Maximum,
                FanControl = legacyAlias ? new(FanMode.Fixed, 5400, ManualFanCurve.Default)
                    : FanControlSettings.FromLegacy(FanMode.Maximum),
            },
        };
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateForDeviceCapabilities(capabilities).Validate(profiles));
        Assert.Contains("fan mode is not supported", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_Real0510DoesNotAdmitTypedOrLegacyMaximum(bool legacyAlias)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02B7);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        Assert.Equal(5200, capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced)!.MaximumRpm);
        Assert.DoesNotContain(FanMode.Maximum, capabilities.FanModes);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Maximum,
                FanControl = legacyAlias ? new(FanMode.Fixed, 5400, ManualFanCurve.Default)
                    : FanControlSettings.FromLegacy(FanMode.Maximum),
            },
        };
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateForDeviceCapabilities(capabilities).Validate(profiles));
        Assert.Contains("fan mode is not supported", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LiteralFixed5400IsStillRejectedForAdmittedMaximum5000Device()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Fixed,
                FanControl = new(FanMode.Fixed, 5400, ManualFanCurve.Default),
            },
        };
        var capabilities = DeviceCapabilities.SafeTargetDefaults() with { FanSpeedRange = new(2000, 5000, 100) };
        Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateForDeviceCapabilities(capabilities).Validate(profiles));
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public void Validate_GlobalSourceCurveUsesAdmittedFullAcPresetsWithoutChangingFixedScope(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        Assert.Empty(capabilities.FanSpeedRangesByPerformancePreset);
        Assert.Contains(FanMode.ManualCurve, capabilities.FanModes);
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        foreach (PerformancePreset performance in capabilities.PerformancePresets.Where(value =>
            value is PerformancePreset.Balanced or PerformancePreset.Silent or PerformancePreset.Performance or PerformancePreset.Custom))
        {
            validator.ValidatePerformanceFanCombination(performance, FanMode.ManualCurve, PowerSource.PluggedIn);
            validator.ValidatePerformanceFanCombination(performance, FanMode.Fixed, PowerSource.PluggedIn);
        }
        Assert.Throws<InvalidDataException>(() => validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced, FanMode.ManualCurve, PowerSource.UsbC));
        Assert.Throws<InvalidDataException>(() => validator.ValidatePerformanceFanCombination(
            PerformancePreset.Balanced, FanMode.Fixed, PowerSource.UsbC));
    }

    [Theory]
    [InlineData(PerformancePreset.Custom)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.Gaming)]
    [InlineData(PerformancePreset.Creator)]
    [InlineData(PerformancePreset.MaximumPower)]
    public void StoredThermalIntentRetainsUnavailablePresetsButStrictChangesRejectThem(PerformancePreset performance)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            PerformancePresets = new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed },
            FanSpeedRange = new(2200, 5000, 100),
        };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var settings = new FanControlSettings(FanMode.ManualCurve, 5000,
            new ManualFanCurve([new(40, 2200), new(80, 5000)], [new(40, 2200), new(80, 5000)], true));
        ProfileSet saved = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = performance,
                CustomPerformance = performance == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
                Fans = FanMode.ManualCurve,
                FanControl = settings,
            },
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        validator.ValidateStoredCustomIntent(saved);
        ProfileSet unrelated = saved with { BatteryPolicy = BatteryPolicy.Create(75) };
        validator.ValidateStoredCustomIntent(unrelated);
        validator.ValidateChangedThermalIntent(unrelated, saved);
        Assert.Equal(settings, unrelated.PluggedIn.FanControl);
        Assert.Throws<InvalidDataException>(() => validator.ValidateChangedThermalIntent(saved, defaults));
    }

    [Fact]
    public void MappedRangeCannotAdmitAnUnadvertisedPerformancePreset()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            PerformancePresets = new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            ReportsPerformanceWithManualFans = true,
            FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Custom] = new(2200, 5000, 100),
            },
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        Assert.Throws<InvalidDataException>(() => validator.ValidatePerformanceFanCombination(
            PerformancePreset.Custom, FanMode.ManualCurve, PowerSource.PluggedIn));
        Assert.Throws<InvalidDataException>(() => validator.ValidatePerformanceFanCombination(
            PerformancePreset.Custom, FanMode.Fixed, PowerSource.PluggedIn));
    }

    [Fact]
    public void ApplicationThermalChangesUseResolvedInheritanceAndExplicitIntentAdmission()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsManualFansOnLimitedPower = false,
            ReportsPerformanceWithManualFans = true,
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var settings = new FanControlSettings(FanMode.ManualCurve, 3800, ManualFanCurve.Default);
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Retained", @"C:\Games\retained.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                UsbC = new ApplicationPowerProfileOverride { Lighting = defaults.UsbC.Lighting },
            });
        ProfileSet saved = defaults with
        {
            UsbC = defaults.UsbC with { Fans = FanMode.ManualCurve, FanControl = settings },
            ApplicationProfiles = [application],
        };
        ApplicationProfileDefinition lightingOnly = application with
        {
            Overrides = application.Overrides with
            {
                UsbC = application.Overrides.UsbC! with { Lighting = LightingSettingsFactory.Static(new(10, 20, 30), 40) },
            },
        };
        ProfileSet draft = saved with { ApplicationProfiles = [lightingOnly] };
        validator.ValidateChangedThermalIntent(draft, saved);
        ApplicationProfileDefinition explicitCurve = lightingOnly with
        {
            Overrides = lightingOnly.Overrides with
            {
                UsbC = lightingOnly.Overrides.UsbC! with { Fans = FanMode.ManualCurve, FanControl = settings },
            },
        };
        Assert.Throws<InvalidDataException>(() => validator.ValidateChangedThermalIntent(
            draft with { ApplicationProfiles = [explicitCurve] }, saved));
        ApplicationProfileDefinition explicitAutomatic = application with
        {
            Overrides = application.Overrides with
            {
                UsbC = application.Overrides.UsbC! with { Fans = FanMode.Automatic },
            },
        };
        ProfileSet inherited = defaults with { ApplicationProfiles = [explicitAutomatic] };
        Assert.Throws<InvalidDataException>(() => validator.ValidateChangedThermalIntent(inherited with
        {
            UsbC = defaults.UsbC with { Fans = FanMode.Fixed, FanControl = new(FanMode.Fixed, 3800, ManualFanCurve.Default) },
        }, inherited));
    }

    [Fact]
    public void ApplicationInheritedCurveIsRevalidatedWhenOnlyBasePresetChanges()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            ReportsPerformanceWithManualFans = true,
            FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Balanced] = new(2000, 5400, 100),
            },
        };
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Inherited curve", @"C:/Games/inherited.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride { Fans = FanMode.ManualCurve },
            });
        ProfileSet saved = defaults with { ApplicationProfiles = [application] };
        ProfileSet changed = saved with
        {
            PluggedIn = saved.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = CustomPerformanceSettings.Default,
            },
        };
        validator.ValidateChangedThermalIntent(changed with { ApplicationProfiles = [] }, defaults);
        Assert.Throws<InvalidDataException>(() => validator.ValidateChangedThermalIntent(changed, saved));
    }

    [Fact]
    public void NewExplicitApplicationCustomLevelsRequireAdmissionEvenBeforeSelectingCustom()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
            }),
        };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Future Custom", @"C:/Games/future-custom.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    CustomPerformance = new(CustomPerformanceLevel.High, CustomPerformanceLevel.High),
                },
            });
        ProfileSetValidator validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities);
        Assert.Throws<InvalidDataException>(() => validator.ValidateChangedThermalIntent(
            defaults with { ApplicationProfiles = [application] }, defaults));
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

    [Fact]
    public void ValidatePreferences_RejectsUndefinedThermalPolicy()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator().ValidatePreferences(new BatteryAutomationPreferences(
                ThermalControlPolicy: (ThermalControlPolicy)999)));

        Assert.Contains("thermal control policy", exception.Message);
    }

    [Fact]
    public void ValidatePreferences_RejectsUndefinedThermalDisclosureState()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            CreateValidator().ValidatePreferences(new BatteryAutomationPreferences(
                ThermalPolicyUpgradeDisclosure:
                    (ThermalPolicyUpgradeDisclosureState)999)));

        Assert.Contains("disclosure state", exception.Message);
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

    private static ThxAudioProfileSettings CreateThxSettings()
    {
        static ThxEqualizerCurves Curves(int count)
        {
            double[] gains = new double[count];
            return new(gains, gains, gains, gains, gains);
        }

        return new(
            ApplicationRoutingEnabled: false,
            Speakers: new ThxAudioEndpointSettings(
                SpatialEnabled: true,
                EqualizerEnabled: true,
                SelectedPreset: ThxEqualizerPreset.Game,
                EqualizerCurves: Curves(ThxAudioProfileSettings.SpeakerEqualizerBandCount),
                VoiceClarity: new ThxAudioEnhancementSettings(false, 0),
                SoundNormalization: new ThxAudioEnhancementSettings(false, 0)),
            Headphones: new ThxAudioEndpointSettings(
                SpatialEnabled: true,
                EqualizerEnabled: true,
                SelectedPreset: ThxEqualizerPreset.Game,
                EqualizerCurves: Curves(ThxAudioProfileSettings.HeadphoneEqualizerBandCount),
                VoiceClarity: new ThxAudioEnhancementSettings(false, 0),
                SoundNormalization: null),
            ApplicationRules:
            [
                new ThxApplicationAudioRule(
                    "ExampleGame.EXE",
                    ThxApplicationAudioOutput.Immersive),
            ]);
    }

    private static ProfileSetValidator CreateValidator(
        IReadOnlySet<PerformanceFanCombination>? combinations = null,
        IReadOnlySet<LightingEffect>? lightingEffects = null,
        CustomPerformanceCapabilities? customPerformance = null,
        FanSpeedRange? fanSpeedRange = null,
        string? keyboardLightingLayoutId = null,
        IReadOnlyDictionary<PerformancePreset, FanSpeedRange>?
            fanSpeedRangesByPerformancePreset = null) => new(new ProfileValidationPolicy(
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
            fanSpeedRange,
            keyboardLightingLayoutId,
            fanSpeedRangesByPerformancePreset: fanSpeedRangesByPerformancePreset));

    private static IReadOnlyDictionary<PerformancePreset, FanSpeedRange> Blade18FanRanges() =>
        new Dictionary<PerformancePreset, FanSpeedRange>
        {
            [PerformancePreset.Balanced] = new(2200, 3800, 100),
            [PerformancePreset.Silent] = new(3700, 5300, 100),
        };

    private static IReadOnlySet<PerformanceFanCombination> Blade18FanCombinations() =>
        new HashSet<PerformanceFanCombination>
        {
            new(PerformancePreset.Balanced, FanMode.Automatic),
            new(PerformancePreset.Balanced, FanMode.Fixed),
            new(PerformancePreset.Balanced, FanMode.ManualCurve),
            new(PerformancePreset.Silent, FanMode.Automatic),
            new(PerformancePreset.Silent, FanMode.Fixed),
            new(PerformancePreset.Silent, FanMode.ManualCurve),
            new(PerformancePreset.Custom, FanMode.Automatic),
            new(PerformancePreset.Custom, FanMode.Fixed),
            new(PerformancePreset.BatterySaver, FanMode.Automatic),
            new(PerformancePreset.BatteryBalanced, FanMode.Automatic),
        };
}
