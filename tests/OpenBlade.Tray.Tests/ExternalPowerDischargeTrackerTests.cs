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
        var time = new ManualTimeProvider();
        var tracker = new ExternalPowerDischargeTracker(time);
        for (int seconds = 0; seconds <= 2; seconds++)
        {
            ObserveAt(tracker, time, seconds, -5000);
            Assert.Equal(seconds == 2, tracker.IsDischarging);
        }
        Assert.Equal(1, tracker.Episode);
        tracker.ObserveSource(PowerSource.Battery, deviceVerified: false);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        ObserveAt(tracker, time, 35, null);
        tracker.ObserveSource(PowerSource.ExternalUnknown, deviceVerified: false);
        ObserveAt(tracker, time, 90, -6000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
        ObserveAt(tracker, time, 95, 0);
        Assert.False(tracker.IsDischarging);
        for (int seconds = 100; seconds <= 102; seconds++)
        {
            ObserveAt(tracker, time, seconds, -5000);
        }
        Assert.Equal(2, tracker.Episode);
        tracker.ObserveSource(PowerSource.Battery, deviceVerified: true);
        Assert.False(tracker.IsDischarging);
    }

    [Fact]
    public void GapsUnknownSamplesAndTimestampRollbackCannotConfirmDischarge()
    {
        var time = new ManualTimeProvider();
        var tracker = new ExternalPowerDischargeTracker(time);
        ObserveAt(tracker, time, 0, -5000);
        ObserveAt(tracker, time, 31, -5000);
        Assert.False(tracker.IsDischarging);
        ObserveAt(tracker, time, 32, null);
        ObserveAt(tracker, time, 33, -5000);
        ObserveAt(tracker, time, 20, -5000);
        Assert.False(tracker.IsDischarging);
        Assert.Equal(0, tracker.Episode);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-60)]
    public void WallClockAdjustmentsDoNotShortenOrRestartConfirmation(int adjustmentSeconds)
    {
        var time = new ManualTimeProvider();
        var tracker = new ExternalPowerDischargeTracker(time);
        ObserveAt(tracker, time, 0, -5000);

        time.UtcNow = Start.AddSeconds(1 + adjustmentSeconds);
        ObserveAt(tracker, time, 1, -5000);
        Assert.False(tracker.IsDischarging);

        time.UtcNow = time.UtcNow.AddSeconds(1);
        ObserveAt(tracker, time, 2, -5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5000)]
    public void InterruptedDischargeRestartsTwoSecondConfirmation(int? interruption)
    {
        var time = new ManualTimeProvider();
        var tracker = new ExternalPowerDischargeTracker(time);
        ObserveAt(tracker, time, 0, -5000);
        ObserveAt(tracker, time, 1, interruption);
        ObserveAt(tracker, time, 2, -5000);
        time.Timestamp = TimeSpan.FromMilliseconds(3999).Ticks;
        tracker.Observe(-5000, MaximumGap);
        Assert.False(tracker.IsDischarging);
        Assert.Equal(0, tracker.Episode);

        ObserveAt(tracker, time, 4, -5000);
        Assert.True(tracker.IsDischarging);
        Assert.Equal(1, tracker.Episode);
    }

    private static void ObserveAt(
        ExternalPowerDischargeTracker tracker, ManualTimeProvider time, int seconds, int? milliwatts)
    {
        time.Timestamp = TimeSpan.FromSeconds(seconds).Ticks;
        tracker.Observe(milliwatts, MaximumGap);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
        public long Timestamp { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
