using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadProtocolPortFactoryTests
{
    [Fact]
    public async Task OrdinaryFactory_AdmissionClosedWithoutNativeConstruction()
    {
        var opened = await new CoolingPadProtocolPortFactory().OpenAsync(CancellationToken.None);
        Assert.Null(opened.Port);
        Assert.Equal(CoolingPadPortFault.AdmissionClosed, opened.Fault);
    }

    [Theory]
    [InlineData(CoolingPadSessionFault.Disconnected, CoolingPadPortFault.Disconnected)]
    [InlineData(CoolingPadSessionFault.ControllerConflict, CoolingPadPortFault.ControllerConflict)]
    [InlineData(CoolingPadSessionFault.SubmissionUnknown, CoolingPadPortFault.SubmissionUnknown)]
    public async Task OpenFault_PreservesSubmissionAndConflictEvidence(CoolingPadSessionFault fault,
        CoolingPadPortFault expected)
    {
        var factory = new CoolingPadProtocolPortFactory(_ => Task.FromResult(new CoolingPadSessionOpenResult(null, fault)));
        var opened = await factory.OpenAsync(CancellationToken.None);
        Assert.Null(opened.Port);
        Assert.Equal(expected, opened.Fault);
    }
}
