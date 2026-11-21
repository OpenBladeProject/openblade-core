using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The vendor SKU recheck declines HyperBoost entry only. Every exit, restoration
// and recovery path keeps working while the restriction is reported.
public sealed partial class ServiceControlRuntimeTests
{
    private static void RestrictHyperBoostEntry(TestContext context) =>
        context.Device.Capabilities = context.Device.Capabilities with
        {
            HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
        };

    [Fact]
    public async Task HyperBoostEntryRestriction_BlocksEntryWithItsReason()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRz090510HyperBoostContextAsync(requiresCoolingPad: false);
        RestrictHyperBoostEntry(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(
            ServiceControlRuntime.HyperBoostEntryRestrictionMessage(HyperBoostEntryRestriction.VendorSystemSku),
            result.Message);
        Assert.Equal(0, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.Equal(
            HyperBoostEntryRestriction.VendorSystemSku,
            context.Runtime.GetStatus().Capabilities!.HyperBoostEntryRestriction);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("battery")]
    [InlineData("lowpower")]
    [InlineData("displayoff")]
    [InlineData("shutdown")]
    public async Task HyperBoostEntryRestriction_OwnedSessionStillExitsOnEveryPath(string exitPath)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRz090510HyperBoostContextAsync(requiresCoolingPad: false);
        context.Device.HyperBoostExitPowerSource = () => context.Telemetry.Current.PowerSource;
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        int setCalls = context.Device.HyperBoostSetCalls;

        // A later open reads an excluded SKU while the session is still owned.
        RestrictHyperBoostEntry(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);

        ControlOperationResult? result = exitPath switch
        {
            "user" => await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None),
            "lowpower" => await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None),
            "displayoff" => await context.Runtime.PrepareOwnedHyperBoostForDisplayOffAsync(CancellationToken.None),
            "shutdown" => await context.Runtime.PrepareForShutdownAsync(CancellationToken.None),
            _ => null,
        };
        if (exitPath == "battery")
        {
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        }

        string observed = $"{exitPath}: {result?.Status} '{result?.Message}' status='{context.Runtime.GetStatus().Message}'";
        Assert.True(context.Device.ConfirmedState.HyperBoostActive == false, observed);
        Assert.Equal(setCalls + 1, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.False((context.Runtime.GetStatus().Message ?? string.Empty).Contains("did not match", StringComparison.Ordinal), observed);
    }

    [Fact]
    public async Task HyperBoostEntryRestriction_UnexpectedExitJournalRecoveryStillRestores()
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.OwnedConfirmed,
                PerformancePreset.Balanced,
                RequiresCoolingPad: false,
                DateTimeOffset.UtcNow),
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(
                CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context, active: true);
        RestrictHyperBoostEntry(context);
        BatteryConfiguration configuration = await context.Store.LoadAsync(CancellationToken.None);
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);

        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        Assert.Equal(1, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
    }

    [Theory]
    [InlineData(PerformancePreset.Turbo, false)]
    [InlineData(PerformancePreset.Turbo, true)]
    [InlineData(PerformancePreset.Silent, false)]
    [InlineData(PerformancePreset.Silent, true)]
    [InlineData(PerformancePreset.Balanced, false)]
    [InlineData(PerformancePreset.Balanced, true)]
    public async Task HyperBoostBatteryExit_ConfirmsTheBalancedHandoffFromEveryBase(
        PerformancePreset entryMode, bool reportsBatteryBalanced)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRz090510HyperBoostContextAsync(requiresCoolingPad: false);
        context.Device.ReportsBatteryBalancedForBalancedExit = reportsBatteryBalanced;
        context.Device.HyperBoostExitPowerSource = () => context.Telemetry.Current.PowerSource;
        await AssertBatteryHandoffAsync(context, journal, entryMode);
    }

    private static async Task AssertBatteryHandoffAsync(
        TestContext context,
        RecordingHyperBoostOwnershipJournalStore journal,
        PerformancePreset entryMode)
    {
        await ApplyPluggedInPerformanceAsync(context, entryMode);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        Assert.Equal(entryMode, journal.Journal!.BaseMode);
        int setCalls = context.Device.HyperBoostSetCalls;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        // One exit write; the handoff confirmed, so no failed-transition restore retried it.
        Assert.Equal(setCalls + 1, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.False(status.WritesBlocked, status.Message);
        Assert.Empty(status.UnconfirmedWrites);
        Assert.DoesNotContain("did not match", status.Message ?? string.Empty, StringComparison.Ordinal);
        // The saved profiles are unchanged; the plugged-in one applies again on AC.
        Assert.Equal(entryMode, status.Profiles.PluggedIn.Performance);
        Assert.Equal(PerformancePreset.BatterySaver, status.Profiles.Battery.Performance);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(entryMode, context.Device.ConfirmedState.Performance);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
    }

    // Saves the AC profile's performance and applies it through the typed path.
    private static async Task ApplyPluggedInPerformanceAsync(TestContext context, PerformancePreset performance)
    {
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        CustomPerformanceSettings? custom = performance == PerformancePreset.Custom
            ? profiles.PluggedIn.CustomPerformance ?? CustomPerformanceSettings.Default
            : null;
        Assert.Equal(ControlOperationStatus.Stored,
            (await context.Runtime.UpdateProfilesAsync(profiles with
            {
                PluggedIn = profiles.PluggedIn with
                {
                    Performance = performance,
                    CustomPerformance = custom ?? profiles.PluggedIn.CustomPerformance,
                },
            }, CancellationToken.None)).Status);
        if (context.Device.ConfirmedState.Performance != performance)
        {
            Assert.Equal(ControlOperationStatus.Applied,
                (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                    new PerformanceOverride(performance, OverrideLifetime.UntilPowerSourceChange, custom),
                    CancellationToken.None)).Status);
        }
        Assert.Equal(performance, context.Device.ConfirmedState.Performance);
    }

    [Theory]
    [InlineData(PerformancePreset.Performance, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Performance, PowerSource.PluggedIn, false)]
    // A Custom base hands off too; Balanced carries no Custom levels to compare.
    [InlineData(PerformancePreset.Custom, PowerSource.Battery, true)]
    public async Task HyperBoostRestoreRecovery_AcceptsTheBalancedHandoffOnlyForABatteryExit(
        PerformancePreset ownedBase, PowerSource exitPowerSource, bool matchesTransition)
    {
        // A restore left pending after the exit landed: fresh readback shows
        // Balanced with Automatic fans rather than the owned base.
        var journal = new RecordingHyperBoostOwnershipJournalStore
        {
            Journal = CreateRestorePendingJournal(FanControlSettings.Default) with
            {
                BaseMode = ownedBase,
                RestorePowerSource = exitPowerSource,
            },
        };
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);

        ControlOperationResult result = await context.Runtime.RecoverHyperBoostRestoreAsync(CancellationToken.None);

        if (matchesTransition)
        {
            // The battery handoff is the expected exit, so the current AC destination is restored.
            Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
            Assert.Equal(
                context.Runtime.GetStatus().Profiles.PluggedIn.Performance,
                context.Device.ConfirmedState.Performance);
            Assert.False(context.Device.ConfirmedState.HyperBoostActive);
            Assert.Null(journal.Journal);
        }
        else
        {
            // On AC the exit must land on the owned base; Balanced is not proof of it.
            Assert.Equal(ControlOperationStatus.Blocked, result.Status);
            Assert.Contains("no longer matches", result.Message, StringComparison.Ordinal);
            Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
            Assert.NotNull(journal.Journal);
        }
    }
}
