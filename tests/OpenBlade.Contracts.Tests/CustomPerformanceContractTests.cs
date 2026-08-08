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
    public void Settings_AcceptsOptionalGpuClockOffsets()
    {
        var offsets = new GpuClockOffsetSettings(125, 500);
        var settings = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = offsets,
        };

        Assert.True(CustomPerformanceSettings.IsWellFormed(settings));
        Assert.Equal(offsets, settings.GpuClockOffsets);
    }

    [Theory]
    [InlineData(-10001, 0)]
    [InlineData(10001, 0)]
    [InlineData(0, -10001)]
    [InlineData(0, 10001)]
    public void GpuClockOffsets_RejectValuesOutsideContractBounds(
        int coreMegahertz,
        int memoryMegahertz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GpuClockOffsetSettings(coreMegahertz, memoryMegahertz));
    }

    [Fact]
    public void GpuClockOffsetCapabilities_ValidateAndEnforceDriverRanges()
    {
        var capabilities = new GpuClockOffsetCapabilities(
            "NVIDIA GPU",
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-2000, 6000, 0));

        Assert.True(capabilities.IsWellFormed);
        Assert.True(capabilities.Supports(new GpuClockOffsetSettings(1000, 6000)));
        Assert.False(capabilities.Supports(new GpuClockOffsetSettings(1001, 0)));
    }

    [Fact]
    public void ExactDeviceCombinations_CanNarrowGenericValidPairs()
    {
        var capabilities = new CustomPerformanceCapabilities(
            new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
                new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
            });

        Assert.True(capabilities.IsWellFormed);
        Assert.True(capabilities.Supports(new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Low)));
        Assert.False(capabilities.Supports(new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)));
    }

    [Fact]
    public void DiscoveredNumericRange_DoesNotClaimWritesWithoutValidatedStep()
    {
        var observedRange = new DeviceNumericRange(-1000, 400, null);
        var writableRange = new DeviceNumericRange(45, 95, 1);

        Assert.True(observedRange.IsWellFormed);
        Assert.False(observedRange.Supports(0));
        Assert.True(writableRange.Supports(61));
        Assert.False(writableRange.Supports(96));
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
        Assert.True(new PerformanceOverride(
            PerformancePreset.Balanced,
            OverrideLifetime.Persistent)
        {
            GpuClockTuning = GpuClockTuningSettings.Disabled,
        }.IsWellFormed());
        Assert.False(new PerformanceOverride(
            PerformancePreset.Custom,
            OverrideLifetime.Persistent,
            CustomPerformanceSettings.Default with
            {
                GpuClockOffsets = GpuClockOffsetSettings.Default,
            })
        {
            GpuClockTuning = GpuClockTuningSettings.Disabled,
        }.IsWellFormed());
    }
}
