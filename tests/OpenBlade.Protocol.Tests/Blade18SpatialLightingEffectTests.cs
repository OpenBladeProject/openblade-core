using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18SpatialLightingEffectTests
{
    private static readonly RgbColor Green = new(0, 255, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    [Theory]
    [InlineData(ReactiveLightingDuration.Short, 500)]
    [InlineData(ReactiveLightingDuration.Medium, 1500)]
    [InlineData(ReactiveLightingDuration.Long, 2000)]
    public void Reactive_FadesOnlyPressedKeyForVendorDuration(ReactiveLightingDuration duration, int milliseconds)
    {
        var effect = new Blade18SpatialLightingEffect(LightingSettingsFactory.Reactive(Green, 100, duration));
        int key = KeyAt(1, 2);
        Assert.All(effect.RenderFrame(TimeSpan.Zero).Slots, color => Assert.Equal(default, color));
        effect.Pulse(key, TimeSpan.Zero);
        Assert.Equal(Green, effect.RenderFrame(TimeSpan.Zero).Slots[key]);
        Assert.Equal(new RgbColor(0, 128, 0), effect.RenderFrame(TimeSpan.FromMilliseconds(milliseconds / 2)).Slots[key]);
        Assert.All(effect.RenderFrame(TimeSpan.FromMilliseconds(milliseconds)).Slots, color => Assert.Equal(default, color));
    }

    [Fact]
    public void Reactive_RejectsHolesAndNegativeTimeAndIgnoresLateOlderPulse()
    {
        var effect = new Blade18SpatialLightingEffect(LightingSettingsFactory.Reactive(Green, 100, ReactiveLightingDuration.Short));
        int key = KeyAt(1, 2);
        int hole = Enumerable.Range(0, 96).First(slot => !Blade18KeyboardLightingLayout.IsKeySlot(slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.Pulse(hole, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.Pulse(key, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.RenderFrame(TimeSpan.FromTicks(-1)));
        effect.Pulse(key, TimeSpan.FromSeconds(1));
        effect.Pulse(key, TimeSpan.Zero);
        Assert.Equal(default, effect.RenderFrame(TimeSpan.Zero).Slots[key]);
        Assert.Equal(Green, effect.RenderFrame(TimeSpan.FromSeconds(1)).Slots[key]);
    }

    [Fact]
    public void LegacyReactive_UsesMediumDuration()
    {
        var legacy = new LightingSettings(LightingEffect.Reactive, Green, 100);
        var effect = new Blade18SpatialLightingEffect(legacy);
        int key = KeyAt(1, 2);
        effect.Pulse(key, TimeSpan.Zero);
        Assert.Equal(new RgbColor(0, 128, 0), effect.RenderFrame(TimeSpan.FromMilliseconds(750)).Slots[key]);
    }

    [Fact]
    public void Ripple_TravelsAcrossPhysicalMatrixAndExpires()
    {
        var effect = new Blade18SpatialLightingEffect(LightingSettingsFactory.Ripple(Green, 100));
        int origin = KeyAt(1, 2);
        effect.Pulse(origin, TimeSpan.Zero);
        Blade18LightingFrame frame = effect.RenderFrame(TimeSpan.FromMilliseconds(200));
        Assert.Equal(default, frame.Slots[origin]);
        Assert.Equal(Green, frame.Slots[KeyAt(5, 2)]);
        Assert.Equal(default, frame.Slots[KeyAt(12, 2)]);
        Assert.All(effect.RenderFrame(TimeSpan.FromSeconds(2)).Slots, color => Assert.Equal(default, color));
        AssertHolesDark(frame);
    }

    [Fact]
    public void Tidal_UsesVendorGradientAndRepeatsAfterFiveSeconds()
    {
        var effect = new Blade18SpatialLightingEffect(LightingSettingsFactory.Tidal(Green, Blue, false, LightingDirection.Leftward, 100));
        int center = KeyAt(7, 3);
        Assert.False(effect.IsKeyTriggered);
        Assert.Throws<InvalidOperationException>(() => effect.Pulse(center, TimeSpan.Zero));
        Assert.Equal(Green, effect.RenderFrame(TimeSpan.FromMilliseconds(1250)).Slots[center]);
        Assert.Equal(Blue, effect.RenderFrame(TimeSpan.FromMilliseconds(3750)).Slots[center]);
        Blade18LightingFrame frame = effect.RenderFrame(TimeSpan.FromMilliseconds(9250));
        Assert.Equal(frame.Slots, effect.RenderFrame(TimeSpan.FromMilliseconds(14250)).Slots);
        AssertHolesDark(frame);
    }

    [Fact]
    public void Tidal_DirectionsDifferAndRandomColorsRemainStableWithinFrame()
    {
        LightingSettings settings = LightingSettingsFactory.Tidal(Green, Blue, false, LightingDirection.Leftward, 100);
        var outward = new Blade18SpatialLightingEffect(settings);
        var inward = new Blade18SpatialLightingEffect(LightingSettingsFactory.Tidal(Green, Blue, false, LightingDirection.Rightward, 100));
        TimeSpan at = TimeSpan.FromMilliseconds(8750);
        Assert.False(outward.RenderFrame(at).Slots.SequenceEqual(inward.RenderFrame(at).Slots));
        var random = new Blade18SpatialLightingEffect(LightingSettingsFactory.Tidal(Green, Blue, true, LightingDirection.Leftward, 100));
        Assert.Equal(random.RenderFrame(at).Slots, random.RenderFrame(at).Slots);
        Assert.False(random.RenderFrame(at).Slots.SequenceEqual(random.RenderFrame(at + Blade18SpatialLightingEffect.TidalCycleDuration).Slots));
    }

    [Fact]
    public void Constructor_RejectsMalformedOrUnrelatedEffects()
    {
        Assert.Throws<ArgumentException>(() => new Blade18SpatialLightingEffect(LightingSettingsFactory.Ripple(Green, 100) with { Brightness = 0 }));
        Assert.Throws<ArgumentException>(() => new Blade18SpatialLightingEffect(LightingSettingsFactory.Fire(100)));
    }

    private static int KeyAt(double x, double y) => Enumerable.Range(0, 96).Single(slot =>
        Blade18KeyboardLightingLayout.TryGetKeyPosition(slot, out double actualX, out double actualY)
        && actualX == x && actualY == y);

    private static void AssertHolesDark(Blade18LightingFrame frame)
    {
        for (int slot = 0; slot < frame.Slots.Count; slot++)
        {
            if (!Blade18KeyboardLightingLayout.IsKeySlot(slot))
            {
                Assert.Equal(default, frame.Slots[slot]);
            }
        }
    }
}
