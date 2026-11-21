using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Restore-only paths after a battery exit on the real mode-7 tables. The default
// battery profile (Battery Saver) is in none of them, so each path retires its
// journal as a handoff instead of a restore that can never run, and never
// replays the exit.
public sealed partial class ServiceControlRuntimeTests
{
    public static TheoryData<int, PerformancePreset, string> RealModeSevenIndeterminateExits()
    {
        var data = new TheoryData<int, PerformancePreset, string>();
        foreach ((int productId, PerformancePreset entryBase) in ModeSevenEntryBases)
        {
            data.Add(productId, entryBase, "boundary");
            data.Add(productId, entryBase, "user");
        }
        return data;
    }

    // Firmware left mode 7 before OpenBlade observed the battery boundary.
    [Theory]
    [MemberData(nameof(RealModeSevenEntryBases))]
    public async Task RealModeSevenTable_FirmwarePreExitAtBatteryBoundaryRetiresAsHandoff(
        int productId, PerformancePreset entryBase)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(productId, entryBase);
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = productId == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced,
            CustomPerformance = null,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        // Nothing is submitted: the exit already happened.
        AssertRealModeSevenBatteryHandoff(context, journal, productId, setCalls, performanceWrites);
        await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
    }

    // An indeterminate exit that a fresh read shows did apply.
    [Theory]
    [MemberData(nameof(RealModeSevenIndeterminateExits))]
    public async Task RealModeSevenTable_AppliedIndeterminateBatteryExitRetiresAsHandoff(
        int productId, PerformancePreset entryBase, string exitPath)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(productId, entryBase);
        if (exitPath == "user")
        {
            context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Injected boundary exit refusal."));
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
            Assert.True(context.Device.ConfirmedState.HyperBoostActive);
        }
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.Indeterminate,
            "Injected indeterminate exit."));
        context.Device.AfterHyperBoostFailure = () =>
        {
            context.Device.AfterHyperBoostFailure = null;
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Performance = productId == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced,
                CustomPerformance = null,
                Fans = FanMode.Automatic,
                HyperBoostActive = false,
            };
        };

        if (exitPath == "user")
        {
            ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);
            Assert.Contains("isn't available on this laptop", result.Message, StringComparison.Ordinal);
        }
        else
        {
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        }
        await context.Runtime.ObserveAsync(CancellationToken.None);

        // The uncertain exit is never replayed.
        AssertRealModeSevenBatteryHandoff(context, journal, productId, setCalls + 1, performanceWrites);
        await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
    }

    // A RestorePending journal whose battery intent can never be restored retires
    // on the next confirmed read, without a write.
    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02B7)]
    public async Task RealModeSevenTable_RestorePendingBatteryIntentRetiresOnObservation(int productId)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenRestorePendingOnBatteryAsync(productId, failFirstClear: false);
        int setters = context.Device.TotalSetterCalls;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Null(journal.Journal);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(status.WritesBlocked, status.Message);
        Assert.False(status.HyperBoostRestorePending);
        Assert.DoesNotContain(DeviceSubsystem.Performance, status.ExternalChanges);
        Assert.Equal(PerformancePreset.BatterySaver, status.Profiles.Battery.Performance);
        await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
    }

    // The explicit recovery on battery retires the same journal as a handoff
    // instead of refusing to run until external power returns.
    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02B7)]
    public async Task RealModeSevenTable_RecoveryOnBatteryRetiresUnadmittedIntentAsHandoff(int productId)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenRestorePendingOnBatteryAsync(productId, failFirstClear: true);
        Assert.NotNull(journal.Journal);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        journal.FailClear = false;
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult result = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);

        Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
        Assert.Contains("Battery Saver setting isn't available", result.Message, StringComparison.Ordinal);
        Assert.Null(journal.Journal);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
    }

    // The handoff covers its own transition and the next one only. After AC
    // returns, an outside change latches exactly as it does without HyperBoost.
    [Fact]
    public async Task HyperBoostHandoff_IsConsumedByAcReturnLikeTheControl()
    {
        IReadOnlyList<string> withHandoff = await RunOutsideBalancedAfterAcReturnAsync(exitOnAcFirst: false);
        IReadOnlyList<string> control = await RunOutsideBalancedAfterAcReturnAsync(exitOnAcFirst: true);

        Assert.Equal(control, withHandoff);
        Assert.Equal("Performance", withHandoff[^2]);
        Assert.Equal("Performance", withHandoff[^1]);
    }

    // A different outside mode after the handoff is not the handoff.
    [Fact]
    public async Task HyperBoostHandoff_DoesNotCoverADifferentOutsideMode()
    {
        (TestContext context, _) = await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Performance);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        int performanceWrites = context.Device.PerformanceWrites.Count;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
    }

    // A Custom handoff keeps its levels: a later level change is an outside change.
    [Fact]
    public async Task HyperBoostHandoff_CustomLevelChangeIsNotTheHandoff()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Custom);
        CustomPerformanceSettings intent = context.Runtime.GetStatus().Profiles.PluggedIn.CustomPerformance!;
        var other = new CustomPerformanceSettings(
            intent.Cpu == CustomPerformanceLevel.Low ? CustomPerformanceLevel.High : CustomPerformanceLevel.Low,
            intent.Gpu);
        // The saved Custom levels are no longer admitted, so the AC exit hands
        // off to the owned Custom base without a restore.
        context.Device.Capabilities = context.Device.Capabilities with
        {
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination> { new(other.Cpu, other.Gpu) }),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult exit = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);
        Assert.True(exit.Status == ControlOperationStatus.Applied, exit.Message);
        Assert.Contains("isn't available on this laptop", exit.Message, StringComparison.Ordinal);
        Assert.Null(journal.Journal);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(intent, context.Device.ConfirmedState.CustomPerformance);

        // Another controller changes the Custom levels on AC.
        context.Device.ConfirmedState = context.Device.ConfirmedState with { CustomPerformance = other };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
    }

    // The battery exit's one fresh-session retry keeps the step's purpose, so the
    // exit command stays ungated by the unadmitted battery intent.
    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02B7)]
    public async Task RealModeSevenTable_BatteryExitRetryKeepsTheExitPurpose(int productId)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(productId, PerformancePreset.Silent);
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            BladeWriteFailurePhase.ThermalPreWriteRead,
            "Injected pre-write read failure."));

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        AssertRealModeSevenBatteryHandoff(context, journal, productId, setCalls + 2, performanceWrites);
    }

    // An AC exit still submits the exit command, but its restore step re-reads the
    // table and is refused when the intent is no longer admitted there.
    [Fact]
    public async Task HyperBoostExit_RestoreStepRequiresTheFreshlyAdmittedIntent()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Silent);
        RemoveFreshPerformancePreset(context, PerformancePreset.Silent);
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("no longer admitted", result.Message, StringComparison.Ordinal);
        Assert.Equal(setCalls + 1, context.Device.HyperBoostSetCalls);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Equal(HyperBoostOwnershipJournalState.RestorePending, journal.Journal?.State);
    }

    // An entry's own HyperBoost step re-reads the table: a stale status can't
    // start HyperBoost for an intent the fresh table no longer admits.
    [Fact]
    public async Task HyperBoostEntry_CommandStepRequiresTheFreshlyAdmittedIntent()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenAcContextAsync(0x029F, PerformancePreset.Silent);
        RemoveFreshPerformancePreset(context, PerformancePreset.Silent);
        int fanWrites = context.Device.FanSetCalls;
        int performanceWrites = context.Device.PerformanceSetCalls;

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Contains("no longer admitted", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Device.HyperBoostSetCalls);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(performanceWrites, context.Device.PerformanceSetCalls);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
    }

    // The entry's one retry re-reads the table too.
    [Fact]
    public async Task HyperBoostEntry_RetryRequiresTheFreshlyAdmittedIntent()
    {
        TestContext? retryContext = null;
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenAcContextAsync(0x029F, PerformancePreset.Silent,
                transitionRetryDelay: (_, _) =>
                {
                    RemoveFreshPerformancePreset(retryContext!, PerformancePreset.Silent);
                    return Task.CompletedTask;
                });
        retryContext = context;
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            BladeWriteFailurePhase.ThermalPreWriteRead,
            "Injected HyperBoost pre-write failure."));

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.HyperBoostSetCalls);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
    }

    // A restriction that appears after the first bridge write stops the forward
    // steps only: the rollback completes and leaves writes open.
    [Fact]
    public async Task HyperBoostEntryRestriction_MidEntryRollbackCompletes()
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed },
            FanSpeedRange = new FanSpeedRange(2000, 5000, 100),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Fixed };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        bool restricted = false;
        context.Device.AfterSetter = subsystem =>
        {
            if (!restricted && subsystem == DeviceSubsystem.Fans)
            {
                restricted = true;
                RestrictHyperBoostEntry(context);
            }
        };

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.True(restricted);
        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Contains("Synapse doesn't offer HyperBoost", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Device.HyperBoostSetCalls);
        // The rollback restores the entry-time intent (Automatic fans here).
        Assert.Contains("entry-time intent was restored", result.Message, StringComparison.Ordinal);
        Assert.Equal(context.Runtime.GetStatus().DesiredState.Fans.Value, context.Device.ConfirmedState.Fans);
        Assert.Null(journal.Journal);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(status.WritesBlocked, status.Message);
        Assert.False(status.HyperBoostRestorePending);
    }

    private static void RemoveFreshPerformancePreset(TestContext context, PerformancePreset preset)
    {
        var presets = new HashSet<PerformancePreset>(context.Device.Capabilities.PerformancePresets);
        presets.Remove(preset);
        context.Device.Capabilities = context.Device.Capabilities with { PerformancePresets = presets };
    }

    private async Task<(TestContext Context, RecordingHyperBoostOwnershipJournalStore Journal)>
        CreateRealModeSevenAcContextAsync(
            int productId,
            PerformancePreset performance,
            Func<TimeSpan, CancellationToken, Task>? transitionRetryDelay = null)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal,
            transitionRetryDelay: transitionRetryDelay);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await ApplyPluggedInPerformanceAsync(context, performance);
        return (context, journal);
    }

    private async Task<(TestContext Context, RecordingHyperBoostOwnershipJournalStore Journal)>
        CreateRealModeSevenRestorePendingOnBatteryAsync(int productId, bool failFirstClear)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.RestorePending,
                PerformancePreset.Silent,
                RequiresCoolingPad: false,
                DateTimeOffset.UtcNow)
            {
                RestoreIntent = new HyperBoostRestoreIntent(
                    PerformancePreset.BatterySaver,
                    CustomPerformance: null,
                    GpuClockTuning: null,
                    FanControlSettings.FromLegacy(FanMode.Automatic)),
                RestoreUsesCurrentPowerDestination = true,
                RestorePowerSource = PowerSource.Battery,
            },
            FailClear = failFirstClear,
        };
        TestContext context = CreateContext(true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = productId == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced,
            CustomPerformance = null,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        if (failFirstClear)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        return (context, journal);
    }

    // RZ09-0483 and RZ09-0482 keep raw 6 after external power returns and read it
    // as DC Balanced until a typed AC set leaves it.
    private async Task ReturnRealModeSevenToAcAndReenterAsync(TestContext context, int productId)
    {
        if (productId != 0x02B7)
        {
            Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.BatteryBalanced,
            };
        }

        int performanceWrites = context.Device.PerformanceWrites.Count;
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(status.Profiles.PluggedIn.Performance, context.Device.ConfirmedState.Performance);
        // The plugged-in mode, Custom included, is set directly from raw 6.
        Assert.Equal([status.Profiles.PluggedIn.Performance],
            context.Device.PerformanceWrites.Skip(performanceWrites).ToArray());
        Assert.Empty(status.ExternalChanges);
        Assert.False(status.WritesBlocked, status.Message);
        ControlOperationResult reentered = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(reentered.Status == ControlOperationStatus.Applied, reentered.Message);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
    }

    // Returns the latched outside changes after each stage.
    private async Task<IReadOnlyList<string>> RunOutsideBalancedAfterAcReturnAsync(bool exitOnAcFirst)
    {
        (TestContext context, _) = await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Performance);
        if (exitOnAcFirst)
        {
            ControlOperationResult exit = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);
            Assert.True(exit.Status == ControlOperationStatus.Applied, exit.Message);
        }
        var stages = new List<string>();
        void Record() => stages.Add(string.Join(',', context.Runtime.GetStatus().ExternalChanges));

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Record();
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Record();

        // Another controller selects Balanced with Automatic fans on AC.
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
            CustomPerformance = null,
            Fans = FanMode.Automatic,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Record();
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Record();
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Record();
        return stages;
    }
}
