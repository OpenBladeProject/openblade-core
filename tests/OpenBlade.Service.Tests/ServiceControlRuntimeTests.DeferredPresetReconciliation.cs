using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// The reconciliation rule for reporting backends without direct manual
// transitions: whenever fans are or become Automatic, the applied preset is
// reconciled to the desired intent from the fresh readback, unless that
// readback shows an external preset change newer than the user's last explicit
// performance choice. Each test drives the real runtime with the exact
// source-reviewed capabilities.
public sealed partial class ServiceControlRuntimeTests
{
    private sealed class MutableFanTemperatureSource(FanTemperatureReadings readings)
        : IFanTemperatureSource
    {
        public FanTemperatureReadings Readings { get; set; } = readings;

        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Readings);
        }
    }

    private async Task<(TestContext Context, List<string> Writes)> EnterExactFanModeAsync(
        int productId,
        FanMode mode = FanMode.Fixed,
        PowerSource powerSource = PowerSource.PluggedIn,
        IFanTemperatureSource? temperatures = null,
        Func<DeviceCapabilities, ProfileSet, ProfileSet>? profiles = null)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        TestContext context = CreateContext(providerAvailable: true, initialPowerSource: powerSource,
            fanTemperatureSource: temperatures,
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
        PerformancePreset start = context.Runtime.GetStatus().DesiredState.Performance.Value;
        if (start != PerformancePreset.Balanced)
        {
            // The battery profile can resolve to another preset; start there.
            context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = start };
            await context.Runtime.ObserveAsync(default);
        }
        if (capabilities.GetFanSpeedRange(start) is null)
        {
            // Manual fans need a preset with a reviewed range; select one first.
            start = new[] { PerformancePreset.Balanced, PerformancePreset.BatteryBalanced }.First(preset =>
                PerformancePowerSourcePolicy.IsAllowed(powerSource, preset)
                && capabilities.GetFanSpeedRange(preset) is not null);
            ControlOperationResult ranged = await ChooseAsync(context, start);
            Assert.True(ranged.Status == ControlOperationStatus.Applied, $"{ranged.Status}: {ranged.Message}");
        }
        if (profiles is not null)
        {
            ControlOperationResult saved = await context.Runtime.UpdateProfilesAsync(
                profiles(capabilities, context.Runtime.GetStatus().Profiles), default);
            Assert.True(saved.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied, saved.Message);
            ControlOperationResult balanced = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(PerformancePreset.Balanced, OverrideLifetime.UntilPowerSourceChange), default);
            Assert.True(balanced.Status is ControlOperationStatus.Applied, balanced.Message);
            Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        }
        FanSpeedRange range = capabilities.GetFanIntentSpeedRange(start)!;
        FanControlSettings settings = mode == FanMode.ManualCurve
            ? new FanControlSettings(FanMode.ManualCurve, range.MaximumRpm, new ManualFanCurve(
                [new(40, range.MinimumRpm), new(80, range.MaximumRpm)],
                [new(40, range.MinimumRpm), new(80, range.MaximumRpm)], true))
            : new FanControlSettings(FanMode.Fixed, capabilities.GetFanSpeedRange(start)!.MinimumRpm,
                ManualFanCurve.Default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(mode, OverrideLifetime.UntilPowerSourceChange), settings, default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(mode == FanMode.ManualCurve ? FanMode.Maximum : FanMode.Fixed, context.Device.ConfirmedState.Fans);
        var writes = new List<string>();
        context.Device.AfterSetter = subsystem => writes.Add(subsystem == DeviceSubsystem.Fans
            ? context.Device.FanWrites.Count > 0 && context.Device.ConfirmedState.Fans != FanMode.Maximum
                ? $"Fans:{context.Device.FanWrites[^1]}"
                : $"Targets:{context.Device.FanTargets[^1].Fan1Rpm}"
            : $"Performance:{context.Device.PerformanceWrites[^1]}");
        return (context, writes);
    }

    private static Task<ControlOperationResult> ChooseAsync(TestContext context, PerformancePreset? preset) =>
        context.Runtime.SetPerformanceConfigurationOverrideAsync(
            preset is { } value ? new PerformanceOverride(value, OverrideLifetime.UntilPowerSourceChange) : null,
            default);

    private static Task<ControlOperationResult> ReturnFansToAutomaticAsync(TestContext context) =>
        context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, default);

    // G1: clearing a temporary override returns the intent to a Custom
    // profile. The clear resolves the profile intent instead of the stale
    // staged preset, so the Custom policy hands fans back to Automatic and
    // applies Custom, as main does, instead of promising a deferral that the
    // later fan clear never applies.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02B6)]
    public async Task ClearingOverrideToCustomProfileUnderManualFansAppliesCustom(int productId)
    {
        CustomPerformanceSettings? custom = null;
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(productId,
            profiles: (capabilities, current) =>
            {
                CustomPerformanceCombination combination = capabilities.CustomPerformance!.AllowedCombinations
                    .OrderBy(item => item.Cpu).ThenBy(item => item.Gpu).First();
                custom = new CustomPerformanceSettings(combination.Cpu, combination.Gpu);
                return current with
                {
                    PluggedIn = current.PluggedIn with
                    {
                        Performance = PerformancePreset.Custom,
                        CustomPerformance = custom,
                    },
                };
            });

        ControlOperationResult cleared = await ChooseAsync(context, null);
        ControlOperationResult fansCleared = await context.Runtime.SetFanControlOverrideAsync(null, null, default);

        Assert.True(cleared.Status == ControlOperationStatus.Applied, $"{cleared.Status}: {cleared.Message}");
        Assert.True(fansCleared.Status is ControlOperationStatus.Applied, $"{fansCleared.Status}: {fansCleared.Message}");
        Assert.Equal(["Fans:Automatic", "Performance:Custom"], writes);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(custom, context.Device.ConfirmedState.CustomPerformance);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
    }

    // G2: an explicit choice made after an observed external change is newer
    // than that change, whether it picks another preset or the original back.
    [Theory]
    [InlineData(0x02A0, PerformancePreset.Silent, PerformancePreset.Performance)]
    [InlineData(0x02A0, PerformancePreset.Silent, PerformancePreset.Balanced)]
    [InlineData(0x02B6, PerformancePreset.Silent, PerformancePreset.Turbo)]
    [InlineData(0x02B6, PerformancePreset.Silent, PerformancePreset.Balanced)]
    public async Task ChoiceAfterObservedExternalChangeIsAppliedOnExit(
        int productId, PerformancePreset external, PerformancePreset chosen)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(productId);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = external };
        await context.Runtime.ObserveAsync(default);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult choice = await ChooseAsync(context, chosen);
        Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic", $"Performance:{chosen}"], writes);
        Assert.Equal(chosen, context.Device.ConfirmedState.Performance);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }

    // G2: a latch that no longer describes the hardware does not suppress the
    // intent; the firmware already holds it, so nothing is written.
    [Fact]
    public async Task StaleExternalLatchWithFirmwareBackAtIntentWritesOnlyFans()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        await context.Runtime.ObserveAsync(default);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Balanced };
        await context.Runtime.ObserveAsync(default);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }

    // G3: an external change that no observation recorded yet is still newer
    // than the intent when the fresh readback shows it, with or without a
    // deferred choice made before it.
    [Theory]
    [InlineData(null)]
    [InlineData(PerformancePreset.Performance)]
    public async Task UnobservedExternalChangeIsKeptOnExit(PerformancePreset? chosenFirst)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        if (chosenFirst is { } chosen)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, chosen)).Status);
        }
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Contains(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }

    // G4: when the first exit returned fans to Automatic but the preset write
    // failed, a retry decides from the fresh preset, not from the previous fan
    // mode, and applies the deferred intent.
    [Fact]
    public async Task RetryAfterFailedExitAppliesDeferredPreset()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected preset write failure."));

        ControlOperationResult first = await ReturnFansToAutomaticAsync(context);
        Assert.NotEqual(ControlOperationStatus.Applied, first.Status);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        writes.Clear();

        ControlOperationResult retry = await ReturnFansToAutomaticAsync(context);

        Assert.True(retry.Status == ControlOperationStatus.Applied, $"{retry.Status}: {retry.Message}");
        Assert.Contains("Performance:Silent", writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
    }

    // G5: a running curve keeps following temperature while a preset waits for
    // Automatic, within the range of the preset the firmware still holds.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02B6)]
    public async Task CurveKeepsTrackingTemperatureWhilePresetIsDeferred(int productId)
    {
        var temperatures = new MutableFanTemperatureSource(new FanTemperatureReadings(40, 40));
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(
            productId, FanMode.ManualCurve, temperatures: temperatures);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        FanSpeedRange balanced = context.Device.Capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced)!;
        writes.Clear();

        temperatures.Readings = new FanTemperatureReadings(88, 88);
        await context.Runtime.MaintainFanCurveAsync(default);

        Assert.Equal([$"Targets:{balanced.MaximumRpm}"], writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }

    // G5: changing the fixed speed while a preset waits uses the range of the
    // preset the firmware holds instead of blocking on the deferred intent.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02B6)]
    public async Task FixedSpeedChangeWhilePresetIsDeferredFollowsAppliedPreset(int productId)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(productId);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        FanSpeedRange balanced = context.Device.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!;
        int rpm = balanced.MinimumRpm + balanced.StepRpm;

        ControlOperationResult changed = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, rpm, ManualFanCurve.Default), default);

        Assert.True(changed.Status == ControlOperationStatus.Applied, $"{changed.Status}: {changed.Message}");
        Assert.Equal(["Fans:Fixed"], writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);
        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // G6: the performance shortcut never sticks under manual fans. Custom
    // takes the same fan handback as an explicit choice, and every press
    // either stores or applies the next preset.
    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02B6)]
    public async Task PerformanceShortcutCyclesUnderManualFans(int productId)
    {
        (TestContext context, _) = await EnterExactFanModeAsync(productId);
        var selected = new List<PerformancePreset>();
        for (int press = 0; press < 6; press++)
        {
            ControlOperationResult result = await context.Runtime.CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton, default);
            Assert.True(result.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied,
                $"press {press + 1}: {result.Status} {result.Message}");
            PerformancePreset preset = context.Runtime.GetStatus().DesiredState.Performance.Value;
            if (preset == PerformancePreset.Custom)
            {
                // The Custom policy hands fans back to Automatic and applies at
                // once, exactly as an explicit Custom choice does.
                Assert.Equal(ControlOperationStatus.Applied, result.Status);
                Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
                Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
            }
            selected.Add(preset);
        }

        Assert.Contains(PerformancePreset.Custom, selected);
        for (int index = 1; index < selected.Count; index++)
        {
            Assert.NotEqual(selected[index - 1], selected[index]);
        }
    }

    // G7: on battery the deferral is reported as stored, matching the exit
    // that later applies it.
    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02C7)]
    public async Task BatteryChoiceUnderManualFansIsStoredAndAppliedOnExit(int productId)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(
            productId, powerSource: PowerSource.Battery);
        PerformancePreset start = context.Device.ConfirmedState.Performance!.Value;
        PerformancePreset chosen = new[] { PerformancePreset.BatterySaver, PerformancePreset.Silent, PerformancePreset.Balanced }
            .First(preset => preset != start && context.Device.Capabilities.PerformancePresets.Contains(preset));

        ControlOperationResult choice = await ChooseAsync(context, chosen);
        Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        Assert.Contains("will be applied when fans return to Automatic", choice.Message, StringComparison.Ordinal);
        Assert.Empty(writes);
        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(["Fans:Automatic", $"Performance:{chosen}"], writes);
        Assert.Equal(chosen, context.Device.ConfirmedState.Performance);
    }

    // G8: a shared selector hides the preset under manual fans, so an observed
    // change there never suppresses the exit restore.
    [Fact]
    public async Task SharedSelectorExitRestoresIntentDespiteObservedPresetChange()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            ReportsPerformanceWithManualFans = false,
            SupportsDirectManualFanTransitions = false,
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
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        await context.Runtime.ObserveAsync(default);
        var writes = new List<string>();
        context.Device.AfterSetter = subsystem =>
        {
            writes.Add(subsystem == DeviceSubsystem.Fans
                ? $"Fans:{context.Device.FanWrites[^1]}"
                : $"Performance:{context.Device.PerformanceWrites[^1]}");
            if (subsystem == DeviceSubsystem.Performance)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Automatic };
            }
        };

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic", "Performance:Balanced"], writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }
}
