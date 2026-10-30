using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsBatteryReportCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SparseReportPreservesMeasurementsAndFillsEveryUncoveredRange()
    {
        WindowsBatteryReportInterval first = Interval(2, 4);
        WindowsBatteryReportInterval second = Interval(6, 8);
        WindowsBatteryReport report = Report(10, [first, second]);

        IReadOnlyList<WindowsBatteryReportInterval> actual = WindowsBatteryReportCoverage.CreateIntervals(report);

        Assert.Equal(5, actual.Count);
        AssertUnknown(actual[0], 0, 2);
        Assert.Same(first, actual[1]);
        AssertUnknown(actual[2], 4, 6);
        Assert.Same(second, actual[3]);
        AssertUnknown(actual[4], 8, 10);
        Assert.Equal(2, report.Intervals.Count);
        Assert.Same(first, report.Intervals[0]);
    }

    [Fact]
    public void EmptyPositiveWindowBecomesEntirelyUnknown()
    {
        WindowsBatteryReportInterval interval = Assert.Single(WindowsBatteryReportCoverage.CreateIntervals(Report(10, [])));
        AssertUnknown(interval, 0, 10);
    }

    [Fact]
    public void ZeroWindowDoesNotCreateAnInterval()
    {
        Assert.Empty(WindowsBatteryReportCoverage.CreateIntervals(Report(0, [])));
    }

    [Fact]
    public void CompleteCoveragePreservesSuppliedUnknownAndKnownIntervalsExactly()
    {
        WindowsBatteryReportInterval unknown = Interval(0, 4) with
        {
            PowerSource = WindowsBatteryPowerSource.Unknown,
            Activity = WindowsBatteryActivity.Unknown,
        };
        WindowsBatteryReportInterval known = Interval(4, 10);
        IReadOnlyList<WindowsBatteryReportInterval> actual = WindowsBatteryReportCoverage.CreateIntervals(Report(10, [unknown, known]));
        Assert.Equal(2, actual.Count);
        Assert.Same(unknown, actual[0]);
        Assert.Same(known, actual[1]);
    }

    [Fact]
    public void MaximumSparseReportHasBoundedCoverageWithoutTransportReconstruction()
    {
        WindowsBatteryReportInterval[] input = Enumerable.Range(0, WindowsBatteryReport.MaximumIntervals)
            .Select(index => Interval(index * 2 + 1, index * 2 + 2)).ToArray();
        int end = WindowsBatteryReport.MaximumIntervals * 2 + 1;
        IReadOnlyList<WindowsBatteryReportInterval> actual = WindowsBatteryReportCoverage.CreateIntervals(Report(end, input));

        Assert.Equal(2 * WindowsBatteryReport.MaximumIntervals + 1, actual.Count);
        for (int index = 0; index < input.Length; index++)
        {
            AssertUnknown(actual[index * 2], index * 2, index * 2 + 1);
            Assert.Same(input[index], actual[index * 2 + 1]);
        }
        AssertUnknown(actual[^1], end - 1, end);
    }

    private static WindowsBatteryReport Report(int seconds, IReadOnlyList<WindowsBatteryReportInterval> intervals) =>
        new(WindowsBatteryReportSource.WindowsPowerCfg, Start.AddSeconds(seconds), Start, Start.AddSeconds(seconds),
            new WindowsBatteryStatus(80_000, 70_000, 87.5, 42), intervals);

    private static WindowsBatteryReportInterval Interval(int start, int end) =>
        new(Start.AddSeconds(start), Start.AddSeconds(end), WindowsBatteryPowerSource.Battery,
            WindowsBatteryActivity.Active, 60_000, 70_000, 1_000, 85.7);

    private static void AssertUnknown(WindowsBatteryReportInterval interval, int start, int end)
    {
        Assert.Equal(Start.AddSeconds(start), interval.StartUtc);
        Assert.Equal(Start.AddSeconds(end), interval.EndUtc);
        Assert.Equal(WindowsBatteryPowerSource.Unknown, interval.PowerSource);
        Assert.Equal(WindowsBatteryActivity.Unknown, interval.Activity);
        Assert.Null(interval.StartChargeMilliwattHours);
        Assert.Null(interval.FullChargeCapacityMilliwattHours);
        Assert.Null(interval.DischargeMilliwattHours);
        Assert.Null(interval.StartChargePercent);
    }
}
