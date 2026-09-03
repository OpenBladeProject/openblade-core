using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

// Every MainViewModel.PropertyChanged reaches each native XAML {Binding}
// subscriber through a CsWinRT marshal of the sender, so the observation paths
// must announce only properties whose values changed. These tests assert the
// notification names raised per cycle; asserting a bound value never catches a
// missing or a redundant notification.
public sealed partial class MainViewModelTests
{
    private static readonly DateTimeOffset NotificationTestTime =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ObservationCyclesAgainstUnchangedSnapshot_RaiseNoRedundantNotifications()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        var time = new MutableTimeProvider(NotificationTestTime);
        await using var viewModel = new MainViewModel(client, dispatcher, time);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        viewModel.SetDashboardTelemetryPresentationActive(true);
        viewModel.SetTrayDashboardTelemetryPresentationActive(true);
        Drain(notifications);

        // The first visible cycle records a refresh time, so only the tooltip changes.
        await ObserveAsync(client, () => viewModel.ObserveStateAsync(CancellationToken.None), status);
        Assert.Equal(
            [nameof(MainViewModel.LiveReadingsRefreshToolTip)],
            Drain(notifications));

        // A second cycle at the same time changes nothing at all.
        await ObserveAsync(client, () => viewModel.ObserveStateAsync(CancellationToken.None), status);
        Assert.Empty(Drain(notifications));

        await ObserveAsync(client, () => viewModel.ObserveTrayStateAsync(CancellationToken.None), status);
        Assert.Empty(Drain(notifications));

        await ObserveAsync(
            client,
            () => viewModel.ObserveTrayDashboardStateAsync(
                includeMainDashboardReadings: false,
                LiveReadingSensors.All,
                CancellationToken.None),
            status);
        Assert.Empty(Drain(notifications));

        await PushAsync(client, dispatcher, status with { Revision = status.Revision + 1 });
        Assert.Empty(Drain(notifications));

