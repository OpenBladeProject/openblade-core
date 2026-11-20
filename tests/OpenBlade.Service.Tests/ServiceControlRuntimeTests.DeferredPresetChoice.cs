using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// The narrowed deferred-choice rule on reporting backends without direct
// manual transitions: only a choice stored with the deferral message is ever
// written by a fan request, once, and only while no performance change was
// observed since it and the fresh preset is known.
public sealed partial class ServiceControlRuntimeTests
{
    private async Task<(TestContext Context, List<string> Writes)> CreateExactAutomaticContextAsync(
        int productId,
        PowerSource powerSource,
        Func<DeviceCapabilities, PerformancePreset, PerformancePreset> firmware)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled: true);
        TestContext context = CreateContext(providerAvailable: true, initialPowerSource: powerSource,
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
        PerformancePreset desired = context.Runtime.GetStatus().DesiredState.Performance.Value;
        // A restart observes whatever the firmware holds as its first state.
        TestContext restarted = CreateContext(providerAvailable: true, initialPowerSource: powerSource,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        restarted.Device.Capabilities = capabilities;
        restarted.Device.FanTargetsAvailable = true;
        restarted.Device.ConfirmedState = restarted.Device.ConfirmedState with
        {
            Performance = firmware(capabilities, desired),
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await restarted.Runtime.ObserveAsync(default);
        Assert.NotEqual(desired, restarted.Device.ConfirmedState.Performance);
        Assert.Equal(desired, restarted.Runtime.GetStatus().DesiredState.Performance.Value);
        var writes = new List<string>();
        restarted.Device.AfterSetter = subsystem => writes.Add(subsystem == DeviceSubsystem.Fans
            ? $"Fans:{restarted.Device.FanWrites[^1]}"
            : $"Performance:{restarted.Device.PerformanceWrites[^1]}");
        return (restarted, writes);
    }

    private static void AssertNoPerformanceWrite(List<string> writes) =>
        Assert.DoesNotContain(writes, write => write.StartsWith("Performance:", StringComparison.Ordinal));

    // H1: HyperBoost reports no preset. Clearing a fan override then writes no
    // performance: without a deferred choice nothing is pending, and a pending
    // choice meets an unknown fresh preset and is dropped. The same holds for
    // an unknown preset without HyperBoost.
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task UnknownFreshPresetNeverReconcilesOnFanClear(bool hyperBoost, bool choseFirst)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        if (choseFirst)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        }
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = null,
            HyperBoostActive = hyperBoost,
        };
        await context.Runtime.ObserveAsync(default);

        ControlOperationResult cleared = await context.Runtime.SetFanControlOverrideAsync(null, null, default);

        AssertNoPerformanceWrite(writes);
        Assert.Null(context.Device.ConfirmedState.Performance);
        Assert.Equal(hyperBoost, context.Device.ConfirmedState.HyperBoostActive);
        if (cleared.Status == ControlOperationStatus.Applied)
        {
            Assert.Equal(choseFirst, cleared.Message.Contains(
                ControlCoordinator.ExternalPerformanceKeptMessage, StringComparison.Ordinal));
        }
    }

    // H2: an observed change of Custom levels after the choice is an external
    // performance change, so the choice is dropped and the levels stay.
    [Fact]
    public async Task ObservedCustomLevelChangeAfterChoiceIsKeptOnExit()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        CustomPerformanceCombination[] combinations = context.Device.Capabilities.CustomPerformance!
            .AllowedCombinations.OrderBy(item => item.Cpu).ThenBy(item => item.Gpu).Take(2).ToArray();
        var first = new CustomPerformanceSettings(combinations[0].Cpu, combinations[0].Gpu);
        var second = new CustomPerformanceSettings(combinations[1].Cpu, combinations[1].Gpu);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = first,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { CustomPerformance = second };
        await context.Runtime.ObserveAsync(default);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Contains(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(second, context.Device.ConfirmedState.CustomPerformance);
    }

    // H5: a change away and back after the choice is still an observed
    // external change, so the choice is dropped although the preset matches.
    [Fact]
    public async Task ExternalChangeAwayAndBackAfterChoiceIsKeptOnExit()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Performance };
        await context.Runtime.ObserveAsync(default);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Balanced };
        await context.Runtime.ObserveAsync(default);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Contains(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        ControlOperationResult again = await ReturnFansToAutomaticAsync(context);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, again.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
    }

    // H3: a state first observed after a restart differs from the profile
    // preset. Without a deferred choice, a fan request to Automatic leaves it.
    [Fact]
    public async Task AutomaticRequestAfterRestartKeepsFirstObservedPreset()
    {
        (TestContext context, List<string> writes) = await CreateExactAutomaticContextAsync(
            0x02A0, PowerSource.PluggedIn, (_, _) => PerformancePreset.Silent);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status is ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        AssertNoPerformanceWrite(writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // H3: on battery the profile's battery preset is not written over the
    // battery preset the firmware holds.
    [Fact]
    public async Task AutomaticRequestOnBatteryKeepsHeldBatteryPreset()
    {
        int productId = ExperimentalBladeModels.All
            .Select(model => (model.ProductId, Capabilities: ExperimentalBladePort.CreateCapabilities(model, enabled: true)))
            .First(item => ControlCoordinator.ReconcilesPerformanceOnAutomaticFans(item.Capabilities)
                && item.Capabilities.PerformancePresets.Contains(PerformancePreset.BatterySaver)
                && item.Capabilities.PerformancePresets.Contains(PerformancePreset.BatteryBalanced))
            .ProductId;
        (TestContext context, List<string> writes) = await CreateExactAutomaticContextAsync(
            productId, PowerSource.Battery, (_, desired) => desired == PerformancePreset.BatterySaver
                ? PerformancePreset.BatteryBalanced
                : PerformancePreset.BatterySaver);
        PerformancePreset held = context.Device.ConfirmedState.Performance!.Value;

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status is ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        AssertNoPerformanceWrite(writes);
        Assert.Equal(held, context.Device.ConfirmedState.Performance);
    }

    // H3: a profile edit under manual fans is not a deferred choice, so the
    // exit and a later Automatic request write only the fans.
    [Fact]
    public async Task ProfileEditUnderManualFansIsNotWrittenByFanRequests()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        ProfileSet current = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(current with
        {
            PluggedIn = current.PluggedIn with { Performance = PerformancePreset.Silent },
        }, default);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);
        ControlOperationResult again = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status is ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.True(again.Status is ControlOperationStatus.Applied, $"{again.Status}: {again.Message}");
        AssertNoPerformanceWrite(writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }

    // H3: a choice applied directly under Automatic fans that failed is not a
    // deferred choice, so a fan request does not retry it.
    [Fact]
    public async Task FailedDirectChoiceIsNotRetriedByFanRequest()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Applied, (await ReturnFansToAutomaticAsync(context)).Status);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected preset write failure."));
        ControlOperationResult choice = await ChooseAsync(context, PerformancePreset.Silent);
        Assert.NotEqual(ControlOperationStatus.Applied, choice.Status);
        Assert.NotEqual(ControlOperationStatus.Stored, choice.Status);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status is ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        AssertNoPerformanceWrite(writes);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }

    // H4: clearing the fan override applies the deferred choice on classified
    // power, as the explicit Automatic request does.
    [Fact]
    public async Task ClearingFanOverrideAppliesDeferredChoice()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);

        ControlOperationResult cleared = await context.Runtime.SetFanControlOverrideAsync(null, null, default);

        Assert.True(cleared.Status == ControlOperationStatus.Applied, $"{cleared.Status}: {cleared.Message}");
        Assert.Equal(["Fans:Automatic", "Performance:Silent"], writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // H4: on unclassified external power, clearing the fan override hands fans
    // back without a performance write. The choice stays pending, the message
    // says so, and the next fan request that finds fans Automatic applies it.
    [Fact]
    public async Task UnclassifiedFanClearKeepsDeferredChoicePending()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.Maximum, FanMode.ManualCurve },
            SupportsDirectManualFanTransitions = false,
        };
        context.Device.FanTargetsAvailable = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), CancellationToken.None);
        Assert.True(entered.Status == ControlOperationStatus.Applied, $"{entered.Status}: {entered.Message}");
        ControlOperationResult choice = await ChooseAsync(context, PerformancePreset.Silent);
        Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        context.Device.PerformanceWrites.Clear();

        ControlOperationResult cleared = await context.Runtime.SetFanControlOverrideAsync(null, null, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, cleared.Status);
        Assert.Equal(
            "Override cleared. Power-profile application and the stored performance choice wait for adapter classification.",
            cleared.Message);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Empty(context.Device.PerformanceWrites);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal([PerformancePreset.Silent], context.Device.PerformanceWrites);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // H6: a choice deferred by the performance shortcut is recorded like an
    // explicit one and applied on exit.
    [Fact]
    public async Task ShortcutDeferredChoiceIsAppliedOnExit()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        ControlOperationResult press = await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.DedicatedButton, default);
        Assert.True(press.Status == ControlOperationStatus.Stored, $"{press.Status}: {press.Message}");
        PerformancePreset selected = context.Runtime.GetStatus().DesiredState.Performance.Value;
        Assert.NotEqual(PerformancePreset.Balanced, selected);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(["Fans:Automatic", $"Performance:{selected}"], writes);
    }

    // H6: a deferred choice that the fresh state already holds needs no
    // preset write, writes only the fans and does not claim a restoration.
    // The held preset is not Balanced, whose restoration is never announced.
    [Fact]
    public async Task UnchangedDeferredChoiceDoesNotClaimRestoration()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, automatic.Message);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.DoesNotContain("restored", automatic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
    }

    // A newer choice that is not deferred supersedes a pending one. When that
    // choice fails, a later fan request does not retry it.
    [Fact]
    public async Task FailedChoiceAfterDeferredChoiceIsNotRetriedByFanRequest()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected preset write failure."));
        ControlOperationResult custom = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange,
                CustomPerformanceSettings.Default), default);
        Assert.NotEqual(ControlOperationStatus.Applied, custom.Status);
        Assert.NotEqual(ControlOperationStatus.Stored, custom.Status);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        AssertNoPerformanceWrite(writes);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlCoordinator.DeferredPerformanceNoLongerAppliesMessage, automatic.Message, StringComparison.Ordinal);
    }

    // H6: the fresh-session retry of a curve target revalidates against the
    // held preset's range, so a deferred choice does not block the retry.
    [Fact]
    public async Task CurveRetryWhilePresetIsDeferredRevalidatesAgainstHeldPreset()
    {
        var temperatures = new MutableFanTemperatureSource(new FanTemperatureReadings(40, 40));
        List<TimeSpan> retryDelays = [];
        (TestContext context, _) = await EnterExactFanModeAsync(
            0x02A0, FanMode.ManualCurve, temperatures: temperatures,
            transitionRetryDelay: (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            });
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        FanSpeedRange balanced = context.Device.Capabilities.GetFanIntentSpeedRange(PerformancePreset.Balanced)!;
        Assert.NotEqual(balanced.MaximumRpm,
            context.Device.Capabilities.GetFanIntentSpeedRange(PerformancePreset.Silent)!.MaximumRpm);
        int established = context.Device.FanTargets.Count;
        context.Device.FanTargetFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            BladeWriteFailurePhase.ThermalPreWriteRead,
            "Synthetic pre-write fan-target read failure."));
        temperatures.Readings = new FanTemperatureReadings(88, 88);

        await context.Runtime.MaintainFanCurveAsync(default);

        Assert.Single(retryDelays);
        Assert.Equal(established + 2, context.Device.FanTargets.Count);
        Assert.Equal(balanced.MaximumRpm, context.Device.FanTargets[^1].Fan1Rpm);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
    }

    // H6: without capabilities a request to Automatic declares Performance, so
    // a fresh read that finds a deferring backend can still apply its choice.
    [Theory]
    [InlineData(FanMode.Automatic)]
    [InlineData(FanMode.Fixed)]
    public void NullCapabilitiesDeclarePerformanceForAnyRequestToAutomatic(FanMode previous)
    {
        Assert.Equal(
            [DeviceSubsystem.Fans, DeviceSubsystem.Performance],
            ControlCoordinator.FanControlTransitionComponents(null, previous, FanMode.Automatic));
    }
}
