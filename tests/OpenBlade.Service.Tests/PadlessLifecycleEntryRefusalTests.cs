using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PadlessLifecycleEntryRefusalTests
{
    private static readonly HyperBoostWithoutCoolingPadEvidenceGates Closed =
        new(false, false, false, false, false, false, false, false, false, false);

    private static PadlessHyperBoostLifecycleTarget Rz090528 => new(
        "RZ09-0528", PerformancePreset.Performance, new string('A', 64), Closed);

    private static PadlessHyperBoostLifecycleTarget Rz090581 => new(
        "RZ09-0581", PerformancePreset.Balanced, new string('B', 64), Closed);

    private static ConfirmedDeviceState Confirmed(PerformancePreset? performance, FanMode fans) =>
        new(performance, fans, new ConfirmedBatteryState(true, 80, false), Lighting: null);

    private static DesiredDeviceState Desired(PerformancePreset performance, FanMode fans,
        GpuClockTuningSettings? tuning = null) =>
        new(
            new ResolvedSetting<PerformancePreset>(performance, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(fans, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(LightingSettingsFactory.SpectrumCycling(50), SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(80), true, false))
        {
            CustomPerformance = performance == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
            GpuClockTuning = tuning,
        };

    [Fact]
    public void Rz090528AdmitsOnlyCapturedPerformanceAutomaticEntry()
    {
        Assert.Null(ServiceControlRuntime.PadlessLifecycleEntryRefusal(
            Rz090528,
            Confirmed(PerformancePreset.Performance, FanMode.Automatic),
            Desired(PerformancePreset.Performance, FanMode.Automatic)));
        Assert.Null(ServiceControlRuntime.PadlessLifecycleEntryRefusal(
            Rz090528,
            Confirmed(PerformancePreset.Performance, FanMode.Automatic),
            Desired(PerformancePreset.Performance, FanMode.Automatic,
                new GpuClockTuningSettings(false, GpuClockOffsetSettings.Default))));
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic, PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.Automatic, PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic, PerformancePreset.Performance, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.ManualCurve, PerformancePreset.Performance, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Performance, FanMode.Automatic, PerformancePreset.Performance, FanMode.Fixed)]
    [InlineData(PerformancePreset.Custom, FanMode.Automatic, PerformancePreset.Custom, FanMode.Automatic)]
    public void Rz090528RefusesUncapturedEntryBridges(PerformancePreset confirmed, FanMode confirmedFans,
        PerformancePreset desired, FanMode desiredFans)
    {
        Assert.NotNull(ServiceControlRuntime.PadlessLifecycleEntryRefusal(
            Rz090528, Confirmed(confirmed, confirmedFans), Desired(desired, desiredFans)));
    }

    [Fact]
    public void Rz090528RefusesEnabledGpuTuning()
    {
        Assert.NotNull(ServiceControlRuntime.PadlessLifecycleEntryRefusal(
            Rz090528,
            Confirmed(PerformancePreset.Performance, FanMode.Automatic),
            Desired(PerformancePreset.Performance, FanMode.Automatic,
                new GpuClockTuningSettings(true, GpuClockOffsetSettings.Default))));
    }

    [Fact]
    public void BalancedTargetKeepsItsExistingEntryPolicy()
    {
        Assert.Null(ServiceControlRuntime.PadlessLifecycleEntryRefusal(
            Rz090581,
            Confirmed(PerformancePreset.Silent, FanMode.ManualCurve),
            Desired(PerformancePreset.Silent, FanMode.ManualCurve)));
    }
}
