using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(60, 10, false, false)]
    [InlineData(61, 10, true, false)]
    [InlineData(60, 11, false, true)]
    public async Task FanStopSettings_WarningsApplyOnlyToCustomExit(int temperature, int seconds, bool hot, bool delayed)
    {
        await using var vm = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        vm.FanStopCpuLimit = temperature;
        vm.FanStopExitSeconds = seconds;
        Assert.False(vm.HasFanStopTemperatureWarning);
        Assert.False(vm.HasFanStopDelayWarning);
        vm.FanStopEndModeIndex = 1;
        Assert.Equal(hot, vm.HasFanStopTemperatureWarning);
        Assert.Equal(delayed, vm.HasFanStopDelayWarning);
        vm.AutomaticFanStopSeconds = 3600;
        Assert.Equal(delayed, vm.HasFanStopDelayWarning);
    }

    [Fact]
    public async Task FanStopSettings_CutoffsStayIndependentAndInvalidDraftDoesNotMoveOtherValues()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        vm.FanStopEndModeIndex = 1;
        Assert.False(vm.HasFanStopTemperatureWarning);
        vm.FanStopCpuImmediateCutoff = 54;
        vm.FanStopGpuImmediateCutoff = 75;
        Assert.Contains("at least", vm.FanStopSettingsError);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.Null(client.LastFanStopSettings);
        await PushAsync(client, dispatcher, status with { Revision = status.Revision + 1 });
        Assert.Equal(54, vm.FanStopCpuImmediateCutoff);
        Assert.Equal(75, vm.FanStopGpuImmediateCutoff);
        Assert.Equal(55, vm.FanStopCpuLimit);
        vm.FanStopCpuImmediateCutoff = 55; // equality is intentionally valid
        Assert.False(vm.HasFanStopSettingsError);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.NotNull(client.LastFanStopSettings);
        Assert.Equal(55, client.LastFanStopSettings.CpuImmediateCutoffCelsius);
        Assert.Equal(75, client.LastFanStopSettings.GpuImmediateCutoffCelsius);
    }

    [Fact]
    public async Task FanStopSettings_InvalidStartLimitsStayInDraftWithoutSubmitting()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        vm.FanStopEndModeIndex = 1;
        vm.AutomaticFanStopEnabled = true;
        vm.AutomaticFanStopCpuLimit = 55;
        vm.AutomaticFanStopGpuLimit = 55;
        Assert.True(vm.HasAutomaticFanStopCpuError);
        Assert.True(vm.HasAutomaticFanStopGpuError);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.Null(client.LastFanStopSettings);
        await PushAsync(client, dispatcher, status with { Revision = status.Revision + 1 });
        Assert.Equal(55, vm.AutomaticFanStopCpuLimit);
        Assert.True(vm.AutomaticFanStopEnabled);
        vm.AutomaticFanStopCpuLimit = 50;
        vm.AutomaticFanStopGpuLimit = 50;
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.NotNull(client.LastFanStopSettings);
        Assert.Equal(FanStopEndMode.Custom, client.LastFanStopSettings.EndMode);
        Assert.True(client.LastFanStopSettings.AutomaticEnabled);
    }

    [Fact]
    public async Task FanStopSettings_PausedAutomationRequiresTypedResumeAndFreshStatus()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Automatic,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true)
        {
            Settings = FanStopSettings.Default with { AutomaticEnabled = true },
            AutomationState = FanStopAutomationState.Paused,
            AutomationPauseReason = FanStopAutomationPauseReason.ManualAuto,
        });
        await PushAsync(client, dispatcher, status);
        Assert.True(vm.CanResumeAutomaticFanStop);
        Assert.Contains("Paused", vm.AutomaticFanStopSummary);
        client.GetStatusResponse = status with
        {
            Revision = status.Revision + 1,
            FanStopStatus = status.FanStopStatus with { AutomationState = FanStopAutomationState.Waiting },
        };
        await vm.ResumeAutomaticFanStopAsync(CancellationToken.None);
        Assert.Contains("resumeAutomaticFanStop", client.MutationOperations);
        Assert.False(vm.CanResumeAutomaticFanStop);
        Assert.False(vm.FanStopRequested);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(50.5)]
    [InlineData(double.PositiveInfinity)]
    public async Task FanStopSettings_NonnumericOrFractionalValuesCannotBecomeFirmwarePolicy(double value)
    {
        await using var vm = new MainViewModel(new FakeServiceClient(), new RecordingDispatcher());
        vm.AutomaticFanStopCpuLimit = value;
        Assert.True(vm.HasFanStopSettingsError);
    }
    [Fact]
    public async Task FanStopSettings_TrialEvidenceDoesNotAuthorizePolicyOrResumeRequests()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive)
        {
            Settings = FanStopSettings.Default with { AutomaticEnabled = true },
            AutomationState = FanStopAutomationState.Paused,
        });
        status = status with { Capabilities = status.Capabilities! with { SupportsFanStop = false } };
        await PushAsync(client, dispatcher, status);
        vm.FanStopCpuLimit = 60;
        Assert.True(vm.IsFanStopVisible);
        Assert.False(vm.CanEditFanStopSettings);
        Assert.False(vm.CanResumeAutomaticFanStop);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        await vm.ResumeAutomaticFanStopAsync(CancellationToken.None);
        Assert.Null(client.LastFanStopSettings);
        Assert.DoesNotContain("resumeAutomaticFanStop", client.MutationOperations);
    }

    [Fact]
    public async Task FanStopSettings_DisablingAutomationCanSaveOverlappingUnusedStartLimits()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true)
        {
            Settings = FanStopSettings.Default with { EndMode = FanStopEndMode.Custom, AutomaticEnabled = true },
        });
        await PushAsync(client, dispatcher, status);
        vm.AutomaticFanStopCpuLimit = 55;
        vm.AutomaticFanStopGpuLimit = 55;
        Assert.True(vm.HasFanStopSettingsError);
        vm.AutomaticFanStopEnabled = false;
        Assert.False(vm.HasFanStopSettingsError);
        Assert.False(vm.HasAutomaticFanStopCpuError);
        Assert.False(vm.HasAutomaticFanStopGpuError);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.NotNull(client.LastFanStopSettings);
        Assert.False(client.LastFanStopSettings.AutomaticEnabled);
        Assert.Equal(55, client.LastFanStopSettings.AutomaticCpuBelowCelsius);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Failed)]
    [InlineData(ControlOperationStatus.Blocked)]
    public async Task FanStopSettings_RejectedSaveKeepsAuthoritativeSummaryAndMarksRetainedDraft(ControlOperationStatus result)
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(result, "Policy was not saved."),
        };
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true));
        await PushAsync(client, dispatcher, status);
        client.GetStatusResponse = status with { Revision = status.Revision + 1 };
        string savedSummary = vm.FanStopEndSummary;
        vm.FanStopEndModeIndex = 1;
        vm.FanStopCpuLimit = 60;
        Assert.True(vm.HasUnsavedFanStopSettings);
        Assert.Equal(savedSummary, vm.FanStopEndSummary);
        await vm.ApplyFanStopSettingsAsync(CancellationToken.None);
        Assert.True(vm.HasOperationFeedback);
        Assert.Contains("Check Troubleshooting for details.", vm.OperationFeedbackMessage);
        Assert.Equal("Policy was not saved.", vm.OperationStatusMessage);
        Assert.Equal(60, vm.FanStopCpuLimit);
        Assert.Equal(1, vm.FanStopEndModeIndex);
        Assert.True(vm.HasUnsavedFanStopSettings);
        Assert.Equal(savedSummary, vm.FanStopEndSummary);
        await PushAsync(client, dispatcher, status with { Revision = status.Revision + 2 });
        Assert.Equal(savedSummary, vm.FanStopEndSummary);
        Assert.True(vm.HasUnsavedFanStopSettings);
    }

    [Fact]
    public async Task FanStopSettings_SavedCustomSummaryChangesOnlyWithAuthoritativeStatus()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var settings = FanStopSettings.Default with { EndMode = FanStopEndMode.Custom };
        var status = CreateFanStopStatus(new(State: FanStopState.Inactive,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true)
        {
            Settings = settings,
        });
        await PushAsync(client, dispatcher, status);
        Assert.False(vm.HasUnsavedFanStopSettings);
        string savedSummary = vm.FanStopEndSummary;
        vm.FanStopCpuLimit = 60;
        Assert.Equal(savedSummary, vm.FanStopEndSummary);
        Assert.True(vm.HasUnsavedFanStopSettings);
        await PushAsync(client, dispatcher, status with
        {
            Revision = status.Revision + 1,
            FanStopStatus = status.FanStopStatus with { Settings = settings with { CpuLimitCelsius = 60 } },
        });
        Assert.NotEqual(savedSummary, vm.FanStopEndSummary);
        Assert.Contains("60", vm.FanStopEndSummary);
        Assert.False(vm.HasUnsavedFanStopSettings);
    }

    [Fact]
    public async Task FanStopSettings_RejectedResumeKeepsPausedStateAndDoesNotClaimSavedChange()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(ControlOperationStatus.Blocked, "Automatic Stop was not resumed."),
        };
        var dispatcher = new RecordingDispatcher();
        await using var vm = new MainViewModel(client, dispatcher);
        await vm.StartAsync();
        var status = CreateFanStopStatus(new(State: FanStopState.Automatic,
            UnavailableReason: FanStopUnavailableReason.None, CanStop: true)
        {
            Settings = FanStopSettings.Default with { AutomaticEnabled = true },
            AutomationState = FanStopAutomationState.Paused,
            AutomationPauseReason = FanStopAutomationPauseReason.ManualAuto,
        });
        await PushAsync(client, dispatcher, status);
        client.GetStatusResponse = status with { Revision = status.Revision + 1 };
        await vm.ResumeAutomaticFanStopAsync(CancellationToken.None);
        Assert.True(vm.HasOperationFeedback);
        Assert.Contains("Check Troubleshooting for details.", vm.OperationFeedbackMessage);
        Assert.Equal("Automatic Stop was not resumed.", vm.OperationStatusMessage);
        Assert.True(vm.CanResumeAutomaticFanStop);
        Assert.Contains("Paused", vm.AutomaticFanStopSummary);
        Assert.False(vm.FanStopRequested);
    }

}
