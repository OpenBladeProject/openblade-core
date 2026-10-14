using System.IO;
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
    private readonly IDiagnosticArchiveDestination destination;
    private readonly string userModeLogDirectory;

    public DiagnosticArchiveSaver(IDiagnosticArchiveDestination destination)
        : this(destination, PersistentTraceLog.DefaultLogDirectory)
    {
    }

    public DiagnosticArchiveSaver(
        IDiagnosticArchiveDestination destination,
        string userModeLogDirectory)
    {
        this.destination = destination
            ?? throw new ArgumentNullException(nameof(destination));
        ArgumentException.ThrowIfNullOrWhiteSpace(userModeLogDirectory);
        this.userModeLogDirectory = userModeLogDirectory;
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
            byte[] archiveBytes = appendUserModeLogs
                ? UserModeDiagnosticLogAppender.TryAppendLogs(
                    archive.ZipBytes,
                    userModeLogDirectory,
                    token)
                : archive.ZipBytes;
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
}
