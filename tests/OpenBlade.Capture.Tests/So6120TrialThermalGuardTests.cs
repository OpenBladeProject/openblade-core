using OpenBlade.Capture;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class So6120TrialThermalGuardTests
{
    [Theory]
    [InlineData(10, null, true, true, true)]
    [InlineData(55, null, true, true, true)]
    [InlineData(56, null, true, true, false)]
    [InlineData(9, null, true, true, false)]
    [InlineData(null, null, true, true, false)]
    [InlineData(40, 40, false, false, false)]
    [InlineData(40, 10, true, false, true)]
    [InlineData(40, 55, true, false, true)]
    [InlineData(40, 85, true, false, false)]
    [InlineData(40, 9, true, false, false)]
    [InlineData(40, null, true, false, false)]
    public void OnlyCoolKnownCpuAndInactiveGpuWithThermalEvidenceAdmitEntry(
        int? cpu, int? gpu, bool inactive, bool poweredDown, bool admitted)
    {
        var source = new Source(() => new(cpu, gpu, inactive, GpuPoweredDown: poweredDown));
        var observations = new List<So6120TrialThermalObservation>();
        var guard = new So6120TrialThermalGuard(source, new Clock(), observations.Add);
        Assert.False(guard.CachedAdmission());
        Assert.Equal(admitted, guard.Refresh());
        var observation = Assert.Single(observations);
        Assert.Equal(admitted, observation.Admitted);
        Assert.Equal(gpu, observation.GpuCelsius);
        Assert.Equal(poweredDown, observation.GpuPoweredDown);
        Assert.Equal(1, source.FanStopReads);
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
            return new(40, null, true, GpuPoweredDown: true);
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
        var guard = new So6120TrialThermalGuard(new Source(() => new(40, null, true, GpuPoweredDown: true)), new Clock(),
            _ => throw new IOException());
        Assert.Throws<IOException>(() => guard.Refresh());
        Assert.False(guard.CachedAdmission());
    }

    private sealed class Source(Func<FanTemperatureReadings> read) : IFanTemperatureSource
    {
        internal int FanStopReads { get; private set; }
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token) =>
            throw new InvalidOperationException("Fan-stop admission requires the dedicated thermal read.");
        public ValueTask<FanTemperatureReadings> ReadForFanStopAsync(CancellationToken token)
        {
            FanStopReads++;
            return ValueTask.FromResult(read());
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan by) => timestamp += by.Ticks;
    }
}
