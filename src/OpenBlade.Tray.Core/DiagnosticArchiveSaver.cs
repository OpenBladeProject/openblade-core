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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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
            sections?.Add(new(entryName, DiagnosticExportStatus.Included, 1));
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
