using System.Buffers;

namespace OpenBlade.Tray;

// Pure projection shared by retained history and the selected chart viewport.
// Retention and event generation stay with TelemetryHistory.
internal static class TelemetrySnapshotProjector
{
    private readonly record struct ScratchPoint(DateTimeOffset TimestampUtc, int? Value)
    {
        public TelemetryChartPoint Materialize() => new(TimestampUtc, Value);
    }

    internal static TelemetryHistorySnapshot CreateSnapshot(
        IReadOnlyList<TelemetrySample> selected,
        IReadOnlyList<TelemetryEvent> selectedEvents,
        int maximumChartPoints,
        TimeSpan? expectedSampleInterval)
    {
        TelemetrySeriesSnapshot[] series = Enum.GetValues<TelemetryMetric>()
            .Select(metric => CreateSeries(
                selected,
                metric,
                maximumChartPoints,
                expectedSampleInterval))
            .ToArray();
        return new TelemetryHistorySnapshot(
            selected.FirstOrDefault()?.TimestampUtc,
            selected.LastOrDefault()?.TimestampUtc,
            selected.Count,
            series,
            selectedEvents)
        {
            ExpectedSampleInterval = expectedSampleInterval,
            RawSamples = selected,
        };
    }

    private static TelemetrySeriesSnapshot CreateSeries(
        IReadOnlyList<TelemetrySample> samples,
        TelemetryMetric metric,
        int maximumChartPoints,
        TimeSpan? expectedSampleInterval)
    {
        int count = 0;
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        long sum = 0;
        // Each sample can add at most one preceding gap. Scratch points contain
        // no references and stay private; only the bounded result escapes.
        ScratchPoint[] scratch = ArrayPool<ScratchPoint>.Shared.Rent(
            checked(samples.Count + Math.Max(0, samples.Count - 1)));
        try
        {
            int pointCount = 0;
            TelemetrySample? previous = null;
            foreach (TelemetrySample sample in samples)
            {
                if (previous is not null
                    && expectedSampleInterval is { } interval
                    && sample.TimestampUtc - previous.TimestampUtc > interval * 2)
                {
                    scratch[pointCount++] = new ScratchPoint(previous.TimestampUtc + interval, null);
                }
                int? value = sample.Value(metric);
                if (value is { } reading)
                {
                    count++;
                    minimum = Math.Min(minimum, reading);
                    maximum = Math.Max(maximum, reading);
                    sum += reading;
                }
                scratch[pointCount++] = new ScratchPoint(sample.TimestampUtc, value);
                previous = sample;
            }
            var statistics = new TelemetryMetricStatistics(
                metric,
                count,
                count > 0 ? minimum : null,
                count > 0 ? maximum : null,
                count > 0 ? (double)sum / count : null)
            {
                MissingCount = samples.Count - count,
            };
            return new TelemetrySeriesSnapshot(
                metric,
                Downsample(scratch.AsSpan(0, pointCount), maximumChartPoints),
                statistics);
        }
        finally
        {
            ArrayPool<ScratchPoint>.Shared.Return(scratch);
        }
    }

