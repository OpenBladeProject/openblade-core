using OpenBlade.Contracts;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopOwnedPowerMonitorTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.ExternalUnknown)]
    public void AdapterUpgradeOrUnknownAtEitherPublicationBoundaryRequiresAuto(PowerSource next)
    {
        foreach (bool coast in new[] { true, false })
        {
            int reads = 0, verifications = 0;
            var monitor = new So6120ServiceOwnedPowerMonitor(PowerSource.UsbC,
                () => { reads++; return next; }, static () => true,
                () => { verifications++; return true; });
            Assert.False(coast ? monitor.BeforeCoastObservation() : monitor.BeforeHeldStop());
            Assert.Equal(FanStopEndReason.PowerChanged, monitor.EndReason);
            Assert.False(monitor.BeforeHeldStop());
            Assert.Equal(1, reads);
            Assert.Equal(0, verifications);
        }
    }

    [Fact]
    public void LowerPowerCoastUsesTheUpcomingMandatoryObservationWithoutAnExtraRead()
    {
        int reads = 0, verifications = 0;
        var monitor = new So6120ServiceOwnedPowerMonitor(PowerSource.UsbC,
            () => { reads++; return PowerSource.Battery; }, static () => true,
            () => { verifications++; return true; });
        Assert.True(monitor.BeforeCoastObservation());
        Assert.Equal(PowerSource.Battery, monitor.Current);
        Assert.Equal(1, reads);
        Assert.Equal(0, verifications);
        // The core caller performs its mandatory native observation next.
    }

    [Theory]
    [InlineData(PowerSource.Battery, true)]
    [InlineData(PowerSource.UsbC, false)]
    [InlineData(PowerSource.PluggedIn, false)]
    [InlineData(PowerSource.ExternalUnknown, false)]
    public void LowerPowerBeforeHeldStopRequiresOwnershipProofThenAStablePowerRead(PowerSource after, bool held)
    {
        int reads = 0, verifications = 0;
        var monitor = new So6120ServiceOwnedPowerMonitor(PowerSource.UsbC,
            () => ++reads == 1 ? PowerSource.Battery : after, static () => true,
            () => { verifications++; return true; });
        Assert.Equal(held, monitor.BeforeHeldStop());
        Assert.Equal(2, reads);
        Assert.Equal(1, verifications);
        if (!held) { Assert.Equal(FanStopEndReason.PowerChanged, monitor.EndReason); }
    }

    [Fact]
    public void PowerQueryFailureIsNotRetriedOrConvertedIntoCleanupAuthority()
    {
        int reads = 0;
        var failure = new So6120ServicePowerReadException(new TimeoutException());
        var monitor = new So6120ServiceOwnedPowerMonitor(PowerSource.UsbC,
            () => { reads++; throw failure; }, static () => true, static () => true);
        Assert.Same(failure, Assert.Throws<So6120ServicePowerReadException>(() => monitor.BeforeCoastObservation()));
        Assert.False(monitor.BeforeHeldStop());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ThermalOrSessionLossDuringPowerReadPreventsOwnershipReadAndPublication()
    {
        bool safe = true;
        int verifications = 0;
        var monitor = new So6120ServiceOwnedPowerMonitor(PowerSource.UsbC,
            () => { safe = false; return PowerSource.Battery; }, () => safe,
            () => { verifications++; return true; });
        Assert.False(monitor.BeforeHeldStop());
        Assert.Equal(0, verifications);
    }
}
