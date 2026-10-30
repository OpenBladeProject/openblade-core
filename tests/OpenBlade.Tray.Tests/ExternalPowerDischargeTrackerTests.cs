using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ExternalPowerDischargeTrackerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(10);

    [Fact]
    public void ConfirmsSustainedDischargeAndRearmsOnlyAfterKnownRecovery()
    {
        var tracker = new ExternalPowerDischargeTracker();
        for (int seconds = 0; seconds <= 10; seconds += 2)
        {
            tracker.Observe(Start.AddSeconds(seconds), -5000, MaximumGap);
            Assert.Equal(seconds == 10, tracker.IsDischarging);
        }
        Assert.Equal(1, tracker.Episode);
        tracker.ObserveSource(PowerSource.Battery, deviceVerified: false);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(Start.AddSeconds(35), null, MaximumGap);
        tracker.ObserveSource(PowerSource.ExternalUnknown, deviceVerified: false);
        tracker.Observe(Start.AddSeconds(90), -6000, MaximumGap);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        tracker.Observe(Start.AddSeconds(95), 0, MaximumGap);
        Assert.False(tracker.IsDischarging);
        for (int seconds = 100; seconds <= 110; seconds += 2)
        {
            tracker.Observe(Start.AddSeconds(seconds), -5000, MaximumGap);
        }
        Assert.Equal(2, tracker.Episode);
        tracker.ObserveSource(PowerSource.Battery, deviceVerified: true);
        Assert.False(tracker.IsDischarging);
    }

    [Fact]
    public void GapsUnknownSamplesAndClockRollbackCannotConfirmDischarge()
    {
        var tracker = new ExternalPowerDischargeTracker();
        tracker.Observe(Start, -5000, MaximumGap);
        tracker.Observe(Start.AddSeconds(31), -5000, MaximumGap);
        Assert.False(tracker.IsDischarging);
        tracker.Observe(Start.AddSeconds(32), null, MaximumGap);
        tracker.Observe(Start.AddSeconds(33), -5000, MaximumGap);
        tracker.Observe(Start.AddSeconds(20), -5000, MaximumGap);
        Assert.False(tracker.IsDischarging);
        Assert.Equal(0, tracker.Episode);
    }
}
