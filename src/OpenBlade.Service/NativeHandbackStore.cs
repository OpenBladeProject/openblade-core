using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Service;

// One handback item that managed has not resolved yet. Key is pending,
// ownedFan, liveTemporaryCharge or abandonedPrepared.
internal sealed record NativeHandbackObligation(string Epoch, string RecordCommitment, string Key, NativeHandbackItem Item);

internal sealed record NativeHandbackAdoption(IReadOnlyList<NativeHandbackObligation> Obligations, string? Failure)
{
    internal static readonly NativeHandbackAdoption None = new([], null);
    internal bool RequiresRecovery => Failure is not null || Obligations.Count > 0;
}

// Identifies the items a pending confirmation resolves. An unresolvable claim
// stands for unreadable evidence and keeps its subsystem blocked.
internal sealed record NativeHandbackClaim(string Epoch, string RecordCommitment, IReadOnlyList<string> Keys, string? UserSid,
    bool EnablesTemporaryFullCharge, bool Resolvable = true)
{
    internal static readonly NativeHandbackClaim Unresolvable = new("", "", [], null, false, Resolvable: false);

    // Only an active override is claimed; nothing native sent is outstanding.
    internal bool LiveTemporaryChargeOnly => Keys is ["liveTemporaryCharge"];
}

