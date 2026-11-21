using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    private static ServiceStatusSnapshot CreateEntryRestrictedHyperBoostStatus(bool active)
    {
        ServiceStatusSnapshot baseline = CreatePadlessHyperBoostStatus(CreateStatus(verified: true, blocked: false));
        return baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
            },
            ConfirmedState = baseline.ConfirmedState! with
            {
                Performance = active ? null : PerformancePreset.Balanced,
                HyperBoostActive = active,
            },
        };
    }

    [Fact]
    public async Task HyperBoostEntryRestriction_InactiveCannotStartAndExplainsWhy()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateEntryRestrictedHyperBoostStatus(active: false));
        await WaitUntilAsync(() => viewModel.HyperBoostDescription.Contains("doesn't start it", StringComparison.Ordinal));

        Assert.True(viewModel.IsHyperBoostVisible);
        Assert.False(viewModel.IsHyperBoostWritable);
        Assert.False(viewModel.CanToggleHyperBoost);
        Assert.Equal(
            "Synapse doesn't offer HyperBoost on this laptop configuration, so OpenBlade doesn't start it.",
            viewModel.HyperBoostUnavailableReason);
    }

    [Fact]
    public async Task HyperBoostEntryRestriction_ActiveCanAlwaysTurnOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateEntryRestrictedHyperBoostStatus(active: true));
        await WaitUntilAsync(() => viewModel.CanToggleHyperBoost);

        Assert.True(viewModel.HyperBoostActive);
        Assert.False(viewModel.IsHyperBoostWritable);
        Assert.Equal(string.Empty, viewModel.HyperBoostUnavailableReason);
        Assert.Equal(
            "Synapse doesn't offer HyperBoost on this laptop configuration. You can still turn it off.",
            viewModel.HyperBoostDescription);
    }
}
