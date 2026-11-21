using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The scope of the mode-7 battery handoff: only a confirmed battery exit to a
// battery preset the table doesn't list retires as a handoff; every other restore
// keeps its normal path.
public sealed partial class ServiceControlRuntimeTests
{
    // Each state guard of the battery retirement, alone, keeps the journal pending.
    [Theory]
    [InlineData("fixedFans")]
    [InlineData("noPerformance")]
    [InlineData("pausedTable")]
    public async Task RealModeSevenTable_BatteryRetirementRequiresEachStateGuard(string state)
    {
        const int productId = 0x029F;
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenRestorePendingOnBatteryAsync(productId, failFirstClear: false);
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities full = context.Device.Capabilities;
        ConfirmedDeviceState handoff = context.Device.ConfirmedState;
        context.Device.ConfirmedState = state switch
        {
            "fixedFans" => handoff with { Fans = FanMode.Fixed },
            "noPerformance" => handoff with { Performance = null },
            _ => handoff,
        };
        if (state == "pausedTable")
        {
            context.Device.Capabilities = ExperimentalBladePort.CreateCapabilities(model, false);
            Assert.False(HyperBoostCapabilityPolicy.CanExit(context.Device.Capabilities));
        }
        int setters = context.Device.TotalSetterCalls;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.NotNull(journal.Journal);
        Assert.True(context.Runtime.GetStatus().HyperBoostRestorePending);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(setters, context.Device.TotalSetterCalls);

        // The same journal retires once the confirmed state and full table allow it.
        context.Device.Capabilities = full;
        context.Device.ConfirmedState = handoff;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Null(journal.Journal);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    // The explicit battery recovery retires the journal only from a fresh read that
    // confirms the handoff itself.
    [Fact]
    public async Task RealModeSevenTable_BatteryRecoveryRequiresFreshHandoffRead()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRealModeSevenRestorePendingOnBatteryAsync(0x029F, failFirstClear: false);
        ConfirmedDeviceState handoff = context.Device.ConfirmedState;
        context.Device.ConfirmedState = handoff with { Fans = FanMode.Fixed };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.NotNull(journal.Journal);
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult refused = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, refused.Status);
        Assert.NotNull(journal.Journal);
        Assert.Equal(setters, context.Device.TotalSetterCalls);

