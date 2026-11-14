using System.Globalization;
using System.Text.Json;
namespace OpenBlade.Deployment;
// Canonical counterpart of portable battery_history.rs. No OS suppliers.
internal static class WindowsNativeBatteryHistory
{
    internal const string Contract = "service-battery-policy-v1/rz09-0581-v1";
    private static IOException Denied() => new("Battery history is incomplete or unsupported.");
    private static int Number(JsonElement e) { if (!e.TryGetInt32(out int v) || e.GetRawText() != v.ToString(CultureInfo.InvariantCulture)) { throw Denied(); } return v; }
    private static void Members(JsonElement e, params string[] names) { if (e.ValueKind != JsonValueKind.Object) { throw Denied(); } var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { if (!names.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name)) { throw Denied(); } } if (seen.Count != names.Length) { throw Denied(); } }
    private static bool Raw(int v) => v == 80 || v is >= 128 and <= 255 && Limit(v & 127);
    private static bool Limit(int v) => v is >= 50 and <= 80 && v % 5 == 0;
    private static (int Limit, bool Protection, bool Temporary) Desired(JsonElement d) { Members(d, "limit", "protection", "temporary"); var v = (Number(d.GetProperty("limit")), d.GetProperty("protection").GetBoolean(), d.GetProperty("temporary").GetBoolean()); if (v.Item1 is < 50 or > 99) { throw Denied(); } return v; }
    internal static byte[] Canonical(JsonElement b)
    {
        Members(b, "contract", "requested", "configured", "desired", "source", "baseline", "target", "temporary", "ids", "fan_owner");
        if (b.GetProperty("contract").GetString() != Contract) { throw Denied(); }
        var c = Desired(b.GetProperty("configured")); var d = Desired(b.GetProperty("desired"));
        var r = b.GetProperty("requested"); string op = r.GetProperty("operation").GetString() ?? throw Denied();
        if (op == "setBatteryPolicy") { Members(r, "operation", "value"); var v = r.GetProperty("value"); Members(v, "chargeLimitPercent"); int n = Number(v.GetProperty("chargeLimitPercent")); if (!Limit(n) || d != (n, c.Protection, c.Temporary)) { throw Denied(); } }
        else if (op == "setBatteryProtection") { Members(r, "operation", "enabled"); if (d != (c.Limit, r.GetProperty("enabled").GetBoolean(), c.Temporary)) { throw Denied(); } }
        else { throw Denied(); }
        if (d.Protection && !Limit(d.Limit) || Number(b.GetProperty("source")) is < 0 or > 2 || !Raw(Number(b.GetProperty("baseline"))) || Number(b.GetProperty("target")) != (d.Protection ? 128 | d.Limit : 80) || d.Temporary || b.GetProperty("temporary").GetBoolean()) { throw Denied(); }
        var ids = b.GetProperty("ids"); if (ids.GetArrayLength() != 14) { throw Denied(); }
        var seen = new HashSet<int>(); foreach (var id in ids.EnumerateArray()) { int n = Number(id); if (n is < 1 or > 254 || !seen.Add(n)) { throw Denied(); } }
        var fan = b.GetProperty("fan_owner"); if (fan.ValueKind != JsonValueKind.Null && !NativeCanonical.IsHex(fan.GetString() ?? "", 64)) { throw Denied(); }
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream))
        {
            void D(string key) { var v = Desired(b.GetProperty(key)); w.WritePropertyName(key); w.WriteStartObject(); w.WriteNumber("limit", v.Limit); w.WriteBoolean("protection", v.Protection); w.WriteBoolean("temporary", v.Temporary); w.WriteEndObject(); }
            w.WriteStartObject(); w.WriteString("contract", Contract); w.WritePropertyName("requested"); w.WriteStartObject(); w.WriteString("operation", op); if (op == "setBatteryPolicy") { w.WritePropertyName("value"); w.WriteStartObject(); w.WriteNumber("chargeLimitPercent", Number(r.GetProperty("value").GetProperty("chargeLimitPercent"))); w.WriteEndObject(); } else { w.WriteBoolean("enabled", r.GetProperty("enabled").GetBoolean()); }
            w.WriteEndObject(); D("configured"); D("desired"); foreach (string k in new[] { "source", "baseline", "target" }) { w.WriteNumber(k, Number(b.GetProperty(k))); }
            w.WriteBoolean("temporary", false); w.WritePropertyName("ids"); w.WriteStartArray(); foreach (var id in ids.EnumerateArray()) { w.WriteNumberValue(Number(id)); }
            w.WriteEndArray(); w.WritePropertyName("fan_owner"); fan.WriteTo(w); w.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static bool ValidProgress(byte p) => p is 0 or 6 or 60 or 61 or 62 or 63;
    internal static bool Follows(JsonElement b, byte p, byte n) => (p, n) is (0, 6) or (0, 60) or (60, 61) or (61, 62) || p == 0 && n == 63 && Number(b.GetProperty("baseline")) == Number(b.GetProperty("target"));
}
