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
        Assert.Equal("Turbo", PerformancePresetPresentation.Label(PerformancePreset.Turbo));
        Assert.Equal("Maximum Power", PerformancePresetPresentation.Label(PerformancePreset.MaximumPower));
        Assert.Equal("Gaming", PerformancePresetPresentation.Label(PerformancePreset.Gaming));
        Assert.Equal("Creator", PerformancePresetPresentation.Label(PerformancePreset.Creator));
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
                PerformancePreset.Turbo,
                PerformancePreset.MaximumPower,
                PerformancePreset.Gaming,
                PerformancePreset.Creator,
                PerformancePreset.Custom,
            ],
            pluggedIn.Select(option => option.Value));
        Assert.Equal(
            ["Balanced", "Silent", "Performance", "Turbo", "Maximum Power", "Gaming", "Creator", "Custom"],
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

    [Theory]
    [InlineData(PerformancePreset.Turbo)]
    [InlineData(PerformancePreset.MaximumPower)]
    [InlineData(PerformancePreset.Gaming)]
    [InlineData(PerformancePreset.Creator)]
    public void NewHighPerformancePresets_RequireExplicitCapabilitiesAndFullPower(PerformancePreset preset)
    {
        var legacy = new HashSet<PerformancePreset>
        {
            PerformancePreset.Balanced, PerformancePreset.Performance, PerformancePreset.Custom,
        };
        Assert.DoesNotContain(PerformancePresetPresentation.CreateOptions(PowerSource.PluggedIn, legacy),
            option => option.Value == preset);
        Assert.Single(PerformancePresetPresentation.CreateOptions(PowerSource.PluggedIn,
            new HashSet<PerformancePreset> { preset }));
        Assert.True(PerformancePresetPresentation.IsAvailable(PowerSource.PluggedIn, preset));
        foreach (PowerSource source in new[] { PowerSource.Battery, PowerSource.UsbC, PowerSource.ExternalUnknown })
        {
            Assert.False(PerformancePresetPresentation.IsAvailable(source, preset));
            Assert.Empty(PerformancePresetPresentation.CreateOptions(source,
                new HashSet<PerformancePreset> { preset }));
        }
    }

    [Theory]
    [InlineData("ru-RU", "Турбо", "Максимальная мощность", "Игровой", "Творческий")]
    [InlineData("zh-CN", "极速", "最大功率", "游戏", "创作")]
    public void NewHighPerformanceLabels_AreLocalized(string language, string turbo, string maximumPower, string gaming, string creator)
    {
        using var culture = new CultureScope(language);
        Assert.Equal(turbo, PerformancePresetPresentation.Label(PerformancePreset.Turbo));
        Assert.Equal(maximumPower, PerformancePresetPresentation.Label(PerformancePreset.MaximumPower));
        Assert.Equal(gaming, PerformancePresetPresentation.Label(PerformancePreset.Gaming));
        Assert.Equal(creator, PerformancePresetPresentation.Label(PerformancePreset.Creator));
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
