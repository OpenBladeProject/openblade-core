using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class CoolingPadControlTests
{
    [Fact]
    public void DefaultsAreInertAndWellFormed()
    {
        Assert.Equal(CoolingPadFanMode.Keep, CoolingPadSettings.Default.FanMode);
        Assert.Equal(CoolingPadLightingMode.Keep, CoolingPadSettings.Default.LightingMode);
        Assert.True(CoolingPadSettings.IsWellFormed(CoolingPadSettings.Default));
        Assert.False(new CoolingPadStatusSnapshot().Active);
        Assert.Null(new CoolingPadStatusSnapshot().MeasuredRpm);
    }

    [Fact]
    public void DiscoveryDistinguishesUnknownFromAbsentAndRejectsInventedMeasurements()
    {
        Assert.Equal(CoolingPadConnectionState.Unavailable, new CoolingPadStatusSnapshot().Connection);
        Assert.True(CoolingPadStatusSnapshot.IsWellFormed(new()));
        Assert.True(CoolingPadStatusSnapshot.IsWellFormed(new() { Connected = true, Connection = CoolingPadConnectionState.ObservationFailed }));
        Assert.False(CoolingPadStatusSnapshot.IsWellFormed(new() { Connected = true, Connection = CoolingPadConnectionState.Absent }));
        Assert.False(CoolingPadStatusSnapshot.IsWellFormed(new() { Connection = CoolingPadConnectionState.PresentExact }));
        Assert.False(CoolingPadStatusSnapshot.IsWellFormed(new() { MeasuredRpm = 1500 }));
        Assert.False(CoolingPadStatusSnapshot.IsWellFormed(new() { ConfirmedTargetRpm = 1501 }));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(499)]
    [InlineData(525)]
    [InlineData(3201)]
    public void RejectsUnrepresentableTargets(int rpm) =>
        Assert.False(CoolingPadSettings.IsWellFormed(new() { FixedRpm = rpm }));

    [Fact]
    public void RejectsDescendingOrUnboundedCurves()
    {
        Assert.False(CoolingPadSettings.IsWellFormed(new() { CpuCurve = [] }));
        Assert.False(CoolingPadSettings.IsWellFormed(new() { CpuCurve = [new(50, 1500), new(60, 1000)] }));
        Assert.False(CoolingPadSettings.IsWellFormed(new() { CpuCurve = [new(50, 500), new(50, 1000)] }));
        Assert.False(CoolingPadSettings.IsWellFormed(new() { CpuCurve = [new(50, 500), new(120, 1000)] }));
    }

    [Fact]
    public async Task TypedRequestsRoundTripAndRejectInvalidLease()
    {
        ServiceRequest[] requests =
        [
            new SetCoolingPadSettingsRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), new()),
            new ReadCoolingPadStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            new ReleaseCoolingPadControlRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            new SetCoolingPadDemandLeaseRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), 1500, 10),
            new RemoveCoolingPadDemandLeaseRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid()),
        ];
        foreach (ServiceRequest request in requests)
        {
            using var stream = new MemoryStream();
            await IpcMessageCodec.WriteRequestAsync(stream, request, default);
            stream.Position = 0;
            ServiceRequest read = await IpcMessageCodec.ReadRequestAsync(stream, default);
            Assert.Equal(request.GetType(), read.GetType());
            Assert.Equal(request.RequestId, read.RequestId);
        }
        using var invalidStream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcMessageCodec.WriteRequestAsync(invalidStream,
            new SetCoolingPadDemandLeaseRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), 1551, 31), default));
    }
}
