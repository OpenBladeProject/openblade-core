using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class DeviceSubsystemOrderTests
{
    [Fact]
    public void ForTransition_UsesFansFirstOnlyWhenLeavingConfirmedManualFans()
    {
        ConfirmedDeviceState manual = Confirmed(fans: FanMode.Maximum);
        ConfirmedDeviceState automatic = Confirmed(fans: FanMode.Automatic);

        Assert.Same(
            DeviceSubsystemOrder.FansFirst,
            DeviceSubsystemOrder.ForTransition(
                manual,
                Desired(fans: FanMode.Automatic)));
        Assert.Same(
            DeviceSubsystemOrder.Default,
            DeviceSubsystemOrder.ForTransition(
                automatic,
                Desired(fans: FanMode.Automatic)));
        Assert.Same(
            DeviceSubsystemOrder.Default,
            DeviceSubsystemOrder.ForTransition(
                manual,
                Desired(fans: FanMode.Maximum)));
    }

    [Fact]
    public void ManualFanPerformanceBridge_RequiresConfirmedAndDesiredManualFans()
    {
        Assert.True(DeviceSubsystemOrder.RequiresManualFanPerformanceBridge(
            Confirmed(fans: FanMode.Maximum),
            Desired(fans: FanMode.Maximum)));
        Assert.False(DeviceSubsystemOrder.RequiresManualFanPerformanceBridge(
            Confirmed(fans: FanMode.Automatic),
            Desired(fans: FanMode.Maximum)));
        Assert.False(DeviceSubsystemOrder.RequiresManualFanPerformanceBridge(
            Confirmed(fans: FanMode.Maximum),
            Desired(fans: FanMode.Automatic)));
    }

    [Theory]
    [InlineData(FanMode.Automatic, false)]
    [InlineData(FanMode.Maximum, true)]
    [InlineData(FanMode.Fixed, true)]
    [InlineData(FanMode.ManualCurve, true)]
    public void IsManualFanMode_ClassifiesEveryDefinedFanMode(
        FanMode mode,
        bool expected) =>
        Assert.Equal(expected, DeviceSubsystemOrder.IsManualFanMode(mode));

    private static ConfirmedDeviceState Confirmed(FanMode fans) => new(
        PerformancePreset.Balanced,
        fans,
        null,
        null);

    private static DesiredDeviceState Desired(FanMode fans) => new(
        new ResolvedSetting<PerformancePreset>(
            PerformancePreset.Balanced,
            SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(fans, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(
            LightingSettingsFactory.Off(),
            SettingValueSource.PowerProfile),
        new DesiredBatteryState(BatteryPolicy.Create(80), false, false));
}
