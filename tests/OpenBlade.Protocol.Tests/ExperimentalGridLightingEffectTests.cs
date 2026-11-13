using OpenBlade.Contracts;
using OpenBlade.Protocol;
using Xunit;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalGridLightingEffectTests
{
    private static readonly RgbColor Red = new(240, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 220);

    public static IEnumerable<object[]> Settings()
    {
        yield return [LightingSettingsFactory.BreathingSingle(Red, 100)];
        yield return [LightingSettingsFactory.BreathingDual(Red, Blue, 100)];
        yield return [LightingSettingsFactory.Wave(LightingDirection.Leftward, 100)];
        yield return [LightingSettingsFactory.Fire(100)];
        yield return [LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 100)];
        yield return [LightingSettingsFactory.Wheel(LightingRotationDirection.Counterclockwise, 100)];
        yield return [LightingSettingsFactory.StarlightSingle(Red, 100, StarlightLightingDuration.Short)];
        yield return [LightingSettingsFactory.StarlightDual(Red, Blue, 100, StarlightLightingDuration.Medium)];
        yield return [LightingSettingsFactory.StarlightRandom(100, StarlightLightingDuration.Long)];
        yield return [LightingSettingsFactory.Tidal(Red, Blue, false, LightingDirection.Leftward, 100)];
        yield return [LightingSettingsFactory.Tidal(Red, Blue, true, LightingDirection.Rightward, 100)];
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void FramesAreBoundedDeterministicAndProgressWithoutRetainedHistory(LightingSettings settings)
    {
        Assert.True(ExperimentalGridLightingEffect.IsSupported(settings));
        var effect = new ExperimentalGridLightingEffect(settings);
        RgbColor[] first = effect.RenderFrame(TimeSpan.FromSeconds(2)).ToArray();
        Assert.Equal(96, first.Length);
        effect.RenderFrame(TimeSpan.MaxValue);
        Assert.Equal(first, effect.RenderFrame(TimeSpan.FromSeconds(2)).ToArray());
        Assert.Equal(first, new ExperimentalGridLightingEffect(settings).RenderFrame(TimeSpan.FromSeconds(2)).ToArray());
        Assert.False(first.SequenceEqual(effect.RenderFrame(TimeSpan.FromSeconds(3.25))));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.RenderFrame(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void BreathingPreservesTypedColorsAndAddressesEveryGridSlot()
    {
        var effect = new ExperimentalGridLightingEffect(LightingSettingsFactory.BreathingDual(Red, Blue, 75));
        Assert.All(effect.RenderFrame(TimeSpan.Zero), color => Assert.Equal(default, color));
        Assert.All(effect.RenderFrame(TimeSpan.FromSeconds(2)), color => Assert.Equal(Red, color));
        Assert.All(effect.RenderFrame(TimeSpan.FromSeconds(6)), color => Assert.Equal(Blue, color));
        Assert.Equal(TimeSpan.FromMilliseconds(50), ExperimentalGridLightingEffect.FrameInterval);
    }

    [Fact]
    public void WaveMovesLeftAcrossAllRowsWithoutLayoutHoles()
    {
        var effect = new ExperimentalGridLightingEffect(LightingSettingsFactory.Wave(LightingDirection.Leftward, 100));
        IReadOnlyList<RgbColor> start = effect.RenderFrame(TimeSpan.Zero);
        IReadOnlyList<RgbColor> shifted = effect.RenderFrame(TimeSpan.FromTicks(3 * TimeSpan.TicksPerSecond / 16));
        for (int row = 0; row < 6; row++)
        {
            for (int column = 0; column < 16; column++)
            {
                Assert.NotEqual(default, start[row * 16 + column]);
                Assert.Equal(start[row * 16 + (column + 1) % 16], shifted[row * 16 + column]);
                Assert.Equal(start[column], start[row * 16 + column]);
            }
        }
    }

    [Fact]
    public void WheelRotatesInOppositeDirections()
    {
        var clockwise = new ExperimentalGridLightingEffect(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 100));
        var counter = new ExperimentalGridLightingEffect(LightingSettingsFactory.Wheel(LightingRotationDirection.Counterclockwise, 100));
        Assert.False(clockwise.RenderFrame(TimeSpan.FromSeconds(1)).SequenceEqual(counter.RenderFrame(TimeSpan.FromSeconds(1))));
        Assert.Equal(clockwise.RenderFrame(TimeSpan.FromSeconds(1)).ToArray(), counter.RenderFrame(TimeSpan.FromSeconds(5)).ToArray());
    }

    [Fact]
    public void TidalHonorsDirectionAndBothTypedColors()
    {
        var outward = new ExperimentalGridLightingEffect(LightingSettingsFactory.Tidal(Red, Blue, false, LightingDirection.Leftward, 100));
        var inward = new ExperimentalGridLightingEffect(LightingSettingsFactory.Tidal(Red, Blue, false, LightingDirection.Rightward, 100));
        RgbColor[] frame = outward.RenderFrame(TimeSpan.FromSeconds(0.8)).ToArray();
        Assert.False(frame.SequenceEqual(inward.RenderFrame(TimeSpan.FromSeconds(0.8))));
        Assert.Contains(frame, color => color.Red > 0);
        Assert.Contains(frame, color => color.Blue > 0);
        Assert.All(frame, color => Assert.Equal((byte)0, color.Green));
        var random = new ExperimentalGridLightingEffect(LightingSettingsFactory.Tidal(Red, Blue, true, LightingDirection.Leftward, 100));
        Assert.Contains(random.RenderFrame(TimeSpan.FromSeconds(10)), color => color.Green > 0);
    }

    [Fact]
    public void StarlightUsesTypedPaletteAndDuration()
    {
        var shortEffect = new ExperimentalGridLightingEffect(LightingSettingsFactory.StarlightDual(Red, Blue, 100, StarlightLightingDuration.Short));
        var longEffect = new ExperimentalGridLightingEffect(LightingSettingsFactory.StarlightDual(Red, Blue, 100, StarlightLightingDuration.Long));
        RgbColor[] samples = Enumerable.Range(1, 200).SelectMany(t => shortEffect.RenderFrame(TimeSpan.FromMilliseconds(t * 100))).ToArray();
        Assert.Contains(samples, color => color.Red > 0);
        Assert.Contains(samples, color => color.Blue > 0);
        Assert.All(samples, color =>
        {
            Assert.Equal((byte)0, color.Green);
            Assert.True(color.Red == 0 || color.Blue == 0);
        });
        Assert.False(shortEffect.RenderFrame(TimeSpan.FromSeconds(4)).SequenceEqual(longEffect.RenderFrame(TimeSpan.FromSeconds(4))));
    }

    [Fact]
    public void FireIsWarmSpatialAndHasNoModelSpecificHoles()
    {
        IReadOnlyList<RgbColor> frame = new ExperimentalGridLightingEffect(LightingSettingsFactory.Fire(100)).RenderFrame(TimeSpan.FromSeconds(1));
        Assert.True(frame.Distinct().Count() > 16);
        Assert.All(frame, color =>
        {
            Assert.True(color.Red > color.Green);
            Assert.Equal((byte)0, color.Blue);
        });
    }

    [Fact]
    public void UnsupportedOrMalformedSettingsRejectBeforeRendering()
    {
        LightingSettings[] invalid =
        [
            LightingSettingsFactory.Off(),
            LightingSettingsFactory.Static(Red, 100),
            LightingSettingsFactory.BreathingRandom(100),
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 100),
            LightingSettingsFactory.Fire(0),
            LightingSettingsFactory.Fire(101),
            LightingSettingsFactory.Fire(100) with { Parameters = null },
            LightingSettingsFactory.Fire(100) with { Parameters = new WaveLightingParameters(LightingDirection.Leftward) },
            LightingSettingsFactory.Wheel((LightingRotationDirection)999, 100),
            LightingSettingsFactory.StarlightSingle(Red, 100, (StarlightLightingDuration)999),
            LightingSettingsFactory.BreathingSingle(Red, 100) with
            {
                Parameters = new BreathingLightingParameters(BreathingColorMode.Single, null, null),
            },
        ];
        Assert.False(ExperimentalGridLightingEffect.IsSupported(null));
        Assert.Throws<ArgumentNullException>(() => new ExperimentalGridLightingEffect(null!));
        foreach (LightingSettings settings in invalid)
        {
            Assert.False(ExperimentalGridLightingEffect.IsSupported(settings));
            Assert.Throws<ArgumentException>(() => new ExperimentalGridLightingEffect(settings));
        }
    }
}
