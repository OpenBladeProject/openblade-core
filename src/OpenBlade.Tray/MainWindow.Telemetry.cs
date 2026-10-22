using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private string? telemetryActionError;
    private bool telemetryPresentationDirty = true;

    private void RefreshTelemetryPresentation(DateTimeOffset? rangeEndOverride = null)
    {
        if (!localizationSnapshotMode
            && (!windowVisible || TelemetryPage.Visibility != Visibility.Visible))
        {
            telemetryPresentationDirty = true;
            return;
        }

        bool replay = ViewModel.DisplayedTelemetrySource == TelemetrySource.LocalCsvReplay;
        TelemetrySessionMetadata? metadata = ViewModel.DisplayedTelemetrySessionMetadata;
        DateTimeOffset end = rangeEndOverride ?? (replay
            ? metadata?.EndedAtUtc
                ?? ViewModel.LatestTelemetrySample?.TimestampUtc
                ?? metadata?.StartedAtUtc
                ?? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow);
        TimeSpan? range = TelemetryRange.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(5),
            1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromHours(1),
            _ => null,
        };
        DateTimeOffset? requestedStart = range is { } duration
            ? end - duration
            : replay ? metadata?.StartedAtUtc : null;
        TelemetryHistorySnapshot snapshot = ViewModel.GetTelemetrySnapshot(requestedStart, end);
        DateTimeOffset? sourceStart = replay ? metadata?.StartedAtUtc : snapshot.RangeStartUtc;
        TelemetryChartTimeWindow visibleWindow = TelemetryChartTimeWindow.Resolve(
            end,
            range,
            sourceStart);
        TelemetryPlot.Update(snapshot, visibleWindow.StartUtc, visibleWindow.EndUtc);
        TelemetrySourceText.Text = replay
            ? LocalizedResources.Format(
                "TelemetryUI_ReplaySource",
                "CSV replay · {0}",
                metadata?.OriginPath ?? string.Empty)
            : LocalizedResources.Format("TelemetryUI_LiveSource", "Live · {0}", ViewModel.LatestTelemetryFreshness switch
            {
                TelemetryFreshness.Fresh => LocalizedResources.Get("TelemetryUI_Fresh", "Fresh"),
                TelemetryFreshness.Stale => LocalizedResources.Get("TelemetryUI_Stale", "Stale"),
                _ => LocalizedResources.Get("TelemetryUI_WaitingForReadings", "Waiting for readings"),
            });
        TelemetryRecordButton.IsEnabled = !localizationSnapshotMode && !ViewModel.IsTelemetryRecording && !replay;
        TelemetryStopButton.IsEnabled = ViewModel.IsTelemetryRecording;
        TelemetryLiveButton.IsEnabled = replay;
        TelemetryRecordingText.Text = ViewModel.IsTelemetryRecording
            ? LocalizedResources.Format(
                "TelemetryUI_RecordingActive",
                "Recording · {0}. Sampling continues while this window is hidden.",
                ViewModel.TelemetryRecordingPath ?? string.Empty)
            : LocalizedResources.Get(
                "TelemetryUI_RecordingOff",
                "Recording off · readings stay in memory until you record a CSV.");
        TelemetryErrorText.Text = telemetryActionError ?? ViewModel.TelemetryErrorMessage;
        TelemetryErrorText.Visibility = string.IsNullOrWhiteSpace(TelemetryErrorText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TelemetryEventsText.Text = snapshot.Events.Count == 0
            ? LocalizedResources.Get(
                "TelemetryUI_NoEvents",
                "No power or profile changes in this range.")
            : string.Join(Environment.NewLine, snapshot.Events.TakeLast(12).Select(marker =>
                $"{marker.TimestampUtc.ToLocalTime():G} · {(marker.Kind == TelemetryEventKind.PowerSourceChanged ? LocalizedResources.Get("TelemetryUI_PowerSource", "Power source") : LocalizedResources.Get("TelemetryUI_Profile", "Profile"))}: {marker.PreviousValue} → {marker.CurrentValue}"));
        telemetryPresentationDirty = false;
    }

    private void OnTelemetryRangeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TelemetryPlot is not null && TelemetrySourceText is not null)
        {
            RefreshTelemetryPresentation();
        }
    }

    private async void OnStartTelemetryRecording(object sender, RoutedEventArgs args) =>
        await RunTelemetryActionAsync(async () =>
        {
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = $"OpenBlade-telemetry-{DateTime.Now:yyyy-MM-dd-HHmmss}",
                DefaultFileExtension = ".csv",
                ShowOverwritePrompt = true,
                Title = LocalizedResources.Get("TelemetryUI_RecordCsvTitle", "Record telemetry to CSV"),
            };
            picker.FileTypeChoices.Add(LocalizedResources.Get("TelemetryUI_CsvFiles", "CSV files"), [".csv"]);
            if (await picker.PickSaveFileAsync() is { } result)
            {
                await ViewModel.StartTelemetryRecordingAsync(result.Path, CancellationToken.None);
                UpdateActiveObservationLoop();
                RefreshTelemetryPresentation();
            }
        }, nameof(OnStartTelemetryRecording));

    private async void OnStopTelemetryRecording(object sender, RoutedEventArgs args) =>
        await RunTelemetryActionAsync(async () =>
        {
            _ = await ViewModel.StopTelemetryRecordingAsync(CancellationToken.None);
            UpdateActiveObservationLoop();
            RefreshTelemetryPresentation();
        }, nameof(OnStopTelemetryRecording));

    private async void OnOpenTelemetryReplay(object sender, RoutedEventArgs args) =>
        await RunTelemetryActionAsync(async () =>
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                Title = LocalizedResources.Get("TelemetryUI_OpenCsvTitle", "Open telemetry CSV"),
            };
            picker.FileTypeFilter.Add(".csv");
            if (await picker.PickSingleFileAsync() is { } result)
            {
                await ViewModel.OpenTelemetryReplayAsync(result.Path, CancellationToken.None);
                RefreshTelemetryPresentation();
            }
        }, nameof(OnOpenTelemetryReplay));

    private void OnCloseTelemetryReplay(object sender, RoutedEventArgs args)
    {
        ViewModel.CloseTelemetryReplay();
        RefreshTelemetryPresentation();
    }

    private async Task RunTelemetryActionAsync(Func<Task> action, string label)
    {
        telemetryActionError = null;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade {0} failed: {1}", label, exception);
            telemetryActionError = string.IsNullOrWhiteSpace(ViewModel.TelemetryErrorMessage)
                ? LocalizedResources.Format("TelemetryUI_ActionFailed", "Telemetry operation failed: {0}", exception.Message)
                : ViewModel.TelemetryErrorMessage;
        }
        finally
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                RefreshTelemetryPresentation();
            }
        }
    }
}
