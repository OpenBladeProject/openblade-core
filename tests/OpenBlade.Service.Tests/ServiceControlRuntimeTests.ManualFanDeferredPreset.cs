using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    // Every exact reporting backend without direct manual transitions, paired
    // with each ordinary preset other than Balanced. The variant names how the
    // preset relates to the Balanced fixed target chosen before it.
    public static TheoryData<int, PerformancePreset, string> DeferredPresetUnderManualFansPairs()
    {
        var data = new TheoryData<int, PerformancePreset, string>();
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
            if (!capabilities.ReportsPerformanceWithManualFans
                || capabilities.SupportsDirectManualFanTransitions)
            {
                continue;
            }
            int rpm = capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!.MinimumRpm;
            foreach (PerformancePreset preset in capabilities.PerformancePresets.Order())
            {
                if (preset is PerformancePreset.Balanced or PerformancePreset.Custom
                    or PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced)
                {
                    continue;
                }
                string variant = capabilities.GetFanSpeedRange(preset) is not { } range ? "NoFanRange"
                    : range.Supports(rpm) ? "Ranged" : "TargetOutOfRange";
                data.Add(model.ProductId, preset, variant);
            }
        }
        return data;
    }

    private async Task<(TestContext Context, List<string> Writes)> EnterExactBalancedFixedFansAsync(int productId)
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
        int rpm = capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!.MinimumRpm;
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, rpm, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        var writes = new List<string>();
        context.Device.AfterSetter = subsystem => writes.Add(subsystem == DeviceSubsystem.Fans
            ? $"Fans:{context.Device.FanWrites[^1]}"
            : $"Performance:{context.Device.PerformanceWrites[^1]}");
        return (context, writes);
    }

    // These backends block performance writes under manual fans. A preset the
    // user chooses there is stored with the deferral message and applied by
    // the exit to Automatic, whether or not the preset has its own fan range.
    [Theory]
    [MemberData(nameof(DeferredPresetUnderManualFansPairs))]
    public async Task ExactReportingBackendWithoutDirectTransitions_AppliesPresetChosenUnderManualFansOnExit(
        int productId, PerformancePreset preset, string variant)
    {
        (TestContext context, List<string> writes) = await EnterExactBalancedFixedFansAsync(productId);

        ControlOperationResult chosen = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(preset, OverrideLifetime.UntilPowerSourceChange), default);

        Assert.True(chosen.Status == ControlOperationStatus.Stored, $"{variant}: {chosen.Status} {chosen.Message}");
        Assert.Contains("will be applied when fans return to Automatic", chosen.Message, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{variant}: {automatic.Message}");
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic", $"Performance:{preset}"], writes);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(preset, status.ConfirmedState!.Performance);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState.Fans);
        Assert.Empty(status.ExternalChanges);
    }

    // Rule 5: a preset changed outside OpenBlade while fans were manual stays
    // as it is when the user returns fans to Automatic. Without a deferred
    // choice nothing is reported; a deferred choice made before the change is
    // dropped and the result says why.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactReportingBackendWithoutDirectTransitions_KeepsExternalPresetChangeOnExit(bool choseFirst)
    {
        (TestContext context, List<string> writes) = await EnterExactBalancedFixedFansAsync(0x02A0);
        if (choseFirst)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(PerformancePreset.Performance, OverrideLifetime.UntilPowerSourceChange),
                default)).Status);
        }
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Silent,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Empty(writes);

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(choseFirst, automatic.Message.Contains(
            ControlCoordinator.ExternalPerformanceKeptMessage, StringComparison.Ordinal));
        Assert.Equal(["Fans:Automatic"], writes);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Silent, status.ConfirmedState!.Performance);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState.Fans);
        Assert.Contains(DeviceSubsystem.Performance, status.ExternalChanges);
    }

    // Backends with direct transitions apply the preset while fans stay
    // manual, so the exit writes only the fan mode.
    [Theory]
    [InlineData(0x029D, PerformancePreset.Silent)]
    [InlineData(0x029F, PerformancePreset.Silent)]
    [InlineData(0x029F, PerformancePreset.Performance)]
    public async Task ExactDirectReportingBackend_AppliesPresetUnderManualFansAndExitWritesOnlyFans(
        int productId, PerformancePreset preset)
    {
        (TestContext context, List<string> writes) = await EnterExactBalancedFixedFansAsync(productId);

        ControlOperationResult chosen = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(preset, OverrideLifetime.UntilPowerSourceChange), default);
        Assert.True(chosen.Status == ControlOperationStatus.Applied, chosen.Message);
        Assert.Equal([$"Performance:{preset}"], writes);
        writes.Clear();

        ControlOperationResult automatic = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(preset, context.Device.ConfirmedState.Performance);
    }

    // Defense in depth for the entry bridge: cached capabilities admit direct
    // manual transitions, so the request declares Fans alone, but the fresh read
    // requires the Balanced bridge. The coordinator stops before any write.
    [Fact]
    public async Task FanRequest_UndeclaredEntryBridgeStopsBeforeAnyWrite()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsDirectManualFanTransitions = false,
        };
        var writes = new List<DeviceSubsystem>();
        context.Device.AfterSetter = writes.Add;

        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);

        Assert.Equal(ControlOperationStatus.Blocked, entered.Status);
        Assert.Contains(ControlCoordinator.UndeclaredThermalComponentMessage, entered.Message, StringComparison.Ordinal);
        Assert.Empty(writes);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }
}
