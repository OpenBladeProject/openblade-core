using System.IO;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public interface IDiagnosticArchiveDestination
{
    string? ChooseDestination(string suggestedFileName);
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
}

public sealed class DiagnosticArchiveSaver(IDiagnosticArchiveDestination destination)
    : IDiagnosticArchiveSaver
{
    private readonly IDiagnosticArchiveDestination destination = destination
        ?? throw new ArgumentNullException(nameof(destination));

    public async Task<DiagnosticArchiveSaveResult> SaveAsync(
        DiagnosticArchive archive,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(archive);
        token.ThrowIfCancellationRequested();

        string? selectedPath = destination.ChooseDestination(archive.SuggestedFileName);
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
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(archive.ZipBytes, token);
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
