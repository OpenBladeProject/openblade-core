using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    public static TheoryData<int> ReportsPerformanceWithManualFansModels()
    {
        var data = new TheoryData<int>();
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            if (ExperimentalBladePort.CreateCapabilities(model, enabled: true)
                .ReportsPerformanceWithManualFans)
            {
                data.Add(model.ProductId);
            }
        }
        return data;
    }

    // Leaving manual fans on a backend that reports its real preset under manual
    // fans must not run a Performance restoration step that the request never
    // declared. The Automatic handback rewrites only the fan flag and confirms
    // the preset, so the request completes with one fan write and no
    // performance write instead of escaping the request handler.
    [Theory]
    [MemberData(nameof(ReportsPerformanceWithManualFansModels))]
    public async Task ExactSourceBackend_FixedToAutomaticUsesOneFanWriteAndKeepsPreset(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        TestContext context = CreateContext(providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        FanSpeedRange range = capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!;
        var fixedSettings = new FanControlSettings(FanMode.Fixed, range.MinimumRpm, ManualFanCurve.Default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            fixedSettings, default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        context.Device.FanWrites.Clear();
        context.Device.PerformanceWrites.Clear();

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Empty(context.Device.PerformanceWrites);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(FanMode.Automatic, status.ConfirmedState!.Fans);
        Assert.Equal(PerformancePreset.Balanced, status.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, status.DesiredState.Fans.Value);
        Assert.Equal(FanMode.Automatic, status.FanOverride!.Value);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public async Task ExactSourceBackend_CurveToAutomaticUsesOneFanWriteAndKeepsPreset(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        var temperatures = new FixedFanTemperatureSource(new FanTemperatureReadings(65, 60));
        TestContext context = CreateContext(providerAvailable: true, fanTemperatureSource: temperatures,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange),
                default)).Status);
        FanSpeedRange range = capabilities.GetFanIntentSpeedRange(PerformancePreset.Silent)!;
        var curve = new ManualFanCurve([new(40, range.MinimumRpm), new(80, range.MaximumRpm)],
            [new(40, range.MinimumRpm), new(80, range.MaximumRpm)], true);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.ManualCurve, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.ManualCurve, range.MaximumRpm, curve), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        context.Device.FanWrites.Clear();
        context.Device.PerformanceWrites.Clear();

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Empty(context.Device.PerformanceWrites);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, context.Runtime.GetStatus().ConfirmedState!.Fans);
    }

    // Each capability shape reaches Automatic with only the writes its firmware
    // contract needs. A shared manual selector hides the preset, so its exit
    // restores the saved intent; a reporting backend keeps the preset and needs
    // one fan write. Reporting backends without direct transitions enter
    // within a reviewed per-preset range, as every shipped one does.
    [Theory]
    [InlineData(true, true, PerformancePreset.Balanced, new[] { "Fans:Automatic" })]
    [InlineData(true, true, PerformancePreset.Silent, new[] { "Fans:Automatic" })]
    [InlineData(true, false, PerformancePreset.Balanced, new[] { "Fans:Automatic" })]
    [InlineData(true, false, PerformancePreset.Silent, new[] { "Fans:Automatic" })]
    [InlineData(false, true, PerformancePreset.Balanced, new[] { "Performance:Balanced" })]
    [InlineData(false, true, PerformancePreset.Silent, new[] { "Performance:Silent" })]
    [InlineData(false, false, PerformancePreset.Balanced, new[] { "Fans:Automatic" })]
    [InlineData(false, false, PerformancePreset.Silent, new[] { "Fans:Automatic", "Performance:Silent" })]
    public async Task FixedToAutomatic_EachCapabilityShapeDeclaresAndWritesOnlyWhatItNeeds(
        bool reportsPerformanceWithManualFans,
        bool supportsDirectManualFanTransitions,
        PerformancePreset intent,
        string[] expectedExitWrites)
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            ReportsPerformanceWithManualFans = reportsPerformanceWithManualFans,
            SupportsDirectManualFanTransitions = supportsDirectManualFanTransitions,
            FanSpeedRangesByPerformancePreset = reportsPerformanceWithManualFans
                && !supportsDirectManualFanTransitions
                    ? new Dictionary<PerformancePreset, FanSpeedRange>
                    {
                        [PerformancePreset.Balanced] = new(2200, 5000, 100),
                        [PerformancePreset.Silent] = new(2200, 5000, 100),
                    }
                    : new Dictionary<PerformancePreset, FanSpeedRange>(),
        };
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        if (intent != PerformancePreset.Balanced)
        {
            ControlOperationResult preset = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(intent, OverrideLifetime.UntilPowerSourceChange), default);
            Assert.True(preset.Status == ControlOperationStatus.Applied, preset.Message);
        }
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        var exitWrites = new List<string>();
        context.Device.AfterSetter = subsystem =>
        {
            exitWrites.Add(subsystem == DeviceSubsystem.Fans
                ? $"Fans:{context.Device.FanWrites[^1]}"
                : $"Performance:{context.Device.PerformanceWrites[^1]}");
            // A shared manual selector returns fans to Automatic when the
            // preset is written; a reporting backend keeps them independent.
            if (subsystem == DeviceSubsystem.Performance && !reportsPerformanceWithManualFans)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Automatic };
            }
        };

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(expectedExitWrites, exitWrites);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(FanMode.Automatic, status.ConfirmedState!.Fans);
        Assert.Equal(intent, status.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, status.DesiredState.Fans.Value);
    }

    private async Task<(TestContext Context, List<string> Writes)> EnterRz090484FixedFansAsync(
        DeviceCapabilities? cachedCapabilities = null)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        TestContext context = CreateContext(providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = cachedCapabilities ?? capabilities;
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        context.Device.Capabilities = capabilities;
        var writes = new List<string>();
        context.Device.AfterSetter = subsystem => writes.Add(subsystem == DeviceSubsystem.Fans
            ? $"Fans:{context.Device.FanWrites[^1]}"
            : $"Performance:{context.Device.PerformanceWrites[^1]}");
        return (context, writes);
    }

    // The runtime's cached state still reports Balanced with manual fans when
    // the request resolves its components, but the device now reports Custom
    // with Automatic fans. The coordinator's fresh read requires the
    // preset-ranged fan restore after the preset write; that Fans step must
    // already be declared and run through the admitted step runner.
    [Fact]
    public async Task PerformanceRequest_FanStateChangedAfterResolutionRunsDeclaredFanRestore()
    {
        (TestContext context, List<string> writes) = await EnterRz090484FixedFansAsync();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = CustomPerformanceSettings.Default,
            Fans = FanMode.Automatic,
        };
        Assert.Equal(FanMode.Fixed, context.Runtime.GetStatus().ConfirmedState!.Fans);
        int singleAttemptFanWrites = context.Device.SingleAttemptFanControlCalls;

        ControlOperationResult result = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange), default);

        Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
        Assert.Equal(["Performance:Silent", "Fans:Fixed"], writes);
        Assert.Equal(singleAttemptFanWrites + 1, context.Device.SingleAttemptFanControlCalls);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Silent, status.ConfirmedState!.Performance);
        Assert.Equal(FanMode.Fixed, status.ConfirmedState.Fans);
    }

    // Defense in depth: when a capability change after resolution makes the
    // fresh decision need a component the request did not declare, the
    // coordinator stops before its first write instead of throwing later.
    [Fact]
    public async Task PerformanceRequest_UndeclaredFanRestoreStopsBeforeAnyWrite()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        DeviceCapabilities cached = ExperimentalBladePort.CreateCapabilities(model, enabled: true) with
        {
            FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>(),
        };
        (TestContext context, List<string> writes) = await EnterRz090484FixedFansAsync(cached);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = CustomPerformanceSettings.Default,
            Fans = FanMode.Automatic,
        };

        ControlOperationResult result = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange), default);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Contains("try again", result.Message, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
    }

    [Fact]
    public async Task FanRequest_UndeclaredPerformanceStepStopsBeforeAnyWrite()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            ReportsPerformanceWithManualFans = true,
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        // The request declares Fans alone from the cached capabilities; the
        // fresh read now describes a shared selector that needs Performance.
        context.Device.Capabilities = context.Device.Capabilities with
        {
            ReportsPerformanceWithManualFans = false,
        };
        var writes = new List<DeviceSubsystem>();
        context.Device.AfterSetter = writes.Add;

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.NotEqual(ControlOperationStatus.Applied, automatic.Status);
        Assert.Contains("try again", automatic.Message, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
    }
}
