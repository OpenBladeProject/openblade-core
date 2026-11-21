using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090510HyperBoost_TurboRoundTripPreservesIntent(bool requiresCoolingPad)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRz090510HyperBoostContextAsync(requiresCoolingPad);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(PerformancePreset.Turbo, OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None)).Status);
        int performanceWrites = context.Device.PerformanceWrites.Count;

        ControlOperationResult enabled = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, enabled.Status);
        // Direct entry from the confirmed Turbo mode; no reset to Balanced first.
        Assert.Equal((PerformancePreset.Turbo, FanMode.Automatic), Assert.Single(context.Device.HyperBoostEntryStates));
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Equal(PerformancePreset.Turbo, journal.Journal!.BaseMode);
        Assert.Equal(PerformancePreset.Turbo, journal.Journal.RestoreIntent.Performance);

        ControlOperationResult disabled = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, disabled.Status);
        Assert.Equal(performanceWrites, context.Device.PerformanceWrites.Count);
        Assert.Equal(PerformancePreset.Turbo, context.Device.ConfirmedState.Performance);
        Assert.Equal(PerformancePreset.Turbo, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Equal([requiresCoolingPad, requiresCoolingPad], context.Device.HyperBoostJournaledPadPaths);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Null(journal.Journal);
    }

    [Fact]
    public async Task Rz090510HyperBoost_BatteryExitAcceptsDcBalancedReadback()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateRz090510HyperBoostContextAsync(requiresCoolingPad: false);
        context.Device.ReportsBatteryBalancedForBalancedExit = true;
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None)).Status);
        Assert.Equal(PerformancePreset.Balanced, journal.Journal!.BaseMode);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(2, context.Device.HyperBoostSetCalls);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        // The exit readback matched, so no failed-transition intent restore ran.
        Assert.DoesNotContain("did not match", context.Runtime.GetStatus().Message ?? string.Empty, StringComparison.Ordinal);
    }

    private async Task<(TestContext Context, RecordingHyperBoostOwnershipJournalStore Journal)>
        CreateRz090510HyperBoostContextAsync(bool requiresCoolingPad)
    {
        var journal = new RecordingHyperBoostOwnershipJournalStore();
        TestContext context = CreateContext(true,
            hyperBoostPadDiscoverySource: new FixedCoolingPadDiscoverySource(
                requiresCoolingPad ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent),
            hyperBoostOwnershipJournalStore: journal);
        ConfigurePadlessHyperBoost(context);
        context.Device.UseScopedPerformanceFanRead = true;
        // RZ09-0510 shape: product 695 lists Turbo (raw 1) and DC Balanced, not Performance.
        context.Device.Capabilities = context.Device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Turbo,
                PerformancePreset.Custom,
                PerformancePreset.BatteryBalanced,
                PerformancePreset.BatterySaver,
            },
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly)
            {
                Custom = DeviceFeatureAccess.ReadWrite,
                Turbo = DeviceFeatureAccess.ReadWrite,
                CoolingPadEntryBaseMode = PerformancePreset.Turbo,
            },
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        return (context, journal);
    }
}
