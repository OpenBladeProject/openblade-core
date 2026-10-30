using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconStateResolverTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Custom, false)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance, true)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Balanced, false)]
    public void DischargeWarningOverridesKnownExternalPowerMode(
        PowerSource source, PerformancePreset performance, bool hyperBoost)
    {
        Assert.Equal(TrayIconState.ExternalPowerDischarging,
            TrayIconStateResolver.Resolve(true, true, source, performance, hyperBoost, true));
        Assert.NotEqual(TrayIconState.ExternalPowerDischarging,
            TrayIconStateResolver.Resolve(true, true, source, performance, hyperBoost, false));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.Custom)]
    public void ConfirmedHyperBoostOverridesItsUnderlyingAcMode(PerformancePreset performance)
    {
        Assert.Equal(TrayIconState.AcHyperBoost,
            TrayIconStateResolver.Resolve(true, true, PowerSource.PluggedIn, performance, true));
        Assert.NotEqual(TrayIconState.AcHyperBoost,
            TrayIconStateResolver.Resolve(true, true, PowerSource.PluggedIn, performance, false));
    }

    [Theory]
    [InlineData(false, true, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, false, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.ExternalUnknown, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.BatteryBalanced)]
    [InlineData(true, true, PowerSource.PluggedIn, PerformancePreset.BatterySaver)]
    [InlineData(true, true, PowerSource.PluggedIn, null)]
    public void HyperBoostDoesNotMaskUnavailableOrInconsistentState(
        bool connected, bool verified, PowerSource source, PerformancePreset? performance)
    {
        Assert.Equal(TrayIconState.GenericError,
            TrayIconStateResolver.Resolve(connected, verified, source, performance, true));
    }

    public static TheoryData<PowerSource, PerformancePreset, TrayIconState> EffectiveStates =>
        new()
        {
            { PowerSource.PluggedIn, PerformancePreset.Balanced, TrayIconState.AcBalanced },
            { PowerSource.PluggedIn, PerformancePreset.Silent, TrayIconState.AcSilent },
            { PowerSource.PluggedIn, PerformancePreset.Performance, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Custom, TrayIconState.AcCustom },
            { PowerSource.UsbC, PerformancePreset.Balanced, TrayIconState.UsbCBalanced },
            { PowerSource.Battery, PerformancePreset.Balanced, TrayIconState.BatteryBalanced },
            { PowerSource.Battery, PerformancePreset.BatteryBalanced, TrayIconState.BatteryBalanced },
            { PowerSource.Battery, PerformancePreset.BatterySaver, TrayIconState.BatterySaver },
        };

    [Theory]
    [MemberData(nameof(EffectiveStates))]
    public void EffectiveStateSelectsMatchingTrayIcon(
        PowerSource source,
        PerformancePreset performance,
        TrayIconState expected)
    {
        TrayIconState actual = TrayIconStateResolver.Resolve(
            isConnected: true,
            deviceVerified: true,
            source,
            performance);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, true, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, false, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.ExternalUnknown, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.PluggedIn, null)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.Performance)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Performance)]
    public void UnavailableOrInconsistentStateUsesGenericError(
        bool isConnected,
        bool deviceVerified,
        PowerSource source,
        PerformancePreset? performance)
    {
        TrayIconState actual = TrayIconStateResolver.Resolve(
            isConnected,
            deviceVerified,
            source,
            performance);

        Assert.Equal(TrayIconState.GenericError, actual);
        Assert.Equal(TrayIconState.GenericError,
            TrayIconStateResolver.Resolve(isConnected, deviceVerified, source, performance,
                externalPowerDischarging: true));
    }
}
