using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Deployment;

internal sealed record NativeHandbackBattery(int Limit, bool Protection, bool Temporary);

// Family is performance, fan, battery or temporaryCharge. Targets are the
// native destination; release progress targets firmware Automatic.
internal sealed record NativeHandbackItem(string Request, string Family, int Progress, string UserSid,
    string? Performance, string? FanMode, NativeHandbackBattery? Battery);

internal sealed record NativeHandbackEvidence(string UserSid, string Sha256);

// What a crashed native generation leaves for managed. Pending may have been
// submitted; OwnedFan is a retained firmware obligation; LiveTemporaryCharge is
// an active override; AbandonedPrepared and StagedJournal were never submitted.
internal sealed record WindowsNativeHandback(string Epoch, NativeHandbackItem? Pending, NativeHandbackItem? OwnedFan,
    NativeHandbackItem? LiveTemporaryCharge, NativeHandbackItem? AbandonedPrepared, string? StagedJournal)
{
    // WindowsNativeEpochFiles publishes at most 16 KiB per record.
    internal const int MaximumBytes = 16384;
    internal const int MaximumUsers = 32;
    internal bool IsEmpty => Pending is null && OwnedFan is null && LiveTemporaryCharge is null && AbandonedPrepared is null && StagedJournal is null;

    // Derived from native history alone; NativeRetirementProof.v4 commits to it.
    internal string Commitment => NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeHandback.v1", Epoch,
        Line(Pending), Line(OwnedFan), Line(LiveTemporaryCharge), Line(AbandonedPrepared), StagedJournal ?? "no-staged-journal"));

    // The record additionally commits to the override evidence Setup observed.
    internal string RecordCommitment(IReadOnlyList<NativeHandbackEvidence> pendingOverrides) =>
        NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeHandbackRecord.v1",
            [Commitment, pendingOverrides.Count.ToString(CultureInfo.InvariantCulture), .. pendingOverrides.Select(o => o.UserSid + "," + o.Sha256)]));

    private static string Line(NativeHandbackItem? item) => item is null ? "none" : string.Join(',', item.Request, item.Family,
        item.Progress.ToString(CultureInfo.InvariantCulture), item.UserSid, item.Performance ?? "-", item.FanMode ?? "-",
        item.Battery is { } b ? $"{b.Limit.ToString(CultureInfo.InvariantCulture)}/{b.Protection}/{b.Temporary}" : "-");

    internal byte[] Serialize(IReadOnlyList<NativeHandbackEvidence> pendingOverrides)
    {
        using var memory = new MemoryStream();
        using (var w = new Utf8JsonWriter(memory))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("epoch", Epoch);
            w.WriteString("history", Commitment);
            w.WriteString("commitment", RecordCommitment(pendingOverrides));
            void Item(string name, NativeHandbackItem? item)
            {
                w.WritePropertyName(name);
                if (item is null) { w.WriteNullValue(); return; }
                w.WriteStartObject();
                w.WriteString("request", item.Request); w.WriteString("family", item.Family);
                w.WriteNumber("progress", item.Progress); w.WriteString("userSid", item.UserSid);
                w.WriteString("performance", item.Performance); w.WriteString("fanMode", item.FanMode);
                w.WritePropertyName("battery");
                if (item.Battery is { } b)
                {
                    w.WriteStartObject(); w.WriteNumber("limit", b.Limit); w.WriteBoolean("protection", b.Protection);
                    w.WriteBoolean("temporary", b.Temporary); w.WriteEndObject();
                }
                else { w.WriteNullValue(); }
                w.WriteEndObject();
            }
            Item("pending", Pending); Item("ownedFan", OwnedFan); Item("liveTemporaryCharge", LiveTemporaryCharge);
            Item("abandonedPrepared", AbandonedPrepared);
            w.WriteString("stagedJournal", StagedJournal);
            w.WriteStartArray("pendingOverrides");
            foreach (var evidence in pendingOverrides)
            { w.WriteStartObject(); w.WriteString("userSid", evidence.UserSid); w.WriteString("sha256", evidence.Sha256); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return memory.ToArray();
    }

    // Strict: any unknown, missing or inconsistent field refuses the record.
    internal static (WindowsNativeHandback Handback, IReadOnlyList<NativeHandbackEvidence> PendingOverrides, string RecordCommitment) Parse(byte[] bytes)
    {
        try { return ParseCore(bytes); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Native handback record is malformed.", error); }
    }
    private static (WindowsNativeHandback, IReadOnlyList<NativeHandbackEvidence>, string) ParseCore(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new InvalidDataException("Native handback record has an invalid size."); }
        using var document = JsonDocument.Parse(bytes);
        var e = document.RootElement;
        Members(e, "schema", "epoch", "history", "commitment", "pending", "ownedFan", "liveTemporaryCharge", "abandonedPrepared", "stagedJournal", "pendingOverrides");
        if (e.GetProperty("schema").GetInt32() != 1) { throw Invalid(); }
        string epoch = Text(e, "epoch");
        string? staged = e.GetProperty("stagedJournal").ValueKind == JsonValueKind.Null ? null : Text(e, "stagedJournal");
        var handback = new WindowsNativeHandback(epoch, Item(e.GetProperty("pending"), "pending"), Item(e.GetProperty("ownedFan"), "ownedFan"),
            Item(e.GetProperty("liveTemporaryCharge"), "liveTemporaryCharge"), Item(e.GetProperty("abandonedPrepared"), "abandonedPrepared"), staged);
        var overrides = new List<NativeHandbackEvidence>();
        foreach (var o in e.GetProperty("pendingOverrides").EnumerateArray())
        {
            Members(o, "userSid", "sha256");
            overrides.Add(new(Text(o, "userSid"), Text(o, "sha256")));
        }
        if (!NativeCanonical.IsHex(epoch, 32) || staged is not null && !NativeCanonical.IsHex(staged, 64) || handback.IsEmpty
            || overrides.Count > MaximumUsers || overrides.Any(o => !IsSid(o.UserSid) || !NativeCanonical.IsHex(o.Sha256, 64))
            || overrides.Select(o => o.UserSid).Distinct(StringComparer.Ordinal).Count() != overrides.Count
            || Text(e, "history") != handback.Commitment || Text(e, "commitment") != handback.RecordCommitment(overrides))
        { throw Invalid(); }
        return (handback, overrides, handback.RecordCommitment(overrides));
    }
    internal static bool IsSid(string value) => value.Length <= 184 && value.StartsWith("S-1-5-21-", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiDigit(c) || c is 'S' or '-');
    private static NativeHandbackItem? Item(JsonElement e, string slot)
    {
        if (e.ValueKind == JsonValueKind.Null) { return null; }
        Members(e, "request", "family", "progress", "userSid", "performance", "fanMode", "battery");
        string? Optional(string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(e, name);
        var b = e.GetProperty("battery");
        NativeHandbackBattery? battery = null;
        if (b.ValueKind != JsonValueKind.Null)
        {
            Members(b, "limit", "protection", "temporary");
            battery = new(b.GetProperty("limit").GetInt32(), b.GetProperty("protection").GetBoolean(), b.GetProperty("temporary").GetBoolean());
        }
        var item = new NativeHandbackItem(Text(e, "request"), Text(e, "family"), e.GetProperty("progress").GetInt32(), Text(e, "userSid"),
            Optional("performance"), Optional("fanMode"), battery);
        bool valid = Guid.TryParseExact(item.Request, "D", out var request) && request != Guid.Empty && IsSid(item.UserSid)
            && item.Family switch
            {
                "performance" => item.Performance is "Silent" or "Balanced" or "Performance" or "BatterySaver" or "BatteryBalanced" or "Custom" && item.FanMode is null && battery is null,
                "fan" => item.FanMode is "Automatic" or "Fixed" or "Maximum" && item.Performance is null && battery is null,
                // Managed cannot hold a temporary full charge with protection off.
                "battery" or "temporaryCharge" => battery is { Limit: >= 50 and <= 99 } && !(battery.Temporary && !battery.Protection)
                    && item.Performance is null && item.FanMode is null,
                _ => false,
            }
            && ValidSlot(slot, item);
        return valid ? item : throw Invalid();
    }

    // Each slot holds only the family and progress that Classify assigns to it.
    internal static bool ValidSlot(string slot, NativeHandbackItem item) => (slot, item.Family, item.Progress) switch
    {
        ("pending", "performance", >= 1 and <= 4) => true,
        // Custom's CPU and GPU level setters (thermal_mutation.rs Progress).
        ("pending", "performance", >= 7 and <= 10) => item.Performance == "Custom",
        // The HyperBoost restore pair before a battery preset.
        ("pending", "performance", >= 80 and <= 83) => item.Performance is "BatterySaver" or "BatteryBalanced",
        ("pending", "fan", >= 10 and <= 17) => true,
        ("pending", "fan", >= 40 and <= 43) => item.FanMode == "Automatic",
        ("pending", "battery", 60 or 61) => true,
        ("pending", "temporaryCharge", 70 or 71) => true,
        ("ownedFan", "fan", 30) => item.FanMode is "Fixed" or "Maximum",
        ("liveTemporaryCharge", "temporaryCharge", 72 or 73) => item.Battery is { Temporary: true },
        ("abandonedPrepared", _, 0) => true,
        ("abandonedPrepared", "fan", 31) => item.FanMode == "Automatic",
        _ => false,
    };
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw Invalid();
    private static void Members(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) { throw Invalid(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) { if (!names.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name)) { throw Invalid(); } }
        if (seen.Count != names.Length) { throw Invalid(); }
    }
    private static InvalidDataException Invalid() => new("Native handback record is unsupported or inconsistent.");
}

