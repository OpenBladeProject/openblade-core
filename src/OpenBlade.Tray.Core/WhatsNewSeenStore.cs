using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Acknowledgements are separate from user settings so saving one never rewrites the other.</summary>
public sealed class WhatsNewSeenStore
{
    private const int MaximumVersionLength = 256;
    private readonly string path;

    public WhatsNewSeenStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public static WhatsNewSeenStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade",
        "whats-new.version"));

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(file);
            var buffer = new char[MaximumVersionLength + 1];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                count += read;
            }
            return count <= MaximumVersionLength ? new string(buffer, 0, count).Trim() : null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > MaximumVersionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, version, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.WhatsNewSeenStore", "DeleteTemporaryAcknowledgement", exception);
            }
        }
    }
}
