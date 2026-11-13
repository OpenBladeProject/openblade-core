using System.Text.Json;

namespace OpenBlade.Deployment;

// Versioned counterpart of fan_history.rs. Raw rows remain in their original
// producer chain; canonical recipe bytes bind intent, commands and retained IDs.
internal static class WindowsNativeFanHistory
{
    internal const string LegacyContract = "service-fan-mutation-v2/rz09-0581-linked-v1";
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
    internal static byte[] Canonical(JsonElement f)
    {
        Members(f, "contract", "requested", "intent", "selector", "entry", "source", "preflight", "ids", "cleanup");
        if (f.GetProperty("contract").GetString() is not (Contract or LegacyContract)) { throw Denied(); }
        Requested(f.GetProperty("requested")); var intent = f.GetProperty("intent"); Members(intent, "mode", "rpm");
        int mode = Number(intent.GetProperty("mode")); int rpm = Number(intent.GetProperty("rpm")); int selector = Number(f.GetProperty("selector")); int source = Number(f.GetProperty("source")); bool entry = f.GetProperty("entry").GetBoolean();
        if (mode is not (0 or 1 or 3) || !Speed(intent.GetProperty("rpm")) || mode == 3 && rpm != 5400 || selector is not (0 or 2) || source is not (0 or 2) || selector == 2 && source != 0 || entry && mode == 0) { throw Denied(); }
        var requested = f.GetProperty("requested");
        if (requested.GetProperty("value").ValueKind != JsonValueKind.Null) { int requestedMode = Number(requested.GetProperty("value").GetProperty("value")); int requestedRpm = requested.GetProperty("settings").ValueKind == JsonValueKind.Null ? 3700 : Number(requested.GetProperty("settings").GetProperty("fixedSpeedRpm")); if (mode != requestedMode || rpm != (mode == 3 ? 5400 : requestedRpm)) { throw Denied(); } }
        int setters = entry ? 4 : 2;
        if (f.GetProperty("preflight").GetArrayLength() != 2 || f.GetProperty("ids").GetArrayLength() != setters + 1 + (entry && selector == 2 ? 1 : 0) || f.GetProperty("cleanup").GetArrayLength() != (mode == 0 ? 0 : 5)) { throw Denied(); }
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
            w.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static bool ValidProgress(JsonElement f, byte p)
    {
        int setters = f.GetProperty("entry").GetBoolean() ? 4 : 2; bool manual = f.GetProperty("intent").GetProperty("mode").GetInt32() != 0;
        return p == 51 && manual && f.GetProperty("contract").GetString() == Contract || p is 0 or 6 or 50 || p >= 10 && p < 10 + setters * 2 || manual && p is 30 or 31 or >= 40 and <= 43;
    }
    internal static bool Follows(JsonElement f, byte p, byte n)
    {
        int end = 9 + (f.GetProperty("entry").GetBoolean() ? 4 : 2) * 2; bool manual = f.GetProperty("intent").GetProperty("mode").GetInt32() != 0;
        return p == 30 && n == 51 && manual && f.GetProperty("contract").GetString() == Contract || p == 0 && n is 6 or 10 || p >= 10 && p < end && n == p + 1 || p == end && n == (manual ? 30 : 50) || manual && (p == 30 && n == 31 || p == 31 && n == 40 || p is >= 40 and <= 42 && n == p + 1 || p == 43 && n == 50);
    }
}
