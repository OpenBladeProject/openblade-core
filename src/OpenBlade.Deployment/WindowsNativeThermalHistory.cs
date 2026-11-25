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
    // exited is the terminal owner whose process exit was proven under custody.
    // Only its own unsettled tail and never-renamed staging are handed back.
    internal static WindowsNativeThermalHistory Read(WindowsNativeEpochFiles files, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners, WindowsNativeTrustLifecycle.OwnerReference? exited = null)
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
        // journal_replace renames .pending before its caller submits, so staging
        // is never-submitted evidence. Rollover recovery remains unsupported.
        byte[]? staged = Read("thermal-mutation.journal.pending", SegmentBytes + 2048);
        if (staged is not null && exited is null || Read("thermal-mutation.journal.rollover", SegmentBytes + 2048) is not null) { throw Denied(); }
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
        _ = result.Rows(tail, owners, requireSettled: exited is null);
        if (exited is not null) { result.Handback = result.Classify(exited, staged is null ? null : NativeCanonical.Digest(staged)); }
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
    private Row? liveTemporary;
    internal WindowsNativeHandback? Handback { get; private set; }
    private WindowsNativeHandback Classify(WindowsNativeTrustLifecycle.OwnerReference exited, string? staged)
    {
        // Chronology admits at most one unsettled row, always the last one.
        Row? open = frontier is { Progress: not (5 or 6 or 30 or 50 or 51 or 62 or 63 or 72 or 73) } last ? last : null;
        if (open is not null && open.Owner != exited.Nonce || ownedFan is not null && ownedFan.Owner != exited.Nonce) { throw Denied(); }
        // Prepared (0) and release preparation (31) precede any setter.
        bool prepared = open is { Progress: 0 or 31 };
        return new(exited.Epoch, open is not null && !prepared ? Item(open) : null, ownedFan is null ? null : Item(ownedFan),
            liveTemporary is not null && liveTemporary.Owner == exited.Nonce ? Item(liveTemporary) : null, prepared ? Item(open!) : null, staged);
    }
    private static NativeHandbackItem Item(Row row)
    {
        if ((row.Temporary ?? row.Battery) is { } family)
        {
            using var document = JsonDocument.Parse(family);
            var desired = document.RootElement.GetProperty("desired");
            return new(row.Request, row.Temporary is null ? "battery" : "temporaryCharge", row.Progress, row.Sid, null, null,
                new(desired.GetProperty("limit").GetInt32(), desired.GetProperty("protection").GetBoolean(), desired.GetProperty("temporary").GetBoolean()));
        }
        if (row.Fan is { } fan)
        {
            using var document = JsonDocument.Parse(fan);
            // Release preparation and release setters target firmware Automatic.
            int mode = row.Progress is 31 or (>= 40 and <= 43) ? 0 : document.RootElement.GetProperty("intent").GetProperty("mode").GetInt32();
            return new(row.Request, "fan", row.Progress, row.Sid, null, mode switch { 0 => "Automatic", 1 => "Fixed", 3 => "Maximum", _ => throw Denied() }, null);
        }
        return new(row.Request, "performance", row.Progress, row.Sid, PresetName(row.Destination) ?? throw Denied(), null, null);
    }
    private readonly HashSet<string> batteryGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> fanGenerations = new(StringComparer.Ordinal);
    private readonly HashSet<string> temporaryGenerations = new(StringComparer.Ordinal);
    private sealed record Row(string Request, string Owner, string Sid, uint Pid, uint Session, int Destination, string Intent, byte Progress, string? Fan, string? Battery, string? Temporary);
    // Each segment ends positively settled; chronology cannot be split by rolling.
    private ulong Rows(byte[] bytes, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners, bool requireSettled)
    {
        if (bytes.Length > SegmentBytes || bytes.Length != 0 && bytes[^1] != 10) { throw Denied(); }
        Row? last = frontier;
        Row? temporaryOwner = null;
        // The automatic cancel that replaced the live enable's reserved IDs; no other
        // automatic cancel of that enable may follow it (thermal_history.rs replaced_by).
        string? replacedBy = null;
        ulong count = 0;
        int position = 0;
        while (position < bytes.Length)
        {
            int length = bytes.AsSpan(position).IndexOf((byte)10) + 1;
            if (length is < 2 or > RowBytes) { throw Denied(); }
            byte[] raw = bytes[position..(position + length)]; position += length;
            using var document = JsonDocument.Parse(raw);
            JsonElement e = document.RootElement;
            Members(e, ["request", "owner", "user_sid", "caller_pid", "session", "requested", "destination", "intent", "progress", "fan", "battery", "tfc"]);
            string Text(string key) => e.GetProperty(key).GetString() ?? throw Denied();
            byte[]? fan = e.TryGetProperty("fan", out var f) ? WindowsNativeFanHistory.Canonical(f) : null;
            byte[]? battery = e.TryGetProperty("battery", out var b) ? WindowsNativeBatteryHistory.Canonical(b) : null;
            byte[]? temporary = e.TryGetProperty("tfc", out var t) ? WindowsNativeTemporaryChargeHistory.Canonical(t) : null;
            if (temporary is not null && (e.TryGetProperty("fan", out _) || e.TryGetProperty("battery", out _))) { throw Denied(); }
            var row = new Row(Text("request"), Text("owner"), Text("user_sid"), e.GetProperty("caller_pid").GetUInt32(), e.GetProperty("session").GetUInt32(), e.GetProperty("destination").GetInt32(), Text("intent"), e.GetProperty("progress").GetByte(), fan is null ? null : Encoding.UTF8.GetString(fan), battery is null ? null : Encoding.UTF8.GetString(battery), temporary is null ? null : Encoding.UTF8.GetString(temporary));
            if (!Guid.TryParseExact(row.Request, "D", out var request) || request == Guid.Empty || !NativeCanonical.IsHex(row.Owner, 64) || !NativeCanonical.IsHex(row.Intent, 64)
                || !row.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || row.Sid.Any(c => !char.IsAsciiDigit(c) && c is not ('S' or '-'))
                || row.Pid == 0 || row.Session == 0 || (temporary is not null ? fan is not null || battery is not null || row.Destination != 0 || !WindowsNativeTemporaryChargeHistory.ValidProgress(row.Progress) : battery is not null ? fan is not null || row.Destination != 0 || !WindowsNativeBatteryHistory.ValidProgress(row.Progress) : fan is null ? !ValidPerformanceProgress(row.Destination, row.Progress) : row.Destination != 0 || !WindowsNativeFanHistory.ValidProgress(f, row.Progress))) { throw Denied(); }
            JsonElement requested = e.TryGetProperty("requested", out var v) ? v : default;
            if ((fan is not null || battery is not null || temporary is not null) && requested.ValueKind != JsonValueKind.Null || NativeCanonical.Digest(temporary ?? battery ?? fan ?? Intent(requested)) != row.Intent) { throw Denied(); }
            if (temporary is not null && t.GetProperty("origin").ValueKind != JsonValueKind.Null)
            {
                var origin = t.GetProperty("origin");
                if (temporaryOwner is null || origin.GetProperty("request").GetString() != temporaryOwner.Request || origin.GetProperty("intent").GetString() != temporaryOwner.Intent
                    || row.Owner != temporaryOwner.Owner || row.Sid != temporaryOwner.Sid || row.Pid != temporaryOwner.Pid || row.Session != temporaryOwner.Session) { throw Denied(); }
                using var enabled = JsonDocument.Parse(temporaryOwner.Temporary ?? throw Denied());
                // Reserved IDs bind the cancel itself; fresh IDs name the reserved set they replace.
                bool fresh = t.TryGetProperty("superseded_ids", out var superseded);
                if (!enabled.RootElement.GetProperty("cancel_ids").EnumerateArray().Select(v => v.GetByte()).SequenceEqual((fresh ? superseded : t.GetProperty("ids")).EnumerateArray().Select(v => v.GetByte()))
                    || replacedBy is not null && replacedBy != row.Request) { throw Denied(); }
                if (fresh) { replacedBy ??= row.Request; }
            }
            bool coupled = false;
            if (battery is not null || temporary is not null)
            {
                var family = temporary is not null ? t : b;
                string? expected = null;
                if (ownedFan is not null)
                {
                    using var ownerDoc = JsonDocument.Parse(ownedFan.Fan ?? throw Denied()); var ownerFan = ownerDoc.RootElement;
                    coupled = ownerFan.GetProperty("contract").GetString() == WindowsNativeFanHistory.CoexistenceContract;
                    expected = NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.BatteryFanOwner.v1", ownedFan.Request, ownedFan.Owner, ownedFan.Sid, ownedFan.Pid.ToString(CultureInfo.InvariantCulture), ownedFan.Session.ToString(CultureInfo.InvariantCulture), NativeCanonical.Digest(WindowsNativeFanHistory.Canonical(ownerFan))));
                    var ids = family.GetProperty("ids").EnumerateArray().Select(v => v.GetByte()).ToHashSet();
                    if (temporary is not null && t.GetProperty("cancel_ids").ValueKind != JsonValueKind.Null) { ids.UnionWith(t.GetProperty("cancel_ids").EnumerateArray().Select(v => v.GetByte())); }
                    foreach (string k in new[] { "preflight", "ids", "cleanup" }) { foreach (var pair in ownerFan.GetProperty(k).EnumerateArray()) { foreach (var id in pair.EnumerateArray()) { if (ids.Contains(id.GetByte())) { throw Denied(); } } } }
                }
                if (family.GetProperty("fan_owner").GetString() != expected) { throw Denied(); }
            }
            if (ownedFan is not null && (row.Owner != ownedFan.Owner || row.Sid != ownedFan.Sid || row.Session != ownedFan.Session || fan is null && !coupled)) { throw Denied(); }
            bool resumedRelease = row.Progress is 31 or 51 && ownedFan is not null && ownedFan with { Progress = row.Progress } == row;
            if (last is not null && last.Request == row.Request)
            {
                if (last with { Progress = row.Progress } != row || !(temporary is not null ? WindowsNativeTemporaryChargeHistory.Follows(t, last.Progress, row.Progress) : battery is not null ? WindowsNativeBatteryHistory.Follows(b, last.Progress, row.Progress) : fan is null ? PerformanceFollows(row.Destination, last.Progress, row.Progress) : WindowsNativeFanHistory.Follows(f, last.Progress, row.Progress))) { throw Denied(); }
            }
            else if (last is not null && last.Progress is not (5 or 6 or 30 or 50 or 51 or 62 or 63 or 72 or 73) || !resumedRelease && (row.Progress != 0 || last?.Progress == 30 && (fan is null && !coupled || row.Owner != last.Owner || row.Sid != last.Sid || row.Session != last.Session))) { throw Denied(); }
            if (fan is not null) { if (row.Progress == 30) { ownedFan = row; } else if (row.Progress is 50 or 51) { ownedFan = null; } }
            var matches = owners.Where(o => o.Nonce == row.Owner).ToArray();
            if (matches.Length != 1) { throw Denied(); }
            var original = matches[0];
            string key = original.Epoch;
            if (temporary is not null) { temporaryGenerations.Add(key); }
            if (fan is not null) { fanGenerations.Add(key); }
            if (battery is not null || fan is not null && f.GetProperty("contract").GetString() == WindowsNativeFanHistory.CoexistenceContract) { batteryGenerations.Add(key); }
            string prior = generations.GetValueOrDefault(key) ?? Empty(original.Installation, original.Epoch, original.Service);
            generations[key] = NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.ThermalGenerationRow.v1", prior, NativeCanonical.Digest(raw), original.Active));
            if (temporary is not null && row.Progress is 72 or 73) { temporaryOwner = t.GetProperty("desired").GetProperty("temporary").GetBoolean() ? row : null; replacedBy = null; }
            last = row; count = checked(count + 1);
        }
        if (requireSettled && (ownedFan is not null || last is not null && last.Progress is not (5 or 6 or 50 or 51 or 62 or 63 or 72 or 73))) { throw Denied(); }
        frontier = last;
        liveTemporary = temporaryOwner;
        return count;
    }
    internal static string Empty(string installation, string epoch, string service) => NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.ThermalGeneration.v1", installation, epoch, service));
    internal string Generation(NativeEpochRecord record, NativeArtifact service, IReadOnlyList<WindowsNativeTrustLifecycle.OwnerReference> owners, IReadOnlyDictionary<string, byte[]> launches)
    {
        if (temporaryGenerations.Contains(record.Epoch) && !service.TemporaryChargeMutation) { throw Denied(); }
        if (batteryGenerations.Contains(record.Epoch) && !service.BatteryMutation) { throw Denied(); }
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
    // The native RZ09-0581 destinations, by managed PerformancePreset wire
    // value and name. The unadmitted presets are not native rows.
    internal static string? PresetName(int value) => value switch
    {
        0 => "Silent",
        1 => "Balanced",
        2 => "Performance",
        3 => "BatterySaver",
        4 => "BatteryBalanced",
        Custom => "Custom",
        _ => null,
    };
    private const int Custom = 5;
    // thermal_mutation.rs Progress::chain: each destination's setter chains. A
    // preset is one pair (0, 1-4, 5); Custom adds the 0D/07 CPU and GPU level
    // setters (7-10) before its final read. Leaving HyperBoost for a battery
    // preset (3 or 4) first writes the restore pair (80-83), then its own pair.
    // Prepared (0) may also retire as 6.
    private static readonly byte[] PairChain = [0, 1, 2, 3, 4, 5];
    private static readonly byte[] CustomChain = [0, 1, 2, 3, 4, 7, 8, 9, 10, 5];
    private static readonly byte[] RestoreChain = [0, 80, 81, 82, 83, 1, 2, 3, 4, 5];
    private static readonly byte[][] PairChains = [PairChain];
    private static readonly byte[][] CustomChains = [CustomChain];
    private static readonly byte[][] BatteryChains = [PairChain, RestoreChain];
    private static byte[][]? Chains(int destination) => destination switch
    {
        Custom => CustomChains,
        3 or 4 => BatteryChains,
        _ => PresetName(destination) is null ? null : PairChains,
    };
    internal static bool ValidPerformanceProgress(int destination, byte progress) => Chains(destination) is { } chains
        && (progress == 6 || chains.Any(chain => chain.Contains(progress)));
    internal static bool PerformanceFollows(int destination, byte prior, byte next) => Chains(destination) is { } chains
        && (prior == 0 && next == 6 || chains.Any(chain => Array.IndexOf(chain, prior) is int at && at >= 0 && at + 1 < chain.Length && chain[at + 1] == next));
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
        var custom = requested.TryGetProperty("customPerformance", out var c) && c.ValueKind != JsonValueKind.Null ? c : default;
        // thermal_history.rs PerformanceOverride::validate: Custom carries its
        // settings and a preset none.
        if (PresetName(value) is null || lifetime > 1 || (value == Custom) != (custom.ValueKind != JsonValueKind.Undefined)) { throw Denied(); }
        var gpu = requested.TryGetProperty("gpuClockTuning", out var g) ? g : default;
        if (custom.ValueKind != JsonValueKind.Undefined)
        {
            // validate_custom_performance: canonical integer levels (CPU Low 0
            // to Boost 3, GPU Low 0 to High 2), an optional maximum-fan flag and
            // optional legacy offsets, which GPU clock tuning excludes.
            Members(custom, ["cpu", "gpu", "maximumFans", "gpuClockOffsets"]);
            foreach ((string k, ulong max) in new[] { ("cpu", 3UL), ("gpu", 2UL) })
            {
                if (!custom.TryGetProperty(k, out var level) || !Canonical(level, out long n) || n < 0 || (ulong)n > max) { throw Denied(); }
            }
            if (custom.TryGetProperty("maximumFans", out var fans) && fans.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw Denied(); }
            if (custom.TryGetProperty("gpuClockOffsets", out var legacy) && legacy.ValueKind != JsonValueKind.Null)
            {
                Members(legacy, ["coreMegahertz", "memoryMegahertz"]);
                foreach (string k in new[] { "coreMegahertz", "memoryMegahertz" })
                {
                    if (!legacy.TryGetProperty(k, out var o) || !Canonical(o, out long n) || n is < -10000 or > 10000) { throw Denied(); }
                }
                if (gpu.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) { throw Denied(); }
            }
        }
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
            void Sorted(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Sorted(p.Value); } writer.WriteEndObject(); }
                else if (e.ValueKind == JsonValueKind.Number) { writer.WriteNumberValue(e.GetInt64()); }
                else { e.WriteTo(writer); }
            }
            writer.WriteStartObject(); writer.WriteNumber("value", value); writer.WriteNumber("lifetime", lifetime);
            writer.WritePropertyName("customPerformance");
            if (custom.ValueKind == JsonValueKind.Undefined) { writer.WriteNullValue(); } else { Sorted(custom); }
            writer.WritePropertyName("gpuClockTuning");
            if (gpu.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { writer.WriteNullValue(); } else { Sorted(gpu); }
            writer.WriteEndObject();
        }
        return memory.ToArray();
    }
    // A JSON integer in its canonical form, as serde_json writes it back.
    private static bool Canonical(JsonElement e, out long n)
    {
        n = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out n) && e.GetRawText() == n.ToString(CultureInfo.InvariantCulture);
    }
}
