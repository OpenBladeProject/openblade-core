using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData("RZ09-0528", WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData("RZ09-0529", WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData("RZ09-0509", WindowsDynamicLightingAdvisory.Off)]
    [InlineData("RZ09-0510", WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData("RZ09-0530", WindowsDynamicLightingAdvisory.Off)]
    public async Task WindowsDynamicLightingReport_RoundTripsForEachReviewedModel(
        string modelNumber,
        WindowsDynamicLightingAdvisory advisory)
    {
        var expected = new ReportWindowsDynamicLightingAdvisoryRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            modelNumber,
            advisory);
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(
            expected,
            Assert.IsType<ReportWindowsDynamicLightingAdvisoryRequest>(
                await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None)));
    }

    [Theory]
    // Unknown is never reported: a failed observation sends nothing.
    [InlineData("RZ09-0529", WindowsDynamicLightingAdvisory.Unknown)]
    [InlineData("RZ09-0529", (WindowsDynamicLightingAdvisory)7)]
    // Neither model is in the reviewed LampArray list.
    [InlineData("RZ09-0483", WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData("RZ09-0581", WindowsDynamicLightingAdvisory.MayControl)]
    // Identity is exact: no aliases or case folding on the wire.
    [InlineData("rz09-0529", WindowsDynamicLightingAdvisory.MayControl)]
    [InlineData("", WindowsDynamicLightingAdvisory.MayControl)]
    public async Task WindowsDynamicLightingReport_RejectsUnreviewedOrInexactReports(
        string modelNumber,
        WindowsDynamicLightingAdvisory advisory)
    {
        var request = new ReportWindowsDynamicLightingAdvisoryRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            modelNumber,
            advisory);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(WindowsDynamicLightingAdvisory.Unknown)]
    [InlineData(WindowsDynamicLightingAdvisory.Off)]
    [InlineData(WindowsDynamicLightingAdvisory.MayControl)]
    public async Task WindowsDynamicLightingStatus_RoundTripsAdvisory(
        WindowsDynamicLightingAdvisory advisory)
    {
        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(
            CreateStatus() with { WindowsDynamicLightingAdvisory = advisory });

        Assert.Equal(advisory, decoded.WindowsDynamicLightingAdvisory);
    }

    [Fact]
    public async Task WindowsDynamicLightingStatus_RejectsUndefinedAdvisory()
    {
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(
                    OpenBladeIpc.ProtocolVersion,
                    CreateStatus() with
                    {
                        WindowsDynamicLightingAdvisory = (WindowsDynamicLightingAdvisory)7,
                    }),
                CancellationToken.None));
    }
}
