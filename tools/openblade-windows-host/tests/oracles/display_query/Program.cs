using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Session;

// Offline only: consume actual native dispatcher frames and checkpoint bytes with
// the production managed Display codec and checkpoint store. No OS supplier,
// provider, process or endpoint runs.
const string Usage = "Usage: DisplayQueryParity <native-dispatcher-frames.jsonl> | checkpoints-verify <native.jsonl> | checkpoints-emit <out.jsonl>";
if (args.Length == 1)
{
    return await VerifyFramesAsync(args[0]);
}
if (args.Length == 2 && args[0] == "checkpoints-verify")
{
    return VerifyCheckpoints(args[1]);
}
if (args.Length == 2 && args[0] == "checkpoints-emit")
{
    return EmitCheckpoints(args[1]);
}
throw new ArgumentException(Usage);

static async Task<int> VerifyFramesAsync(string path)
{
    var corpus = new FileInfo(path);
    Require(corpus.Length is > 0 and <= 16 * 1024 * 1024, "Corpus size is invalid.");
    Guid requestId = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    int queries = 0, writes = 0;
    foreach (string line in await File.ReadAllLinesAsync(corpus.FullName))
    {
        Require(line.Length is > 0 and <= 2 * 1024 * 1024, "Invalid corpus row length.");
        // Corpus integrity only. Display's production codec has no duplicate scan.
        IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(line));
        using JsonDocument row = JsonDocument.Parse(line);
        JsonElement item = row.RootElement;
        Require(item.ValueKind == JsonValueKind.Object, "Corpus row must be an object.");
        int members = item.EnumerateObject().Count();
        Require(members is 4 or 5,
            "Corpus row must have case, expectedStatus, [expectedError,] expectedState and frame.");
        bool write = members == 5;
        string name = item.GetProperty("case").GetString() ?? throw new InvalidDataException("Missing case.");
        Require(!string.IsNullOrWhiteSpace(name) && seen.Add(name) && seen.Count <= 128, "Unknown or duplicate case.");
        int statusValue = item.GetProperty("expectedStatus").GetInt32();
        Require(statusValue is >= 0 and <= 3, "Unknown expected status: " + name);
        var expectedStatus = (SessionDisplayOperationStatus)statusValue;
        string? expectedError = null;
        if (write)
        {
            JsonElement errorElement = item.GetProperty("expectedError");
            Require(errorElement.ValueKind is JsonValueKind.String or JsonValueKind.Null, "expectedError must be string or null: " + name);
            expectedError = errorElement.GetString();
            Require((expectedError is null) == (expectedStatus == SessionDisplayOperationStatus.Succeeded),
                "expectedError must be null exactly for Succeeded: " + name);
            writes++;
        }
        else
        {
            Require(expectedStatus == SessionDisplayOperationStatus.Succeeded,
                "Read-only query observation must use Succeeded, including unavailable state: " + name);
            queries++;
        }
        JsonElement expectedState = item.GetProperty("expectedState");
        Require(expectedState.ValueKind == JsonValueKind.Object, "Missing expected snapshot: " + name);
        JsonElement bytes = item.GetProperty("frame");
        Require(bytes.ValueKind == JsonValueKind.Array && bytes.GetArrayLength() is > 4 and <= SessionDisplayIpc.MaximumMessageBytes + 4,
            "Frame length is invalid: " + name);
        byte[] frame = bytes.EnumerateArray().Select(static value => value.GetByte()).ToArray();
        Require(BinaryPrimitives.ReadInt32LittleEndian(frame) == frame.Length - 4, "Frame prefix mismatch: " + name);
        using var stream = new MemoryStream(frame, writable: false);
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(stream);
        Require(stream.Position == stream.Length, "Decoder did not consume complete frame: " + name);
        Require(response.Version == SessionDisplayIpc.ProtocolVersion && response.RequestId == requestId
            && response.Status == expectedStatus && string.Equals(response.Error, expectedError, StringComparison.Ordinal),
            "Response envelope mismatch: " + name);
        byte? percent = response.State.BrightnessPercent;
        IReadOnlyList<byte> levels = response.State.SupportedBrightnessLevels;
        Require(percent is null ? levels.Count == 0 : percent <= 100 && levels.Count is > 0 and <= 101
            && levels.All(static level => level <= 100) && levels.SequenceEqual(levels.Distinct().Order()),
            "Brightness observation is not optional, bounded and normalized: " + name);
        JsonNode? expected = JsonNode.Parse(expectedState.GetRawText());
        JsonNode? actual = JsonSerializer.SerializeToNode(response.State, options);
        Require(JsonNode.DeepEquals(expected, actual), "Complete snapshot mismatch: " + name);
        using var roundtrip = new MemoryStream();
        await SessionDisplayIpcCodec.WriteResponseAsync(roundtrip, response);
        roundtrip.Position = 0;
        SessionDisplayResponse decoded = await SessionDisplayIpcCodec.ReadResponseAsync(roundtrip);
        Require(roundtrip.Position == roundtrip.Length && decoded.Version == response.Version
            && decoded.RequestId == response.RequestId && decoded.Status == response.Status && decoded.Error == response.Error
            && JsonNode.DeepEquals(actual, JsonSerializer.SerializeToNode(decoded.State, options)),
            "Managed roundtrip mismatch: " + name);
        Console.WriteLine("PASS " + name);
    }
    Require(seen.Count > 0, "Corpus must contain response frames.");
    Console.WriteLine($"PASS actual native Display dispatcher frames match managed codec ({queries} query cases, {writes} write cases).");
    return 0;
}

