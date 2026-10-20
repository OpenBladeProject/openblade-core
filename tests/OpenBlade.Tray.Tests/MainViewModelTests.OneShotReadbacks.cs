using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task CompletedUnknownOneShotReadbacksStopLoadingWithoutEnablingCommands()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateOneShotReadbackStatus();
        client.Publish(status with { ObservationReason = DeviceObservationReason.RefreshingControls });
        await WaitUntilAsync(() => viewModel.IsStartupAnimationFieldLoading);

        client.Publish(status with { Revision = status.Revision + 1 });
        await WaitUntilAsync(() => viewModel.StartupAnimationStateLabel == "Unavailable");
        AssertUnavailableOneShotReadbacks(viewModel);
        Assert.Null(viewModel.StartupAnimationEnabled);
        Assert.Null(viewModel.FunctionKeyPrimary);
        Assert.Null(viewModel.GamingModeEnabled);

        // Repeated failed observations cannot restart a spinner or enable an action.
        client.Publish(status with { Revision = status.Revision + 2 });
        await WaitUntilAsync(() => viewModel.FunctionKeyPrimaryStateLabel == "Unavailable");
        AssertUnavailableOneShotReadbacks(viewModel);
    }

    [Fact]
    public async Task OneShotObservationReasonChangesNotifyBindingsWithoutOtherMetadataChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        var loadingChanges = new ConcurrentQueue<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsStartupAnimationFieldLoading))
            {
                loadingChanges.Enqueue(viewModel.IsStartupAnimationFieldLoading);
            }
        };
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateOneShotReadbackStatus();
        client.Publish(status with { ObservationReason = DeviceObservationReason.RefreshingControls });
        await WaitUntilAsync(() => loadingChanges.Contains(true));
        loadingChanges.Clear();

        // Only the typed observation reason changes; live-readings coalescing
        // must not bypass the PropertyChanged event that updates XAML bindings.
        client.Publish(status with { Revision = status.Revision + 1 });
        await WaitUntilAsync(() => loadingChanges.Contains(false));
        AssertUnavailableOneShotReadbacks(viewModel);
        loadingChanges.Clear();

        client.Publish(status with
        {
            Revision = status.Revision + 2,
            ObservationReason = DeviceObservationReason.RefreshingControls,
        });
        await WaitUntilAsync(() => loadingChanges.Contains(true));
        Assert.Equal("Checking", viewModel.StartupAnimationStateLabel);
        Assert.False(viewModel.CanEditStartupAnimation);
    }

    [Fact]
    public async Task FailedOneShotRefreshRetainsValuesWithoutTreatingThemAsCurrentReadback()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateOneShotReadbackStatus();
        client.Publish(status with
        {
            StartupAnimationEnabled = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            GamingModeEnabled = true,
        });
        await WaitUntilAsync(() => viewModel.CanEditGamingMode
            && viewModel.StartupAnimationEnabled == true
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.MultimediaKeys
            && viewModel.GamingModeEnabled == true);

        client.Publish(status with { Revision = status.Revision + 1 });
        await WaitUntilAsync(() => viewModel.StartupAnimationStateLabel == "Unavailable");
        AssertUnavailableOneShotReadbacks(viewModel);
        Assert.True(viewModel.StartupAnimationEnabled);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, viewModel.FunctionKeyPrimary);
        Assert.True(viewModel.GamingModeEnabled);
        Assert.Null(viewModel.SelectedFunctionKeyPrimaryOption);

        // Only fresh confirmed values re-enable the controls, without a restart.
        client.Publish(status with
        {
            Revision = status.Revision + 2,
            StartupAnimationEnabled = false,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = false,
        });
        // The test dispatcher permits concurrent readers during the callback;
        // admission flags are updated before the retained bound values.
        await WaitUntilAsync(() => viewModel.CanEditGamingMode
            && viewModel.StartupAnimationEnabled == false
            && viewModel.FunctionKeyPrimaryStateLabel == "Function keys"
            && viewModel.GamingModeEnabled == false);
        Assert.False(viewModel.IsStartupAnimationFieldLoading);
        Assert.True(viewModel.CanEditStartupAnimation);
        Assert.True(viewModel.CanEditFunctionKeyPrimary);
        Assert.Equal("Off", viewModel.StartupAnimationStateLabel);
        Assert.Equal("Function keys", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.Equal("Off", viewModel.TrayDashboardGamingModeLabel);
        Assert.True(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(viewModel.StartupAnimationOnAction));
    }

    [Fact]
    public async Task MissingStartupReadbackDoesNotDisableOtherConfirmedOneShotControls()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateOneShotReadbackStatus() with
        {
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = false,
        });
        await WaitUntilAsync(() => viewModel.CanEditGamingMode);
        Assert.False(viewModel.IsStartupAnimationFieldLoading);
        Assert.False(viewModel.CanEditStartupAnimation);
        Assert.Equal("Unavailable", viewModel.StartupAnimationStateLabel);
        Assert.True(viewModel.CanEditFunctionKeyPrimary);
        Assert.True(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(viewModel.MultimediaKeysPrimaryAction));
        Assert.True(viewModel.CanEditGamingMode);
    }

    private static void AssertUnavailableOneShotReadbacks(MainViewModel viewModel)
    {
        Assert.False(viewModel.IsStartupAnimationFieldLoading);
        Assert.False(viewModel.IsFunctionKeyFieldLoading);
        Assert.False(viewModel.IsGamingModeFieldLoading);
        Assert.Equal("Unavailable", viewModel.StartupAnimationStateLabel);
        Assert.Equal("Unavailable", viewModel.FunctionKeyPrimaryStateLabel);
        Assert.Equal("Unavailable", viewModel.TrayDashboardGamingModeLabel);
        Assert.False(viewModel.CanEditStartupAnimation);
        Assert.False(viewModel.CanEditFunctionKeyPrimary);
        Assert.False(viewModel.CanEditGamingMode);
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(viewModel.StartupAnimationOnAction));
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(viewModel.MultimediaKeysPrimaryAction));
        Assert.False(viewModel.ApplyOneShotLaptopControlCommand.CanExecute(viewModel.GamingModeOnAction));
    }

    private static ServiceStatusSnapshot CreateOneShotReadbackStatus() => CreateStatus(
        verified: true,
        blocked: false,
        capabilities: DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            },
        });
}
