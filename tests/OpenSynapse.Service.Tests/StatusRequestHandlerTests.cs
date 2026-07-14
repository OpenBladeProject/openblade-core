using OpenSynapse.Contracts;
using OpenSynapse.Protocol;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class StatusRequestHandlerTests
{
    [Fact]
    public void Handle_RejectsUnknownProtocolVersion()
    {
        StatusRequestHandler handler = new(new FakeHardwareAccessGuard([]));
        ServiceRequest request = new GetStatusRequest(99, Guid.NewGuid());

        ServiceResponse response = handler.Handle(request);

        Assert.False(response.Success);
        Assert.Null(response.Status);
    }

    [Fact]
    public void Handle_StatusReportsConflictAndUnverifiedDevice()
    {
        StatusRequestHandler handler = new(new FakeHardwareAccessGuard(["RazerAppEngine"]));
        ServiceRequest request = new GetStatusRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid());

        ServiceResponse response = handler.Handle(request);

        Assert.True(response.Success);
        Assert.NotNull(response.Status);
        Assert.False(response.Status.DeviceVerified);
        Assert.True(response.Status.WritesBlocked);
        Assert.Equal(["RazerAppEngine"], response.Status.Conflicts);
        Assert.Empty(response.Status.Capabilities!.PerformancePresets);
    }

    [Fact]
    public void Handle_StatusIdentifiesEveryDetectedConflictInSortedOrder()
    {
        StatusRequestHandler handler = new(new FakeHardwareAccessGuard(
            ["Razer Synapse Service", "Razer Central Service"]));
        ServiceRequest request = new GetStatusRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid());

        ServiceResponse response = handler.Handle(request);

        Assert.True(response.Success);
        Assert.NotNull(response.Status);
        Assert.True(response.Status.WritesBlocked);
        Assert.Equal(
            ["Razer Central Service", "Razer Synapse Service"],
            response.Status.Conflicts);
        Assert.Equal(
            "Razer controller conflicts detected: Razer Central Service, Razer Synapse Service.",
            response.Status.Message);
    }

    [Fact]
    public void Handle_StatusReportsDetectionFailureAndPreservedConflicts()
    {
        StatusRequestHandler handler = new(new FakeHardwareAccessGuard(
            ["RazerAppEngine"],
            "Windows service enumeration failed."));
        ServiceRequest request = new GetStatusRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid());

        ServiceResponse response = handler.Handle(request);

        Assert.True(response.Success);
        Assert.NotNull(response.Status);
        Assert.True(response.Status.WritesBlocked);
        Assert.Equal(["RazerAppEngine"], response.Status.Conflicts);
        Assert.Equal(
            "Controller conflict detection failed: Windows service enumeration failed. " +
            "Detected conflicts: RazerAppEngine.",
            response.Status.Message);
    }

    private sealed class FakeHardwareAccessGuard(
        IReadOnlyList<string> conflicts,
        string? detectionFailure = null) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, detectionFailure);
    }
}
