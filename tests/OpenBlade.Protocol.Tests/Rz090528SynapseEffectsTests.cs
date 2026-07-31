using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528SynapseEffectsTests
{
    [Fact]
    public void FullAdmission_MatchesExactSynapseQuickEffects()
    {
        DeviceCapabilities capabilities =
            Rz090528BrightnessLightingPort.CreateCapabilities(
                allSynapseEffectsAdmitted: true);

        Assert.True(capabilities.LightingEffects.SetEquals(
        [
            LightingEffect.Off,
            LightingEffect.Static,
            LightingEffect.Spectrum,
            LightingEffect.Starlight,
            LightingEffect.Ripple,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
            LightingEffect.Breathing,
            LightingEffect.Reactive,
            LightingEffect.AmbientAwareness,
            LightingEffect.AudioMeter,
            LightingEffect.Fire,
        ]));
        Assert.Empty(capabilities.ObservedLightingEffects);
    }

    [Theory]
    [InlineData(StarlightLightingDuration.Short, 500)]
    [InlineData(StarlightLightingDuration.Medium, 1500)]
    [InlineData(StarlightLightingDuration.Long, 2000)]
    public void StarlightDurations_MatchExactSynapseChoices(
        StarlightLightingDuration duration,
        int milliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(milliseconds),
            Rz090528BrightnessLightingPort.StarlightDuration(duration));
    }

    [Fact]
    public void TidalOracle_LoadsBothDirectionsAndKeepsHolesBlack()
    {
        RgbColor green = new(0, 255, 0);
        RgbColor blue = new(0, 0, 255);
        Rz090528MatrixFrame left = Rz090528TidalOracle.Render(
            LightingDirection.Leftward,
            TimeSpan.FromMilliseconds(1375),
            green,
            blue);
        Rz090528MatrixFrame right = Rz090528TidalOracle.Render(
            LightingDirection.Rightward,
            TimeSpan.FromMilliseconds(1375),
            green,
            blue);

        Assert.NotEqual(FrameBytes(left), FrameBytes(right));
        AssertHolesBlack(left);
        AssertHolesBlack(right);
        Assert.Equal(
            FrameBytes(left),
            FrameBytes(Rz090528TidalOracle.Render(
                LightingDirection.Leftward,
                TimeSpan.FromMilliseconds(6375),
                green,
                blue)));
    }

    [Theory]
    [InlineData("Wave", "Leftward")]
    [InlineData("Wave", "Rightward")]
    [InlineData("Wheel", "Clockwise")]
    [InlineData("Wheel", "Counterclockwise")]
    public void WaveWheelOracle_LoadsEveryExactVariant(
        string effect,
        string direction)
    {
        Rz090528MatrixFrame frame = Rz090528WaveWheelOracle.Render(
            effect,
            direction,
            TimeSpan.FromMilliseconds(317));
        AssertHolesBlack(frame);
        Assert.Equal(
            FrameBytes(frame),
            FrameBytes(Rz090528WaveWheelOracle.Render(
                effect,
                direction,
                TimeSpan.FromMilliseconds(1317))));
    }

    [Fact]
    public void WaveAndWheelDirections_RenderDistinctFrames()
    {
        TimeSpan at = TimeSpan.FromMilliseconds(317);
        Assert.NotEqual(
            FrameBytes(new Rz090528WaveEffect(LightingDirection.Leftward)
                .RenderFrame(at)),
            FrameBytes(new Rz090528WaveEffect(LightingDirection.Rightward)
                .RenderFrame(at)));
        Assert.NotEqual(
            FrameBytes(new Rz090528WheelEffect(
                LightingRotationDirection.Clockwise).RenderFrame(at)),
            FrameBytes(new Rz090528WheelEffect(
                LightingRotationDirection.Counterclockwise).RenderFrame(at)));
    }

    [Fact]
    public void Ripple_PulsesAnActiveSlotAndRejectsCapturedHoles()
    {
        var effect = new Rz090528RippleEffect(new RgbColor(0, 255, 0));
        effect.Pulse(53, TimeSpan.Zero);

        Assert.NotEqual(
            default,
            effect.RenderFrame(TimeSpan.FromMilliseconds(50))[53]);
        Assert.Equal(
            Rz090528LightingTiming.AnimatedFrameInterval,
            effect.GetNextFrameDelay(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(
            Rz090528LightingTiming.IdleKeepaliveInterval,
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => effect.Pulse(51, TimeSpan.Zero));
    }

    [Fact]
    public void Starlight_AllModesPreserveCapturedHoleMask()
    {
        foreach (StarlightColorMode mode in Enum.GetValues<StarlightColorMode>())
        {
            var effect = new Rz090528StarlightEffect(
                mode,
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255),
                TimeSpan.FromMilliseconds(500));
            AssertHolesBlack(effect.RenderFrame(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void Breathing_AllColorModesPreserveCapturedHoleMask()
    {
        foreach (BreathingColorMode mode in Enum.GetValues<BreathingColorMode>())
        {
            var effect = new Rz090528BreathingEffect(
                mode,
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255));
            AssertHolesBlack(effect.RenderFrame(TimeSpan.FromMilliseconds(2800)));
        }
    }

    [Fact]
    public void Reactive_PulsesAndFadesAnExactSlot()
    {
        var effect = new Rz090528ReactiveEffect(
            new RgbColor(0, 255, 0),
            TimeSpan.FromMilliseconds(500));
        effect.Pulse(53, TimeSpan.Zero);

        Assert.Equal(new RgbColor(0, 255, 0), effect.RenderFrame(TimeSpan.Zero)[53]);
        Assert.Equal(default, effect.RenderFrame(TimeSpan.FromMilliseconds(500))[53]);
        Assert.Throws<ArgumentException>(() => effect.Pulse(51, TimeSpan.Zero));
    }

    [Fact]
    public void AmbientAwareness_UsesSessionFrameAndPreservesHoles()
    {
        var effect = new Rz090528AmbientAwarenessEffect();
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = AmbientLightingFrame.IsHole(slot)
                ? default
                : new RgbColor(12, 34, 56);
        }
        effect.Update(new AmbientLightingFrame(slots));

        Rz090528MatrixFrame frame = effect.RenderFrame(TimeSpan.Zero);
        Assert.Equal(new RgbColor(12, 34, 56), frame[53]);
        AssertHolesBlack(frame);
    }

    [Fact]
    public void AudioMeter_ValidatesBoostAndUsesSessionFrame()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Rz090528AudioMeterEffect(0));
        var effect = new Rz090528AudioMeterEffect(3);
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        slots[53] = new RgbColor(255, 128, 0);
        effect.Update(new AmbientLightingFrame(slots));

        Assert.Equal(3, effect.ColorBoost);
        Assert.Equal(new RgbColor(255, 128, 0), effect.RenderFrame(TimeSpan.Zero)[53]);
    }

    [Fact]
    public void Fire_IsDeterministicPerFrameAndPreservesHoles()
    {
        var effect = new Rz090528FireEffect();
        TimeSpan at = TimeSpan.FromMilliseconds(450);
        Rz090528MatrixFrame first = effect.RenderFrame(at);
        Rz090528MatrixFrame repeated = effect.RenderFrame(at);

        Assert.Equal(FrameBytes(first), FrameBytes(repeated));
        AssertHolesBlack(first);
        Assert.Contains(
            Enumerable.Range(0, Rz090528MatrixFrame.SlotCount),
            slot => first[slot] != default);
    }

    private static byte[] FrameBytes(Rz090528MatrixFrame frame)
    {
        byte[] result = new byte[Rz090528MatrixFrame.SlotCount * 3];
        for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
        {
            int offset = slot * 3;
            result[offset] = frame[slot].Red;
            result[offset + 1] = frame[slot].Green;
            result[offset + 2] = frame[slot].Blue;
        }
        return result;
    }

    private static void AssertHolesBlack(Rz090528MatrixFrame frame)
    {
        foreach (int slot in Rz090528MatrixFrame.CapturedHoleSlots)
        {
            Assert.Equal(default, frame[slot]);
        }
    }
}
