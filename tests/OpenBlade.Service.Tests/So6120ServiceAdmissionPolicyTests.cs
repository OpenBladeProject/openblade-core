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

}
