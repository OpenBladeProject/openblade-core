using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    private const string WindowsReplayFile = "WindowsReplay/v1/service.json";

    [Fact]
    public void WindowsReplay_FrozenWireInventoryMatchesManagedRegistry()
    {
        using JsonDocument fixture = ReadWindowsReplay();
        JsonElement root = fixture.RootElement;
        JsonElement protocol = root.GetProperty("protocol");
        Assert.Equal(OpenBladeIpc.ProtocolVersion, protocol.GetProperty("version").GetInt32());
        Assert.Equal(OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion, protocol.GetProperty("compatibilityVersion").GetInt32());
        Assert.Equal(OpenBladeIpc.PipeName, protocol.GetProperty("pipeName").GetString());
        Assert.Equal(OpenBladeIpc.MaximumMessageBytes, protocol.GetProperty("maxMessageBytes").GetInt32());
        Assert.Equal(OpenBladeIpc.MaximumDiagnosticArchiveBytes, protocol.GetProperty("maxDiagnosticArchiveBytes").GetInt32());
        Assert.Equal(OpenBladeIpc.MaximumDiagnosticResponseBytes, protocol.GetProperty("maxDiagnosticResponseBytes").GetInt32());

        IEnumerable<string?> operations = root.GetProperty("operationFamilies").EnumerateObject()
            .SelectMany(family => family.Value.EnumerateArray()).Select(value => value.GetString());
        Assert.Equal(RegisteredDiscriminators<ServiceRequest>(), operations.Order(StringComparer.Ordinal));
        Assert.Equal(RegisteredDiscriminators<ServiceServerFrame>(), root.GetProperty("serverFrames").EnumerateArray()
            .Select(value => value.GetString()).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(WindowsReplayCases), "requests")]
    public async Task WindowsReplay_DecodesRequestsAndWritesCanonicalFrames(string id)
    {
        using JsonDocument fixture = ReadWindowsReplay();
        JsonElement example = ReplayCase(fixture, "requests", id);
        using MemoryStream stream = OpenReplayFrame(example);

        if (example.TryGetProperty("failure", out JsonElement failure))
        {
            await Assert.ThrowsAsync(ReplayExceptionType(failure), () =>
                IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
            return;
        }

        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None);
        Assert.Equal(stream.Length, stream.Position);
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(encoded, request, CancellationToken.None);
        AssertReplayFrame(example.GetProperty("output"), encoded);
    }

    [Theory]
    [MemberData(nameof(WindowsReplayCases), "responses")]
    public async Task WindowsReplay_PreservesTypedResultsAndProtocolFailures(string id)
    {
        using JsonDocument fixture = ReadWindowsReplay();
        JsonElement example = ReplayCase(fixture, "responses", id);
        using MemoryStream stream = OpenReplayFrame(example);
        if (example.TryGetProperty("failure", out JsonElement failure))
        {
            await Assert.ThrowsAsync(ReplayExceptionType(failure), () =>
                IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
            return;
        }

        ServiceServerFrame response = await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None);
        Assert.Equal(stream.Length, stream.Position);
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(encoded, response, CancellationToken.None);
        AssertReplayFrame(example.GetProperty("input"), encoded);
    }

    [Fact]
    public async Task WindowsReplay_StatusProjectionSeparatesDesiredAndConfirmedState()
    {
        using JsonDocument fixture = ReadWindowsReplay();
        ServiceStatusSnapshot status = CreateStatus() with
        {
            WritesBlocked = false,
            ConfirmedState = new ConfirmedDeviceState(null, null, null, null) { HyperBoostActive = null },
        };
        var frame = new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status);
        foreach (JsonElement expected in fixture.RootElement.GetProperty("statusProjection").EnumerateArray())
        {
            int protocol = expected.GetProperty("protocol").GetInt32();
            using var encoded = new MemoryStream();
            if (protocol == OpenBladeIpc.ProtocolVersion)
            {
                await IpcMessageCodec.WriteServerFrameAsync(encoded, frame, CancellationToken.None);
                using JsonDocument golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                    AppContext.BaseDirectory, "WindowsReplay/v1/service-status-event.json")));
                AssertReplayFrame(golden.RootElement, encoded);
                using MemoryStream replay = FrameJson(golden.RootElement.GetRawText());
                Assert.IsType<ServiceStatusEvent>(
                    await IpcMessageCodec.ReadServerFrameAsync(replay, CancellationToken.None));
            }
            else
            {
                Assert.Equal(OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion, protocol);
                await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(encoded, frame, CancellationToken.None);
            }

            JsonObject actual = FramedJsonObject(encoded);
            Assert.Equal("statusEvent", actual["frame"]!.GetValue<string>());
            Assert.Equal(protocol, actual["version"]!.GetValue<int>());
            JsonObject body = Assert.IsType<JsonObject>(actual["status"]);
            AssertReplayMember(expected, "writeBlocked", body["writesBlocked"]);
            JsonObject confirmed = Assert.IsType<JsonObject>(body["confirmedState"]);
            Assert.True(confirmed.ContainsKey("hyperBoostActive"));
            AssertReplayMember(expected, "confirmedHyperBoostActive", confirmed["hyperBoostActive"]);
            AssertReplayMember(expected, "confirmedPerformance", confirmed["performance"]);
            AssertReplayMember(expected, "desiredPerformance", body["desiredState"]!["performance"]!["value"]);
        }
    }

    public static TheoryData<string> WindowsReplayCases(string group)
    {
        using JsonDocument fixture = ReadWindowsReplay();
        var cases = new TheoryData<string>();
        foreach (JsonElement example in fixture.RootElement.GetProperty(group).EnumerateArray())
        {
            cases.Add(example.GetProperty("id").GetString()!);
        }

        return cases;
    }

    private static JsonDocument ReadWindowsReplay() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, WindowsReplayFile)));

    private static JsonElement ReplayCase(JsonDocument fixture, string group, string id) =>
        fixture.RootElement.GetProperty(group).EnumerateArray().Single(example =>
            example.GetProperty("id").GetString() == id);

    private static MemoryStream OpenReplayFrame(JsonElement example)
    {
        if (example.TryGetProperty("declaredLength", out JsonElement declaredLength))
        {
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, declaredLength.GetInt32());
            return new MemoryStream(prefix);
        }

        return FrameJson(example.TryGetProperty("json", out JsonElement json)
            ? json.GetString()!
            : example.GetProperty("input").GetRawText());
    }

    private static Type ReplayExceptionType(JsonElement failure) => failure.GetString() switch
    {
        "versionMismatch" => typeof(IpcProtocolVersionMismatchException),
        "invalidData" => typeof(InvalidDataException),
        "endOfStream" => typeof(EndOfStreamException),
        _ => throw new InvalidDataException("Unknown replay failure category."),
    };

    private static void AssertReplayFrame(JsonElement expected, MemoryStream stream)
    {
        Assert.Equal(stream.Length - sizeof(int), BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        JsonObject actual = FramedJsonObject(stream);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), actual), actual.ToJsonString());
    }

    private static void AssertReplayMember(JsonElement expected, string name, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetProperty(name).GetRawText()), actual), name);

    private static IEnumerable<string?> RegisteredDiscriminators<T>() =>
        typeof(T).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => Assert.IsType<string>(attribute.TypeDiscriminator))
            .Order(StringComparer.Ordinal);
}
