#if OPENBLADE_DEVELOPER_HARNESS
namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private static readonly Guid TelemetryReplaySnapshotSessionId =
        Guid.Parse("933944b5-c596-45f0-913b-8d597f4ef7bf");
    private static readonly DateTimeOffset TelemetrySourceSnapshotEnd =
        FeatureSnapshotTime.AddSeconds(35);

    private string? telemetrySourceSnapshotDestination;
    private string? telemetrySourceSnapshotPath;

    internal async Task PrepareLocalizationSnapshotTelemetrySourceAsync(
        string destination,
        string outputDirectory)
    {
        if (!localizationSnapshotMode)
        {
            throw new InvalidOperationException(
                "Telemetry source sample data requires disconnected snapshot mode.");
        }
        if (destination is not ("telemetry-recording" or "telemetry-replay"))
        {
            throw new ArgumentOutOfRangeException(nameof(destination), destination,
                "Unknown telemetry source snapshot route.");
        }
        if (telemetrySourceSnapshotPath is not null)
        {
            throw new InvalidOperationException("A telemetry source snapshot is already prepared.");
        }

        string path = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $".openblade-{destination}-{Guid.NewGuid():N}.csv");
        telemetrySourceSnapshotDestination = destination;
        telemetrySourceSnapshotPath = path;
        try
        {
            if (destination == "telemetry-recording")
            {
                await ViewModel.StartTelemetryRecordingAsync(path, CancellationToken.None);
            }
            else
            {
                TelemetryHistorySnapshot snapshot = ViewModel.GetTelemetrySnapshot(
                    rangeEndUtc: TelemetrySourceSnapshotEnd);
                var metadata = TelemetrySessionMetadata.CreateLive(
                    FeatureSnapshotTime,
                    TelemetryReplaySnapshotSessionId) with
                {
                    EndedAtUtc = TelemetrySourceSnapshotEnd,
                };
                await TelemetryCsv.WriteAsync(
                    path,
                    new TelemetrySessionDocument(metadata, snapshot.RawSamples, snapshot.Events),
                    cancellationToken: CancellationToken.None);
                await ViewModel.OpenTelemetryReplayAsync(path, CancellationToken.None);
            }

            RefreshTelemetryPresentation(TelemetrySourceSnapshotEnd);
            TelemetryPlot.ShowLocalizationSnapshotInspection(0.4);
        }
        catch
        {
            await CleanupLocalizationSnapshotTelemetrySourceAsync();
            throw;
        }
    }

    internal async Task CleanupLocalizationSnapshotTelemetrySourceAsync()
    {
        string? destination = telemetrySourceSnapshotDestination;
        string? path = telemetrySourceSnapshotPath;
        if (path is null)
        {
            return;
        }

        try
        {
            if (destination == "telemetry-recording")
            {
                await ViewModel.StopTelemetryRecordingAsync(CancellationToken.None);
            }
            else if (destination == "telemetry-replay")
            {
                ViewModel.CloseTelemetryReplay();
            }
        }
        finally
        {
            telemetrySourceSnapshotDestination = null;
            telemetrySourceSnapshotPath = null;
            try
            {
                File.Delete(path);
            }
            finally
            {
                File.Delete(path + ".partial");
            }
        }
    }
}
#endif
