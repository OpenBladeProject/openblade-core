using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528HyperBoostGpuValidationCommandTests
{
    private static readonly string[] Arguments = ["--confirm-target", Rz090528HyperBoostGpuValidationCommand.Confirmation,
        "--confirm-pad-ready", Rz090528HyperBoostValidationCommand.PadConfirmation];

    [Fact]
    public async Task BothFixedScenariosHaveTheRequiredOrderingAndRestoreZero()
    {
        var events = new List<string>();
        var gpu = new Gpu(events);
        using var output = new StringWriter();
        int result = await Run(gpu, events, output);
        Assert.Equal(0, result);
        Assert.Equal(new[] { "HB", "GPU5", "GPU0", "Performance", "GPU5", "HB", "Performance", "GPU0" }, events);
        Assert.Equal(0, gpu.Current);
        Assert.Contains("\"Success\": true", output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GpuFailureOrIndependentDisagreementStopsBeforeThermalExit(bool writeFailure)
    {
        var events = new List<string>();
        var gpu = new Gpu(events) { FailWrite = writeFailure, DisagreeAfterWrite = !writeFailure };
        using var output = new StringWriter();
        Assert.Equal(3, await Run(gpu, events, output));
        Assert.Equal(new[] { "HB", "GPU5" }, events);
        Assert.Contains("\"ManualRecoveryRequired\": true", output.ToString());
    }

    [Theory]
    [InlineData("617.13", 0)]
    [InlineData("617.14", 5)]
    public async Task WrongDriverOrNonzeroBaselineNeverMutates(string driver, int baseline)
    {
        var events = new List<string>();
        var gpu = new Gpu(events) { Current = baseline };
        using var output = new StringWriter();
        Assert.Equal(3, await Run(gpu, events, output, driver));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    [InlineData(4, 8)]
    public async Task AnyLaterGpuFailureStopsTheFixedSequence(int failureAt, int expectedEvents)
    {
        var events = new List<string>();
        var gpu = new Gpu(events) { FailAt = failureAt };
        using var output = new StringWriter();
        Assert.Equal(3, await Run(gpu, events, output));
        Assert.Equal(expectedEvents, events.Count);
        Assert.Contains("\"ManualRecoveryRequired\": true", output.ToString());
    }

    [Fact]
    public async Task MissingConfirmationNeverCreatesHardware()
    {
        using var output = new StringWriter();
        Assert.Equal(2, await Rz090528HyperBoostGpuValidationCommand.RunAsync([], _ => throw new Exception(),
            _ => throw new Exception(), new Gpu([]), () => throw new Exception(), output, CancellationToken.None));
    }

    private static Task<int> Run(Gpu gpu, List<string> events, TextWriter output, string driver = "617.14") =>
        Rz090528HyperBoostGpuValidationCommand.RunAsync(Arguments, observer => new Runner(observer, events),
            guard => { gpu.Guard = guard; return gpu; }, gpu, () => driver, output, CancellationToken.None);

    private sealed class Gpu(List<string> events) : IGpuClockOffsetValidationController, IGpuClockOffsetProbe
    {
        internal int Current;
        internal bool FailWrite;
        internal bool DisagreeAfterWrite;
        internal int FailAt;
        private int writes;
        internal Func<bool> Guard = () => true;
        public GpuClockOffsetCapabilities Query() => new("NVIDIA GeForce RTX 5090 Laptop GPU",
            new(-15, 15, Current), new(-15, 15, Current));
        GpuClockOffsetCapabilities IGpuClockOffsetProbe.Query() => DisagreeAfterWrite && Current == 5
            ? Query() with { Memory = new(-15, 15, 0) } : Query();
        public void Set(GpuClockOffsetValidationSettings settings)
        {
            if (!Guard())
            {
                throw new InvalidOperationException();
            }

            events.Add($"GPU{settings.CoreMegahertz}");
            Current = settings.CoreMegahertz;
            if (FailWrite || ++writes == FailAt)
            {
                throw new InvalidOperationException();
            }
        }
    }

    private sealed class Runner(ExactHyperBoostValidationObserver observer, List<string> events)
        : IPerformanceFanSetterValidationRunner
    {
        public async Task<PerformanceFanSetterValidationResult> RunAsync(PerformanceFanValidationTarget target, CancellationToken token)
        {
            bool applied = false, restored = false;
            bool baseline = await observer(ExactHyperBoostValidationStage.Baseline, _ => Task.FromResult(true), token);
            if (baseline)
            {
                events.Add("HB"); applied = true;
                if (await observer(ExactHyperBoostValidationStage.Candidate, _ => Task.FromResult(true), token))
                {
                    events.Add("Performance"); restored = true;
                    restored &= await observer(ExactHyperBoostValidationStage.Restored, _ => Task.FromResult(true), token);
                }
            }
            return new(target, new("01010200", "01020200"), null, null, [], null,
                applied, applied, applied, restored, restored, restored, false, !restored,
                restored ? PerformanceFanValidationFailure.None : PerformanceFanValidationFailure.SustainedValidationFailed);
        }
    }
}
