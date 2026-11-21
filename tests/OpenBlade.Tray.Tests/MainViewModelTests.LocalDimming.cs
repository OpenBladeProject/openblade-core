using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    private static ServiceStatusSnapshot CreateLocalDimmingStatus(
        LocalDimmingCapabilities? capabilities,
        bool? active,
        bool blocked = false,
        long revision = 1)
    {
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: blocked);
        return status with
        {
            Capabilities = status.Capabilities! with { LocalDimming = capabilities },
            ConfirmedState = new ConfirmedDeviceState(null, null, null, null) { LocalDimmingActive = active },
            Revision = revision,
            Message = $"Local dimming status {revision}",
        };
    }

    [Fact]
    public async Task LocalDimmingClear_ShowsOnlyForAdmittedClearWhileConfirmedOn()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var admitted = new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true);

        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(admitted, active: true));
        Assert.True(viewModel.ShowLocalDimmingClear);
        Assert.True(viewModel.CanClearLocalDimming);
        Assert.True(viewModel.ClearLocalDimmingCommand.CanExecute(null));

        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(admitted, active: false, revision: 2));
        Assert.False(viewModel.ShowLocalDimmingClear);
        Assert.False(viewModel.ClearLocalDimmingCommand.CanExecute(null));

        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(admitted, active: null, revision: 3));
        Assert.False(viewModel.ShowLocalDimmingClear);

        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, SupportsClear: false), active: true, revision: 4));
        Assert.False(viewModel.ShowLocalDimmingClear);

        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(admitted, active: true, blocked: true, revision: 5));
        Assert.True(viewModel.ShowLocalDimmingClear);
        Assert.False(viewModel.CanClearLocalDimming);
        Assert.False(await viewModel.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(0, client.ClearLocalDimmingCalls);
    }

    [Fact]
    public async Task LocalDimmingClear_SendsOneRequestAndReleasesTheCommand()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true), active: true));

        Assert.True(await viewModel.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(1, client.ClearLocalDimmingCalls);
        Assert.False(viewModel.IsClearLocalDimmingOperationInProgress);
    }

    [Fact]
    public async Task LocalDimmingClear_RefusalShowsItsReasonOnTheCardWithoutTheSavedChangeText()
    {
        const string refusal = "OpenBlade can’t turn off local dimming while Maximum Fans is on. Turn off Maximum Fans in Synapse, then try again.";
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(ControlOperationStatus.Blocked, refusal),
        };
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true), active: true));

        Assert.False(await viewModel.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(refusal, viewModel.LocalDimmingStatusMessage);
        Assert.True(viewModel.HasLocalDimmingStatusMessage);
        Assert.DoesNotContain("saved the change", viewModel.OperationFeedbackMessage, StringComparison.Ordinal);
        Assert.False(viewModel.HasOperationFeedback);

        client.NextResult = new ControlOperationResult(ControlOperationStatus.Applied, "Local dimming is off.");
        Assert.True(await viewModel.ClearLocalDimmingAsync(CancellationToken.None));
        Assert.Equal(string.Empty, viewModel.LocalDimmingStatusMessage);
        Assert.False(viewModel.HasLocalDimmingStatusMessage);
    }

    [Fact]
    public async Task LocalDimmingClear_FailureShowsTheServiceMessageOnTheCard()
    {
        const string failure = "OpenBlade couldn't turn off local dimming. Try again.";
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(ControlOperationStatus.Failed, failure),
        };
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        await PublishStatusAndWaitAsync(client, dispatcher, viewModel, CreateLocalDimmingStatus(
            new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true), active: true));

        Assert.False(await viewModel.ClearLocalDimmingAsync(CancellationToken.None));

        Assert.Equal(failure, viewModel.LocalDimmingStatusMessage);
        Assert.True(viewModel.HasOperationFeedback);
    }
}
