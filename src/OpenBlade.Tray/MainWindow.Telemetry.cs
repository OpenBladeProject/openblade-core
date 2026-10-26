using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using Button = Microsoft.UI.Xaml.Controls.Button;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private string? telemetryActionError;
    private bool telemetryPresentationDirty = true;
    private bool telemetryToggleSynchronizing;
    private bool telemetryToggleInProgress;

    private async void OnTelemetryEnabledToggled(object sender, RoutedEventArgs args)
    {
        if (telemetryToggleSynchronizing || TelemetryEnabledToggle.IsOn == ViewModel.TelemetryEnabled)
        {
            return;
        }
        telemetryToggleInProgress = true;
        TelemetryEnabledToggle.IsEnabled = false;
        try
        {
            await RunTelemetryActionAsync(
                () => ViewModel.SetTelemetryEnabledAsync(TelemetryEnabledToggle.IsOn),
                nameof(OnTelemetryEnabledToggled));
        }
        finally
        {
            telemetryToggleInProgress = false;
            if (Volatile.Read(ref disposed) == 0)
            {
                TelemetryEnabledToggle.IsEnabled = true;
                UpdateDashboardTelemetryPresentationState();
                UpdateActiveObservationLoop();
                RefreshTelemetryPresentation();
            }
        }
    }

    private void RefreshTelemetryPresentation(DateTimeOffset? rangeEndOverride = null)
    {
        if (!localizationSnapshotMode
            && (!windowVisible || TelemetryPage.Visibility != Visibility.Visible))
        {
            telemetryPresentationDirty = true;
            return;
        }

        bool enabled = ViewModel.TelemetryEnabled;
        telemetryToggleSynchronizing = true;
        try { TelemetryEnabledToggle.IsOn = enabled; }
        finally { telemetryToggleSynchronizing = false; }
        TelemetryContent.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TelemetryDisabledText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        TelemetryErrorText.Text = telemetryActionError ?? ViewModel.TelemetryErrorMessage;
        TelemetryErrorText.Visibility = string.IsNullOrWhiteSpace(TelemetryErrorText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled)
        {
            telemetryPresentationDirty = false;
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
        // The chart owns the visible window; the overview needs the complete bounded history.
        TelemetryHistorySnapshot snapshot = ViewModel.GetTelemetrySnapshot(rangeEndUtc: end);
        DateTimeOffset? sourceStart = replay ? metadata?.StartedAtUtc : snapshot.RangeStartUtc;
        TelemetryPlot.Update(snapshot, sourceStart ?? end, end, ViewModel.DisplayedTelemetrySourceSessionId);
        TelemetrySourceText.Text = replay
            ? LocalizedResources.Format(
                "TelemetryUI_ReplaySource",
                "CSV replay · {0}",
                localizationSnapshotMode ? @"C:\Telemetry\Example.csv" : metadata?.OriginPath ?? string.Empty)
            : LocalizedResources.Format("TelemetryUI_LiveSource", "Live · {0}", ViewModel.LatestTelemetryFreshness switch
            {
                TelemetryFreshness.Fresh => LocalizedResources.Get("TelemetryUI_Fresh", "Fresh"),
                TelemetryFreshness.Stale => LocalizedResources.Get("TelemetryUI_Stale", "Stale"),
                _ => LocalizedResources.Get("TelemetryUI_WaitingForReadings", "Waiting for readings"),
            });
        bool recording = ViewModel.IsTelemetryRecording;
        SetTelemetryCommandPresentation(
            TelemetryRecordButton,
            TelemetryRecordLabel,
            LocalizedResources.Get("Telemetry_Record.Content", "Record CSV"));
        SetTelemetryCommandPresentation(
            TelemetryStopButton,
            TelemetryStopLabel,
            LocalizedResources.Get("Telemetry_Stop.Content", "Stop recording"));
        SetTelemetryCommandPresentation(
            TelemetryOpenButton,
            TelemetryOpenLabel,
            LocalizedResources.Get("Telemetry_Open.Content", "Open CSV replay"));
        SetTelemetryCommandPresentation(
            TelemetryLiveButton,
            TelemetryLiveLabel,
            LocalizedResources.Get("Telemetry_Live.Content", "Return to live"));
        TelemetryRecordButton.Visibility = !recording && !replay
            ? Visibility.Visible
            : Visibility.Collapsed;
        TelemetryStopButton.Visibility = recording
            ? Visibility.Visible
            : Visibility.Collapsed;
        TelemetryLiveButton.Visibility = replay
            ? Visibility.Visible
            : Visibility.Collapsed;
        TelemetryRecordButton.IsEnabled = enabled && !recording && !replay;
        TelemetryStopButton.IsEnabled = recording;
        TelemetryLiveButton.IsEnabled = replay;
        string recordingOff = LocalizedResources.Get(
            "TelemetryUI_RecordingOff",
            "Recording off · readings stay in memory until you record a CSV.");
        ToolTipService.SetToolTip(TelemetryRecordButton, recordingOff);
        TelemetryRecordingText.Text = recording
            ? LocalizedResources.Format(
                "TelemetryUI_RecordingActive",
                "Recording · {0}. Sampling continues while this window is hidden.",
                localizationSnapshotMode ? @"C:\Telemetry\Example.csv" : ViewModel.TelemetryRecordingPath ?? string.Empty)
            : string.Empty;
        TelemetryRecordingText.Visibility = recording
            ? Visibility.Visible
            : Visibility.Collapsed;
        telemetryPresentationDirty = false;
    }

    private static void SetTelemetryCommandPresentation(
        Button button,
        TextBlock label,
        string text)
    {
        label.Text = text;
        AutomationProperties.SetName(button, text);
    }

    private void RefreshTelemetryEvents()
    {
        IReadOnlyList<TelemetryEvent> events = TelemetryPlot.VisibleEvents;
        TelemetryEventsText.Text = events.Count == 0
            ? LocalizedResources.Get(
                "TelemetryUI_NoEvents",
                "No power or profile changes in this range.")
            : string.Join(Environment.NewLine, events.TakeLast(12).Select(marker =>
                $"{PresentationTimestamp.ForDisplay(marker.TimestampUtc):G} · {(marker.Kind == TelemetryEventKind.PowerSourceChanged ? LocalizedResources.Get("TelemetryUI_PowerSource", "Power source") : LocalizedResources.Get("TelemetryUI_Profile", "Profile"))}: {marker.PreviousValue} → {marker.CurrentValue}"));
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
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.MainWindow.Telemetry",
                "RunTelemetryAction",
                exception,
                label);
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
