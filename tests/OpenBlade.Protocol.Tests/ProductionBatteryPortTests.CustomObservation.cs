using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionBatteryPortTests
{
    [Fact]
    public async Task CustomObservationParity_ActualGetterPreservesManualUnknownAndNextGetterHealth()
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Parity", "rz09-0581-custom-observation.json")),
            new JsonDocumentOptions { AllowDuplicateProperties = false });
        CheckProperties(fixture.RootElement, "schemaVersion", "model", "cases");
        Assert.Equal(1, fixture.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RZ09-0581", fixture.RootElement.GetProperty("model").GetString());
        foreach (JsonElement row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            CheckProperties(row, "id", "fan", "cpuPayload", "gpuPayload", "expected", "queryCount", "portHealthy");
            byte fan = row.GetProperty("fan").GetByte();
            byte[] Payload(string key) => row.GetProperty(key).EnumerateArray().Select(v => v.GetByte()).ToArray();
            var session = new ScriptedSession(Success([1, 1, 4, fan]), Success([1, 2, 4, fan]),
                Success(Payload("cpuPayload")), Success(Payload("gpuPayload")), Success([1, 1, 0, 0]), Success([1, 2, 0, 0]));
            await using var port = CreatePort(session, DeviceCommandCatalog.Empty, supportsDirectManualFanTransitions: true);
            JsonElement expected = row.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                await Assert.ThrowsAsync<RazerProtocolException>(() => port.QueryPerformanceFanStateAsync(default));
                await Assert.ThrowsAsync<IOException>(() => port.QueryPerformanceFanStateAsync(default));
                Assert.False(row.GetProperty("portHealthy").GetBoolean());
            }
            else
            {
                CheckProperties(expected, "performance", "fans", "hyperBoostActive", "customPerformance");
                ProductionPerformanceFanState state = await port.QueryPerformanceFanStateAsync(default);
                Assert.Equal((PerformancePreset)expected.GetProperty("performance").GetInt32(), state.Performance);
                Assert.Equal((FanMode)expected.GetProperty("fans").GetInt32(), state.Fans);
                Assert.Equal(expected.GetProperty("hyperBoostActive").GetBoolean(), state.HyperBoostActive);
                JsonElement custom = expected.GetProperty("customPerformance");
                if (custom.ValueKind == JsonValueKind.Null) { Assert.Null(state.CustomPerformance); }
                else
                {
                    CheckProperties(custom, "cpu", "gpu", "maximumFans");
                    Assert.NotNull(state.CustomPerformance);
                    Assert.Equal((CustomPerformanceLevel)custom.GetProperty("cpu").GetInt32(), state.CustomPerformance.Cpu);
                    Assert.Equal((CustomPerformanceLevel)custom.GetProperty("gpu").GetInt32(), state.CustomPerformance.Gpu);
                    Assert.False(state.CustomPerformance.MaximumFans);
                }
                Assert.Equal(4, session.Commands.Count);
                Assert.Equal(PerformancePreset.Balanced, (await port.QueryPerformanceFanStateAsync(default)).Performance);
                Assert.True(row.GetProperty("portHealthy").GetBoolean());
            }
            int queries = row.GetProperty("queryCount").GetInt32();
            Assert.Equal(queries + (expected.ValueKind == JsonValueKind.Null ? 0 : 2), session.Commands.Count);
            Assert.Equal(new byte[] { 0x82, 0x82, 0x87, 0x87 }.Take(queries), session.Commands.Take(queries).Select(c => c.CommandId));
            Assert.Equal("010100", Convert.ToHexString(session.Commands[2].Arguments.Span));
            if (queries == 4) { Assert.Equal("010200", Convert.ToHexString(session.Commands[3].Arguments.Span)); }
            Assert.Empty(port.Capabilities.PerformancePresets);
        }
    }

    private static void CheckProperties(JsonElement value, params string[] names) =>
        Assert.Equal(names.Order(StringComparer.Ordinal), value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(HidSessionFailureKind.QueuedCanceled, false)]
    [InlineData(HidSessionFailureKind.Canceled, true)]
    public async Task CustomGpuCancellationAfterCpuSuccess_DistinguishesQueuedFromClaimedPortPoison(
        HidSessionFailureKind failure, bool poisoned)
    {
        using var cancel = new CancellationTokenSource();
        var session = new ScriptedSession(Success([1, 1, 4, 0]), Success([1, 2, 4, 0]), Success([1, 1, 1]),
            new(default, failure), Success([1, 1, 0, 0]), Success([1, 2, 0, 0]))
        {
            BeforeResult = (count, _) => { if (count == 4) { cancel.Cancel(); } },
        };
        await using var port = CreatePort(session, DeviceCommandCatalog.Empty, supportsDirectManualFanTransitions: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.QueryPerformanceFanStateAsync(cancel.Token));
        Assert.Equal(4, session.Commands.Count); // GPU invocation, never proof of native submission.
        Assert.Equal(RazerCommandSessionDisposition.Healthy, session.Disposition);
        if (poisoned)
        {
            await Assert.ThrowsAsync<IOException>(() => port.QueryPerformanceFanStateAsync(default));
            Assert.Equal(4, session.Commands.Count);
        }
        else
        {
            Assert.Equal(PerformancePreset.Balanced, (await port.QueryPerformanceFanStateAsync(default)).Performance);
            Assert.Equal(6, session.Commands.Count);
        }
    }
}
