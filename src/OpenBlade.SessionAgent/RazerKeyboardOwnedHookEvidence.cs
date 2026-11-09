using System.Text.Json;

namespace OpenBlade.SessionAgent;

/// <summary>Records only independently read-back hook ownership for the exact supervised child.</summary>
internal static class RazerKeyboardOwnedHookEvidence
{
    internal static string PathFor(int parentId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBlade",
        $"keyboard-driver-owned-{parentId}.json");

    internal static void Write(int parentId, IReadOnlyList<byte[]> hooks)
    {
        string path = PathFor(parentId);
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, new Evidence(Environment.ProcessId, DateTimeOffset.UtcNow, hooks.ToArray()));
            file.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static IReadOnlyList<byte[]> Read(int parentId, int childId, DateTimeOffset startedAt,
        RazerKeyboardHookPurpose purpose)
    {
        string path = PathFor(parentId);
        if (!File.Exists(path)) { return []; }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 131072) { throw new InvalidDataException("Keyboard ownership evidence is oversized."); }
        Evidence? evidence = JsonSerializer.Deserialize<Evidence>(file);
        if (evidence is null || !IsValid(evidence.ProcessId, evidence.UpdatedAt, evidence.Hooks,
            childId, startedAt, purpose))
        {
            throw new InvalidDataException("Keyboard ownership evidence does not match the supervised child.");
        }
        return evidence.Hooks;
    }

    internal static bool IsValid(int processId, DateTimeOffset updatedAt, IReadOnlyList<byte[]>? hooks,
        int childId, DateTimeOffset startedAt, RazerKeyboardHookPurpose purpose) =>
        processId == childId && updatedAt >= startedAt && hooks is not null
            && hooks.Count <= RazerSnapTapDriverProtocol.ScanCodes(purpose).Count
            && !hooks.Any(record => record is null
                || !RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
                || hook is null || !RazerSnapTapDriverProtocol.IsOwnedHook(hook, purpose))
            && hooks.Select(Convert.ToHexString).Distinct().Count() == hooks.Count;

    private sealed record Evidence(int ProcessId, DateTimeOffset UpdatedAt, byte[][] Hooks);
}
