using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task FanStop_AcceptedRequestsPresentPendingUntilIndependentConfirmation()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        client.GetStatusResponse = status with
        {
            Revision = status.Revision + 1,
            FanStopStatus = new(FanStopMode.Stop, FanStopState.Starting,
                FanStopUnavailableReason.None, CanReturnToAutomatic: true),
        };
        await viewModel.SetFanStopAsync(true, CancellationToken.None);
        Assert.True(viewModel.FanStopRequested);
        Assert.Equal("Waiting for fan stop confirmation", viewModel.ActiveFanModeLabel);
        client.GetStatusResponse = status with
        {
            Revision = status.Revision + 2,
            FanStopStatus = new(FanStopMode.Automatic, FanStopState.ReturningToAutomatic,
                FanStopUnavailableReason.None, CanReturnToAutomatic: true),
        };
        await viewModel.SetFanStopAsync(false, CancellationToken.None);
        Assert.False(viewModel.FanStopRequested);
        Assert.Equal("Returning to Auto", viewModel.ActiveFanModeLabel);
        Assert.Contains("not confirmed yet", viewModel.FanStopDescription);
        Assert.False(viewModel.CanToggleFanStop);
        Assert.Contains("fanStop:Automatic", client.MutationOperations);
    }

    [Theory]
    [InlineData(FanStopState.Starting, FanStopMode.Stop, true, "Waiting for fan stop confirmation")]
    [InlineData(FanStopState.ReturningToAutomatic, FanStopMode.Automatic, false, "Returning to Auto")]
    public async Task FanStop_PendingStatePreservesIntentWithoutClaimingConfirmation(
        FanStopState state, FanStopMode mode, bool canToggle, string label)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var normal = new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default);
        var status = CreateFanStopStatus(new(mode, state, FanStopUnavailableReason.None,
            CanReturnToAutomatic: true, NormalFanControl: normal));
        status = status with
        {
            Capabilities = status.Capabilities! with
            {
                SupportsFanStop = false,
                HasFanStopTrialEvidence = false,
            },
        };
        await PushAsync(client, dispatcher, status);
        Assert.True(viewModel.IsFanStopVisible);
        Assert.Equal(mode == FanStopMode.Stop, viewModel.FanStopRequested);
        Assert.Equal(canToggle, viewModel.CanToggleFanStop);
        Assert.False(viewModel.CanEditFanControl);
        Assert.Equal(FanMode.Fixed, viewModel.SelectedFanMode);
        Assert.Equal(3000, viewModel.FixedFanSpeedRpm);
        Assert.Equal(label, viewModel.ActiveFanModeLabel);
        Assert.Contains("tens of seconds", viewModel.FanStopDescription);
    }

    [Fact]
    public async Task FanStop_TrialEvidenceShowsUnavailableControlWithoutGrantingWrites()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var status = CreateFanStopStatus(new(UnavailableReason: FanStopUnavailableReason.RecoveryNotValidated));
        status = status with { Capabilities = status.Capabilities! with { SupportsFanStop = false } };
        await PushAsync(client, dispatcher, status);

        Assert.True(viewModel.IsFanStopVisible);
        Assert.False(viewModel.FanStopRequested);
        Assert.False(viewModel.CanToggleFanStop);
        Assert.Contains("not available", viewModel.FanStopDescription);
        await viewModel.SetFanStopAsync(true, CancellationToken.None);
        Assert.DoesNotContain(client.MutationOperations, operation => operation.StartsWith("fanStop:"));
        Assert.True(viewModel.CanEditFanControl);
    }

    [Fact]
    public async Task FanStop_RequestUsesTypedCommandAndConfirmedStatusWithoutChangingNormalIntent()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var normal = new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default);
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        client.GetStatusResponse = status with
        {
            Revision = status.Revision + 1,
            FanStopStatus = new(FanStopMode.Stop, FanStopState.Stopped,
                FanStopUnavailableReason.None, CanReturnToAutomatic: true, NormalFanControl: normal),
        };

        await viewModel.SetFanStopAsync(true, CancellationToken.None);
        Assert.Contains("fanStop:Stop", client.MutationOperations);
        Assert.True(viewModel.FanStopRequested);
        Assert.True(viewModel.CanToggleFanStop);
        Assert.False(viewModel.CanEditFanControl);
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Maximum));
        Assert.Equal(FanMode.Fixed, viewModel.SelectedFanMode);
        Assert.Equal(3000, viewModel.FixedFanSpeedRpm);
        Assert.Equal("Fans are stopped", viewModel.ActiveFanModeLabel);
        Assert.Empty(viewModel.FanControlUnavailableMessage);
        Assert.Null(client.LastProfiles);
        Assert.Null(client.LastFanControlSettings);
        await PushAsync(client, dispatcher, client.GetStatusResponse with
        {
            Revision = status.Revision + 2,
            FanStopStatus = client.GetStatusResponse.FanStopStatus with { CanReturnToAutomatic = false },
        });
        Assert.False(viewModel.CanToggleFanStop);
        Assert.Equal("Fans are stopped", viewModel.FanStopDescription);
    }

    [Fact]
    public async Task FanStop_AutoExitRemainsAvailableAfterEntryScopeAndCapabilityChange()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var status = CreateFanStopStatus(new(FanStopMode.Stop, FanStopState.Stopped,
            FanStopUnavailableReason.None, CanReturnToAutomatic: true, NormalFanControl: FanControlSettings.Default));
        status = status with
        {
            ActivePowerSource = PowerSource.Battery,
            Capabilities = status.Capabilities! with { SupportsFanStop = false, HasFanStopTrialEvidence = false },
            ConfirmedState = status.ConfirmedState! with { Performance = PerformancePreset.BatterySaver },
            DesiredState = status.DesiredState with { Performance = new(PerformancePreset.BatterySaver, SettingValueSource.PowerProfile) },
        };
        await PushAsync(client, dispatcher, status);
        Assert.True(viewModel.IsFanStopVisible);
        Assert.True(viewModel.CanToggleFanStop);
        client.GetStatusResponse = status with
        {
            Revision = status.Revision + 1,
            FanStopStatus = new(State: FanStopState.Automatic, UnavailableReason: FanStopUnavailableReason.None),
        };
        await viewModel.SetFanStopAsync(false, CancellationToken.None);
        Assert.Contains("fanStop:Automatic", client.MutationOperations);
        Assert.False(viewModel.FanStopRequested);
        Assert.Contains("Auto", viewModel.ActiveFanModeLabel);
        Assert.Null(client.LastProfiles);
    }

    [Fact]
    public async Task FanStop_ConfirmedAutoReplacesStaleManualSelectionAndKeepsSavedEditor()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var normal = new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default);
        var status = CreateFanStopStatus(new(State: FanStopState.Automatic,
            UnavailableReason: FanStopUnavailableReason.None));
        status = status with
        {
            FanControlOverride = normal,
            ConfirmedState = status.ConfirmedState! with { Fans = FanMode.Fixed },
            DesiredState = status.DesiredState with
            {
                Fans = new(FanMode.Fixed, SettingValueSource.TemporaryOverride),
                FanControl = normal,
            },
        };
        await PushAsync(client, dispatcher, status);

        Assert.Equal(FanMode.Automatic, viewModel.SelectedFanMode);
        Assert.Equal(3000, viewModel.FixedFanSpeedRpm);
        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Fixed));
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetFanModeCommand).ExecuteAsync(FanMode.Fixed);
        Assert.Equal(FanMode.Fixed, client.LastFanControlSettings?.Mode);
        Assert.Equal(3000, client.LastFanControlSettings?.FixedSpeedRpm);
        Assert.Null(client.LastProfiles);
    }

    [Fact]
    public async Task FanStop_RejectedRequestDoesNotOptimisticallyTurnOnAndRecoveryCannotWrite()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        client.NextResult = new(ControlOperationStatus.Blocked, "Blocked");
        client.GetStatusResponse = status with { Revision = status.Revision + 1 };
        await viewModel.SetFanStopAsync(true, CancellationToken.None);
        Assert.False(viewModel.FanStopRequested);
        int count = client.MutationOperations.Count;

        await PushAsync(client, dispatcher, status with
        {
            Revision = status.Revision + 2,
            WritesBlocked = true,
            ObservationReason = DeviceObservationReason.RetainedPowerSource,
            FanStopStatus = new(FanStopMode.Stop, FanStopState.RecoveryRequired,
                FanStopUnavailableReason.None, NormalFanControl: FanControlSettings.Default),
        });
        Assert.True(viewModel.FanStopRequested);
        Assert.False(viewModel.CanToggleFanStop);
        Assert.False(viewModel.CanEditFanControl);
        Assert.False(viewModel.CanRequestHardwareControl);
        Assert.False(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.Equal("Fan control needs recovery", viewModel.ActiveFanModeLabel);
        Assert.Contains("not confirmed", viewModel.FanStopDescription);
        await viewModel.SetFanStopAsync(false, CancellationToken.None);
        Assert.Equal(count, client.MutationOperations.Count);
    }

    private static ServiceStatusSnapshot CreateFanStopStatus(FanStopStatus fanStop)
    {
        var status = CreateStatus(verified: true, blocked: false);
        return status with
        {
            ActivePowerSource = PowerSource.UsbC,
            Capabilities = status.Capabilities! with { HasFanStopTrialEvidence = true, SupportsFanStop = true },
            ConfirmedState = status.ConfirmedState! with { Performance = PerformancePreset.Balanced },
            DesiredState = status.DesiredState with { Performance = new(PerformancePreset.Balanced, SettingValueSource.PowerProfile) },
            FanStopStatus = fanStop,
        };
    }
}
