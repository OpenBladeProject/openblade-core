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
        Assert.Equal("Custom", PerformancePresetPresentation.Label(PerformancePreset.Custom));
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
        IReadOnlyList<PerformancePresetOption> usbC =
            PerformancePresetPresentation.CreateOptions(PowerSource.UsbC, admitted);

        Assert.Equal(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            ],
            pluggedIn.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Silent", "Performance", "Custom"],
            pluggedIn.Select(option => option.Label));
        Assert.Equal(
            [PerformancePreset.BatteryBalanced, PerformancePreset.BatterySaver],
            battery.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Battery Saver"],
            battery.Select(option => option.Label));
        Assert.Equal(
            [PerformancePreset.Balanced],
            usbC.Select(option => option.Value));
        Assert.Empty(PerformancePresetPresentation.CreateOptions(
            PowerSource.ExternalUnknown,
            admitted));
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
        Assert.Empty(PerformancePresetPresentation.CreateOptions(PowerSource.UsbC, admitted));
    }

    [Fact]
    public void CreateLaptopOptions_AppendsTransientHyperBoostWithoutChangingPresets()
    {
        IReadOnlyList<PerformancePresetOption> presets =
        [
            new(PerformancePreset.Balanced, "Balanced"),
            new(PerformancePreset.Performance, "Performance"),
        ];

        IReadOnlyList<LaptopPerformanceOption> withoutHyperBoost =
            PerformancePresetPresentation.CreateLaptopOptions(
                presets,
                includeHyperBoost: false);
        IReadOnlyList<LaptopPerformanceOption> withHyperBoost =
            PerformancePresetPresentation.CreateLaptopOptions(
                presets,
                includeHyperBoost: true);

        Assert.Equal(
            [PerformancePreset.Balanced, PerformancePreset.Performance],
            withoutHyperBoost.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Performance", "HyperBoost"],
            withHyperBoost.Select(option => option.Label));
        Assert.True(withHyperBoost[^1].IsHyperBoost);
        Assert.Null(withHyperBoost[^1].Value);
        Assert.Equal(2, presets.Count);
    }

    [Fact]
    public void IsAvailable_UsesExplicitPowerSourceRules()
    {
        Assert.True(PerformancePresetPresentation.IsAvailable(
            PowerSource.UsbC,
            PerformancePreset.Balanced));
        Assert.False(PerformancePresetPresentation.IsAvailable(
            PowerSource.UsbC,
            PerformancePreset.Silent));
        Assert.False(PerformancePresetPresentation.IsAvailable(
            PowerSource.ExternalUnknown,
            PerformancePreset.Balanced));
    }
}
