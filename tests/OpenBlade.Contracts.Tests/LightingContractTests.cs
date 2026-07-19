using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class LightingContractTests
{
    [Fact]
    public void ExistingThreeArgumentSettingsRemainValidLegacyContracts()
    {
        var legacy = new LightingSettings(
            LightingEffect.Static,
            new RgbColor(12, 34, 56),
            Brightness: 64);

        Assert.Null(legacy.Parameters);
        Assert.True(LightingSettingsValidation.IsWellFormed(legacy));
    }

    [Fact]
    public void FactoriesProduceTypedParametersForEveryApprovedEffect()
    {
        RgbColor primary = new(12, 34, 56);
        RgbColor secondary = new(78, 90, 123);

        LightingSettings[] settings =
        [
            LightingSettingsFactory.Off(),
            LightingSettingsFactory.Static(primary, 90),
            LightingSettingsFactory.SpectrumCycling(80),
            LightingSettingsFactory.BreathingSingle(primary, 70),
            LightingSettingsFactory.BreathingDual(primary, secondary, 60),
            LightingSettingsFactory.BreathingRandom(50),
            LightingSettingsFactory.Reactive(
                primary,
                40,
                ReactiveLightingDuration.Long),
        ];

        Assert.All(settings, value =>
            Assert.True(LightingSettingsValidation.IsWellFormed(value)));
        Assert.IsType<OffLightingParameters>(settings[0].Parameters);
        Assert.IsType<StaticLightingParameters>(settings[1].Parameters);
        Assert.IsType<SpectrumCyclingLightingParameters>(settings[2].Parameters);
        Assert.Collection(
            settings.Skip(3).Take(3),
            value => Assert.Equal(
                BreathingColorMode.Single,
                Assert.IsType<BreathingLightingParameters>(value.Parameters).ColorMode),
            value => Assert.Equal(
                BreathingColorMode.Dual,
                Assert.IsType<BreathingLightingParameters>(value.Parameters).ColorMode),
            value => Assert.Equal(
                BreathingColorMode.Random,
                Assert.IsType<BreathingLightingParameters>(value.Parameters).ColorMode));
        Assert.Equal(
            ReactiveLightingDuration.Long,
            Assert.IsType<ReactiveLightingParameters>(settings[6].Parameters).Duration);
    }

    [Fact]
    public void ValidationRejectsMismatchedOrIncompleteTypedParameters()
    {
        RgbColor primary = new(1, 2, 3);

        LightingSettings[] invalid =
        [
            LightingSettingsFactory.Static(primary, 50) with
            {
                Parameters = new SpectrumCyclingLightingParameters(),
            },
            LightingSettingsFactory.BreathingSingle(primary, 50) with
            {
                Parameters = new BreathingLightingParameters(
                    BreathingColorMode.Dual,
                    primary,
                    SecondaryColor: null),
            },
            LightingSettingsFactory.BreathingRandom(50) with
            {
                Parameters = new BreathingLightingParameters(
                    BreathingColorMode.Random,
                    primary,
                    SecondaryColor: null),
            },
            LightingSettingsFactory.Reactive(
                primary,
                50,
                ReactiveLightingDuration.Medium) with
            {
                Parameters = new ReactiveLightingParameters(
                    primary,
                    (ReactiveLightingDuration)999),
            },
        ];

        Assert.All(invalid, value =>
            Assert.False(LightingSettingsValidation.IsWellFormed(value)));
    }

    [Fact]
    public void ValidationReservesZeroBrightnessForOff()
    {
        LightingSettings[] invisible =
        [
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 0),
            LightingSettingsFactory.SpectrumCycling(0),
            LightingSettingsFactory.BreathingSingle(new RgbColor(1, 2, 3), 0),
            LightingSettingsFactory.BreathingRandom(0),
            LightingSettingsFactory.Reactive(
                new RgbColor(1, 2, 3),
                0,
                ReactiveLightingDuration.Medium),
        ];

        Assert.All(invisible, value =>
            Assert.False(LightingSettingsValidation.IsWellFormed(value)));
        Assert.True(LightingSettingsValidation.IsWellFormed(
            LightingSettingsFactory.Off()));
    }

    [Fact]
    public void LegacyInvisibleBreathingNormalizesToVisibleDefaults()
    {
        LightingSettings normalized =
            LightingSettingsFactory.NormalizeLegacyInvisibleEffect(
                LightingSettingsFactory.BreathingSingle(default, 0));

        Assert.Equal(LightingEffect.Breathing, normalized.Effect);
        Assert.Equal(LightingSettingsFactory.DefaultVisibleBrightness, normalized.Brightness);
        Assert.Equal(LightingSettingsFactory.DefaultVisibleColor, normalized.Color);
        Assert.True(LightingSettingsValidation.IsWellFormed(normalized));
    }
}
