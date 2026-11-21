using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData("RZ09-0528", WindowsDynamicLightingOwnership.Owned)]
    [InlineData("RZ09-0529", WindowsDynamicLightingOwnership.Owned)]
    [InlineData("RZ09-0509", WindowsDynamicLightingOwnership.Released)]
    [InlineData("RZ09-0510", WindowsDynamicLightingOwnership.Owned)]
    [InlineData("RZ09-0530", WindowsDynamicLightingOwnership.Released)]
    public async Task WindowsDynamicLightingReport_RoundTripsForEachReviewedModel(
        string modelNumber,
        WindowsDynamicLightingOwnership ownership)
    {
        var expected = new ReportWindowsDynamicLightingOwnershipRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            modelNumber,
            ownership);
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(
            expected,
            Assert.IsType<ReportWindowsDynamicLightingOwnershipRequest>(
                await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None)));
    }

    [Theory]
    // Unknown is never reported: a failed observation sends nothing.
    [InlineData("RZ09-0529", WindowsDynamicLightingOwnership.Unknown)]
    [InlineData("RZ09-0529", (WindowsDynamicLightingOwnership)7)]
    // SO690 has no LampArray; RZ09-0581's descriptor is unreviewed.
    [InlineData("RZ09-0483", WindowsDynamicLightingOwnership.Owned)]
    [InlineData("RZ09-0581", WindowsDynamicLightingOwnership.Owned)]
    // Identity is exact: no aliases or case folding on the wire.
    [InlineData("rz09-0529", WindowsDynamicLightingOwnership.Owned)]
    [InlineData("", WindowsDynamicLightingOwnership.Owned)]
    public async Task WindowsDynamicLightingReport_RejectsUnreviewedOrInexactReports(
        string modelNumber,
        WindowsDynamicLightingOwnership ownership)
    {
        var request = new ReportWindowsDynamicLightingOwnershipRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            modelNumber,
            ownership);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(WindowsDynamicLightingOwnership.Unknown)]
    [InlineData(WindowsDynamicLightingOwnership.Released)]
    [InlineData(WindowsDynamicLightingOwnership.Owned)]
    public async Task WindowsDynamicLightingStatus_RoundTripsOwnership(
        WindowsDynamicLightingOwnership ownership)
    {
        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(
            CreateStatus() with { WindowsDynamicLightingOwnership = ownership });

        Assert.Equal(ownership, decoded.WindowsDynamicLightingOwnership);
    }

    [Fact]
    public async Task WindowsDynamicLightingStatus_RejectsUndefinedOwnership()
    {
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(
                    OpenBladeIpc.ProtocolVersion,
                    CreateStatus() with
                    {
                        WindowsDynamicLightingOwnership = (WindowsDynamicLightingOwnership)7,
                    }),
                CancellationToken.None));
    }
}
