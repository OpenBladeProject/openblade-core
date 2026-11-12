using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBlade.Contracts.Tests;

public sealed class NativeThermalMutationResponseTests
{
    [Fact]
    public async Task ActualNativeMutationResponsesUseManagedV80CodecAndOutcomes()
    {
        // The evidence runner extracts actual emitted Rust test replies into a
        // held, hashed private input. There is deliberately no product fallback.
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_THERMAL_RESPONSES")
            ?? throw new InvalidOperationException("The prelisted native response input is required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var seen = new HashSet<ControlOperationStatus>();
        bool sawConfirmedPreset = false;
        bool sawPartialCustom = false;
        var decodeErrors = new List<string>();
        int index = 0;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            int frameIndex = index++;
            byte[] bytes = Encoding.UTF8.GetBytes(element.GetRawText());
            byte[] framed = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, bytes.Length);
            bytes.CopyTo(framed, 4);
            using var stream = new MemoryStream(framed, writable: false);
            ServiceResponse response;
            try
            {
                response = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
            }
            catch (Exception exception)
            {
                decodeErrors.Add($"Frame {frameIndex}: {exception.Message} {exception.InnerException?.Message}");
                continue;
            }
            Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
            Assert.NotEqual(Guid.Empty, response.RequestId);
            if (!response.Success)
            {
                Assert.False(string.IsNullOrWhiteSpace(response.Error));
                Assert.Null(response.Status);
                Assert.Null(response.Result);
                Assert.Null(response.Archive);
                continue;
            }
            Assert.Null(response.Error);
            if (response.Status is { } status)
            {
                Assert.Null(response.Result);
                Assert.Null(response.Archive);
                if (status.ConfirmedState is { Performance: PerformancePreset.Custom, CustomPerformance: null } partial)
                {
                    Assert.Equal(FanMode.Maximum, partial.Fans);
                    sawPartialCustom = true;
                    // Derive both negative inputs from the actual native frame.
                    // No inferred CPU/GPU settings are added to its positive case.
                    JsonNode desired = JsonNode.Parse(element.GetRawText())!;
                    desired["status"]!["desiredState"]!["performance"]!["value"] = (int)PerformancePreset.Custom;
                    desired["status"]!["desiredState"]!["customPerformance"] = null;
                    using var desiredFrame = FrameJson(desired.ToJsonString());
                    await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(desiredFrame, CancellationToken.None));
                    JsonNode malformed = JsonNode.Parse(element.GetRawText())!;
                    malformed["status"]!["confirmedState"]!["customPerformance"] = new JsonObject { ["cpu"] = 99, ["gpu"] = 2 };
                    using var malformedFrame = FrameJson(malformed.ToJsonString());
                    await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(malformedFrame, CancellationToken.None));
                }
                if (status.ConfirmedState?.Performance is PerformancePreset.Balanced or PerformancePreset.Performance)
                {
                    Assert.Null(status.Capabilities);
                    // A later metadata projection keeps confirmed presentation,
                    // but cannot renew the mutation's independent read authority.
                    Assert.True(status.WritesBlocked);
                    sawConfirmedPreset = true;
                }
                continue;
            }
            var result = Assert.IsType<ControlOperationResult>(response.Result);
            Assert.Null(response.Status);
            Assert.Null(response.Archive);
            seen.Add(result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            if (result.Status == ControlOperationStatus.Applied)
            {
                Assert.Contains("GPU", result.Message, StringComparison.Ordinal);
            }
        }
        Assert.True(decodeErrors.Count == 0, string.Join(Environment.NewLine, decodeErrors));
        Assert.True(sawPartialCustom, "Actual CustomManual observation must retain known selectors and unknown settings.");
        using var requestedFrame = FrameJson("""{"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001","value":{"value":5,"lifetime":1,"customPerformance":null,"gpuClockTuning":null}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(requestedFrame, CancellationToken.None));
        Assert.True(sawConfirmedPreset, "Actual status retains confirmed hardware separately from the result-only reply without renewing write authority.");
        Assert.Contains(ControlOperationStatus.Stored, seen);
        Assert.Contains(ControlOperationStatus.Applied, seen);
        Assert.Contains(ControlOperationStatus.Blocked, seen);
    }
    private static MemoryStream FrameJson(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed, bytes.Length);
        bytes.CopyTo(framed, 4);
        return new MemoryStream(framed, writable: false);
    }
}
