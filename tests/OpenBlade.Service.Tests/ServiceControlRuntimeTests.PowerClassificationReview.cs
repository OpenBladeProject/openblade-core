using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    private static void SetReviewRuntimeField(ServiceControlRuntime runtime, string name, object value) =>
        typeof(ServiceControlRuntime).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(runtime, value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclassifiedExternal_Review2_PowerOnlyBlockRecoversWithoutFullObservation(bool fullObservation)
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.FullPower280W);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.UsbCPower100W);
        if (fullObservation) { await context.Runtime.ObserveAsync(default); }
        else { await context.Runtime.RefreshPowerSourceClassificationAsync(default); }
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        int reads = context.Device.StateReadCalls;
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.RefreshPowerSourceClassificationAsync(default);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(reads, context.Device.StateReadCalls);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(LightingSettingsFactory.Static(new RgbColor(20, 30, 40), 60), OverrideLifetime.Persistent), default)).Status);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_CurveUsesFreshPrivatePresenceWhenPublishedFactIsFalse()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.FanTargetsAvailable = true;
        var settings = new FanControlSettings(FanMode.ManualCurve, 3700, ManualFanCurve.Default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.ManualCurve, OverrideLifetime.UntilPowerSourceChange), settings, default)).Status);
        SetReviewRuntimeField(context.Runtime, "status", context.Runtime.GetStatus() with { ExternalPowerPresent = false });
        SetReviewRuntimeField(context.Runtime, "externalPowerPresent", 0);
        int typed = context.Device.TypedCurveTargetCalls;
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.True(context.Device.TypedCurveTargetCalls > typed);
        int maximum = context.Device.Capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced)!.MaximumRpm;
        Assert.Equal(maximum, context.Device.FanTargets[^1].Fan1Rpm);
        Assert.Equal(maximum, context.Device.FanTargets[^1].Fan2Rpm);
        Assert.Equal(FanMode.ManualCurve, context.Runtime.GetStatus().FanOverride!.Value);
        Assert.DoesNotContain(FanMode.Automatic, context.Device.FanWrites);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_ContendedCurveDoesNotCaptureOrMutatePowerOutsideGate()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        SetReviewRuntimeField(context.Runtime, "externalPowerPresent", 0);
        var gate = (SemaphoreSlim)typeof(ServiceControlRuntime).GetField("operationGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context.Runtime)!;
        await gate.WaitAsync();
        int captures = context.Telemetry.CaptureCount;
        try
        {
            await context.Runtime.MaintainFanCurveAsync(default);
            Assert.Equal(captures, context.Telemetry.CaptureCount);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_OwnedBatteryStopReleasesOnUnclassifiedAcWithoutProfileWrites()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true, GpuPoweredDown: true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = true,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetBatterySaverFanStopAsync(true, default)).Status);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        ProfileSet saved = context.Runtime.GetStatus().Profiles;
        int performanceWrites = context.Device.PerformanceSetCalls;
        int samples = temperatures.FanStopReads;
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(PerformancePreset.BatterySaver, context.Device.ConfirmedState.Performance);
        Assert.Equal(1, context.Device.OwnedStopReleaseCalls);
        Assert.Equal(performanceWrites, context.Device.PerformanceSetCalls);
        Assert.Equal(samples, temperatures.FanStopReads);
        Assert.Equal(saved, context.Runtime.GetStatus().Profiles);
        Assert.True(saved.Battery.StopFansInBatterySaver);
        Assert.False(context.Runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclassifiedExternal_OwnedStopReleaseRechecksOwnershipAndPowerEpochBeforeSubmission(bool powerBoundary)
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true, GpuPoweredDown: true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = true,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetBatterySaverFanStopAsync(true, default)).Status);
        int writes = context.Device.TotalSetterCalls;
        ProfileSet saved = context.Runtime.GetStatus().Profiles;
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        context.Device.BeforeOwnedStopRelease = () =>
        {
            string field = powerBoundary ? "powerAdmissionEpoch" : "fanAuthorityEpoch";
            var info = typeof(ServiceControlRuntime).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            info.SetValue(context.Runtime, (long)info.GetValue(context.Runtime)! + 1);
        };

        await context.Runtime.MaintainFanCurveAsync(default);

        Assert.Equal(0, context.Device.OwnedStopReleaseCalls);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Equal(PerformancePreset.BatterySaver, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.Equal(saved, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_MaintenancePublishesCompletedUnavailableClassificationImmediately()
    {
        var (context, _, _) = await CreateOwnedManualFanCurveContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = true,
        };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        SetReviewRuntimeField(context.Runtime, "lastCurveFan1Rpm", null!);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(DeviceObservationReason.RetainedPowerSource, context.Runtime.GetStatus().ObservationReason);
        Assert.Equal(DeviceObservationReason.RetainedPowerSource, context.Hub.Published[^1].ObservationReason);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_CustomShortcutFallsBackFromUnsupportedConfirmedTuple()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Custom },
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceSettings.Default.Cpu, CustomPerformanceSettings.Default.Gpu),
            }),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            CustomPerformance = new CustomPerformanceSettings(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.FunctionP, default)).Status);
        Assert.Equal(CustomPerformanceSettings.Default, context.Device.ConfirmedState.CustomPerformance);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_ProfileRestoreStillYieldsOwnedLightingBeforeDeferral()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.RefreshPowerSourceClassificationAsync(default);
        SetReviewRuntimeField(context.Runtime, "pendingLightingRestore", LightingSettingsFactory.Off());
        SetReviewRuntimeField(context.Runtime, "pendingLightingRestoreFromLifecycle", false);
        SetReviewRuntimeField(context.Runtime, "explicitLightingOwnershipActive", false);
        SetReviewRuntimeField(context.Runtime, "status", context.Runtime.GetStatus() with
        {
            PotentialControllers = ["RazerAppEngine"],
        });
        int writes = context.Device.LightingSetCalls;
        int relinquished = context.Provider.RelinquishCount;
        await context.Runtime.RestoreLightingRendererAsync(default);
        Assert.Equal(relinquished + 1, context.Provider.RelinquishCount);
        Assert.Equal(writes, context.Device.LightingSetCalls);
        Assert.Equal(LightingRendererOwnershipState.None, context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task UnclassifiedExternal_Review2_CustomShortcutFiltersMaximumFansWhenCurrentFansAreManual()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Custom },
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceSettings.Default.Cpu, CustomPerformanceSettings.Default.Gpu),
            })
            { SupportsMaximumFans = true },
        };
        await context.Runtime.ObserveAsync(default);
        var custom = CustomPerformanceSettings.Default with { MaximumFans = true };
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, custom), default)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Fixed,
        };
        await context.Runtime.ObserveAsync(default);
        int writes = context.Device.TotalSetterCalls;
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.FunctionP, default)).Status);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Equal(custom, context.Runtime.GetStatus().PerformanceOverride!.CustomPerformance);
    }

}
