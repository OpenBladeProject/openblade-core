using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuTuningStartup_PresentsNonzeroEnabledIntentWithoutToggle(bool delayedRanges)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        var offsets = new GpuClockOffsetSettings(125, 500);
        var ranges = new GpuClockOffsetCapabilities(
            "NVIDIA GPU",
            new GpuClockOffsetRange(-1000, 400, 125),
            new GpuClockOffsetRange(-1000, 3000, 500));
        ServiceStatusSnapshot status = baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                GpuOverclock = new GpuOverclockCapabilities(
                    new DeviceNumericRange(-1000, 400, null),
                    new DeviceNumericRange(-1000, 3000, null),
                    DeviceFeatureAccess.ReadWrite),
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                GpuClockOffsets = delayedRanges ? null : ranges,
            },
            DesiredState = baseline.DesiredState with
            {
                GpuClockTuning = new GpuClockTuningSettings(true, offsets),
            },
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.IsGpuClockTuningEnabled);
        Assert.Equal(offsets, viewModel.CurrentGpuClockOffsets);
        if (delayedRanges)
        {
            Assert.False(viewModel.CanEditGpuClockTuning);
            client.Publish(status with
            {
                Revision = status.Revision + 1,
                ConfirmedState = status.ConfirmedState! with { GpuClockOffsets = ranges },
            });
        }
        await WaitUntilAsync(() => viewModel.CanEditGpuClockTuning);
        Assert.True(viewModel.IsGpuClockTuningEnabled);
        Assert.Equal(offsets, viewModel.CurrentGpuClockOffsets);
        Assert.Equal(-1000, viewModel.GpuCoreClockOffsetMinimum);
        Assert.Equal(400, viewModel.GpuCoreClockOffsetMaximum);
        Assert.Equal(-1000, viewModel.GpuMemoryClockOffsetMinimum);
        Assert.Equal(3000, viewModel.GpuMemoryClockOffsetMaximum);
        Assert.Equal(0, client.MutationCalls);
    }
}
