using System.Globalization;
using System.Text.Json;

namespace OpenBlade.Deployment;

// Canonical counterpart of temporary_charge_history.rs. No Windows suppliers.
internal static class WindowsNativeTemporaryChargeHistory
{
    // No release has shipped this contract yet, so superseded_ids was added to it
    // without a new name. The first release that ships a temporary-charge reader
    // must include that member in both readers; otherwise adding it needs a new
    // contract and capability (docs/windows-rust-thermal-history.md).
    internal const string Contract = "service-temporary-charge-v1/rz09-0581-v1";
    private static IOException Denied() => new("Temporary charge history is incomplete or unsupported.");
    private static int Number(JsonElement e)
    {
        if (!e.TryGetInt32(out int n) || e.GetRawText() != n.ToString(CultureInfo.InvariantCulture)) { throw Denied(); }
        return n;
    }
    private static void Members(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) { throw Denied(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) { if (!names.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name)) { throw Denied(); } }
        if (seen.Count != names.Length) { throw Denied(); }
    }
    private static (int Limit, bool Protection, bool Temporary) Desired(JsonElement e)
    {
        Members(e, "limit", "protection", "temporary");
        int limit = Number(e.GetProperty("limit"));
        if (limit is < 50 or > 99) { throw Denied(); }
        return (limit, e.GetProperty("protection").GetBoolean(), e.GetProperty("temporary").GetBoolean());
    }
    private static bool Policy(int p) => p == 80 || p is >= 128 and <= 255 && (p & 127) is >= 50 and <= 80 && (p & 127) % 5 == 0;
    internal static byte[] Canonical(JsonElement t)
    {
        // superseded_ids is present only on an automatic cancel that replaced its
        // enable's reserved IDs; a reader without it refuses the member.
        bool replaced = t.TryGetProperty("superseded_ids", out var superseded);
        string[] members = ["contract", "requested", "configured", "desired", "source", "baseline", "target", "policy", "ids", "cancel_ids", "origin", "fan_owner"];
        Members(t, replaced ? [.. members, "superseded_ids"] : members);
        if (t.GetProperty("contract").GetString() != Contract) { throw Denied(); }
        var r = t.GetProperty("requested"); Members(r, "operation", "enabled");
        if (r.GetProperty("operation").GetString() != "setFullChargeOverride") { throw Denied(); }
        bool enabled = r.GetProperty("enabled").GetBoolean();
        var c = Desired(t.GetProperty("configured")); var d = Desired(t.GetProperty("desired"));
        int source = Number(t.GetProperty("source")); int baseline = Number(t.GetProperty("baseline")); int target = Number(t.GetProperty("target"));
        var policy = t.GetProperty("policy"); var cancel = t.GetProperty("cancel_ids"); var origin = t.GetProperty("origin");
        if (source is < 0 or > 2 || baseline is not (0 or 4) || target != (enabled ? 4 : 0) || d != (c.Limit, c.Protection, enabled)
            || policy.ValueKind != JsonValueKind.Null && !Policy(Number(policy))
            || enabled && (source == 1 || !c.Protection || policy.ValueKind == JsonValueKind.Null || Number(policy) == 80 || cancel.ValueKind == JsonValueKind.Null || origin.ValueKind != JsonValueKind.Null)
            || !enabled && cancel.ValueKind != JsonValueKind.Null
            || replaced && (enabled || origin.ValueKind == JsonValueKind.Null)) { throw Denied(); }
        var seen = new HashSet<int>();
        void Ids(JsonElement ids)
        {
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 8) { throw Denied(); }
            foreach (var id in ids.EnumerateArray()) { int n = Number(id); if (n is < 1 or > 254 || !seen.Add(n)) { throw Denied(); } }
        }
        // Fresh IDs share none with the reserved IDs they replace.
        Ids(t.GetProperty("ids")); if (cancel.ValueKind != JsonValueKind.Null) { Ids(cancel); }
        if (replaced) { Ids(superseded); }
        if (origin.ValueKind != JsonValueKind.Null)
        {
            Members(origin, "request", "intent");
            if (source != 1 || !Guid.TryParseExact(origin.GetProperty("request").GetString(), "D", out var request) || request == Guid.Empty || !NativeCanonical.IsHex(origin.GetProperty("intent").GetString() ?? "", 64)) { throw Denied(); }
        }
        var fan = t.GetProperty("fan_owner");
        if (fan.ValueKind != JsonValueKind.Null && !NativeCanonical.IsHex(fan.GetString() ?? "", 64)) { throw Denied(); }
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            void D(string key)
            {
                var v = Desired(t.GetProperty(key)); w.WritePropertyName(key); w.WriteStartObject();
                w.WriteNumber("limit", v.Limit); w.WriteBoolean("protection", v.Protection); w.WriteBoolean("temporary", v.Temporary); w.WriteEndObject();
            }
            w.WriteStartObject(); w.WriteString("contract", Contract); w.WritePropertyName("requested"); w.WriteStartObject();
            w.WriteString("operation", "setFullChargeOverride"); w.WriteBoolean("enabled", enabled); w.WriteEndObject();
            D("configured"); D("desired"); w.WriteNumber("source", source); w.WriteNumber("baseline", baseline); w.WriteNumber("target", target);
            w.WritePropertyName("policy"); policy.WriteTo(w);
            foreach (string key in new[] { "ids", "cancel_ids" }) { w.WritePropertyName(key); t.GetProperty(key).WriteTo(w); }
            if (replaced) { w.WritePropertyName("superseded_ids"); superseded.WriteTo(w); }
            w.WritePropertyName("origin");
            if (origin.ValueKind == JsonValueKind.Null) { w.WriteNullValue(); }
            else { w.WriteStartObject(); w.WriteString("request", origin.GetProperty("request").GetString()); w.WriteString("intent", origin.GetProperty("intent").GetString()); w.WriteEndObject(); }
            w.WritePropertyName("fan_owner"); fan.WriteTo(w); w.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static bool ValidProgress(byte p) => p is 0 or 6 or 70 or 71 or 72 or 73;
    internal static bool Follows(JsonElement t, byte p, byte n) => (p, n) is (0, 6) or (70, 71) or (71, 72)
        || p == 0 && n == 70 && Number(t.GetProperty("baseline")) != Number(t.GetProperty("target"))
        || p == 0 && n == 73 && Number(t.GetProperty("baseline")) == Number(t.GetProperty("target"));
}
