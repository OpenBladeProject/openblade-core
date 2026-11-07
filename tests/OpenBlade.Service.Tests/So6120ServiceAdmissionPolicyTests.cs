using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class So6120ServiceAdmissionPolicyTests
{
    [Theory]
    [InlineData(AdapterPowerState.Unknown)]
    [InlineData((AdapterPowerState)99)]
    public async Task CompleteUnknownPowerReplyLosesStopScopeWithoutInventingNativeAmbiguity(AdapterPowerState state)
    {
        PowerSource source = await So6120ServicePowerReadPolicy.ReadAsync(_ => Task.FromResult(state), default);
        Assert.Equal(PowerSource.ExternalUnknown, source);
        Assert.Equal(FanStopPowerTransition.Automatic,
            FanStopPowerTransitionPolicy.Evaluate(PowerSource.UsbC, source));
    }

    [Theory]
    [InlineData(AdapterPowerState.LimitedExternal, PowerSource.UsbC)]
    [InlineData(AdapterPowerState.SufficientExternal, PowerSource.PluggedIn)]
    [InlineData(AdapterPowerState.Battery, PowerSource.Battery)]
    public async Task CompletedRelativeWattageStatesRetainTheirTypedPowerMeaning(AdapterPowerState state, PowerSource expected)
    {
        Assert.Equal(expected, await So6120ServicePowerReadPolicy.ReadAsync(_ => Task.FromResult(state), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledNativeReadRemainsUnresolvedAndCannotBeUsedAsAutoAdmission(bool canceled)
    {
        Exception cause = canceled ? new OperationCanceledException() : new TimeoutException();
        var failure = await Assert.ThrowsAsync<So6120ServicePowerReadException>(() =>
            So6120ServicePowerReadPolicy.ReadAsync(_ => Task.FromException<AdapterPowerState>(cause), default));
        Assert.True(failure.NativeOperationUnresolved);
        Assert.Same(cause, failure.InnerException);
    }

    [Fact]
    public void ContinuousMonitorSamplesAtMostOncePerSecondAndSeesNewControllerLoss()
    {
        var clock = new Clock();
        int observations = 0;
        bool clear = true;
        var monitor = new So6120ServiceContinuationMonitor(() => { observations++; return clear; }, clock);
        Assert.True(monitor.Observe());
        for (int index = 0; index < 100; index++) { Assert.True(monitor.Observe()); }
        Assert.Equal(1, observations);
        clear = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(monitor.Observe());
        Assert.Equal(2, observations);
    }

    [Fact]
    public void SlowOrFailedContinuousObservationCannotRetainStop()
    {
        var clock = new Clock();
        var slow = new So6120ServiceContinuationMonitor(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(2001));
            return true;
        }, clock);
        Assert.False(slow.Observe());
        var failed = new So6120ServiceContinuationMonitor(() => throw new IOException(), clock);
        Assert.False(failed.Observe());
    }

    [Theory]
    [InlineData(10, true, 0, false, true)]
    [InlineData(55, true, 2000, false, true)]
    [InlineData(9, true, 0, false, false)]
    [InlineData(56, true, 0, false, false)]
    [InlineData(null, true, 0, false, false)]
    [InlineData(35, false, 0, false, false)]
    [InlineData(35, true, 2001, false, false)]
    [InlineData(35, true, 0, true, false)]
    public void ThermalSampleMustBePlausibleCoolGpuInactiveAndFresh(int? cpu, bool gpuInactive,
        int milliseconds, bool canceled, bool expected)
    {
        var policy = new FanStopThermalPolicy(FanStopSettings.Default, TimeProvider.System);
        Assert.Equal(expected, policy.AdmitsEntry(cpu, null, gpuInactive, false,
            TimeSpan.FromMilliseconds(milliseconds), canceled));
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
