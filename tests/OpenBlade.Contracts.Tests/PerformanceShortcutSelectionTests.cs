using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class PerformanceShortcutSelectionTests
{
    [Fact]
    public void PluggedInCycle_UsesAdmittedModesInStableOrder()
    {
        var admitted = new HashSet<PerformancePreset>
        {
            PerformancePreset.Balanced,
            PerformancePreset.Performance,
        };

        Assert.True(PerformanceShortcutSelection.TrySelectNext(
            PowerSource.PluggedIn,
            admitted,
            PerformancePreset.Balanced,
            out PerformancePreset next));

        Assert.Equal(PerformancePreset.Performance, next);
    }

    [Fact]
    public void BatteryCycle_UsesBatterySpecificModes()
    {
        Assert.True(PerformanceShortcutSelection.TrySelectNext(
            PowerSource.Battery,
            admitted: null,
            PerformancePreset.BatteryBalanced,
            out PerformancePreset next));

        Assert.Equal(PerformancePreset.BatterySaver, next);
    }

    [Theory]
    [InlineData(PowerSource.UsbC)]
    [InlineData(PowerSource.ExternalUnknown)]
    public void UnsupportedPowerSource_DoesNotPredictASelection(PowerSource source)
    {
        Assert.False(PerformanceShortcutSelection.TrySelectNext(
            source,
            admitted: null,
            PerformancePreset.Balanced,
            out _));
    }
}
