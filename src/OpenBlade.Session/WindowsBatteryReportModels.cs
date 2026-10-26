namespace OpenBlade.Session;

public enum WindowsBatteryReportSource
{
    WindowsPowerCfg,
}

public enum WindowsBatteryPowerSource
{
    Unknown,
    Battery,
    Ac,
}

public enum WindowsBatteryActivity
{
    Unknown,
    Active,
    ConnectedStandby,
    Suspended,
}

public sealed record WindowsBatteryStatus(
    long? DesignCapacityMilliwattHours,
    long? FullChargeCapacityMilliwattHours,
    double? HealthPercent,
    int? CycleCount);

public sealed record WindowsBatteryReportInterval(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    WindowsBatteryPowerSource PowerSource,
    WindowsBatteryActivity Activity,
    long? StartChargeMilliwattHours,
    long? FullChargeCapacityMilliwattHours,
    long? DischargeMilliwattHours,
    double? StartChargePercent);

public sealed record WindowsBatteryReport
{
    public const int MaximumIntervals = 512;

    public WindowsBatteryReport(
        WindowsBatteryReportSource source,
        DateTimeOffset generatedAtUtc,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        WindowsBatteryStatus status,
        IReadOnlyList<WindowsBatteryReportInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(intervals);
        if (generatedAtUtc.Offset != TimeSpan.Zero
            || windowStartUtc.Offset != TimeSpan.Zero
            || windowEndUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Battery report timestamps must use UTC.");
        }
        if (windowStartUtc > generatedAtUtc || generatedAtUtc > windowEndUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generatedAtUtc),
                "The generated time must be inside the report window.");
        }

        WindowsBatteryReportInterval[] copy = intervals.ToArray();
        if (copy.Length > MaximumIntervals)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervals),
                $"A battery report cannot contain more than {MaximumIntervals} intervals.");
        }
        for (int index = 0; index < copy.Length; index++)
        {
            WindowsBatteryReportInterval interval = copy[index];
            if (interval.StartUtc.Offset != TimeSpan.Zero
                || interval.EndUtc.Offset != TimeSpan.Zero
                || interval.StartUtc < windowStartUtc
                || interval.EndUtc > windowEndUtc
                || interval.StartUtc >= interval.EndUtc
                || (index > 0 && copy[index - 1].EndUtc > interval.StartUtc))
            {
                throw new ArgumentException(
                    "Battery report intervals must be ordered, non-overlapping, positive UTC ranges inside the report window.",
                    nameof(intervals));
            }
        }

        Source = source;
        GeneratedAtUtc = generatedAtUtc;
        WindowStartUtc = windowStartUtc;
        WindowEndUtc = windowEndUtc;
        Status = status;
        Intervals = Array.AsReadOnly(copy);
    }

    public WindowsBatteryReportSource Source { get; }
    public DateTimeOffset GeneratedAtUtc { get; }
    public DateTimeOffset WindowStartUtc { get; }
    public DateTimeOffset WindowEndUtc { get; }
    public WindowsBatteryStatus Status { get; }
    public IReadOnlyList<WindowsBatteryReportInterval> Intervals { get; }
}
