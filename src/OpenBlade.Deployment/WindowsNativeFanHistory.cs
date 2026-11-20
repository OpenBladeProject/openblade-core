using System.Text.Json;

namespace OpenBlade.Deployment;

// Versioned counterpart of fan_history.rs. Raw rows remain in their original
// producer chain; canonical recipe bytes bind intent, commands and retained IDs.
internal static class WindowsNativeFanHistory
{
    internal const string LegacyContract = "service-fan-mutation-v2/rz09-0581-linked-v1";
    internal const string CoexistenceContract = "service-battery-policy-v1/rz09-0581-fan-linked-v3";
    internal const string Contract = "service-fan-mutation-v2/rz09-0581-linked-v2";
    private static IOException Denied() => new("Fan history is invalid or unsupported.");
    private static void Members(JsonElement e, params string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object) { throw Denied(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) { if (!keys.Contains(p.Name, StringComparer.Ordinal) || !names.Add(p.Name)) { throw Denied(); } }
        if (names.Count != keys.Length) { throw Denied(); }
    }
    private static int Number(JsonElement e) { if (!e.TryGetInt32(out int n) || e.GetRawText() != n.ToString(System.Globalization.CultureInfo.InvariantCulture)) { throw Denied(); } return n; }
    private static bool Speed(JsonElement e) => e.TryGetInt32(out int n) && n is >= 2000 and <= 5400 && n % 100 == 0;
    private static void Settings(JsonElement e)
    {
        Members(e, "mode", "fixedSpeedRpm", "manualCurve");
        if (Number(e.GetProperty("mode")) is < 0 or > 3 || !Speed(e.GetProperty("fixedSpeedRpm"))) { throw Denied(); }
        var c = e.GetProperty("manualCurve"); Members(c, "cpu", "gpu", "useHigherTemperatureForBoth");
        _ = c.GetProperty("useHigherTemperatureForBoth").GetBoolean();
        foreach (string k in new[] { "cpu", "gpu" })
        {
            var points = c.GetProperty(k); if (points.GetArrayLength() is < 2 or > 10) { throw Denied(); }
            int priorT = 0, priorR = 0;
            foreach (var p in points.EnumerateArray())
            {
                Members(p, "temperatureCelsius", "speedRpm"); int t = Number(p.GetProperty("temperatureCelsius")); int rpm = Number(p.GetProperty("speedRpm"));
                if (t is < 20 or > 100 || !Speed(p.GetProperty("speedRpm")) || t <= priorT || rpm < priorR) { throw Denied(); }
                priorT = t; priorR = rpm;
            }
        }
    }
    private static void Requested(JsonElement e)
    {
        Members(e, "value", "settings"); var value = e.GetProperty("value"); var settings = e.GetProperty("settings");
        if (value.ValueKind == JsonValueKind.Null) { if (settings.ValueKind != JsonValueKind.Null) { throw Denied(); } return; }
        Members(value, "value", "lifetime");
        if (Number(value.GetProperty("value")) is < 0 or > 3 || Number(value.GetProperty("lifetime")) is < 0 or > 1) { throw Denied(); }
        if (settings.ValueKind != JsonValueKind.Null) { Settings(settings); if (Number(settings.GetProperty("mode")) != Number(value.GetProperty("value"))) { throw Denied(); } }
    }
    // A return to Automatic that writes Custom records its CPU and GPU levels: the
    // RZ09-0581 Custom range, Low (0) to High (2) except High/High. Absent otherwise,
    // so earlier records keep their bytes and digest; an explicit null is refused.
    private static int[]? Custom(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("custom", out var c)) { return null; }
        if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 2) { throw Denied(); }
        int[] levels = [Number(c[0]), Number(c[1])];
        if (levels[0] is < 0 or > 2 || levels[1] is < 0 or > 2 || levels is [2, 2]) { throw Denied(); }
        return levels;
    }
    private static int Setters(JsonElement f) => f.GetProperty("entry").GetBoolean() || f.TryGetProperty("custom", out _) ? 4 : 2;
    internal static byte[] Canonical(JsonElement f)
    {
        int[]? custom = Custom(f);
        if (custom is null) { Members(f, "contract", "requested", "intent", "selector", "entry", "source", "preflight", "ids", "cleanup"); }
        else { Members(f, "contract", "requested", "intent", "selector", "entry", "source", "preflight", "ids", "cleanup", "custom"); }
        if (f.GetProperty("contract").GetString() is not (Contract or LegacyContract or CoexistenceContract)) { throw Denied(); }
        Requested(f.GetProperty("requested")); var intent = f.GetProperty("intent"); Members(intent, "mode", "rpm");
        int mode = Number(intent.GetProperty("mode")); int rpm = Number(intent.GetProperty("rpm")); int selector = Number(f.GetProperty("selector")); int source = Number(f.GetProperty("source")); bool entry = f.GetProperty("entry").GetBoolean();
        // The Performance selector (2) is valid on both classified external sources, full (0)
        // and limited (2), as in the Rust FanRecord; readers before d6ce3339 refuse it on 2.
        // Source 3 is unclassified external power (fresh Windows AC presence, completed but
        // unclassified 07/8C query); readers before it refuse the row and fence custody.
        // Manual fans keep Balanced (0) or Performance (2). A return to Automatic may also
        // write Silent (5), Battery Saver (3) or Battery Balanced (6), the battery pair never
        // on unclassified power, or Custom (4) with its levels, as Rust FanRecord does.
        bool destination = (selector, custom) switch
        {
            (0 or 2, null) => true,
            (5, null) => mode == 0,
            (3 or 6, null) => mode == 0 && source != 3,
            (4, not null) => mode == 0,
            _ => false,
        };
        if (mode is not (0 or 1 or 3) || !Speed(intent.GetProperty("rpm")) || mode == 3 && rpm != 5400 || !destination || source is not (0 or 2 or 3) || entry && mode == 0) { throw Denied(); }
        var requested = f.GetProperty("requested");
        if (requested.GetProperty("value").ValueKind != JsonValueKind.Null) { int requestedMode = Number(requested.GetProperty("value").GetProperty("value")); int requestedRpm = requested.GetProperty("settings").ValueKind == JsonValueKind.Null ? 3700 : Number(requested.GetProperty("settings").GetProperty("fixedSpeedRpm")); if (mode != requestedMode || rpm != (mode == 3 ? 5400 : requestedRpm)) { throw Denied(); } }
        int setters = Setters(f);
        if (f.GetProperty("preflight").GetArrayLength() != 2 || f.GetProperty("ids").GetArrayLength() != setters + 1 + (entry && selector == 2 ? 1 : 0) + (custom is null ? 0 : 1) || f.GetProperty("cleanup").GetArrayLength() != (mode == 0 ? 0 : 5)) { throw Denied(); }
        var seen = new HashSet<int>();
        foreach (string key in new[] { "preflight", "ids", "cleanup" })
        {
            foreach (var pair in f.GetProperty(key).EnumerateArray()) { if (pair.GetArrayLength() != 2) { throw Denied(); } foreach (var item in pair.EnumerateArray()) { int id = Number(item); if (id is < 1 or > 254 || !seen.Add(id)) { throw Denied(); } } }
        }
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            void Sorted(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object: w.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); Sorted(p.Value); } w.WriteEndObject(); break;
                    case JsonValueKind.Array: w.WriteStartArray(); foreach (var v in e.EnumerateArray()) { Sorted(v); } w.WriteEndArray(); break;
                    case JsonValueKind.Number: w.WriteNumberValue(Number(e)); break;
                    default: e.WriteTo(w); break;
                }
            }
            w.WriteStartObject(); w.WriteString("contract", f.GetProperty("contract").GetString()); w.WritePropertyName("requested"); Sorted(f.GetProperty("requested"));
            w.WritePropertyName("intent"); w.WriteStartObject(); w.WriteNumber("mode", mode); w.WriteNumber("rpm", rpm); w.WriteEndObject();
            w.WriteNumber("selector", selector); w.WriteBoolean("entry", entry); w.WriteNumber("source", source);
            foreach (string k in new[] { "preflight", "ids", "cleanup" }) { w.WritePropertyName(k); Sorted(f.GetProperty(k)); }
            if (custom is not null) { w.WritePropertyName("custom"); w.WriteStartArray(); w.WriteNumberValue(custom[0]); w.WriteNumberValue(custom[1]); w.WriteEndArray(); }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static bool ValidProgress(JsonElement f, byte p)
    {
        int setters = Setters(f); bool manual = f.GetProperty("intent").GetProperty("mode").GetInt32() != 0;
        return p == 51 && manual && f.GetProperty("contract").GetString() is Contract or CoexistenceContract || p is 0 or 6 or 50 || p >= 10 && p < 10 + setters * 2 || manual && p is 30 or 31 or >= 40 and <= 43;
    }
    internal static bool Follows(JsonElement f, byte p, byte n)
    {
        int end = 9 + Setters(f) * 2; bool manual = f.GetProperty("intent").GetProperty("mode").GetInt32() != 0;
        return p == 30 && n == 51 && manual && f.GetProperty("contract").GetString() is Contract or CoexistenceContract || p == 0 && n is 6 or 10 || p >= 10 && p < end && n == p + 1 || p == end && n == (manual ? 30 : 50) || manual && (p == 30 && n == 31 || p == 31 && n == 40 || p is >= 40 and <= 42 && n == p + 1 || p == 43 && n == 50);
    }
}