static JsonObject StateToJson(DisplayAutomationRecoveryState s)
{
    JsonNode? baseline = s.Baseline is null ? null : JsonSerializer.SerializeToNode(s.Baseline);
    baseline?.AsObject().Remove("IsConfigured");
    return new JsonObject
    {
        ["LastReconciledPowerSource"] = (int)s.LastReconciledPowerSource,
        ["ApplicationProfileId"] = s.ApplicationProfileId?.ToString(),
        ["Baseline"] = baseline,
        ["OwnedRefreshRate"] = s.OwnedRefreshRate,
        ["OwnedHdr"] = s.OwnedHdr,
        ["OwnedColorProfile"] = s.OwnedColorProfile,
        ["OwnedBrightness"] = s.OwnedBrightness,
        ["ManualHold"] = s.ManualHold,
        ["AutomaticWritesPaused"] = s.AutomaticWritesPaused,
        ["UncertainRefreshRate"] = s.UncertainRefreshRate,
        ["UncertainHdr"] = s.UncertainHdr,
        ["UncertainColorProfile"] = s.UncertainColorProfile,
        ["UncertainBrightness"] = s.UncertainBrightness,
    };
}

static string NewTempFile() => Path.Combine(Path.GetTempPath(), "obdq-" + Guid.NewGuid().ToString("N") + ".json");

