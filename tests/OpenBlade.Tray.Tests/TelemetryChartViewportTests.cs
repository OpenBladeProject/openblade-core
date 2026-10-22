using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryChartViewportTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildSnapshot_RecomputesExactVisibleStatisticsFromRawSamples()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 20; index++)
        {
            history.AddSample(Sample(
                index,
                battery: index is 6 or 8 ? null : -30_000 + (index * 1_000),
                cpu: index == 7 ? null : 40 + index));
        }
        TelemetryHistorySnapshot retained = history.Snapshot(maximumChartPoints: 2);

        TelemetryHistorySnapshot visible = TelemetryChartViewport.BuildSnapshot(
            retained,
            Start.AddSeconds(5),
            Start.AddSeconds(10),
            maximumChartPoints: 3);

        TelemetrySeriesSnapshot battery = visible.Series.Single(
            value => value.Metric == TelemetryMetric.BatteryPowerFlowMilliwatts);
        TelemetrySeriesSnapshot cpu = visible.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);
        Assert.Equal(6, visible.RawSampleCount);
        Assert.Equal(3, battery.Points.Count);
        Assert.Equal(4, battery.Statistics.Count);
        Assert.Equal(2, battery.Statistics.MissingCount);
        Assert.Equal(-25_000, battery.Statistics.Minimum);
        Assert.Equal(-20_000, battery.Statistics.Maximum);
        Assert.Equal(-22_250, battery.Statistics.Average);
        Assert.Equal(5, cpu.Statistics.Count);
        Assert.Equal(1, cpu.Statistics.MissingCount);
        Assert.Equal(45, cpu.Statistics.Minimum);
        Assert.Equal(50, cpu.Statistics.Maximum);
    }

    [Fact]
    public void BuildSnapshot_PreservesStoppedObservationGapAndFiltersEvents()
    {
        var history = new TelemetryHistory();
        history.AddSample(Sample(0, power: PowerSource.Battery, profile: "Battery"));
        history.AddSample(Sample(1, power: PowerSource.Battery, profile: "Battery"));
        history.AddSample(Sample(10, power: PowerSource.PluggedIn, profile: "AC"));
        history.AddSample(Sample(11, power: PowerSource.PluggedIn, profile: "AC"));
        TelemetryHistorySnapshot retained = history.Snapshot(
            expectedSampleInterval: TimeSpan.FromSeconds(1));

        TelemetryHistorySnapshot visible = TelemetryChartViewport.BuildSnapshot(
            retained,
            Start.AddSeconds(1),
            Start.AddSeconds(10),
            maximumChartPoints: 8);

        TelemetrySeriesSnapshot battery = visible.Series.Single(
            value => value.Metric == TelemetryMetric.BatteryPowerFlowMilliwatts);
        Assert.Collection(
            battery.Points,
            point => Assert.Equal(Start.AddSeconds(1), point.TimestampUtc),
            point =>
            {
                Assert.Equal(Start.AddSeconds(2), point.TimestampUtc);
                Assert.Null(point.Value);
            },
            point => Assert.Equal(Start.AddSeconds(10), point.TimestampUtc));
        Assert.Equal(2, battery.Statistics.Count);
        Assert.Equal(0, battery.Statistics.MissingCount);
        Assert.Equal(2, visible.Events.Count);
    }

    [Fact]
    public void BuildSnapshot_HandlesEmptyWindowAndRejectsIncompleteRawInput()
    {
        var history = new TelemetryHistory();
        history.AddSample(Sample(0));
        TelemetryHistorySnapshot retained = history.Snapshot();

        TelemetryHistorySnapshot empty = TelemetryChartViewport.BuildSnapshot(
            retained,
            Start.AddMinutes(1),
            Start.AddMinutes(2));

        Assert.Equal(0, empty.RawSampleCount);
        Assert.Null(empty.RangeStartUtc);
        Assert.Null(empty.RangeEndUtc);
        Assert.All(empty.Series, series =>
        {
            Assert.Empty(series.Points);
            Assert.Equal(0, series.Statistics.Count);
            Assert.Equal(0, series.Statistics.MissingCount);
        });

        TelemetryHistorySnapshot incomplete = retained with { RawSamples = [] };
        Assert.Throws<InvalidOperationException>(() =>
            TelemetryChartViewport.BuildSnapshot(incomplete, Start, Start));
    }

    [Fact]
    public void BuildSnapshot_RejectsInvalidWindow()
    {
        TelemetryHistorySnapshot empty = new(
            null,
            null,
            0,
            [],
            []);

        Assert.Throws<ArgumentException>(() => TelemetryChartViewport.BuildSnapshot(
            empty,
            Start.AddSeconds(1),
            Start));
        Assert.Throws<InvalidDataException>(() => TelemetryChartViewport.BuildSnapshot(
            empty,
            Start.ToOffset(TimeSpan.FromHours(1)),
            Start.AddSeconds(1)));
    }

    private static TelemetrySample Sample(
        int second,
        PowerSource power = PowerSource.PluggedIn,
        string profile = "AC",
        int? battery = -12_000,
        int? cpu = 55) => new(
            Start.AddSeconds(second),
            second,
            TelemetrySource.LiveService,
            power,
            profile,
            battery,
            2_000,
            2_100,
            cpu,
            45);
}
