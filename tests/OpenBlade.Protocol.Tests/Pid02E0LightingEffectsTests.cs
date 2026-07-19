using System.Text.Json;
using OpenBlade.Contracts;
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
        Assert.Equal(TimeSpan.FromMilliseconds(37_708.802), Pid02E0LightingTiming.SpectrumCycle);
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
    [InlineData(0.00, 0xFF, 0x10, 0x10)]
    [InlineData(0.10, 0xFF, 0x7E, 0x00)]
    [InlineData(0.34, 0x00, 0xFF, 0xFF)]
    [InlineData(0.50, 0x18, 0x58, 0xFF)]
    [InlineData(0.60, 0x1F, 0x55, 0xFF)]
    [InlineData(0.84, 0xFF, 0x17, 0xE7)]
    [InlineData(1.00, 0xFF, 0x10, 0x10)]
    public void Spectrum_FollowsCapturedThreeRunAverageCurve(
        double phase,
        byte red,
        byte green,
        byte blue)
    {
        var effect = new Pid02E0SpectrumEffect();
        TimeSpan elapsed = TimeSpan.FromTicks(
            (long)Math.Round(Pid02E0LightingTiming.SpectrumCycle.Ticks * phase));

        Pid02E0MatrixFrame frame = effect.RenderFrame(elapsed);

        Assert.Equal(
            Pid02E0LightingTiming.AnimatedFrameInterval,
            effect.GetNextFrameDelay(elapsed));
        Assert.Equal(new MatrixRgb(red, green, blue), frame[1]);
        AssertUniform(frame, new MatrixRgb(red, green, blue));
    }

    [Fact]
    public void Spectrum_InterpolatesBetweenCapturedAnchors()
    {
        var effect = new Pid02E0SpectrumEffect();
        TimeSpan onePercent = TimeSpan.FromTicks(
            Pid02E0LightingTiming.SpectrumCycle.Ticks / 100);

        Assert.Equal(
            new MatrixRgb(0xFF, 0x1A, 0x0E),
            effect.RenderFrame(onePercent)[1]);
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
    public void RandomPaletteColorSource_UsesCapturedPaletteAndKeepsEachCycleStable()
    {
        int[] selectedIndices = [0, 0, 23];
        int nextSelection = 0;
        var source = new Pid02E0RandomPaletteColorSource(
            _ => selectedIndices[nextSelection++]);

        MatrixRgb cycleZero = source.ColorForCycle(0);
        MatrixRgb cycleZeroAgain = source.ColorForCycle(0);
        MatrixRgb cycleOne = source.ColorForCycle(1);
        MatrixRgb cycleTwo = source.ColorForCycle(2);

        Assert.Equal(new MatrixRgb(0x00, 0x00, 0x80), cycleZero);
        Assert.Equal(cycleZero, cycleZeroAgain);
        Assert.Equal(cycleZero, cycleOne);
        Assert.Equal(new MatrixRgb(0xFF, 0xFF, 0x80), cycleTwo);
        Assert.Equal(3, nextSelection);

        IReadOnlyList<MatrixRgb> palette = Pid02E0RandomPaletteColorSource.CapturedPalette;
        Assert.Equal(24, palette.Count);
        Assert.Equal(24, palette.Distinct().Count());
        Assert.DoesNotContain(MatrixRgb.Black, palette);
        Assert.DoesNotContain(new MatrixRgb(0x80, 0x80, 0x80), palette);
        Assert.DoesNotContain(new MatrixRgb(0xFF, 0xFF, 0xFF), palette);
        Assert.All(
            palette.SelectMany(color => new[] { color.Red, color.Green, color.Blue }),
            channel => Assert.Contains(channel, new byte[] { 0x00, 0x80, 0xFF }));
    }

    [Fact]
    public void SpectrumAndRandomBreathingFixture_PinsProductionOracleMeasurements()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(
            "captures",
            "decoded",
            "rz09-0581-pid-02e0-bios-3.01-lighting-spectrum-random-oracle-summary.json")));
        JsonElement root = document.RootElement;
        JsonElement spectrum = root.GetProperty("spectrum");
        JsonElement random = root.GetProperty("randomBreathing");

        Assert.Equal(
            "D0186AB4A14DBFAF9ABCE91BC39F30A05CF51E6D35C6AAD137D3DB6C8BAF7628",
            root.GetProperty("sourceCapture").GetProperty("sha256").GetString());
        Assert.Equal(3, spectrum.GetProperty("completeCycleCount").GetInt32());
        Assert.Equal(
            Pid02E0LightingTiming.SpectrumCycle.TotalMilliseconds,
            spectrum.GetProperty("meanPeriodMilliseconds").GetDouble(),
            precision: 3);
        Assert.Equal(256, random.GetProperty("boundedCycleCount").GetInt32());
        Assert.Equal(12, random.GetProperty("consecutiveRepeatedPlateauCount").GetInt32());

        var effect = new Pid02E0SpectrumEffect();
        MatrixRgb[] expectedAnchors = spectrum.GetProperty("anchorsRgbHex")
            .EnumerateArray()
            .Select(value => ParseRgb(value.GetString()!))
            .ToArray();
        Assert.Equal(51, expectedAnchors.Length);
        for (int index = 0; index < expectedAnchors.Length; index++)
        {
            TimeSpan elapsed = TimeSpan.FromTicks(
                (long)Math.Round(Pid02E0LightingTiming.SpectrumCycle.Ticks * (index / 50.0)));
            Assert.Equal(expectedAnchors[index], effect.RenderFrame(elapsed)[1]);
        }

        MatrixRgb[] expectedPalette = random.GetProperty("paletteRgbHex")
            .EnumerateArray()
            .Select(value => ParseRgb(value.GetString()!))
            .ToArray();
        Assert.Equal(Pid02E0RandomPaletteColorSource.CapturedPalette, expectedPalette);
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
    public void Tidal_UsesSanitizedCapturedDirectionTemplatesAndSubstitutesColors()
    {
        MatrixRgb red = new(255, 0, 0);
        MatrixRgb cyan = new(0, 255, 255);
        var left = new Pid02E0TidalEffect(
            red,
            cyan,
            randomColors: false,
            LightingDirection.Leftward);
        var right = new Pid02E0TidalEffect(
            red,
            cyan,
            randomColors: false,
            LightingDirection.Rightward);

        Pid02E0MatrixFrame leftFrame = left.RenderFrame(TimeSpan.FromMilliseconds(500));
        Pid02E0MatrixFrame rightFrame = right.RenderFrame(TimeSpan.FromMilliseconds(500));

        Assert.NotEqual(leftFrame.ToArray(), rightFrame.ToArray());
        AssertHolesBlack(leftFrame);
        AssertHolesBlack(rightFrame);
        Assert.All(leftFrame.ToArray(), color =>
            Assert.True(
                color == MatrixRgb.Black
                || color.Green == 0 && color.Blue == 0
                || color.Red == 0 && color.Green == color.Blue));
    }

    [Fact]
    public void Tidal_LoopsAtFiveSecondsAndAdvancesEveryFiftyMilliseconds()
    {
        var effect = new Pid02E0TidalEffect(
            Green,
            new MatrixRgb(0, 0, 255),
            randomColors: false,
            LightingDirection.Rightward);

        Assert.Equal(TimeSpan.FromMilliseconds(50), effect.GetNextFrameDelay(TimeSpan.Zero));
        AssertFramesEqual(
            effect.RenderFrame(TimeSpan.Zero),
            effect.RenderFrame(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(
            effect.RenderFrame(TimeSpan.Zero).ToArray(),
            effect.RenderFrame(TimeSpan.FromMilliseconds(50)).ToArray());
    }

    [Fact]
    public void TidalRandom_UsesOneStableDistinctPairPerCycle()
    {
        var source = new RecordingColorPairSource();
        var effect = new Pid02E0TidalEffect(
            default,
            default,
            randomColors: true,
            LightingDirection.Rightward,
            source);

        Pid02E0MatrixFrame first = effect.RenderFrame(TimeSpan.FromMilliseconds(500));
        Pid02E0MatrixFrame repeated = effect.RenderFrame(TimeSpan.FromMilliseconds(500));
        Pid02E0MatrixFrame nextCycle = effect.RenderFrame(TimeSpan.FromMilliseconds(5500));

        AssertFramesEqual(first, repeated);
        Assert.NotEqual(first.ToArray(), nextCycle.ToArray());
        Assert.Equal([0L, 0L, 1L], source.RequestedCycles);
    }

    [Fact]
    public void StarlightSingle_ProducesDeterministicSparseSymmetricTwinkles()
    {
        var effect = new Pid02E0StarlightEffect(
            StarlightColorMode.Single,
            Green,
            default,
            TimeSpan.FromSeconds(1.5));

        Pid02E0MatrixFrame first = effect.RenderFrame(TimeSpan.FromSeconds(30));
        Pid02E0MatrixFrame repeated = effect.RenderFrame(TimeSpan.FromSeconds(30));
        int lit = first.ToArray().Count(color => color != MatrixRgb.Black);

        Assert.Equal(TimeSpan.FromMilliseconds(50), effect.GetNextFrameDelay(TimeSpan.Zero));
        Assert.InRange(lit, 2, 20);
        AssertFramesEqual(first, repeated);
        AssertHolesBlack(first);
        Assert.All(
            first.ToArray().Where(color => color != MatrixRgb.Black),
            color => Assert.True(color.Red == 0 && color.Green > 0 && color.Blue == 0));
    }

    [Fact]
    public void StarlightDualAndRandom_SelectPerTwinkleColors()
    {
        var dual = new Pid02E0StarlightEffect(
            StarlightColorMode.Dual,
            new MatrixRgb(255, 0, 0),
            new MatrixRgb(0, 0, 255),
            TimeSpan.FromSeconds(1.5));
        var random = new Pid02E0StarlightEffect(
            StarlightColorMode.Random,
            default,
            default,
            TimeSpan.FromSeconds(1.5));

        MatrixRgb[] dualColors = Enumerable.Range(0, 200)
            .SelectMany(index => dual.RenderFrame(TimeSpan.FromMilliseconds(index * 50)).ToArray())
            .Where(color => color != MatrixRgb.Black)
            .ToArray();
        MatrixRgb[] randomColors = Enumerable.Range(0, 200)
            .SelectMany(index => random.RenderFrame(TimeSpan.FromMilliseconds(index * 50)).ToArray())
            .Where(color => color != MatrixRgb.Black)
            .ToArray();

        Assert.Contains(dualColors, color => color.Red > 0 && color.Blue == 0);
        Assert.Contains(dualColors, color => color.Red == 0 && color.Blue > 0);
        Assert.True(
            randomColors.Select(color => (color.Red > 0, color.Green > 0, color.Blue > 0))
                .Distinct()
                .Count() >= 4);
    }

    [Theory]
    [InlineData(StarlightLightingDuration.Short, 0.5)]
    [InlineData(StarlightLightingDuration.Medium, 1.5)]
    [InlineData(StarlightLightingDuration.Long, 2.0)]
    public void StarlightDuration_UsesCapturedNominalChoices(
        StarlightLightingDuration duration,
        double expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            ProductionLightingPort.StarlightDuration(duration));
    }

    [Fact]
    public void Effects_RejectInvalidTimingInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pid02E0SpectrumEffect(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Pid02E0BreathingEffect.Single(Green, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Pid02E0ReactiveEffect(Green, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Pid02E0StarlightEffect(
                StarlightColorMode.Single,
                Green,
                default,
                TimeSpan.Zero));

        IPid02E0LightingEffect[] effects =
        [
            new Pid02E0StaticEffect(Green),
            new Pid02E0SpectrumEffect(),
            Pid02E0BreathingEffect.Single(Green),
            new Pid02E0ReactiveEffect(Green, TimeSpan.FromSeconds(1)),
            new Pid02E0TidalEffect(
                Green,
                new MatrixRgb(0, 0, 255),
                randomColors: false,
                LightingDirection.Rightward),
            new Pid02E0StarlightEffect(
                StarlightColorMode.Single,
                Green,
                default,
                TimeSpan.FromSeconds(1.5)),
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

    private static MatrixRgb ParseRgb(string hex) => new(
        Convert.ToByte(hex[..2], 16),
        Convert.ToByte(hex.Substring(2, 2), 16),
        Convert.ToByte(hex.Substring(4, 2), 16));

    private static string RepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeSegments)}.");
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

    private sealed class RecordingColorPairSource : IPid02E0RandomColorPairSource
    {
        internal List<long> RequestedCycles { get; } = [];

        public (MatrixRgb Primary, MatrixRgb Secondary) ColorsForCycle(long cycleIndex)
        {
            RequestedCycles.Add(cycleIndex);
            return cycleIndex == 0
                ? (new MatrixRgb(255, 0, 0), new MatrixRgb(0, 0, 255))
                : (new MatrixRgb(0, 255, 0), new MatrixRgb(255, 0, 255));
        }
    }
}
