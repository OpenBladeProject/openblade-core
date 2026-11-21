using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Built on each mode-7 model's own capability table and the stored default
// profiles. The default battery profile (Battery Saver) is in none of these
// tables, so a battery exit hands off to Balanced without a restore write and
// keeps that saved profile. The exit command is never gated on it.
public sealed partial class ServiceControlRuntimeTests
{
    private static readonly (int ProductId, PerformancePreset Base)[] ModeSevenEntryBases =
    [
        (0x029F, PerformancePreset.Silent),
        (0x029F, PerformancePreset.Balanced),
        (0x029F, PerformancePreset.Performance),
        (0x029F, PerformancePreset.Custom),
        (0x029D, PerformancePreset.Silent),
        (0x029D, PerformancePreset.Balanced),
        (0x029D, PerformancePreset.Custom),
        (0x02B7, PerformancePreset.Silent),
        (0x02B7, PerformancePreset.Balanced),
        (0x02B7, PerformancePreset.Turbo),
        (0x02B7, PerformancePreset.Custom),
    ];

    public static TheoryData<int, PerformancePreset> RealModeSevenEntryBases()
    {
        var data = new TheoryData<int, PerformancePreset>();
        foreach ((int productId, PerformancePreset entryBase) in ModeSevenEntryBases)
        {
            data.Add(productId, entryBase);
        }
        return data;
    }

    public static TheoryData<int, PerformancePreset, string> RealModeSevenBatteryExits()
    {
        var data = new TheoryData<int, PerformancePreset, string>();
        foreach ((int productId, PerformancePreset entryBase) in ModeSevenEntryBases)
        {
            foreach (string path in new[] { "user", "lowPower", "shutdown" })
            {
                data.Add(productId, entryBase, path);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RealModeSevenEntryBases))]
    public async Task RealModeSevenTable_BatteryBoundaryHandsOffAndKeepsTheSavedProfile(
        int productId, PerformancePreset entryBase)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(productId, entryBase);
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        AssertRealModeSevenBatteryHandoff(context, journal, productId, setCalls + 1, performanceWrites);

        // When AC returns the plugged-in profile applies again; the owned base is
        // not carried over, and a later entry works.
        await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
    }

    [Theory]
    [MemberData(nameof(RealModeSevenBatteryExits))]
    public async Task RealModeSevenTable_EveryBatteryExitHandsOffWithoutARestore(
        int productId, PerformancePreset entryBase, string exitPath)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await EnterRealModeSevenHyperBoostAsync(productId, entryBase);
        // The boundary exit is refused before submission, so HyperBoost is still
        // on while the laptop runs on battery.
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected boundary exit refusal."));
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
        int setCalls = context.Device.HyperBoostSetCalls;
        int performanceWrites = context.Device.PerformanceWrites.Count;

        ControlOperationResult result = exitPath switch
        {
            "user" => await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None),
            "lowPower" => await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None),
            _ => await context.Runtime.PrepareForShutdownAsync(CancellationToken.None),
        };

        AssertRealModeSevenBatteryHandoff(context, journal, productId, setCalls + 1, performanceWrites);
        if (exitPath == "user")
        {
            Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
            Assert.Contains("Battery Saver setting isn't available", result.Message, StringComparison.Ordinal);
            Assert.Equal(context.Device.ConfirmedState.Performance, result.HyperBoostRestoredPerformance);
            await ReturnRealModeSevenToAcAndReenterAsync(context, productId);
        }
    }

    private async Task<(TestContext Context, RecordingHyperBoostOwnershipJournalStore Journal)>
        EnterRealModeSevenHyperBoostAsync(int productId, PerformancePreset entryBase)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        Assert.DoesNotContain(PerformancePreset.BatterySaver, capabilities.PerformancePresets);
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities, capabilities),
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        context.Device.Capabilities = capabilities;
        context.Device.HyperBoostExitPowerSource = () =>
        {
            PowerSource source = context.Telemetry.Current.PowerSource;
            // 02B7 confirms the DC raw 6 handoff as its own Battery Balanced preset.
            context.Device.ReportsBatteryBalancedForBalancedExit =
                productId == 0x02B7 && source == PowerSource.Battery;
            return source;
        };
        // Mirrors the port's Custom baseline gate: raw 6 (Battery Balanced) is a
        // Custom baseline only off battery on these three models.
        context.Device.PerformancePrecondition = value =>
            value == PerformancePreset.Custom
            && context.Device.ConfirmedState.Performance == PerformancePreset.BatteryBalanced
            && context.Telemetry.Current.PowerSource == PowerSource.Battery
                ? new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental custom performance requires an ordinary AC preset and a model-supported fan baseline.")
                : null;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            PerformancePreset.BatterySaver,
            context.Runtime.GetStatus().Profiles.Battery.Performance);

        await ApplyPluggedInPerformanceAsync(context, entryBase);
        ControlOperationResult entered = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(entered.Status == ControlOperationStatus.Applied, entered.Message);
        Assert.Equal(entryBase, journal.Journal!.BaseMode);
        return (context, journal);
    }

    private static void AssertRealModeSevenBatteryHandoff(
        TestContext context,
        RecordingHyperBoostOwnershipJournalStore journal,
        int productId,
        int expectedSetCalls,
        int performanceWrites)
    {
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(
            productId == 0x02B7 ? PerformancePreset.BatteryBalanced : PerformancePreset.Balanced,
            context.Device.ConfirmedState.Performance);
        Assert.Equal(expectedSetCalls, context.Device.HyperBoostSetCalls);
        // Battery Saver is not in the table, so nothing restores it after the handoff.
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Null(journal.Journal);
        Assert.False(status.WritesBlocked, status.Message);
        Assert.Empty(status.UnconfirmedWrites);
        // The handoff is OpenBlade's own result, not an outside change.
        Assert.DoesNotContain(DeviceSubsystem.Performance, status.ExternalChanges);
        Assert.Equal(PerformancePreset.BatterySaver, status.Profiles.Battery.Performance);
    }

    // A stale published status cannot get a bridge write past a restricted entry:
    // every step re-reads the capability table before submission.
    [Fact]
    public async Task HyperBoostEntryRestriction_StaleStatusSubmitsNoBridgeWrite()
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
        Assert.Null(context.Runtime.GetStatus().Capabilities!.HyperBoostEntryRestriction);
        // The published status stays unrestricted; every fresh capability read is restricted.
        RestrictHyperBoostEntry(context);
        int fanWrites = context.Device.FanSetCalls;
        int performanceWrites = context.Device.PerformanceSetCalls;

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Contains("Synapse doesn't offer HyperBoost", result.Message, StringComparison.Ordinal);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(performanceWrites, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.HyperBoostSetCalls);
        Assert.Equal(FanMode.Fixed, context.Device.ConfirmedState.Fans);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Null(journal.Journal);
    }

    // The one fresh-session retry re-reads the table too, so a restriction that
    // appears after a refused first attempt stops the entry.
    [Fact]
    public async Task HyperBoostEntryRestriction_BlocksTheEntryRetry()
    {
        TestContext? retryContext = null;
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal,
            transitionRetryDelay: (_, _) =>
            {
                RestrictHyperBoostEntry(retryContext!);
                return Task.CompletedTask;
            });
        retryContext = context;
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            BladeWriteFailurePhase.ThermalPreWriteRead,
            "Injected HyperBoost pre-write failure."));

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Contains("Synapse doesn't offer HyperBoost", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, context.Device.HyperBoostSetCalls);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
    }
}
