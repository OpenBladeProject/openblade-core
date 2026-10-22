using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TelemetryChartLayoutTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestedWindowsKeepFiveFifteenAndSixtyMinutesDistinctWithShortData()
    {
        DateTimeOffset end = Start.AddHours(1);
        TelemetryChartTimeWindow fiveMinutes = TelemetryChartTimeWindow.Resolve(
            end,
            TimeSpan.FromMinutes(5),
            Start);
        TelemetryChartTimeWindow fifteenMinutes = TelemetryChartTimeWindow.Resolve(
            end,
            TimeSpan.FromMinutes(15),
            Start);
        TelemetryChartTimeWindow sixtyMinutes = TelemetryChartTimeWindow.Resolve(
            end,
            TimeSpan.FromMinutes(60),
            Start);
        TelemetryChartTimeWindow fullSession = TelemetryChartTimeWindow.Resolve(
            end,
            requestedDuration: null,
            sourceStartUtc: Start.AddMinutes(10));

        Assert.Equal(end.AddMinutes(-5), fiveMinutes.StartUtc);
        Assert.Equal(end.AddMinutes(-15), fifteenMinutes.StartUtc);
        Assert.Equal(end.AddMinutes(-60), sixtyMinutes.StartUtc);
        Assert.Equal(Start.AddMinutes(10), fullSession.StartUtc);
        Assert.All(
            new[] { fiveMinutes, fifteenMinutes, sixtyMinutes, fullSession },
            window => Assert.Equal(end, window.EndUtc));

        DateTimeOffset firstOfShortData = end.AddSeconds(-22);
        double fiveMinutePosition = TelemetryChartScale.Create(
            fiveMinutes.StartUtc, end, 0, 1).TimeFraction(firstOfShortData);
        double fifteenMinutePosition = TelemetryChartScale.Create(
            fifteenMinutes.StartUtc, end, 0, 1).TimeFraction(firstOfShortData);
        double sixtyMinutePosition = TelemetryChartScale.Create(
            sixtyMinutes.StartUtc, end, 0, 1).TimeFraction(firstOfShortData);
        Assert.True(fiveMinutePosition < fifteenMinutePosition);
        Assert.True(fifteenMinutePosition < sixtyMinutePosition);
    }

    [Fact]
    public void HiddenTelemetryPresentationDefersSnapshotUntilShown()
    {
        string telemetrySource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "OpenBlade.Tray",
            "MainWindow.Telemetry.cs"));
        string mainWindowSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs"));

        int visibilityGuard = telemetrySource.IndexOf(
            "&& (!windowVisible || TelemetryPage.Visibility != Visibility.Visible)",
            StringComparison.Ordinal);
        int snapshotRead = telemetrySource.IndexOf(
            "ViewModel.GetTelemetrySnapshot(requestedStart, end)",
            StringComparison.Ordinal);
        Assert.True(visibilityGuard >= 0);
        Assert.True(snapshotRead > visibilityGuard);
        Assert.Contains("telemetryPresentationDirty = true;", telemetrySource, StringComparison.Ordinal);
        int resumeGuard = mainWindowSource.IndexOf(
            "else if (telemetryPresentationDirty)",
            StringComparison.Ordinal);
        Assert.True(resumeGuard >= 0);
        int resumedRefresh = mainWindowSource.IndexOf(
            "RefreshTelemetryPresentation();",
            resumeGuard,
            StringComparison.Ordinal);
        Assert.True(resumedRefresh > resumeGuard);
    }

    [Fact]
    public void ScaleMapsRealTimestampsAndValueRange()
    {
        TelemetryChartScale scale = TelemetryChartScale.Create(
            Start,
            Start.AddMinutes(10),
            40,
            80);

        Assert.Equal(0, scale.TimeFraction(Start));
        Assert.Equal(0.5, scale.TimeFraction(Start.AddMinutes(5)), 6);
        Assert.Equal(1, scale.TimeFraction(Start.AddMinutes(20)));
        Assert.Equal(Start.AddMinutes(2.5), scale.TimestampAtFraction(0.25));
        Assert.Equal(Start, scale.TimestampAtFraction(-1));
        Assert.Equal(Start.AddMinutes(10), scale.TimestampAtFraction(2));
        Assert.Equal(0.5, scale.ValueFractionFromTop(60), 6);

        Assert.Equal(
            [Start, Start.AddMinutes(2.5), Start.AddMinutes(5), Start.AddMinutes(7.5), Start.AddMinutes(10)],
            scale.CreateTimeTicks(5));
        Assert.Equal(5, scale.CreateValueTicks(5).Count);
    }

    [Fact]
    public void ScaleCentersSingleTimestampAndExpandsFlatValues()
    {
        TelemetryChartScale scale = TelemetryChartScale.Create(Start, Start, 55, 55);

        Assert.Equal(0.5, scale.TimeFraction(Start));
        Assert.Equal([Start], scale.CreateTimeTicks(5));
        Assert.True(scale.Minimum < 55);
        Assert.True(scale.Maximum > 55);
        Assert.Equal(0.5, scale.ValueFractionFromTop(55), 6);
    }

    [Fact]
    public void ScaleRejectsReversedRangesAndNonFiniteValues()
    {
        Assert.Throws<ArgumentException>(() => TelemetryChartScale.Create(
            Start,
            Start.AddSeconds(-1),
            0,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryChartScale.Create(
            Start,
            Start,
            double.NaN,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TelemetryChartScale.Create(Start, Start, 2, 1));
    }

    [Fact]
    public void SelectionUsesNearestRealSampleAndPrefersEarlierOnTie()
    {
        TelemetrySample[] samples =
        [
            Sample(0, cpu: 50),
            Sample(10, cpu: null),
            Sample(30, cpu: 70),
        ];

        Assert.Equal(0, TelemetryChartSelection.FindNearestSampleIndex(samples, Start.AddSeconds(5)));
        Assert.Equal(1, TelemetryChartSelection.FindNearestSampleIndex(samples, Start.AddSeconds(6)));
        Assert.Equal(1, TelemetryChartSelection.FindNearestSampleIndex(samples, Start.AddSeconds(10)));
        Assert.Equal(2, TelemetryChartSelection.FindNearestSampleIndex(samples, Start.AddMinutes(1)));
        Assert.Equal(-1, TelemetryChartSelection.FindNearestSampleIndex([], Start));
        Assert.Null(samples[1].Value(TelemetryMetric.CpuTemperatureCelsius));
    }

    [Fact]
    public void SnapshotRetainsBoundedRawSamplesForExactInspectionAfterDownsampling()
    {
        var history = new TelemetryHistory(capacity: 8);
        for (int index = 0; index < 12; index++)
        {
            history.AddSample(Sample(index, cpu: index == 7 ? null : 40 + index));
        }

        TelemetryHistorySnapshot snapshot = history.Snapshot(maximumChartPoints: 3);
        TelemetrySeriesSnapshot cpu = snapshot.Series.Single(
            value => value.Metric == TelemetryMetric.CpuTemperatureCelsius);

        Assert.Equal(8, snapshot.RawSamples.Count);
        Assert.Equal(Start.AddSeconds(4), snapshot.RawSamples[0].TimestampUtc);
        Assert.Equal(Start.AddSeconds(11), snapshot.RawSamples[^1].TimestampUtc);
        Assert.Null(snapshot.RawSamples.Single(
            sample => sample.TimestampUtc == Start.AddSeconds(7)).CpuTemperatureCelsius);
        Assert.Equal(3, cpu.Points.Count);
    }

    [Fact]
    public void ChartUsesCrosshairAndVisibleSampleControlsInsteadOfAStandaloneSlider()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "OpenBlade.Tray",
            "TelemetryChart.cs"));

        Assert.Contains("Line crosshair", source, StringComparison.Ordinal);
        Assert.Contains("plot.PointerMoved += OnPlotPointerMoved", source, StringComparison.Ordinal);
        Assert.Contains("plot.PointerPressed += OnPlotPointerPressed", source, StringComparison.Ordinal);
        Assert.Contains("previousSampleButton.Click", source, StringComparison.Ordinal);
        Assert.Contains("nextSampleButton.Click", source, StringComparison.Ordinal);
        Assert.Contains("followLatestButton.Click", source, StringComparison.Ordinal);
        Assert.Contains("MinHeight = 44", source, StringComparison.Ordinal);
        Assert.Contains(
            "TelemetryChartSelection.FindNearestSampleIndex(rawSamples, target)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("sample.Value(choice.Metric)", source, StringComparison.Ordinal);
        Assert.Contains("scale.CreateTimeTicks(tickCount)", source, StringComparison.Ordinal);
        Assert.Contains("visibleStartUtc ?? snapshot.RangeStartUtc", source, StringComparison.Ordinal);
        string drawMethod = source[source.IndexOf("private void Draw()", StringComparison.Ordinal)..source.IndexOf("private int ResolveSelectedIndex", StringComparison.Ordinal)];
        Assert.DoesNotContain("rawSamples.Count == 0", drawMethod, StringComparison.Ordinal);
        Assert.Contains("ShowLocalizationSnapshotInspection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Slider", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(SourcePath)!,
        "..",
        ".."));

    private static string SourcePath => GetSourcePath();

    private static TelemetrySample Sample(int second, int? cpu) => new(
        Start.AddSeconds(second),
        ServiceRevision: second,
        TelemetrySource.LiveService,
        PowerSource.PluggedIn,
        "AC|base",
        BatteryPowerFlowMilliwatts: 10_000,
        Fan1Rpm: 2_000,
        Fan2Rpm: 2_100,
        CpuTemperatureCelsius: cpu,
        GpuTemperatureCelsius: 45);

    private static string GetSourcePath([CallerFilePath] string path = "") => path;
}
