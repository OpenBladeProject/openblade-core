using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ProfileAutomationStateTests
{
    [Fact]
    public void PowerTransition_ClearsTemporaryOverrideButKeepsPersistentOverride()
    {
        ProfileAutomationState state = new(ProfileDefaults.CreateSafeDefaults(), PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.UntilPowerSourceChange));
        state.SetFanOverride(new ManualOverride<FanMode>(
            FanMode.Maximum,
            OverrideLifetime.Persistent));

        state.ChangePowerSource(PowerSource.Battery);
        DesiredDeviceState desired = state.Resolve(new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.Silent, desired.Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, desired.Performance.Source);
        Assert.Equal(FanMode.Maximum, desired.Fans.Value);
        Assert.Equal(SettingValueSource.PersistentOverride, desired.Fans.Source);
    }

    [Fact]
    public void ChargeToFull_IsAutomaticallyCancelledOnUnplug()
    {
        ProfileAutomationState state = new(ProfileDefaults.CreateSafeDefaults(), PowerSource.PluggedIn);
        state.EnableTemporaryFullCharge();

        Assert.True(state.BatteryPolicy.TemporaryFullCharge);

        state.ChangePowerSource(PowerSource.Battery);

        Assert.False(state.BatteryPolicy.TemporaryFullCharge);
        Assert.Equal(80, state.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public void ChargeToFull_CannotBeEnabledWhileAlreadyOnBattery()
    {
        ProfileAutomationState state = new(
            ProfileDefaults.CreateSafeDefaults(),
            PowerSource.Battery);

        state.EnableTemporaryFullCharge();

        Assert.False(state.BatteryPolicy.TemporaryFullCharge);
        Assert.Equal(80, state.BatteryPolicy.ChargeLimitPercent);
    }
}
