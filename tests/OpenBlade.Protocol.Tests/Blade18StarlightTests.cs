using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Blade18StarlightTests
{
    [Fact]
    public void Starlight_AddressesAllOwnCellsWithoutAnotherModelsHoles()
    {
        LightingSettings settings = LightingSettingsFactory.StarlightSingle(
            new RgbColor(255, 0, 0), 50, StarlightLightingDuration.Short);
        var seen = new bool[Blade18LightingFrame.SlotCount];
        for (int milliseconds = 0; milliseconds < 20000; milliseconds += 125)
        {
            Blade18LightingFrame frame = Blade18LightingAnimation.Render(
                settings, TimeSpan.FromMilliseconds(milliseconds));
            Assert.Equal(96, frame.Slots.Count);
            for (int slot = 0; slot < seen.Length; slot++)
            {
                RgbColor color = frame.Slots[slot];
                Assert.Equal(0, color.Green);
                Assert.Equal(0, color.Blue);
                seen[slot] |= color.Red > 0;
            }
        }
        Assert.All(seen, value => Assert.True(value));
        Assert.Contains(Enumerable.Range(0, seen.Length), Pid02E0MatrixLayout.IsHole);
    }

    [Fact]
    public void Starlight_DurationScalesTheSharedPatternAndRenderingIsDeterministic()
    {
        LightingSettings shortSettings = LightingSettingsFactory.StarlightDual(
            new RgbColor(255, 0, 0), new RgbColor(0, 0, 255),
            50, StarlightLightingDuration.Short);
        LightingSettings mediumSettings = LightingSettingsFactory.StarlightDual(
            new RgbColor(255, 0, 0), new RgbColor(0, 0, 255),
            50, StarlightLightingDuration.Medium);
        Blade18LightingFrame first = Blade18LightingAnimation.Render(
            shortSettings, TimeSpan.FromMilliseconds(2500));
        Assert.Equal(first.Slots, Blade18LightingAnimation.Render(
            shortSettings, TimeSpan.FromMilliseconds(2500)).Slots);
        Assert.Equal(first.Slots, Blade18LightingAnimation.Render(
            mediumSettings, TimeSpan.FromMilliseconds(7500)).Slots);
        Assert.Contains(first.Slots, color => color.Red > 0 || color.Blue > 0);
        Assert.All(first.Slots, color =>
        {
            Assert.Equal(0, color.Green);
            Assert.True(color.Red == 0 || color.Blue == 0);
        });
    }

    [Fact]
    public void Starlight_RandomUsesBoundedColorsAndAdvancesOverTime()
    {
        LightingSettings settings = LightingSettingsFactory.StarlightRandom(
            50, StarlightLightingDuration.Short);
        Blade18LightingFrame first = Blade18LightingAnimation.Render(
            settings, TimeSpan.FromSeconds(2));
        Blade18LightingFrame next = Blade18LightingAnimation.Render(
            settings, TimeSpan.FromSeconds(4));
        Assert.Contains(first.Slots, color => color != default);
        Assert.False(first.Slots.SequenceEqual(next.Slots));
        Assert.Equal(52, first.EncodeRow(5).Length);
    }

    [Fact]
    public void Starlight_RejectsMalformedTypedParameters()
    {
        LightingSettings invalid = LightingSettingsFactory.StarlightSingle(
            new RgbColor(255, 0, 0), 50, (StarlightLightingDuration)99);
        Assert.Throws<ArgumentException>(() => Blade18LightingAnimation.Render(
            invalid, TimeSpan.Zero));
    }
}