    private static IReadOnlyList<TelemetryChartPoint> Downsample(
        Span<ScratchPoint> source,
        int maximumPoints)
    {
        if (source.Length <= maximumPoints)
        {
            return Materialize(source);
        }

        int collapsedCount = 0;
        bool previousWasGap = false;
        foreach (ScratchPoint point in source)
        {
            bool gap = point.Value is null;
            if (!gap || !previousWasGap)
            {
                source[collapsedCount++] = point;
            }
            previousWasGap = gap;
        }
        Span<ScratchPoint> collapsed = source[..collapsedCount];
        if (collapsed.Length <= maximumPoints)
        {
            return Materialize(collapsed);
        }

        // Reserve at least half the budget for real readings before gaps can
        // crowd them out. Keep the ordinary low-gap selection unchanged.
        // Stop collecting indices early. Both this list and
        // the ordinary path's SortedSet stay bounded by the output point budget.
        var required = new List<int>();
        int gapCount = 0;
        for (int index = 0; index < collapsed.Length; index++)
        {
            if (collapsed[index].Value is null)
            {
                gapCount++;
            }
            if (index == 0 || index == collapsed.Length - 1 || collapsed[index].Value is null)
            {
                required.Add(index);
                if (required.Count >= maximumPoints || gapCount > maximumPoints / 2)
                {
                    return SelectAcrossGaps(collapsed, maximumPoints);
                }
            }
        }

        var selected = new SortedSet<int>(required);
        int remaining = maximumPoints - selected.Count;
        for (int slot = 1; slot <= remaining; slot++)
        {
            int index = (int)Math.Round(
                slot * (collapsed.Length - 1d) / (remaining + 1d),
                MidpointRounding.AwayFromZero);
            selected.Add(index);
        }
        if (selected.Count < maximumPoints)
        {
            for (int index = 1; index < collapsed.Length - 1 && selected.Count < maximumPoints; index++)
            {
                selected.Add(index);
            }
        }
        var selectedPoints = new TelemetryChartPoint[selected.Count];
        int destination = 0;
        foreach (int index in selected)
        {
            selectedPoints[destination++] = collapsed[index].Materialize();
        }
        return selectedPoints;
    }

    private static IReadOnlyList<TelemetryChartPoint> SelectAcrossGaps(
        ReadOnlySpan<ScratchPoint> source,
        int maximumPoints)
    {
        int validCount = 0;
        int firstValid = -1;
        int lastValid = -1;
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index].Value is not null)
            {
                firstValid = firstValid < 0 ? index : firstValid;
                lastValid = index;
                validCount++;
            }
        }
        // All-missing input collapses to one point before this branch.
        // Budget for one real reading per selected rank and an actual gap
        // between disconnected readings. Two slots cannot show both sides of
        // a gap without falsely joining them, so retain the first real reading.
        bool oneContinuousRun = validCount == lastValid - firstValid + 1;
        int valueBudget = Math.Min(validCount, oneContinuousRun
            ? maximumPoints
            : maximumPoints / 2 + maximumPoints % 2);
        var selected = new int[valueBudget];
        int selectedCount = 0;
        int validRank = 0;
        for (int index = firstValid; index <= lastValid; index++)
        {
            if (source[index].Value is null)
            {
                continue;
            }
            int targetRank = valueBudget == 1 ? 0 : (int)Math.Round(
                selectedCount * (validCount - 1d) / (valueBudget - 1d),
                MidpointRounding.AwayFromZero);
            if (validRank == targetRank)
            {
                selected[selectedCount++] = index;
                if (selectedCount == valueBudget)
                {
                    break;
                }
            }
            validRank++;
        }

        var points = new List<TelemetryChartPoint>(maximumPoints);
        int previousIndex = -1;
        foreach (int index in selected)
        {
            if (previousIndex >= 0)
            {
                for (int between = previousIndex + 1; between < index; between++)
                {
                    if (source[between].Value is null)
                    {
                        points.Add(source[between].Materialize());
                        break;
                    }
                }
            }
            points.Add(source[index].Materialize());
            previousIndex = index;
        }
        // Preserve outer missing boundaries when space remains; never spend
        // the whole budget on gaps while valid observations exist.
        if (points.Count < maximumPoints && firstValid > 0)
        {
            points.Insert(0, source[0].Materialize());
        }
        if (points.Count < maximumPoints && lastValid < source.Length - 1)
        {
            points.Add(source[^1].Materialize());
        }
        return points;
    }

    private static TelemetryChartPoint[] Materialize(ReadOnlySpan<ScratchPoint> source)
    {
        var result = new TelemetryChartPoint[source.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = source[index].Materialize();
        }
        return result;
    }
}
