using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record LightingCadenceFrameSample(
    long FrameIndex,
    double ScheduledMilliseconds,
    double SubmittedMilliseconds,
    double CompletedMilliseconds,
    double WriteDurationMilliseconds,
    double LatenessMilliseconds);

internal sealed record LightingCadenceMetricSummary(
    double? MinimumMilliseconds,
    double? MedianMilliseconds,
    double? P95Milliseconds,
    double? P99Milliseconds,
    double? MaximumMilliseconds);

internal sealed record LightingCadenceStageResult(
    int TargetFramesPerSecond,
    double FrameBudgetMilliseconds,
    int ScheduledFrames,
    int AttemptedFrames,
    int CompletedFrames,
    int SkippedFrames,
    double AchievedFramesPerSecond,
    LightingCadenceMetricSummary WriteDuration,
    LightingCadenceMetricSummary CompletionInterval,
    double MaximumCompletionGapMilliseconds,
    bool TransportQualified,
    WriteSubmissionState? FailureSubmissionState,
    string? Failure,
    IReadOnlyList<LightingCadenceFrameSample> Samples);

internal sealed class LightingCadenceProbeRunner
{
    internal const int MinimumFramesPerSecond = 30;
    internal const int MaximumFramesPerSecond = 60;

    // Twelve bits cover the longest admitted 62-second 60 Hz stage without
    // repeating, so every high-speed-video frame has a distinct visible code.
    private static readonly int[] MarkerSlots =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly TimeSpan PatternCycle = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CoarseDelaySafetyMargin =
        TimeSpan.FromMilliseconds(25);

    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal LightingCadenceProbeRunner(
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.delay = delay ?? DelayPreciselyAsync;
    }

    internal async Task<LightingCadenceStageResult> RunAsync(
        int targetFramesPerSecond,
        TimeSpan warmupDuration,
        TimeSpan measurementDuration,
        Func<AmbientLightingFrame, CancellationToken, Task> pushFrame,
        CancellationToken cancellationToken)
    {
        ValidatePlan(targetFramesPerSecond, warmupDuration, measurementDuration);
        ArgumentNullException.ThrowIfNull(pushFrame);

        int warmupFrames = FrameCount(warmupDuration, targetFramesPerSecond);
        int measuredFrames = FrameCount(measurementDuration, targetFramesPerSecond);
        int totalFrames = checked(warmupFrames + measuredFrames);
        long startedTimestamp = timeProvider.GetTimestamp();
        var measuredSamples = new List<LightingCadenceFrameSample>(measuredFrames);
        int attemptedFrames = 0;
        int skippedFrames = 0;
        WriteSubmissionState? failureSubmissionState = null;
        string? failure = null;

        int frameIndex = 0;
        while (frameIndex < totalFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan scheduledAt = ScheduledAt(frameIndex, targetFramesPerSecond);
            TimeSpan elapsed = timeProvider.GetElapsedTime(startedTimestamp);
            if (elapsed < scheduledAt)
            {
                await delay(scheduledAt - elapsed, cancellationToken)
                    .ConfigureAwait(false);
                elapsed = timeProvider.GetElapsedTime(startedTimestamp);
            }

            while (frameIndex + 1 < totalFrames
                && elapsed >= ScheduledAt(frameIndex + 1, targetFramesPerSecond))
            {
                if (frameIndex >= warmupFrames)
                {
                    skippedFrames++;
                }
                frameIndex++;
                scheduledAt = ScheduledAt(frameIndex, targetFramesPerSecond);
            }

            bool measured = frameIndex >= warmupFrames;
            long submittedTimestamp = timeProvider.GetTimestamp();
            TimeSpan submittedAt = timeProvider.GetElapsedTime(
                startedTimestamp,
                submittedTimestamp);
            if (measured)
            {
                attemptedFrames++;
            }

            try
            {
                AmbientLightingFrame frame = CreateFrame(frameIndex, scheduledAt);
                await pushFrame(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (BladeWriteException exception)
            {
                failureSubmissionState = exception.SubmissionState;
                failure = "matrixWriteFailed";
                break;
            }

            long completedTimestamp = timeProvider.GetTimestamp();
            TimeSpan completedAt = timeProvider.GetElapsedTime(
                startedTimestamp,
                completedTimestamp);
            if (measured)
            {
                measuredSamples.Add(new LightingCadenceFrameSample(
                    frameIndex - warmupFrames,
                    scheduledAt.TotalMilliseconds,
                    submittedAt.TotalMilliseconds,
                    completedAt.TotalMilliseconds,
                    timeProvider.GetElapsedTime(
                        submittedTimestamp,
                        completedTimestamp).TotalMilliseconds,
                    Math.Max(0, (submittedAt - scheduledAt).TotalMilliseconds)));
            }

            frameIndex++;
        }

        return CreateResult(
            targetFramesPerSecond,
            measurementDuration,
            measuredFrames,
            attemptedFrames,
            skippedFrames,
            failureSubmissionState,
            failure,
            measuredSamples);
    }

    internal static AmbientLightingFrame CreateFrame(long frameIndex, TimeSpan scheduledAt)
    {
        if (frameIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }
        if (scheduledAt < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduledAt));
        }

        double phase = Pid02E0LightingMath.CyclePhase(scheduledAt, PatternCycle);
        MatrixRgb rendered = Pid02E0LightingMath.SpectrumColor(phase);
        var baseColor = new RgbColor(rendered.Red, rendered.Green, rendered.Blue);
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = AmbientLightingFrame.IsHole(slot) ? default : baseColor;
        }

