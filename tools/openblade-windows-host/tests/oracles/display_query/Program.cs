using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Session;

// Offline only: consume actual native dispatcher frames and checkpoint bytes with
// the production managed Display codec and checkpoint store. No OS supplier,
// provider, process or endpoint runs.
const string Usage = "Usage: DisplayQueryParity <native-dispatcher-frames.jsonl> | checkpoints-verify <native.jsonl> | checkpoints-emit <out.jsonl> | requests-emit <out.jsonl> | requests-verify <in.jsonl>";
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
if (args.Length == 2 && args[0] == "requests-emit")
{
    return await EmitRequestsAsync(args[1]);
}
if (args.Length == 2 && args[0] == "requests-verify")
{
    return await VerifyRequestsAsync(args[1]);
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
        // Saved keys are text a user can choose; serde_json treats only member
        // names with this prefix specially.
        ("battery-private-prefix-keys-baseline", S(PowerSource.Battery, null,
            new DisplayProfileSettings("$serde_json::private::Number", false, "$serde_json::private::srgb"), oR: true, oC: true)),
        // Main admits ExternalUnknown only as a durable manual hold that owns nothing.
        ("externalunknown-manualhold-paused-uncertain-brightness", S(PowerSource.ExternalUnknown, hold: true, paused: true, uB: true)),
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

static async Task<byte[]> EncodeRequestAsync(SessionDisplayRequest request)
{
    using var stream = new MemoryStream();
    await SessionDisplayIpcCodec.WriteRequestAsync(stream, request);
    return stream.ToArray();
}

static byte[] RawFrame(string json)
{
    byte[] body = Encoding.UTF8.GetBytes(json);
    byte[] frame = new byte[body.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
    body.CopyTo(frame, 4);
    return frame;
}

static async Task<SessionDisplayRequest> DecodeRequestAsync(byte[] frame)
{
    using var stream = new MemoryStream(frame, writable: false);
    SessionDisplayRequest request = await SessionDisplayIpcCodec.ReadRequestAsync(stream);
    Require(stream.Position == stream.Length, "Request decoder did not consume the complete frame.");
    return request;
}

static async Task<bool> RejectedAsync(byte[] frame)
{
    try
    {
        await DecodeRequestAsync(frame);
        return false;
    }
    catch (InvalidDataException)
    {
        return true;
    }
}

static async Task<int> EmitRequestsAsync(string outPath)
{
    Guid id = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
    const int V = SessionDisplayIpc.ProtocolVersion;
    var rows = new List<(string Name, bool Valid, byte[] Frame)>();
    async Task AddValid(string name, SessionDisplayRequest request) =>
        rows.Add((name, true, await EncodeRequestAsync(request)));
    void AddInvalid(string name, string json) => rows.Add((name, false, RawFrame(json)));

    await AddValid("query", new QueryDisplayRequest(V, id));
    foreach (byte percent in new byte[] { 0, 50, 100 })
    {
        foreach (PowerSource source in new[] { PowerSource.PluggedIn, PowerSource.Battery, PowerSource.UsbC })
        {
            await AddValid($"setBrightness-{percent}-power{(int)source}", new SetBrightnessRequest(V, id, source, percent));
        }
    }
    await AddValid("setRefreshRate-normal", new SetRefreshRateRequest(V, id, PowerSource.Battery, "1920x1080@144"));
    await AddValid("setRefreshRate-1024", new SetRefreshRateRequest(V, id, PowerSource.PluggedIn, new string('r', 1024)));
    await AddValid("setHdr-true", new SetHdrRequest(V, id, PowerSource.UsbC, true));
    await AddValid("setHdr-false", new SetHdrRequest(V, id, PowerSource.PluggedIn, false));
    await AddValid("setColorProfile-spaces-nonascii", new SetColorProfileRequest(
        V, id, PowerSource.Battery, "sRGB écran 日本 \U0001F600 x"));
    await AddValid("setColorProfile-1024", new SetColorProfileRequest(V, id, PowerSource.UsbC, new string('c', 1024)));
    // Keys are text a user can choose; only member names are serde_json tokens.
    await AddValid("setRefreshRate-private-prefix", new SetRefreshRateRequest(V, id, PowerSource.Battery, "$serde_json::private::Number"));
    await AddValid("setColorProfile-private-prefix", new SetColorProfileRequest(V, id, PowerSource.UsbC, "$serde_json::private::RawValue"));
    // Manual operations also name unclassified external power; applyProfile does not.
    await AddValid("setBrightness-unclassified-external", new SetBrightnessRequest(V, id, PowerSource.ExternalUnknown, 50));
    await AddValid("setHdr-unclassified-external", new SetHdrRequest(V, id, PowerSource.ExternalUnknown, true));
    await AddValid("setRefreshRate-unclassified-external", new SetRefreshRateRequest(V, id, PowerSource.ExternalUnknown, "fixed-60"));
    await AddValid("setColorProfile-unclassified-external", new SetColorProfileRequest(V, id, PowerSource.ExternalUnknown, "sRGB.icm"));
    await AddValid("applyProfile-all-null", new ApplyDisplayProfileRequest(
        V, id, PowerSource.PluggedIn, new DisplayProfileSettings(null, null, null)));
    await AddValid("applyProfile-full", new ApplyDisplayProfileRequest(
        V, id, PowerSource.Battery, new DisplayProfileSettings("60 Hz", true, "Profile one") { BrightnessPercent = 75 }));
    await AddValid("applyProfile-refresh-1024", new ApplyDisplayProfileRequest(
        V, id, PowerSource.UsbC, new DisplayProfileSettings(new string('r', 1024), null, null)));
    await AddValid("applyProfile-hdr-false", new ApplyDisplayProfileRequest(
        V, id, PowerSource.PluggedIn, new DisplayProfileSettings(null, false, null)));
    await AddValid("applyProfile-color-nonascii", new ApplyDisplayProfileRequest(
        V, id, PowerSource.Battery, new DisplayProfileSettings(null, null, "sRGB écran 日本 \U0001F600 x")));
    await AddValid("applyProfile-private-prefix-keys", new ApplyDisplayProfileRequest(
        V, id, PowerSource.PluggedIn, new DisplayProfileSettings("$serde_json::private::Number", null, "$serde_json::private::game")));
    await AddValid("applyProfile-private-prefix-color", new ApplyDisplayProfileRequest(
        V, id, PowerSource.Battery, new DisplayProfileSettings(null, true, "$serde_json::private::")));
    await AddValid("applyProfile-brightness-0", new ApplyDisplayProfileRequest(
        V, id, PowerSource.UsbC, DisplayProfileSettings.Unconfigured() with { BrightnessPercent = 0 }));
    await AddValid("applyProfile-brightness-100", new ApplyDisplayProfileRequest(
        V, id, PowerSource.PluggedIn, DisplayProfileSettings.Unconfigured() with { BrightnessPercent = 100 }));

    string Op(string op, string rest, int version = V) =>
        $"{{\"operation\":\"{op}\",\"version\":{version},\"requestId\":\"{id}\"{rest}}}";
    string Rate(string key) =>
        Op("setRefreshRate", $",\"expectedPowerSource\":0,\"refreshRateKey\":{JsonSerializer.Serialize(key)}");
    string Color(string key) =>
        Op("setColorProfile", $",\"expectedPowerSource\":0,\"colorProfileKey\":{JsonSerializer.Serialize(key)}");

    AddInvalid("unknown-member", Op("setBrightness", ",\"expectedPowerSource\":0,\"percent\":50,\"extra\":1"));
    AddInvalid("null-version", $"{{\"operation\":\"query\",\"version\":null,\"requestId\":\"{id}\"}}");
    AddInvalid("null-requestId", $"{{\"operation\":\"query\",\"version\":{V},\"requestId\":null}}");
    AddInvalid("null-expectedPowerSource", Op("setBrightness", ",\"expectedPowerSource\":null,\"percent\":50"));
    AddInvalid("null-percent", Op("setBrightness", ",\"expectedPowerSource\":0,\"percent\":null"));
    AddInvalid("null-refreshRateKey", Op("setRefreshRate", ",\"expectedPowerSource\":0,\"refreshRateKey\":null"));
    AddInvalid("null-enabled", Op("setHdr", ",\"expectedPowerSource\":0,\"enabled\":null"));
    AddInvalid("null-colorProfileKey", Op("setColorProfile", ",\"expectedPowerSource\":0,\"colorProfileKey\":null"));
    AddInvalid("null-settings", Op("applyProfile", ",\"expectedPowerSource\":0,\"settings\":null"));
    AddInvalid("power-source-4", Op("setBrightness", ",\"expectedPowerSource\":4,\"percent\":50"));
    AddInvalid("power-source-minus-1", Op("setBrightness", ",\"expectedPowerSource\":-1,\"percent\":50"));
    AddInvalid("percent-101", Op("setBrightness", ",\"expectedPowerSource\":0,\"percent\":101"));
    AddInvalid("blank-key", Rate("   "));
    AddInvalid("empty-key", Rate(string.Empty));
    AddInvalid("space-padded-key", Rate(" 60 Hz "));
    AddInvalid("key-1025", Rate(new string('r', 1025)));
    AddInvalid("control-char-key", Rate("60\u0007Hz"));
    AddInvalid("color-blank-key", Color("   "));
    AddInvalid("color-empty-key", Color(string.Empty));
    AddInvalid("color-space-padded-key", Color(" sRGB.icm "));
    AddInvalid("color-key-1025", Color(new string('c', 1025)));
    AddInvalid("color-control-char-key", Color("sRGB\u0007.icm"));
    AddInvalid("enabled-as-string", Op("setHdr", ",\"expectedPowerSource\":0,\"enabled\":\"true\""));
    AddInvalid("wrong-version", Op("setBrightness", ",\"expectedPowerSource\":0,\"percent\":50", V + 1));
    AddInvalid("operation-not-first", $"{{\"version\":{V},\"requestId\":\"{id}\",\"operation\":\"query\"}}");
    // A JSON array is not an object. These are positional in the native
    // envelope declaration order, which a bare serde derive would bind.
    AddInvalid("query-positional-array", $"[{V},\"{id}\",\"query\"]");
    AddInvalid("setBrightness-positional-array", $"[{V},\"{id}\",\"setBrightness\",0,50]");
    // The discriminator must be a JSON string. serde's externally tagged
    // form of a selected operation, {"<name>":null}, with otherwise valid
    // members, is refused.
    string ObjectOp(string op, string rest) =>
        $"{{\"operation\":{{\"{op}\":null}},\"version\":{V},\"requestId\":\"{id}\"{rest}}}";
    AddInvalid("object-operation-query", ObjectOp("query", string.Empty));
    AddInvalid("object-operation-setBrightness", ObjectOp("setBrightness", ",\"expectedPowerSource\":0,\"percent\":50"));
    AddInvalid("object-operation-setHdr", ObjectOp("setHdr", ",\"expectedPowerSource\":0,\"enabled\":true"));
    AddInvalid("object-operation-setRefreshRate", ObjectOp("setRefreshRate", ",\"expectedPowerSource\":0,\"refreshRateKey\":\"fixed-60\""));
    AddInvalid("object-operation-setColorProfile", ObjectOp("setColorProfile", ",\"expectedPowerSource\":0,\"colorProfileKey\":\"sRGB.icm\""));
    // applyProfile settings exactly as the managed codec writes them.
    string Settings(string refresh = "null", string hdr = "null", string color = "null",
        string brightness = "null", string configured = "true") =>
        $"{{\"refreshRateKey\":{refresh},\"hdrEnabled\":{hdr},\"colorProfileKey\":{color},\"brightnessPercent\":{brightness},\"isConfigured\":{configured}}}";
    string Profile(string settings, string power = "0") =>
        Op("applyProfile", $",\"expectedPowerSource\":{power},\"settings\":{settings}");
    AddInvalid("object-operation-applyProfile", ObjectOp("applyProfile", ",\"expectedPowerSource\":0,\"settings\":" + Settings(brightness: "50")));
    AddInvalid("applyProfile-power-source-3", Profile(Settings(brightness: "50"), "3"));
    AddInvalid("applyProfile-missing-power-source", Op("applyProfile", ",\"settings\":" + Settings(brightness: "50")));
    AddInvalid("applyProfile-missing-settings", Op("applyProfile", ",\"expectedPowerSource\":0"));
    AddInvalid("applyProfile-settings-array", Profile("[]"));
    // Five members in order: serde's derive would bind them positionally.
    AddInvalid("applyProfile-settings-five-element-array", Profile("[null,true,null,null,true]"));
    AddInvalid("applyProfile-settings-configured-array", Profile("[\"fixed-120\",false,\"sRGB.icm\",50,true]"));
    AddInvalid("applyProfile-blank-refresh-key", Profile(Settings(refresh: "\"   \"")));
    AddInvalid("applyProfile-refresh-key-1025", Profile(Settings(refresh: JsonSerializer.Serialize(new string('r', 1025)))));
    AddInvalid("applyProfile-padded-color-key", Profile(Settings(color: "\" sRGB.icm \"")));
    AddInvalid("applyProfile-control-char-color-key", Profile(Settings(color: JsonSerializer.Serialize("sRGB\u0007.icm"))));
    AddInvalid("applyProfile-brightness-101", Profile(Settings(brightness: "101")));
    AddInvalid("applyProfile-brightness-minus-1", Profile(Settings(brightness: "-1")));
    AddInvalid("applyProfile-hdr-as-string", Profile(Settings(hdr: "\"true\"")));
    AddInvalid("applyProfile-missing-refreshRateKey", Profile("{\"hdrEnabled\":true,\"colorProfileKey\":null,\"brightnessPercent\":null,\"isConfigured\":true}"));
    AddInvalid("applyProfile-unknown-settings-member", Profile(Settings(brightness: "50").TrimEnd('}') + ",\"extra\":1}"));
    AddInvalid("applyProfile-percent-member", Op("applyProfile", ",\"expectedPowerSource\":0,\"percent\":50,\"settings\":" + Settings(brightness: "50")));
    // Managed leniency (last member wins), so not a fixture row; printed instead.
    var lenient = new List<(string Name, string Json)>
    {
        ("duplicate-member", Op("setBrightness", ",\"expectedPowerSource\":0,\"percent\":50,\"percent\":60")),
        // Members the managed codec always writes but does not require or
        // check; native requires the written shape.
        ("applyProfile-missing-brightnessPercent", Profile("{\"refreshRateKey\":null,\"hdrEnabled\":true,\"colorProfileKey\":null,\"isConfigured\":true}")),
        ("applyProfile-missing-isConfigured", Profile("{\"refreshRateKey\":null,\"hdrEnabled\":true,\"colorProfileKey\":null,\"brightnessPercent\":null}")),
        ("applyProfile-all-null-missing-isConfigured", Profile("{\"refreshRateKey\":null,\"hdrEnabled\":null,\"colorProfileKey\":null,\"brightnessPercent\":null}")),
        ("applyProfile-inconsistent-isConfigured", Profile(Settings(hdr: "true", configured: "false"))),
        ("applyProfile-pascal-case-settings", Profile("{\"RefreshRateKey\":null,\"HdrEnabled\":true,\"ColorProfileKey\":null,\"BrightnessPercent\":null,\"IsConfigured\":true}")),
        ("applyProfile-brightness-as-string", Profile(Settings(brightness: "\"50\""))),
    };
    AddInvalid("unknown-operation", Op("bogus", string.Empty));

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var lines = new List<string>();
    foreach ((string name, string json) in lenient)
    {
        Console.WriteLine("observed managed behavior (not in fixture) " + name + ": " + ((await RejectedAsync(RawFrame(json))) ? "rejected" : "accepted"));
    }
    foreach ((string name, bool valid, byte[] frame) in rows)
    {
        Require(seen.Add(name), "Duplicate case name: " + name);
        if (valid)
        {
            await DecodeRequestAsync(frame);
        }
        else
        {
            bool rejected = await RejectedAsync(frame);
            Console.WriteLine((rejected ? "rejected " : "ACCEPTED ") + name);
            Require(rejected, "Managed reader accepted invalid case: " + name);
        }
        var row = new JsonObject
        {
            ["case"] = name,
            ["valid"] = valid,
            ["frame"] = Convert.ToBase64String(frame),
        };
        lines.Add(row.ToJsonString());
    }
    File.WriteAllText(outPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"Wrote {rows.Count(r => r.Valid)} valid and {rows.Count(r => !r.Valid)} invalid request rows.");
    return 0;
}

static async Task<int> VerifyRequestsAsync(string path)
{
    var corpus = new FileInfo(path);
    Require(corpus.Length is > 0 and <= 16 * 1024 * 1024, "Corpus size is invalid.");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    int valid = 0, invalid = 0;
    foreach (string line in await File.ReadAllLinesAsync(corpus.FullName))
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement item = doc.RootElement;
        Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == 3,
            "Row must have case, valid and frame.");
        string name = item.GetProperty("case").GetString() ?? throw new InvalidDataException("Missing case.");
        Require(seen.Add(name), "Duplicate case: " + name);
        bool expectValid = item.GetProperty("valid").GetBoolean();
        byte[] frame = Convert.FromBase64String(item.GetProperty("frame").GetString() ?? string.Empty);
        if (expectValid)
        {
            SessionDisplayRequest request = await DecodeRequestAsync(frame);
            Require((await EncodeRequestAsync(request)).AsSpan().SequenceEqual(frame),
                "Managed re-encode differs from fixture bytes: " + name);
            valid++;
        }
        else
        {
            Require(await RejectedAsync(frame), "Managed reader accepted invalid case: " + name);
            invalid++;
        }
    }
    Require(valid > 0 && invalid > 0, "Corpus must contain valid and invalid rows.");
    Console.WriteLine($"PASS managed request codec agrees with {valid} valid and {invalid} invalid rows.");
    return 0;
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}
