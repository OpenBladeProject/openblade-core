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
    public void BriefReturnToLimitDoesNotResetHotRollingAverage()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(56, 40, false, false, TimeSpan.Zero));
        for (int second = 1; second <= 4; second++)
        {
            clock.Advance(1);
            Assert.True(policy.Observe(second == 4 ? 55 : 56, second >= 3 ? 56 : 40, false, false, TimeSpan.Zero));
        }
        // Four seconds at 56 and one at 55 still average above 55. The former
        // continuous timer incorrectly discarded this heat at the equality dip.
        clock.Advance(1);
        Assert.False(policy.Observe(56, 56, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.ThermalThreshold, policy.EndReason);
    }

    [Fact]
    public void CommandSamplesCannotResetAdvanceOrBypassContinuationWindows()
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
    public void MissingContinuityEndsStopAndDevicePowerDownResetsGpuWindow()
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
    public void FreshReadCannotCreditARealUncoveredGapToTheAverage()
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndependentRawCutoffEndsImmediatelyBeforeWindowFills(bool cpuHot)
    {
        var settings = Custom with { CpuImmediateCutoffCelsius = 70, GpuImmediateCutoffCelsius = 75 };
        var policy = new FanStopThermalPolicy(settings, new Clock());
        Assert.True(policy.Observe(70, 75, false, false, TimeSpan.Zero)); // equality is allowed
        Assert.Null(policy.TerminalThermalDecision);
        Assert.False(policy.Observe(cpuHot ? 71 : 40, cpuHot ? 40 : 76, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.ThermalThreshold, policy.EndReason);
        var trigger = policy.TerminalThermalDecision!;
        Assert.Equal("ImmediateCutoff", trigger.Rule);
        Assert.Equal(cpuHot ? new FanStopThermalTrigger(71, 70) : null, trigger.Cpu);
        Assert.Equal(cpuHot ? null : new FanStopThermalTrigger(76, 75), trigger.Gpu);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        Assert.Same(trigger, policy.TerminalThermalDecision);
        var command = new FanStopThermalPolicy(settings, new Clock());
        Assert.False(command.AdmitsCommand(cpuHot ? 71 : 40, cpuHot ? 40 : 76, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.ThermalThreshold, command.EndReason);
        Assert.Equal(trigger, command.TerminalThermalDecision);
    }

    [Fact]
    public void IrregularSamplesAreWeightedByElapsedTimeAndTrimmedAtWindowBoundary()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 2 }, clock);
        Assert.True(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        clock.Advance(1.5);
        Assert.True(policy.Observe(64, 40, false, false, TimeSpan.Zero));
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(.1);
            Assert.True(policy.Observe(64, 40, false, false, TimeSpan.Zero));
        }
        // Sample-count averaging would already exceed 55; elapsed average is 46.
        clock.Advance(.5);
        Assert.True(policy.Observe(64, 40, false, false, TimeSpan.Zero)); // average 52
        clock.Advance(.5);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero)); // average 58
        Assert.Equal(FanStopEndReason.ThermalThreshold, policy.EndReason);
        Assert.Equal(new FanStopThermalTrigger(58, 55), policy.TerminalThermalDecision!.Cpu);
        Assert.Null(policy.TerminalThermalDecision.Gpu);
        Assert.Contains("thermalRule=RollingAverage; cpuC=58; cpuLimitC=55; windowSeconds=2; coverageSeconds=2", policy.TerminalThermalDecision.Diagnostic);
    }

    [Fact]
    public void FullWindowIsRequiredAndSensorHeatCannotBeCombined()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 2 }, clock);
        Assert.True(policy.Observe(64, 40, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(40, 64, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(40, 64, false, false, TimeSpan.Zero)); // each average 52
        clock.Advance(.5);
        Assert.False(policy.Observe(40, 64, false, false, TimeSpan.Zero)); // GPU average 58
        Assert.Null(policy.TerminalThermalDecision!.Cpu);
        Assert.Equal(new FanStopThermalTrigger(58, 55), policy.TerminalThermalDecision.Gpu);
    }

    [Fact]
    public void ExplicitD3ResetsOnlyGpuHistoryAndWakeRequiresAFullWindow()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 2 }, clock);
        Assert.True(policy.Observe(40, 64, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(40, null, true, true, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(40, 64, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(40, 64, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.False(policy.Observe(40, 64, false, false, TimeSpan.Zero));

        clock = new Clock();
        policy = new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 2 }, clock);
        Assert.True(policy.Observe(64, 40, false, false, TimeSpan.Zero));
        clock.Advance(1);
        Assert.True(policy.Observe(64, null, true, true, TimeSpan.Zero));
        clock.Advance(1);
        Assert.False(policy.Observe(64, null, true, true, TimeSpan.Zero));
    }

    [Fact]
    public void BackwardClockEndsStopWithoutRearmingAndRapidSamplingIsBounded()
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        clock.Advance(-1);
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        clock = new Clock();
        policy = new FanStopThermalPolicy(Custom with { SecondsAboveLimit = 300 }, clock);
        for (int i = 0; i <= 4096; i++)
        {
            Assert.True(policy.Observe(40 + i % 2, 40, false, false, TimeSpan.Zero));
            clock.Advance(.001);
        }
        Assert.False(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        Assert.Null(policy.TerminalThermalDecision);
    }

    [Fact]
    public void EntryReportsBothSensorsWithoutEndingPolicyAndResetsOnSafetyOrCoolSample()
    {
        var policy = new FanStopThermalPolicy(Custom, new Clock());
        Assert.False(policy.AdmitsEntry(60, 61, false, false, TimeSpan.Zero));
        Assert.Equal("EntryLimit", policy.EntryThermalDecision!.Rule);
        Assert.Equal(new FanStopThermalTrigger(60, 55), policy.EntryThermalDecision.Cpu);
        Assert.Equal(new FanStopThermalTrigger(61, 55), policy.EntryThermalDecision.Gpu);
        Assert.Equal(FanStopEndReason.None, policy.EndReason);
        Assert.Null(policy.TerminalThermalDecision);
        Assert.False(policy.AdmitsEntry(100, null, false, false, TimeSpan.Zero));
        Assert.Null(policy.EntryThermalDecision);
        Assert.True(policy.AdmitsEntry(55, 55, false, false, TimeSpan.Zero));
        Assert.Null(policy.EntryThermalDecision);
    }

    [Fact]
    public void AutoReportsFixedCpuLimitAndSafetyDoesNotPublishHeat()
    {
        var policy = new FanStopThermalPolicy(FanStopSettings.Default, new Clock());
        Assert.False(policy.AdmitsEntry(56, null, true, false, TimeSpan.Zero));
        Assert.Equal("AutoCpuLimit", policy.EntryThermalDecision!.Rule);
        Assert.False(policy.Observe(56, null, true, false, TimeSpan.Zero));
        Assert.Equal(new FanStopThermalTrigger(56, 55), policy.TerminalThermalDecision!.Cpu);
        var safety = new FanStopThermalPolicy(FanStopSettings.Default, new Clock());
        Assert.False(safety.Observe(56, null, false, false, TimeSpan.Zero));
        Assert.Null(safety.TerminalThermalDecision);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)]
    public void HotReadingCannotReclassifyMissingOrBackwardContinuityAsThermalCompletion(int elapsed, bool command)
    {
        var clock = new Clock();
        var policy = new FanStopThermalPolicy(Custom, clock);
        Assert.True(policy.Observe(40, 40, false, false, TimeSpan.Zero));
        clock.Advance(elapsed);
        Assert.False(command ? policy.AdmitsCommand(100, 100, false, false, TimeSpan.Zero)
            : policy.Observe(100, 100, false, false, TimeSpan.Zero));
        Assert.Equal(FanStopEndReason.SafetyCondition, policy.EndReason);
        Assert.Null(policy.TerminalThermalDecision);
    }

    private sealed class Clock : TimeProvider
    {
        private long now = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        internal void Advance(double seconds) => now += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
