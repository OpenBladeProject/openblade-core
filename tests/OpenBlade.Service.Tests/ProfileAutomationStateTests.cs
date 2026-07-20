using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileAutomationStateTests
{
    [Fact]
    public void PluggedInFixedOverrideCarriesItsAdjustableTargetIntoDesiredState()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        var settings = new FanControlSettings(
            FanMode.Fixed,
            3700,
            ManualFanCurve.Default);
        state.SetFanOverride(
            new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.UntilPowerSourceChange),
            settings);

        DesiredDeviceState desired = state.Resolve(
            new ConfirmedDeviceState(
                PerformancePreset.Balanced,
                FanMode.Automatic,
                null,
                null));

        Assert.Equal(FanMode.Fixed, desired.Fans.Value);
        Assert.Equal(SettingValueSource.TemporaryOverride, desired.Fans.Source);
        Assert.Equal(settings, desired.FanControl);
    }

    [Fact]
    public void DisabledProtectionPreferenceSurvivesProfileUpdate()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        ProfileAutomationState state = new(
            profiles,
            new BatteryAutomationPreferences(ProtectionEnabled: false),
            PowerSource.PluggedIn);
        ProfileSet updated = profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        state.UpdateProfiles(updated);

        Assert.False(state.DesiredBattery.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(75), state.DesiredBattery.ConfiguredPolicy);
    }

    [Fact]
    public void DisabledProtectionCannotEnableTemporaryFullCharge()
    {
        ProfileAutomationState state = new(
            ProfileDefaults.CreateSafeDefaults(),
            new BatteryAutomationPreferences(ProtectionEnabled: false),
            PowerSource.PluggedIn);

        state.EnableTemporaryFullCharge();

        Assert.False(state.DesiredBattery.TemporaryFullCharge);
        Assert.False(state.DesiredBattery.ProtectionEnabled);
    }

    [Fact]
    public void DesiredBatteryState_CommitsProtectionAndConfiguredLimitTogether()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        var desired = new DesiredBatteryState(
            BatteryPolicy.Create(75),
            protectionEnabled: false,
            temporaryFullCharge: false);

        state.CommitBatteryState(desired);

        Assert.Same(desired, state.DesiredBattery);
        Assert.Equal(BatteryPolicy.Create(75), state.Profiles.BatteryPolicy);
    }

    [Fact]
    public void PowerTransition_ClearsTemporaryOverrideButKeepsPersistentOverride()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.UntilPowerSourceChange));
        state.SetFanOverride(new ManualOverride<FanMode>(
            FanMode.Maximum,
            OverrideLifetime.Persistent));

        state.ChangePowerSource(PowerSource.Battery);
        DesiredDeviceState desired = state.Resolve(new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.BatterySaver, desired.Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, desired.Performance.Source);
        Assert.Equal(FanMode.Maximum, desired.Fans.Value);
        Assert.Equal(SettingValueSource.PersistentOverride, desired.Fans.Source);
    }

    [Fact]
    public void PersistentPluggedInPerformanceOverride_PausesOnBatteryAndReturnsOnAc()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.Persistent));

        state.ChangePowerSource(PowerSource.Battery);
        DesiredDeviceState onBattery = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.BatterySaver, onBattery.Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, onBattery.Performance.Source);
        Assert.Equal(PerformancePreset.Performance, state.PerformanceOverride!.Value);
        Assert.Equal(OverrideLifetime.Persistent, state.PerformanceOverride.Lifetime);

        state.ChangePowerSource(PowerSource.PluggedIn);
        DesiredDeviceState onAc = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.Performance, onAc.Performance.Value);
        Assert.Equal(SettingValueSource.PersistentOverride, onAc.Performance.Source);
    }

    [Fact]
    public void CustomPerformanceOverride_PreservesLevelsAndPausesAwayFromAc()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        var levels = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);
        state.SetPerformanceOverride(new PerformanceOverride(
            PerformancePreset.Custom,
            OverrideLifetime.Persistent,
            levels));

        DesiredDeviceState onAc = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));
        state.ChangePowerSource(PowerSource.Battery);
        DesiredDeviceState onBattery = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));
        state.ChangePowerSource(PowerSource.PluggedIn);
        DesiredDeviceState restored = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.Custom, onAc.Performance.Value);
        Assert.Equal(levels, onAc.CustomPerformance);
        Assert.Equal(PerformancePreset.BatterySaver, onBattery.Performance.Value);
        Assert.Null(onBattery.CustomPerformance);
        Assert.Equal(PerformancePreset.Custom, restored.Performance.Value);
        Assert.Equal(levels, restored.CustomPerformance);
        Assert.Equal(SettingValueSource.PersistentOverride, restored.Performance.Source);
    }

    [Fact]
    public void PersistentBatteryPerformanceOverride_PausesOnAcAndReturnsOnBattery()
    {
        ProfileAutomationState state = CreateState(PowerSource.Battery);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.BatteryBalanced,
            OverrideLifetime.Persistent));

        state.ChangePowerSource(PowerSource.PluggedIn);
        DesiredDeviceState onAc = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.Balanced, onAc.Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, onAc.Performance.Source);
        Assert.Equal(PerformancePreset.BatteryBalanced, state.PerformanceOverride!.Value);
        Assert.Equal(OverrideLifetime.Persistent, state.PerformanceOverride.Lifetime);

        state.ChangePowerSource(PowerSource.Battery);
        DesiredDeviceState onBattery = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.BatteryBalanced, onBattery.Performance.Value);
        Assert.Equal(SettingValueSource.PersistentOverride, onBattery.Performance.Source);
    }

    [Fact]
    public void UsbC_UsesDedicatedProfileAndPausesIncompatiblePersistentOverrides()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = PerformancePreset.Performance,
                Fans = FanMode.Maximum,
            },
        };
        ProfileAutomationState state = new(
            profiles,
            new BatteryAutomationPreferences(ProtectionEnabled: true),
            PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.Persistent));
        state.SetFanOverride(new ManualOverride<FanMode>(
            FanMode.Maximum,
            OverrideLifetime.Persistent));

        state.ChangePowerSource(PowerSource.UsbC);
        DesiredDeviceState desired = state.Resolve(
            new ConfirmedDeviceState(null, null, null, null));

        Assert.Equal(PerformancePreset.Balanced, desired.Performance.Value);
        Assert.Equal(FanMode.Automatic, desired.Fans.Value);
        Assert.Equal(SettingValueSource.PowerProfile, desired.Performance.Source);
        Assert.Equal(SettingValueSource.PowerProfile, desired.Fans.Source);
        Assert.Equal(PerformancePreset.Performance, state.PerformanceOverride!.Value);
        Assert.Equal(FanMode.Maximum, state.FanOverride!.Value);
    }

    [Fact]
    public void NewTemporaryOverride_ReplacesPersistentSlotInsteadOfBeingMasked()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.Persistent));

        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Silent,
            OverrideLifetime.UntilPowerSourceChange));

        Assert.Equal(OverrideLifetime.UntilPowerSourceChange, state.PerformanceOverride!.Lifetime);
        Assert.Equal(
            SettingValueSource.TemporaryOverride,
            state.Resolve(new ConfirmedDeviceState(null, null, null, null)).Performance.Source);

        state.ChangePowerSource(PowerSource.Battery);
        Assert.Null(state.PerformanceOverride);
    }

    [Fact]
    public void NewPersistentOverride_ReplacesTemporarySlotAndSurvivesTransition()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetFanOverride(new ManualOverride<FanMode>(
            FanMode.Maximum,
            OverrideLifetime.UntilPowerSourceChange));

        state.SetFanOverride(new ManualOverride<FanMode>(
            FanMode.Automatic,
            OverrideLifetime.Persistent));
        state.ChangePowerSource(PowerSource.Battery);

        Assert.Equal(OverrideLifetime.Persistent, state.FanOverride!.Lifetime);
        Assert.Equal(FanMode.Automatic, state.FanOverride.Value);
    }

    [Fact]
    public void Constructor_RestoresOnlyDurablePersistentOverrides()
    {
        ProfileAutomationState state = new(
            ProfileDefaults.CreateSafeDefaults(),
            new BatteryAutomationPreferences(ProtectionEnabled: true),
            PowerSource.PluggedIn,
            new PersistentOverrideState(
                PerformancePreset.Performance,
                FanMode.Maximum,
                null));

        Assert.Equal(OverrideLifetime.Persistent, state.PerformanceOverride!.Lifetime);
        Assert.Equal(OverrideLifetime.Persistent, state.FanOverride!.Lifetime);
        Assert.Null(state.LightingOverride);
    }

    [Fact]
    public void ChargeToFull_IsAutomaticallyCancelledOnUnplug()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.EnableTemporaryFullCharge();

        Assert.True(state.DesiredBattery.TemporaryFullCharge);

        state.ChangePowerSource(PowerSource.Battery);

        Assert.False(state.DesiredBattery.TemporaryFullCharge);
        Assert.Equal(80, state.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public void ChargeToFull_CannotBeEnabledWhileAlreadyOnBattery()
    {
        ProfileAutomationState state = CreateState(PowerSource.Battery);

        state.EnableTemporaryFullCharge();

        Assert.False(state.DesiredBattery.TemporaryFullCharge);
        Assert.Equal(80, state.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public void ChangePowerSource_SameSourceReturnsFalseWithoutClearingTemporaryState()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.UntilPowerSourceChange));
        state.EnableTemporaryFullCharge();

        bool changed = state.ChangePowerSource(PowerSource.PluggedIn);

        Assert.False(changed);
        Assert.NotNull(state.PerformanceOverride);
        Assert.True(state.DesiredBattery.TemporaryFullCharge);
    }

    [Fact]
    public void ChangePowerSource_TransitionReturnsTrueAfterClearingTemporaryState()
    {
        ProfileAutomationState state = CreateState(PowerSource.PluggedIn);
        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Performance,
            OverrideLifetime.UntilPowerSourceChange));
        state.EnableTemporaryFullCharge();

        bool changed = state.ChangePowerSource(PowerSource.Battery);

        Assert.True(changed);
        Assert.Null(state.PerformanceOverride);
        Assert.False(state.DesiredBattery.TemporaryFullCharge);
    }

    private static ProfileAutomationState CreateState(PowerSource powerSource) => new(
        ProfileDefaults.CreateSafeDefaults(),
        new BatteryAutomationPreferences(ProtectionEnabled: true),
        powerSource);
}
