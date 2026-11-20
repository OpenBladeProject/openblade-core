using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// Custom Maximum Fans on the HyperBoost models: RZ09-0482 (029D) and RZ09-0483 (029F).
// The shared coordinator must compare the confirmed flag so that flag-only
// changes and the HyperBoost exit restore reach the device.
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
}
