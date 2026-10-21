using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private string? telemetryActionError;
    private void RefreshTelemetryPresentation()
    {
        bool replay = ViewModel.DisplayedTelemetrySource == TelemetrySource.LocalCsvReplay;
        DateTimeOffset? end = replay ? ViewModel.DisplayedTelemetrySessionMetadata?.EndedAtUtc
            ?? ViewModel.LatestTelemetrySample?.TimestampUtc : null;
        TimeSpan? range = TelemetryRange.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(5),
            1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromHours(1),
            _ => null,
        };
        DateTimeOffset? start = range is { } duration ? (end ?? DateTimeOffset.UtcNow) - duration : null;
        TelemetryHistorySnapshot snapshot = ViewModel.GetTelemetrySnapshot(start, end);
        TelemetryPlot.Update(snapshot);
        TelemetrySourceText.Text = replay
            ? LocalizedResources.Format("Feature_8f1c7cba79a7", "Local CSV replay · {0}", ViewModel.DisplayedTelemetrySessionMetadata?.OriginPath ?? string.Empty)
            : LocalizedResources.Format("Feature_6c455cda31d0", "Live service · {0}", ViewModel.LatestTelemetryFreshness switch
            {
                TelemetryFreshness.Fresh => LocalizedResources.Get("Feature_f04321b95415", "Fresh"),
                TelemetryFreshness.Stale => LocalizedResources.Get("Feature_94d954185187", "Stale"),
                _ => LocalizedResources.Get("Feature_73e7c4edf7e7", "Waiting for readings"),
            });
        TelemetryRecordButton.IsEnabled = !localizationSnapshotMode && !ViewModel.IsTelemetryRecording && !replay;
        TelemetryStopButton.IsEnabled = ViewModel.IsTelemetryRecording;
        TelemetryLiveButton.IsEnabled = replay;
        TelemetryRecordingText.Text = ViewModel.IsTelemetryRecording
            ? LocalizedResources.Format("Feature_a72204aa94d0", "Recording locally to {0}. Sampling continues while this window is hidden.", ViewModel.TelemetryRecordingPath ?? string.Empty)
            : LocalizedResources.Get("Feature_d6db2da1a792", "Recording is off. Readings stay in memory until you explicitly record a CSV.");
        TelemetryErrorText.Text = telemetryActionError ?? ViewModel.TelemetryErrorMessage;
        TelemetryEventsText.Text = snapshot.Events.Count == 0
            ? LocalizedResources.Get("Feature_c7f01a7da8db", "No power-source or profile changes in this range.")
            : string.Join(Environment.NewLine, snapshot.Events.TakeLast(12).Select(marker =>
                $"{marker.TimestampUtc.ToLocalTime():G} · {(marker.Kind == TelemetryEventKind.PowerSourceChanged ? LocalizedResources.Get("Feature_061b9b48e177", "Power source") : LocalizedResources.Get("Feature_96ad4bd0f7ba", "Profile"))}: {marker.PreviousValue} → {marker.CurrentValue}"));
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
                Title = LocalizedResources.Get("Feature_b5c358cef921", "Record telemetry to CSV"),
            };
            picker.FileTypeChoices.Add(LocalizedResources.Get("Feature_72539d6acd07", "CSV files"), [".csv"]);
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
            var picker = new FileOpenPicker(AppWindow.Id) { Title = LocalizedResources.Get("Feature_e09ca605b555", "Open telemetry CSV") };
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
                ? LocalizedResources.Format("Telemetry_ActionFailed", "Telemetry operation failed: {0}", exception.Message)
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
