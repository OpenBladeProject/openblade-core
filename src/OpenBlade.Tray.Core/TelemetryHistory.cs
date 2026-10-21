namespace OpenBlade.Tray;

public sealed record TelemetryMetricStatistics(
    TelemetryMetric Metric,
    int Count,
    double? Minimum,
    double? Maximum,
    double? Average);

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
        TelemetrySeriesSnapshot[] series = Enum.GetValues<TelemetryMetric>()
            .Select(metric => CreateSeries(
                selected,
                metric,
                maximumChartPoints,
                expectedSampleInterval))
            .ToArray();
        TelemetryEvent[] selectedEvents = events
            .Where(value => (rangeStartUtc is null || value.TimestampUtc >= rangeStartUtc)
                && (rangeEndUtc is null || value.TimestampUtc <= rangeEndUtc))
            .ToArray();
        return new TelemetryHistorySnapshot(
            selected.FirstOrDefault()?.TimestampUtc,
            selected.LastOrDefault()?.TimestampUtc,
            selected.Length,
            series,
            selectedEvents)
        {
            ExpectedSampleInterval = expectedSampleInterval,
        };
    }

    private static TelemetrySeriesSnapshot CreateSeries(
        IReadOnlyList<TelemetrySample> samples,
        TelemetryMetric metric,
        int maximumChartPoints,
        TimeSpan? expectedSampleInterval)
    {
        int[] values = samples.Select(sample => sample.Value(metric)).OfType<int>().ToArray();
        TelemetryMetricStatistics statistics = values.Length == 0
            ? new TelemetryMetricStatistics(metric, 0, null, null, null)
            : new TelemetryMetricStatistics(
                metric,
                values.Length,
                values.Min(),
                values.Max(),
                values.Average(value => (double)value));
        var points = new List<TelemetryChartPoint>(samples.Count);
        TelemetrySample? previous = null;
        foreach (TelemetrySample sample in samples)
        {
            if (previous is not null
                && expectedSampleInterval is { } interval
                && sample.TimestampUtc - previous.TimestampUtc > interval * 2)
            {
                points.Add(new TelemetryChartPoint(previous.TimestampUtc + interval, null));
            }
            points.Add(new TelemetryChartPoint(sample.TimestampUtc, sample.Value(metric)));
            previous = sample;
        }
        return new TelemetrySeriesSnapshot(
            metric,
            Downsample(points, maximumChartPoints),
            statistics);
    }

    internal static IReadOnlyList<TelemetryChartPoint> Downsample(
        IReadOnlyList<TelemetryChartPoint> source,
        int maximumPoints)
    {
        if (source.Count <= maximumPoints)
        {
            return source.ToArray();
        }

        var collapsed = new List<TelemetryChartPoint>(source.Count);
        bool previousWasGap = false;
        foreach (TelemetryChartPoint point in source)
        {
            bool gap = point.Value is null;
            if (!gap || !previousWasGap)
            {
                collapsed.Add(point);
            }
            previousWasGap = gap;
        }
        if (collapsed.Count <= maximumPoints)
        {
            return collapsed;
        }

        var required = new SortedSet<int> { 0, collapsed.Count - 1 };
        for (int index = 0; index < collapsed.Count; index++)
        {
            if (collapsed[index].Value is null)
            {
                required.Add(index);
            }
        }
        if (required.Count > maximumPoints)
        {
            return EvenlySelect(required.Select(index => collapsed[index]).ToArray(), maximumPoints);
        }

        var selected = new SortedSet<int>(required);
        int remaining = maximumPoints - selected.Count;
        for (int slot = 1; slot <= remaining; slot++)
        {
            int index = (int)Math.Round(
                slot * (collapsed.Count - 1d) / (remaining + 1d),
                MidpointRounding.AwayFromZero);
            selected.Add(index);
        }
        if (selected.Count < maximumPoints)
        {
            for (int index = 1; index < collapsed.Count - 1 && selected.Count < maximumPoints; index++)
            {
                selected.Add(index);
            }
        }
        return selected.Select(index => collapsed[index]).ToArray();
    }

    private static IReadOnlyList<TelemetryChartPoint> EvenlySelect(
        IReadOnlyList<TelemetryChartPoint> source,
        int count)
    {
        var result = new List<TelemetryChartPoint>(count);
        for (int index = 0; index < count; index++)
        {
            int sourceIndex = (int)Math.Round(
                index * (source.Count - 1d) / (count - 1d),
                MidpointRounding.AwayFromZero);
            result.Add(source[sourceIndex]);
        }
        return result;
    }
}
