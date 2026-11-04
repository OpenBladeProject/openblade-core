using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal sealed record LaptopControlPendingEntry(
    ApplyOneShotLaptopControl Requested, bool ReconciliationRequired);

internal sealed record LaptopControlPendingDocument(
    int Version, string Machine, string Identity, LaptopControlPendingEntry[] Entries);

internal sealed record LaptopControlPendingLoad(
    LaptopControlPendingDocument? Document, string? Sha256, string? Failure)
{
    internal bool RequiresReconciliation => Failure is not null
        || Document?.Entries.Any(entry => entry.ReconciliationRequired) == true;
}

// Machine-owned service storage, never the active user's profile directory. A
// missing record and an unreadable/unknown record have intentionally different meanings.
internal sealed class LaptopControlPendingStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;
    private readonly SemaphoreSlim gate;

    internal LaptopControlPendingStore(string path)
    {
        this.path = Path.GetFullPath(path);
        gate = Gates.GetOrAdd(this.path, static _ => new SemaphoreSlim(1, 1));
    }

    internal LaptopControlPendingLoad Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 8192)
            {
                throw new InvalidDataException("The laptop-control recovery record has an invalid size.");
            }
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            LaptopControlPendingDocument? document = JsonSerializer.Deserialize<LaptopControlPendingDocument>(bytes);
            if (document is null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Machine)
                || document.Machine.Length > 256 || string.IsNullOrWhiteSpace(document.Identity)
                || document.Identity.Length > 256 || document.Entries is null || document.Entries.Length > 4
                || document.Entries.Any(entry => entry?.Requested?.IsWellFormed() != true)
                || document.Entries.Select(entry => entry.Requested.Control).Distinct().Count() != document.Entries.Length)
            {
                throw new InvalidDataException("The laptop-control recovery record is unsupported or invalid.");
            }
            return new(document, Convert.ToHexString(SHA256.HashData(bytes)), null);
        }
        catch (FileNotFoundException)
        {
            return new(null, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(null, null, null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null, $"Laptop-control recovery is paused: {exception.Message}");
        }
    }

    internal async Task<bool> TrySaveAsync(string? expectedSha256,
        LaptopControlPendingDocument document, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LaptopControlPendingLoad current = Load();
            if (current.Failure is not null || !string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal))
            {
                return false;
            }
            await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(document), token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static string DocumentHash(LaptopControlPendingDocument document) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document)));
}
