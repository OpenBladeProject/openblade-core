using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public interface IDiagnosticArchiveDestination
{
    Task<string?> ChooseDestinationAsync(
        string suggestedFileName,
        CancellationToken token);
}

public enum DiagnosticArchiveSaveStatus
{
    Saved,
    Canceled,
}

public sealed record DiagnosticArchiveSaveResult(
    DiagnosticArchiveSaveStatus Status,
    string? DestinationPath);

public interface IDiagnosticArchiveSaver
{
    Task<DiagnosticArchiveSaveResult> SaveAsync(
        DiagnosticArchive archive,
        CancellationToken token);

    Task<DiagnosticArchiveSaveResult> SaveExactAsync(
        DiagnosticArchive archive,
        CancellationToken token);
}

public sealed class DiagnosticArchiveSaver : IDiagnosticArchiveSaver
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IDiagnosticArchiveDestination destination;
    private readonly string userModeLogDirectory;
    private readonly IThxAudioDiagnosticProvider? thxAudioDiagnostics;
    private readonly Func<SnapTapDiagnosticReport> snapTapDiagnostics;

    public DiagnosticArchiveSaver(IDiagnosticArchiveDestination destination)
        : this(destination, PersistentTraceLog.DefaultLogDirectory, thxAudioDiagnostics: null)
    {
    }

    public DiagnosticArchiveSaver(
        IDiagnosticArchiveDestination destination,
        IThxAudioDiagnosticProvider thxAudioDiagnostics)
        : this(destination, PersistentTraceLog.DefaultLogDirectory, thxAudioDiagnostics)
    {
    }

    public DiagnosticArchiveSaver(
        IDiagnosticArchiveDestination destination,
        string userModeLogDirectory)
        : this(destination, userModeLogDirectory, thxAudioDiagnostics: null)
    {
    }

    public DiagnosticArchiveSaver(
        IDiagnosticArchiveDestination destination,
        string userModeLogDirectory,
        IThxAudioDiagnosticProvider? thxAudioDiagnostics,
        Func<SnapTapDiagnosticReport>? snapTapDiagnostics = null)
    {
        this.destination = destination
            ?? throw new ArgumentNullException(nameof(destination));
        ArgumentException.ThrowIfNullOrWhiteSpace(userModeLogDirectory);
        this.userModeLogDirectory = userModeLogDirectory;
        this.thxAudioDiagnostics = thxAudioDiagnostics;
        this.snapTapDiagnostics = snapTapDiagnostics ?? (() => SnapTapStatusStore.CaptureDiagnostics());
    }

    public Task<DiagnosticArchiveSaveResult> SaveAsync(
        DiagnosticArchive archive,
        CancellationToken token) =>
        SaveCoreAsync(archive, appendUserModeLogs: true, token);

    public Task<DiagnosticArchiveSaveResult> SaveExactAsync(
        DiagnosticArchive archive,
        CancellationToken token) =>
        SaveCoreAsync(archive, appendUserModeLogs: false, token);

    private async Task<DiagnosticArchiveSaveResult> SaveCoreAsync(
        DiagnosticArchive archive,
        bool appendUserModeLogs,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(archive);
        token.ThrowIfCancellationRequested();

        string? selectedPath = await destination
            .ChooseDestinationAsync(archive.SuggestedFileName, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (selectedPath is null)
        {
            return new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Canceled,
                null);
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new InvalidOperationException("A diagnostic destination is required.");
        }

        string destinationPath = Path.GetFullPath(selectedPath);
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw new InvalidOperationException("The diagnostic destination has no parent directory.");
        }

        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] archiveBytes = archive.ZipBytes;
            if (appendUserModeLogs)
            {
                var sections = new List<DiagnosticExportSection>();
                archiveBytes = UserModeDiagnosticLogAppender.TryAppendLogs(
                    archive.ZipBytes,
                    userModeLogDirectory,
                    token,
                    sections.Add);
                SnapTapDiagnosticReport snapTap;
                try
                {
                    snapTap = snapTapDiagnostics();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    snapTap = new(DateTimeOffset.UtcNow, null,
                        DiagnosticFailure.FromException("SnapTap", "Diagnostics", "Capture", "CaptureFailed", exception));
                }
                archiveBytes = TryAppendJson(archiveBytes, "snap-tap.json", snapTap, sections, token);
                if (thxAudioDiagnostics is not null)
                {
                    ThxAudioDiagnosticReport diagnostics;
                    try
                    {
                        diagnostics = await thxAudioDiagnostics
                            .CaptureAsync(token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        diagnostics = ThxAudioDiagnosticProvider.Failure(exception);
                    }
                    archiveBytes = TryAppendJson(
                        archiveBytes,
                        "thx-audio.json",
                        diagnostics,
                        sections,
                        token);
                }
                else
                {
                    sections.Add(new("thx-audio.json", DiagnosticExportStatus.NotRequested));
                }
                var health = new List<DiagnosticLogWriterHealth>();
                try
                {
                    string healthDirectory = Path.Combine(userModeLogDirectory, "health");
                    string[] healthFiles = Directory.Exists(healthDirectory)
                        ? Directory.EnumerateFiles(healthDirectory, "OpenBlade.*.health.json").Take(33).ToArray() : [];
                    var healthFailures = new List<DiagnosticFailure>();
                    foreach (string healthFile in healthFiles.Take(32))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            using var healthStream = new FileStream(healthFile, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            if (healthStream.Length > 2 * 1024 * 1024)
                            {
                                throw new InvalidDataException("Writer health exceeds its inspection bound.");
                            }
                            DiagnosticLogWriterHealth observation = JsonSerializer.Deserialize<DiagnosticLogWriterHealth>(healthStream)
                                ?? throw new InvalidDataException("Writer health is empty.");
                            if (observation.Component is not { Length: > 0 and <= 96 }
                                || observation.Version?.Length > 256 || observation.LastFailureVersion?.Length > 256
                                || observation.FailedWrites < 0 || observation.TruncatedEvents < 0
                                || observation.HealthPersistenceFailures < 0
                                || observation.LastFailure is { } failure && !failure.IsValid())
                            {
                                throw new InvalidDataException("Writer health has invalid counters or detail.");
                            }
                            health.Add(observation);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                            or JsonException or InvalidDataException or NotSupportedException)
                        {
                            healthFailures.Add(DiagnosticFailure.FromException(
                                "Diagnostics", "WriterHealth", "Read", "HealthReadFailed", exception));
                        }
                    }
                    string[] expected = snapTap.Status?.Failure is not null
                        ? ["OpenBlade.Tray", "OpenBlade.SessionAgent", "OpenBlade.SnapTap"]
                        : ["OpenBlade.Tray", "OpenBlade.SessionAgent"];
                    foreach (string component in expected.Where(component => !health.Any(value => value.Component == component)))
                    {
                        healthFailures.Add(new("Diagnostics", component, "WriterHealth", "SourceNotObserved"));
                    }
                    if (healthFiles.Length > 32)
                    {
                        healthFailures.Add(new("Diagnostics", "WriterHealth", "Read", "HealthFileCountLimit"));
                    }
                    sections.Add(new("LogWriterHealth", healthFailures.Count == 0
                        ? DiagnosticExportStatus.Included : DiagnosticExportStatus.Partial,
                        health.Count, healthFailures));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    sections.Add(new("LogWriterHealth", DiagnosticExportStatus.Failed, Failures:
                        [DiagnosticFailure.FromException("Diagnostics", "WriterHealth", "Enumerate", "HealthReadFailed", exception)]));
                }
                archiveBytes = TryAppendJson(archiveBytes, "log-writer-health.json", health.ToArray(), sections, token);
                try
                {
                    using (var payload = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read))
                    {
                        JsonElement? serviceHistory = null;
                        if (payload.GetEntry("service-history.json") is { } historyEntry)
                        {
                            using Stream historyStream = historyEntry.Open();
                            using JsonDocument historyDocument = JsonDocument.Parse(historyStream);
                            serviceHistory = historyDocument.RootElement.Clone();
                        }
                        bool historyComplete = serviceHistory is null || serviceHistory.Value.EnumerateObject()
                            .Where(value => value.Name.Equals("EvictedEntries", StringComparison.OrdinalIgnoreCase)
                                || value.Name.Equals("DroppedEntries", StringComparison.OrdinalIgnoreCase)
                                || value.Name.Equals("TruncatedValues", StringComparison.OrdinalIgnoreCase))
                            .All(value => value.Value.TryGetInt64(out long count) && count == 0);
                        IReadOnlyList<DiagnosticLogWriterHealth> writerHealth = PersistentTraceLog.CaptureWriterHealth();
                        var inventory = new
                        {
                            CapturedAt = DateTimeOffset.UtcNow,
                            FailureDetailsVersion = 2,
                            ProcessId = Environment.ProcessId,
                            CurrentProcessWriters = writerHealth,
                            ServiceHistory = serviceHistory,
                            IsComplete = historyComplete
                                && writerHealth.Concat(health).All(writer => writer.FailedWrites == 0
                                    && writer.TruncatedEvents == 0 && writer.HealthPersistenceFailures == 0)
                                && sections.All(section => section.Status is DiagnosticExportStatus.Included
                                    or DiagnosticExportStatus.NotRequested),
                            Limits = new
                            {
                                LogFileBytes = PersistentTraceLog.MaximumFileBytes,
                                LogFiles = 128,
                                TotalLogBytes = 64L * 1024 * 1024,
                                FailureDepth = DiagnosticFailure.MaximumDepth,
                                FailureNodes = DiagnosticFailure.MaximumNodes
                            },
                            Entries = payload.Entries.Select(entry => new { entry.FullName, entry.Length }).ToArray(),
                        };
                        archiveBytes = TryAppendJson(archiveBytes, "diagnostic-inventory.json", inventory, sections, token);
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException
                    or InvalidOperationException or IOException or NotSupportedException)
                {
                    sections.Add(new("diagnostic-inventory.json", DiagnosticExportStatus.Failed, Failures:
                        [DiagnosticFailure.FromException("Diagnostics", "Inventory", "ArchiveRead", "InvalidArchive", exception)]));
                }
                // Best-effort capture must remain explicit about omissions. The manifest
                // also survives a failed sibling supplement and contains no private paths.
                archiveBytes = TryAppendJson(archiveBytes, "diagnostic-export.json",
                    sections.ToArray(), null, token);
            }
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(archiveBytes, token);
                await stream.FlushAsync(token);
                token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new DiagnosticArchiveSaveResult(
                DiagnosticArchiveSaveStatus.Saved,
                destinationPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException exception)
            {
                DiagnosticTrace.TraceException("Diagnostics", "RemoveTemporaryArchive", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                DiagnosticTrace.TraceException("Diagnostics", "RemoveTemporaryArchive", exception);
            }
        }
    }

    private static byte[] TryAppendJson<T>(
        byte[] archiveBytes,
        string entryName,
        T diagnostics,
        ICollection<DiagnosticExportSection>? sections,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var destination = new MemoryStream();
            destination.Write(archiveBytes);
            destination.Position = 0;
            using (var archive = new ZipArchive(
                destination,
                ZipArchiveMode.Update,
                leaveOpen: true))
            {
                archive.GetEntry(entryName)?.Delete();
                ZipArchiveEntry entry = archive.CreateEntry(
                    entryName,
                    CompressionLevel.Optimal);
                using Stream target = entry.Open();
                JsonSerializer.Serialize(target, diagnostics, DiagnosticJsonOptions);
            }
            DiagnosticFailure? captureFailure = diagnostics switch
            {
                SnapTapDiagnosticReport report => report.CaptureFailure,
                ThxAudioDiagnosticReport report => report.Failure,
                _ => null,
            };
            sections?.Add(new(entryName,
                captureFailure is null ? DiagnosticExportStatus.Included : DiagnosticExportStatus.Partial,
                1, captureFailure is null ? null : [captureFailure]));
            return destination.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or NotSupportedException)
        {
            DiagnosticFailure failure = DiagnosticFailure.FromException(
                "Diagnostics", "Export", "AppendSupplement", "SupplementAppendFailed", exception);
            sections?.Add(new(entryName, DiagnosticExportStatus.Failed, Failures: [failure]));
            Trace.TraceWarning("Diagnostic supplement {0} was omitted: {1}", entryName, failure);
            return archiveBytes;
        }
    }
}
