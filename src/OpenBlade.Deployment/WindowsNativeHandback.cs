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
    internal const int MaximumBytes = 65536;
    internal bool IsEmpty => Pending is null && OwnedFan is null && LiveTemporaryCharge is null && AbandonedPrepared is null && StagedJournal is null;
    internal string Commitment => NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeHandback.v1", Epoch,
        Line(Pending), Line(OwnedFan), Line(LiveTemporaryCharge), Line(AbandonedPrepared), StagedJournal ?? "no-staged-journal"));
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
            w.WriteString("commitment", Commitment);
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
    internal static (WindowsNativeHandback Handback, IReadOnlyList<NativeHandbackEvidence> PendingOverrides) Parse(byte[] bytes)
    {
        try { return ParseCore(bytes); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Native handback record is malformed.", error); }
    }
    private static (WindowsNativeHandback Handback, IReadOnlyList<NativeHandbackEvidence> PendingOverrides) ParseCore(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) { throw new InvalidDataException("Native handback record has an invalid size."); }
        using var document = JsonDocument.Parse(bytes);
        var e = document.RootElement;
        Members(e, "schema", "epoch", "commitment", "pending", "ownedFan", "liveTemporaryCharge", "abandonedPrepared", "stagedJournal", "pendingOverrides");
        if (e.GetProperty("schema").GetInt32() != 1) { throw Invalid(); }
        string epoch = Text(e, "epoch");
        string? staged = e.GetProperty("stagedJournal").ValueKind == JsonValueKind.Null ? null : Text(e, "stagedJournal");
        var handback = new WindowsNativeHandback(epoch, Item(e.GetProperty("pending")), Item(e.GetProperty("ownedFan")),
            Item(e.GetProperty("liveTemporaryCharge")), Item(e.GetProperty("abandonedPrepared")), staged);
        var overrides = new List<NativeHandbackEvidence>();
        foreach (var o in e.GetProperty("pendingOverrides").EnumerateArray())
        {
            Members(o, "userSid", "sha256");
            overrides.Add(new(Text(o, "userSid"), Text(o, "sha256")));
        }
        if (!NativeCanonical.IsHex(epoch, 32) || staged is not null && !NativeCanonical.IsHex(staged, 64) || handback.IsEmpty
            || Text(e, "commitment") != handback.Commitment || overrides.Count > MaximumUsers
            || overrides.Any(o => !IsSid(o.UserSid) || !NativeCanonical.IsHex(o.Sha256, 64)))
        { throw Invalid(); }
        return (handback, overrides);
    }
    internal const int MaximumUsers = 256;
    internal static bool IsSid(string value) => value.Length <= 184 && value.StartsWith("S-1-5-21-", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiDigit(c) || c is 'S' or '-');
    private static NativeHandbackItem? Item(JsonElement e)
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
            && item.Progress is >= 0 and <= 73 && item.Family switch
            {
                "performance" => item.Performance is "Balanced" or "Performance" && item.FanMode is null && battery is null,
                "fan" => item.FanMode is "Automatic" or "Fixed" or "Maximum" && item.Performance is null && battery is null,
                "battery" or "temporaryCharge" => battery is { Limit: >= 50 and <= 99 } && item.Performance is null && item.FanMode is null,
                _ => false,
            };
        return valid ? item : throw Invalid();
    }
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

// Setup-owned writer. The record is immutable: a rerun keeps an identical
// commitment and refuses a different one. Managed resolution lives elsewhere.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeHandbackWriter(string machineDataDirectory)
{
    internal const string DirectoryName = "native-handback";
    internal string Directory { get; } = Path.Combine(Path.GetFullPath(machineDataDirectory), DirectoryName);

    internal void Persist(WindowsNativeHandback handback)
    {
        string path = Path.Combine(Directory, handback.Epoch + ".json");
        if (File.Exists(path))
        {
            var existing = WindowsNativeHandback.Parse(ReadBounded(path)).Handback;
            if (existing.Epoch != handback.Epoch || existing.Commitment != handback.Commitment)
            { throw new IOException("A different native handback record is already preserved for this generation."); }
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        byte[] bytes = handback.Serialize(PendingOverrides());
        string staging = path + ".tmp";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(staging, path, overwrite: false);
        if (!ReadBounded(path).AsSpan().SequenceEqual(bytes)) { throw new IOException("Native handback record publication is unresolved."); }
    }

    // manual-overrides.json.pending is preserved for review; only its digest is recorded.
    private List<NativeHandbackEvidence> PendingOverrides()
    {
        string users = Path.Combine(Path.GetDirectoryName(Directory)!, "users");
        var result = new List<NativeHandbackEvidence>();
        if (!System.IO.Directory.Exists(users)) { return result; }
        foreach (string user in System.IO.Directory.EnumerateDirectories(users).Order(StringComparer.Ordinal))
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

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > WindowsNativeHandback.MaximumBytes) { throw new InvalidDataException("Native handback record has an invalid size."); }
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
