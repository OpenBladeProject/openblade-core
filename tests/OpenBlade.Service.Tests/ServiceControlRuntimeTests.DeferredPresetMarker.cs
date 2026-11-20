using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// The deferred-choice marker after startup reconciliation, where observations
// compare the confirmed state with the desired state. Only an observed
// hardware change invalidates the marker; OpenBlade's own comparisons, which
// differ by design while a choice is deferred, neither invalidate nor clear
// it. The marker applies only while the resolved intent still equals the
// recorded choice; otherwise it is dropped as no longer applying.
public sealed partial class ServiceControlRuntimeTests
{
    // J1: a persistent choice that is unchanged across a power change still
    // applies, although the transition compared it with the held preset.
    [Fact]
    public async Task PersistentDeferredChoiceAppliesAfterPowerSourceChange()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0,
            reconcileStartup: true, fanLifetime: OverrideLifetime.Persistent);
        ControlOperationResult choice = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.Persistent), default);
        Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        await context.Runtime.HandlePowerSourceChangedAsync(default);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Silent, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        AssertNoPerformanceWrite(writes);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal(["Fans:Automatic", "Performance:Silent"], writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // J1: application-profile activation runs a transition that compares the
    // held preset with the deferred choice. An explicit choice outranks the
    // application profile, so it still applies. A clear to the profile that
    // the application profile then replaces is dropped with the accurate
    // message, not as an outside change, and writes no performance.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationProfileActivationKeepsOrReplacesDeferredChoice(bool explicitChoice)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0,
            reconcileStartup: true, fanLifetime: OverrideLifetime.Persistent);
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Game", @"C:\Games\deferred.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = explicitChoice ? PerformancePreset.Performance : PerformancePreset.Silent,
                },
            });
        ControlOperationResult saved = await context.Runtime.UpdateProfilesAsync(
            context.Runtime.GetStatus().Profiles with { ApplicationProfiles = [application] }, default);
        Assert.True(saved.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied, saved.Message);
        if (!explicitChoice)
        {
            // Hold a preset other than the profile preset, so the clear is deferred.
            context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Performance };
            await context.Runtime.ObserveAsync(default);
        }
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        if (!explicitChoice)
        {
            ControlOperationResult cleared = await ChooseAsync(context, null);
            Assert.True(cleared.Status == ControlOperationStatus.Stored, $"{cleared.Status}: {cleared.Message}");
        }
        PerformancePreset held = context.Device.ConfirmedState.Performance!.Value;
        await ReportTestApplicationAsync(context, application);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(application.Id, context.Runtime.GetStatus().ActiveApplicationProfileId);
        Assert.Equal(PerformancePreset.Silent, context.Runtime.GetStatus().DesiredState.Performance.Value);
        AssertNoPerformanceWrite(writes);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        if (explicitChoice)
        {
            Assert.Equal(["Fans:Automatic", "Performance:Silent"], writes);
            Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        }
        else
        {
            Assert.Contains(ControlCoordinator.DeferredPerformanceNoLongerAppliesMessage, automatic.Message, StringComparison.Ordinal);
            Assert.Equal(["Fans:Automatic"], writes);
            Assert.Equal(held, context.Device.ConfirmedState.Performance);
        }
    }

    // J1: a choice deferred on unclassified external power still applies
    // after the adapter is classified, although that classification ran a
    // comparison against the held preset.
    [Fact]
    public async Task UnclassifiedDeferredChoiceAppliesAfterAdapterClassification()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.Maximum, FanMode.ManualCurve },
            SupportsDirectManualFanTransitions = false,
        };
        context.Device.FanTargetsAvailable = true;
        await context.Runtime.ReconcileStartupProfileAsync(default);
        await context.Runtime.ObserveAsync(default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.Fixed, OverrideLifetime.Persistent),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, $"{entered.Status}: {entered.Message}");
        ControlOperationResult choice = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.Persistent), default);
        Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(default);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        context.Device.PerformanceWrites.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal([PerformancePreset.Silent], context.Device.PerformanceWrites);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // J1: a choice made before startup reconciliation survives the first
    // comparison after it.
    [Fact]
    public async Task DeferredChoiceSurvivesFirstComparisonAfterStartup()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        await context.Runtime.ReconcileStartupProfileAsync(default);
        await context.Runtime.ObserveAsync(default);
        AssertNoPerformanceWrite(writes);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal(["Fans:Automatic", "Performance:Silent"], writes);
    }

    // J1: an observed hardware change after startup reconciliation still
    // invalidates the choice.
    [Fact]
    public async Task ObservedChangeAfterStartupStillKeepsMode()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0, reconcileStartup: true);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Performance };
        await context.Runtime.ObserveAsync(default);
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.Contains(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        AssertNoPerformanceWrite(writes);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
    }

    // J3: a battery choice deferred under manual fans does not become the
    // plugged-in profile preset after plug-in. The choice no longer applies.
    [Fact]
    public async Task BatteryDeferredChoiceIsNotAppliedAfterPlugIn()
    {
        int productId = ExperimentalBladeModels.All
            .Select(model => (model.ProductId, Capabilities: ExperimentalBladePort.CreateCapabilities(model, enabled: true)))
            .First(item => ControlCoordinator.ReconcilesPerformanceOnAutomaticFans(item.Capabilities)
                && item.Capabilities.PerformancePresets.Contains(PerformancePreset.BatterySaver)
                && item.Capabilities.PerformancePresets.Contains(PerformancePreset.BatteryBalanced))
            .ProductId;
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(productId,
            reconcileStartup: true, fanLifetime: OverrideLifetime.Persistent);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(default);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        PerformancePreset held = context.Device.ConfirmedState.Performance!.Value;
        PerformancePreset choice = held == PerformancePreset.BatterySaver
            ? PerformancePreset.BatteryBalanced : PerformancePreset.BatterySaver;
        ControlOperationResult stored = await ChooseAsync(context, choice);
        Assert.True(stored.Status == ControlOperationStatus.Stored, $"{stored.Status}: {stored.Message}");
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.HandlePowerSourceChangedAsync(default);
        await context.Runtime.ObserveAsync(default);
        PerformancePreset pluggedIn = context.Runtime.GetStatus().DesiredState.Performance.Value;
        Assert.NotEqual(choice, pluggedIn);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        PerformancePreset beforeExit = context.Device.ConfirmedState.Performance!.Value;
        writes.Clear();

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Contains(ControlCoordinator.DeferredPerformanceNoLongerAppliesMessage, automatic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlCoordinator.ExternalPerformanceKeptMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(beforeExit, context.Device.ConfirmedState.Performance);
    }

    // J3: a clear to the profile deferred under manual fans does not write a
    // later profile edit. The recorded choice no longer applies.
    [Fact]
    public async Task ClearToProfileDeferralDoesNotWriteLaterProfileEdit()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0, reconcileStartup: true);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Performance };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        ControlOperationResult cleared = await ChooseAsync(context, null);
        Assert.True(cleared.Status == ControlOperationStatus.Stored, $"{cleared.Status}: {cleared.Message}");
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        Assert.NotEqual(PerformancePreset.Silent, profiles.PluggedIn.Performance);
        ControlOperationResult edited = await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = PerformancePreset.Silent },
        }, default);
        Assert.True(edited.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied, edited.Message);
        AssertNoPerformanceWrite(writes);

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Contains(ControlCoordinator.DeferredPerformanceNoLongerAppliesMessage, automatic.Message, StringComparison.Ordinal);
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
    }

    // J2: an exit preset write that may have been submitted is never replayed
    // by a later fan request.
    [Theory]
    [InlineData("indeterminate")]
    [InlineData("io")]
    [InlineData("canceled")]
    public async Task PossiblySubmittedExitPresetWriteIsNeverReplayed(string failure)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        using var cancellation = new CancellationTokenSource();
        context.Device.PerformanceFailures.Enqueue(failure switch
        {
            "indeterminate" => new BladeWriteException(WriteSubmissionState.Indeterminate, "Injected uncertain preset write."),
            "io" => new IOException("Injected preset write failure."),
            _ => new OperationCanceledException(cancellation.Token),
        });
        if (failure == "canceled")
        {
            context.Device.BeforeQueuedPerformanceFailure = cancellation.Cancel;
        }

        try
        {
            ControlOperationResult exit = await context.Runtime.SetFanControlOverrideAsync(
                new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
                FanControlSettings.Default, cancellation.Token);
            Assert.NotEqual(ControlOperationStatus.Applied, exit.Status);
        }
        catch (OperationCanceledException) when (failure == "canceled")
        {
        }
        Assert.Equal([PerformancePreset.Silent], context.Device.PerformanceWrites);
        context.Device.BeforeQueuedPerformanceFailure = null;
        await context.Runtime.ObserveAsync(default);

        await ReturnFansToAutomaticAsync(context);
        await context.Runtime.SetFanControlOverrideAsync(null, null, default);

        Assert.Equal([PerformancePreset.Silent], context.Device.PerformanceWrites);
    }

    // J2: an exit preset write that was known not to be applied re-arms the
    // choice, so the next fan request applies it exactly once.
    [Fact]
    public async Task KnownNotAppliedExitPresetWriteAppliesExactlyOnceLater()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied, "Injected preset write that was not submitted."));

        ControlOperationResult failed = await ReturnFansToAutomaticAsync(context);
        ControlOperationResult retried = await ReturnFansToAutomaticAsync(context);
        ControlOperationResult again = await ReturnFansToAutomaticAsync(context);

        Assert.NotEqual(ControlOperationStatus.Applied, failed.Status);
        Assert.True(retried.Status == ControlOperationStatus.Applied, $"{retried.Status}: {retried.Message}");
        Assert.True(again.Status == ControlOperationStatus.Applied, $"{again.Status}: {again.Message}");
        Assert.Equal([PerformancePreset.Silent, PerformancePreset.Silent], context.Device.PerformanceWrites);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
    }

    // J2: a preset step stopped before any write re-arms the choice, so the
    // next fan request applies it: a lost session or a failed pre-write read
    // (known not applied), or fans found manual again by the pre-write read
    // (blocked with no submission at all).
    [Theory]
    [InlineData("session")]
    [InlineData("read")]
    [InlineData("fans")]
    public async Task ExitPresetStepBlockedBeforeWriteAppliesLater(string stop)
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0,
            transitionRetryDelay: static (_, _) => Task.CompletedTask);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        bool stillControlsSession = true;
        int readsAfterFanWrite = -1;
        context.Device.UseScopedPerformanceFanRead = stop != "session";
        context.Device.BeforePerformanceFanStateReadResult = _ =>
        {
            // The first read confirms the fans; later ones precede the preset write.
            if (readsAfterFanWrite >= 0 && ++readsAfterFanWrite >= 2)
            {
                if (stop == "read")
                {
                    throw new IOException("Injected pre-write read failure.");
                }
                context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Fixed };
            }
        };
        Action<DeviceSubsystem>? recordWrite = context.Device.AfterSetter;
        context.Device.AfterSetter = subsystem =>
        {
            recordWrite?.Invoke(subsystem);
            if (subsystem == DeviceSubsystem.Fans)
            {
                stillControlsSession = stop != "session";
                readsAfterFanWrite = stop == "session" ? -1 : 0;
            }
        };

        ControlOperationResult blocked = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.UntilPowerSourceChange),
            FanControlSettings.Default, () => stillControlsSession, default);
        readsAfterFanWrite = -1;
        Assert.True(blocked.Status != ControlOperationStatus.Applied, blocked.Message);
        Assert.Empty(context.Device.PerformanceWrites);
        context.Device.AfterSetter = recordWrite;

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal([PerformancePreset.Silent], context.Device.PerformanceWrites);
    }

    // A preset that the pre-write read already finds at the choice is not
    // written and is not announced as restored.
    [Fact]
    public async Task ExitPresetFoundAppliedBeforeWriteDoesNotClaimRestoration()
    {
        (TestContext context, List<string> writes) = await EnterExactFanModeAsync(0x02A0);
        Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        int readsAfterFanWrite = -1;
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.BeforePerformanceFanStateReadResult = _ =>
        {
            if (readsAfterFanWrite >= 0 && ++readsAfterFanWrite == 2)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
            }
        };
        Action<DeviceSubsystem>? recordWrite = context.Device.AfterSetter;
        context.Device.AfterSetter = subsystem =>
        {
            recordWrite?.Invoke(subsystem);
            if (subsystem == DeviceSubsystem.Fans)
            {
                readsAfterFanWrite = 0;
            }
        };

        ControlOperationResult automatic = await ReturnFansToAutomaticAsync(context);

        Assert.True(automatic.Status == ControlOperationStatus.Applied, $"{automatic.Status}: {automatic.Message}");
        Assert.Equal(["Fans:Automatic"], writes);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.DoesNotContain("restored", automatic.Message, StringComparison.Ordinal);
    }

    // J4: the tray clears performance before fans. On a shared selector the
    // performance clear under manual fans is stored, and the fan clear then
    // writes only the fans: the override preset is not applied first and
    // replaced afterwards, and the end state matches the other order.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedSelectorClearOrderReachesSameStateWithoutExtraWrites(bool performanceFirst)
    {
        TestContext context = CreateContext(providerAvailable: true, initialPowerSource: PowerSource.PluggedIn);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsDirectManualFanTransitions = false,
            ReportsPerformanceWithManualFans = false,
        };
        context.Device.FanTargetsAvailable = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied, (await ChooseAsync(context, PerformancePreset.Silent)).Status);
        int rpm = context.Device.Capabilities.GetFanSpeedRange(PerformancePreset.Balanced)!.MinimumRpm;
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, rpm, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        var writes = new List<string>();
        context.Device.AfterSetter = subsystem => writes.Add(subsystem == DeviceSubsystem.Fans
            ? $"Fans:{context.Device.FanWrites[^1]}"
            : $"Performance:{context.Device.PerformanceWrites[^1]}");

        if (performanceFirst)
        {
            Assert.Equal(ControlOperationStatus.Stored, (await ChooseAsync(context, null)).Status);
            Assert.Equal(ControlOperationStatus.Applied,
                (await context.Runtime.SetFanControlOverrideAsync(null, null, default)).Status);
        }
        else
        {
            Assert.Equal(ControlOperationStatus.Applied,
                (await context.Runtime.SetFanControlOverrideAsync(null, null, default)).Status);
            Assert.Equal(ControlOperationStatus.Applied, (await ChooseAsync(context, null)).Status);
        }

        Assert.Equal(
            performanceFirst
                ? ["Fans:Automatic"]
                : ["Fans:Automatic", "Performance:Silent", "Performance:Balanced"],
            writes);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Null(status.PerformanceOverride);
        Assert.Null(status.FanOverride);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(PerformancePreset.Balanced, status.DesiredState.Performance.Value);
    }

    // J5: the unclassified fan clear names the stored choice only when one is
    // pending on a backend that applies it on Automatic fans. A choice stored
    // before the backend reported direct manual transitions does not wait.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnclassifiedFanClearNamesStoredChoiceOnlyWhenItWaits(bool choose, bool direct)
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.Maximum, FanMode.ManualCurve },
            SupportsDirectManualFanTransitions = false,
        };
        context.Device.FanTargetsAvailable = true;
        await context.Runtime.ObserveAsync(default);
        ControlOperationResult entered = await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default), default);
        Assert.True(entered.Status == ControlOperationStatus.Applied, $"{entered.Status}: {entered.Message}");
        if (choose)
        {
            ControlOperationResult choice = await ChooseAsync(context, PerformancePreset.Silent);
            Assert.True(choice.Status == ControlOperationStatus.Stored, $"{choice.Status}: {choice.Message}");
        }
        if (direct)
        {
            context.Device.Capabilities = context.Device.Capabilities with { SupportsDirectManualFanTransitions = true };
            await context.Runtime.ObserveAsync(default);
        }

        ControlOperationResult cleared = await context.Runtime.SetFanControlOverrideAsync(null, null, default);

        Assert.Equal(ControlOperationStatus.Stored, cleared.Status);
        Assert.Equal("Override cleared. Power-profile application waits for adapter classification.", cleared.Message);
    }
}
