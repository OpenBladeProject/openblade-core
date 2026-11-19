using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Service;

// One handback item that managed has not resolved yet. Key is pending,
// ownedFan, liveTemporaryCharge or abandonedPrepared.
// Confirmed marks a temporary charge an earlier start already confirmed as
// active ("continuing"); it is no longer a request native may have sent.
internal sealed record NativeHandbackObligation(string Epoch, string RecordCommitment, string Key, NativeHandbackItem Item,
    bool Confirmed = false);

internal sealed record NativeHandbackAdoption(IReadOnlyList<NativeHandbackObligation> Obligations, string? Failure)
{
    internal static readonly NativeHandbackAdoption None = new([], null);
    internal bool RequiresRecovery => Failure is not null || Obligations.Count > 0;
}

// Identifies the items a pending confirmation resolves. An unresolvable claim
// stands for unreadable evidence and keeps its subsystem blocked.
// ActiveChargeOnly: every claimed item is an active charge, either the live
// slot or a charge an earlier start confirmed. Nothing native sent is
// outstanding, so its end is reported rather than treated as a mismatch.
internal sealed record NativeHandbackClaim(string Epoch, string RecordCommitment, IReadOnlyList<string> Keys, string? UserSid,
    bool EnablesTemporaryFullCharge, bool Resolvable = true, bool ActiveChargeOnly = false)
{
    internal static readonly NativeHandbackClaim Unresolvable = new("", "", [], null, false, Resolvable: false);
}

// Setup writes an immutable record into the handed-back generation's directory
// of the native namespace. This store reads it and keeps managed's write-ahead
// resolution beside it; records are never deleted. The binding comes from this
// start's fresh native custody, which refuses a record for any generation it
// does not hand back. An unreadable or missing record for a bound generation
// is a failure, never "resolved". An unopened or refused custody admission is
// not caught here: it is the custody failure it is, never "no handback".
internal sealed class NativeHandbackStore(Func<NativeHandbackBinding> binding)
{
    // No handback binding: Load reports none and no claim resolves. Only the
    // runtime convenience constructors use it; the Service passes its own store.
    internal static NativeHandbackStore Unbound => new(() => NativeHandbackBinding.None);
    internal const string Continuing = "continuing";
    internal static readonly string[] Keys = ["pending", "ownedFan", "liveTemporaryCharge", "abandonedPrepared"];
    // continuing: a temporary full charge confirmed while its owner had not
    // signed in. A later start confirms it again by a fresh read, because it
    // may have ended in between; only then is it continued or reported.
    internal static readonly string[] Outcomes = ["matched", "mismatched", "reported", Continuing];
    private const string ResolutionHeader = "OpenBlade.NativeHandbackResolution.v1";
    private const string Unresolved = "-";
    private readonly object gate = new();

    internal NativeHandbackAdoption Load()
    {
        lock (gate)
        {
            NativeHandbackBinding bound = binding();
            if (bound.Commitments.Count == 0) { return NativeHandbackAdoption.None; }
            try
            {
                using WindowsNativeEpochFiles files = (bound.Open
                    ?? throw new InvalidDataException("A native handback record is missing for its generation."))();
                var obligations = new List<NativeHandbackObligation>();
                foreach ((string epoch, string history) in bound.Commitments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    (WindowsNativeHandback handback, string commitment) = ReadRecord(files, epoch);
                    if (history != handback.Commitment)
                    { throw new InvalidDataException("A native handback record differs from its native history."); }
                    Dictionary<string, string> outcomes = ReadResolution(files, epoch, commitment);
                    (string Key, NativeHandbackItem Item)[] items = [.. Items(handback)];
                    if (Keys.Any(key => outcomes[key] != Unresolved && !items.Any(i => i.Key == key)))
                    { throw new InvalidDataException("A native handback resolution names an item its record lacks."); }
                    if (items.Any(i => outcomes[i.Key] == Continuing && i.Item.Battery is not { Temporary: true }))
                    { throw new InvalidDataException("A native handback resolution continues a change that is not a temporary charge."); }
                    obligations.AddRange(items.Where(i => outcomes[i.Key] is Unresolved or Continuing)
                        .Select(i => new NativeHandbackObligation(epoch, commitment, i.Key, i.Item, outcomes[i.Key] == Continuing)));
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

    // Write-ahead: the caller acts only after this returns true. An outcome is
    // never replaced, except that a continuing charge reaches its final outcome.
    internal bool TryResolve(NativeHandbackClaim claim, string outcome)
    {
        if (!claim.Resolvable || !Outcomes.Contains(outcome)) { return false; }
        lock (gate)
        {
            NativeHandbackBinding bound = binding();
            if (!bound.Commitments.ContainsKey(claim.Epoch) || bound.Open is null) { return false; }
            try
            {
                using WindowsNativeEpochFiles files = bound.Open();
                (WindowsNativeHandback handback, string commitment) = ReadRecord(files, claim.Epoch);
                if (commitment != claim.RecordCommitment) { return false; }
                string[] present = [.. Items(handback).Select(i => i.Key)];
                if (claim.Keys.Count == 0 || claim.Keys.Any(key => !present.Contains(key))) { return false; }
                Dictionary<string, string> outcomes = ReadResolution(files, claim.Epoch, commitment);
                foreach (string key in claim.Keys.Where(key => outcomes[key] == Unresolved
                    || outcomes[key] == Continuing && outcome != Continuing))
                {
                    outcomes[key] = outcome;
                }
                files.Publish(WindowsNativeHandbackStorage.ResolutionPath(files, claim.Epoch),
                    NativeCanonical.Lines(ResolutionHeader, [claim.Epoch, commitment, .. Keys.Select(key => outcomes[key])]),
                    replace: true, privateCustody: true);
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

    private static (WindowsNativeHandback Handback, string RecordCommitment) ReadRecord(WindowsNativeEpochFiles files, string epoch)
    {
        byte[] bytes = ManagedMailboxRecovery.ReadOptional(files, WindowsNativeHandbackStorage.RecordPath(files, epoch))
            ?? throw new InvalidDataException("A native handback record is missing for its generation.");
        (WindowsNativeHandback handback, _, string commitment) = WindowsNativeHandback.Parse(bytes);
        if (handback.Epoch != epoch) { throw new InvalidDataException("Native handback record differs from its generation."); }
        return (handback, commitment);
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
