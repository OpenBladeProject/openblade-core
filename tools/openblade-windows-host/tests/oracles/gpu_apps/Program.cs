using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Platform;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

// Offline only: the production managed Session GPU codec and dispatcher over
// scripted scans and action results. No process, counter, endpoint or OS
// supplier is used.
const string Usage = "Usage: GpuAppsParity requests-emit <out.jsonl> | requests-verify <in.jsonl> | dispatch-emit <out.jsonl> | responses-verify <native.jsonl>";
if (args.Length == 2 && args[0] == "requests-emit")
{
    return await EmitRequestsAsync(args[1]);
}
if (args.Length == 2 && args[0] == "requests-verify")
{
    return await VerifyRequestsAsync(args[1]);
}
if (args.Length == 2 && args[0] == "dispatch-emit")
{
    return await EmitDispatchAsync(args[1]);
}
if (args.Length == 2 && args[0] == "responses-verify")
{
    return await VerifyResponsesAsync(args[1]);
}
throw new ArgumentException(Usage);

static async Task<byte[]> EncodeRequestAsync(SessionGpuRequest request)
{
    using var stream = new MemoryStream();
    await SessionGpuIpcCodec.WriteRequestAsync(stream, request);
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

static async Task<SessionGpuRequest> DecodeRequestAsync(byte[] frame)
{
    using var stream = new MemoryStream(frame, writable: false);
    SessionGpuRequest request = await SessionGpuIpcCodec.ReadRequestAsync(stream);
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
    const string Id = "9f876742-1234-4321-abcd-d3ea3e1cf102";
    const int V = SessionGpuIpc.ProtocolVersion;
    const long Ticks = 638_900_000_000_000_000;
    var rows = new List<(string Name, bool Valid, byte[] Frame)>();
    async Task AddValid(string name, SessionGpuRequest request) =>
        rows.Add((name, true, await EncodeRequestAsync(request)));
    void AddInvalid(string name, string json) => rows.Add((name, false, RawFrame(json)));
    static SessionGpuAppIdentity Target(int pid, string name = "Game", long ticks = Ticks) => new(pid, ticks, name);
    string Close(string force, string targets) =>
        $"{{\"operation\":\"close\",\"version\":2,\"requestId\":\"{Id}\",\"force\":{force},\"targets\":{targets}}}";
    string One(string target) => Close("false", "[" + target + "]");
    string TargetJson(string pid = "41", string ticks = "638900000000000000", string name = "\"Game\"") =>
        $"{{\"processId\":{pid},\"startTimeUtcTicks\":{ticks},\"name\":{name}}}";

    await AddValid("query", new QueryGpuAppsRequest(V, id));
    await AddValid("close-one", new CloseGpuAppsRequest(V, id, false, [Target(41)]));
    await AddValid("force-one", new CloseGpuAppsRequest(V, id, true, [Target(41)]));
    await AddValid("close-two", new CloseGpuAppsRequest(V, id, false, [Target(41), Target(42, "Other")]));
    await AddValid("close-128", new CloseGpuAppsRequest(V, id, false,
        [.. Enumerable.Range(1, SessionGpuIpc.MaximumApps).Select(pid => Target(pid))]));
    await AddValid("close-maximum-values", new CloseGpuAppsRequest(V, id, false, [Target(int.MaxValue, "x", long.MaxValue)]));
    await AddValid("close-name-260", new CloseGpuAppsRequest(V, id, false, [Target(41, new string('n', 260))]));
    await AddValid("close-name-inner-spaces-nonascii", new CloseGpuAppsRequest(V, id, false,
        [Target(41, "Jeu écran 日本 \U0001F600 x")]));
    await AddValid("close-name-quotes-backslash", new CloseGpuAppsRequest(V, id, false, [Target(41, "a\"b\\c")]));
    // Names are text a process chose; only member names are serde_json tokens.
    await AddValid("close-name-private-prefix", new CloseGpuAppsRequest(V, id, false, [Target(41, "$serde_json::private::Number")]));

    AddInvalid("query-version-1", $"{{\"operation\":\"query\",\"version\":1,\"requestId\":\"{Id}\"}}");
    AddInvalid("query-version-3", $"{{\"operation\":\"query\",\"version\":3,\"requestId\":\"{Id}\"}}");
    AddInvalid("close-version-1", $"{{\"operation\":\"close\",\"version\":1,\"requestId\":\"{Id}\",\"force\":false,\"targets\":[{TargetJson()}]}}");
    AddInvalid("query-empty-guid", "{\"operation\":\"query\",\"version\":2,\"requestId\":\"00000000-0000-0000-0000-000000000000\"}");
    AddInvalid("query-guid-not-a-guid", "{\"operation\":\"query\",\"version\":2,\"requestId\":\"not-a-guid\"}");
    AddInvalid("query-guid-number", "{\"operation\":\"query\",\"version\":2,\"requestId\":1}");
    AddInvalid("query-missing-guid", "{\"operation\":\"query\",\"version\":2}");
    AddInvalid("query-missing-version", $"{{\"operation\":\"query\",\"requestId\":\"{Id}\"}}");
    AddInvalid("query-version-float", $"{{\"operation\":\"query\",\"version\":2.0,\"requestId\":\"{Id}\"}}");
    AddInvalid("unknown-operation", $"{{\"operation\":\"bogus\",\"version\":2,\"requestId\":\"{Id}\"}}");
    AddInvalid("operation-object", $"{{\"operation\":{{\"query\":null}},\"version\":2,\"requestId\":\"{Id}\"}}");
    AddInvalid("operation-number", $"{{\"operation\":0,\"version\":2,\"requestId\":\"{Id}\"}}");
    AddInvalid("operation-late", $"{{\"version\":2,\"operation\":\"query\",\"requestId\":\"{Id}\"}}");
    AddInvalid("operation-missing", $"{{\"version\":2,\"requestId\":\"{Id}\"}}");
    AddInvalid("query-unknown-member", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id}\",\"futureMember\":true}}");
    AddInvalid("query-private-member", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id}\",\"$serde_json::private::Number\":\"2\"}}");
    AddInvalid("query-with-force", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id}\",\"force\":false}}");
    AddInvalid("query-with-targets", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id}\",\"targets\":[{TargetJson()}]}}");
    AddInvalid("close-missing-force", $"{{\"operation\":\"close\",\"version\":2,\"requestId\":\"{Id}\",\"targets\":[{TargetJson()}]}}");
    AddInvalid("close-missing-targets", $"{{\"operation\":\"close\",\"version\":2,\"requestId\":\"{Id}\",\"force\":false}}");
    AddInvalid("close-null-force", Close("null", "[" + TargetJson() + "]"));
    AddInvalid("close-force-string", Close("\"true\"", "[" + TargetJson() + "]"));
    AddInvalid("close-null-targets", Close("false", "null"));
    AddInvalid("close-targets-object", Close("false", TargetJson()));
    AddInvalid("close-empty-targets", Close("false", "[]"));
    AddInvalid("close-129-targets", Close("false",
        "[" + string.Join(',', Enumerable.Range(1, SessionGpuIpc.MaximumApps + 1).Select(pid => TargetJson(pid.ToString()))) + "]"));
    AddInvalid("force-two-targets", Close("true", $"[{TargetJson()},{TargetJson("42")}]"));
    AddInvalid("close-duplicate-pid", Close("false", $"[{TargetJson()},{TargetJson(name: "\"Other\"")}]"));
    AddInvalid("close-null-target", Close("false", "[null]"));
    AddInvalid("close-target-array", Close("false", "[[41,638900000000000000,\"Game\"]]"));
    AddInvalid("close-pid-zero", One(TargetJson("0")));
    AddInvalid("close-pid-negative", One(TargetJson("-1")));
    AddInvalid("close-pid-overflow", One(TargetJson("2147483648")));
    AddInvalid("close-pid-float", One(TargetJson("41.0")));
    AddInvalid("close-ticks-zero", One(TargetJson(ticks: "0")));
    AddInvalid("close-ticks-negative", One(TargetJson(ticks: "-5")));
    AddInvalid("close-name-empty", One(TargetJson(name: "\"\"")));
    AddInvalid("close-name-blank", One(TargetJson(name: "\"   \"")));
    AddInvalid("close-name-leading-space", One(TargetJson(name: "\" Game\"")));
    AddInvalid("close-name-trailing-tab", One(TargetJson(name: "\"Game\\t\"")));
    AddInvalid("close-name-nbsp-edge", One(TargetJson(name: "\"Game\\u00a0\"")));
    AddInvalid("close-name-control", One(TargetJson(name: "\"Ga\\u0001me\"")));
    AddInvalid("close-name-nel", One(TargetJson(name: "\"Ga\\u0085me\"")));
    AddInvalid("close-name-261", One(TargetJson(name: "\"" + new string('n', 261) + "\"")));
    AddInvalid("close-name-null", One(TargetJson(name: "null")));
    AddInvalid("close-target-missing-name", One("{\"processId\":41,\"startTimeUtcTicks\":638900000000000000}"));
    AddInvalid("close-target-unknown-member", One("{\"processId\":41,\"startTimeUtcTicks\":638900000000000000,\"name\":\"Game\",\"extra\":1}"));
    AddInvalid("request-array", $"[\"query\",2,\"{Id}\"]");
    AddInvalid("request-null", "null");
    AddInvalid("request-string", "\"query\"");
    AddInvalid("request-truncated", "{\"operation\":\"query\"");
    AddInvalid("request-trailing-object", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id}\"}}{{}}");
    AddInvalid("lone-surrogate-name", One(TargetJson(name: "\"Ga\\ud800me\"")));
    AddInvalid("guid-braces", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{{{Id}}}\"}}");
    AddInvalid("guid-no-hyphens", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id.Replace("-", "")}\"}}");

    // Managed reads these but never writes them; native refuses them. They
    // are printed as observations, not fixture rows.
    var lenient = new List<(string Name, string Json)>
    {
        ("duplicate-member", $"{{\"operation\":\"query\",\"version\":2,\"version\":2,\"requestId\":\"{Id}\"}}"),
        ("pascal-case-members", $"{{\"operation\":\"query\",\"Version\":2,\"RequestId\":\"{Id}\"}}"),
        ("version-as-string", $"{{\"operation\":\"query\",\"version\":\"2\",\"requestId\":\"{Id}\"}}"),
        ("pid-as-string", One(TargetJson("\"41\""))),
    };
    // Managed accepts and native accepts too (native echoes lower case).
    var tolerated = new List<(string Name, string Json)>
    {
        ("guid-upper-case", $"{{\"operation\":\"query\",\"version\":2,\"requestId\":\"{Id.ToUpperInvariant()}\"}}"),
        ("whitespace-around", $" {{ \"operation\" : \"query\" , \"version\" : 2 , \"requestId\" : \"{Id}\" }} "),
    };

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var lines = new List<string>();
    foreach ((string name, string json) in lenient.Concat(tolerated))
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
            SessionGpuRequest request = await DecodeRequestAsync(frame);
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

