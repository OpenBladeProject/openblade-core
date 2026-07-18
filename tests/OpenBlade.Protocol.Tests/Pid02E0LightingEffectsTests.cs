using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Pid02E0LightingEffectsTests
{
    private static readonly MatrixRgb Green = new(0, 255, 0);

    [Fact]
    public void Timing_UsesCapturedAnimationCadenceAndCycleDurations()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), Pid02E0LightingTiming.AnimatedFrameInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), Pid02E0LightingTiming.IdleKeepaliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), Pid02E0LightingTiming.BreathingPlateauStreamDuration);
        Assert.Equal(TimeSpan.FromSeconds(40), Pid02E0LightingTiming.SpectrumCycle);
        Assert.Equal(TimeSpan.FromSeconds(7), Pid02E0LightingTiming.BreathingCycle);
        Assert.Equal(20, 1 / Pid02E0LightingTiming.AnimatedFrameInterval.TotalSeconds);
    }

    [Fact]
    public void Static_RenderIsStableAndUsesCapturedFiveSecondKeepalive()
    {
        var effect = new Pid02E0StaticEffect(new MatrixRgb(12, 34, 56));

        Pid02E0MatrixFrame first = effect.RenderFrame(TimeSpan.Zero);
        Pid02E0MatrixFrame later = effect.RenderFrame(TimeSpan.FromDays(1));

        Assert.Equal(
            Pid02E0LightingTiming.IdleKeepaliveInterval,
            effect.GetNextFrameDelay(TimeSpan.Zero));
        Assert.Equal(new MatrixRgb(12, 34, 56), first[1]);
        AssertFramesEqual(first, later);
        AssertHolesBlack(first);
    }

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(5, 255, 191, 0)]
    [InlineData(10, 128, 255, 0)]
    [InlineData(20, 0, 255, 255)]
    [InlineData(30, 128, 0, 255)]
    [InlineData(40, 255, 0, 0)]
    public void Spectrum_TraversesOneUniformHsvCycleInFortySeconds(
        int seconds,
        byte red,
        byte green,
        byte blue)
    {
        var effect = new Pid02E0SpectrumEffect();

        Pid02E0MatrixFrame frame = effect.RenderFrame(TimeSpan.FromSeconds(seconds));

        Assert.Equal(
            Pid02E0LightingTiming.AnimatedFrameInterval,
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(new MatrixRgb(red, green, blue), frame[1]);
        AssertUniform(frame, new MatrixRgb(red, green, blue));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.75, 0)]
    [InlineData(2.625, 128)]
    [InlineData(3.5, 255)]
    [InlineData(5.25, 255)]
    [InlineData(6.125, 128)]
    [InlineData(7, 0)]
    public void BreathingSingle_UsesCapturedSevenSecondFourQuarterTrapezoid(
        double seconds,
        byte expectedGreen)
    {
        Pid02E0BreathingEffect effect = Pid02E0BreathingEffect.Single(Green);

        Pid02E0MatrixFrame frame = effect.RenderFrame(TimeSpan.FromSeconds(seconds));

        Assert.Equal(new MatrixRgb(0, expectedGreen, 0), frame[1]);
        AssertHolesBlack(frame);
    }

    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(1, 0.80)]
    [InlineData(1.80, 0.05)]
    [InlineData(3.50, 0.05)]
    [InlineData(4.50, 0.80)]
    [InlineData(5.30, 0.05)]
    public void Breathing_UsesCapturedRampCadenceAndSuppressesLatePlateauDuplicates(
        double elapsedSeconds,
        double expectedDelaySeconds)
    {
        Pid02E0BreathingEffect effect = Pid02E0BreathingEffect.Single(Green);

        Assert.Equal(
            TimeSpan.FromSeconds(expectedDelaySeconds),
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(elapsedSeconds)));
    }

    [Fact]
    public void BreathingDual_AlternatesColorAtEachPeakToPeakCycle()
    {
        MatrixRgb first = new(255, 0, 0);
        MatrixRgb second = new(0, 0, 255);
        Pid02E0BreathingEffect effect = Pid02E0BreathingEffect.Dual(first, second);

        Assert.Equal(MatrixRgb.Black, effect.RenderFrame(TimeSpan.Zero)[1]);
        Assert.Equal(first, effect.RenderFrame(TimeSpan.FromSeconds(4))[1]);
        Assert.Equal(second, effect.RenderFrame(TimeSpan.FromSeconds(11))[1]);
        Assert.Equal(first, effect.RenderFrame(TimeSpan.FromSeconds(18))[1]);
    }

    [Fact]
    public void BreathingRandom_IsCycleAddressedAndIndependentOfRenderOrder()
    {
        var source = new RecordingColorSource();
        Pid02E0BreathingEffect effect = Pid02E0BreathingEffect.Random(source);

        MatrixRgb cycleThree = effect.RenderFrame(TimeSpan.FromSeconds(25))[1];
        MatrixRgb cycleZero = effect.RenderFrame(TimeSpan.FromSeconds(4))[1];
        MatrixRgb cycleThreeAgain = effect.RenderFrame(TimeSpan.FromSeconds(25))[1];

        Assert.Equal(new MatrixRgb(3, 103, 203), cycleThree);
        Assert.Equal(new MatrixRgb(0, 100, 200), cycleZero);
        Assert.Equal(cycleThree, cycleThreeAgain);
        Assert.Equal([3L, 0L, 3L], source.RequestedCycles);
    }

    [Fact]
    public void SeededRandomColorSource_IsDeterministicAndCycleSpecific()
    {
        var first = new Pid02E0SeededRandomColorSource(12345);
        var second = new Pid02E0SeededRandomColorSource(12345);

        MatrixRgb[] firstPass = Enumerable.Range(0, 4)
            .Select(index => first.ColorForCycle(index))
            .ToArray();
        MatrixRgb[] secondPass = Enumerable.Range(0, 4)
            .Select(index => second.ColorForCycle(index))
            .ToArray();

        Assert.Equal(firstPass, secondPass);
        Assert.True(firstPass.Distinct().Count() > 1);
        Assert.All(firstPass, color => Assert.Equal(255, Math.Max(color.Red, Math.Max(color.Green, color.Blue))));
    }

    [Fact]
    public void Reactive_PulseLightsOnlyTargetSlotAndFadesLinearlyToBlack()
    {
        var effect = new Pid02E0ReactiveEffect(Green, TimeSpan.FromSeconds(2));
        effect.Pulse(1, TimeSpan.FromSeconds(10));

        Assert.Equal(MatrixRgb.Black, effect.RenderFrame(TimeSpan.FromSeconds(9))[1]);
        Assert.Equal(Green, effect.RenderFrame(TimeSpan.FromSeconds(10))[1]);
        Assert.Equal(new MatrixRgb(0, 128, 0), effect.RenderFrame(TimeSpan.FromSeconds(11))[1]);
        Assert.Equal(MatrixRgb.Black, effect.RenderFrame(TimeSpan.FromSeconds(12))[1]);
        Assert.Equal(
            Pid02E0LightingTiming.IdleKeepaliveInterval,
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(9)));
        Assert.Equal(
            Pid02E0LightingTiming.AnimatedFrameInterval,
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            Pid02E0LightingTiming.IdleKeepaliveInterval,
            effect.GetNextFrameDelay(TimeSpan.FromSeconds(12)));

        Pid02E0MatrixFrame middle = effect.RenderFrame(TimeSpan.FromSeconds(11));
        Assert.All(
            Enumerable.Range(0, Pid02E0MatrixFrame.SlotCount).Where(slot => slot != 1),
            slot => Assert.Equal(MatrixRgb.Black, middle[slot]));
    }

    [Fact]
    public void Reactive_TracksOverlappingSlotsAndRepulseRestartsOneFade()
    {
        var effect = new Pid02E0ReactiveEffect(new MatrixRgb(200, 100, 50), TimeSpan.FromSeconds(2));
        effect.Pulse(1, TimeSpan.Zero);
        effect.Pulse(2, TimeSpan.FromSeconds(1));
        effect.Pulse(1, TimeSpan.FromSeconds(1.5));

        Pid02E0MatrixFrame frame = effect.RenderFrame(TimeSpan.FromSeconds(2));

        Assert.Equal(new MatrixRgb(150, 75, 37), frame[1]);
        Assert.Equal(new MatrixRgb(100, 50, 25), frame[2]);
        AssertHolesBlack(frame);
    }

    [Fact]
    public void Reactive_RejectsInvalidAndHoleSlots()
    {
        var effect = new Pid02E0ReactiveEffect(Green, TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => effect.Pulse(-1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.Pulse(102, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => effect.Pulse(17, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.Pulse(1, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void Effects_RejectInvalidTimingInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pid02E0SpectrumEffect(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Pid02E0BreathingEffect.Single(Green, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Pid02E0ReactiveEffect(Green, TimeSpan.Zero));

        IPid02E0LightingEffect[] effects =
        [
            new Pid02E0StaticEffect(Green),
            new Pid02E0SpectrumEffect(),
            Pid02E0BreathingEffect.Single(Green),
            new Pid02E0ReactiveEffect(Green, TimeSpan.FromSeconds(1)),
        ];

        Assert.All(effects, effect =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => effect.RenderFrame(TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => effect.GetNextFrameDelay(TimeSpan.FromTicks(-1)));
        });
    }

    private static void AssertFramesEqual(Pid02E0MatrixFrame expected, Pid02E0MatrixFrame actual)
    {
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    private static void AssertUniform(Pid02E0MatrixFrame frame, MatrixRgb color)
    {
        for (int slot = 0; slot < Pid02E0MatrixFrame.SlotCount; slot++)
        {
            Assert.Equal(Pid02E0MatrixLayout.IsHole(slot) ? MatrixRgb.Black : color, frame[slot]);
        }
    }

    private static void AssertHolesBlack(Pid02E0MatrixFrame frame)
    {
        Assert.All(Pid02E0MatrixLayout.CapturedHoleSlots,
            slot => Assert.Equal(MatrixRgb.Black, frame[slot]));
    }

    private sealed class RecordingColorSource : IPid02E0RandomColorSource
    {
        internal List<long> RequestedCycles { get; } = [];

        public MatrixRgb ColorForCycle(long cycleIndex)
        {
            RequestedCycles.Add(cycleIndex);
            return new MatrixRgb((byte)cycleIndex, (byte)(cycleIndex + 100), (byte)(cycleIndex + 200));
        }
    }
}
