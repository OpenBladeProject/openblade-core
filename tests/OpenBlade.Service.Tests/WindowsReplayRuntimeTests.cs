using System.Buffers.Binary;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ControlRequestHandlerTests
{
    [Theory]
    [MemberData(nameof(WindowsReplayData.Cases), "dispatch", MemberType = typeof(WindowsReplayData))]
    public async Task WindowsReplay_EnforcesSessionAndDispatchBoundaries(string id)
    {
        using JsonDocument fixture = WindowsReplayData.Open();
        JsonElement example = WindowsReplayData.Find(fixture, "dispatch", id);
        JsonElement input = example.GetProperty("input");
        JsonElement expected = example.GetProperty("expected");
        using var stream = new MemoryStream();
        byte[] payload = Encoding.UTF8.GetBytes(input.GetRawText());
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Position = 0;
        ServiceRequest request = await IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(stream, default);

        var runtime = new RecordingRuntime();
        var handler = new ControlRequestHandler(
            runtime, new ThrowingArchiveProvider(), NullServiceShutdownSignal.Instance,
            new FixedSessionAuthority(4));
        var client = new ServiceClientContext(
            new SecurityIdentifier("S-1-5-21-1-2-3-1001"),
            SessionId: example.GetProperty("session").GetString() == "active" ? 4 : 3,
            IsAdministrator: false,
            ServiceClientLogonKind.Interactive);
        using var cancellation = new CancellationTokenSource();
        if (example.TryGetProperty("cancelBeforeDispatch", out JsonElement cancel) && cancel.GetBoolean())
        {
            cancellation.Cancel();
        }

        if (expected.TryGetProperty("failure", out JsonElement failure))
        {
            Assert.Equal("canceled", failure.GetString());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handler.HandleAsync(request, client, cancellation.Token));
        }
        else
        {
            bool compatibility = request.Version == OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion;
            // The named-pipe boundary performs this normalization before calling the read-only dispatcher.
            ServiceResponse response = compatibility
                ? await handler.HandleProtocol51ReadOnlyAsync(
                    request with { Version = OpenBladeIpc.ProtocolVersion }, client, cancellation.Token)
                : await handler.HandleAsync(request, client, cancellation.Token);

            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(expected.GetProperty("success").GetBoolean(), response.Success);
            if (expected.TryGetProperty("controlAccess", out JsonElement access))
            {
                Assert.Equal(access.GetInt32(), (int)Assert.IsType<ServiceStatusSnapshot>(response.Status).ControlAccess);
            }

            if (expected.TryGetProperty("operationStatus", out JsonElement status))
            {
                Assert.Equal(status.GetInt32(), (int)Assert.IsType<ControlOperationResult>(response.Result).Status);
            }

            if (expected.TryGetProperty("sensors", out JsonElement sensors))
            {
                Assert.Equal(sensors.GetInt32(), (int)runtime.LastRequestedLiveReadings);
            }
        }

        Assert.Equal(expected.GetProperty("activations").GetInt32(), runtime.ActivationCalls);
        Assert.Equal(expected.GetProperty("calls").EnumerateArray().Select(call => call.GetString()), runtime.Calls);
    }
}

public sealed partial class ThermalTransitionExecutorTests
{
    [Theory]
    [MemberData(nameof(WindowsReplayData.Cases), "thermal", MemberType = typeof(WindowsReplayData))]
    public async Task WindowsReplay_ReportsTypedThermalOutcomeWithoutRetry(string id)
    {
        using JsonDocument fixture = WindowsReplayData.Open();
        JsonElement example = WindowsReplayData.Find(fixture, "thermal", id);
        JsonElement expected = example.GetProperty("expected");
        ScopedApplyOutcome input = example.GetProperty("input").GetString() switch
        {
            "indeterminate" => IndeterminateOutcome(),
            "confirmed" => ConfirmedOutcome(writeAttempted: true),
            _ => throw new InvalidDataException("Unknown thermal replay input."),
        };
        int attempts = 0;
        ThermalTransitionExecution execution = await ThermalTransitionExecutor.ExecuteScopedPerformanceAsync(
            new StubDevice(),
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(input);
            },
            (_, _) => throw new InvalidOperationException("Retry admission was unexpected."),
            CancellationToken.None);

        Assert.Equal(expected.GetProperty("attempts").GetInt32(), attempts);
        Assert.Equal(expected.GetProperty("retryConsumed").GetBoolean(), execution.Trace.RetryConsumed);
        Assert.Equal(expected.GetProperty("progress").GetString(), execution.Trace.SubmissionProgress.ToString());
        Assert.Equal(expected.GetProperty("certainty").GetString(), execution.Trace.Certainty?.ToString());
        Assert.Equal(expected.GetProperty("disposition").GetString(), execution.Trace.Disposition.ToString());
    }
}

public sealed class WindowsReplayPersistenceTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("OpenBlade.WindowsReplay.").FullName;

    [Theory]
    [MemberData(nameof(WindowsReplayData.Cases), "overrides", MemberType = typeof(WindowsReplayData))]
    public async Task WindowsReplay_OverrideMigrationOrQuarantineSurvivesRestart(string id)
    {
        using JsonDocument fixture = WindowsReplayData.Open();
        JsonElement example = WindowsReplayData.Find(fixture, "overrides", id);
        JsonElement expected = example.GetProperty("expected");
        string path = Path.Combine(directory, "manual-overrides.json");
        string input = example.GetProperty("input").GetRawText();
        await File.WriteAllTextAsync(path, input);

        PersistentOverrideLoadResult first = await new PersistentOverrideStore(path).LoadAsync(default);
        var restarted = new PersistentOverrideStore(path);
        PersistentOverrideLoadResult actual = await restarted.LoadAsync(default);
        Assert.Equal(first, actual);
        Assert.Equal(expected.GetProperty("disposition").GetString(), actual.Disposition.ToString());
        Assert.Equal(expected.GetProperty("quarantineReason").GetString(), actual.QuarantineReason.ToString());
        Assert.Equal(expected.GetProperty("performance").Deserialize<int?>(), (int?)actual.Overrides.Performance);
        JsonElement offsets = expected.GetProperty("gpuOffsets");
        if (offsets.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual.Overrides.GpuClockTuning);
        }
        else
        {
            Assert.True(actual.Overrides.GpuClockTuning!.Enabled);
            Assert.Null(actual.Overrides.CustomPerformance?.GpuClockOffsets);
            Assert.Equal(offsets.GetProperty("coreMegahertz").GetInt32(), actual.Overrides.GpuClockTuning.Offsets.CoreMegahertz);
            Assert.Equal(offsets.GetProperty("memoryMegahertz").GetInt32(), actual.Overrides.GpuClockTuning.Offsets.MemoryMegahertz);
        }

        if (actual.Disposition == ConfigurationLoadDisposition.Quarantined)
        {
            await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
                restarted.SaveAsync(PersistentOverrideState.Empty, default));
            Assert.Equal(input, await File.ReadAllTextAsync(path));
        }

        using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(expected.GetProperty("schemaVersion").GetInt32(),
            saved.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [MemberData(nameof(WindowsReplayData.Cases), "journals", MemberType = typeof(WindowsReplayData))]
    public async Task WindowsReplay_PendingLaptopControlFailsClosedAfterRestart(string id)
    {
        using JsonDocument fixture = WindowsReplayData.Open();
        JsonElement example = WindowsReplayData.Find(fixture, "journals", id);
        JsonElement expected = example.GetProperty("expected");
        string path = Path.Combine(directory, "laptop-control-pending.json");
        JsonElement input = example.GetProperty("input");
        if (input.ValueKind != JsonValueKind.Null)
        {
            await File.WriteAllTextAsync(path, input.GetRawText());
        }

        var restarted = new LaptopControlPendingStore(path);
        LaptopControlPendingLoad actual = restarted.Load();
        Assert.Equal(expected.GetProperty("reconciliation").GetBoolean(), actual.RequiresReconciliation);
        Assert.Equal(expected.GetProperty("readable").GetBoolean(), actual.Document is not null);
        var settled = new LaptopControlPendingDocument(1, "synthetic-host", "synthetic-device", []);
        bool saved = await restarted.TrySaveAsync(actual.Sha256, settled, default);
        Assert.Equal(expected.GetProperty("saveAllowed").GetBoolean(), saved);
        if (saved)
        {
            LaptopControlPendingLoad reloaded = new LaptopControlPendingStore(path).Load();
            Assert.False(reloaded.RequiresReconciliation);
            Assert.Empty(reloaded.Document!.Entries);
        }
        else
        {
            Assert.Equal(input.GetRawText(), await File.ReadAllTextAsync(path));
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

public static class WindowsReplayData
{
    internal static JsonDocument Open() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "WindowsReplay/v1/service-runtime.json")));

    internal static JsonElement Find(JsonDocument fixture, string group, string id) =>
        fixture.RootElement.GetProperty(group).EnumerateArray().Single(example =>
            example.GetProperty("id").GetString() == id);

    public static TheoryData<string> Cases(string group)
    {
        using JsonDocument fixture = Open();
        var cases = new TheoryData<string>();
        foreach (JsonElement example in fixture.RootElement.GetProperty(group).EnumerateArray())
        {
            cases.Add(example.GetProperty("id").GetString()!);
        }
        return cases;
    }
}
