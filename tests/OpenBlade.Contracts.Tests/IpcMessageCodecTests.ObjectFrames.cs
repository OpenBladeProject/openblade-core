using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

// Managed evidence for the native object-only Service decoders. Every row of
// tools/openblade-windows-host/tests/fixtures/service-object-frames.json runs
// through the production codec here and through the native decoders in
// tools/openblade-windows-host/src/service_status/object_tests.rs.
public sealed partial class IpcMessageCodecTests
{
    public static TheoryData<string> ObjectFrameRequestCases() => ObjectFrameCases("requests");

    public static TheoryData<string> ObjectFrameServerCases() => ObjectFrameCases("serverFrames");

    [Theory]
    [MemberData(nameof(ObjectFrameRequestCases))]
    public async Task ObjectFrames_ServiceRequestsAreReadOnlyFromObjects(string name)
    {
        JsonObject row = ObjectFrameRow("requests", name);
        string frame = row["frame"]!.GetValue<string>();
        foreach (bool protocol51 in new[] { false, true })
        {
            using MemoryStream stream = FrameJson(frame);
            Task<ServiceRequest> read = protocol51
                ? IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(stream, CancellationToken.None)
                : IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None);
            if (!row["valid"]!.GetValue<bool>())
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => read);
                continue;
            }

            // Valid rows are exactly the bytes the managed writer produces.
            ServiceRequest request = await read;
            using MemoryStream encoded = new();
            await IpcMessageCodec.WriteRequestAsync(encoded, request, CancellationToken.None);
            Assert.Equal(frame, Encoding.UTF8.GetString(encoded.ToArray(), sizeof(int), (int)encoded.Length - sizeof(int)));
        }
    }

    [Theory]
    [MemberData(nameof(ObjectFrameServerCases))]
    public async Task ObjectFrames_ServiceServerFramesAreReadOnlyFromObjects(string name)
    {
        JsonObject row = ObjectFrameRow("serverFrames", name);
        string payload = ObjectServerFrame(row);
        using MemoryStream stream = FrameJson(payload);
        Task<ServiceServerFrame> read = IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None);
        if (row["valid"]!.GetValue<bool>())
        {
            // Valid rows, edited ones included, are what the managed writer
            // produces for the frame they decode to.
            ServiceServerFrame frame = await read;
            Assert.Equal(OpenBladeIpc.ProtocolVersion, frame.Version);
            using MemoryStream encoded = new();
            await IpcMessageCodec.WriteServerFrameAsync(encoded, frame, CancellationToken.None);
            JsonNode written = JsonNode.Parse(Encoding.UTF8.GetString(
                encoded.ToArray(), sizeof(int), (int)encoded.Length - sizeof(int)))!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), written), name);
        }
        else if (row["managedError"]?.GetValue<string>() == "versionMismatch")
        {
            await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() => read);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => read);
        }
    }

    private static TheoryData<string> ObjectFrameCases(string group)
    {
        TheoryData<string> cases = [];
        foreach (JsonNode? row in ObjectFrameRows(group))
        {
            cases.Add(row!["case"]!.GetValue<string>());
        }

        return cases;
    }

    private static JsonObject ObjectFrameRow(string group, string name) =>
        ObjectFrameRows(group).Single(row => row!["case"]!.GetValue<string>() == name)!.AsObject();

    private static JsonArray ObjectFrameRows(string group)
    {
        JsonNode fixture = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "service-object-frames.json")))!;
        return (group == "requests" ? fixture["requests"] : fixture["serverFrames"]!["rows"])!.AsArray();
    }

    // The managed-written base frame after the row's edit: the object at path
    // replaced by an array of its listed (or all) members, or the member at
    // path replaced by the row's JSON text.
    private static string ObjectServerFrame(JsonObject row)
    {
        (string file, int index) = row["base"]!.GetValue<string>() switch
        {
            "get-status" => ("get-status-v91.frames", 1),
            "observe-state" => ("observe-state-v91.frames", 1),
            "status-event" => ("subscribe-status-v91.frames", 2),
            string other => throw new InvalidOperationException("Unknown base " + other),
        };
        byte[] frames = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, file));
        int offset = 0;
        for (int skipped = 0; skipped < index; skipped++)
        {
            offset += sizeof(int) + BinaryPrimitives.ReadInt32LittleEndian(frames.AsSpan(offset));
        }

        string payload = Encoding.UTF8.GetString(
            frames,
            offset + sizeof(int),
            BinaryPrimitives.ReadInt32LittleEndian(frames.AsSpan(offset)));
        if (row["positional"] is null && row["value"] is null)
        {
            return payload;
        }

        JsonNode frame = JsonNode.Parse(payload)!;
        JsonObject target = frame.AsObject();
        JsonArray path = row["path"]!.AsArray();
        for (int depth = 0; depth < path.Count - 1; depth++)
        {
            target = target[path[depth]!.GetValue<string>()]!.AsObject();
        }

        // A value row splices its JSON text in place of the member, unparsed.
        if (row["value"] is JsonNode value)
        {
            const string Mark = "\"object-frame-value\"";
            target[path[^1]!.GetValue<string>()] = Mark.Trim('"');
            string marked = frame.ToJsonString();
            Assert.Equal(2, marked.Split(Mark).Length);
            return marked.Replace(Mark, value.GetValue<string>(), StringComparison.Ordinal);
        }

        JsonObject member = path.Count == 0
            ? target
            : target[path[^1]!.GetValue<string>()]!.AsObject();
        IEnumerable<string> keys = row["positional"] is JsonArray members
            ? members.Select(name => name!.GetValue<string>())
            : row["positional"]!.GetValue<string>() == "*"
                ? member.Select(pair => pair.Key).ToList()
                : throw new InvalidOperationException("Unknown positional form");
        JsonArray positional = [];
        foreach (string key in keys)
        {
            Assert.True(member.ContainsKey(key), key);
            positional.Add(member[key]?.DeepClone());
        }

        if (path.Count == 0)
        {
            return positional.ToJsonString();
        }

        target[path[^1]!.GetValue<string>()] = positional;
        return frame.ToJsonString();
    }
}
