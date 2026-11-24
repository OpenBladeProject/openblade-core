using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    // The tray uses the service's confirmed-external-power predicate, which
    // includes the limited-adapter UsbC profile. Synapse hides its panel on low
    // power too; OpenBlade documents that difference. Battery hides the control.
    [Theory]
    [InlineData(PowerSource.PluggedIn, true)]
    [InlineData(PowerSource.UsbC, true)]
    [InlineData(PowerSource.Battery, true)]
    [InlineData(PowerSource.UsbC, false)]
    public async Task GpuTuning_PresentationFollowsConfirmedExternalPower(PowerSource source, bool writable)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        // The RZ09-0529/RZ09-0530 policy envelope.
        var policy = new GpuOverclockCapabilities(
            new DeviceNumericRange(-1000, 400, 1),
            new DeviceNumericRange(-1000, 3000, 1),
            DeviceFeatureAccess.ReadWrite)
        {
            SupportsEnableDisable = true,
        };
        PowerProfile profile = source switch
        {
            PowerSource.UsbC => baseline.Profiles!.UsbC,
            PowerSource.Battery => baseline.Profiles!.Battery,
            _ => baseline.Profiles!.PluggedIn,
        };
        client.Publish(baseline with
        {
            ActivePowerSource = source,
            Capabilities = baseline.Capabilities! with
            {
                GpuOverclock = writable ? policy : null,
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = profile.Performance,
                Fans = FanMode.Automatic,
                HyperBoostActive = false,
                CustomPerformance = null,
                GpuClockOffsets = null,
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    profile.Performance,
                    SettingValueSource.PowerProfile),
                CustomPerformance = null,
                GpuClockTuning = null,
            },
        });
        bool expectedVisible = source != PowerSource.Battery;
        bool expectedToggle = expectedVisible && writable;
        await WaitUntilAsync(() => viewModel.IsConnected
            && viewModel.ActivePowerSource == source
            && viewModel.IsGpuClockTuningVisible == expectedVisible
            && viewModel.CanToggleGpuClockTuning == expectedToggle);

        if (expectedToggle)
        {
            await viewModel.SetGpuClockTuningEnabledAsync(enabled: true, CancellationToken.None);
            Assert.Equal(
                new GpuClockTuningSettings(true, GpuClockOffsetSettings.Default),
                client.PerformanceOverride?.GpuClockTuning);
        }
        else
        {
            Assert.Equal(0, client.MutationCalls);
        }
    }
}
