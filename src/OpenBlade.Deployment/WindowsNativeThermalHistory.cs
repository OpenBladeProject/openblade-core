using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment;

// Same portable grammar as thermal_history.rs. Called only with owner writer
// and original generation custody held. Checkpoints never replace raw evidence.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeThermalHistory
{
    internal const int SegmentBytes = 1024 * 1024;
    internal const int RowBytes = 16384;
    internal const string Contract = "service-thermal-mutation-v1/rows-v1";
    private readonly Dictionary<string, string> generations = new(StringComparer.Ordinal);
    internal IEnumerable<string> Epochs => generations.Keys;
    internal bool Present { get; private set; }
    internal string Commitment { get; private set; } = "";
    internal static bool IsNamespace(string name) => name is "thermal-history" or "thermal-mutation.journal" or "thermal-mutation.journal.pending" or "thermal-mutation.journal.rollover";
    internal static string Name(string kind, ulong sequence) => $"{kind}-{sequence:D20}.bin";
    internal static byte[] Envelope(ulong sequence, string head, byte[] tail) => [.. NativeCanonical.Lines("OpenBlade.ThermalActive.v1", sequence.ToString(CultureInfo.InvariantCulture), head, Contract), 10, .. tail];
    private static ulong Number(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) || value != n.ToString(CultureInfo.InvariantCulture)) { throw Denied(); }
        return n;
    }
    private static IOException Denied() => new("Thermal history is incomplete, unsettled, unsupported or differs from its original producer.");
    internal static WindowsNativeThermalHistory Read(WindowsNativeEpochFiles files, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners)
    {
        var result = new WindowsNativeThermalHistory();
        string owner = Path.Combine(files.Root, "owner");
        byte[]? Read(string name, int max)
        {
            string path = Path.Combine(owner, name);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            using var held = files.OpenFile(path);
            return WindowsNativeEpochFiles.ReadBounded(held, max);
        }
        if (Read("thermal-mutation.journal.pending", SegmentBytes + 2048) is not null || Read("thermal-mutation.journal.rollover", SegmentBytes + 2048) is not null) { throw Denied(); }
        byte[]? physical = Read("thermal-mutation.journal", SegmentBytes + 2048);
        byte[] tail = physical ?? [];
        ulong sequence = 0;
        string head = new('0', 64);
        if (tail.AsSpan().StartsWith("OpenBlade.ThermalActive."u8))
        {
            int split = tail.AsSpan().IndexOf("\n\n"u8);
            if (split < 0) { throw Denied(); }
            var f = NativeCanonical.ParseLines(tail[..(split + 1)], "OpenBlade.ThermalActive.v1", 4);
            sequence = Number(f[0]); head = f[1];
            if (sequence == 0 || !NativeCanonical.IsHex(head, 64) || f[2] != Contract) { throw Denied(); }
            tail = tail[(split + 2)..];
        }
        result.Present = physical is not null;
        string previous = new('0', 64);
        string directory = Path.Combine(owner, "thermal-history");
        bool hasDirectory = WindowsNativeEpochFiles.IsPresentDirectory(directory);
        using var pinned = hasDirectory ? files.RetainDirectory(directory) : null;
        if (sequence > 0 && !hasDirectory) { throw Denied(); }
        for (ulong n = 1; n <= sequence; n = checked(n + 1))
        {
            byte[] descriptor = Read(Path.Combine("thermal-history", Name("checkpoint", n)), 2048) ?? throw Denied();
            var f = NativeCanonical.ParseLines(descriptor, "OpenBlade.ThermalCheckpoint.v1", 7);
            byte[] segment = Read(Path.Combine("thermal-history", Name("segment", n)), SegmentBytes) ?? throw Denied();
            ulong rows = result.Rows(segment, owners, requireSettled: true);
            if (Number(f[0]) != n || f[1] != previous || f[2] != NativeCanonical.Digest(segment) || Number(f[3]) != (ulong)segment.Length || Number(f[4]) != rows || rows == 0 || f[5] != Contract) { throw Denied(); }
            byte[] roll = Read(Path.Combine("thermal-history", Name("roll", n)), 2048) ?? throw Denied();
            byte[] old = n == 1 ? segment : Envelope(n - 1, previous, segment);
            byte[] next = Envelope(n, NativeCanonical.Digest(descriptor), []);
            if (!roll.AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.ThermalRoll.v1", NativeCanonical.Digest(old), NativeCanonical.Digest(next), NativeCanonical.Digest(descriptor), Contract))) { throw Denied(); }
            previous = NativeCanonical.Digest(descriptor);
        }
        if (head != previous) { throw Denied(); }
        _ = result.Rows(tail, owners, requireSettled: true);
        if (hasDirectory)
        {
            ulong count = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(path);
                int split = name.IndexOf('-');
                if (split < 0 || name.Length != split + 25 || !name.EndsWith(".bin", StringComparison.Ordinal)) { throw Denied(); }
                string kind = name[..split];
                if (kind is not ("segment" or "checkpoint" or "roll") || !ulong.TryParse(name.AsSpan(split + 1, 20), NumberStyles.None, CultureInfo.InvariantCulture, out ulong n)
                    || n == 0 || n > sequence || name != Name(kind, n)) { throw Denied(); }
                count = checked(count + 1);
            }
            if (count != checked(sequence * 3)) { throw Denied(); }
        }
        result.Commitment = NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.ThermalHistory.v1", sequence.ToString(CultureInfo.InvariantCulture), head,
            physical is null ? "known-empty" : NativeCanonical.Digest(physical)));
        return result;
    }
    private Row? frontier;
    private Row? ownedFan;
    private readonly HashSet<string> fanGenerations = new(StringComparer.Ordinal);
    private sealed record Row(string Request, string Owner, string Sid, uint Pid, uint Session, int Destination, string Intent, byte Progress, string? Fan);
    // Each segment ends positively settled; chronology cannot be split by rolling.
    private ulong Rows(byte[] bytes, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners, bool requireSettled)
    {
        if (bytes.Length > SegmentBytes || bytes.Length != 0 && bytes[^1] != 10) { throw Denied(); }
        Row? last = frontier;
        ulong count = 0;
        int position = 0;
        while (position < bytes.Length)
        {
            int length = bytes.AsSpan(position).IndexOf((byte)10) + 1;
            if (length is < 2 or > RowBytes) { throw Denied(); }
            byte[] raw = bytes[position..(position + length)]; position += length;
            using var document = JsonDocument.Parse(raw);
            JsonElement e = document.RootElement;
            Members(e, ["request", "owner", "user_sid", "caller_pid", "session", "requested", "destination", "intent", "progress", "fan"]);
            string Text(string key) => e.GetProperty(key).GetString() ?? throw Denied();
            byte[]? fan = e.TryGetProperty("fan", out var f) ? WindowsNativeFanHistory.Canonical(f) : null;
            var row = new Row(Text("request"), Text("owner"), Text("user_sid"), e.GetProperty("caller_pid").GetUInt32(), e.GetProperty("session").GetUInt32(), e.GetProperty("destination").GetInt32(), Text("intent"), e.GetProperty("progress").GetByte(), fan is null ? null : Encoding.UTF8.GetString(fan));
            if (!Guid.TryParseExact(row.Request, "D", out var request) || request == Guid.Empty || !NativeCanonical.IsHex(row.Owner, 64) || !NativeCanonical.IsHex(row.Intent, 64)
                || !row.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || row.Sid.Any(c => !char.IsAsciiDigit(c) && c is not ('S' or '-'))
                || row.Pid == 0 || row.Session == 0 || (fan is null ? row.Destination is not (1 or 2) || row.Progress > 6 : row.Destination != 0 || !WindowsNativeFanHistory.ValidProgress(f, row.Progress))) { throw Denied(); }
            JsonElement requested = e.TryGetProperty("requested", out var v) ? v : default;
            if (fan is not null && requested.ValueKind != JsonValueKind.Null || NativeCanonical.Digest(fan ?? Intent(requested)) != row.Intent) { throw Denied(); }
            if (ownedFan is not null && (row.Owner != ownedFan.Owner || fan is null)) { throw Denied(); }
            bool resumedRelease = row.Progress == 31 && ownedFan is not null && ownedFan with { Progress = 31 } == row;
            if (last is not null && last.Request == row.Request)
            {
                if (last with { Progress = row.Progress } != row || !(fan is null ? last.Progress < 5 && row.Progress == last.Progress + 1 || last.Progress == 0 && row.Progress == 6 : WindowsNativeFanHistory.Follows(f, last.Progress, row.Progress))) { throw Denied(); }
            }
            else if (last is not null && last.Progress is not (5 or 6 or 30 or 50) || !resumedRelease && row.Progress != 0) { throw Denied(); }
            if (fan is not null) { if (row.Progress == 30) { ownedFan = row; } else if (row.Progress == 50) { ownedFan = null; } }
            var matches = owners.Where(o => o.Nonce == row.Owner).ToArray();
            if (matches.Length != 1) { throw Denied(); }
            var original = matches[0];
            string key = original.Epoch;
            if (fan is not null) { fanGenerations.Add(key); }
            string prior = generations.GetValueOrDefault(key) ?? Empty(original.Installation, original.Epoch, original.Service);
            generations[key] = NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.ThermalGenerationRow.v1", prior, NativeCanonical.Digest(raw), original.Active));
            last = row; count = checked(count + 1);
        }
        if (requireSettled && (ownedFan is not null || last is not null && last.Progress is not (5 or 6 or 50))) { throw Denied(); }
        frontier = last;
        return count;
    }
    internal static string Empty(string installation, string epoch, string service) => NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.ThermalGeneration.v1", installation, epoch, service));
    internal string Generation(NativeEpochRecord record, NativeArtifact service, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners, IReadOnlyDictionary<string, byte[]> launches)
    {
        if (fanGenerations.Contains(record.Epoch) && !service.FanMutation) { throw Denied(); }
        if (service.Id != record.ServiceId) { throw Denied(); }
        var original = owners.Where(o => o.Epoch == record.Epoch).ToArray();
        foreach (var owner in original.Where(_ => service.ThermalMutation || generations.ContainsKey(record.Epoch)))
        {
            if (owner.Installation != record.Installation || owner.Service != record.ServiceId || !launches.ContainsKey(owner.Process.Replace(':', '-') + ".bin")) { throw Denied(); }
        }
        if (generations.ContainsKey(record.Epoch) && (!service.ThermalMutation || original.Length == 0)) { throw Denied(); }
        return generations.GetValueOrDefault(record.Epoch) ?? Empty(record.Installation, record.Epoch, record.ServiceId);
    }
    private static void Members(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw Denied(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject()) { if (!allowed.Contains(p.Name, StringComparer.Ordinal) || !names.Add(p.Name)) { throw Denied(); } }
    }
    internal static byte[] Intent(JsonElement requested)
    {
        if (requested.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return "null"u8.ToArray(); }
        Members(requested, ["value", "lifetime", "customPerformance", "gpuClockTuning"]);
        int value = requested.GetProperty("value").GetInt32();
        byte lifetime = requested.GetProperty("lifetime").GetByte();
        if (value is not (1 or 2) || lifetime > 1 || requested.TryGetProperty("customPerformance", out var custom) && custom.ValueKind != JsonValueKind.Null) { throw Denied(); }
        var gpu = requested.TryGetProperty("gpuClockTuning", out var g) ? g : default;
        if (gpu.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            Members(gpu, ["enabled", "offsets", "effectiveOffsets"]);
            bool enabled = gpu.GetProperty("enabled").GetBoolean();
            var offsets = gpu.GetProperty("offsets");
            void Offsets(JsonElement o)
            {
                Members(o, ["coreMegahertz", "memoryMegahertz"]);
                foreach (string k in new[] { "coreMegahertz", "memoryMegahertz" }) { if (o.GetProperty(k).GetInt64() is < -10000 or > 10000) { throw Denied(); } }
            }
            Offsets(offsets);
            if (gpu.TryGetProperty("effectiveOffsets", out var effective))
            {
                Offsets(effective);
                foreach (string k in new[] { "coreMegahertz", "memoryMegahertz" }) { if (effective.GetProperty(k).GetInt64() != (enabled ? offsets.GetProperty(k).GetInt64() : 0)) { throw Denied(); } }
            }
        }
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteNumber("value", value); writer.WriteNumber("lifetime", lifetime); writer.WriteNull("customPerformance");
            writer.WritePropertyName("gpuClockTuning");
            void Sorted(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Sorted(p.Value); } writer.WriteEndObject(); }
                else if (e.ValueKind == JsonValueKind.Number) { writer.WriteNumberValue(e.GetInt64()); }
                else { e.WriteTo(writer); }
            }
            if (gpu.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { writer.WriteNullValue(); } else { Sorted(gpu); }
            writer.WriteEndObject();
        }
        return memory.ToArray();
    }
}
