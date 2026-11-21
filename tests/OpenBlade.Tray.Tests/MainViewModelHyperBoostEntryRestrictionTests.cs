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

    [Fact]
    public async Task HyperBoostEntryRestriction_ActiveWithoutExitNeverOffersTurningItOff()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();

        // The power source is unclassified, so the exit is unavailable for a reason
        // other than the restriction; the restriction text must not claim otherwise.
        client.Publish(CreateEntryRestrictedHyperBoostStatus(active: true) with
        {
            ActivePowerSource = PowerSource.ExternalUnknown,
        });
        await WaitUntilAsync(() => viewModel.HyperBoostActive && viewModel.HasHyperBoostUnavailableReason);

        Assert.False(viewModel.CanToggleHyperBoost);
        Assert.DoesNotContain("Synapse", viewModel.HyperBoostUnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain("turn it off", viewModel.HyperBoostUnavailableReason, StringComparison.Ordinal);
        // The exit is admitted, so the reason is the general state, not the
        // restriction's "cannot safely change it" text.
        Assert.Equal(
            "HyperBoost is unavailable with the current performance and fan state. Refresh the laptop state.",
            viewModel.HyperBoostUnavailableReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HyperBoostEntryRestriction_RestrictionOnlyCapabilityChangeReachesPresentation(
        bool startsRestricted)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot unrestricted = CreatePadlessHyperBoostStatus(CreateStatus(verified: true, blocked: false));
        ServiceStatusSnapshot restricted = unrestricted with
        {
            Capabilities = unrestricted.Capabilities! with
            {
                HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
            },
        };

        client.Publish(startsRestricted ? restricted : unrestricted);
        await WaitUntilAsync(() => viewModel.IsHyperBoostVisible
            && viewModel.CanToggleHyperBoost == !startsRestricted);

        // Only Capabilities.HyperBoostEntryRestriction differs in the next status.
        client.Publish(startsRestricted ? unrestricted : restricted);
        await WaitUntilAsync(() => viewModel.CanToggleHyperBoost == startsRestricted);

        Assert.Equal(startsRestricted, viewModel.IsHyperBoostWritable);
        Assert.Equal(
            startsRestricted ? null : HyperBoostEntryRestriction.VendorSystemSku,
            viewModel.Capabilities.HyperBoostEntryRestriction);
        Assert.Equal(
            !startsRestricted,
            viewModel.HyperBoostDescription.Contains("Synapse", StringComparison.Ordinal));
    }
}
