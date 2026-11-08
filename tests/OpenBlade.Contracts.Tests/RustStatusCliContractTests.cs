using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Fact]
    public async Task RustStatusCli_FixedRequestMatchesAndIsAcceptedByProductionCodec()
    {
        string fixture = ReadRustStatusFixture("request.json").TrimEnd();
        var request = new GetStatusRequest(
            OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion,
            Guid.Parse("11111111-2222-4333-8444-555555555555"));
        string actual = JsonSerializer.Serialize<ServiceRequest>(
            request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(51, OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion);
        Assert.Equal("OpenBlade.Service.v2", OpenBladeIpc.PipeName);
        Assert.Equal(65536, OpenBladeIpc.MaximumMessageBytes);
        Assert.Equal(fixture, actual);
        Assert.Equal(89, Encoding.UTF8.GetByteCount(actual));
        using MemoryStream stream = FrameJson(fixture);
        ServiceRequest decoded = await IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(
            stream, CancellationToken.None);
        Assert.Equal(request, Assert.IsType<GetStatusRequest>(decoded));
    }

    [Fact]
    public async Task RustStatusCli_ResponseWhitelistMatchesRealCompatibilityWriter()
    {
        JsonNode expected = JsonNode.Parse(ReadRustStatusFixture("response.json"))!;
        ServiceStatusSnapshot status = CreateStatus() with
        {
            Revision = long.MaxValue,
            DeviceVerified = true,
            ActivePowerSource = PowerSource.UsbC,
            LiveReadings = new LiveReadings(-32768, 2700, null, 65, null),
            ControlAccess = SessionControlAccess.Active,
        };
        var response = new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            true, null, status, null);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(
            stream, response, CancellationToken.None);
        JsonObject actual = FramedJsonObject(stream);

        foreach (string name in new[] { "frame", "version", "requestId", "success", "error", "errorCode", "result", "archive" })
        {
            Assert.True(JsonNode.DeepEquals(expected[name], actual[name]), name);
        }

        foreach (string name in new[] { "revision", "deviceVerified", "activePowerSource", "controlAccess", "liveReadings" })
        {
            Assert.True(JsonNode.DeepEquals(expected["status"]![name], actual["status"]![name]), name);
        }
    }

    private static string ReadRustStatusFixture(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName, "rust", "crates", "openblade-status-cli", "fixtures", fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository fixture root is unavailable.");
    }
}
