using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconStateResolverTests
{
    [Theory]
    [InlineData(PerformancePreset.Balanced, TrayIconState.AcBalanced)]
    [InlineData(PerformancePreset.Silent, TrayIconState.AcSilent)]
    [InlineData(PerformancePreset.Performance, TrayIconState.AcPerformance)]
    [InlineData(PerformancePreset.Custom, TrayIconState.AcCustom)]
    public void PositiveAcPresenceRemovesOnlyTheUnclassifiedSourceError(
        PerformancePreset performance, TrayIconState expected)
    {
        Assert.Equal(expected, TrayIconStateResolver.Resolve(true, true,
            PowerSource.ExternalUnknown, performance, externalPowerPresent: true));
        Assert.Equal(TrayIconState.GenericError, TrayIconStateResolver.Resolve(true, true,
            PowerSource.ExternalUnknown, performance));
        Assert.Equal(TrayIconState.GenericError, TrayIconStateResolver.Resolve(false, true,
            PowerSource.ExternalUnknown, performance, externalPowerPresent: true));
        Assert.Equal(TrayIconState.GenericError, TrayIconStateResolver.Resolve(true, false,
            PowerSource.ExternalUnknown, performance, externalPowerPresent: true));
        Assert.Equal("External power (unclassified)", PowerSourcePresentation.Label(PowerSource.ExternalUnknown));
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Custom, false)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance, true)]
    [InlineData(PowerSource.UsbC, PerformancePreset.Balanced, false)]
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
            { PowerSource.PluggedIn, PerformancePreset.Turbo, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.MaximumPower, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Gaming, TrayIconState.AcPerformance },
            { PowerSource.PluggedIn, PerformancePreset.Creator, TrayIconState.AcPerformance },
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
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Turbo)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.MaximumPower)]
    [InlineData(true, true, PowerSource.Battery, PerformancePreset.Gaming)]
    [InlineData(true, true, PowerSource.UsbC, PerformancePreset.Creator)]
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
