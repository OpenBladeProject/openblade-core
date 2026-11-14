using System.Buffers.Binary;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

// Offline only: actual dispatcher frames, actual managed strict codec and XML parser.
// No provider, powercfg, child process, pipe endpoint, or installed product is run.
if (args.Length != 1)
{
    throw new ArgumentException("Usage: BatteryReportParity <native-dispatcher-frames.jsonl>");
}
const long maximumCorpusBytes = 16 * 1024 * 1024;
var corpus = new FileInfo(args[0]);
Require(corpus.Length is > 0 and <= maximumCorpusBytes, "Corpus size is invalid.");
var expected = new Dictionary<string, SessionBatteryOperationStatus>(StringComparer.Ordinal)
{
    ["success"] = SessionBatteryOperationStatus.Succeeded,
    ["malformed"] = SessionBatteryOperationStatus.Unavailable,
    ["oversized"] = SessionBatteryOperationStatus.Unavailable,
    ["output-custody"] = SessionBatteryOperationStatus.Unavailable,
    ["process-exit"] = SessionBatteryOperationStatus.Unavailable,
    ["acquisition-timeout"] = SessionBatteryOperationStatus.Unavailable,
    ["scratch"] = SessionBatteryOperationStatus.Unavailable,
    ["cleanup"] = SessionBatteryOperationStatus.Unavailable,
    ["start"] = SessionBatteryOperationStatus.Failed,
    ["wait"] = SessionBatteryOperationStatus.Failed,
};
Guid requestId = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
byte[] xml = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "timeline.xml"));
Require(WindowsBatteryReportParser.TryParse(xml, out WindowsBatteryReport? managed), "Managed timeline parse failed.");
var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (string line in await File.ReadAllLinesAsync(corpus.FullName))
{
    Require(line.Length is > 0 and <= 2 * 1024 * 1024, "Invalid corpus row length.");
    IpcJsonValidation.ValidateNoDuplicateMembers(System.Text.Encoding.UTF8.GetBytes(line));
    using JsonDocument row = JsonDocument.Parse(line);
    JsonElement item = row.RootElement;
    Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == 3,
        "Corpus row must have only case, expectedStatus and frame.");
    string name = item.GetProperty("case").GetString() ?? throw new InvalidDataException("Missing case.");
    Require(expected.TryGetValue(name, out SessionBatteryOperationStatus status) && seen.Add(name), "Unknown or duplicate case: " + name);
    Require(item.GetProperty("expectedStatus").GetInt32() == (int)status, "Corpus status disagrees with independent oracle: " + name);
    JsonElement bytes = item.GetProperty("frame");
    Require(bytes.ValueKind == JsonValueKind.Array && bytes.GetArrayLength() is > 4 and <= SessionBatteryIpc.MaximumMessageBytes + 4,
        "Frame length is invalid: " + name);
    byte[] frame = bytes.EnumerateArray().Select(static value => value.GetByte()).ToArray();
    Require(BinaryPrimitives.ReadInt32LittleEndian(frame) == frame.Length - 4, "Frame prefix mismatch: " + name);
    using var stream = new MemoryStream(frame, writable: false);
    SessionBatteryResponse response = await SessionBatteryIpcCodec.ReadResponseAsync(stream);
    Require(stream.Position == stream.Length, "Decoder did not consume complete frame: " + name);
    Require(response.Version == SessionBatteryIpc.ProtocolVersion && response.RequestId == requestId && response.Status == status,
        "Response envelope mismatch: " + name);
    string? error = status switch
    {
        SessionBatteryOperationStatus.Unavailable => "Windows could not provide a battery report.",
        SessionBatteryOperationStatus.Failed => "The Session Agent could not read the Windows battery report.",
        _ => null,
    };
    Require(response.Error == error && (response.Report is null) == (status != SessionBatteryOperationStatus.Succeeded),
        "Response error/report nullness mismatch: " + name);
    if (status == SessionBatteryOperationStatus.Succeeded)
    {
        EqualReport(managed!, response.Report!);
    }
    using var roundtrip = new MemoryStream();
    await SessionBatteryIpcCodec.WriteResponseAsync(roundtrip, response);
    roundtrip.Position = 0;
    SessionBatteryResponse decoded = await SessionBatteryIpcCodec.ReadResponseAsync(roundtrip);
    Require(roundtrip.Position == roundtrip.Length && decoded.Version == response.Version && decoded.RequestId == response.RequestId
        && decoded.Status == response.Status && decoded.Error == response.Error, "Managed roundtrip mismatch: " + name);
    if (response.Report is not null) { EqualReport(response.Report, decoded.Report!); }
    Console.WriteLine("PASS " + name);
}
Require(seen.SetEquals(expected.Keys), "Corpus must contain the complete exact ten-case dispatcher set.");
Console.WriteLine("PASS actual native Battery.v1 dispatcher frames match managed codec/parser (10 cases).");

static void EqualReport(WindowsBatteryReport expected, WindowsBatteryReport actual)
{
    Require(actual is not null && expected.Source == actual.Source && expected.GeneratedAtUtc == actual.GeneratedAtUtc
        && expected.WindowStartUtc == actual.WindowStartUtc && expected.WindowEndUtc == actual.WindowEndUtc
        && expected.Status == actual.Status && expected.Intervals.SequenceEqual(actual.Intervals),
        "Native report differs from actual managed XML parser.");
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}