        // The tooltip is still announced once its text changes.
        time.Advance(TimeSpan.FromSeconds(5));
        await ObserveAsync(client, () => viewModel.ObserveStateAsync(CancellationToken.None), status);
        Assert.Equal(
            [nameof(MainViewModel.LiveReadingsRefreshToolTip)],
            Drain(notifications));
    }

    [Fact]
    public async Task ServicePushWithOneChangedField_NotifiesOnlyItsPresentation()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        viewModel.SetDashboardTelemetryPresentationActive(true);
        viewModel.SetTrayDashboardTelemetryPresentationActive(true);
        Drain(notifications);

        await PushAsync(
            client,
            dispatcher,
            status with { Revision = 2, KeyboardDriverModeActive = true });
        List<string> firstPush = Drain(notifications);
        Assert.Contains(nameof(MainViewModel.TroubleshootingKeyboardDriverModeStatus), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.CanEditPerformance), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.HasThermalProfileDifference), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.PluggedInProfileThermalSummary), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.AboutServiceVersionStatus), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.CpuTemperatureValue), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.TrayHoverLabelText), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.LightingBrightnessEditor), firstPush);
        Assert.DoesNotContain(nameof(MainViewModel.CpuFanCurveEditorPoints), firstPush);

        await PushAsync(
            client,
            dispatcher,
            status with { Revision = 3, KeyboardDriverModeActive = true, Message = "Ready." });
        Assert.Equal([nameof(MainViewModel.ConnectionStatus)], Drain(notifications));

        await PushAsync(
            client,
            dispatcher,
            status with { Revision = 4, KeyboardDriverModeActive = false, Message = "Ready." });
        Assert.Equal(
            [nameof(MainViewModel.TroubleshootingKeyboardDriverModeStatus)],
            Drain(notifications));
    }

    [Fact]
    public async Task ChangedLiveReading_NotifiesOnlyThePresentationItFeeds()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        viewModel.SetDashboardTelemetryPresentationActive(true);
        viewModel.SetTrayDashboardTelemetryPresentationActive(true);
        Drain(notifications);

        LiveReadings warmer = status.LiveReadings! with { CpuTemperatureCelsius = 56 };
        await PushAsync(client, dispatcher, status with { Revision = 2, LiveReadings = warmer });
        List<string> changed = Drain(notifications);
        Assert.Contains(nameof(MainViewModel.LiveReadings), changed);
        Assert.Contains(nameof(MainViewModel.CpuTemperatureValue), changed);
        Assert.Contains(nameof(MainViewModel.TrayDashboardTemperatureLabel), changed);
        Assert.Contains(nameof(MainViewModel.VisibleDashboardTelemetryGroups), changed);
        Assert.DoesNotContain(nameof(MainViewModel.GpuTemperatureValue), changed);
        Assert.DoesNotContain(nameof(MainViewModel.Fan1RpmLabel), changed);
        Assert.DoesNotContain(nameof(MainViewModel.BatteryPowerFlowLabel), changed);
        Assert.DoesNotContain(nameof(MainViewModel.TrayDashboardFanSpeedLabel), changed);
        Assert.DoesNotContain(nameof(MainViewModel.HasThermalProfileDifference), changed);

        // Equal readings with a newer revision announce nothing.
        await PushAsync(client, dispatcher, status with { Revision = 3, LiveReadings = warmer });
        Assert.Empty(Drain(notifications));
    }

    [Fact]
    public async Task DependentComputedProperties_NotifyWheneverTheirValueChanges()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        Drain(notifications);
        string[] writeGated =
        [
            nameof(MainViewModel.IsReadOnly),
            nameof(MainViewModel.CanEditPerformance),
            nameof(MainViewModel.CanSelectLaptopPerformance),
            nameof(MainViewModel.CanEditFanControl),
            nameof(MainViewModel.CanChangeFanMode),
            nameof(MainViewModel.CanControlLighting),
            nameof(MainViewModel.CanAdjustChargeLimit),
            nameof(MainViewModel.CanChangeBatteryProtection),
        ];

        await PushAsync(client, dispatcher, status with { Revision = 2, WritesBlocked = true });
        List<string> blocked = Drain(notifications);
        Assert.True(viewModel.IsReadOnly);
        Assert.All(writeGated, name => Assert.Contains(name, blocked));

        // Returning to the earlier value is a change from the last published one.
        await PushAsync(client, dispatcher, status with { Revision = 3, WritesBlocked = false });
        List<string> unblocked = Drain(notifications);
        Assert.False(viewModel.IsReadOnly);
        Assert.All(writeGated, name => Assert.Contains(name, unblocked));

        // A third push with the same value announces none of them again.
        await PushAsync(
            client,
            dispatcher,
            status with { Revision = 4, WritesBlocked = false, Message = "Ready." });
        List<string> unchanged = Drain(notifications);
        Assert.All(writeGated, name => Assert.DoesNotContain(name, unchanged));
    }

    [Fact]
    public async Task DashboardTelemetryReplay_PublishesOnlyReadingsChangedWhileInactive()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        viewModel.SetDashboardTelemetryPresentationActive(true);
        Drain(notifications);
        LiveReadings original = status.LiveReadings!;
        LiveReadings warmer = original with { CpuTemperatureCelsius = 60 };

        viewModel.SetDashboardTelemetryPresentationActive(false);
        await PushAsync(client, dispatcher, status with { Revision = 2, LiveReadings = warmer });
        Assert.DoesNotContain(nameof(MainViewModel.CpuTemperatureValue), Drain(notifications));

        viewModel.SetDashboardTelemetryPresentationActive(true);
        List<string> replayed = Drain(notifications);
        Assert.Contains(nameof(MainViewModel.CpuTemperatureValue), replayed);
        Assert.DoesNotContain(nameof(MainViewModel.Fan1RpmLabel), replayed);

        // The reading returned to its original value while hidden: the last
        // published value was the warmer one, so the replay must announce it.
        viewModel.SetDashboardTelemetryPresentationActive(false);
        await PushAsync(client, dispatcher, status with { Revision = 3, LiveReadings = original });
        viewModel.SetDashboardTelemetryPresentationActive(true);
        Assert.Contains(nameof(MainViewModel.CpuTemperatureValue), Drain(notifications));

        // Changed and changed back while hidden: the bound value is current.
        viewModel.SetDashboardTelemetryPresentationActive(false);
        await PushAsync(client, dispatcher, status with { Revision = 4, LiveReadings = warmer });
        await PushAsync(client, dispatcher, status with { Revision = 5, LiveReadings = original });
        viewModel.SetDashboardTelemetryPresentationActive(true);
        Assert.DoesNotContain(nameof(MainViewModel.CpuTemperatureValue), Drain(notifications));
    }

    [Fact]
    public async Task FanControlEditorReload_KeepsBoundPointsWhileTheCurveIsUnchanged()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        Drain(notifications);
        IReadOnlyList<FanCurveEditorPoint> gpuPoints = viewModel.GpuFanCurveEditorPoints;
        var steeperCpu = new ManualFanCurve(
            [
                new(40, 2500),
                new(50, 2600),
                new(60, 3000),
                new(70, 3800),
                new(80, 4600),
                new(90, 5400),
            ],
            ManualFanCurve.Default.Gpu,
            useHigherTemperatureForBoth: true);
        var fanControl = new FanControlSettings(FanMode.ManualCurve, 3700, steeperCpu);

        await PushAsync(
            client,
            dispatcher,
            status with
            {
                Revision = 2,
                DesiredState = status.DesiredState with { FanControl = fanControl },
            });
        List<string> changed = Drain(notifications);
        Assert.Contains(nameof(MainViewModel.CpuFanCurveEditorPoints), changed);
        Assert.Contains(nameof(MainViewModel.CurrentFanControlSettings), changed);
        Assert.DoesNotContain(nameof(MainViewModel.GpuFanCurveEditorPoints), changed);
        Assert.Same(gpuPoints, viewModel.GpuFanCurveEditorPoints);
        IReadOnlyList<FanCurveEditorPoint> cpuPoints = viewModel.CpuFanCurveEditorPoints;
        Assert.Equal(2500, cpuPoints[0].SpeedRpm);

        await PushAsync(
            client,
            dispatcher,
            status with
            {
                Revision = 3,
                Message = "Ready.",
                DesiredState = status.DesiredState with { FanControl = fanControl },
            });
        List<string> unchanged = Drain(notifications);
        Assert.DoesNotContain(nameof(MainViewModel.CpuFanCurveEditorPoints), unchanged);
        Assert.DoesNotContain(nameof(MainViewModel.GpuFanCurveEditorPoints), unchanged);
        Assert.DoesNotContain(nameof(MainViewModel.CurrentFanControlSettings), unchanged);
        Assert.Same(cpuPoints, viewModel.CpuFanCurveEditorPoints);
        Assert.Same(gpuPoints, viewModel.GpuFanCurveEditorPoints);
    }

    [Fact]
    public async Task UnconfirmedKeyboardEffect_LoadsDefaultBrightnessWithoutAnIntermediateZero()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        ServiceStatusSnapshot status = CreateLiveStatus();
        ConcurrentQueue<string> notifications = await ConnectAsync(
            viewModel,
            client,
            dispatcher,
            status);
        Assert.True(viewModel.IsKeyboardEffectUnconfirmed);
        int brightness = viewModel.LightingBrightnessEditor;
        Assert.True(brightness > 0);
        Drain(notifications);

        await PushAsync(client, dispatcher, status with { Revision = 2, Message = "Ready." });
        Assert.DoesNotContain(nameof(MainViewModel.LightingBrightnessEditor), Drain(notifications));
        Assert.Equal(brightness, viewModel.LightingBrightnessEditor);
    }

    private static ServiceStatusSnapshot CreateLiveStatus() =>
        CreateStatus(verified: true, blocked: false) with
        {
            LiveReadings = new LiveReadings(12000, 2000, 2100, 55, 48),
        };

    private static async Task<ConcurrentQueue<string>> ConnectAsync(
        MainViewModel viewModel,
        FakeServiceClient client,
        RecordingDispatcher dispatcher,
        ServiceStatusSnapshot status)
    {
        var notifications = new ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) =>
            notifications.Enqueue(args.PropertyName ?? string.Empty);
        await viewModel.StartAsync();
        await PushAsync(client, dispatcher, status);
        Assert.True(viewModel.IsConnected);
        return notifications;
    }

    private static List<string> Drain(ConcurrentQueue<string> notifications)
    {
        var names = new List<string>();
        while (notifications.TryDequeue(out string? name))
        {
            names.Add(name);
        }

        return names;
    }

    private static async Task ObserveAsync(
        FakeServiceClient client,
        Func<Task> observe,
        ServiceStatusSnapshot status)
    {
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task cycle = observe();
        client.PendingObservation.SetResult(status);
        await cycle;
    }

    private static async Task PushAsync(
        FakeServiceClient client,
        RecordingDispatcher dispatcher,
        ServiceStatusSnapshot status)
    {
        int completed = dispatcher.CompletedCalls;
        client.Publish(status);
        await WaitUntilAsync(() => dispatcher.CompletedCalls > completed);
    }
}
