using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Owns bounded profile-file ingestion and parsing away from the UI thread.</summary>
public static class ProfileImportReader
{
    public static Task<ProfileSet> ReadAsync(string path, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadCoreAsync(stream, token).ConfigureAwait(false);
        }, token);

    public static Task<ProfileSet> ReadAsync(Stream stream, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // Own the whole read/decode/parse pipeline off the caller's context, including
        // streams whose asynchronous reads complete synchronously.
        return Task.Run(() => ReadCoreAsync(stream, token), token);
    }

    private static async Task<ProfileSet> ReadCoreAsync(Stream stream, CancellationToken token)
    {
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int remaining = ProfileExportSerializer.MaximumFileSizeBytes - (int)content.Length;
            int count = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), token)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            if (count > remaining)
            {
                throw new InvalidDataException("Profile files cannot exceed 1 MiB.");
            }
            content.Write(buffer, 0, count);
        }

        token.ThrowIfCancellationRequested();
        content.Position = 0;
        using var reader = new StreamReader(content, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        string json = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ProfileSet profiles = ProfileExportSerializer.Deserialize(json);
        token.ThrowIfCancellationRequested();
        return profiles;
    }
}
