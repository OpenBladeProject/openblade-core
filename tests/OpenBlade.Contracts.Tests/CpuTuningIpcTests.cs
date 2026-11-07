using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class CpuTuningIpcTests
{
    [Fact]
    public async Task OptionalTargetRecoveryRoundTripsWithoutPrivateJournalData()
    {
        CpuTuningStatus status = ValidStatus() with
        {
            RestorePending = true,
            FirmwareRecovery = new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer),
            LiveRecovery = new(CpuTuningRecoveryState.None, CpuTuningFeatures.None),
        };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream,
            ResultResponse(new(ControlOperationStatus.Stored, "Saved") { CpuTuning = status }), CancellationToken.None);
        string json = Encoding.UTF8.GetString(stream.ToArray(), 4, checked((int)stream.Length - 4));
        Assert.DoesNotContain("baselineCurrent", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baselineLast", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steps", json, StringComparison.OrdinalIgnoreCase);
        stream.Position = 0;
        var actual = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(status.FirmwareRecovery, actual.Result!.CpuTuning!.FirmwareRecovery);
        Assert.Equal(status.LiveRecovery, actual.Result.CpuTuning.LiveRecovery);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        var cpu = root["result"]!["cpuTuning"]!.AsObject();
        cpu.Remove("firmwareRecovery");
        cpu.Remove("liveRecovery");
        using MemoryStream legacy = Frame(root.ToJsonString());
        var old = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(legacy, CancellationToken.None));
        Assert.True(old.Result!.CpuTuning!.RestorePending);
        Assert.Null(old.Result.CpuTuning.FirmwareRecovery);
        Assert.Null(old.Result.CpuTuning.LiveRecovery);
    }

    [Theory]
    [InlineData(99, 0, true)]
    [InlineData(2, 4, true)]
    [InlineData(2, -1, true)]
    [InlineData(1, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(2, 2, false)]
    public async Task MalformedOrContradictoryRecoveryCannotCrossIpc(int state, int features, bool pending)
    {
        using MemoryStream valid = new();
        await IpcMessageCodec.WriteServerFrameAsync(valid,
            ResultResponse(new(ControlOperationStatus.Stored, "Saved") { CpuTuning = ValidStatus() }), CancellationToken.None);
        JsonObject root = JsonNode.Parse(Encoding.UTF8.GetString(valid.ToArray(), 4, checked((int)valid.Length - 4)))!.AsObject();
        var cpu = root["result"]!["cpuTuning"]!.AsObject();
        cpu["restorePending"] = pending;
        cpu["firmwareRecovery"] = new JsonObject { ["state"] = state, ["features"] = features };
        using MemoryStream invalid = Frame(root.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(invalid, CancellationToken.None));
    }

    [Fact]
    public async Task RequestsRoundTripExplicitTargetIntentAndOptionalDependencyChoice()
    {
        ServiceRequest[] requests =
        [
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                CpuTuningTarget.FirmwareNextBoot, new(65, new(false, -17))),
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                CpuTuningTarget.LiveNow, new(null, new(true, -30))),
            new ObserveCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false),
            new ObserveCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
            new RestoreCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), CpuTuningTarget.LiveNow),
        ];
        foreach (ServiceRequest request in requests)
        {
            using MemoryStream stream = new();
            await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
            stream.Position = 0;
            Assert.Equal(request, await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
        }
    }

    [Fact]
    public async Task InvalidTypedRequestsAreRejectedBeforeWriting()
    {
        ServiceRequest[] requests =
        [
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), (CpuTuningTarget)99, new(65, null)),
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), CpuTuningTarget.LiveNow, null!),
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), CpuTuningTarget.LiveNow, new(null, null)),
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), CpuTuningTarget.LiveNow, new(96, null)),
            new SetCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), CpuTuningTarget.LiveNow, new(null, new(false, 0))),
            new RestoreCpuTuningRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), (CpuTuningTarget)99),
        ];
        foreach (ServiceRequest request in requests)
        {
            using MemoryStream stream = new();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
            Assert.Equal(0, stream.Length);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"powerLimitWatts\":96,\"curveOptimizer\":null}")]
    [InlineData("{\"powerLimitWatts\":null,\"curveOptimizer\":{\"enabled\":false,\"value\":0}}")]
    public async Task MalformedSettingsRejectedOnRead(string settings)
    {
        using MemoryStream stream = Frame($$"""
            {"operation":"setCpuTuning","version":{{OpenBladeIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","target":0,"settings":{{settings}}}
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("setCpuTuning", ",\"settings\":{\"powerLimitWatts\":65,\"curveOptimizer\":null}")]
    [InlineData("restoreCpuTuning", "")]
    public async Task UnknownTargetRejectedOnRead(string operation, string settings)
    {
        using MemoryStream stream = Frame($$"""
            {"operation":"{{operation}}","version":{{OpenBladeIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","target":99{{settings}}}
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(58)]
    [InlineData(59)]
    public async Task ClosedVersionRejectsFormerPeersRatherThanTreatingCpuOperationsAsCompatible(int version)
    {
        using MemoryStream stream = Frame($$"""
            {"operation":"observeCpuTuning","version":{{version}},"requestId":"{{Guid.NewGuid()}}","useInstalledBackend":false}
            """);
        await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ResultStateRoundTripsMixedLiveBaselineSeparatelyFromPendingSavedIntent()
    {
        ServiceResponse response = ResultResponse(new(ControlOperationStatus.Stored, "Saved") { CpuTuning = ValidStatus() });
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        var actual = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(CpuTuningPendingBoot.Requested, actual.Result!.CpuTuning!.PendingBoot);
        Assert.Equal(65000U, actual.Result.CpuTuning.SavedConfiguration!.PowerLimitMilliwatts);
        Assert.Null(actual.Result.CpuTuning.LiveState!.UniformCurveOptimizerValue);
        Assert.Equal(95001U, actual.Result.CpuTuning.LiveState.PowerLimits!.Message33Milliwatts);
    }

    [Fact]
    public async Task MissingOptionalCpuResultStatusRemainsCompatibleWithinCurrentVersion()
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream,
            ResultResponse(new(ControlOperationStatus.Blocked, "Unavailable")), CancellationToken.None);
        Assert.DoesNotContain("cpuTuning", Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        var actual = Assert.IsType<ServiceResponse>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Null(actual.Result!.CpuTuning);
    }

    [Fact]
    public async Task UnknownNestedEnumAndIncompleteCoreBaselineRejectedOnRead()
    {
        var response = ResultResponse(new(ControlOperationStatus.Blocked, "Unavailable") { CpuTuning = ValidStatus() });
        using MemoryStream valid = new();
        await IpcMessageCodec.WriteServerFrameAsync(valid, response, CancellationToken.None);
        JsonObject root = JsonNode.Parse(Encoding.UTF8.GetString(valid.ToArray(), 4, checked((int)valid.Length - 4)))!.AsObject();
        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            cpu => cpu["pendingBoot"] = 99,
            cpu => cpu["live"]!["availability"] = 99,
            cpu => cpu["liveState"]!["coreValues"] = null,
            cpu => { cpu["liveState"]!["coreValues"] = new JsonArray(); cpu["liveState"]!["powerLimits"] = null; },
            cpu => { cpu["liveState"]!["coreValues"] = new JsonArray(); cpu["liveState"]!["powerLimits"]!["effectiveWatts"] = -1; },
        })
        {
            JsonObject copy = root.DeepClone().AsObject();
            mutate(copy["result"]!["cpuTuning"]!.AsObject());
            using MemoryStream invalid = Frame(copy.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                IpcMessageCodec.ReadServerFrameAsync(invalid, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Protocol51ProjectionOmitsCpuResultState()
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            ResultResponse(new(ControlOperationStatus.Blocked, "Unavailable") { CpuTuning = ValidStatus() }),
            CancellationToken.None);
        Assert.DoesNotContain("cpuTuning", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task SnapshotOptionalCpuStateRoundTripsAndRejectsUnknownState()
    {
        ServiceStatusSnapshot snapshot = Snapshot() with { CpuTuning = ValidStatus() };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, snapshot), CancellationToken.None);
        stream.Position = 0;
        var actual = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(CpuTuningPendingBoot.Requested, actual.Status.CpuTuning!.PendingBoot);

        using MemoryStream invalid = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteServerFrameAsync(invalid,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion,
                snapshot with { CpuTuning = ValidStatus() with { PendingBoot = (CpuTuningPendingBoot)99 } }),
            CancellationToken.None));

        using MemoryStream omitted = new();
        await IpcMessageCodec.WriteServerFrameAsync(omitted,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, Snapshot()), CancellationToken.None);
        Assert.DoesNotContain("cpuTuning", Encoding.UTF8.GetString(omitted.ToArray()));
        omitted.Position = 0;
        Assert.Null(Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(omitted, CancellationToken.None)).Status.CpuTuning);

        using MemoryStream projected = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(projected,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, snapshot), CancellationToken.None);
        Assert.DoesNotContain("cpuTuning", Encoding.UTF8.GetString(projected.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerOnlyObservationRoundTripsWithoutInventingCurveOrEffectiveWatts(bool statusEvent)
    {
        CpuTuningStatus status = ValidStatus() with { LiveState = new(new(null, 85123, 80765), []) };
        ServiceServerFrame frame = statusEvent
            ? new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, Snapshot() with { CpuTuning = status })
            : ResultResponse(new(ControlOperationStatus.Stored, "Observed") { CpuTuning = status });
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None);
        string json = Encoding.UTF8.GetString(stream.ToArray(), 4, checked((int)stream.Length - 4));
        stream.Position = 0;
        ServiceServerFrame actual = await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None);
        CpuLiveState live = (actual is ServiceStatusEvent update ? update.Status.CpuTuning
            : Assert.IsType<ServiceResponse>(actual).Result!.CpuTuning)!.LiveState!;
        Assert.True(live.IsWellFormed);
        Assert.Empty(live.CoreValues);
        Assert.Null(live.UniformCurveOptimizerValue);
        Assert.Equal(new(null, 85123, 80765), live.PowerLimits);

        JsonObject old = JsonNode.Parse(json)!.AsObject();
        old["version"] = 59;
        using MemoryStream formerVersion = Frame(old.ToJsonString());
        await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(formerVersion, CancellationToken.None));

        using MemoryStream projected = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(projected, frame, CancellationToken.None);
        Assert.DoesNotContain("cpuTuning", Encoding.UTF8.GetString(projected.ToArray()));
    }

    private static ServiceStatusSnapshot Snapshot()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
            new DesiredBatteryState(profiles.BatteryPolicy, true, false));
        return new(1, false, true, [], null, [], "Unavailable", DeviceCapabilities.Unavailable(),
            null, profiles, desired, PowerSource.PluggedIn, null, null, null, null, false,
            ServiceVersion: "0.1.0-test");
    }

    private static CpuTuningStatus ValidStatus() => new(
        new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -10, [])),
        new(new(65, 65123, 95001), [new(0, -10), new(1, 5)]),
        new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Available),
        new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.MissingDependency),
        CpuTuningPendingBoot.Requested, false);

    private static ServiceResponse ResultResponse(ControlOperationResult result) => new(
        OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true, null, null, result);

    private static MemoryStream Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] bytes = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, body.Length);
        body.CopyTo(bytes, 4);
        return new(bytes);
    }
}
