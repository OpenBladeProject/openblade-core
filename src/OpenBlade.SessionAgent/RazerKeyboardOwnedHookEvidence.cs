using System.Text.Json;

namespace OpenBlade.SessionAgent;

/// <summary>Records only independently read-back hook ownership for the exact supervised child.</summary>
internal static class RazerKeyboardOwnedHookEvidence
{
    private const int MaximumLength = 131072;

    internal static string PathFor(int parentId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBlade",
        $"keyboard-driver-owned-{parentId}.json");

    internal static void Write(int parentId, IReadOnlyList<byte[]> hooks) =>
        WriteAtomically(PathFor(parentId),
            JsonSerializer.SerializeToUtf8Bytes(new Evidence(Environment.ProcessId, DateTimeOffset.UtcNow, hooks.ToArray())));

    // Replaces evidence with a record no reader accepts (process id 0). A start that
    // finds it refuses automatic recovery instead of replaying or trusting the record.
    internal static void WriteRefusal(string path) =>
        WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(new Evidence(0, DateTimeOffset.UtcNow, [])));

    // Rewrites the evidence to the hooks still to clear before each cleanup
    // submission. If the rewrite fails, deleting the evidence also prevents replay
    // (the remaining hooks then refuse automatic recovery); if both fail, the
    // exception stops cleanup before that submission.
    internal static Action<IReadOnlyList<byte[]>> Recorder(string path, int workerId) => remaining =>
    {
        try
        {
            WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(
                new Evidence(workerId, DateTimeOffset.UtcNow, remaining.ToArray())));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            OpenBlade.Contracts.DiagnosticTrace.TraceException("OpenBlade.SessionAgent.RazerKeyboardOwnedHookEvidence",
                "RecordRemaining", exception);
            File.Delete(path);
        }
    };

    private static void WriteAtomically(string path, byte[] contents)
    {
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            file.Write(contents);
            file.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static IReadOnlyList<byte[]> Read(int parentId, int childId, DateTimeOffset startedAt,
        RazerKeyboardHookPurpose purpose)
    {
        Evidence? evidence = ReadFile(PathFor(parentId));
        if (evidence is null) { return []; }
        if (!IsValid(evidence.ProcessId, evidence.UpdatedAt, evidence.Hooks, childId, startedAt, purpose))
        {
            throw new InvalidDataException("Keyboard ownership evidence does not match the supervised child.");
        }
        return evidence.Hooks;
    }

    // Reads the evidence a terminated supervisor's worker left behind. The worker is
    // not known in advance, so the record must name a plausible worker and be no
    // older than the journal it belongs to. Returns null when no evidence exists.
    internal static Evidence? ReadTerminatedOwner(string path, DateTimeOffset journalCreatedAt,
        RazerKeyboardHookPurpose purpose)
    {
        Evidence? evidence = ReadFile(path);
        if (evidence is null) { return null; }
        if (evidence.ProcessId <= 0
            || !IsValid(evidence.ProcessId, evidence.UpdatedAt, evidence.Hooks, evidence.ProcessId, journalCreatedAt, purpose))
        {
            throw new InvalidDataException("Keyboard ownership evidence does not match the terminated owner's journal.");
        }
        return evidence;
    }

    internal static bool IsValid(int processId, DateTimeOffset updatedAt, IReadOnlyList<byte[]>? hooks,
        int childId, DateTimeOffset startedAt, RazerKeyboardHookPurpose purpose) =>
        processId == childId && updatedAt >= startedAt && hooks is not null
            && hooks.Count <= RazerSnapTapDriverProtocol.ScanCodes(purpose).Count
            && !hooks.Any(record => record is null
                || !RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
                || hook is null || !RazerSnapTapDriverProtocol.IsOwnedHook(hook, purpose))
            && hooks.Select(Convert.ToHexString).Distinct().Count() == hooks.Count;

    private static Evidence? ReadFile(string path)
    {
        if (!File.Exists(path)) { return null; }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumLength) { throw new InvalidDataException("Keyboard ownership evidence is oversized."); }
        return JsonSerializer.Deserialize<Evidence>(file)
            ?? throw new InvalidDataException("Keyboard ownership evidence is empty.");
    }

    internal sealed record Evidence(int ProcessId, DateTimeOffset UpdatedAt, byte[][] Hooks);
}
