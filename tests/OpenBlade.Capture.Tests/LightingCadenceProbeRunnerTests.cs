using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class LightingCadenceProbeRunnerTests
{
    [Fact]
    public async Task RunAsync_UsesAbsoluteDeadlinesAndQualifiesHealthyTransport()
    {
        var time = new ManualTimeProvider();
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            });
        int inFlight = 0;
        int maximumInFlight = 0;

        LightingCadenceStageResult result = await runner.RunAsync(
            60,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                maximumInFlight = Math.Max(maximumInFlight, ++inFlight);
                time.Advance(TimeSpan.FromMilliseconds(5));
                inFlight--;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.TransportQualified);
        Assert.Equal(300, result.ScheduledFrames);
        Assert.Equal(300, result.CompletedFrames);
        Assert.Equal(0, result.SkippedFrames);
        Assert.Equal(60, result.AchievedFramesPerSecond);
        Assert.Equal(1, maximumInFlight);
        Assert.All(
            result.Samples,
            sample => Assert.Equal(5, sample.WriteDurationMilliseconds, precision: 6));
    }

    [Fact]
    public async Task RunAsync_SkipsMissedDeadlinesInsteadOfBursting()
    {
        var time = new ManualTimeProvider();
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            });
        int inFlight = 0;
        int maximumInFlight = 0;

        LightingCadenceStageResult result = await runner.RunAsync(
            60,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                maximumInFlight = Math.Max(maximumInFlight, ++inFlight);
                time.Advance(TimeSpan.FromMilliseconds(25));
                inFlight--;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.TransportQualified);
        Assert.True(result.SkippedFrames > 0);
        Assert.Equal(result.AttemptedFrames, result.CompletedFrames);
        Assert.Equal(1, maximumInFlight);
        Assert.True(result.CompletedFrames < result.ScheduledFrames);
    }

    [Fact]
    public async Task RunAsync_StopsAfterIndeterminateWrite()
    {
        var time = new ManualTimeProvider();
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            });
        int attempts = 0;

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                attempts++;
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Injected indeterminate write.");
            },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(WriteSubmissionState.Indeterminate, result.FailureSubmissionState);
        Assert.Equal("matrixWriteFailed", result.Failure);
        Assert.False(result.TransportQualified);
    }

    [Fact]
    public async Task RunAsync_RecordsProcessCpuAcrossMeasuredWindow()
    {
        var time = new ManualTimeProvider();
        var cpuReadings = new Queue<TimeSpan>(
            [TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1600)]);
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            () => cpuReadings.Dequeue(),
            logicalProcessorCount: 12);

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        LightingCadenceProcessorUsage usage = Assert.IsType<LightingCadenceProcessorUsage>(
            result.ProcessorUsage);
        Assert.Equal(600, usage.ProcessCpuTimeMilliseconds);
        Assert.Equal(1000, usage.MeasurementWallTimeMilliseconds, precision: 6);
        Assert.Equal(12, usage.LogicalProcessorCount);
        Assert.Equal(60, usage.SingleLogicalProcessorPercent, precision: 10);
        Assert.Equal(5, usage.WholeMachineCapacityPercent, precision: 10);
        Assert.Empty(cpuReadings);
    }

    [Fact]
    public async Task RunAsync_LeavesProcessorUsageNullWhenWarmupWriteFails()
    {
        var time = new ManualTimeProvider();
        int cpuReads = 0;
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            () =>
            {
                cpuReads++;
                return TimeSpan.Zero;
            },
            logicalProcessorCount: 12);

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            (_, _) => throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Injected warmup failure."),
            CancellationToken.None);

        Assert.Null(result.ProcessorUsage);
        Assert.Equal(0, cpuReads);
    }

    [Fact]
    public void CreateFrame_PreservesHolesAndEncodesGrayCounter()
    {
        AmbientLightingFrame frame0 = LightingCadenceProbeRunner.CreateFrame(
            0,
            TimeSpan.Zero);
        AmbientLightingFrame frame1 = LightingCadenceProbeRunner.CreateFrame(
            1,
            TimeSpan.Zero);

        Assert.Equal(default, frame0.Slots[0]);
        Assert.Equal(new RgbColor(0x20, 0x58, 0xFF), frame0.Slots[1]);
        Assert.Equal(new RgbColor(0xFF, 0x20, 0x20), frame1.Slots[1]);
        Assert.Equal(frame0.Slots[2], frame1.Slots[2]);
    }

    [Fact]
    public void CreateFrame_TidalUsesCapturedOracleAndPreservesHoles()
    {
        TimeSpan scheduledAt = TimeSpan.FromMilliseconds(25);

        AmbientLightingFrame actual = LightingCadenceProbeRunner.CreateFrame(
            LightingCadencePattern.Tidal,
            1,
            scheduledAt);
        Pid02E0MatrixFrame expected = Pid02E0TidalOracle.Render(
            LightingDirection.Rightward,
            scheduledAt,
            new MatrixRgb(0x00, 0xFF, 0x00),
            new MatrixRgb(0x00, 0x00, 0xFF));

        Assert.True(actual.IsWellFormed());
        Assert.All(
            Enumerable.Range(0, AmbientLightingFrame.SlotCount),
            slot => Assert.Equal(
                new RgbColor(
                    expected[slot].Red,
                    expected[slot].Green,
                    expected[slot].Blue),
                actual.Slots[slot]));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(60)]
    public void CoarseDelayFor_AvoidsSystemTimerAtHighRefreshRates(int rate)
    {
        TimeSpan frameBudget = LightingCadenceProbeRunner.ScheduledAt(1, rate);

        TimeSpan coarseDelay = LightingCadenceProbeRunner.CoarseDelayFor(frameBudget);

        Assert.Equal(TimeSpan.Zero, coarseDelay);
    }

    [Fact]
    public void CoarseDelayFor_LeavesTwentyFiveMillisecondsBeforeThirtyHzDeadline()
    {
        TimeSpan frameBudget = LightingCadenceProbeRunner.ScheduledAt(1, 30);

        TimeSpan coarseDelay = LightingCadenceProbeRunner.CoarseDelayFor(frameBudget);

        Assert.Equal(TimeSpan.FromMilliseconds(25), frameBudget - coarseDelay);
    }

    [Fact]
    public void CalculateProcessorUsage_ReportsOneCoreAndWholeMachineCapacity()
    {
        LightingCadenceProcessorUsage? usage =
            LightingCadenceProbeRunner.CalculateProcessorUsage(
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5),
                logicalProcessorCount: 12);

        Assert.NotNull(usage);
        Assert.Equal(3000, usage.ProcessCpuTimeMilliseconds);
        Assert.Equal(5000, usage.MeasurementWallTimeMilliseconds);
        Assert.Equal(12, usage.LogicalProcessorCount);
        Assert.Equal(60, usage.SingleLogicalProcessorPercent);
        Assert.Equal(5, usage.WholeMachineCapacityPercent);
    }

    [Fact]
    public void CalculateProcessorUsage_ReturnsNullForZeroWallTime()
    {
        Assert.Null(LightingCadenceProbeRunner.CalculateProcessorUsage(
            TimeSpan.Zero,
            TimeSpan.Zero,
            logicalProcessorCount: 12));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
