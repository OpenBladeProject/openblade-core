using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class NativeThermalEdgeFramesTests
{
    [NativeThermalEdgeFact]
    public async Task ActualNativeEdgeFramesPreserveSavedCustomAndCurrentPolicyThroughStrictCodec()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_EDGE_FRAMES")
            ?? throw new InvalidOperationException("Explicit native edge frame corpus required.");
        Assert.True(Path.IsPathFullyQualified(path));
        using JsonDocument corpus = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        int events = 0;
        int expired = 0;
        HashSet<string> savedVariantOperations = [];
        HashSet<bool> maximumFlags = [];
        foreach (JsonElement entry in corpus.RootElement.EnumerateArray())
        {
            string raw = entry.GetProperty("raw").GetString()!;
            byte[] json = Encoding.UTF8.GetBytes(raw);
            byte[] framed = new byte[json.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, json.Length);
            json.CopyTo(framed, 4);
            using var stream = new MemoryStream(framed, writable: false);
            ServiceServerFrame decoded = await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None);
            Assert.Equal(OpenBladeIpc.ProtocolVersion, decoded.Version);
            Assert.Equal(stream.Length, stream.Position);
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement frame = document.RootElement;
            if (!frame.TryGetProperty("status", out JsonElement status) || status.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (decoded is ServiceStatusEvent)
            {
                events++;
            }
            if (entry.GetProperty("method").GetString()!.Contains("saved_custom", StringComparison.Ordinal)
                && status.TryGetProperty("performanceOverride", out JsonElement saved)
                && saved.ValueKind == JsonValueKind.Object)
            {
                Assert.Equal(5, saved.GetProperty("value").GetInt32());
                JsonElement custom = saved.GetProperty("customPerformance");
                Assert.Equal(1, custom.GetProperty("cpu").GetInt32());
                Assert.Equal(2, custom.GetProperty("gpu").GetInt32());
                bool maximum = custom.GetProperty("maximumFans").GetBoolean();
                maximumFlags.Add(maximum);
                if (maximum)
                {
                    JsonElement gpu = saved.GetProperty("gpuClockTuning");
                    Assert.True(gpu.GetProperty("enabled").GetBoolean());
                    Assert.Equal(125, gpu.GetProperty("offsets").GetProperty("coreMegahertz").GetInt32());
                    Assert.Equal(500, gpu.GetProperty("offsets").GetProperty("memoryMegahertz").GetInt32());
                }
                savedVariantOperations.Add($"{maximum}:{entry.GetProperty("operation").GetString()!}");
                if (status.GetProperty("confirmedState").ValueKind == JsonValueKind.Object)
                {
                    Assert.Equal(5, status.GetProperty("confirmedState").GetProperty("performance").GetInt32());
                    Assert.Equal(JsonValueKind.Null, status.GetProperty("confirmedState").GetProperty("customPerformance").ValueKind);
                }
            }
            if (entry.GetProperty("method").GetString()!.Contains("cannot_resurrect", StringComparison.Ordinal)
                && decoded is ServiceStatusEvent
                && entry.GetProperty("operation").GetString() == "afterStored")
            {
                Assert.Equal(1, status.GetProperty("activePowerSource").GetInt32());
                Assert.Equal(JsonValueKind.Null, status.GetProperty("performanceOverride").ValueKind);
                expired++;
            }
        }
        Assert.True(events >= 3);
        Assert.True(expired >= 1);
        Assert.Equal(new HashSet<bool> { false, true }, maximumFlags);
        foreach (string operation in new[] { "getStatus", "observeState", "subscribeStatus", "statusEvent" })
        {
            Assert.Contains($"False:{operation}", savedVariantOperations);
            Assert.Contains($"True:{operation}", savedVariantOperations);
        }
    }
}

internal sealed class NativeThermalEdgeFactAttribute : FactAttribute
{
    public NativeThermalEdgeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_EDGE_FIXTURES") != "1")
        {
            Skip = "Requires explicit OPENBLADE_NATIVE_EDGE_FIXTURES=1 and private fixture inputs.";
        }
    }
}
