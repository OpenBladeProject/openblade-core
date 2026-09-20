using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class DeviceObservationStateTests
{
    [Theory]
    [InlineData("Checking laptop connection.")]
    [InlineData("Refreshing laptop controls after a temporary device-access failure.")]
    [InlineData("The last confirmed power source is retained while checking power.")]
    public void Refresh_RetainsPriorCapabilitiesWhenIncomingCapabilitiesAreMissing(string message)
    {
        var observation = new DeviceObservationState
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults(),
        };
        ServiceStatusSnapshot status = CreateStatus() with { Message = message };

        Assert.Same(observation.Capabilities, observation.ResolveCapabilities(status, true));
        Assert.Same(
            observation.Capabilities,
            observation.ResolveCapabilities(status with { Capabilities = null }, true));
        Assert.Same(status.Capabilities, observation.ResolveCapabilities(status, false));
    }

    [Theory]
    [InlineData(true, true, PowerSource.PluggedIn, true)]
    [InlineData(true, true, PowerSource.UsbC, true)]
    [InlineData(true, true, PowerSource.Battery, false)]
    [InlineData(false, true, PowerSource.PluggedIn, false)]
    [InlineData(true, false, PowerSource.PluggedIn, false)]
    public void PowerRecheck_RequiresExistingVerificationBlockAndRetainedSource(
        bool verified,
        bool blocked,
        PowerSource source,
        bool retain)
    {
        var observation = new DeviceObservationState
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults(),
        };
        ServiceStatusSnapshot status = CreateStatus() with
        {
            DeviceVerified = verified,
            WritesBlocked = blocked,
            ActivePowerSource = source,
            Message = "The last confirmed power source is retained.",
        };

        Assert.Same(
            retain ? observation.Capabilities : status.Capabilities,
            observation.ResolveCapabilities(status, true));
    }

    [Fact]
    public void Refresh_UsesNewCapabilitiesWithoutMergingDifferentModelFeatures()
    {
        var observation = new DeviceObservationState
        {
            Capabilities = OneShotCapabilities() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ObservedOnly,
            },
        };
        DeviceCapabilities incoming = DeviceCapabilities.Unavailable() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.FunctionKeyPrimary,
            },
        };
        ServiceStatusSnapshot status = CreateStatus() with
        {
            Message = "Checking laptop connection.",
            Capabilities = incoming,
        };

        DeviceCapabilities resolved = observation.ResolveCapabilities(status, true);

        Assert.Same(incoming, resolved);
        Assert.Null(resolved.HyperBoostAccess);
        Assert.DoesNotContain(OneShotLaptopControl.GamingMode, resolved.OneShotLaptopControls);
    }

    [Fact]
    public void Refresh_RetainsObservedOnlyCapabilities()
    {
        DeviceCapabilities observed = DeviceCapabilities.Unavailable() with
        {
            HyperBoostWithoutCoolingPadAccess = DeviceFeatureAccess.ObservedOnly,
        };
        var observation = new DeviceObservationState { Capabilities = observed };
        ServiceStatusSnapshot status = CreateStatus() with { Message = "Checking laptop connection." };

        Assert.Same(observed, observation.ResolveCapabilities(status, true));
    }

    [Fact]
    public void UnknownReadbacks_RetainValuesAndMarkEachSupportedControlPending()
    {
        var observation = new DeviceObservationState
        {
            Capabilities = OneShotCapabilities(),
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled = false,
        };

        DeviceObservationState pending = observation.ObserveOneShotReadbacks(CreateStatus());

        Assert.True(pending.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, pending.FunctionKeyPrimary);
        Assert.False(pending.GamingModeEnabled);
        Assert.True(pending.StartupAnimationReadPending);
        Assert.True(pending.FunctionKeyReadPending);
        Assert.True(pending.GamingModeReadPending);
        Assert.False(observation.StartupAnimationReadPending);
    }

    [Fact]
    public void FirstUnknownReadbacks_StayUnknownUntilConfirmed()
    {
        var observation = new DeviceObservationState { Capabilities = OneShotCapabilities() };
        DeviceObservationState pending = observation.ObserveOneShotReadbacks(CreateStatus());

        Assert.Null(pending.StartupAnimationEnabled);
        Assert.Null(pending.FunctionKeyPrimary);
        Assert.Null(pending.GamingModeEnabled);
        Assert.True(pending.StartupAnimationReadPending);
        Assert.True(pending.FunctionKeyReadPending);
        Assert.True(pending.GamingModeReadPending);

        DeviceObservationState confirmed = pending.ObserveOneShotReadbacks(CreateStatus() with
        {
            StartupAnimationEnabled = false,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = true,
        });

        Assert.False(confirmed.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, confirmed.FunctionKeyPrimary);
        Assert.True(confirmed.GamingModeEnabled);
        Assert.False(confirmed.StartupAnimationReadPending);
        Assert.False(confirmed.FunctionKeyReadPending);
        Assert.False(confirmed.GamingModeReadPending);
    }

    [Fact]
    public void Disconnect_ClearsCapabilitiesRetainedValuesAndPendingFlags()
    {
        var observation = new DeviceObservationState
        {
            Capabilities = OneShotCapabilities(),
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled = true,
            StartupAnimationReadPending = true,
            FunctionKeyReadPending = true,
            GamingModeReadPending = true,
        };
        ServiceStatusSnapshot disconnected = CreateStatus() with
        {
            DeviceVerified = false,
            Message = "No supported laptop connected.",
        };

        observation = observation with
        {
            Capabilities = observation.ResolveCapabilities(disconnected, true),
        };
        observation = observation.ObserveOneShotReadbacks(disconnected);

        Assert.Same(disconnected.Capabilities, observation.Capabilities);
        Assert.Null(observation.StartupAnimationEnabled);
        Assert.Null(observation.FunctionKeyPrimary);
        Assert.Null(observation.GamingModeEnabled);
        Assert.False(observation.StartupAnimationReadPending);
        Assert.False(observation.FunctionKeyReadPending);
        Assert.False(observation.GamingModeReadPending);
    }

    [Fact]
    public void UnsupportedControls_DoNotBecomePendingOrRetainMissingValues()
    {
        var observation = new DeviceObservationState
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary,
                },
            },
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled = true,
        };

        DeviceObservationState next = observation.ObserveOneShotReadbacks(CreateStatus());

        Assert.Null(next.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, next.FunctionKeyPrimary);
        Assert.Null(next.GamingModeEnabled);
        Assert.False(next.StartupAnimationReadPending);
        Assert.True(next.FunctionKeyReadPending);
        Assert.False(next.GamingModeReadPending);
    }

    private static DeviceCapabilities OneShotCapabilities() => DeviceCapabilities.Unavailable() with
    {
        OneShotLaptopControls = new HashSet<OneShotLaptopControl>(
            Enum.GetValues<OneShotLaptopControl>()),
    };

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1,
            true,
            true,
            [],
            null,
            [],
            "Ready",
            DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            false);
    }
}
