using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent.Tests;

// The native Session Agent's session power relay is pinned against managed:
// the request frames IpcMessageCodec writes for reportSessionPowerTransition,
// the bodies it refuses before writing a byte, and the requests the real
// SessionPowerTransitionRelay sends for a sequence of native power
// notifications. The Rust replay reads the same file.
public sealed class SessionPowerTransitionRelayNativeVectorTests
{
    private const string VectorFile = "session-power-relay-vectors.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_SESSION_POWER_RELAY_VECTORS";
    private const long Started = 100;
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Guid Instance = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private const string Comment =
        "Recorded by SessionPowerTransitionRelayNativeVectorTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_SESSION_POWER_RELAY_VECTORS set to this directory. An encoding "
        + "is one reportSessionPowerTransition request; expect is the payload IpcMessageCodec writes "
        + "after the length prefix, or refused when it writes nothing. A notification case feeds native "
        + "power notification types to the relay (instance 11111111-2222-4333-8444-555555555555, started "
        + "and timestamp at QPC 100) over a sink that completes each report at once; expect lists each "
        + "sent request's sequence and direction.";

    private const string Encodings = """
        [
        {"name":"suspend","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":100,"sequence":1,"timestamp":100,"suspending":true},
        {"name":"resume","request":"00000000-0000-0000-0000-000000000001","instance":"ffffffff-ffff-ffff-ffff-ffffffffffff","started":1,"sequence":2,"timestamp":250,"suspending":false},
        {"name":"wide-values","request":"0f8fad5b-d9cb-469f-a165-70867728950e","instance":"7c9e6679-7425-40de-944b-e07fc1f90ae7","started":9007199254740993,"sequence":9223372036854775807,"timestamp":9223372036854775807,"suspending":false},
        {"name":"refused/empty-instance","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"00000000-0000-0000-0000-000000000000","started":100,"sequence":1,"timestamp":100,"suspending":true},
        {"name":"refused/zero-start","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":0,"sequence":1,"timestamp":100,"suspending":true},
        {"name":"refused/negative-start","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":-1,"sequence":1,"timestamp":100,"suspending":true},
        {"name":"refused/zero-sequence","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":100,"sequence":0,"timestamp":100,"suspending":true},
        {"name":"refused/negative-sequence","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":100,"sequence":-1,"timestamp":100,"suspending":false},
        {"name":"refused/timestamp-before-start","request":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","instance":"11111111-2222-4333-8444-555555555555","started":100,"sequence":1,"timestamp":99,"suspending":true}
        ]
        """;

    private const string Notifications = """
        [
        {"name":"alternating","types":[4,18,4,7]},
        {"name":"duplicates-and-unrelated","types":[0,10,32787,4,4,18,7,4]},
        {"name":"resume-variants","types":[18,7,18]},
        {"name":"resume-first","types":[7,4,4,4,18]},
        {"name":"unrelated-only","types":[0,1,2,3,5,6,8,9,10,32787]}
        ]
        """;

    [Fact]
    public async Task NativeVectors_MatchManagedRelayAndCodec()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, VectorFile)
            : Path.Combine(output, VectorFile);
        var expected = new JsonObject
        {
            ["comment"] = Comment,
            ["encodings"] = JsonNode.Parse(Encodings),
            ["notifications"] = JsonNode.Parse(Notifications),
        };
        JsonObject root = string.IsNullOrEmpty(output)
            ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
            : expected;

        // The file holds exactly the reviewed cases.
        Assert.True(JsonNode.DeepEquals(InputsOnly(root), InputsOnly(expected)));

        var failures = new List<string>();
        foreach (JsonNode? entry in root["encodings"]!.AsArray())
        {
            Check(entry!.AsObject(), await EncodeAsync(entry.AsObject()));
        }

        foreach (JsonNode? entry in root["notifications"]!.AsArray())
        {
            Check(entry!.AsObject(), Notify(entry.AsObject()));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One case per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(root["comment"]!.ToJsonString(Readable))
                .Append(",\n\"encodings\":[\n")
                .AppendJoin(",\n", root["encodings"]!.AsArray().Select(row => row!.ToJsonString(Readable)))
                .Append("\n],\n\"notifications\":[\n")
                .AppendJoin(",\n", root["notifications"]!.AsArray().Select(row => row!.ToJsonString(Readable)))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Check(JsonObject vector, JsonNode observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                vector["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(vector["expect"], observed))
            {
                failures.Add($"{vector["name"]}: {observed.ToJsonString()}");
            }
        }

        static JsonObject InputsOnly(JsonObject root)
        {
            var inputs = (JsonObject)root.DeepClone();
            inputs.Remove("comment");
            foreach (string set in new[] { "encodings", "notifications" })
            {
                foreach (JsonNode? row in inputs[set]!.AsArray())
                {
                    row!.AsObject().Remove("expect");
                }
            }

            return inputs;
        }
    }

    private static async Task<JsonNode> EncodeAsync(JsonObject vector)
    {
        var request = new ReportSessionPowerTransitionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.Parse((string)vector["request"]!),
            Guid.Parse((string)vector["instance"]!),
            (long)vector["started"]!,
            (long)vector["sequence"]!,
            (long)vector["timestamp"]!,
            (bool)vector["suspending"]!);
        using var stream = new MemoryStream();
        try
        {
            await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            Assert.Equal(0, stream.Length);
            return new JsonObject { ["refused"] = true };
        }

        byte[] frame = stream.ToArray();
        Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame, 0));
        return new JsonObject { ["payload"] = Encoding.UTF8.GetString(frame, 4, frame.Length - 4) };
    }

    private static JsonNode Notify(JsonObject vector)
    {
        var sent = new JsonArray();
        using (var relay = new SessionPowerTransitionRelay(
            Instance,
            Started,
            (request, _) =>
            {
                Assert.Equal(Instance, request.InstanceId);
                Assert.Equal(Started, request.InstanceStartedQpc);
                Assert.Equal(Started, request.TimestampQpc);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);
                Assert.NotEqual(Guid.Empty, request.RequestId);
                sent.Add(new JsonObject
                {
                    ["sequence"] = request.Sequence,
                    ["suspending"] = request.Suspending,
                });
                return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Applied, "completed"));
            },
            () => Started,
            1000))
        {
            foreach (JsonNode? type in vector["types"]!.AsArray())
            {
                relay.OnPowerNotification((uint)type!);
            }
        }

        return sent;
    }
}