        context.Device.ConfirmedState = handoff;
        ControlOperationResult retired = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, retired.Status);
        Assert.Null(journal.Journal);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    // A deferred restore of the current destination on AC restores that destination
    // (the plugged-in Balanced profile) instead of retiring as a handoff.
    [Fact]
    public async Task RealModeSevenTable_DeferredCurrentDestinationRestoreOnAcAppliesPluggedInProfile()
    {
        const int productId = 0x029F;
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
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
                RecoveryBaseline = new HyperBoostRecoveryBaseline(
                    PerformancePreset.BatteryBalanced, FanMode.Automatic, HyperBoostActive: false),
                RestoreRetryDisposition = HyperBoostRestoreRetryDisposition.DeferredPreSubmission,
                RestoreRetryTarget = HyperBoostRestoreRetryTarget.CurrentPowerDestination,
                RestoreUsesCurrentPowerDestination = true,
                RestorePowerSource = PowerSource.Battery,
            },
        };
        TestContext context = CreateRealModeSevenAcJournalContext(capabilities, journal);

        // The first observation publishes the table; the deferred retry then runs
        // once through its own fresh admission.
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.True(journal.Journal is null,
            context.Runtime.GetStatus().Message + " writes=" + string.Join(',', context.Device.PerformanceWrites)
            + " blocked=" + context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal([PerformancePreset.Balanced], context.Device.PerformanceWrites);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        Assert.DoesNotContain("isn't available", context.Runtime.GetStatus().Message, StringComparison.Ordinal);
    }

    // A battery-intent journal found on AC is recovered explicitly to the current AC
    // destination, as before the handoff existed.
    [Fact]
    public async Task RealModeSevenTable_BatteryIntentJournalOnAcRecoversToPluggedInProfile()
    {
        const int productId = 0x029F;
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
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
        };
        TestContext context = CreateRealModeSevenAcJournalContext(capabilities, journal);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.NotNull(journal.Journal);
        Assert.Empty(context.Device.PerformanceWrites);

        ControlOperationResult recovery = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);

        Assert.True(recovery.Status == ControlOperationStatus.Applied, recovery.Message);
        Assert.Null(journal.Journal);
        Assert.Equal([PerformancePreset.Balanced], context.Device.PerformanceWrites);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    // A Fixed-fan AC intent inside the reviewed envelope is never retired because a
    // fresh firmware range is narrower.
    [Fact]
    public async Task RealModeSevenTable_NarrowerObservedFanRangeDoesNotRetireAcIntent()
    {
        const int productId = 0x02B7;
        DeviceCapabilities full = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
        DeviceCapabilities narrower = full with
        {
            ObservedFanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
            {
                [PerformancePreset.Balanced] = new FanSpeedRange(3400, 5000, 100),
            },
        };
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.RestorePending,
                PerformancePreset.Balanced,
                RequiresCoolingPad: false,
                DateTimeOffset.UtcNow)
            {
                RestoreIntent = new HyperBoostRestoreIntent(
                    PerformancePreset.Balanced,
                    CustomPerformance: null,
                    GpuClockTuning: null,
                    new FanControlSettings(FanMode.Fixed, 5200, ManualFanCurve.Default)),
                RecoveryBaseline = new HyperBoostRecoveryBaseline(
                    PerformancePreset.Balanced, FanMode.Automatic, HyperBoostActive: false),
                RestorePowerSource = PowerSource.PluggedIn,
            },
        };
        TestContext context = CreateRealModeSevenAcJournalContext(full, journal, narrower, PerformancePreset.Balanced);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.NotNull(journal.Journal);
        Assert.True(context.Runtime.GetStatus().HyperBoostRestorePending);
        Assert.DoesNotContain("isn't available", context.Runtime.GetStatus().Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    // A lifecycle exit on battery whose HyperBoost write is indeterminate but applied
    // retires as a handoff, and its history never claims a restore.
    [Theory]
    [InlineData("lowPower")]
    [InlineData("shutdown")]
    public async Task RealModeSevenTable_IndeterminateLifecycleBatteryExitReportsSkippedRestore(string path)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Silent);
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected boundary exit refusal."));
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.Indeterminate,
            "Injected indeterminate exit."));
        context.Device.AfterHyperBoostFailure = () =>
        {
            context.Device.AfterHyperBoostFailure = null;
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.Balanced,
                CustomPerformance = null,
                Fans = FanMode.Automatic,
                HyperBoostActive = false,
            };
        };
        int performanceWrites = context.Device.PerformanceWrites.Count;

        ControlOperationResult result = path == "lowPower"
            ? await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None)
            : await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);

        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Contains("isn't available on this laptop, so it was not applied", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("entry-time profile was restored", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(context.History.Snapshot().CommandResults,
            entry => entry.Detail?.Contains("entry-time profile was restored", StringComparison.Ordinal) == true);
    }

    // Another controller leaves the handoff mode and comes back before AC returns. The
    // first confirmed non-handoff state retires the handoff, with or without the
    // startup reconciliation, so AC return keeps that outside change.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealModeSevenTable_OutsideThereAndBackOnBatteryStaysOutsideAtAcReturn(bool startupReconciled)
    {
        const int productId = 0x02B7;
        (TestContext context, _) = await EnterRealModeSevenHyperBoostAsync(productId, PerformancePreset.Turbo);
        if (startupReconciled)
        {
            await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.BatteryBalanced, context.Device.ConfirmedState.Performance);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.BatteryBalanced };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int performanceWrites = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Equal(PerformancePreset.BatteryBalanced, context.Device.ConfirmedState.Performance);
    }

    // RZ09-0510 lists Battery Balanced, so its raw 0 Balanced is never the handoff.
    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    public async Task Rz090510_OutsideModeAfterDcBalancedHandoffStaysOutsideAtAcReturn(PerformancePreset outside)
    {
        (TestContext context, _) = await EnterRealModeSevenHyperBoostAsync(0x02B7, PerformancePreset.Turbo);
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = outside };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int performanceWrites = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Equal(outside, context.Device.ConfirmedState.Performance);
    }

    // RZ09-0483 still treats its Balanced battery handoff and the DC Balanced reading
    // after AC returns as one mode.
    [Fact]
    public async Task Rz090483_BalancedHandoffReadsAsDcBalancedAfterAcReturn()
    {
        (TestContext context, _) = await EnterRealModeSevenHyperBoostAsync(0x029F, PerformancePreset.Performance);
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        await ReturnRealModeSevenToAcAndReenterAsync(context, 0x029F);
    }

    // The service starts on battery while another controller holds raw 6 and no
    // handoff exists. On RZ09-0483 the DC Balanced reading after AC returns is
    // transitional, so the plugged-in profile applies; a real AC mode stays outside.
    [Theory]
    [InlineData(0x029F, PerformancePreset.Balanced, PerformancePreset.BatteryBalanced, true)]
    [InlineData(0x029F, PerformancePreset.Performance, PerformancePreset.BatteryBalanced, true)]
    [InlineData(0x029D, PerformancePreset.Silent, PerformancePreset.BatteryBalanced, true)]
    [InlineData(0x029F, PerformancePreset.Balanced, PerformancePreset.Silent, false)]
    [InlineData(0x029F, PerformancePreset.Performance, PerformancePreset.Balanced, false)]
    [InlineData(0x02B7, PerformancePreset.Turbo, PerformancePreset.BatteryBalanced, false)]
    public async Task RealModeSevenTable_ColdStartOnBatteryThenAcReturnFromHeldMode(
        int productId,
        PerformancePreset pluggedIn,
        PerformancePreset heldOnAc,
        bool pluggedInApplies)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
        TestContext context = CreateContext(true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: new RecordingHyperBoostOwnershipJournalStore());
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
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        ControlOperationResult updated = await context.Runtime.UpdateProfilesAsync(profiles with
        {
            PluggedIn = profiles.PluggedIn with { Performance = pluggedIn },
        }, CancellationToken.None);
        Assert.True(updated.Status == ControlOperationStatus.Stored, updated.Message);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = heldOnAc };
        int performanceWrites = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        if (pluggedInApplies)
        {
            Assert.Empty(status.ExternalChanges);
            Assert.Equal(pluggedIn, context.Device.ConfirmedState.Performance);
            Assert.Equal([pluggedIn], context.Device.PerformanceWrites.Skip(performanceWrites).ToArray());
        }
        else
        {
            Assert.Contains(DeviceSubsystem.Performance, status.ExternalChanges);
            Assert.Equal(heldOnAc, context.Device.ConfirmedState.Performance);
            Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        }
    }

    // A new Windows user on battery retires the handoff. On RZ09-0483 the AC return
    // then reads transitional DC Balanced and applies the new user's plugged-in
    // profile. RZ09-0510 lists that mode, so its AC return keeps it as an outside
    // change instead (the safe direction: no write).
    [Theory]
    [InlineData(0x029F, PerformancePreset.Performance, true)]
    [InlineData(0x02B7, PerformancePreset.Turbo, false)]
    public async Task RealModeSevenTable_UserChangeOnBatteryAfterHandoffThenAcReturn(
        int productId,
        PerformancePreset firstUserPluggedIn,
        bool secondUserProfileApplies)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
        var validator = ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities);
        var repository = new UserConfigurationRepository(
            Path.Combine(directory, $"handoff-users-{Guid.NewGuid():N}"), validator);
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(firstSid, CancellationToken.None);
        UserConfigurationContext second = await repository.LoadAsync(secondSid, CancellationToken.None);
        await first.Store.SaveProfilesAsync(first.Configuration.Profiles with
        {
            PluggedIn = first.Configuration.Profiles.PluggedIn with { Performance = firstUserPluggedIn },
        }, CancellationToken.None);
        await second.Store.SaveProfilesAsync(second.Configuration.Profiles with
        {
            PluggedIn = second.Configuration.Profiles.PluggedIn with { Performance = PerformancePreset.Silent },
        }, CancellationToken.None);
        TestContext context = CreateContext(true,
            profileValidator: validator,
            userConfigurationRepository: repository,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: new RecordingHyperBoostOwnershipJournalStore());
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = capabilities;
        context.Device.HyperBoostExitPowerSource = () =>
        {
            PowerSource source = context.Telemetry.Current.PowerSource;
            context.Device.ReportsBatteryBalancedForBalancedExit =
                productId == 0x02B7 && source == PowerSource.Battery;
            return source;
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult entered = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (productId != 0x02B7)
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.BatteryBalanced,
            };
        }
        int performanceWrites = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        if (secondUserProfileApplies)
        {
            Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
            Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        }
        else
        {
            Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
            Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        }
    }

    // A journal finalization failure compensates with an exit. Like every exit, it is
    // not refused by an entry restriction that appears after activation.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HyperBoostJournalFinalizationFailure_CompensatingExitIgnoresLateEntryRestriction(
        bool restrictionAppears)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore { FailWriteOnCall = 2 };
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (restrictionAppears)
        {
            journal.AfterWriteFailure = () => RestrictHyperBoostEntry(context);
        }

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("did not leave HyperBoost active", result.Message, StringComparison.Ordinal);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(2, context.Device.HyperBoostSetCalls);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    // On battery, a journal with a deferred retry, or one whose intent is not a
    // battery mode, keeps its restore path instead of retiring as a handoff.
    [Theory]
    [InlineData("deferredRetry")]
    [InlineData("acIntent")]
    public async Task RealModeSevenTable_OnlyABatteryDestinationRetiresOnBattery(string journalKind)
    {
        int productId = journalKind == "acIntent" ? 0x02B7 : 0x029F;
        DeviceCapabilities full = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(item => item.ProductId == productId), true);
        HyperBoostOwnershipJournal pending = journalKind == "acIntent"
            ? new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.RestorePending,
                PerformancePreset.Balanced,
                RequiresCoolingPad: false,
                DateTimeOffset.UtcNow)
            {
                // Inside the reviewed envelope; a narrower observed range makes it
                // unsupported, which is not a battery handoff.
                RestoreIntent = new HyperBoostRestoreIntent(
                    PerformancePreset.Balanced,
                    CustomPerformance: null,
                    GpuClockTuning: null,
                    new FanControlSettings(FanMode.Fixed, 5200, ManualFanCurve.Default)),
                RestorePowerSource = PowerSource.PluggedIn,
            }
            : new HyperBoostOwnershipJournal(
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
                RecoveryBaseline = new HyperBoostRecoveryBaseline(
                    PerformancePreset.Balanced, FanMode.Automatic, HyperBoostActive: false),
                RestoreRetryDisposition = HyperBoostRestoreRetryDisposition.DeferredPreSubmission,
                RestoreRetryTarget = HyperBoostRestoreRetryTarget.CurrentPowerDestination,
                RestoreUsesCurrentPowerDestination = true,
                RestorePowerSource = PowerSource.Battery,
            };
        var journal = new RecordingHyperBoostOwnershipJournalStore { Journal = pending };
        TestContext context = CreateContext(true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(full, full),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = journalKind == "acIntent"
            ? full with
            {
                ObservedFanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
                {
                    [PerformancePreset.Balanced] = new FanSpeedRange(3400, 5000, 100),
                },
            }
            : full;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = productId == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced,
            CustomPerformance = null,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(pending, journal.Journal);
        Assert.True(context.Runtime.GetStatus().HyperBoostRestorePending);
        Assert.DoesNotContain("isn't available", context.Runtime.GetStatus().Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    private TestContext CreateRealModeSevenAcJournalContext(
        DeviceCapabilities validatorCapabilities,
        RecordingHyperBoostOwnershipJournalStore journal,
        DeviceCapabilities? deviceCapabilities = null,
        PerformancePreset reading = PerformancePreset.BatteryBalanced)
    {
        TestContext context = CreateContext(true,
            initialPowerSource: PowerSource.PluggedIn,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                validatorCapabilities, validatorCapabilities),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = deviceCapabilities ?? validatorCapabilities;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = reading,
            CustomPerformance = null,
            Fans = FanMode.Automatic,
            HyperBoostActive = false,
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        return context;
    }
}
