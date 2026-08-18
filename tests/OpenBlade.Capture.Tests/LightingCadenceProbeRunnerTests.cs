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
        Assert.False(result.QualificationExcludesControllerGuard);
        Assert.Equal(
            result.WriteDuration,
            result.QualificationWriteDuration);
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
    public async Task RunAsync_RecordsOptInWritePhaseSamplesAndSummaries()
    {
        var time = new ManualTimeProvider();
        var timings = new BatchTimingSource();
        int clearCalls = 0;
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            clearWriteTimings: () => clearCalls++,
            takeWriteTimings: timings.TakeAll);

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                timings.Record(new MatrixWritePhaseTiming(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(2),
                    TimeSpan.FromMilliseconds(3),
                    TimeSpan.FromMilliseconds(1)));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, clearCalls);
        LightingCadenceWritePhaseSample first = Assert.IsType<LightingCadenceWritePhaseSample>(
            result.Samples[0].WritePhases);
        Assert.Equal(1, first.QueueWaitMilliseconds);
        Assert.Equal(2, first.ControllerGuardMilliseconds);
        Assert.Equal(3, first.NativeSubmitToCompletionMilliseconds);
        Assert.Equal(1, first.ManagedCompletionMilliseconds);
        Assert.Equal(3, first.ResidualMilliseconds);
        LightingCadenceWritePhaseSummary summary =
            Assert.IsType<LightingCadenceWritePhaseSummary>(result.WritePhases);
        Assert.Equal(result.CompletedFrames, summary.CapturedSamples);
        Assert.Equal(0, summary.AlignmentFailures);
        Assert.Equal(3, summary.Residual.P99Milliseconds);
        Assert.True(result.QualificationExcludesControllerGuard);
        Assert.Equal(8, result.QualificationWriteDuration?.P99Milliseconds);
    }

    [Fact]
    public async Task RunAsync_ExcludesGuardOnlyFromP99FrameBudgetGate()
    {
        var time = new ManualTimeProvider();
        var timings = new BatchTimingSource();
        int writes = 0;
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            clearWriteTimings: () => { },
            takeWriteTimings: timings.TakeAll);

        LightingCadenceStageResult result = await runner.RunAsync(
            60,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                writes++;
                bool lastWriteWasLong = writes % 50 == 0;
                time.Advance(TimeSpan.FromMilliseconds(
                    lastWriteWasLong ? 20 : 5));
                timings.Record(new MatrixWritePhaseTiming(
                    TimeSpan.Zero,
                    lastWriteWasLong
                        ? TimeSpan.FromMilliseconds(18)
                        : TimeSpan.FromMilliseconds(2),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.Zero));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.QualificationExcludesControllerGuard);
        Assert.True(
            result.WriteDuration.P99Milliseconds > result.FrameBudgetMilliseconds);
        Assert.Equal(3, result.QualificationWriteDuration?.P99Milliseconds);
        Assert.Equal(300, result.CompletedFrames);
        Assert.Equal(0, result.SkippedFrames);
        Assert.True(result.TransportQualified);
    }

    [Fact]
    public async Task RunAsync_ExtraTimingDisablesGuardExclusionAndFallsBackToEndToEnd()
    {
        var time = new ManualTimeProvider();
        var timings = new BatchTimingSource();
        int writes = 0;
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            clearWriteTimings: () => { },
            takeWriteTimings: timings.TakeAll);

        LightingCadenceStageResult result = await runner.RunAsync(
            60,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                writes++;
                bool lastWriteWasLong = writes % 50 == 0;
                time.Advance(TimeSpan.FromMilliseconds(
                    lastWriteWasLong ? 20 : 5));
                var timing = new MatrixWritePhaseTiming(
                    TimeSpan.Zero,
                    lastWriteWasLong
                        ? TimeSpan.FromMilliseconds(18)
                        : TimeSpan.FromMilliseconds(2),
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.Zero);
                timings.Record(writes == 50 ? [timing, timing] : [timing]);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        LightingCadenceWritePhaseSummary summary =
            Assert.IsType<LightingCadenceWritePhaseSummary>(result.WritePhases);
        Assert.Equal(1, summary.AlignmentFailures);
        Assert.Equal(result.CompletedFrames - 1, summary.CapturedSamples);
        Assert.False(result.QualificationExcludesControllerGuard);
        Assert.Equal(result.WriteDuration, result.QualificationWriteDuration);
        Assert.False(result.TransportQualified);
    }

    [Fact]
    public async Task RunAsync_OverAttributedTimingDisablesGuardExclusion()
    {
        var time = new ManualTimeProvider();
        var timings = new BatchTimingSource();
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            },
            clearWriteTimings: () => { },
            takeWriteTimings: timings.TakeAll);

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5),
            (_, _) =>
            {
                time.Advance(TimeSpan.FromMilliseconds(5));
                timings.Record(new MatrixWritePhaseTiming(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(2),
                    TimeSpan.FromMilliseconds(2),
                    TimeSpan.FromMilliseconds(1)));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        LightingCadenceWritePhaseSummary summary =
            Assert.IsType<LightingCadenceWritePhaseSummary>(result.WritePhases);
        Assert.Equal(result.CompletedFrames, summary.AlignmentFailures);
        Assert.Equal(0, summary.CapturedSamples);
        Assert.False(result.QualificationExcludesControllerGuard);
        Assert.Equal(result.WriteDuration, result.QualificationWriteDuration);
    }

    [Fact]
    public async Task RunAsync_MaximumGapRemainsIndependentEndToEndGate()
    {
        var time = new ManualTimeProvider();
        int writes = 0;
        var runner = new LightingCadenceProbeRunner(
            time,
            (duration, _) =>
            {
                time.Advance(duration);
                return Task.CompletedTask;
            });

        LightingCadenceStageResult result = await runner.RunAsync(
            30,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60),
            (_, _) =>
            {
                writes++;
                time.Advance(TimeSpan.FromMilliseconds(writes == 900 ? 70 : 1));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.CompletedFrames / (double)result.ScheduledFrames >= 0.995);
        Assert.True(result.MaximumCompletionGapMilliseconds > result.FrameBudgetMilliseconds * 2);
        Assert.False(result.TransportQualified);
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

    private sealed class BatchTimingSource
    {
        private readonly List<MatrixWritePhaseTiming> pending = [];

        internal void Record(params MatrixWritePhaseTiming[] timings) =>
            pending.AddRange(timings);

        internal IReadOnlyList<MatrixWritePhaseTiming> TakeAll()
        {
            MatrixWritePhaseTiming[] drained = pending.ToArray();
            pending.Clear();
            return drained;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
