using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryHistoryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddSample_BoundsHistoryAndEmitsOnlyActualTransitions()
    {
        var history = new TelemetryHistory(capacity: 2);

        Assert.Empty(history.AddSample(Sample(0, power: PowerSource.PluggedIn, profile: "AC")));
        Assert.Empty(history.AddSample(Sample(1, power: PowerSource.PluggedIn, profile: "AC")));
        IReadOnlyList<TelemetryEvent> markers = history.AddSample(
            Sample(2, power: PowerSource.Battery, profile: "Battery saver"));

        Assert.Equal(2, history.Count);
        Assert.Collection(
            markers,
            marker => Assert.Equal(TelemetryEventKind.PowerSourceChanged, marker.Kind),
            marker => Assert.Equal(TelemetryEventKind.ProfileChanged, marker.Kind));
        TelemetryHistorySnapshot snapshot = history.Snapshot();
        Assert.Equal(2, snapshot.RawSampleCount);
        Assert.Equal(Start.AddSeconds(1), snapshot.RangeStartUtc);
        Assert.Equal(2, snapshot.Events.Count);
    }

    [Fact]
    public void Snapshot_ComputesRawStatisticsForRangeAndDoesNotTurnMissingIntoZero()
    {
        var history = new TelemetryHistory();
        history.AddSample(Sample(0, battery: null, cpu: null));
        history.AddSample(Sample(1, battery: -20_000, cpu: 50));
        history.AddSample(Sample(2, battery: 10_000, cpu: 70));

        TelemetryHistorySnapshot snapshot = history.Snapshot(
            Start.AddSeconds(1),
            Start.AddSeconds(2));
        TelemetryMetricStatistics battery = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.BatteryPowerFlowMilliwatts).Statistics;
        TelemetryMetricStatistics cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius).Statistics;

        Assert.Equal(2, snapshot.RawSampleCount);
        Assert.Equal(2, battery.Count);
        Assert.Equal(-20_000, battery.Minimum);
        Assert.Equal(10_000, battery.Maximum);
        Assert.Equal(-5_000, battery.Average);
        Assert.Equal(60, cpu.Average);
        Assert.All(
            snapshot.Series.Single(value => value.Metric == TelemetryMetric.Fan1Rpm).Points,
            point => Assert.Null(point.Value));
    }

    [Fact]
    public void Downsampling_RemainsBoundedAndRetainsMissingGap()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 40; index++)
        {
            history.AddSample(Sample(index, cpu: index is >= 15 and <= 19 ? null : 40 + index));
        }

        TelemetrySeriesSnapshot cpu = history.Snapshot(maximumChartPoints: 10)
            .Series.Single(value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(10, cpu.Points.Count);
        Assert.Contains(cpu.Points, point => point.Value is null);
        Assert.Equal(35, cpu.Statistics.Count);
        Assert.Equal(40, cpu.Statistics.Minimum);
        Assert.Equal(79, cpu.Statistics.Maximum);
    }

    [Fact]
    public void Snapshot_InsertsMissingPointAcrossStoppedObservationGap()
    {
        var history = new TelemetryHistory();
        history.AddSample(Sample(0, cpu: 50));
        history.AddSample(Sample(10, cpu: 60));

        TelemetryHistorySnapshot snapshot = history.Snapshot(
            expectedSampleInterval: TimeSpan.FromSeconds(2));
        TelemetrySeriesSnapshot cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(TimeSpan.FromSeconds(4), snapshot.MaximumContinuousGap);
        Assert.Collection(
            cpu.Points,
            point => Assert.Equal(50, point.Value),
            point => Assert.Null(point.Value),
            point => Assert.Equal(60, point.Value));
        Assert.Equal(2, cpu.Statistics.Count);
    }

    [Fact]
    public void SampleFreshness_DistinguishesLiveStaleAndReplay()
    {
        TelemetrySample live = Sample(0);
        TelemetrySample replay = live with
        {
            Source = TelemetrySource.LocalCsvReplay,
            RecordedSource = TelemetrySource.LiveService,
        };

        Assert.Equal(
            TelemetryFreshness.Fresh,
            live.GetFreshness(Start.AddSeconds(4), TimeSpan.FromSeconds(2)));
        Assert.Equal(
            TelemetryFreshness.Stale,
            live.GetFreshness(Start.AddSeconds(5), TimeSpan.FromSeconds(2)));
        Assert.Equal(
            TelemetryFreshness.Replay,
            replay.GetFreshness(Start.AddYears(1), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FullRecordingCapacity_CanBeLoadedWithoutQuadraticLastSampleLookup()
    {
        const int maximumRecordingRows = 86_400;
        Task<TelemetryHistory> load = Task.Run(
            () =>
            {
                var history = new TelemetryHistory(maximumRecordingRows);
                for (int index = 0; index < maximumRecordingRows; index++)
                {
                    history.AddSample(Sample(index));
                }
                return history;
            });

        TelemetryHistory history = await load.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(maximumRecordingRows, history.Count);
    }

    private static TelemetrySample Sample(
        int second,
        PowerSource power = PowerSource.PluggedIn,
        string profile = "AC",
        int? battery = 12_000,
        int? cpu = 55) => new(
            Start.AddSeconds(second),
            second,
            TelemetrySource.LiveService,
            power,
            profile,
            battery,
            null,
            null,
            cpu,
            null);
}
