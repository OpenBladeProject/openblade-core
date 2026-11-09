using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class WindowsReplaySessionTests
{
    [Fact]
    public void WindowsReplay_InventoryCoversEachSessionOperation()
    {
        using JsonDocument fixture = Open();
        JsonElement families = fixture.RootElement.GetProperty("families");
        CheckInventory<SessionDisplayRequest>(families.GetProperty("display"),
            SessionDisplayIpc.ProtocolVersion, SessionDisplayIpc.MaximumMessageBytes, SessionDisplayIpc.PipeNamePrefix);
        CheckInventory<SessionGpuRequest>(families.GetProperty("gpu"),
            SessionGpuIpc.ProtocolVersion, SessionGpuIpc.MaximumMessageBytes, SessionGpuIpc.PipeNamePrefix);
        CheckInventory<SessionAudioRequest>(families.GetProperty("audio"),
            SessionAudioIpc.ProtocolVersion, SessionAudioIpc.MaximumMessageBytes, SessionAudioIpc.PipeNamePrefix);
        CheckInventory<SessionBatteryRequest>(families.GetProperty("battery"),
            SessionBatteryIpc.ProtocolVersion, SessionBatteryIpc.MaximumMessageBytes, SessionBatteryIpc.PipeNameSuffix);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WindowsReplay_DecodesAndEncodesSessionFrames(string family, string id)
    {
        using JsonDocument fixture = Open();
        JsonElement example = fixture.RootElement.GetProperty("families").GetProperty(family)
            .GetProperty("requests").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        using MemoryStream stream = OpenFrame(example);
        using var output = new MemoryStream();
        if (example.TryGetProperty("failure", out JsonElement failure))
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => ReplayAsync(family, stream, output));
            switch (failure.GetString())
            {
                case "versionMismatch":
                    Assert.Contains("protocol version", error.Message, StringComparison.Ordinal);
                    Assert.Null(error.InnerException);
                    break;
                case "lengthOutOfRange":
                    Assert.Contains("message length", error.Message, StringComparison.Ordinal);
                    Assert.Equal(sizeof(int), stream.Position);
                    break;
                case "unknownMember":
                    Assert.IsType<JsonException>(error.InnerException);
                    break;
                default:
                    throw new InvalidDataException("Unknown Session replay failure category.");
            }

            Assert.Equal(0, output.Length);
            return;
        }

        await ReplayAsync(family, stream, output);
        Assert.Equal(stream.Length, stream.Position);
        AssertFrame(example.GetProperty("input"), output);
    }

    [Fact]
    public async Task WindowsReplay_LegacyAudioResponseLeavesBaseAudioUnspecified()
    {
        using JsonDocument fixture = Open();
        JsonElement expected = fixture.RootElement.GetProperty("families").GetProperty("audio").GetProperty("legacyResponse");
        byte[] payload = Encoding.UTF8.GetBytes(expected.GetRawText());
        using MemoryStream stream = Frame(payload.Length, payload);
        SessionAudioResponse response = await SessionAudioIpcCodec.ReadResponseAsync(stream);
        Assert.Null(response.BaseAudio);
        using var output = new MemoryStream();
        await SessionAudioIpcCodec.WriteResponseAsync(output, response);
        AssertFrame(expected, output);
    }

    public static TheoryData<string, string> Cases()
    {
        using JsonDocument fixture = Open();
        var cases = new TheoryData<string, string>();
        foreach (JsonProperty family in fixture.RootElement.GetProperty("families").EnumerateObject())
        {
            foreach (JsonElement example in family.Value.GetProperty("requests").EnumerateArray())
            {
                cases.Add(family.Name, example.GetProperty("id").GetString()!);
            }
        }

        return cases;
    }

    private static async Task ReplayAsync(string family, Stream stream, Stream output)
    {
        switch (family)
        {
            case "display":
                await SessionDisplayIpcCodec.WriteRequestAsync(output, await SessionDisplayIpcCodec.ReadRequestAsync(stream));
                break;
            case "gpu":
                await SessionGpuIpcCodec.WriteRequestAsync(output, await SessionGpuIpcCodec.ReadRequestAsync(stream));
                break;
            case "audio":
                await SessionAudioIpcCodec.WriteRequestAsync(output, await SessionAudioIpcCodec.ReadRequestAsync(stream));
                break;
            case "battery":
                await SessionBatteryIpcCodec.WriteRequestAsync(output, await SessionBatteryIpcCodec.ReadRequestAsync(stream));
                break;
            default:
                throw new ArgumentException("Unknown replay family.", nameof(family));
        }
    }

    private static void CheckInventory<T>(JsonElement family, int version, int capacity, string pipeAffix)
    {
        Assert.Equal(family.GetProperty("version").GetInt32(), version);
        Assert.Equal(family.GetProperty("maxMessageBytes").GetInt32(), capacity);
        Assert.Equal(family.GetProperty("pipeAffix").GetString(), pipeAffix);
        string? discriminator = typeof(T).GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName;
        string?[] registered = typeof(T).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => Assert.IsType<string>(attribute.TypeDiscriminator))
            .Order(StringComparer.Ordinal).ToArray();
        JsonElement[] valid = family.GetProperty("requests").EnumerateArray()
            .Where(example => !example.TryGetProperty("failure", out _))
            .Select(example => example.GetProperty("input")).ToArray();
        if (discriminator is null)
        {
            Assert.Empty(registered);
            Assert.False(Assert.Single(valid).TryGetProperty("operation", out _));
            return;
        }

        Assert.Equal("operation", discriminator);
        Assert.Equal(registered, valid.Select(input => input.GetProperty(discriminator).GetString())
            .Order(StringComparer.Ordinal));
    }

    private static JsonDocument Open() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "WindowsReplay/v1/session.json")));

    private static MemoryStream OpenFrame(JsonElement example)
    {
        if (example.TryGetProperty("declaredLength", out JsonElement length))
        {
            return Frame(length.GetInt32(), []);
        }

        byte[] payload = Encoding.UTF8.GetBytes(example.GetProperty("input").GetRawText());
        return Frame(payload.Length, payload);
    }

    private static MemoryStream Frame(int length, byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }

    private static void AssertFrame(JsonElement expected, MemoryStream output)
    {
        byte[] actual = output.ToArray();
        Assert.Equal(actual.Length - sizeof(int), BinaryPrimitives.ReadInt32LittleEndian(actual));
        JsonNode? body = JsonNode.Parse(actual.AsSpan(sizeof(int)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), body), body?.ToJsonString());
    }
}
