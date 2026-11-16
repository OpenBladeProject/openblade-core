using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ApplicationProfileThermalIntentEditorTests
{
    [Fact]
    public void UnchangedLegacyMaximumPreservesAliasDespiteCoercedDisplay()
    {
        PowerProfile basis = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        var current = new ApplicationPowerProfileOverride
        {
            Fans = FanMode.Maximum,
            FanControl = new(FanMode.Fixed, 5400, ManualFanCurve.Default),
            CustomPerformance = CustomPerformanceSettings.Default,
        };
        ApplicationPowerProfileOverride result = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, null, FanMode.Maximum, 5000, false, false, false);
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(result));
        Assert.Same(current.FanControl, result.FanControl);
    }

    [Fact]
    public void UnchangedCurvePreservesInactiveFixedSpeedAndPoints()
    {
        PowerProfile basis = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        var current = new ApplicationPowerProfileOverride
        {
            Fans = FanMode.ManualCurve,
            FanControl = new(FanMode.ManualCurve, 5500, ManualFanCurve.Default),
        };
        ApplicationPowerProfileOverride result = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, null, FanMode.ManualCurve, 5000, false, false, false);
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(result));
        Assert.Same(current.FanControl, result.FanControl);
    }

    [Fact]
    public void UnchangedInheritedFieldsRemainNull()
    {
        PowerProfile basis = ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = CustomPerformanceSettings.Default,
            Fans = FanMode.Fixed,
            FanControl = new(FanMode.Fixed, 5000, ManualFanCurve.Default),
        };
        var current = new ApplicationPowerProfileOverride();
        ApplicationPowerProfileOverride result = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, null, null, 5000, false, false, false);
        Assert.Equal(current, result);
        Assert.Null(result.Fans);
        Assert.Null(result.FanControl);
        Assert.Null(result.CustomPerformance);
    }

    [Fact]
    public void ExplicitPerformanceChangeSavesVisibleActiveFixedSpeed()
    {
        PowerProfile basis = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        var current = new ApplicationPowerProfileOverride
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Fixed,
            FanControl = new(FanMode.Fixed, 5500, ManualFanCurve.Default),
        };
        ApplicationPowerProfileOverride result = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, PerformancePreset.Silent, FanMode.Fixed, 3800, true, false, false);
        Assert.Equal(PerformancePreset.Silent, result.Performance);
        Assert.Equal(3800, result.FanControl!.FixedSpeedRpm);
    }

    [Fact]
    public void ExplicitFanEditUsesReviewedDisplayValueAndCanReturnToInheritance()
    {
        PowerProfile basis = ProfileDefaults.CreateSafeDefaults().PluggedIn;
        var current = new ApplicationPowerProfileOverride
        {
            Fans = FanMode.Maximum,
            FanControl = new(FanMode.Fixed, 5400, ManualFanCurve.Default),
        };
        ApplicationPowerProfileOverride fixedResult = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, null, FanMode.Fixed, 5500, false, true, false);
        Assert.Equal(FanMode.Fixed, fixedResult.Fans);
        Assert.Equal(5500, fixedResult.FanControl!.FixedSpeedRpm);
        ApplicationPowerProfileOverride inherited = ApplicationProfileThermalIntentEditor.Apply(
            current, basis, null, null, 5000, false, true, false);
        Assert.Null(inherited.Fans);
        Assert.Null(inherited.FanControl);
    }
}
