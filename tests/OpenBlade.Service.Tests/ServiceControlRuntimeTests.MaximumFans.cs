using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// Custom Maximum Fans on the HyperBoost models: RZ09-0482 (029D) and RZ09-0483 (029F).
// The shared coordinator must compare the confirmed flag so that flag-only
// changes and the HyperBoost exit restore reach the device. A known refusal of
// that flag write must not leave HyperBoost recovery blocked.
public sealed partial class ServiceControlRuntimeTests
{
    private async Task<(TestContext Context, RecordingHyperBoostOwnershipJournalStore Journal)>
        CreateMaximumFansHyperBoostContextAsync(bool blade14)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateBlade14HyperBoostContextAsync(requiresCoolingPad: false);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            PerformancePresets = blade14
                ? context.Device.Capabilities.PerformancePresets
                : new HashSet<PerformancePreset>
                {
                    PerformancePreset.Balanced,
                    PerformancePreset.Silent,
                    PerformancePreset.Performance,
                    PerformancePreset.Custom,
                },
            HyperBoostBaseModes = blade14
                ? context.Device.Capabilities.HyperBoostBaseModes
                : new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite)
                {
                    Custom = DeviceFeatureAccess.ReadWrite,
                },
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceSettings.Default.Cpu, CustomPerformanceSettings.Default.Gpu),
                })
            {
                SupportsMaximumFans = true,
            },
        };
        context.Device.ClearsMaximumFansForHyperBoost = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        return (context, journal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaximumFans_HyperBoostExitFromCustomRestoresTheFlag(bool blade14)
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateMaximumFansHyperBoostContextAsync(blade14);
        CustomPerformanceSettings custom = CustomPerformanceSettings.Default with { MaximumFans = true };
        ControlOperationResult selected = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, custom),
            CancellationToken.None);
        Assert.True(selected.Status == ControlOperationStatus.Applied, selected.Message);
        Assert.Equal(custom, context.Device.ConfirmedState.CustomPerformance);

        ControlOperationResult enabled = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(enabled.Status == ControlOperationStatus.Applied, enabled.Message);
        Assert.Equal(custom, journal.Journal!.RestoreIntent.CustomPerformance);
        Assert.False(context.Device.ConfirmedState.CustomPerformance?.MaximumFans == true);

        ControlOperationResult disabled = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        Assert.True(disabled.Status == ControlOperationStatus.Applied, disabled.Message);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(custom, context.Device.ConfirmedState.CustomPerformance);
        Assert.Equal(custom, context.Runtime.GetStatus().ConfirmedState?.CustomPerformance);
        Assert.Null(journal.Journal);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task MaximumFans_FlagOnlyChangeInCustomReachesDevice(bool blade14, bool before, bool after)
    {
        (TestContext context, _) = await CreateMaximumFansHyperBoostContextAsync(blade14);
        CustomPerformanceSettings initial = CustomPerformanceSettings.Default with { MaximumFans = before };
        ControlOperationResult selected = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, initial),
            CancellationToken.None);
        Assert.True(selected.Status == ControlOperationStatus.Applied, selected.Message);
        Assert.Equal(initial, context.Device.ConfirmedState.CustomPerformance);
        int writes = context.Device.PerformanceWrites.Count;

        CustomPerformanceSettings requested = initial with { MaximumFans = after };
        ControlOperationResult toggled = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, requested),
            CancellationToken.None);

        Assert.True(toggled.Status == ControlOperationStatus.Applied, toggled.Message);
        Assert.Equal(writes + 1, context.Device.PerformanceWrites.Count);
        Assert.Equal(requested, context.Device.ConfirmedState.CustomPerformance);
        Assert.Equal(requested, context.Runtime.GetStatus().ConfirmedState?.CustomPerformance);
    }

    [Fact]
    public async Task MaximumFans_RefusedFlagRestoreAtHyperBoostExitKeepsWritesOpen()
    {
        // The exit's flag write is refused before submission, for example while
        // CPU Boost is on. Mode 7 left and Custom is confirmed, so nothing is
        // ambiguous: retire the journal and keep the saved intent for a reapply.
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateMaximumFansHyperBoostContextAsync(blade14: true);
        CustomPerformanceSettings custom = CustomPerformanceSettings.Default with { MaximumFans = true };
        ControlOperationResult selected = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.Persistent, custom),
            CancellationToken.None);
        Assert.True(selected.Status == ControlOperationStatus.Applied, selected.Message);
        ControlOperationResult enabled = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(enabled.Status == ControlOperationStatus.Applied, enabled.Message);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "OpenBlade won't change maximum fans because it would also change other laptop settings that are on (CPU boost)."));

        ControlOperationResult disabled = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Applied, disabled.Status);
        Assert.Equal("HyperBoost is off. Maximum Fans was not turned back on; reapply Custom to restore it.", disabled.Message);
        Assert.Null(journal.Journal);
        Assert.False(status.WritesBlocked);
        Assert.False(context.Device.ConfirmedState.HyperBoostActive);
        Assert.Equal(PerformancePreset.Custom, context.Device.ConfirmedState.Performance);
        Assert.Equal(custom with { MaximumFans = false }, status.ConfirmedState?.CustomPerformance);
        Assert.Equal(custom, status.DesiredState.CustomPerformance);

        for (int index = 0; index < 3; index++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult reapplied = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.Persistent, custom),
            CancellationToken.None);
        Assert.True(reapplied.Status == ControlOperationStatus.Applied, reapplied.Message);
        Assert.Equal(custom, context.Device.ConfirmedState.CustomPerformance);
    }

    [Fact]
    public async Task MaximumFans_IndeterminateFlagRestoreAtHyperBoostExitStaysBlocked()
    {
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateMaximumFansHyperBoostContextAsync(blade14: true);
        CustomPerformanceSettings custom = CustomPerformanceSettings.Default with { MaximumFans = true };
        ControlOperationResult selected = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.Persistent, custom),
            CancellationToken.None);
        Assert.True(selected.Status == ControlOperationStatus.Applied, selected.Message);
        ControlOperationResult enabled = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);
        Assert.True(enabled.Status == ControlOperationStatus.Applied, enabled.Message);
        context.Device.PerformanceFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.Indeterminate, "The Maximum Fans write timed out."));

        ControlOperationResult disabled = await context.Runtime.SetHyperBoostAsync(false, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, disabled.Status);
        Assert.DoesNotContain("reapply Custom", disabled.Message, StringComparison.Ordinal);
        Assert.Equal(HyperBoostOwnershipJournalState.RestorePending, journal.Journal?.State);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task MaximumFans_KnownHyperBoostEntryStopAfterCleanupIsNotAnExternalChange()
    {
        // The port turned Maximum Fans off and then stopped before mode 7.
        // The confirmed flag follows that readback, so later observations do
        // not blame another controller, and reapplying Custom turns it on.
        (TestContext context, RecordingHyperBoostOwnershipJournalStore journal) =
            await CreateMaximumFansHyperBoostContextAsync(blade14: true);
        CustomPerformanceSettings custom = CustomPerformanceSettings.Default with { MaximumFans = true };
        ControlOperationResult selected = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, custom),
            CancellationToken.None);
        Assert.True(selected.Status == ControlOperationStatus.Applied, selected.Message);
        context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            $"OpenBlade can no longer confirm the exact admitted laptop identity. {ExperimentalBladePort.MaximumFansClearedForHyperBoostNotice}")
        {
            MaximumFansClearedBeforeFailure = true,
        });
        context.Device.AfterHyperBoostFailure = () => context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            CustomPerformance = custom with { MaximumFans = false },
        };

        ControlOperationResult entered = await context.Runtime.SetHyperBoostAsync(true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, entered.Status);
        Assert.EndsWith(ExperimentalBladePort.MaximumFansClearedForHyperBoostNotice, entered.Message, StringComparison.Ordinal);
        Assert.Null(journal.Journal);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(custom with { MaximumFans = false }, context.Runtime.GetStatus().ConfirmedState?.CustomPerformance);
        for (int index = 0; index < 3; index++)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult reapplied = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(PerformancePreset.Custom, OverrideLifetime.UntilPowerSourceChange, custom),
            CancellationToken.None);
        Assert.True(reapplied.Status == ControlOperationStatus.Applied, reapplied.Message);
        Assert.Equal(custom, context.Device.ConfirmedState.CustomPerformance);
    }
}
