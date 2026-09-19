using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Protocol;

// Cooperative installation evidence, not a trust or hardware-admission authority.
// Restoring/updating any payload invalidates it; no Synapse version is assumed.
internal sealed class SynapseBladeBlockerVerifier
{
    private const int MaximumManifestBytes = 65536;
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    internal bool IsApplied(RunningControllerProcess process) => GetAppliedBladeProductIds(process) is not null;

    internal IReadOnlySet<int>? GetAppliedBladeProductIds(RunningControllerProcess process)
    {
        if (process.ImagePath is null || process.StartedAtUtc is null
            || !Path.IsPathFullyQualified(process.ImagePath)
            || !string.Equals(Path.GetFileName(process.ImagePath), "RazerAppEngine.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        lock (gate)
        {
            try
            {
                string executable = Path.GetFullPath(process.ImagePath);
                string resources = Path.Combine(Path.GetDirectoryName(executable)!, "resources");
                string archive = Path.Combine(resources, "app.asar");
                string manifest = Path.Combine(resources, "synapse-blade-blocker.json");
                var stamp = new InstallationStamp(Stamp(executable), Stamp(archive), Stamp(manifest));
                if (!cache.TryGetValue(executable, out CacheEntry? entry) || entry.Stamp != stamp)
                {
                    AppliedPatch? appliedPatch = Verify(executable, archive, manifest);
                    if (stamp != new InstallationStamp(Stamp(executable), Stamp(archive), Stamp(manifest)))
                    {
                        return null;
                    }

                    if (cache.Count >= 8)
                    {
                        cache.Clear();
                    }

                    entry = new CacheEntry(stamp, appliedPatch);
                    cache[executable] = entry;
                }

                return entry.Patch is { } applied && process.StartedAtUtc > applied.AppliedAtUtc
                    ? applied.BladeProductIds : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException
                or KeyNotFoundException)
            {
                return null;
            }
        }
    }

    private static AppliedPatch? Verify(string executable, string archive, string manifest)
    {
        using var stream = File.OpenRead(manifest);
        if (stream.Length <= 0 || stream.Length > MaximumManifestBytes)
        {
            return null;
        }

        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        using JsonDocument marker = JsonDocument.Parse(data);
        JsonElement root = marker.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("patchId").GetString() != "OSSBlade/synapse-blade-blocker"
            || !root.GetProperty("appliedAtUtc").TryGetDateTimeOffset(out DateTimeOffset appliedAt)
            || appliedAt.Offset != TimeSpan.Zero || appliedAt > DateTimeOffset.UtcNow)
        {
            return null;
        }

        HashSet<int> ids = [];
        foreach (JsonElement item in root.GetProperty("bladeProductIds").EnumerateArray())
        {
            int id = item.GetInt32();
            if (id <= 0 || id > ushort.MaxValue || !ids.Add(id))
            {
                return null;
            }
        }

        // All currently admitted laptop identities must be excluded, including for
        // callers that observe controllers without passing a specific device.
        if (!ids.Contains(0x02E0) || !ids.Contains(0x02C6))
        {
            return null;
        }

        return MatchesHash(archive, root.GetProperty("archiveSha256"))
            && MatchesHash(executable, root.GetProperty("executableSha256")) ? new AppliedPatch(appliedAt, ids) : null;
    }

    private static bool MatchesHash(string path, JsonElement expected)
    {
        string? hash = expected.GetString();
        if (hash is not { Length: 64 })
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc);
    }

    private sealed record FileStamp(long Length, DateTime LastWriteTimeUtc);
    private sealed record InstallationStamp(FileStamp Executable, FileStamp Archive, FileStamp Manifest);
    private sealed record AppliedPatch(DateTimeOffset AppliedAtUtc, IReadOnlySet<int> BladeProductIds);
    private sealed record CacheEntry(InstallationStamp Stamp, AppliedPatch? Patch);
}

internal sealed record RunningControllerProcess(
    string Name,
    string? ImagePath,
    DateTimeOffset? StartedAtUtc,
    int? ProcessId = null,
    int? ParentProcessId = null,
    int? SessionId = null,
    bool IsLampArrayHelper = false);