        long grayCode = frameIndex ^ (frameIndex >> 1);
        for (int bit = 0; bit < MarkerSlots.Length; bit++)
        {
            slots[MarkerSlots[bit]] = (grayCode & (1L << bit)) != 0
                ? new RgbColor(0xFF, 0x20, 0x20)
                : new RgbColor(0x20, 0x58, 0xFF);
        }

        return new AmbientLightingFrame(slots);
    }

    internal static TimeSpan ScheduledAt(long frameIndex, int framesPerSecond)
    {
        if (frameIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }
        if (framesPerSecond is < MinimumFramesPerSecond or > MaximumFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        return TimeSpan.FromTicks(checked(
            (frameIndex * TimeSpan.TicksPerSecond) / framesPerSecond));
    }

    internal static TimeSpan CoarseDelayFor(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return duration > CoarseDelaySafetyMargin
            ? duration - CoarseDelaySafetyMargin
            : TimeSpan.Zero;
    }

    internal static LightingCadenceMetricSummary Summarize(
        IEnumerable<double> milliseconds)
    {
        ArgumentNullException.ThrowIfNull(milliseconds);
        double[] ordered = milliseconds.Order().ToArray();
        return ordered.Length == 0
            ? new(null, null, null, null, null)
            : new(
                ordered[0],
                Percentile(ordered, 0.50),
                Percentile(ordered, 0.95),
                Percentile(ordered, 0.99),
                ordered[^1]);
    }

    private static LightingCadenceStageResult CreateResult(
        int targetFramesPerSecond,
        TimeSpan measurementDuration,
        int scheduledFrames,
        int attemptedFrames,
        int skippedFrames,
        WriteSubmissionState? failureSubmissionState,
        string? failure,
        IReadOnlyList<LightingCadenceFrameSample> samples)
    {
        double frameBudgetMilliseconds = 1000.0 / targetFramesPerSecond;
        double achievedFramesPerSecond = samples.Count / measurementDuration.TotalSeconds;
        LightingCadenceMetricSummary writeDuration = Summarize(
            samples.Select(sample => sample.WriteDurationMilliseconds));
        double[] completionIntervals = samples
            .Zip(
                samples.Skip(1),
                (previous, current) =>
                    current.CompletedMilliseconds - previous.CompletedMilliseconds)
            .ToArray();
        LightingCadenceMetricSummary completionInterval = Summarize(completionIntervals);
        double maximumGap = completionIntervals.Length == 0
            ? 0
            : completionIntervals.Max();
        double completionRatio = samples.Count / (double)scheduledFrames;
        bool qualified = failure is null
            && completionRatio >= 0.995
            && achievedFramesPerSecond >= targetFramesPerSecond * 0.99
            && writeDuration.P99Milliseconds is { } p99
            && p99 <= frameBudgetMilliseconds
            && maximumGap <= frameBudgetMilliseconds * 2;

        return new LightingCadenceStageResult(
            targetFramesPerSecond,
            frameBudgetMilliseconds,
            scheduledFrames,
            attemptedFrames,
            samples.Count,
            skippedFrames,
            achievedFramesPerSecond,
            writeDuration,
            completionInterval,
            maximumGap,
            qualified,
            failureSubmissionState,
            failure,
            samples);
    }

    private static int FrameCount(TimeSpan duration, int framesPerSecond) =>
        checked((int)Math.Ceiling(duration.TotalSeconds * framesPerSecond));

    private async Task DelayPreciselyAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        long startedTimestamp = timeProvider.GetTimestamp();
        TimeSpan coarseDelay = CoarseDelayFor(duration);
        if (coarseDelay > TimeSpan.Zero)
        {
            await Task.Delay(coarseDelay, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        // Windows' default timer tick is too coarse to qualify 45 or 60 Hz.
        // Keep any Task.Delay at least 25 ms away from the deadline, then use
        // this validation process's current worker thread for the final wait.
        // This is intentionally local and does not change the system timer period.
        while (timeProvider.GetElapsedTime(startedTimestamp) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(64);
        }
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        double position = (ordered.Count - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        double fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static void ValidatePlan(
        int targetFramesPerSecond,
        TimeSpan warmupDuration,
        TimeSpan measurementDuration)
    {
        if (targetFramesPerSecond is < MinimumFramesPerSecond or > MaximumFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }
        if (warmupDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupDuration));
        }
        if (measurementDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(measurementDuration));
        }
    }
}
