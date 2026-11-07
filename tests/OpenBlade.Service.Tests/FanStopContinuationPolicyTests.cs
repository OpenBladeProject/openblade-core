using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopContinuationPolicyTests
{
    [Fact]
    public void SlowOrThrowingThermalExitCannotBecomeFreshThermalRearmEvidence()
    {
        var clock = new Clock();
        var slow = new So6120ServiceContinuationMonitor(() => { clock.Advance(2001); return false; }, clock);
        Assert.False(slow.Observe());
        Assert.False(slow.SampleFresh);
        var failed = new So6120ServiceContinuationMonitor(() => throw new IOException(), clock);
        Assert.False(failed.Observe());
        Assert.False(failed.SampleFresh);
        var freshExit = new So6120ServiceContinuationMonitor(() => false, clock);
        Assert.False(freshExit.Observe());
        Assert.True(freshExit.SampleFresh);
    }

    private sealed class Clock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        internal void Advance(int milliseconds) => now += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