// Setup writes immutable records into the protected native-handback
// directory. This store reads them and keeps managed's write-ahead resolution
// for each generation beside its record; records are never deleted. A record
// with unresolved items is adopted only when this start's fresh native custody
// computed the same history commitment for its generation. Unreadable or
// unbound evidence, or a missing record, is a failure, never "resolved".
internal sealed class NativeHandbackStore(
    Func<WindowsNativeEpochFiles?> open,
    Func<IReadOnlyDictionary<string, string>> authenticated)
{
    internal static readonly string[] Keys = ["pending", "ownedFan", "liveTemporaryCharge", "abandonedPrepared"];
    internal static readonly string[] Outcomes = ["matched", "mismatched", "reported"];
    private const string ResolutionHeader = "OpenBlade.NativeHandbackResolution.v1";
    private const string Unresolved = "-";
    private const int MaximumRecords = 64;
    private const int MaximumEntries = 2 * MaximumRecords + 8;
    private readonly object gate = new();

    internal NativeHandbackAdoption Load()
    {
        lock (gate)
        {
            try
            {
                IReadOnlyDictionary<string, string> bound = authenticated();
                using WindowsNativeEpochFiles? files = open();
                if (files is null)
                {
                    return bound.Count == 0 ? NativeHandbackAdoption.None
                        : throw new InvalidDataException("A native handback record is missing for its generation.");
                }

                string[] names = [.. Directory.EnumerateFileSystemEntries(files.Root).Take(MaximumEntries + 1)
                    .Select(path => Path.GetFileName(path))];
                if (names.Length > MaximumEntries) { throw new InvalidDataException("Native handback evidence exceeds its bound."); }
                var records = new SortedSet<string>(StringComparer.Ordinal);
                var resolutions = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in names)
                {
                    // An interrupted protected publication leaves only its receipt.
                    if (name.StartsWith("receipt-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)) { continue; }
                    if (Epoch(name, ".json") is { } record) { records.Add(record); }
                    else if (Epoch(name, ".resolved") is { } resolved) { resolutions.Add(resolved); }
                    else { throw new InvalidDataException("Unknown native handback evidence was preserved."); }
                }
                if (records.Count > MaximumRecords) { throw new InvalidDataException("Native handback records exceed their bound."); }
                if (resolutions.Any(epoch => !records.Contains(epoch)) || bound.Keys.Any(epoch => !records.Contains(epoch)))
                { throw new InvalidDataException("A native handback record is missing for its generation."); }

                var obligations = new List<NativeHandbackObligation>();
                foreach (string epoch in records)
                {
                    (WindowsNativeHandback handback, _, string commitment) = WindowsNativeHandback.Parse(Read(files, epoch + ".json"));
                    if (handback.Epoch != epoch) { throw new InvalidDataException("Native handback record name differs from its generation."); }
                    bool authenticatedHistory = bound.TryGetValue(epoch, out string? history);
                    if (authenticatedHistory && history != handback.Commitment)
                    { throw new InvalidDataException("A native handback record differs from its native history."); }
                    Dictionary<string, string> outcomes = ReadResolution(files, epoch, commitment);
                    string[] present = [.. Items(handback).Select(i => i.Key)];
                    if (Keys.Any(key => outcomes[key] != Unresolved && !present.Contains(key)))
                    { throw new InvalidDataException("A native handback resolution names an item its record lacks."); }
                    NativeHandbackObligation[] unresolved = [.. Items(handback).Where(i => outcomes[i.Key] == Unresolved)
                        .Select(i => new NativeHandbackObligation(epoch, commitment, i.Key, i.Item))];
                    if (unresolved.Length > 0 && !authenticatedHistory)
                    { throw new InvalidDataException("An unresolved native handback record is not bound to native history."); }
                    obligations.AddRange(unresolved);
                }
                // Only one crashed generation can hand back; more is unknown history.
                if (obligations.Select(o => o.Epoch).Distinct().Count() > 1)
                { throw new InvalidDataException("More than one native generation has unresolved handback items."); }
                return new(obligations, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException)
            {
                return new([], $"Native handback evidence could not be read ({exception.Message}).");
            }
        }
    }

    // Write-ahead: the caller acts only after this returns true.
    internal bool TryResolve(NativeHandbackClaim claim, string outcome)
    {
        if (!claim.Resolvable || !Outcomes.Contains(outcome)) { return false; }
        lock (gate)
        {
            try
            {
                using WindowsNativeEpochFiles? files = open();
                if (files is null) { return false; }
                (WindowsNativeHandback handback, _, string commitment) = WindowsNativeHandback.Parse(Read(files, claim.Epoch + ".json"));
                if (handback.Epoch != claim.Epoch || commitment != claim.RecordCommitment) { return false; }
                string[] present = [.. Items(handback).Select(i => i.Key)];
                if (claim.Keys.Count == 0 || claim.Keys.Any(key => !present.Contains(key))) { return false; }
                Dictionary<string, string> outcomes = ReadResolution(files, claim.Epoch, commitment);
                foreach (string key in claim.Keys.Where(key => outcomes[key] == Unresolved)) { outcomes[key] = outcome; }
                files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, claim.Epoch),
                    NativeCanonical.Lines(ResolutionHeader, [claim.Epoch, commitment, .. Keys.Select(key => outcomes[key])]), replace: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException)
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

    private static string? Epoch(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal) && NativeCanonical.IsHex(name[..^suffix.Length], 32)
            ? name[..^suffix.Length] : null;

    private static byte[] Read(WindowsNativeEpochFiles files, string name)
    {
        using FileStream held = files.OpenFile(Path.Combine(files.Root, name));
        return WindowsNativeEpochFiles.ReadBounded(held, WindowsNativeHandback.MaximumBytes);
    }

    // One outcome per key, or "-" while unresolved. A resolution written for a
    // different record is a failure.
    private static Dictionary<string, string> ReadResolution(WindowsNativeEpochFiles files, string epoch, string commitment)
    {
        var outcomes = Keys.ToDictionary(key => key, _ => Unresolved, StringComparer.Ordinal);
        if (ManagedMailboxRecovery.ReadOptional(files, WindowsNativeHandbackStorage.ResolutionPath(files, epoch)) is not { } bytes) { return outcomes; }
        string[] fields = NativeCanonical.ParseLines(bytes, ResolutionHeader, 3 + Keys.Length);
        if (fields[0] != epoch || fields[1] != commitment)
        { throw new InvalidDataException("A native handback resolution belongs to a different record."); }
        for (int index = 0; index < Keys.Length; index++)
        {
            string value = fields[2 + index];
            if (value != Unresolved && !Outcomes.Contains(value)) { throw new InvalidDataException("A native handback resolution is invalid."); }
            outcomes[Keys[index]] = value;
        }
        return outcomes;
    }
}
