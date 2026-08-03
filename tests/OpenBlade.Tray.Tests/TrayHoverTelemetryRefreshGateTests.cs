using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TrayHoverTelemetryRefreshGateTests
{
    [Fact]
    public void FirstHoverRefreshesAndRepeatedMouseMovementIsRateLimited()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var gate = new TrayHoverTelemetryRefreshGate(time);

        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(2)));
        Assert.False(gate.TryAcquire(TimeSpan.FromSeconds(2)));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(gate.TryAcquire(TimeSpan.FromSeconds(2)));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void OffNeverRefreshesAndResetAllowsImmediateRefresh()
    {
        var gate = new TrayHoverTelemetryRefreshGate();

        Assert.False(gate.TryAcquire(Timeout.InfiniteTimeSpan));
        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(10)));
        Assert.False(gate.TryAcquire(TimeSpan.FromSeconds(10)));

        gate.Reset();
        Assert.True(gate.TryAcquire(TimeSpan.FromSeconds(10)));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