// Managed dispatcher responses for scripted scans and action results. Each
// row carries the scan and the response the dispatcher wrote, so the native
// test can feed the same scan and results through its own response mapping.
static async Task<int> EmitDispatchAsync(string outPath)
{
    Guid id = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
    const int V = SessionGpuIpc.ProtocolVersion;
    const long Ticks = 638_900_000_000_000_000;
    const string Protected = "OpenBlade does not close Windows or OpenBlade processes.";
    static WindowsGpuApp App(int pid, string name = "Game", string? block = null, double utilization = 12.5) =>
        new(pid, Ticks + pid, name, utilization, block is null, block);
    static SessionGpuAppIdentity Target(WindowsGpuApp app) => new(app.ProcessId, app.StartTimeUtcTicks, app.Name);
    static WindowsGpuAppActionResult Ok(WindowsGpuApp app) => new(app.ProcessId, app.StartTimeUtcTicks, true, null);
    static WindowsGpuAppActionResult Fail(WindowsGpuApp app, string error) => new(app.ProcessId, app.StartTimeUtcTicks, false, error);
    WindowsGpuApp game = App(41), tool = App(43, "Tool"), shell = App(44, "explorer", Protected);
    WindowsGpuAppScan Scan(params WindowsGpuApp[] apps) => new(true, null, apps);
    var unavailable = new WindowsGpuAppScan(false, "Windows GPU activity counters are unavailable.", []);
    var many = Scan([.. Enumerable.Range(1, 130).Select(pid => App(pid, utilization: pid % 2 == 0 ? 0 : 100))]);

    // Each action script receives the matched apps and the operation's token
    // source, so it can stop the operation part way through like Stop does.
    var cases = new List<(string Name, SessionGpuRequest Request, WindowsGpuAppScan Scan,
        Func<IReadOnlyList<WindowsGpuApp>, CancellationTokenSource, IReadOnlyList<WindowsGpuAppActionResult>> Act)>
    {
        ("query", new QueryGpuAppsRequest(V, id), Scan(game, shell), NoAction),
        ("query-empty", new QueryGpuAppsRequest(V, id), Scan(), NoAction),
        ("query-unavailable", new QueryGpuAppsRequest(V, id), unavailable, NoAction),
        ("query-130-apps", new QueryGpuAppsRequest(V, id), many, NoAction),
        ("close-success", new CloseGpuAppsRequest(V, id, false, [Target(game)]), Scan(game, tool),
            (apps, _) => [.. apps.Select(Ok)]),
        ("force-success", new CloseGpuAppsRequest(V, id, true, [Target(game)]), Scan(game),
            (apps, _) => [.. apps.Select(Ok)]),
        ("close-unavailable", new CloseGpuAppsRequest(V, id, false, [Target(game)]), unavailable, NoAction),
        ("close-stale-identity", new CloseGpuAppsRequest(V, id, false,
            [new SessionGpuAppIdentity(41, Ticks, "Game"), new SessionGpuAppIdentity(43, Ticks + 43, "Renamed"), Target(App(45))]),
            Scan(game, tool), NoAction),
        ("close-partial", new CloseGpuAppsRequest(V, id, false, [Target(App(40)), Target(game), Target(tool), Target(shell)]),
            Scan(game, tool, shell),
            (apps, _) => [Ok(apps[0]), Fail(apps[1], "The application has no window that can be closed gracefully."), Fail(apps[2], Protected)]),
        ("close-all-failed", new CloseGpuAppsRequest(V, id, false, [Target(game), Target(tool)]), Scan(game, tool),
            (apps, _) => [.. apps.Select(app => Fail(app, "Windows could not close this process."))]),
        ("close-stopped-after-first", new CloseGpuAppsRequest(V, id, false, [Target(App(40)), Target(game), Target(tool)]),
            Scan(game, tool),
            (apps, source) => { source.Cancel(); return [Ok(apps[0])]; }),
        ("close-stopped-before-any", new CloseGpuAppsRequest(V, id, false, [Target(game), Target(tool)]), Scan(game, tool),
            (_, source) => { source.Cancel(); return []; }),
        ("close-130-apps", new CloseGpuAppsRequest(V, id, false, [Target(many.Apps[129])]), many,
            (apps, _) => [.. apps.Select(Ok)]),
    };

    var lines = new List<string>();
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    foreach (var (name, request, scan, act) in cases)
    {
        using var source = new CancellationTokenSource();
        var dispatcher = new SessionGpuRequestDispatcher(new ScriptedManager(scan, apps => act(apps, source)));
        SessionGpuResponse response = await dispatcher.DispatchAsync(request, source.Token);
        using var stream = new MemoryStream();
        await SessionGpuIpcCodec.WriteResponseAsync(stream, response);
        byte[] frame = stream.ToArray();
        byte[] encodedRequest = await EncodeRequestAsync(request);
        var row = new JsonObject
        {
            ["case"] = name,
            ["request"] = Convert.ToBase64String(encodedRequest),
            ["scan"] = JsonSerializer.SerializeToNode(scan, options),
            ["response"] = JsonNode.Parse(frame.AsSpan(4)),
        };
        lines.Add(row.ToJsonString());
        Console.WriteLine($"EMIT {name}: status {response.Status}, {response.Apps.Count} apps, {response.Results.Count} results");
    }
    File.WriteAllText(outPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"Wrote {lines.Count} managed dispatcher rows.");
    return 0;

    static IReadOnlyList<WindowsGpuAppActionResult> NoAction(IReadOnlyList<WindowsGpuApp> apps, CancellationTokenSource source) =>
        apps.Count == 0 ? [] : throw new InvalidOperationException("Unexpected action.");
}

