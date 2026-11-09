using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ExternalPowerDischargeTrackerTests
{
    [Fact]
    public void FirstNegativeSampleStartsAnEpisodeAndFurtherDischargeDoesNotRepeatIt()
    {
        var tracker = new ExternalPowerDischargeTracker();

        tracker.Observe(-1);

        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(-6000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5000)]
    public void UnknownOrNonnegativeSampleDoesNotStartAnEpisode(int? milliwatts)
    {
        var tracker = new ExternalPowerDischargeTracker();

        tracker.Observe(milliwatts);

        Assert.False(tracker.IsDischarging);
        Assert.Equal(0, tracker.Episode);
        tracker.Observe(-5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
    }

    [Fact]
    public void UnknownReadingsAndUnverifiedSourcesPreserveAnActiveEpisode()
    {
        var tracker = new ExternalPowerDischargeTracker();
        tracker.Observe(-5000);

        tracker.Observe(null);
        tracker.ObserveSource(PowerSource.Battery, deviceVerified: false);
        tracker.ObserveSource(PowerSource.ExternalUnknown, deviceVerified: true);
        tracker.ObserveSource(PowerSource.UsbC, deviceVerified: false);

        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(-6000);
        Assert.Equal(1, tracker.Episode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    public void KnownRecoveryClearsTheWarningAndTheNextNegativeSampleRearmsImmediately(int milliwatts)
    {
        var tracker = new ExternalPowerDischargeTracker();
        tracker.Observe(-5000);

        tracker.Observe(milliwatts);

        Assert.False(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(-5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(2, tracker.Episode);
    }

    [Fact]
    public void VerifiedUnplugClearsTheWarningAndAllowsANewEpisode()
    {
        var tracker = new ExternalPowerDischargeTracker();
        tracker.Observe(-5000);

        tracker.ObserveSource(PowerSource.Battery, deviceVerified: true);

        Assert.False(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.ObserveSource(PowerSource.UsbC, deviceVerified: true);
        tracker.Observe(-5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(2, tracker.Episode);
    }

    [Fact]
    public void ExplicitResetClearsStateWithoutReusingThePreviousEpisodeIdentity()
    {
        var tracker = new ExternalPowerDischargeTracker();
        tracker.Observe(-5000);

        tracker.Reset();

        Assert.False(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(-5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(2, tracker.Episode);
    }
}
