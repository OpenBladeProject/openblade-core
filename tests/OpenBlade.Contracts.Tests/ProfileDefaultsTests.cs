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
        Assert.True(profiles.PluggedIn.BatteryProtectionEnabled);
        Assert.True(profiles.Battery.BatteryProtectionEnabled);
        Assert.True(profiles.UsbC.BatteryProtectionEnabled);
        Assert.Null(profiles.PluggedIn.WindowsPowerMode);
        Assert.Null(profiles.Battery.WindowsPowerMode);
        Assert.Null(profiles.UsbC.WindowsPowerMode);
        Assert.All(
            Enum.GetValues<PerformancePreset>(),
            performance => Assert.Null(
                profiles.WindowsPowerModes.ForPerformance(performance)));
        Assert.Equal(80, profiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public void NewWindowsPowerModeAssociations_AreIndependentAndRetainTheLegacyConstructor()
    {
        var legacy = new WindowsPowerModeAssociations(null, WindowsPowerMode.Balanced,
            WindowsPowerMode.BestPerformance, null, null, null);
        Assert.Null(legacy.Turbo);
        Assert.Null(legacy.MaximumPower);
        WindowsPowerModeAssociations configured = legacy
            .WithPerformance(PerformancePreset.Turbo, WindowsPowerMode.BestPowerEfficiency)
            .WithPerformance(PerformancePreset.MaximumPower, WindowsPowerMode.Balanced);
        Assert.Equal(WindowsPowerMode.BestPerformance, configured.Performance);
        Assert.Equal(WindowsPowerMode.BestPowerEfficiency, configured.ForPerformance(PerformancePreset.Turbo));
        Assert.Equal(WindowsPowerMode.Balanced, configured.ForPerformance(PerformancePreset.MaximumPower));
        WindowsPowerModeAssociations updated = configured.WithPerformance(
            PerformancePreset.Turbo, WindowsPowerMode.BestPerformance);
        Assert.Equal(WindowsPowerMode.BestPerformance, updated.Turbo);
        Assert.Equal(configured.MaximumPower, updated.MaximumPower);
        Assert.Equal(configured.Performance, updated.Performance);
        Assert.Equal(configured.Balanced, updated.Balanced);
        string json = System.Text.Json.JsonSerializer.Serialize(configured);
        Assert.Equal(configured, System.Text.Json.JsonSerializer.Deserialize<WindowsPowerModeAssociations>(json));
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
    [InlineData(100)]
    [InlineData(101)]
    public void BatteryPolicy_RejectsUnsafeChargeLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BatteryPolicy.Create(limit));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(65)]
    [InlineData(80)]
    [InlineData(99)]
    public void BatteryPolicy_AcceptsWholePercentEnabledLimits(int limit)
    {
        BatteryPolicy policy = BatteryPolicy.Create(limit);

        Assert.Equal(limit, policy.ChargeLimitPercent);
    }
}
