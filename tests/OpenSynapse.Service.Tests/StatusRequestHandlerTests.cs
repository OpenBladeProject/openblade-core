using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class StatusRequestHandlerTests
{
    [Fact]
    public void Handle_RejectsUnknownProtocolVersion()
    {
        StatusRequestHandler handler = new(new FakeConflictDetector([]));
        ServiceRequest request = new(99, Guid.NewGuid(), "status");

        ServiceResponse response = handler.Handle(request);

        Assert.False(response.Success);
        Assert.Null(response.Status);
    }

    [Fact]
    public void Handle_StatusReportsConflictAndUnverifiedDevice()
    {
        StatusRequestHandler handler = new(new FakeConflictDetector(["RazerAppEngine"]));
        ServiceRequest request = new(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), "status");

        ServiceResponse response = handler.Handle(request);

        Assert.True(response.Success);
        Assert.NotNull(response.Status);
        Assert.False(response.Status.DeviceVerified);
        Assert.True(response.Status.WritesBlocked);
        Assert.Equal(["RazerAppEngine"], response.Status.Conflicts);
    }

    [Fact]
    public void Handle_StatusReportsDetectionFailureAndPreservedConflicts()
    {
        StatusRequestHandler handler = new(new FakeConflictDetector(
            ["RazerAppEngine"],
            "Windows service enumeration failed."));
        ServiceRequest request = new(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid(), "status");

        ServiceResponse response = handler.Handle(request);

        Assert.True(response.Success);
        Assert.NotNull(response.Status);
        Assert.True(response.Status.WritesBlocked);
        Assert.Equal(["RazerAppEngine"], response.Status.Conflicts);
        Assert.Contains("Windows service enumeration failed.", response.Status.Message);
    }

    private sealed class FakeConflictDetector(
        IReadOnlyList<string> conflicts,
        string? detectionFailure = null) : IConflictDetector
    {
        public ConflictCheck Check() => new(conflicts, detectionFailure);
    }
}
