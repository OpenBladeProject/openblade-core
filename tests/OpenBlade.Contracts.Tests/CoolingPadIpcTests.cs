using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData(CoolingPadRestorationStage.Begin)]
    [InlineData(CoolingPadRestorationStage.Medium)]
    [InlineData(CoolingPadRestorationStage.Quiet)]
    [InlineData(CoolingPadRestorationStage.Final)]
    public async Task RestorationStagesRoundTripWithoutRawCommands(CoolingPadRestorationStage stage)
    {
        var request = new RestoreCoolingPadQuietRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
            stage == CoolingPadRestorationStage.Begin ? Guid.Empty : Guid.NewGuid(), stage, true);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, request, default);
        stream.Position = 0;
        Assert.Equal(request, Assert.IsType<RestoreCoolingPadQuietRequest>(await IpcMessageCodec.ReadRequestAsync(stream, default)));
    }

    [Theory]
    [InlineData(CoolingPadControlMode.Stop)]
    [InlineData(CoolingPadControlMode.FixedDemand)]
    [InlineData(CoolingPadControlMode.HostCurve)]
    public async Task CoolingPadSettingsRoundTripWithoutBecomingFirmwareReadback(CoolingPadControlMode mode)
    {
        var curve = new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]);
        CoolingPadControlSettings settings = mode switch
        {
            CoolingPadControlMode.FixedDemand => new(mode, new(1500)),
            CoolingPadControlMode.HostCurve => new(mode, cpuCurve: curve, gpuCurve: curve),
            _ => CoolingPadControlSettings.Stopped,
        };
        var expected = new SetCoolingPadControlRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), settings);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, expected, default);
        stream.Position = 0;
        Assert.Equal(expected, Assert.IsType<SetCoolingPadControlRequest>(await IpcMessageCodec.ReadRequestAsync(stream, default)));
    }

    [Fact]
    public async Task CoolingPadResumeHasNoSavedAuthorityOrCommandArguments()
    {
        var expected = new ResumeCoolingPadControlRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, expected, default);
        stream.Position = 0;
        Assert.Equal(expected, Assert.IsType<ResumeCoolingPadControlRequest>(await IpcMessageCodec.ReadRequestAsync(stream, default)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"mode\":99}")]
    [InlineData("{\"mode\":1}")]
    [InlineData("{\"mode\":1,\"fixedDemand\":{\"rpm\":0}}")]
    [InlineData("{\"mode\":1,\"fixedDemand\":{\"rpm\":1510}}")]
    [InlineData("{\"mode\":0,\"fixedDemand\":{\"rpm\":1500}}")]
    [InlineData("{\"mode\":2,\"cpuCurve\":{\"points\":[]},\"gpuCurve\":null}")]
    [InlineData("{\"mode\":0,\"rawCommand\":\"000600\"}")]
    public async Task CoolingPadWireRejectsMalformedSettings(string settings)
    {
        string json = $$"""{"operation":"setCoolingPadControl","version":{{OpenBladeIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","settings":{{settings}}} """;
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadRequestAsync(FrameJson(json), default));
    }

    [Fact]
    public async Task CoolingPadStatusSeparatesRequestedPlannedAndAcknowledgedDemand()
    {
        var expected = new CoolingPadControlStatus(new(CoolingPadControlMode.FixedDemand, new(1600)),
            CoolingPadControlState.Paused, CoolingPadControlPauseReason.SubmissionUnknown, new(1500), new(1350))
        { ResumeRequired = true, SubmissionStateUnknown = true };
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(stream, new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion,
            CreateStatus() with { CoolingPadControl = expected }), default);
        stream.Position = 0;
        var actual = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(stream, default));
        Assert.Equal(expected, actual.Status.CoolingPadControl);
        Assert.NotNull(actual.Status.CoolingPadControl);
        Assert.False(actual.Status.CoolingPadControl.MeasuredRpmReadbackSupported);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, actual.Status.CoolingPadControl.Access);
    }

    [Fact]
    public async Task CoolingPadStatusIsOptionalAndAbsentFromProtocol51Projection()
    {
        using var current = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(current,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateStatus()), default);
        JsonNode node = JsonNode.Parse(current.ToArray().AsSpan(sizeof(int)))!;
        node["status"]!.AsObject().Remove("coolingPadControl");
        var omitted = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(FrameJson(node.ToJsonString()), default));
        Assert.Null(omitted.Status.CoolingPadControl);
        using var projected = new MemoryStream();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(projected,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateStatus() with
            {
                CoolingPadControl = new(CoolingPadControlSettings.Stopped, CoolingPadControlState.Unavailable,
                    CoolingPadControlPauseReason.AdmissionClosed),
            }), default);
        Assert.DoesNotContain("coolingPadControl", Encoding.UTF8.GetString(projected.ToArray().AsSpan(sizeof(int))));
        Assert.Equal(projected.Length - sizeof(int), BinaryPrimitives.ReadInt32LittleEndian(projected.ToArray()));
    }

    [Theory]
    [InlineData("state", 99)]
    [InlineData("pauseReason", 99)]
    [InlineData("access", 99)]
    [InlineData("connection", 99)]
    public async Task CoolingPadStatusRejectsUnknownEnums(string name, int value)
    {
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(stream, new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                CoolingPadControl = new(CoolingPadControlSettings.Stopped,
                CoolingPadControlState.Stopped, CoolingPadControlPauseReason.None)
            }), default);
        JsonNode node = JsonNode.Parse(stream.ToArray().AsSpan(sizeof(int)))!;
        node["status"]!["coolingPadControl"]![name] = value;
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.ReadServerFrameAsync(FrameJson(node.ToJsonString()), default));
    }
}