// Response frames written by the native endpoint tests: each must decode
// with the managed codec (its validation included), carry the fixed request
// ID and bind every member.
static async Task<int> VerifyResponsesAsync(string path)
{
    var corpus = new FileInfo(path);
    Require(corpus.Length is > 0 and <= 16 * 1024 * 1024, "Corpus size is invalid.");
    Guid requestId = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (string line in await File.ReadAllLinesAsync(corpus.FullName))
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement item = doc.RootElement;
        Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == 2, "Row must have case and frame.");
        string name = item.GetProperty("case").GetString() ?? throw new InvalidDataException("Missing case.");
        Require(seen.Add(name), "Duplicate case: " + name);
        byte[] frame = Convert.FromBase64String(item.GetProperty("frame").GetString() ?? string.Empty);
        Require(frame.Length is > 4 and <= SessionGpuIpc.MaximumMessageBytes + 4
            && BinaryPrimitives.ReadInt32LittleEndian(frame) == frame.Length - 4, "Frame length is invalid: " + name);
        using var stream = new MemoryStream(frame, writable: false);
        SessionGpuResponse response = await SessionGpuIpcCodec.ReadResponseAsync(stream);
        Require(stream.Position == stream.Length && response.RequestId == requestId, "Response envelope mismatch: " + name);
        // Every native member is bound: the decoded record re-serializes to the
        // same JSON values. A managed write is not required to fit: managed
        // escapes non-ASCII text, so a frame the native writer trimmed to the
        // byte cap can exceed it when managed re-encodes it.
        Require(JsonNode.DeepEquals(JsonNode.Parse(frame.AsSpan(4)), JsonSerializer.SerializeToNode(response, options)),
            "Decoded response differs from the native frame: " + name);
        Console.WriteLine($"PASS {name}: status {response.Status}, {response.Apps.Count} apps, {response.Results.Count} results");
    }
    Require(seen.Count > 0, "Corpus must contain response frames.");
    Console.WriteLine($"PASS {seen.Count} native Session GPU response frames decode with the managed codec.");
    return 0;
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}

internal sealed class ScriptedManager(
    WindowsGpuAppScan scan,
    Func<IReadOnlyList<WindowsGpuApp>, IReadOnlyList<WindowsGpuAppActionResult>> act) : IWindowsGpuAppManager
{
    public Task<WindowsGpuAppScan> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(scan);

    public IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
        IReadOnlyList<WindowsGpuApp> apps, CancellationToken cancellationToken = default) => act(apps);

    public IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
        IReadOnlyList<WindowsGpuApp> apps, CancellationToken cancellationToken = default) => act(apps);
}