// The record and managed's resolutions live in the handed-back generation's
// own directory of the native epoch namespace, beside the history that binds
// them, so record and binding share one lifetime. Uninstall-and-purge removes
// OpenBlade's machine data but never the native namespace. Every access uses
// the exact-path, non-reparse, trusted-ACL checks of WindowsNativeEpochFiles.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsNativeHandbackStorage
{
    internal const string RecordName = "handback.json";
    internal const string ResolutionName = "handback.resolved";
    internal static string RecordPath(WindowsNativeEpochFiles files, string epoch) => Path.Combine(files.Root, epoch, RecordName);
    internal static string ResolutionPath(WindowsNativeEpochFiles files, string epoch) => Path.Combine(files.Root, epoch, ResolutionName);
}

// Fresh native custody's handback binding: the history commitment of each
// handed-back generation, and a view of the namespace that holds its record.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed record NativeHandbackBinding(IReadOnlyDictionary<string, string> Commitments, Func<WindowsNativeEpochFiles>? Open)
{
    internal static readonly NativeHandbackBinding None = new(new Dictionary<string, string>(StringComparer.Ordinal), null);
}

// Setup-owned writer. The record is immutable: a rerun keeps a record with the
// same epoch and history commitment and refuses a different one.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeHandbackWriter(string? usersDirectory)
{
    internal static WindowsNativeHandbackWriter ForMachineData(string machineDataDirectory) =>
        new(Path.Combine(Path.GetFullPath(machineDataDirectory), "users"));

    // files is the native namespace whose epoch custody the caller holds.
    internal void Persist(WindowsNativeEpochFiles files, WindowsNativeHandback handback)
    {
        string path = WindowsNativeHandbackStorage.RecordPath(files, handback.Epoch);
        if (ManagedMailboxRecovery.ReadOptional(files, path) is { } existing)
        {
            var preserved = WindowsNativeHandback.Parse(existing).Handback;
            if (preserved.Epoch != handback.Epoch || preserved.Commitment != handback.Commitment)
            { throw new IOException("A different native handback record is already preserved for this generation."); }
            return;
        }
        // Resolutions follow their record. One that predates it was not written
        // for this record and is never adopted.
        WindowsNativeEpochFiles.RequireAbsent(WindowsNativeHandbackStorage.ResolutionPath(files, handback.Epoch));
        // Private custody: the record names user SIDs, requested settings and
        // override digests, so it must not inherit the namespace's Users read.
        files.Publish(path, handback.Serialize(PendingOverrides()), privateCustody: true);
    }

    // manual-overrides.json.pending is preserved for review; only its digest is recorded.
    private List<NativeHandbackEvidence> PendingOverrides()
    {
        var result = new List<NativeHandbackEvidence>();
        if (usersDirectory is null || !Directory.Exists(usersDirectory)) { return result; }
        foreach (string user in Directory.EnumerateDirectories(usersDirectory).Order(StringComparer.Ordinal))
        {
            string sid = Path.GetFileName(user);
            string pending = Path.Combine(user, "manual-overrides.json.pending");
            if (!WindowsNativeHandback.IsSid(sid) || !File.Exists(pending)) { continue; }
            if (result.Count == WindowsNativeHandback.MaximumUsers) { throw new IOException("Pending native override evidence exceeds its bound."); }
            using var stream = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read);
            result.Add(new(sid, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        return result;
    }
}
