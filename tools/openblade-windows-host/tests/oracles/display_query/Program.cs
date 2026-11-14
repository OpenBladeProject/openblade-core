using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Session;

// Offline only: consume actual native dispatcher frames with the production
// managed Display codec. No OS supplier, provider, process or endpoint runs.
if (args.Length != 1)
{
    throw new ArgumentException("Usage: DisplayQueryParity <native-dispatcher-frames.jsonl>");
}
var corpus = new FileInfo(args[0]);
Require(corpus.Length is > 0 and <= 16 * 1024 * 1024, "Corpus size is invalid.");
Guid requestId = Guid.Parse("9f876742-1234-4321-abcd-d3ea3e1cf102");
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (string line in await File.ReadAllLinesAsync(corpus.FullName))
{
    Require(line.Length is > 0 and <= 2 * 1024 * 1024, "Invalid corpus row length.");
    // Corpus integrity only. Display's production codec has no duplicate scan.
    IpcJsonValidation.ValidateNoDuplicateMembers(System.Text.Encoding.UTF8.GetBytes(line));
    using JsonDocument row = JsonDocument.Parse(line);
    JsonElement item = row.RootElement;
    Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == 4,
        "Corpus row must have only case, expectedStatus, expectedState and frame.");
    string name = item.GetProperty("case").GetString() ?? throw new InvalidDataException("Missing case.");
    Require(!string.IsNullOrWhiteSpace(name) && seen.Add(name) && seen.Count <= 64, "Unknown or duplicate case.");
    Require(item.GetProperty("expectedStatus").GetInt32() == (int)SessionDisplayOperationStatus.Succeeded,
        "Read-only query observation must use Succeeded, including unavailable state: " + name);
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
        && response.Status == SessionDisplayOperationStatus.Succeeded && response.Error is null,
        "Response envelope mismatch: " + name);
    Require(response.State.BrightnessPercent is null && response.State.SupportedBrightnessLevels.Count == 0,
        "Partial display query must not claim brightness: " + name);
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
Require(seen.Count > 0, "Corpus must contain query frames.");
Console.WriteLine($"PASS actual native partial Display query dispatcher frames match managed codec ({seen.Count} cases).");

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}
