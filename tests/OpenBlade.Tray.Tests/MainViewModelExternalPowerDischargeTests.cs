using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task ExternalDischargeUsesFreshBatteryOnlyObservationsAndClearsOnRecovery(PowerSource source)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(NotificationTestTime);
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        ServiceStatusSnapshot status = CreateLiveStatus() with
        {
            ActivePowerSource = source,
            LiveReadings = new LiveReadings(-5000, null, null),
        };
        var notifications = await ConnectAsync(viewModel, client, dispatcher, status);
        TrayIconState baseState = viewModel.TrayIconState;
        foreach (TrayHoverLabelItemOption option in viewModel.TrayHoverLabelItems)
        {
            option.IsEnabled = false;
        }
        Assert.True(viewModel.RequiresExternalPowerDischargeObservation);
        Drain(notifications);
        for (int seconds = 0; seconds <= 30; seconds += 2)
        {
            if (seconds > 0)
            {
                time.Advance(TimeSpan.FromSeconds(2));
            }
            await ObserveAsync(client,
                () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
            Assert.Equal(seconds == 30, viewModel.IsExternalPowerDischarging);
        }
        Assert.All(client.RequestedLiveReadings, sensors => Assert.Equal(LiveReadingSensors.BatteryPower, sensors));
        Assert.Equal(baseState, viewModel.TrayIconState);
        Assert.Contains(nameof(MainViewModel.IsExternalPowerDischarging), Drain(notifications));
        Assert.EndsWith("-discharging-light.ico", TrayIconAssetPath.Resolve(viewModel.TrayIconState, true, true));
        Assert.Equal("Plugged in, battery discharging", viewModel.TrayHoverLabelText);
        Assert.Equal(1, viewModel.ExternalPowerDischargeEpisode);
        await PushAsync(client, dispatcher, status with { Revision = status.Revision + 1, DeviceVerified = false });
        Assert.Equal(TrayIconState.GenericError, viewModel.TrayIconState);
        Assert.NotEqual("Plugged in, battery discharging", viewModel.TrayHoverLabelText);
        Assert.Equal(1, viewModel.ExternalPowerDischargeEpisode);
        status = status with { Revision = status.Revision + 2 };
        await PushAsync(client, dispatcher, status);
        Assert.Equal(baseState, viewModel.TrayIconState);
        if (source == PowerSource.PluggedIn)
        {
            status = status with
            {
                Revision = status.Revision + 1,
                ConfirmedState = status.ConfirmedState! with { Performance = PerformancePreset.Performance },
            };
            await PushAsync(client, dispatcher, status);
            Assert.True(viewModel.IsExternalPowerDischarging);
            Assert.Equal(TrayIconState.AcPerformance, viewModel.TrayIconState);
            Assert.Equal(Path.Combine("Tray", "ac-performance-discharging-light.ico"),
                TrayIconAssetPath.Resolve(viewModel.TrayIconState, true, viewModel.IsExternalPowerDischarging));
            Assert.Equal(1, viewModel.ExternalPowerDischargeEpisode);
        }
        status = status with { Revision = status.Revision + 1, LiveReadings = new LiveReadings(3000, null, null) };
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.False(viewModel.IsExternalPowerDischarging);
        Assert.DoesNotContain("-discharging-", TrayIconAssetPath.Resolve(
            viewModel.TrayIconState, true, viewModel.IsExternalPowerDischarging));
        Assert.Equal(1, viewModel.ExternalPowerDischargeEpisode);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task SubscriptionSnapshotsDoNotConfirmDischargeAndOffStopsBatteryObservation()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(NotificationTestTime);
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        ServiceStatusSnapshot status = CreateLiveStatus() with
        {
            ActivePowerSource = PowerSource.UsbC,
            LiveReadings = new LiveReadings(-5000, null, null),
        };
        await ConnectAsync(viewModel, client, dispatcher, status);
        for (int i = 1; i <= 8; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await PushAsync(client, dispatcher, status with { Revision = status.Revision + i });
        }
        Assert.False(viewModel.IsExternalPowerDischarging);
        Assert.Equal(0, viewModel.ExternalPowerDischargeEpisode);
        viewModel.LiveReadingsEnabled = false;
        Assert.False(viewModel.RequiresExternalPowerDischargeObservation);
        await viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None);
        Assert.Equal(0, client.ObservationCalls);
    }
}
