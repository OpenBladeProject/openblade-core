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
            await window.VerifyTelemetryObservationPresentationAsync();
            await window.VerifyTelemetryReplayActionsAsync(resolvedReportPath + ".replay.csv");
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
                replayActionChecksPassed = true,
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
                CompleteTelemetryObservationPresentation(
                    ViewModel.LatestTelemetrySample! with { ServiceRevision = -1 }, null);
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

    internal async Task VerifyTelemetryObservationPresentationAsync()
    {
        bool wasActive = ViewModel.IsTelemetryPresentationActive;
        bool wasEnabled = ViewModel.TelemetryEnabled;
        FieldInfo replayField = typeof(MainViewModel).GetField(
            "telemetryReplaySession", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Replay fixture hook was not found.");
        ApplySnapshotStatus(ProfileDefaults.CreateSafeDefaults(), null);
        FieldInfo statusField = typeof(MainViewModel).GetField(
            "latestStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Status fixture hook was not found.");
        var status = (ServiceStatusSnapshot)(statusField.GetValue(ViewModel)
            ?? throw new InvalidOperationException("The disconnected status fixture is unavailable."));
        MethodInfo capture = typeof(MainViewModel).GetMethod(
            "CaptureTelemetryObservation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Telemetry capture fixture hook was not found.");
        // Snapshot mode has no live subscriptions. Forward presentation only;
        // changing demand here must never start an observation loop.
        void OnDisplayedPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(MainViewModel.RequiresTelemetryObservation)
                or nameof(MainViewModel.RequiresExternalPowerDischargeObservation)))
            {
                OnViewModelPropertyChanged(sender, args);
            }
        }
        ViewModel.PropertyChanged += OnDisplayedPropertyChanged;
        try
        {
            ViewModel.SetTelemetryPresentationActive(true);
            TelemetrySample beforeCapture = ViewModel.LatestTelemetrySample
                ?? throw new InvalidOperationException("A live sample is required.");
            VerifyChartRebuilds("accepted capture and completion", 1, () =>
            {
                if (capture.Invoke(ViewModel, [status]) is null
                    || ReferenceEquals(beforeCapture, ViewModel.LatestTelemetrySample))
                {
                    throw new InvalidOperationException("The disconnected observation was not captured.");
                }
                CompleteTelemetryObservationPresentation(beforeCapture, null);
            }, expectedFreshness: 1);
            VerifyChartRebuilds("newer-sample completion", 0,
                () => CompleteTelemetryObservationPresentation(beforeCapture, null), expectedFreshness: 0);
            TelemetrySample latest = ViewModel.LatestTelemetrySample!;
            VerifyChartRebuilds("no-sample completion", 1,
                () => CompleteTelemetryObservationPresentation(latest, null), expectedFreshness: 1);

            ViewModel.SetTelemetryPresentationActive(false);
            VerifyChartRebuilds("inactive completion", 0,
                () => CompleteTelemetryObservationPresentation(latest, null), expectedFreshness: 0);
            ViewModel.SetTelemetryPresentationActive(true);
            await ViewModel.SetTelemetryEnabledAsync(false);
            VerifyChartRebuilds("disabled completion", 0,
                () => CompleteTelemetryObservationPresentation(latest, null), expectedFreshness: 0);
            await ViewModel.SetTelemetryEnabledAsync(true);

            var replay = new TelemetrySessionDocument(
                new TelemetrySessionMetadata(Guid.NewGuid(), latest.TimestampUtc, latest.TimestampUtc,
                    TelemetrySource.LocalCsvReplay), [], []);
            VerifyChartRebuilds("observation began in replay", 0,
                () => CompleteTelemetryObservationPresentation(latest, replay), expectedFreshness: 0);
            replayField.SetValue(ViewModel, replay);
            VerifyChartRebuilds("replay selected during observation", 0,
                () => CompleteTelemetryObservationPresentation(latest, null), expectedFreshness: 0);
        }
        finally
        {
            ViewModel.PropertyChanged -= OnDisplayedPropertyChanged;
            replayField.SetValue(ViewModel, null);
            ViewModel.SetTelemetryPresentationActive(wasActive);
            await ViewModel.SetTelemetryEnabledAsync(wasEnabled);
        }
    }

    internal void VerifyDisposedTelemetryObservationPresentation() =>
        VerifyChartRebuilds("disposed completion", 0,
            () => CompleteTelemetryObservationPresentation(ViewModel.LatestTelemetrySample, null), expectedFreshness: 0);

    internal async Task VerifyTelemetryReplayActionsAsync(string path)
    {
        TelemetryHistorySnapshot snapshot = ViewModel.GetTelemetrySnapshot();
        await TelemetryCsv.WriteAsync(path, new TelemetrySessionDocument(
            TelemetrySessionMetadata.CreateLive(snapshot.RangeStartUtc!.Value) with
            {
                EndedAtUtc = snapshot.RangeEndUtc,
            }, snapshot.RawSamples, snapshot.Events), cancellationToken: CancellationToken.None);
        // Snapshot windows skip InitializeCoreAsync and its event subscription.
        // Exercise the real presentation handler without starting observation.
        void OnDisplayedPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(MainViewModel.RequiresTelemetryObservation)
                or nameof(MainViewModel.RequiresExternalPowerDischargeObservation)))
            {
                OnViewModelPropertyChanged(sender, args);
            }
        }
        ViewModel.PropertyChanged += OnDisplayedPropertyChanged;
        try
        {
            await Check("open replay action", 1, () => RunTelemetryActionAsync(
                () => ViewModel.OpenTelemetryReplayAsync(path, CancellationToken.None), "test-open"));
            if (ViewModel.TelemetryReplaySession is null || !TelemetryLiveButton.IsEnabled)
            {
                throw new InvalidOperationException("Replay source and commands were not updated.");
            }
            TelemetrySessionDocument replay = ViewModel.TelemetryReplaySession;
            await Check("failed replay action", 0, () => RunTelemetryActionAsync(
                () => ViewModel.OpenTelemetryReplayAsync(path + ".missing", CancellationToken.None), "test-failure"));
            if (!ReferenceEquals(replay, ViewModel.TelemetryReplaySession)
                || !TelemetryLiveButton.IsEnabled || string.IsNullOrWhiteSpace(TelemetryErrorText.Text))
            {
                throw new InvalidOperationException("Failed replay replaced the source or lost command feedback.");
            }
            VerifyChartRebuilds("close replay action", 1,
                () => OnCloseTelemetryReplay(this, new Microsoft.UI.Xaml.RoutedEventArgs()));
            FieldInfo readerField = typeof(MainViewModel).GetField(
                "telemetryReplayReader", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Replay reader fixture hook was not found.");
            object? originalReader = readerField.GetValue(ViewModel);
            var pendingRead = new TaskCompletionSource<TelemetrySessionDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            readerField.SetValue(ViewModel, new Func<string, TimeProvider, CancellationToken, Task<TelemetrySessionDocument>>(
                (_, _, _) => pendingRead.Task));
            try
            {
                await Check("superseded replay action", 0, () => RunTelemetryActionAsync(async () =>
                {
                    Task opening = ViewModel.OpenTelemetryReplayAsync(path, CancellationToken.None);
                    ViewModel.CloseTelemetryReplay();
                    pendingRead.SetResult(replay);
                    await opening;
                }, "test-superseded"));
            }
            finally { readerField.SetValue(ViewModel, originalReader); }
            if (ViewModel.TelemetryReplaySession is not null || TelemetryLiveButton.IsEnabled
                || !string.IsNullOrWhiteSpace(TelemetryErrorText.Text))
            {
                throw new InvalidOperationException("Superseded replay changed the live source or command feedback.");
            }
        }
        finally
        {
            ViewModel.PropertyChanged -= OnDisplayedPropertyChanged;
            ViewModel.CloseTelemetryReplay();
            File.Delete(path);
        }

        async Task Check(string scenario, int expected, Func<Task> action)
        {
            int rebuilds = 0;
            void Changed(object? sender, EventArgs args) => rebuilds++;
            TelemetryPlot.VisibleRangeChanged += Changed;
            try
            {
                await action();
                if (rebuilds != expected)
                {
                    throw new InvalidOperationException($"{scenario}: expected {expected} rebuilds, got {rebuilds}.");
                }
            }
            finally { TelemetryPlot.VisibleRangeChanged -= Changed; }
        }
    }

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

    private void VerifyChartRebuilds(string scenario, int expected, Action action, int? expectedFreshness = null)
    {
        int rebuilds = 0;
        int freshness = 0;
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.LatestTelemetryFreshness)) { freshness++; }
        }
        void OnVisibleRangeChanged(object? sender, EventArgs args) => rebuilds++;
        TelemetryPlot.VisibleRangeChanged += OnVisibleRangeChanged;
        ViewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            action();
            if (expectedFreshness is { } count && freshness != count)
            {
                throw new InvalidOperationException($"{scenario}: expected {count} freshness notifications, got {freshness}.");
            }
            if (rebuilds != expected)
            {
                throw new InvalidOperationException(
                    $"{scenario}: expected {expected} chart rebuilds, got {rebuilds}.");
            }
        }
        finally
        {
            TelemetryPlot.VisibleRangeChanged -= OnVisibleRangeChanged;
            ViewModel.PropertyChanged -= OnPropertyChanged;
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
