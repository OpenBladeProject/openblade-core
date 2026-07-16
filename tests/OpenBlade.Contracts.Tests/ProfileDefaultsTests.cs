using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ProfileDefaultsTests
{
    [Fact]
    public void CreateSafeDefaults_UsesBalancedOnAcAndSilentOnBattery()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        Assert.Equal(PerformancePreset.Balanced, profiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Automatic, profiles.PluggedIn.Fans);
        Assert.Equal(PerformancePreset.Silent, profiles.Battery.Performance);
        Assert.Equal(FanMode.Automatic, profiles.Battery.Fans);
        Assert.Equal(LightingEffect.Off, profiles.PluggedIn.Lighting.Effect);
        Assert.Equal(LightingEffect.Off, profiles.Battery.Lighting.Effect);
        Assert.Equal(80, profiles.BatteryPolicy.ChargeLimitPercent);
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
