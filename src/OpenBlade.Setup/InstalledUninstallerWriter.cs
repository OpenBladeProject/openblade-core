using System.Security.Cryptography;

namespace OpenBlade.Setup;

public sealed record InstalledUninstallerWriteRequest(
    string SourceExecutable,
    string DestinationExecutable);

public interface IInstalledUninstallerWriter
{
    Task WriteVerifiedAsync(
        InstalledUninstallerWriteRequest request,
        CancellationToken cancellationToken);
}

internal sealed class VerifiedInstalledUninstallerWriter : IInstalledUninstallerWriter
{
    private const int BufferSize = 128 * 1024;

    public async Task WriteVerifiedAsync(
        InstalledUninstallerWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string source = Path.GetFullPath(request.SourceExecutable);
        string destination = Path.GetFullPath(request.DestinationExecutable);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Setup cannot install the uninstaller from the active install directory.");
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The running OpenBlade setup executable is unavailable.", source);
        }

        string destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The installed uninstaller destination is invalid.");
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The activated OpenBlade install directory is unavailable: '{destinationDirectory}'.");
        }

        long sourceLength = new FileInfo(source).Length;
        byte[] sourceHash;
        await using (FileStream sourceHashStream = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            sourceHash = await SHA256.HashDataAsync(sourceHashStream, cancellationToken);
        }

        bool destinationCreated = false;
        try
        {
            await using (FileStream sourceStream = new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destinationStream = new(
                destination,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                }))
            {
                destinationCreated = true;
                await sourceStream.CopyToAsync(destinationStream, BufferSize, cancellationToken);
                destinationStream.Flush(flushToDisk: true);
            }

            FileInfo destinationInfo = new(destination);
            if (destinationInfo.Length != sourceLength)
            {
                throw new InvalidDataException(
                    $"Installed uninstaller length mismatch: expected {sourceLength}, got {destinationInfo.Length}.");
            }

            byte[] destinationHash;
            await using (FileStream destinationHashStream = new(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                destinationHash = await SHA256.HashDataAsync(destinationHashStream, cancellationToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            {
                throw new InvalidDataException("Installed uninstaller SHA-256 verification failed.");
            }
        }
        catch
        {
            if (destinationCreated)
            {
                try
                {
                    File.Delete(destination);
                }
                catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
                {
                    // The enclosing payload transaction still owns and removes the activated tree.
                }
            }

            throw;
        }
    }
}
