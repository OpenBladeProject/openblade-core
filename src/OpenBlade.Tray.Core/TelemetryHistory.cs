namespace OpenBlade.Tray;

public sealed record TelemetryMetricStatistics(
    TelemetryMetric Metric,
    int Count,
    double? Minimum,
    double? Maximum,
    double? Average)
{
    public int MissingCount { get; init; }
}

public sealed record TelemetryChartPoint(DateTimeOffset TimestampUtc, double? Value);

public sealed record TelemetrySeriesSnapshot(
    TelemetryMetric Metric,
    IReadOnlyList<TelemetryChartPoint> Points,
    TelemetryMetricStatistics Statistics);

public sealed record TelemetryHistorySnapshot(
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc,
    int RawSampleCount,
    IReadOnlyList<TelemetrySeriesSnapshot> Series,
    IReadOnlyList<TelemetryEvent> Events)
{
    public TimeSpan? ExpectedSampleInterval { get; init; }

    public IReadOnlyList<TelemetrySample> RawSamples { get; init; } = [];

    public TimeSpan? MaximumContinuousGap => ExpectedSampleInterval * 2;
}

public sealed class TelemetryHistory
{
    public const int DefaultCapacity = 3_600;
    public const int DefaultMaximumChartPoints = 600;

    private readonly int capacity;
    private readonly Queue<TelemetrySample> samples;
    private readonly Queue<TelemetryEvent> events = new();
    private TelemetrySample? lastSample;

    public TelemetryHistory(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        this.capacity = capacity;
        samples = new Queue<TelemetrySample>(capacity);
    }

    public int Count => samples.Count;

    public IReadOnlyList<TelemetryEvent> AddSample(TelemetrySample sample)
    {
        TelemetryValidation.ValidateSample(sample);
        TelemetrySample? previous = lastSample;
        if (previous is not null && sample.TimestampUtc <= previous.TimestampUtc)
        {
            throw new InvalidDataException("Telemetry sample timestamps must increase.");
        }

        List<TelemetryEvent> emitted = [];
        if (previous is not null && previous.PowerSource != sample.PowerSource)
        {
            emitted.Add(new TelemetryEvent(
                sample.TimestampUtc,
                TelemetryEventKind.PowerSourceChanged,
                previous.PowerSource.ToString(),
                sample.PowerSource.ToString()));
        }
        if (previous is not null
            && !string.Equals(previous.ProfileIdentity, sample.ProfileIdentity, StringComparison.Ordinal))
        {
            emitted.Add(new TelemetryEvent(
                sample.TimestampUtc,
                TelemetryEventKind.ProfileChanged,
                previous.ProfileIdentity,
                sample.ProfileIdentity));
        }

        samples.Enqueue(sample);
        lastSample = sample;
        foreach (TelemetryEvent marker in emitted)
        {
            events.Enqueue(marker);
        }
        while (samples.Count > capacity)
        {
            samples.Dequeue();
        }
        DateTimeOffset oldest = samples.Peek().TimestampUtc;
        while (events.Count > 0 && events.Peek().TimestampUtc < oldest)
        {
            events.Dequeue();
        }
        return emitted;
    }

    public TelemetryHistorySnapshot Snapshot(
        DateTimeOffset? rangeStartUtc = null,
        DateTimeOffset? rangeEndUtc = null,
        int maximumChartPoints = DefaultMaximumChartPoints,
        TimeSpan? expectedSampleInterval = null)
    {
        if (rangeStartUtc is { } start)
        {
            TelemetryValidation.ValidateUtc(start, nameof(rangeStartUtc));
        }
        if (rangeEndUtc is { } end)
        {
            TelemetryValidation.ValidateUtc(end, nameof(rangeEndUtc));
        }
        if (rangeStartUtc > rangeEndUtc)
        {
            throw new ArgumentException("Telemetry range start must not follow its end.");
        }
        if (maximumChartPoints < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChartPoints));
        }
        if (expectedSampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSampleInterval));
        }

        TelemetrySample[] selected = samples
            .Where(sample => (rangeStartUtc is null || sample.TimestampUtc >= rangeStartUtc)
                && (rangeEndUtc is null || sample.TimestampUtc <= rangeEndUtc))
            .ToArray();
        TelemetryEvent[] selectedEvents = events
            .Where(value => (rangeStartUtc is null || value.TimestampUtc >= rangeStartUtc)
                && (rangeEndUtc is null || value.TimestampUtc <= rangeEndUtc))
            .ToArray();
        return TelemetrySnapshotProjector.CreateSnapshot(
            selected,
            selectedEvents,
            maximumChartPoints,
            expectedSampleInterval);
    }
}
