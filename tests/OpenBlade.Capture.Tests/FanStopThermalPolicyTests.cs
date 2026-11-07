using OpenBlade.Contracts;
using OpenBlade.FanStop;

namespace OpenBlade.Capture.Tests;

public sealed class FanStopThermalPolicyTests
{
    private static FanStopSettings Custom => FanStopSettings.Default with { EndMode = FanStopEndMode.Custom };

    [Theory]
    [InlineData(FanStopEndMode.Auto)]
    [InlineData(FanStopEndMode.Custom)]
    public void MissingCpuTemperatureRefusesEntryAndEndsOwnedStopWithoutWaiting(FanStopEndMode mode)
    {
        var settings = FanStopSettings.Default with { EndMode = mode, SecondsAboveLimit = 300 };
        var policy = new FanStopThermalPolicy(settings, new Clock());
        Assert.True(policy.Observe(45, null, true, true, TimeSpan.Zero));
        Assert.False(policy.AdmitsEntry(null, null, true, true, TimeSpan.Zero));
        Assert.False(policy.Observe(null, null, true, true, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        Assert.False(policy.AdmitsCommand(45, null, true, true, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(10, true, 2000, true)]
    [InlineData(55, true, 0, true)]
    [InlineData(56, true, 0, false)]
    [InlineData(9, true, 0, false)]
    [InlineData(null, true, 0, false)]
    [InlineData(40, false, 0, false)]
    [InlineData(40, true, 2001, false)]
    [InlineData(40, true, -1, false)]
    public void AutoPreservesExistingCpuZoneInactiveGpuAndFreshnessPolicy(int? cpu, bool inactive, int age, bool expected)
    {
        var policy = new FanStopThermalPolicy(FanStopSettings.Default, new Clock());
        Assert.Equal(expected, policy.AdmitsEntry(cpu, null, inactive, false, TimeSpan.FromMilliseconds(age)));
    }

    [Theory]
    [InlineData(56, true, FanStopEndReason.ThermalThreshold)]
    [InlineData(40, false, FanStopEndReason.SafetyCondition)]
    [InlineData(null, true, FanStopEndReason.SafetyCondition)]
    public void AutoSeparatesHeatFromSafetyExit(int? cpu, bool inactive, FanStopEndReason reason)
    {
        var policy = new FanStopThermalPolicy(FanStopSettings.Default, new Clock());
        Assert.False(policy.Observe(cpu, null, inactive, false, TimeSpan.Zero));
        Assert.Equal(reason, policy.EndReason);
    }

    [Fact]
    public void AutoRetainsFreshSamplePolicyWithoutCustomContinuousTimerState()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(FanStopSettings.Default, clock);
        Assert.True(policy.Observe(40, null, true, false, TimeSpan.Zero));
        clock.Advance(3);
        Assert.True(policy.Observe(40, null, true, false, TimeSpan.Zero));
        Assert.False(policy.Observe(40, null, true, false, TimeSpan.FromSeconds(3)));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
    }

    [Fact]
    public void CustomEntryUsesFreshActualGpuTemperatureOrExplicitDevicePowerEvidence()
    {
        var policy = new FanStopThermalPolicy(Custom, new Clock());
        Assert.True(policy.AdmitsEntry(55, 55, false, false, TimeSpan.Zero));
        Assert.False(policy.AdmitsEntry(56, 40, true, false, TimeSpan.Zero));
        Assert.False(policy.AdmitsEntry(40, 56, false, false, TimeSpan.Zero));
        Assert.False(policy.AdmitsEntry(40, null, true, false, TimeSpan.Zero));
        Assert.True(policy.AdmitsEntry(40, null, true, true, TimeSpan.Zero));
        Assert.False(policy.AdmitsEntry(40, 40, true, false, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void EachSensorNeedsItsOwnContinuousDurationAndEqualityResetsIt()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.Zero));
        for (int second = 1; second <= 4; second++)
        {
            clock.Advance(1);
            Assert.True(policy.Observe(second == 4 ? 55 : 56, second >= 3 ? 56 : 40, false, false, TimeSpan.Zero));
        }
        // CPU's four-second excursion did not transfer to GPU's two seconds.
        clock.Advance(1);
        Assert.True(policy.Observe(56, 56, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(56, 56, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(56, 56, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.False(policy.Observe(56, 56, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.ThermalThreshold, policy.EndReason);
    }

    [Fact]
    public void CommandSamplesCannotResetAdvanceOrBypassContinuationTimers()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.Zero));
        for (int second = 1; second <= 5; second++)
        {
            clock.Advance(1);
            Assert.True(policy.AdmitsCommand(40, 40, false, false, TimeSpan.Zero));
            Assert.False(policy.AdmitsEntry(56, 40, false, false, TimeSpan.Zero));
            Assert.Equal(second < 5, policy.Observe(56, 40, false, false, TimeSpan.Zero));
        }
        Assert.False(policy.AdmitsCommand(40, 40, false, false, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null, 40, false, 0, false)]
    [InlineData(40, null, false, 0, false)]
    [InlineData(40, 126, false, 0, false)]
    [InlineData(40, 40, false, 2001, false)]
    [InlineData(40, 40, false, 0, true)]
    public void FaultsImmediatelyEndCustomContinuationWithoutThermalRearm(int? cpu, int? gpu, bool down, int age, bool canceled)
    {
        var policy = new FanStopThermalPolicy(Custom, new Clock());
        Assert.False(policy.Observe(cpu, gpu, false, down, TimeSpan.FromMilliseconds(age), canceled));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
    }

    [Fact]
    public void MissingContinuityEndsStopAndDevicePowerDownResetsGpuTimer()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(40, 56, false, false, TimeSpan.Zero));
        clock.Advance(2);
        Assert.True(policy.Observe(40, null, true, true, TimeSpan.Zero));
        clock.Advance(2);
        Assert.True(policy.Observe(40, 56, false, false, TimeSpan.Zero));
        clock.Advance(3);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
    }

    [Fact]
    public void BoundedFreshReadTimeDoesNotCountAsMissingContinuation()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.FromMilliseconds(1500)));
        clock.Advance(3); // 1.5 seconds polling/power work plus a 1.5-second read.
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.FromMilliseconds(1500)));
        clock.Advance(3);
        Assert.False(policy.Observe(56, 40, false, false, TimeSpan.FromMilliseconds(1500)));
        Assert.Equal(FanStopEndReason.ThermalThreshold, policy.EndReason);
    }

    [Fact]
    public void FreshReadCannotCreditARealUncoveredGapToTheHotTimer()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.Zero));
        clock.Advance(4); // 2.5 seconds unobserved, then a valid 1.5-second read.
        Assert.False(policy.Observe(56, 40, false, false, TimeSpan.FromMilliseconds(1500)));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
    }

    [Fact]
    public void InvalidImmutablePolicyIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 0 }, new Clock()));
    }

    [Fact]
    public void FaultDuringFreshCommandAdmissionEndsContinuationWithoutResettingItsAuthority()
    {
        var policy = new FanStopThermalPolicy(Custom, new Clock());
        Assert.False(policy.AdmitsCommand(40, null, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
    }

    private sealed class Clock : TimeProvider
    {
        private long now = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        internal void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
