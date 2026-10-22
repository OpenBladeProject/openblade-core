namespace OpenBlade.Tray;

public static class TelemetryChartViewport
{
    public static TelemetryHistorySnapshot BuildSnapshot(
        TelemetryHistorySnapshot retainedSnapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumChartPoints = TelemetryHistory.DefaultMaximumChartPoints)
    {
        ArgumentNullException.ThrowIfNull(retainedSnapshot);
        TelemetryValidation.ValidateUtc(startUtc, nameof(startUtc));
        TelemetryValidation.ValidateUtc(endUtc, nameof(endUtc));
        if (startUtc > endUtc)
        {
            throw new ArgumentException(
                "Telemetry viewport start must not follow its end.",
                nameof(startUtc));
        }
        if (maximumChartPoints < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChartPoints));
        }
        if (retainedSnapshot.ExpectedSampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedSnapshot),
                "The expected telemetry sample interval must be positive.");
        }
        if (retainedSnapshot.RawSampleCount != retainedSnapshot.RawSamples.Count)
        {
            throw new InvalidOperationException(
                "A telemetry viewport requires the complete retained raw sample set.");
        }

        TelemetrySample[] samples = retainedSnapshot.RawSamples
            .Where(sample => sample.TimestampUtc >= startUtc && sample.TimestampUtc <= endUtc)
            .ToArray();
        TelemetryEvent[] events = retainedSnapshot.Events
            .Where(value => value.TimestampUtc >= startUtc && value.TimestampUtc <= endUtc)
            .ToArray();
        return TelemetryHistory.CreateSnapshot(
            samples,
            events,
            maximumChartPoints,
            retainedSnapshot.ExpectedSampleInterval);
    }
}

public sealed record TelemetryTimeRangeSelection
{
    private TelemetryTimeRangeSelection(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool followLatest)
    {
        SourceStartUtc = sourceStartUtc;
        SourceEndUtc = sourceEndUtc;
        StartUtc = startUtc;
        EndUtc = endUtc;
        FollowLatest = followLatest;
    }

    public DateTimeOffset SourceStartUtc { get; }

    public DateTimeOffset SourceEndUtc { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public bool FollowLatest { get; private init; }

    public TimeSpan Duration => EndUtc - StartUtc;

    public static TelemetryTimeRangeSelection Create(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc,
        TimeSpan? requestedDuration = null,
        bool followLatest = true)
    {
        ValidateSourceBounds(sourceStartUtc, sourceEndUtc);
        if (requestedDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDuration));
        }

