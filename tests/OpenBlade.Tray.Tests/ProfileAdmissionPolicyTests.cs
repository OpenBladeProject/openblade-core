using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ProfileAdmissionPolicyTests
{
    [Fact]
    public void RequiresConfirmedCapabilities()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        Assert.False(ProfileAdmissionPolicy.Supports(profiles, profiles, null));
        Assert.True(ProfileAdmissionPolicy.Supports(
            profiles, profiles, SupportedCapabilities()));
    }

    [Fact]
    public void PreservesUnsupportedUnchangedLightingButRejectsNewLighting()
    {
        ProfileSet current = ProfileDefaults.CreateSafeDefaults();
        DeviceCapabilities capabilities = SupportedCapabilities() with
        {
            LightingEffects = new HashSet<LightingEffect>(),
        };
        Assert.True(ProfileAdmissionPolicy.Supports(current, current, capabilities));
        ProfileSet draft = current with
        {
            PluggedIn = current.PluggedIn with
            {
                Lighting = LightingSettingsFactory.Static(new RgbColor(12, 34, 56), 73),
            },
        };
        Assert.False(ProfileAdmissionPolicy.Supports(draft, current, capabilities));
    }

    [Fact]
    public void PreservesBatteryThermalSettingsButNeverBypassesUsbCRestriction()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        PowerProfile retained = profiles.Battery with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Maximum,
        };
        ProfileSet battery = profiles with { Battery = retained };
        Assert.True(ProfileAdmissionPolicy.Supports(
            battery, battery, SupportedCapabilities()));
        ProfileSet usbC = profiles with { UsbC = retained };
        Assert.False(ProfileAdmissionPolicy.Supports(
            usbC, usbC, SupportedCapabilities()));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, FanMode.Fixed)]
    [InlineData(PerformancePreset.Silent, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Silent, FanMode.Maximum)]
    [InlineData(PerformancePreset.Balanced, FanMode.Fixed)]
    [InlineData(PerformancePreset.Balanced, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Balanced, FanMode.Maximum)]
    [InlineData(PerformancePreset.Performance, FanMode.Fixed)]
    [InlineData(PerformancePreset.Performance, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Performance, FanMode.Maximum)]
    [InlineData(PerformancePreset.Custom, FanMode.Fixed)]
    [InlineData(PerformancePreset.Custom, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Custom, FanMode.Maximum)]
    public void DirectManualFanTransitionsAllowEveryAcPerformanceIntent(
        PerformancePreset performance,
        FanMode fans)
    {
        ProfileSet current = ProfileDefaults.CreateSafeDefaults();
        ProfileSet draft = current with
        {
            PluggedIn = current.PluggedIn with
            {
                Performance = performance,
                Fans = fans,
                CustomPerformance = CustomPerformanceSettings.Default,
            },
        };
        DeviceCapabilities capabilities = SupportedCapabilities() with
        {
            SupportsDirectManualFanTransitions = true,
        };

        Assert.True(ProfileAdmissionPolicy.Supports(draft, current, capabilities));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.Custom)]
    public void LegacyCapabilitiesKeepBalancedEntryForManualFans(
        PerformancePreset performance)
    {
        ProfileSet current = ProfileDefaults.CreateSafeDefaults();
        ProfileSet draft = current with
        {
            PluggedIn = current.PluggedIn with
            {
                Performance = performance,
                Fans = FanMode.Maximum,
                CustomPerformance = CustomPerformanceSettings.Default,
            },
        };

        Assert.False(ProfileAdmissionPolicy.Supports(
            draft,
            current,
            SupportedCapabilities()));
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public void DirectManualFanTransitionsRemainAcOnly(PowerSource powerSource)
    {
        ProfileSet current = ProfileDefaults.CreateSafeDefaults();
        PowerProfile manual = powerSource switch
        {
            PowerSource.Battery => current.Battery with { Fans = FanMode.ManualCurve },
            PowerSource.UsbC => current.UsbC with { Fans = FanMode.ManualCurve },
            _ => throw new ArgumentOutOfRangeException(nameof(powerSource)),
        };
        ProfileSet draft = powerSource switch
        {
            PowerSource.Battery => current with { Battery = manual },
            PowerSource.UsbC => current with { UsbC = manual },
            _ => throw new ArgumentOutOfRangeException(nameof(powerSource)),
        };
        DeviceCapabilities capabilities = SupportedCapabilities() with
        {
            SupportsDirectManualFanTransitions = true,
        };

        Assert.False(ProfileAdmissionPolicy.Supports(draft, current, capabilities));
    }

    [Fact]
    public void UnchangedInvalidEnumsAreStillRejected()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Fans = (FanMode)int.MaxValue },
        };
        Assert.False(ProfileAdmissionPolicy.Supports(
            profiles, profiles, SupportedCapabilities()));
    }

    [Fact]
    public void UnsupportedChargeLimitCannotBePreservedAsUnchanged()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DeviceCapabilities capabilities = SupportedCapabilities() with
        {
            ChargeLimits = new HashSet<int> { 50 },
        };
        Assert.False(ProfileAdmissionPolicy.Supports(profiles, profiles, capabilities));
    }

    private static DeviceCapabilities SupportedCapabilities() =>
        DeviceCapabilities.SafeTargetDefaults() with
        {
            ChargeLimits = new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
        };
}
