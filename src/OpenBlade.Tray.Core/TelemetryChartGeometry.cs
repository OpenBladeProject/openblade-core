namespace OpenBlade.Tray;

public sealed record TelemetryChartTimeWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc)
{
    public static TelemetryChartTimeWindow Resolve(
        DateTimeOffset endUtc,
        TimeSpan? requestedDuration,
        DateTimeOffset? sourceStartUtc)
    {
        if (requestedDuration is { } candidateDuration && candidateDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDuration));
        }

        DateTimeOffset startUtc = requestedDuration is { } duration
            ? endUtc - duration
            : sourceStartUtc ?? endUtc;
        if (startUtc > endUtc)
        {
            throw new ArgumentException(
                "Telemetry chart start must not follow its end.",
                nameof(sourceStartUtc));
        }

        return new TelemetryChartTimeWindow(startUtc, endUtc);
    }
}

public sealed record TelemetryChartScale(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double Minimum,
    double Maximum)
{
    public static TelemetryChartScale Create(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        double minimum,
        double maximum)
    {
        if (endUtc < startUtc)
        {
            throw new ArgumentException("Telemetry chart end must not precede its start.", nameof(endUtc));
        }
        if (!double.IsFinite(minimum))
        {
            throw new ArgumentOutOfRangeException(nameof(minimum));
        }
        if (!double.IsFinite(maximum) || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        double span = maximum - minimum;
        double padding = span > 0
            ? span * 0.08
            : Math.Max(1, Math.Abs(minimum) * 0.08);
        return new TelemetryChartScale(
            startUtc,
            endUtc,
            minimum - padding,
            maximum + padding);
    }

    public double TimeFraction(DateTimeOffset timestampUtc)
    {
        if (EndUtc == StartUtc)
        {
            return 0.5;
        }

        return Math.Clamp(
            (timestampUtc - StartUtc).TotalMilliseconds /
                (EndUtc - StartUtc).TotalMilliseconds,
            0,
            1);
    }

    public DateTimeOffset TimestampAtFraction(double fraction)
    {
        double bounded = Math.Clamp(fraction, 0, 1);
        return StartUtc + TimeSpan.FromTicks(
            (long)Math.Round((EndUtc - StartUtc).Ticks * bounded));
    }

    public double ValueFractionFromTop(double value) =>
        1 - Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);

    public IReadOnlyList<DateTimeOffset> CreateTimeTicks(int count)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (StartUtc == EndUtc)
        {
            return [StartUtc];
        }

        var result = new DateTimeOffset[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = TimestampAtFraction(index / (count - 1d));
        }
        return result;
    }

    public IReadOnlyList<double> CreateValueTicks(int count)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var result = new double[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = Maximum - ((Maximum - Minimum) * index / (count - 1d));
        }
        return result;
    }
}

public static class TelemetryChartSelection
{
    public static int FindNearestSampleIndex(
        IReadOnlyList<TelemetrySample> samples,
        DateTimeOffset targetUtc)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return -1;
        }

        int low = 0;
        int high = samples.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            DateTimeOffset timestamp = samples[middle].TimestampUtc;
            if (timestamp == targetUtc)
            {
                return middle;
            }
            if (timestamp < targetUtc)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low == 0)
        {
            return 0;
        }
        if (low == samples.Count)
        {
            return samples.Count - 1;
        }

        long previousDistance = targetUtc.Ticks - samples[low - 1].TimestampUtc.Ticks;
        long nextDistance = samples[low].TimestampUtc.Ticks - targetUtc.Ticks;
        return previousDistance <= nextDistance ? low - 1 : low;
    }
}
