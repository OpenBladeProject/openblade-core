using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class CustomPerformanceContractTests
{
    [Theory]
    [InlineData(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low)]
    [InlineData(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High)]
    [InlineData(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium)]
    public void Settings_AcceptsCapturedValidPairs(
        CustomPerformanceLevel cpu,
        CustomPerformanceLevel gpu)
    {
        var settings = new CustomPerformanceSettings(cpu, gpu);

        Assert.Equal(cpu, settings.Cpu);
        Assert.Equal(gpu, settings.Gpu);
        Assert.True(CustomPerformanceSettings.IsWellFormed(settings));
    }

    [Fact]
    public void Settings_RejectsCapturedInvalidHighHighPair()
    {
        Assert.Throws<ArgumentException>(() => new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.High));
    }

    [Fact]
    public void PerformanceOverride_RequiresLevelsOnlyForCustom()
    {
        Assert.True(new PerformanceOverride(
            PerformancePreset.Custom,
            OverrideLifetime.Persistent,
            CustomPerformanceSettings.Default).IsWellFormed());
        Assert.False(new PerformanceOverride(
            PerformancePreset.Custom,
            OverrideLifetime.Persistent).IsWellFormed());
        Assert.False(new PerformanceOverride(
            PerformancePreset.Balanced,
            OverrideLifetime.Persistent,
            CustomPerformanceSettings.Default).IsWellFormed());
    }
}
