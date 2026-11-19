using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Service;

// One handback item that managed has not resolved yet. Key is pending,
// ownedFan, liveTemporaryCharge or abandonedPrepared.
internal sealed record NativeHandbackObligation(string Epoch, string RecordSha256, string Key, NativeHandbackItem Item);

internal sealed record NativeHandbackAdoption(IReadOnlyList<NativeHandbackObligation> Obligations, string? Failure)
{
    internal static readonly NativeHandbackAdoption None = new([], null);
    internal bool RequiresRecovery => Failure is not null || Obligations.Count > 0;
}

// Identifies the items a pending confirmation resolves. An unresolvable claim
// stands for unreadable evidence and keeps its subsystem blocked.
internal sealed record NativeHandbackClaim(string Epoch, string RecordSha256, IReadOnlyList<string> Keys, string? UserSid,
    bool EnablesTemporaryFullCharge, bool Resolvable = true)
{
    internal static readonly NativeHandbackClaim Unresolvable = new("", "", [], null, false, Resolvable: false);
}

internal sealed record NativeHandbackResolution(string Epoch, string RecordSha256, string Key, string Outcome);

internal sealed record NativeHandbackConsumed(string Epoch, string RecordSha256);

internal sealed record NativeHandbackResolutionDocument(int Version, NativeHandbackResolution[] Resolutions, NativeHandbackConsumed[] Consumed);

// Setup writes immutable records; this store only reads them and keeps
// managed's write-ahead resolutions beside them. Records are never deleted.
// Unreadable evidence or resolutions are a failure, never "resolved".
internal sealed class NativeHandbackStore(string directory)
{
    internal const string ResolutionFile = "resolutions.json";
    private const int MaximumRecords = 64;
    private const int MaximumResolutions = 1024;
    private readonly object gate = new();
    internal string Directory { get; } = Path.GetFullPath(directory);

    internal NativeHandbackAdoption Load()
    {
        lock (gate)
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) { return NativeHandbackAdoption.None; }
                NativeHandbackResolutionDocument resolutions = ReadResolutions();
                var obligations = new List<NativeHandbackObligation>();
                int count = 0;
                foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(path);
                    if (name == ResolutionFile) { continue; }
                    if (++count > MaximumRecords) { throw new InvalidDataException("Native handback records exceed their bound."); }
                    byte[] bytes = ReadBounded(path, WindowsNativeHandback.MaximumBytes);
                    WindowsNativeHandback handback = WindowsNativeHandback.Parse(bytes).Handback;
                    if (name != handback.Epoch + ".json") { throw new InvalidDataException("Native handback record name differs from its generation."); }
                    string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (resolutions.Consumed.Any(c => c.Epoch == handback.Epoch && c.RecordSha256 == sha256)) { continue; }
                    foreach ((string key, NativeHandbackItem item) in Items(handback))
                    {
                        if (!resolutions.Resolutions.Any(r => r.Epoch == handback.Epoch && r.RecordSha256 == sha256 && r.Key == key))
                        { obligations.Add(new(handback.Epoch, sha256, key, item)); }
                    }
                }
                // Only one crashed generation can hand back; more is unknown history.
                if (obligations.Select(o => o.Epoch).Distinct().Count() > 1)
                { throw new InvalidDataException("More than one native generation has unresolved handback items."); }
                return new(obligations, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return new([], $"Native handback evidence could not be read ({exception.Message}).");
            }
        }
    }

    // Write-ahead: the caller acts only after this returns true. The record is
    // consumed once every item it carries has a resolution.
    internal bool TryResolve(NativeHandbackClaim claim, string outcome)
    {
        if (!claim.Resolvable) { return false; }
        lock (gate)
        {
            try
            {
                byte[] bytes = ReadBounded(Path.Combine(Directory, claim.Epoch + ".json"), WindowsNativeHandback.MaximumBytes);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != claim.RecordSha256) { return false; }
                WindowsNativeHandback handback = WindowsNativeHandback.Parse(bytes).Handback;
                NativeHandbackResolutionDocument current = ReadResolutions();
                var resolutions = current.Resolutions.ToList();
                foreach (string key in claim.Keys)
                {
                    if (!resolutions.Any(r => r.Epoch == claim.Epoch && r.RecordSha256 == claim.RecordSha256 && r.Key == key))
                    { resolutions.Add(new(claim.Epoch, claim.RecordSha256, key, outcome)); }
                }
                var consumed = current.Consumed.ToList();
                if (Items(handback).All(i => resolutions.Any(r => r.Epoch == claim.Epoch && r.RecordSha256 == claim.RecordSha256 && r.Key == i.Key))
                    && !consumed.Any(c => c.Epoch == claim.Epoch && c.RecordSha256 == claim.RecordSha256))
                { consumed.Add(new(claim.Epoch, claim.RecordSha256)); }
                if (resolutions.Count > MaximumResolutions) { return false; }
                Write(JsonSerializer.SerializeToUtf8Bytes(new NativeHandbackResolutionDocument(1, [.. resolutions], [.. consumed])));
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return false;
            }
        }
    }

    internal static IEnumerable<(string Key, NativeHandbackItem Item)> Items(WindowsNativeHandback handback)
    {
        if (handback.Pending is { } pending) { yield return ("pending", pending); }
        if (handback.OwnedFan is { } ownedFan) { yield return ("ownedFan", ownedFan); }
        if (handback.LiveTemporaryCharge is { } live) { yield return ("liveTemporaryCharge", live); }
        if (handback.AbandonedPrepared is { } prepared) { yield return ("abandonedPrepared", prepared); }
    }

    private NativeHandbackResolutionDocument ReadResolutions()
    {
        string path = Path.Combine(Directory, ResolutionFile);
        if (!File.Exists(path)) { return new(1, [], []); }
        NativeHandbackResolutionDocument? document = JsonSerializer.Deserialize<NativeHandbackResolutionDocument>(ReadBounded(path, 1024 * 1024));
        if (document is not { Version: 1, Resolutions: not null, Consumed: not null } || document.Resolutions.Length > MaximumResolutions
            || document.Resolutions.Any(r => r is null || !NativeCanonical.IsHex(r.Epoch ?? "", 32) || !NativeCanonical.IsHex(r.RecordSha256 ?? "", 64)
                || r.Key is not ("pending" or "ownedFan" or "liveTemporaryCharge" or "abandonedPrepared") || r.Outcome is not ("matched" or "mismatched" or "reported"))
            || document.Consumed.Any(c => c is null || !NativeCanonical.IsHex(c.Epoch ?? "", 32) || !NativeCanonical.IsHex(c.RecordSha256 ?? "", 64)))
        { throw new InvalidDataException("Native handback resolutions are unsupported or invalid."); }
        return document;
    }

    private void Write(byte[] bytes)
    {
        string path = Path.Combine(Directory, ResolutionFile);
        string staging = path + ".tmp";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(staging, path, overwrite: true);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum) { throw new InvalidDataException("Native handback file has an invalid size."); }
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
