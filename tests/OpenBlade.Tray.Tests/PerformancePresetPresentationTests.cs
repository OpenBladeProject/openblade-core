using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PerformancePresetPresentationTests
{
    [Fact]
    public void Labels_PreserveDistinctFirmwareModes()
    {
        Assert.Equal("Balanced", PerformancePresetPresentation.Label(PerformancePreset.Balanced));
        Assert.Equal("Silent", PerformancePresetPresentation.Label(PerformancePreset.Silent));
        Assert.Equal("Performance", PerformancePresetPresentation.Label(PerformancePreset.Performance));
        Assert.Equal("Balanced", PerformancePresetPresentation.Label(PerformancePreset.BatteryBalanced));
        Assert.Equal("Battery Saver", PerformancePresetPresentation.Label(PerformancePreset.BatterySaver));
    }

    [Fact]
    public void CreateOptions_FiltersAndOrdersModesByPowerSource()
    {
        var admitted = new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>());

        IReadOnlyList<PerformancePresetOption> pluggedIn =
            PerformancePresetPresentation.CreateOptions(PowerSource.PluggedIn, admitted);
        IReadOnlyList<PerformancePresetOption> battery =
            PerformancePresetPresentation.CreateOptions(PowerSource.Battery, admitted);

        Assert.Equal(
            [PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance],
            pluggedIn.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Silent", "Performance"],
            pluggedIn.Select(option => option.Label));
        Assert.Equal(
            [PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver],
            battery.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Battery Saver"],
            battery.Select(option => option.Label));
    }

    [Fact]
    public void CreateOptions_IntersectsContextWithAdmittedCapabilities()
    {
        var admitted = new HashSet<PerformancePreset>
        {
            PerformancePreset.Performance,
            PerformancePreset.BatteryBalanced,
            PerformancePreset.BatterySaver,
        };

        Assert.Equal(
            [PerformancePreset.Performance],
            PerformancePresetPresentation.CreateOptions(PowerSource.PluggedIn, admitted)
                .Select(option => option.Value));
        Assert.Equal(
            [PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver],
            PerformancePresetPresentation.CreateOptions(PowerSource.Battery, admitted)
                .Select(option => option.Value));
    }
}
