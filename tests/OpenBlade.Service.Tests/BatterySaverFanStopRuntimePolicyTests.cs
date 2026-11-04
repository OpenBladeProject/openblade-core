using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class BatterySaverFanStopRuntimePolicyTests
{
    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Lighting)]
    public void LowPowerCleanupGrantCannotAdmitAnotherSubsystem(DeviceSubsystem subsystem) =>
        Assert.Throws<ArgumentException>(() => new ScopedThermalExecutionOptions([subsystem],
            ScopedThermalFreshSessionRetryPolicy.BlockDuringBoundedCleanup,
            authorityPurpose: ScopedThermalAuthorityPurpose.PriorAuthorityCleanup,
            lowPowerFanCleanupAdmission: static () => true));

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(56, true, false)]
    [InlineData(45, false, false)]
    [InlineData(55, true, true)]
    [InlineData(45, true, true)]
    public void CoolingAdmissionRequiresKnownCoolCpuAndConfirmedInactiveGpu(
        int? cpu, bool inactiveGpu, bool admitted) =>
        Assert.Equal(admitted, BatterySaverFanStopRuntimePolicy.IsCool(new(cpu, null, inactiveGpu)));

    [Fact]
    public void OwnedStopReleasesToAutomaticAndLatchesUntilScopeExit()
    {
        var policy = new BatterySaverFanStopRuntimePolicy();
        Assert.Equal(FanMode.Stop, policy.Resolve(true, true, false));
        Assert.Equal(FanMode.Automatic, policy.Resolve(true, false, true));
        Assert.Equal(FanMode.Automatic, policy.Resolve(true, true, false));
        Assert.True(policy.IsSuspended(true, true));
        Assert.Null(policy.Resolve(false, true, false));
        Assert.Equal(FanMode.Stop, policy.Resolve(true, true, false));
    }

    [Fact]
    public void InitialUnsafeReadingPreservesOrdinaryIntentAndCanLaterEngage()
    {
        var policy = new BatterySaverFanStopRuntimePolicy();
        Assert.Null(policy.Resolve(true, false, false));
        Assert.True(policy.IsSuspended(true, false));
        Assert.False(policy.ThermallyLatched);
        Assert.Equal(FanMode.Stop, policy.Resolve(true, true, false));
    }

    [Fact]
    public void ExplicitPreferenceCycleResetsThermalSuspension()
    {
        var policy = new BatterySaverFanStopRuntimePolicy();
        policy.Resolve(true, false, true);
        policy.Reset();
        Assert.Equal(FanMode.Stop, policy.Resolve(true, true, false));
    }

    [Theory]
    [InlineData(false, true, PowerSource.Battery, PerformancePreset.BatterySaver, false)]
    [InlineData(true, false, PowerSource.Battery, PerformancePreset.BatterySaver, false)]
    [InlineData(true, true, PowerSource.PluggedIn, PerformancePreset.BatterySaver, false)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.BatterySaver, false)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Balanced, false)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.BatterySaver, true)]
    public void ScopeRequiresOptInCapabilityRealBatteryAndEffectiveSaver(
        bool requested, bool capability, PowerSource power, PerformancePreset performance, bool expected) =>
        Assert.Equal(expected, BatterySaverFanStopRuntimePolicy.IsInScope(requested,
            DeviceCapabilities.SafeTargetDefaults() with { SupportsBatterySaverFanStop = capability }, power, performance));
}
