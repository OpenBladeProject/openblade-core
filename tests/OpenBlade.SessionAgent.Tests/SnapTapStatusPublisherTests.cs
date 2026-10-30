using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapStatusPublisherTests
{
    [Fact]
    public void WindowsBackendCannotClaimDriverDeviceOrValidationProvenance()
    {
        var profile = RazerSnapTapDriverProfiles.All.First(profile => !profile.PhysicallyValidated);
        var failure = new DiagnosticFailure("SnapTapWorker", "WindowsHook", "SendInput", "OutputFailed", true);
        SnapTapRuntimeStatus status = SnapTapStatusPublisher.CreateStatus(
            SnapTapRuntimeState.Failed, failure, profile, KeyboardInputBackend.WindowsHook);
        Assert.Equal(KeyboardInputBackend.WindowsHook, status.Backend);
        Assert.Null(status.DriverModel);
        Assert.False(status.ExperimentalDriver);
        Assert.Same(failure, status.Failure);
    }

    [Fact]
    public void DriverBackendRetainsItsReviewedProfileProvenance()
    {
        foreach (RazerSnapTapDriverProfile profile in RazerSnapTapDriverProfiles.All)
        {
            SnapTapRuntimeStatus status = SnapTapStatusPublisher.CreateStatus(
                SnapTapRuntimeState.Armed, null, profile, KeyboardInputBackend.RazerDriver);
            Assert.Equal(profile.ProductName, status.DriverModel);
            Assert.Equal(!profile.PhysicallyValidated, status.ExperimentalDriver);
            Assert.Equal(KeyboardInputBackend.RazerDriver, status.Backend);
        }
    }
}
