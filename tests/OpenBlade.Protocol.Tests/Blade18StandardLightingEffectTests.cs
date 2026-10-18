using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18StandardLightingEffectTests
{
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    [Fact]
    public void SingleBreathing_UsesDeterministicSoftwareCycleAndUnionMask()
    {
        var effect = new Blade18StandardLightingEffect(
            LightingSettingsFactory.BreathingSingle(Red, 50));
        int key = KeyAt(0, 0);

        Assert.Equal(default, effect.RenderFrame(TimeSpan.Zero).Slots[key]);
        RgbColor quarter = effect.RenderFrame(
            Blade18StandardLightingEffect.BreathingCycleDuration / 4).Slots[key];
        Assert.InRange(quarter.Red, (byte)127, (byte)128);
        Assert.Equal((byte)0, quarter.Green);
        Assert.Equal((byte)0, quarter.Blue);
        Assert.Equal(
            Red,
            effect.RenderFrame(Blade18StandardLightingEffect.BreathingCycleDuration / 2).Slots[key]);
        Assert.Equal(
            effect.RenderFrame(TimeSpan.Zero).Slots,
            effect.RenderFrame(Blade18StandardLightingEffect.BreathingCycleDuration).Slots);
        AssertHolesDark(effect.RenderFrame(Blade18StandardLightingEffect.BreathingCycleDuration / 2));
    }

    [Fact]
    public void DualBreathing_AlternatesColorsAtCycleBoundary()
    {
        var effect = new Blade18StandardLightingEffect(
            LightingSettingsFactory.BreathingDual(Red, Blue, 50));
        int key = KeyAt(0, 0);
        TimeSpan peak = Blade18StandardLightingEffect.BreathingCycleDuration / 2;

        Assert.Equal(Red, effect.RenderFrame(peak).Slots[key]);
        Assert.Equal(
            Blue,
            effect.RenderFrame(Blade18StandardLightingEffect.BreathingCycleDuration + peak).Slots[key]);
    }

    [Fact]
    public void WaveLeftward_MovesRainbowTowardDecreasingXAndRepeats()
    {
        var effect = new Blade18StandardLightingEffect(
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 50));
        int key = KeyAt(0, 0);
        Blade18LightingFrame initial = effect.RenderFrame(TimeSpan.Zero);
        Blade18LightingFrame quarter = effect.RenderFrame(
            Blade18StandardLightingEffect.WaveCycleDuration / 4);

        Assert.Equal(Red, initial.Slots[key]);
        Assert.Equal(new RgbColor(128, 255, 0), quarter.Slots[key]);
        Assert.Equal(
            initial.Slots,
            effect.RenderFrame(Blade18StandardLightingEffect.WaveCycleDuration).Slots);
        AssertHolesDark(quarter);
    }

    [Fact]
    public void ExactRegion_MasksKeysAbsentFromThatLayout()
    {
        LightingSettings settings = LightingSettingsFactory.BreathingSingle(
            Red,
            50);
        var union = new Blade18StandardLightingEffect(settings);
        var ansi = new Blade18StandardLightingEffect(settings, 1);
        TimeSpan peak = Blade18StandardLightingEffect.BreathingCycleDuration / 2;

        Assert.Equal(Red, union.RenderFrame(peak).Slots[84]);
        Assert.Equal(default, ansi.RenderFrame(peak).Slots[84]);
    }

    [Fact]
    public void Constructor_RejectsFirmwareConfirmedAndUnrelatedOptions()
    {
        Assert.Throws<ArgumentException>(() => new Blade18StandardLightingEffect(
            LightingSettingsFactory.BreathingRandom(50)));
        Assert.Throws<ArgumentException>(() => new Blade18StandardLightingEffect(
            LightingSettingsFactory.Wave(LightingDirection.Rightward, 50)));
        Assert.Throws<ArgumentException>(() => new Blade18StandardLightingEffect(
            LightingSettingsFactory.SpectrumCycling(50)));
        Assert.Throws<ArgumentException>(() => new Blade18StandardLightingEffect(
            LightingSettingsFactory.BreathingSingle(Red, 50) with { Brightness = 0 }));
    }

    [Fact]
    public void RenderFrame_RejectsNegativeTime()
    {
        var effect = new Blade18StandardLightingEffect(
            LightingSettingsFactory.BreathingSingle(Red, 50));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            effect.RenderFrame(TimeSpan.FromTicks(-1)));
    }

    private static int KeyAt(double x, double y) => Enumerable.Range(
        0,
        Blade18LightingFrame.SlotCount).Single(slot =>
            Blade18KeyboardLightingLayout.TryGetKeyPosition(
                slot, out double actualX, out double actualY)
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
