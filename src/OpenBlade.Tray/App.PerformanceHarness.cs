#if OPENBLADE_DEVELOPER_HARNESS
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public partial class App
{
    // Deliberately uses the disconnected snapshot window. It never initializes
    // the product launch path, service subscriptions, or hardware observation.
    private async Task RunPerformanceAuditAsync(string reportPath)
    {
        MainWindow? window = null;
        Exception? failure = null;
        object? report = null;
        string? resolvedReportPath = null;
        try
        {
            resolvedReportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(resolvedReportPath)!);
            System.Windows.Forms.Application.EnableVisualStyles();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            window = MainWindow.CreateLocalizationSnapshot();
            double constructionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long constructionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
            ShowNoninteractiveSnapshotWindow(window);
            await WaitForSnapshotLayoutAsync();
            await window.PreparePerformanceTelemetryAsync();
            await WaitForSnapshotLayoutAsync();

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan idleCpuBefore = process.TotalProcessorTime;
            long idleStarted = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(3));
            process.Refresh();
            double idleSeconds = Stopwatch.GetElapsedTime(idleStarted).TotalSeconds;
            double idleCpuMilliseconds = (process.TotalProcessorTime - idleCpuBefore).TotalMilliseconds;
            long privateBytes = process.PrivateMemorySize64;
            long workingSetBytes = process.WorkingSet64;

            IReadOnlyList<TelemetryNotificationMeasurement> measurements =
                window.MeasureTelemetryNotifications();
            foreach (TelemetryNotificationMeasurement measurement in measurements)
            {
                int expected = measurement.Scenario == "freshness" ? 0 : measurement.Iterations;
                if (measurement.ChartRebuilds != expected)
                {
                    throw new InvalidOperationException(
                        $"{measurement.Scenario}: expected {expected} chart rebuilds, got {measurement.ChartRebuilds}.");
                }
            }
            window.VerifyTelemetryObservationPresentation();
            await SaveSnapshotAsync(window.Content, Path.ChangeExtension(resolvedReportPath, ".png"));
            window.PreparePerformanceGapChart();
            await WaitForSnapshotLayoutAsync();
            await SaveSnapshotAsync(window.Content, Path.ChangeExtension(resolvedReportPath, ".gaps.png"));
            await window.DisposeAsync();
            window.VerifyDisposedTelemetryObservationPresentation();
            report = new
            {
                schemaVersion = 1,
                productVersion = OpenBladeProductVersion.Read(typeof(App).Assembly),
                disconnected = true,
                sampleCount = TelemetryHistory.DefaultCapacity,
                constructionMilliseconds,
                constructionAllocatedBytes,
                idleSeconds,
                idleCpuMilliseconds,
                privateBytes,
                workingSetBytes,
                measurements,
                observationChecksPassed = true,
                gapChartChecksPassed = true,
                notes = "Synthetic live telemetry on a disconnected window. Constructor timing excludes layout stabilization. Notification batches exercise the production handler directly. Timing is diagnostic, not a pass threshold.",
            };
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (window is not null)
            {
                try
                {
                    await window.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }

        try
        {
            if (resolvedReportPath is null)
            {
                Environment.Exit(1);
                return;
            }
            if (failure is null)
            {
                await File.WriteAllTextAsync(resolvedReportPath, JsonSerializer.Serialize(
                    report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            }
            else
            {
                await File.WriteAllTextAsync(resolvedReportPath + ".error.txt", failure.ToString());
            }
        }
        catch
        {
            Environment.Exit(1);
            return;
        }
        // Closing the last XAML window can end the dispatcher. Finish every
        // asynchronous artifact write first; there must be no await after Close.
        window?.Close();
        Environment.Exit(failure is null ? 0 : 1);
    }
}

public sealed partial class MainWindow
{
    internal async Task PreparePerformanceTelemetryAsync()
    {
        if (!localizationSnapshotMode)
        {
            throw new InvalidOperationException("Performance fixtures require disconnected snapshot mode.");
        }
        await ViewModel.SetTelemetryEnabledAsync(true);
        SelectLocalizationSnapshotPage("telemetry");

        // Reuse the snapshot harness's explicit fixture seam; production has no
        // data-injection API and this entire file is absent from product builds.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo historyField = typeof(MainViewModel).GetField("telemetryHistory", flags)
            ?? throw new InvalidOperationException("Telemetry history fixture hook was not found.");
        FieldInfo latestField = typeof(MainViewModel).GetField("latestLiveTelemetrySample", flags)
            ?? throw new InvalidOperationException("Latest telemetry fixture hook was not found.");
        var history = (TelemetryHistory)(historyField.GetValue(ViewModel)
            ?? throw new InvalidOperationException("Telemetry history fixture was unavailable."));
        DateTimeOffset end = DateTimeOffset.UtcNow;
        TelemetrySample? latest = null;
        for (int index = 0; index < TelemetryHistory.DefaultCapacity; index++)
        {
            latest = new TelemetrySample(
                end.AddSeconds(index - TelemetryHistory.DefaultCapacity + 1),
                index + 1,
                TelemetrySource.LiveService,
                PowerSource.PluggedIn,
                "Performance fixture",
                -12_000 - index % 5_000,
                2_000 + index % 800,
                1_900 + index % 700,
                index % 97 == 0 ? null : 55 + index % 20,
                50 + index % 15);
            history.AddSample(latest);
        }
        latestField.SetValue(ViewModel, latest);
        RefreshTelemetryPresentation(end);
    }

    internal IReadOnlyList<TelemetryNotificationMeasurement> MeasureTelemetryNotifications()
    {
        if (!localizationSnapshotMode || ViewModel.TelemetryReplaySession is not null)
        {
            throw new InvalidOperationException("Notification measurements require disconnected live fixtures.");
        }
        return
        [
            Measure("freshness", () => Notify(nameof(MainViewModel.LatestTelemetryFreshness))),
            Measure("history", () => Notify(nameof(MainViewModel.TelemetryHistorySnapshot))),
            Measure("accepted-sample-notifications", () =>
            {
                Notify(nameof(MainViewModel.LatestTelemetryFreshness));
                Notify(nameof(MainViewModel.TelemetryHistorySnapshot));
                Notify(nameof(MainViewModel.LatestTelemetryFreshness));
            }),
        ];

        void Notify(string propertyName) =>
            OnViewModelPropertyChanged(ViewModel, new PropertyChangedEventArgs(propertyName));

        TelemetryNotificationMeasurement Measure(string scenario, Action notificationBatch)
        {
            const int iterations = 30;
            for (int index = 0; index < 3; index++)
            {
                notificationBatch();
            }
            int rebuilds = 0;
            void OnVisibleRangeChanged(object? sender, EventArgs args) => rebuilds++;
            TelemetryPlot.VisibleRangeChanged += OnVisibleRangeChanged;
            try
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int index = 0; index < iterations; index++)
                {
                    notificationBatch();
                }
                double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                return new TelemetryNotificationMeasurement(
                    scenario, iterations, rebuilds,
                    elapsedMilliseconds / iterations, allocatedBytes / iterations);
            }
            finally
            {
                TelemetryPlot.VisibleRangeChanged -= OnVisibleRangeChanged;
            }
        }
    }

    internal void VerifyTelemetryObservationPresentation()
    {
        TelemetrySample latest = ViewModel.LatestTelemetrySample
            ?? throw new InvalidOperationException("A live sample is required.");
        VerifyChartRebuilds("no-sample completion", 1,
            () => CompleteTelemetryObservationPresentation(latest, null));
        VerifyChartRebuilds("newer-sample completion", 0,
            () => CompleteTelemetryObservationPresentation(latest with { ServiceRevision = latest.ServiceRevision - 1 }, null));

        var replay = new TelemetrySessionDocument(
            new TelemetrySessionMetadata(Guid.NewGuid(), latest.TimestampUtc, latest.TimestampUtc,
                TelemetrySource.LocalCsvReplay), [], []);
        VerifyChartRebuilds("observation began in replay", 0,
            () => CompleteTelemetryObservationPresentation(latest, replay));
        FieldInfo replayField = typeof(MainViewModel).GetField(
            "telemetryReplaySession", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Replay fixture hook was not found.");
        replayField.SetValue(ViewModel, replay);
        try
        {
            VerifyChartRebuilds("replay selected during observation", 0,
                () => CompleteTelemetryObservationPresentation(latest, null));
        }
        finally
        {
            replayField.SetValue(ViewModel, null);
        }
    }

    internal void VerifyDisposedTelemetryObservationPresentation() =>
        VerifyChartRebuilds("disposed completion", 0,
            () => CompleteTelemetryObservationPresentation(ViewModel.LatestTelemetrySample, null));

    internal void PreparePerformanceGapChart()
    {
        TelemetrySample fixture = ViewModel.LatestTelemetrySample
            ?? throw new InvalidOperationException("The live fixture is unavailable.");
        var history = new TelemetryHistory();
        DateTimeOffset end = DateTimeOffset.UtcNow;
        for (int index = 0; index < TelemetryHistory.DefaultCapacity; index++)
        {
            history.AddSample(fixture with
            {
                TimestampUtc = end.AddSeconds(index - TelemetryHistory.DefaultCapacity + 1),
                CpuTemperatureCelsius = index % 2 == 0 ? null : 55 + index % 20,
                GpuTemperatureCelsius = index % 2 == 0 ? 50 + index % 15 : null,
            });
        }
        TelemetryHistorySnapshot snapshot = history.Snapshot();
        foreach (TelemetrySeriesSnapshot series in snapshot.Series.Where(series =>
            series.Metric is TelemetryMetric.CpuTemperatureCelsius or TelemetryMetric.GpuTemperatureCelsius))
        {
            if (!series.Points.Any(point => point.Value is not null)
                || !series.Points.Any(point => point.Value is null))
            {
                throw new InvalidOperationException("A sparse metric lost its readings or its gaps.");
            }
        }
        TelemetryPlot.Update(snapshot, snapshot.RangeStartUtc!.Value, end,
            ViewModel.DisplayedTelemetrySourceSessionId);
    }

    private void VerifyChartRebuilds(string scenario, int expected, Action action)
    {
        int rebuilds = 0;
        void OnVisibleRangeChanged(object? sender, EventArgs args) => rebuilds++;
        TelemetryPlot.VisibleRangeChanged += OnVisibleRangeChanged;
        try
        {
            action();
            if (rebuilds != expected)
            {
                throw new InvalidOperationException(
                    $"{scenario}: expected {expected} chart rebuilds, got {rebuilds}.");
            }
        }
        finally
        {
            TelemetryPlot.VisibleRangeChanged -= OnVisibleRangeChanged;
        }
    }
}

internal sealed record TelemetryNotificationMeasurement(
    string Scenario,
    int Iterations,
    int ChartRebuilds,
    double MillisecondsPerBatch,
    long AllocatedBytesPerBatch);
#endif
