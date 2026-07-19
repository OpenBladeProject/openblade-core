using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class LightingContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public void LightingPowerPolicy_AcceptsCapturedIdleRange(int minutes)
    {
        var policy = new LightingPowerPolicy(
            turnOffWhenDisplayTurnsOff: true,
            idleTimeoutMinutes: minutes);

        Assert.True(policy.TurnOffWhenDisplayTurnsOff);
        Assert.Equal(minutes, policy.IdleTimeoutMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void LightingPowerPolicy_RejectsIdleValueOutsideCapturedRange(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LightingPowerPolicy(false, minutes));
    }

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
            LightingSettingsFactory.Tidal(
                primary,
                secondary,
                randomColors: false,
                LightingDirection.Rightward,
                brightness: 77),
            LightingSettingsFactory.Tidal(
                primary,
                secondary,
                randomColors: true,
                LightingDirection.Leftward,
                brightness: 66),
            LightingSettingsFactory.StarlightSingle(
                primary,
                55,
                StarlightLightingDuration.Short),
            LightingSettingsFactory.StarlightDual(
                primary,
                secondary,
                44,
                StarlightLightingDuration.Medium),
            LightingSettingsFactory.StarlightRandom(
                33,
                StarlightLightingDuration.Long),
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 22),
            LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                11),
            LightingSettingsFactory.Ripple(primary, 25),
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
        Assert.False(
            Assert.IsType<TidalLightingParameters>(settings[7].Parameters).RandomColors);
        Assert.Equal(default, settings[8].Color);
        Assert.Equal(
            StarlightColorMode.Single,
            Assert.IsType<StarlightLightingParameters>(settings[9].Parameters).ColorMode);
        Assert.Equal(
            StarlightColorMode.Dual,
            Assert.IsType<StarlightLightingParameters>(settings[10].Parameters).ColorMode);
        Assert.Equal(
            StarlightColorMode.Random,
            Assert.IsType<StarlightLightingParameters>(settings[11].Parameters).ColorMode);
        Assert.Equal(default, settings[11].Color);
        Assert.Equal(
            LightingDirection.Leftward,
            Assert.IsType<WaveLightingParameters>(settings[12].Parameters).Direction);
        Assert.Equal(
            LightingRotationDirection.Counterclockwise,
            Assert.IsType<WheelLightingParameters>(settings[13].Parameters).Direction);
        Assert.Equal(primary, Assert.IsType<RippleLightingParameters>(settings[14].Parameters).Color);
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
            LightingSettingsFactory.Tidal(
                primary,
                new RgbColor(4, 5, 6),
                randomColors: false,
                LightingDirection.Rightward,
                brightness: 50) with
            {
                Parameters = new TidalLightingParameters(
                    primary,
                    new RgbColor(4, 5, 6),
                    RandomColors: false,
                    (LightingDirection)999),
            },
            LightingSettingsFactory.StarlightSingle(
                primary,
                50,
                StarlightLightingDuration.Medium) with
            {
                Parameters = new StarlightLightingParameters(
                    StarlightColorMode.Random,
                    primary,
                    SecondaryColor: null,
                    StarlightLightingDuration.Medium),
            },
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50) with
            {
                Parameters = new WaveLightingParameters((LightingDirection)999),
            },
            LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 50) with
            {
                Parameters = new WheelLightingParameters((LightingRotationDirection)999),
            },
            LightingSettingsFactory.Ripple(primary, 50) with
            {
                Parameters = new RippleLightingParameters(new RgbColor(4, 5, 6)),
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
            LightingSettingsFactory.Tidal(
                new RgbColor(1, 2, 3),
                new RgbColor(4, 5, 6),
                randomColors: false,
                LightingDirection.Rightward,
                brightness: 0),
            LightingSettingsFactory.StarlightRandom(
                0,
                StarlightLightingDuration.Medium),
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 0),
            LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 0),
            LightingSettingsFactory.Ripple(new RgbColor(1, 2, 3), 0),
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