        long sourceSpanTicks = sourceEndUtc.Ticks - sourceStartUtc.Ticks;
        long durationTicks = requestedDuration is { } duration
            ? Math.Min(duration.Ticks, sourceSpanTicks)
            : sourceSpanTicks;
        DateTimeOffset startUtc = sourceEndUtc.AddTicks(-durationTicks);
        return new TelemetryTimeRangeSelection(
            sourceStartUtc,
            sourceEndUtc,
            startUtc,
            sourceEndUtc,
            followLatest);
    }

    public TelemetryTimeRangeSelection UpdateSource(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc,
        bool sourceReset = false)
    {
        ValidateSourceBounds(sourceStartUtc, sourceEndUtc);
        if (sourceReset)
        {
            return Create(sourceStartUtc, sourceEndUtc, followLatest: FollowLatest);
        }

        TimeSpan duration = Duration;
        if (FollowLatest)
        {
            return Create(sourceStartUtc, sourceEndUtc, duration, followLatest: true);
        }

        (DateTimeOffset startUtc, DateTimeOffset endUtc) = ClampWindow(
            StartUtc,
            EndUtc,
            sourceStartUtc,
            sourceEndUtc);
        return new TelemetryTimeRangeSelection(
            sourceStartUtc,
            sourceEndUtc,
            startUtc,
            endUtc,
            followLatest: false);
    }

    public TelemetryTimeRangeSelection MoveStart(
        DateTimeOffset startUtc,
        TimeSpan minimumSpan)
    {
        ValidateUtcAndMinimumSpan(startUtc, nameof(startUtc), minimumSpan);
        long availableSpanTicks = EndUtc.Ticks - SourceStartUtc.Ticks;
        long minimumSpanTicks = Math.Min(minimumSpan.Ticks, availableSpanTicks);
        long latestStartTicks = EndUtc.Ticks - minimumSpanTicks;
        long boundedTicks = Math.Clamp(
            startUtc.Ticks,
            SourceStartUtc.Ticks,
            latestStartTicks);
        return new TelemetryTimeRangeSelection(
            SourceStartUtc,
            SourceEndUtc,
            new DateTimeOffset(boundedTicks, TimeSpan.Zero),
            EndUtc,
            followLatest: false);
    }

    public TelemetryTimeRangeSelection MoveEnd(
        DateTimeOffset endUtc,
        TimeSpan minimumSpan)
    {
        ValidateUtcAndMinimumSpan(endUtc, nameof(endUtc), minimumSpan);
        long availableSpanTicks = SourceEndUtc.Ticks - StartUtc.Ticks;
        long minimumSpanTicks = Math.Min(minimumSpan.Ticks, availableSpanTicks);
        long earliestEndTicks = StartUtc.Ticks + minimumSpanTicks;
        long boundedTicks = Math.Clamp(
            endUtc.Ticks,
            earliestEndTicks,
            SourceEndUtc.Ticks);
        return new TelemetryTimeRangeSelection(
            SourceStartUtc,
            SourceEndUtc,
            StartUtc,
            new DateTimeOffset(boundedTicks, TimeSpan.Zero),
            followLatest: false);
    }

    public TelemetryTimeRangeSelection Pan(TimeSpan offset)
    {
        long boundedOffsetTicks = Math.Clamp(
            offset.Ticks,
            SourceStartUtc.Ticks - StartUtc.Ticks,
            SourceEndUtc.Ticks - EndUtc.Ticks);
        return new TelemetryTimeRangeSelection(
            SourceStartUtc,
            SourceEndUtc,
            StartUtc.AddTicks(boundedOffsetTicks),
            EndUtc.AddTicks(boundedOffsetTicks),
            followLatest: false);
    }

    public TelemetryTimeRangeSelection SetFollowLatest(bool followLatest)
    {
        if (!followLatest)
        {
            return this with { FollowLatest = false };
        }
        if (FollowLatest)
        {
            return this;
        }

        return Create(SourceStartUtc, SourceEndUtc, Duration, followLatest: true);
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ClampWindow(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc)
    {
        long sourceSpanTicks = sourceEndUtc.Ticks - sourceStartUtc.Ticks;
        long durationTicks = endUtc.Ticks - startUtc.Ticks;
        if (durationTicks >= sourceSpanTicks)
        {
            return (sourceStartUtc, sourceEndUtc);
        }

        long startTicks = Math.Clamp(
            startUtc.Ticks,
            sourceStartUtc.Ticks,
            sourceEndUtc.Ticks - durationTicks);
        return (
            new DateTimeOffset(startTicks, TimeSpan.Zero),
            new DateTimeOffset(startTicks + durationTicks, TimeSpan.Zero));
    }

    private static void ValidateSourceBounds(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc)
    {
        TelemetryValidation.ValidateUtc(sourceStartUtc, nameof(sourceStartUtc));
        TelemetryValidation.ValidateUtc(sourceEndUtc, nameof(sourceEndUtc));
        if (sourceEndUtc < sourceStartUtc)
        {
            throw new ArgumentException(
                "Telemetry source end must not precede its start.",
                nameof(sourceEndUtc));
        }
    }

    private static void ValidateUtcAndMinimumSpan(
        DateTimeOffset value,
        string valueName,
        TimeSpan minimumSpan)
    {
        TelemetryValidation.ValidateUtc(value, valueName);
        if (minimumSpan <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSpan));
        }
    }
}