static void DeleteTemp(string path)
{
    foreach (string p in new[] { path, path + ".tmp" })
    {
        try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

static int EmitCheckpoints(string outPath)
{
    var guid = Guid.Parse("3f2c8a10-5b6d-4e7a-9c1f-0a2b3c4d5e6f");
    var full = new DisplayProfileSettings("120", true, "srgb") { BrightnessPercent = 55 };
    var brightnessOnly = new DisplayProfileSettings(null, null, null) { BrightnessPercent = 30 };
    DisplayAutomationRecoveryState S(PowerSource p, Guid? id = null, DisplayProfileSettings? b = null,
        bool oR = false, bool oH = false, bool oC = false, bool oB = false, bool hold = false, bool paused = false,
        bool uR = false, bool uH = false, bool uC = false, bool uB = false)
        => new(p, id, b, oR, oH, oC, oB, hold, paused, uR, uH, uC, uB);
    var cases = new List<(string Name, DisplayAutomationRecoveryState State)>
    {
        ("pluggedin-minimal", S(PowerSource.PluggedIn)),
        ("battery-minimal", S(PowerSource.Battery)),
        ("usbc-minimal", S(PowerSource.UsbC)),
        ("battery-manualhold-paused", S(PowerSource.Battery, hold: true, paused: true)),
        ("pluggedin-owned-all-with-profile-full-baseline", S(PowerSource.PluggedIn, guid, full, true, true, true, true)),
        ("usbc-uncertain-all-brightness-only-baseline", S(PowerSource.UsbC, null, brightnessOnly, uR: true, uH: true, uC: true, uB: true)),
        ("battery-owned-brightness-uncertain-hdr-hold", S(PowerSource.Battery, guid, brightnessOnly, oB: true, uH: true, hold: true)),
        ("pluggedin-owned-refresh-color-uncertain-refresh-paused", S(PowerSource.PluggedIn, null, full, oR: true, oC: true, uR: true, paused: true)),
    };
    var lines = new StringBuilder();
    foreach (var (name, state) in cases)
    {
        string file = NewTempFile();
        try
        {
            Require(new DisplayAutomationCheckpointStore(file).SaveRecovery(state), "SaveRecovery failed: " + name);
            string text = File.ReadAllText(file, new UTF8Encoding(false));
            var row = new JsonObject { ["case"] = name, ["bytes"] = text, ["state"] = StateToJson(state) };
            lines.Append(row.ToJsonString()).Append('\n');
            Console.WriteLine("EMIT " + name);
        }
        finally { DeleteTemp(file); }
    }
    string legacy = NewTempFile();
    try
    {
        Require(new DisplayAutomationCheckpointStore(legacy).Save(PowerSource.Battery), "Legacy Save failed.");
        var row = new JsonObject
        {
            ["case"] = "legacy-schema1-battery",
            ["bytes"] = File.ReadAllText(legacy, new UTF8Encoding(false)),
            ["state"] = null,
            ["legacySource"] = (int)PowerSource.Battery,
        };
        lines.Append(row.ToJsonString()).Append('\n');
        Console.WriteLine("EMIT legacy-schema1-battery");
    }
    finally { DeleteTemp(legacy); }
    string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
    File.WriteAllBytes(outPath, new UTF8Encoding(false).GetBytes(lines.ToString()));
    Console.WriteLine($"PASS emitted {cases.Count + 1} managed checkpoint cases.");
    return 0;
}

static int VerifyCheckpoints(string path)
{
    var corpus = new FileInfo(path);
    Require(corpus.Length is > 0 and <= 16 * 1024 * 1024, "Corpus size is invalid.");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    int valid = 0, invalid = 0;
    foreach (string line in File.ReadAllLines(corpus.FullName))
    {
        Require(line.Length is > 0 and <= 2 * 1024 * 1024, "Invalid corpus row length.");
        IpcJsonValidation.ValidateNoDuplicateMembers(Encoding.UTF8.GetBytes(line));
        JsonObject row = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Row is not an object.");
        string name = row["case"]?.GetValue<string>() ?? throw new InvalidDataException("Missing case.");
        Require(!string.IsNullOrWhiteSpace(name) && seen.Add(name), "Unknown or duplicate case.");
        string bytes = row["bytes"]?.GetValue<string>() ?? throw new InvalidDataException("Missing bytes: " + name);
        string file = NewTempFile();
        try
        {
            File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes(bytes));
            var store = new DisplayAutomationCheckpointStore(file);
            if (row["expectedInvalid"]?.GetValue<bool>() == true)
            {
                Require(store.LoadRecovery() is null && store.Load() is null, "Invalid bytes were accepted: " + name);
                invalid++;
            }
            else
            {
                JsonNode expected = row["expected"] ?? throw new InvalidDataException("Missing expected: " + name);
                Require(expected is JsonObject { Count: 13 }, "expected must have 13 members: " + name);
                DisplayAutomationRecoveryState loaded = store.LoadRecovery()
                    ?? throw new InvalidDataException("LoadRecovery returned null: " + name);
                JsonObject actual = StateToJson(loaded);
                JsonObject exp = expected.DeepClone().AsObject();
                if (exp["Baseline"] is JsonObject b) { b.Remove("IsConfigured"); }
                Require(JsonNode.DeepEquals(exp, actual), $"State mismatch: {name}\n expected {exp.ToJsonString()}\n actual   {actual.ToJsonString()}");
                string second = NewTempFile();
                try
                {
                    var store2 = new DisplayAutomationCheckpointStore(second);
                    Require(store2.SaveRecovery(loaded), "SaveRecovery failed: " + name);
                    DisplayAutomationRecoveryState? again = store2.LoadRecovery();
                    Require(again is not null && again == loaded, "Managed roundtrip mismatch: " + name);
                }
                finally { DeleteTemp(second); }
                valid++;
            }
        }
        finally { DeleteTemp(file); }
        Console.WriteLine("PASS " + name);
    }
    Require(seen.Count > 0, "Corpus must contain checkpoint rows.");
    Console.WriteLine($"PASS native checkpoint bytes match managed store ({valid} loadable cases, {invalid} rejected cases).");
    return 0;
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}
