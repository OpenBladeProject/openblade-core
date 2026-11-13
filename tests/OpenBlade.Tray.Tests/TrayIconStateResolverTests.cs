using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconStateResolverTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public void EveryExternalPresetSupportsTheExistingDischargeBadge(PowerSource source)
    {
        foreach (PerformancePreset preset in Enum.GetValues<PerformancePreset>())
        {
            TrayIconState state = TrayIconStateResolver.Resolve(true, true, source, preset);
            Assert.NotEqual(TrayIconState.GenericError, state);
            Assert.True(TrayIconStateResolver.CanShowDischargeBadge(state));
            Assert.NotEqual(TrayIconAssetPath.Resolve(state, true),
                TrayIconAssetPath.Resolve(state, true, externalPowerDischarging: true));
        }
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Custom, false)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance, true)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Balanced, false)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Custom, false)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Custom, true)]
    public void ExternalPowerModesRemainTheBaseOfDischargeBadges(
        PowerSource source, PerformancePreset performance, bool hyperBoost)
    {
        TrayIconState state = TrayIconStateResolver.Resolve(true, true, source, performance, hyperBoost);
        Assert.True(TrayIconStateResolver.CanShowDischargeBadge(state));
        Assert.NotEqual(TrayIconAssetPath.Resolve(state, true), TrayIconAssetPath.Resolve(state, true, true));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.Custom)]
    [InlineData(PerformancePreset.Turbo)]
    [InlineData(PerformancePreset.MaximumPower)]
    [InlineData(PerformancePreset.Gaming)]
    [InlineData(PerformancePreset.Creator)]
    public void ConfirmedHyperBoostOverridesItsUnderlyingExternalMode(PerformancePreset performance)
    {
        Assert.Equal(TrayIconState.AcHyperBoost,
            TrayIconStateResolver.Resolve(true, true, PowerSource.PluggedIn, performance, true));
        Assert.Equal(TrayIconState.AcHyperBoost,
            TrayIconStateResolver.Resolve(true, true, PowerSource.UsbC, performance, true));
        Assert.NotEqual(TrayIconState.AcHyperBoost,
            TrayIconStateResolver.Resolve(true, true, PowerSource.PluggedIn, performance, false));
    }

    [Theory]
    [InlineData(false, true, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, false, PowerSource.PluggedIn, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.ExternalUnknown, PerformancePreset.Balanced)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.BatteryBalanced)]
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
            { PowerSource.PluggedIn, PerformancePreset.Turbo, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.MaximumPower, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Gaming, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Creator, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Custom, TrayIconState.AcCustom },
            { PowerSource.UsbC, PerformancePreset.Balanced, TrayIconState.UsbCBalanced },
            { PowerSource.UsbC, PerformancePreset.Silent, TrayIconState.AcSilent },
            { PowerSource.UsbC, PerformancePreset.Custom, TrayIconState.AcCustom },
            { PowerSource.UsbC, PerformancePreset.Performance, TrayIconState.AcPerformance },
            { PowerSource.UsbC, PerformancePreset.MaximumPower, TrayIconState.AcPerformance },
            { PowerSource.UsbC, PerformancePreset.Creator, TrayIconState.AcPerformance },
            { PowerSource.UsbC, PerformancePreset.BatteryBalanced, TrayIconState.AcBalanced },
            { PowerSource.PluggedIn, PerformancePreset.BatterySaver, TrayIconState.AcSilent },
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
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Performance)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Turbo)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Gaming)]
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
        Assert.False(TrayIconStateResolver.CanShowDischargeBadge(actual));
    }
}
