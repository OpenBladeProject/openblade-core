using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ProfileDefaultsTests
{
    [Fact]
    public void CreateSafeDefaults_UsesSafeProfilesForEveryPowerSource()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        Assert.Equal(PerformancePreset.Balanced, profiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Automatic, profiles.PluggedIn.Fans);
        Assert.Equal(PerformancePreset.BatterySaver, profiles.Battery.Performance);
        Assert.Equal(FanMode.Automatic, profiles.Battery.Fans);
        Assert.Equal(PerformancePreset.Balanced, profiles.UsbC.Performance);
        Assert.Equal(FanMode.Automatic, profiles.UsbC.Fans);
        Assert.Equal(LightingEffect.Off, profiles.PluggedIn.Lighting.Effect);
        Assert.Equal(LightingEffect.Off, profiles.Battery.Lighting.Effect);
        Assert.Equal(LightingEffect.Off, profiles.UsbC.Lighting.Effect);
        Assert.False(profiles.PluggedIn.Display!.IsConfigured);
        Assert.False(profiles.Battery.Display!.IsConfigured);
        Assert.False(profiles.UsbC.Display!.IsConfigured);
        Assert.Equal(80, profiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public void PowerSource_AppendsUsbCStatesWithoutRenumberingExistingValues()
    {
        Assert.Equal(0, (int)PowerSource.PluggedIn);
        Assert.Equal(1, (int)PowerSource.Battery);
        Assert.Equal(2, (int)PowerSource.UsbC);
        Assert.Equal(3, (int)PowerSource.ExternalUnknown);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(52)]
    [InlineData(85)]
    public void BatteryPolicy_RejectsUnsafeChargeLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BatteryPolicy.Create(limit));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(65)]
    [InlineData(80)]
    public void BatteryPolicy_AcceptsVendorRangeInFivePercentSteps(int limit)
    {
        BatteryPolicy policy = BatteryPolicy.Create(limit);

        Assert.Equal(limit, policy.ChargeLimitPercent);
    }
}
