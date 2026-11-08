using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedCoolingPadWireCurvesPreserveTrayOnlyFastPath(bool connected)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(NotificationTestTime);
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        ServiceStatusSnapshot status = CreateLiveStatus() with
        {
            CoolingPad = new()
            {
                Connected = connected,
                Connection = connected ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent,
            },
        };
        ServiceStatusSnapshot initial = await RoundTripStatusAsync(status);
        await ConnectAsync(viewModel, client, dispatcher, initial);
        int padNotifications = 0;
        viewModel.CoolingPad.PropertyChanged += (_, _) => padNotifications++;

        ServiceStatusSnapshot unchanged = await RoundTripStatusAsync(status with
        {
            Revision = status.Revision + 1,
            LiveReadings = new LiveReadings(-6000, 2300, 2400, 48, 50),
        });
        Assert.NotSame(initial.CoolingPad.Requested.CpuCurve, unchanged.CoolingPad.Requested.CpuCurve);
        Assert.NotSame(initial.CoolingPad.Requested.GpuCurve, unchanged.CoolingPad.Requested.GpuCurve);
        Assert.Equal(initial.CoolingPad.Requested.CpuCurve, unchanged.CoolingPad.Requested.CpuCurve);
        Assert.Equal(initial.CoolingPad.Requested.GpuCurve, unchanged.CoolingPad.Requested.GpuCurve);

        await ObserveAsync(client, () => viewModel.ObserveTrayStateAsync(CancellationToken.None), unchanged);
        Assert.Equal(unchanged.LiveReadings, viewModel.LiveReadings);
        Assert.Equal(connected, viewModel.CoolingPad.IsVisible);
        Assert.Equal(0, padNotifications);

        // An actual curve edit must leave the shortcut even when every scalar matches.
        ServiceStatusSnapshot changed = await RoundTripStatusAsync(unchanged with
        {
            Revision = unchanged.Revision + 1,
            CoolingPad = unchanged.CoolingPad with
            {
                Requested = unchanged.CoolingPad.Requested with
                {
                    CpuCurve = [new(50, 550), new(65, 1500), new(80, 2500), new(90, 3200)],
                },
            },
        });
        await ObserveAsync(client, () => viewModel.ObserveTrayStateAsync(CancellationToken.None), changed);
        Assert.True(padNotifications > 0);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task TrayOnlyObservationPublishesCoolingPadOnlyPresenceAndSafetyChanges()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus() with
        {
            ActivePowerSource = PowerSource.PluggedIn,
            CoolingPad = new() { Connection = CoolingPadConnectionState.Absent },
        };
        await ConnectAsync(viewModel, client, dispatcher, status);
        Assert.False(viewModel.CoolingPad.IsVisible);

        status = status with
        {
            Revision = status.Revision + 1,
            CoolingPad = new()
            {
                Connected = true,
                Connection = CoolingPadConnectionState.PresentExact,
                ConfirmedTargetRpm = 1500,
            },
        };
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.True(viewModel.CoolingPad.IsVisible);
        Assert.True(viewModel.CoolingPad.CanApply);
        Assert.Contains("1500", viewModel.CoolingPad.ConfiguredTargetText, StringComparison.Ordinal);

        status = status with
        {
            Revision = status.Revision + 1,
            CoolingPad = status.CoolingPad with { WritesPaused = true, ConfirmedTargetRpm = 2000 },
        };
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.True(viewModel.CoolingPad.IsVisible);
        Assert.False(viewModel.CoolingPad.CanApply);
        Assert.Contains("2000", viewModel.CoolingPad.ConfiguredTargetText, StringComparison.Ordinal);

        status = status with
        {
            Revision = status.Revision + 1,
            CoolingPad = new() { Connection = CoolingPadConnectionState.Absent },
        };
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.False(viewModel.CoolingPad.IsVisible);
        Assert.False(viewModel.CoolingPad.CanApply);
        Assert.Equal(3, client.ObservationCalls);
        Assert.All(client.RequestedLiveReadings, sensors => Assert.Equal(LiveReadingSensors.BatteryPower, sensors));
        Assert.Equal(0, client.MutationCalls);
    }

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
        Assert.False(viewModel.IsExternalPowerDischarging);
        Assert.Equal(0, viewModel.ExternalPowerDischargeEpisode);
        Drain(notifications);
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.True(viewModel.IsExternalPowerDischarging);
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
    public async Task HyperBoostUsbCExitRefreshesCachedDischargeTooltipAndOverflow()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(NotificationTestTime);
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        ServiceStatusSnapshot baseline = CreatePadlessHyperBoostStatus(CreateLiveStatus());
        ServiceStatusSnapshot status = baseline with
        {
            ActivePowerSource = PowerSource.UsbC,
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = null,
                CustomPerformance = null,
                HyperBoostActive = true,
            },
            LiveReadings = new LiveReadings(-18_400, 2300, 2500, 46, 51),
        };
        await ConnectAsync(viewModel, client, dispatcher, status);
        foreach (TrayHoverLabelItemOption option in viewModel.TrayHoverLabelItems)
        {
            option.IsEnabled = true;
        }
        await ObserveAsync(client,
            () => viewModel.ObserveExternalPowerDischargeAsync(CancellationToken.None), status);
        Assert.True(viewModel.IsExternalPowerDischarging);
        Assert.True(viewModel.CanToggleHyperBoost);
        Assert.Equal(TrayIconState.GenericError, viewModel.TrayIconState);
        Assert.True(viewModel.HasTrayHoverLabelOverflow);
        string cachedTooltip = viewModel.TrayHoverLabelText;
        bool cachedOverflow = viewModel.HasTrayHoverLabelOverflow;
        long episode = viewModel.ExternalPowerDischargeEpisode;
        Assert.NotEqual("Plugged in, battery discharging", cachedTooltip);
        // Native consumers cache these values and refresh only on their own
        // property notifications, rather than reevaluating on every icon change.
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TrayHoverLabelText))
            {
                cachedTooltip = viewModel.TrayHoverLabelText;
            }
            else if (args.PropertyName == nameof(MainViewModel.HasTrayHoverLabelOverflow))
            {
                cachedOverflow = viewModel.HasTrayHoverLabelOverflow;
            }
        };

        await viewModel.SetHyperBoostEnabledAsync(false, CancellationToken.None);

        Assert.False(viewModel.HyperBoostActive);
        Assert.Equal(TrayIconState.UsbCBalanced, viewModel.TrayIconState);
        Assert.True(viewModel.IsExternalPowerDischarging);
        Assert.Equal(episode, viewModel.ExternalPowerDischargeEpisode);
        Assert.Equal("Plugged in, battery discharging", viewModel.TrayHoverLabelText);
        Assert.Equal(viewModel.TrayHoverLabelText, cachedTooltip);
        Assert.False(viewModel.HasTrayHoverLabelOverflow);
        Assert.False(cachedOverflow);
        Assert.Equal(["hyperBoost:False"], client.MutationOperations);
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
