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
    public void PluggedInCycle_NewModesRequireExplicitAdmissionAndRemainDistinct()
    {
        var admitted = new HashSet<PerformancePreset>
        {
            PerformancePreset.MaximumPower, PerformancePreset.Turbo, PerformancePreset.Balanced,
        };
        Assert.Equal(new[] { PerformancePreset.Balanced, PerformancePreset.Turbo, PerformancePreset.MaximumPower },
            PerformanceShortcutSelection.GetCycle(PowerSource.PluggedIn, admitted));
        Assert.True(PerformanceShortcutSelection.TrySelectNext(PowerSource.PluggedIn, admitted,
            PerformancePreset.Turbo, out PerformancePreset next));
        Assert.Equal(PerformancePreset.MaximumPower, next);
        Assert.True(PerformanceShortcutSelection.TrySelectNext(PowerSource.PluggedIn, admitted,
            PerformancePreset.MaximumPower, out next));
        Assert.Equal(PerformancePreset.Balanced, next);
        Assert.Empty(PerformanceShortcutSelection.GetCycle(PowerSource.Battery, admitted));
    }

    [Fact]
    public void GamingCreatorCycle_UsesExactAdmittedBaseModes()
    {
        var admitted = new HashSet<PerformancePreset>
        {
            PerformancePreset.Creator, PerformancePreset.Balanced, PerformancePreset.Gaming,
        };
        Assert.Equal(new[] { PerformancePreset.Balanced, PerformancePreset.Gaming, PerformancePreset.Creator },
            PerformanceShortcutSelection.GetCycle(PowerSource.PluggedIn, admitted));
        Assert.True(PerformanceShortcutSelection.TrySelectNext(PowerSource.PluggedIn, admitted,
            PerformancePreset.Gaming, out PerformancePreset next));
        Assert.Equal(PerformancePreset.Creator, next);
        Assert.True(PerformanceShortcutSelection.TrySelectNext(PowerSource.PluggedIn, admitted,
            PerformancePreset.Creator, out next));
        Assert.Equal(PerformancePreset.Balanced, next);
        Assert.Empty(PerformanceShortcutSelection.GetCycle(PowerSource.Battery, admitted));
        Assert.Equal(PerformanceShortcutSelection.GetCycle(PowerSource.PluggedIn, admitted),
            PerformanceShortcutSelection.GetCycle(PowerSource.UsbC, admitted));
    }

    [Fact]
    public void MissingCapabilities_RetainEstablishedCycleWithoutNewModes()
    {
        PerformancePreset[] expected =
        [
            PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance, PerformancePreset.Custom,
        ];
        Assert.Equal(expected, PerformanceShortcutSelection.GetCycle(PowerSource.PluggedIn, null));
        Assert.Equal(expected, PerformanceShortcutSelection.GetCycle(PowerSource.UsbC, null));
        Assert.Equal(expected, PerformanceShortcutSelection.GetCycle(PowerSource.PluggedIn, new HashSet<PerformancePreset>()));
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
    [InlineData(PowerSource.ExternalUnknown)]
    [InlineData((PowerSource)999)]
    public void UnsupportedPowerSource_DoesNotPredictASelection(PowerSource source)
    {
        Assert.False(PerformanceShortcutSelection.TrySelectNext(
            source,
            admitted: null,
            PerformancePreset.Balanced,
            out _));
    }
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public void EveryAdmittedPresetCyclesOnExternalPower(PowerSource source)
    {
        var admitted = new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>())
        {
            (PerformancePreset)999,
        };
        IReadOnlyList<PerformancePreset> cycle = PerformanceShortcutSelection.GetCycle(source, admitted);
        Assert.Equal(Enum.GetValues<PerformancePreset>().Order(), cycle.Order());
        for (int index = 0; index < cycle.Count; index++)
        {
            Assert.True(PerformanceShortcutSelection.TrySelectNext(source, admitted, cycle[index], out var next));
            Assert.Equal(cycle[(index + 1) % cycle.Count], next);
        }
    }

}
