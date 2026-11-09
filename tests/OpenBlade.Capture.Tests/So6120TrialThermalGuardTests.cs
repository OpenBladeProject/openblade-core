using OpenBlade.Capture;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class So6120TrialThermalGuardTests
{
    [Theory]
    [InlineData(10, true, true)]
    [InlineData(55, true, true)]
    [InlineData(56, true, false)]
    [InlineData(9, true, false)]
    [InlineData(null, true, false)]
    [InlineData(40, false, false)]
    public void OnlyCoolKnownCpuAndInactiveGpuAdmitEntry(int? cpu, bool inactive, bool admitted)
    {
        var source = new Source(() => new(cpu, null, inactive));
        var observations = new List<So6120TrialThermalObservation>();
        var guard = new So6120TrialThermalGuard(source, new Clock(), observations.Add);
        Assert.False(guard.CachedAdmission());
        Assert.Equal(admitted, guard.Refresh());
        Assert.Equal(admitted, Assert.Single(observations).Admitted);
    }

    [Fact]
    public void SlowReadCannotManufactureFreshnessAndLaterFailureClearsOldAdmission()
    {
        var clock = new Clock();
        bool slow = false, fail = false;
        var guard = new So6120TrialThermalGuard(new Source(() =>
        {
            if (fail) { throw new IOException(); }
            if (slow) { clock.Advance(TimeSpan.FromSeconds(3)); }
            return new(40, null, true);
        }), clock, _ => { });
        Assert.True(guard.Refresh());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(guard.CachedAdmission());
        slow = true;
        Assert.False(guard.Refresh());
        slow = false;
        Assert.True(guard.Refresh());
        fail = true;
        Assert.False(guard.Refresh());
        Assert.False(guard.CachedAdmission());
    }

    [Fact]
    public void FailedDurableObservationCannotLeaveThermalAdmissionSet()
    {
        var guard = new So6120TrialThermalGuard(new Source(() => new(40, null, true)), new Clock(),
            _ => throw new IOException());
        Assert.Throws<IOException>(() => guard.Refresh());
        Assert.False(guard.CachedAdmission());
    }

    private sealed class Source(Func<FanTemperatureReadings> read) : IFanTemperatureSource
    { public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token) => ValueTask.FromResult(read()); }
    private sealed class Clock : TimeProvider
    {
        private long timestamp = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan by) => timestamp += by.Ticks;
    }
}
