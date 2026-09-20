using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task FanControlEditor_EditsNotifyChangedBindingsBeforeSettings()
    {
        await using var viewModel = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.FixedFanSpeedRpm = 3850;
        Assert.Equal(3900, viewModel.FixedFanSpeedRpm);
        Assert.Equal([nameof(MainViewModel.FixedFanSpeedRpm), nameof(MainViewModel.CurrentFanControlSettings)], notifications);
        notifications.Clear();
        viewModel.FixedFanSpeedRpm = 3901;
        Assert.Empty(notifications);

        viewModel.UseHigherFanTemperature = false;
        Assert.Equal([nameof(MainViewModel.UseHigherFanTemperature), nameof(MainViewModel.CurrentFanControlSettings)], notifications);
        notifications.Clear();
        viewModel.UseHigherFanTemperature = false;
        Assert.Empty(notifications);

        IReadOnlyList<FanCurveEditorPoint> gpu = viewModel.GpuFanCurveEditorPoints;
        viewModel.UpdateFanCurvePoint(viewModel.CpuFanCurveEditorPoints[2], 5400);
        Assert.Equal(3400, viewModel.CpuFanCurveEditorPoints[2].SpeedRpm);
        Assert.Same(gpu, viewModel.GpuFanCurveEditorPoints);
        Assert.Equal([nameof(MainViewModel.CpuFanCurveEditorPoints), nameof(MainViewModel.CurrentFanControlSettings)], notifications);
        Assert.False(viewModel.SetFanControlCommand.CanExecute(viewModel.CurrentFanControlSettings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanControlEditor_FailedCommandRestoresValuesWithinLatestDeviceRange(bool throws)
    {
        var client = new FakeServiceClient
        {
            BlockMutations = true,
            NextResult = new ControlOperationResult(ControlOperationStatus.Failed, "Rejected"),
            MutationFailure = throws ? new IOException("Connection lost") : null,
        };
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var previous = new FanControlSettings(FanMode.Fixed, 5200, ManualFanCurve.Default);
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        status = status with
        {
            DesiredState = status.DesiredState with
            {
                Fans = new ResolvedSetting<FanMode>(FanMode.Fixed, SettingValueSource.PowerProfile),
                FanControl = previous,
            },
        };
        await PushAsync(client, dispatcher, status);
        Assert.Equal(previous, viewModel.CurrentFanControlSettings);
        var requested = new FanControlSettings(FanMode.ManualCurve, 4600,
            new ManualFanCurve(
                [new(40, 2500), new(90, 4800)],
                [new(35, 2400), new(85, 4900)],
                useHigherTemperatureForBoth: false));

        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetFanControlCommand).ExecuteAsync(requested);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(requested, viewModel.CurrentFanControlSettings);
        IReadOnlyList<FanCurveEditorPoint> requestedCpu = viewModel.CpuFanCurveEditorPoints;
        IReadOnlyList<FanCurveEditorPoint> requestedGpu = viewModel.GpuFanCurveEditorPoints;

        await PushAsync(client, dispatcher, status with
        {
            Revision = status.Revision + 1,
            WritesBlocked = true,
            Capabilities = status.Capabilities! with { FanSpeedRange = new FanSpeedRange(2000, 5000, 100) },
        });
        Assert.True(viewModel.IsFanControlOperationInProgress);
        Assert.Equal(requested, viewModel.CurrentFanControlSettings);
        Assert.Same(requestedCpu, viewModel.CpuFanCurveEditorPoints);
        Assert.Same(requestedGpu, viewModel.GpuFanCurveEditorPoints);

        client.CompleteMutation();
        await apply;

        Assert.False(viewModel.IsFanControlOperationInProgress);
        Assert.Equal(FanMode.Fixed, viewModel.SelectedFanMode);
        Assert.Equal(5000, viewModel.FixedFanSpeedRpm);
        Assert.True(viewModel.UseHigherFanTemperature);
        Assert.Equal(previous.ManualCurve.Cpu.Select(point => point.TemperatureCelsius),
            viewModel.CpuFanCurveEditorPoints.Select(point => point.TemperatureCelsius));
        Assert.Equal(previous.ManualCurve.Gpu.Select(point => point.TemperatureCelsius),
            viewModel.GpuFanCurveEditorPoints.Select(point => point.TemperatureCelsius));
        Assert.Equal(previous.ManualCurve.Cpu.Select(point => Math.Min(point.SpeedRpm, 5000)),
            viewModel.CpuFanCurveEditorPoints.Select(point => point.SpeedRpm));
        Assert.Equal(previous.ManualCurve.Gpu.Select(point => Math.Min(point.SpeedRpm, 5000)),
            viewModel.GpuFanCurveEditorPoints.Select(point => point.SpeedRpm));
        Assert.False(viewModel.SetFanControlCommand.CanExecute(viewModel.CurrentFanControlSettings));
        Assert.Equal(requested, client.LastFanControlSettings);
        Assert.Equal(1, client.MutationCalls);
    }
}
