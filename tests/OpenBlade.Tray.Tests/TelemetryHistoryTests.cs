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

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(-1_000_000)]
    public void Snapshot_AccumulatesLargeSignedReadingsWithoutOverflow(int reading)
    {
        const int count = 3_000;
        var history = new TelemetryHistory(count);
        for (int index = 0; index < count; index++)
        {
            history.AddSample(Sample(index, battery: reading, cpu: null));
        }

        TelemetryHistorySnapshot snapshot = history.Snapshot(maximumChartPoints: 2);
        TelemetryMetricStatistics battery = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.BatteryPowerFlowMilliwatts).Statistics;
        TelemetryMetricStatistics cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius).Statistics;

        Assert.Equal(count, battery.Count);
        Assert.Equal(0, battery.MissingCount);
        Assert.Equal(reading, battery.Minimum);
        Assert.Equal(reading, battery.Maximum);
        Assert.Equal(reading, battery.Average);
        Assert.Equal(0, cpu.Count);
        Assert.Equal(count, cpu.MissingCount);
        Assert.Null(cpu.Minimum);
        Assert.Null(cpu.Maximum);
        Assert.Null(cpu.Average);
    }

    [Fact]
    public void Downsampling_TooManyGapsRetainsRealReadingsSeparatedByActualGaps()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 20; index++)
        {
            history.AddSample(Sample(index, cpu: index % 2 == 0 ? 50 : null));
        }

        TelemetrySeriesSnapshot cpu = history.Snapshot(maximumChartPoints: 5)
            .Series.Single(value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(new[] { 0, 1, 10, 11, 18 }.Select(second => Start.AddSeconds(second)),
            cpu.Points.Select(point => point.TimestampUtc));
        Assert.Equal(new double?[] { 50, null, 50, null, 50 }, cpu.Points.Select(point => point.Value));
        Assert.Equal(10, cpu.Statistics.Count);
        Assert.Equal(10, cpu.Statistics.MissingCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Downsampling_AlternatingMissingReadingsStaysVisibleWithoutJoiningGaps(int budget)
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 64; index++)
        {
            history.AddSample(Sample(index, cpu: index % 2 == 0 ? null : 50));
        }
        TelemetryHistorySnapshot snapshot = history.Snapshot(maximumChartPoints: budget);
        TelemetrySeriesSnapshot cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.InRange(cpu.Points.Count, 1, budget);
        Assert.Contains(cpu.Points, point => point.Value == 50);
        Assert.Equal(Start.AddSeconds(1), cpu.Points.First(point => point.Value is not null).TimestampUtc);
        if (budget >= 3)
        {
            Assert.Equal(Start.AddSeconds(63), cpu.Points.Last(point => point.Value is not null).TimestampUtc);
        }
        AssertNoInventedContinuity(cpu, snapshot.RawSamples);
        Assert.Equal(cpu.Points, history.Snapshot(maximumChartPoints: budget).Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius).Points);
        Assert.Equal(32, cpu.Statistics.Count);
        Assert.Equal(32, cpu.Statistics.MissingCount);
        Assert.Equal(50, cpu.Statistics.Average);
    }

    [Fact]
    public void Downsampling_GapsExactlyFillBudgetStillShowsTheValidEndpoints()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 5; index++)
        {
            history.AddSample(Sample(index, cpu: index % 2 == 0 ? null : 50 + index));
        }
        TelemetrySeriesSnapshot cpu = history.Snapshot(maximumChartPoints: 3).Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(new[] { 1, 2, 3 }.Select(second => Start.AddSeconds(second)),
            cpu.Points.Select(point => point.TimestampUtc));
        Assert.Equal(new double?[] { 51, null, 53 }, cpu.Points.Select(point => point.Value));
    }

    [Fact]
    public void Downsampling_TwoPointBudgetKeepsBothEndpointsOfOneContinuousRun()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 5; index++)
        {
            history.AddSample(Sample(index, cpu: index is 0 or 4 ? null : 50 + index));
        }
        TelemetryHistorySnapshot snapshot = history.Snapshot(maximumChartPoints: 2);
        TelemetrySeriesSnapshot cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(new[] { 1, 3 }.Select(second => Start.AddSeconds(second)),
            cpu.Points.Select(point => point.TimestampUtc));
        Assert.Equal(new double?[] { 51, 53 }, cpu.Points.Select(point => point.Value));
        AssertNoInventedContinuity(cpu, snapshot.RawSamples);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Downsampling_ValidRunsRemainSeparatedAcrossDiscardedMissingReadings(int budget)
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 100; index++)
        {
            history.AddSample(Sample(index, cpu: index % 50 < 25 ? 50 : null));
        }
        TelemetryHistorySnapshot snapshot = history.Snapshot(maximumChartPoints: budget);
        TelemetrySeriesSnapshot cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.InRange(cpu.Points.Count, 1, budget);
        Assert.Contains(cpu.Points, point => point.Value is not null);
        AssertNoInventedContinuity(cpu, snapshot.RawSamples);
        Assert.Equal(50, cpu.Statistics.Count);
        Assert.Equal(50, cpu.Statistics.MissingCount);
    }

    [Fact]
    public void Downsampling_AllMissingReadingsStayMissing()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 20; index++)
        {
            history.AddSample(Sample(index, cpu: null));
        }
        TelemetrySeriesSnapshot cpu = history.Snapshot(maximumChartPoints: 2).Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.InRange(cpu.Points.Count, 1, 2);
        Assert.All(cpu.Points, point => Assert.Null(point.Value));
        Assert.Equal(0, cpu.Statistics.Count);
        Assert.Equal(20, cpu.Statistics.MissingCount);
        Assert.Null(cpu.Statistics.Average);
    }

    [Fact]
    public void Downsampling_TooManyStoppedObservationGapsRetainsReadingsWithoutBridgingTime()
    {
        var history = new TelemetryHistory();
        for (int index = 0; index < 15; index++)
        {
            history.AddSample(Sample(index * 10, cpu: 50));
        }
        TelemetrySeriesSnapshot cpu = history.Snapshot(
            maximumChartPoints: 5,
            expectedSampleInterval: TimeSpan.FromSeconds(1)).Series.Single(
                value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(new[] { 0, 1, 70, 71, 140 }.Select(second => Start.AddSeconds(second)),
            cpu.Points.Select(point => point.TimestampUtc));
        Assert.Equal(new double?[] { 50, null, 50, null, 50 }, cpu.Points.Select(point => point.Value));
        Assert.Equal(15, cpu.Statistics.Count);
        Assert.Equal(0, cpu.Statistics.MissingCount);
    }

    private static void AssertNoInventedContinuity(
        TelemetrySeriesSnapshot series,
        IReadOnlyList<TelemetrySample> samples)
    {
        for (int index = 1; index < series.Points.Count; index++)
        {
            TelemetryChartPoint previous = series.Points[index - 1];
            TelemetryChartPoint current = series.Points[index];
            Assert.True(previous.TimestampUtc < current.TimestampUtc);
            if (previous.Value is not null && current.Value is not null)
            {
                Assert.All(samples.Where(sample => sample.TimestampUtc >= previous.TimestampUtc
                    && sample.TimestampUtc <= current.TimestampUtc),
                    sample => Assert.NotNull(sample.Value(series.Metric)));
            }
        }
    }

    [Fact]
    public void Snapshot_ReturnedPointsRemainOwnedAfterLaterProjections()
    {
        var history = new TelemetryHistory(capacity: 20);
        for (int index = 0; index < 20; index++)
        {
            history.AddSample(Sample(index, cpu: index is >= 5 and <= 14 ? null : 50));
        }
        TelemetryHistorySnapshot first = history.Snapshot(maximumChartPoints: 4);
        TelemetryChartPoint[][] original = first.Series.Select(series => series.Points.ToArray()).ToArray();

        for (int index = 20; index < 40; index++)
        {
            history.AddSample(Sample(index, battery: -index, cpu: 80));
        }
        _ = history.Snapshot(maximumChartPoints: 3);

        for (int index = 0; index < first.Series.Count; index++)
        {
            Assert.Equal(original[index], first.Series[index].Points);
        }
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
