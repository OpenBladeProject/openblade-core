using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528SynapseEffectsTests
{
    private static readonly (double Inner, double Outer)[] RippleReferenceBands =
    [
        (0.00, 0.00), (0.00, 1.00), (1.00, 2.24), (1.41, 3.61),
        (2.83, 4.24), (3.61, 5.39), (4.47, 6.32), (5.83, 7.28),
        (6.71, 8.25), (7.28, 9.22), (8.25, 10.44), (9.22, 11.40),
        (10.20, 13.15), (11.18, 13.34), (12.17, 14.32), (14.00, 14.32),
    ];

    [Fact]
    public void AnimatedEffects_PresentAtThirtyFramesPerSecond()
    {
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            Rz090528LightingTiming.AnimatedFrameInterval);
        Assert.Equal(
            30,
            1 / Rz090528LightingTiming.AnimatedFrameInterval.TotalSeconds,
            precision: 3);
    }

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
            LightingEffect.Sequence,
        ]));
        Assert.Equal(
            KeyboardLightingLayoutIds.Rz09_0528,
            capabilities.KeyboardLightingLayoutId);
        Assert.Equal(
            [LightingRefreshRate.ThirtyHz],
            capabilities.SupportedLightingRefreshRates);
        Assert.Empty(capabilities.ObservedLightingEffects);
    }

    [Fact]
    public void OracleResources_RequireApprovedHashAndExactDevice()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Rz090528LightingOracleResources.ReadDocument<object>(
                Rz090528FireOracle.ResourceName,
                new string('0', 64)));
        Assert.True(Rz090528LightingOracleResources.IsExactTarget(
            new Rz090528LightingOracleResources.Target(
                "RZ09-0528",
                "1532",
                "02C6",
                "2.02")));
        Assert.False(Rz090528LightingOracleResources.IsExactTarget(
            new Rz090528LightingOracleResources.Target(
                "RZ09-0581",
                "1532",
                "02E0",
                "3.01")));
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

    [Fact]
    public void RandomTidalColors_AreDerivedOncePerCycle()
    {
        var colors = new RecordingRandomColorPairSource();
        var effect = new Rz090528TidalEffect(
            default,
            default,
            randomColors: true,
            LightingDirection.Leftward,
            colors);

        _ = effect.RenderFrame(TimeSpan.Zero);
        _ = effect.RenderFrame(TimeSpan.FromSeconds(1));
        _ = effect.RenderFrame(Rz090528TidalOracle.CycleDuration);

        Assert.Equal([0, 1], colors.Cycles);
    }

    [Fact]
    public void NegativeEffectTime_ReportsTheCallerParameterName()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new Rz090528SpectrumEffect().RenderFrame(
                TimeSpan.FromTicks(-1)));

        Assert.Equal("elapsed", exception.ParamName);
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
    public void CapturedOracleEffects_InterpolateAtThirtyFpsPresentationTimes()
    {
        TimeSpan presentationTime = Rz090528LightingTiming.AnimatedFrameInterval;
        TimeSpan fireSourceFrame = TimeSpan.FromSeconds(4);

        AssertInterpolatedFrame(
            Rz090528FireOracle.Render(fireSourceFrame),
            Rz090528FireOracle.Render(
                fireSourceFrame + TimeSpan.FromMilliseconds(50)),
            Rz090528FireOracle.Render(fireSourceFrame + presentationTime),
            presentationTime.TotalMilliseconds / 50);
        AssertInterpolatedFrame(
            Rz090528TidalOracle.Render(
                LightingDirection.Leftward,
                TimeSpan.Zero,
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255)),
            Rz090528TidalOracle.Render(
                LightingDirection.Leftward,
                TimeSpan.FromMilliseconds(50),
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255)),
            Rz090528TidalOracle.Render(
                LightingDirection.Leftward,
                presentationTime,
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255)),
            presentationTime.TotalMilliseconds / 50);
        AssertInterpolatedFrame(
            Rz090528WaveWheelOracle.Render(
                "Wave",
                "Leftward",
                TimeSpan.Zero),
            Rz090528WaveWheelOracle.Render(
                "Wave",
                "Leftward",
                TimeSpan.FromMilliseconds(40)),
            Rz090528WaveWheelOracle.Render(
                "Wave",
                "Leftward",
                presentationTime),
            presentationTime.TotalMilliseconds / 40);
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
    public void Ripple_PrecomputedBandsMatchOriginalDistanceAlgorithm()
    {
        var color = new RgbColor(0x47, 0xE1, 0x0C);
        for (int origin = 0; origin < Rz090528MatrixFrame.SlotCount; origin++)
        {
            if (Rz090528MatrixFrame.IsHole(origin))
            {
                continue;
            }

            var effect = new Rz090528RippleEffect(color);
            effect.Pulse(origin, TimeSpan.Zero);
            for (int band = 0; band < RippleReferenceBands.Length; band++)
            {
                Rz090528MatrixFrame frame = effect.RenderFrame(
                    TimeSpan.FromMilliseconds(band * 50));
                for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
                {
                    Assert.Equal(
                        IsInReferenceRippleBand(origin, slot, band)
                            ? color
                            : default,
                        frame[slot]);
                }
            }
        }
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
    public void Breathing_UsesCapturedTwoSecondRiseAndSevenSecondCycle()
    {
        var effect = new Rz090528BreathingEffect(
            BreathingColorMode.Dual,
            new RgbColor(0, 255, 0),
            new RgbColor(128, 0, 255));

        Assert.Equal(default, effect.RenderFrame(TimeSpan.Zero)[53]);
        Assert.Equal(
            new RgbColor(0, 128, 0),
            effect.RenderFrame(TimeSpan.FromSeconds(1))[53]);
        Assert.Equal(
            new RgbColor(0, 255, 0),
            effect.RenderFrame(TimeSpan.FromSeconds(2))[53]);
        Assert.Equal(
            new RgbColor(0, 128, 0),
            effect.RenderFrame(TimeSpan.FromSeconds(4.5))[53]);
        Assert.Equal(
            default,
            effect.RenderFrame(TimeSpan.FromSeconds(5.5))[53]);
        Assert.Equal(
            new RgbColor(128, 0, 255),
            effect.RenderFrame(TimeSpan.FromSeconds(9))[53]);
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
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Rz090528AudioMeterEffect(1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Rz090528AudioMeterEffect(4.25));
        var effect = new Rz090528AudioMeterEffect(1.25);
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        slots[53] = new RgbColor(255, 128, 0);
        effect.Update(new AmbientLightingFrame(slots));

        Assert.Equal(1.25, effect.ColorBoost);
        Assert.Equal(new RgbColor(255, 128, 0), effect.RenderFrame(TimeSpan.Zero)[53]);
    }

    [Fact]
    public void Fire_UsesCapturedBottomHotOracleAndPreservesHoles()
    {
        var effect = new Rz090528FireEffect();
        TimeSpan at = TimeSpan.FromSeconds(4);
        Rz090528MatrixFrame first = effect.RenderFrame(at);
        Rz090528MatrixFrame repeated = effect.RenderFrame(at);
        Rz090528MatrixFrame cycled = effect.RenderFrame(
            at + Rz090528FireOracle.CycleDuration);

        Assert.Equal(FrameBytes(first), FrameBytes(repeated));
        Assert.Equal(FrameBytes(first), FrameBytes(cycled));
        AssertHolesBlack(first);
        Assert.All(
            Enumerable.Range(0, Rz090528MatrixFrame.SlotCount)
                .Where(slot => !Rz090528MatrixFrame.IsHole(slot)),
            slot =>
            {
                Assert.NotEqual(default, first[slot]);
                Assert.Equal(0, first[slot].Blue);
            });
        Assert.True(RowAverageRed(first, 5) > RowAverageRed(first, 0));
        Assert.Equal(
            default,
            effect.RenderFrame(TimeSpan.Zero)[53]);
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

    private static void AssertInterpolatedFrame(
        Rz090528MatrixFrame lower,
        Rz090528MatrixFrame upper,
        Rz090528MatrixFrame actual,
        double fraction)
    {
        for (int slot = 0; slot < Rz090528MatrixFrame.SlotCount; slot++)
        {
            Assert.Equal(
                Rz090528LightingMath.Interpolate(
                    lower[slot],
                    upper[slot],
                    fraction),
                actual[slot]);
        }
    }

    private static bool IsInReferenceRippleBand(int origin, int slot, int band)
    {
        if (Rz090528MatrixFrame.IsHole(slot))
        {
            return false;
        }

        int originRow = origin / Rz090528MatrixFrame.ColumnCount;
        int originColumn = origin % Rz090528MatrixFrame.ColumnCount;
        int rowDelta = (slot / Rz090528MatrixFrame.ColumnCount) - originRow;
        int columnDelta = (slot % Rz090528MatrixFrame.ColumnCount) - originColumn;
        double distance = Math.Sqrt(
            (rowDelta * rowDelta) + (columnDelta * columnDelta));
        (double inner, double outer) = RippleReferenceBands[band];
        return distance + 1e-9 >= inner && distance <= outer + 1e-9;
    }

    private static void AssertHolesBlack(Rz090528MatrixFrame frame)
    {
        foreach (int slot in Rz090528MatrixFrame.CapturedHoleSlots)
        {
            Assert.Equal(default, frame[slot]);
        }
    }

    private static double RowAverageRed(Rz090528MatrixFrame frame, int row)
    {
        int start = row * Rz090528MatrixFrame.ColumnCount;
        return Enumerable.Range(start, Rz090528MatrixFrame.ColumnCount)
            .Where(slot => !Rz090528MatrixFrame.IsHole(slot))
            .Average(slot => frame[slot].Red);
    }

    private sealed class RecordingRandomColorPairSource
        : IRz090528RandomColorPairSource
    {
        internal List<long> Cycles { get; } = [];

        public (RgbColor Primary, RgbColor Secondary) ColorsForCycle(long cycleIndex)
        {
            Cycles.Add(cycleIndex);
            return (new RgbColor(0, 255, 0), new RgbColor(0, 0, 255));
        }
    }
}
