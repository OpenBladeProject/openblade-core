using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioComponentDownloadViewModelTests
{
    [Fact]
    public async Task DownloadRequiresAnExplicitActionAndLeavesAudioDisconnected()
    {
        var adapter = new MissingComponentAdapter();
        int downloads = 0;
        await using var viewModel = new ThxAudioViewModel(adapter, _ =>
        {
            downloads++;
            return Task.FromResult("verified-component");
        }, () => false);

        await viewModel.DownloadComponentAsync();
        viewModel.ApplyPreference(true);
        await viewModel.ConnectIfNeededAsync();
        Assert.Equal(0, downloads);
        Assert.True(viewModel.CanDownloadComponent);

        await viewModel.DownloadComponentAsync();

        Assert.Equal(1, downloads);
        Assert.Equal(1, adapter.ConnectCalls);
        Assert.Equal(0, adapter.ReadCalls);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.IsComponentDownloadVisible);
        Assert.True(viewModel.IsReadSettingsVisible);
        Assert.Contains("Read the current settings", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(ThxAudioAvailability.UnsupportedModel, false)]
    [InlineData(ThxAudioAvailability.ControllerActive, false)]
    [InlineData(ThxAudioAvailability.ComponentsUnavailable, true)]
    public async Task ComponentDownloadDoesNotReplaceOtherAdmissionFailures(
        ThxAudioAvailability availability, bool componentAvailable)
    {
        var adapter = new MissingComponentAdapter { Availability = availability };
        await using var viewModel = new ThxAudioViewModel(adapter,
            _ => throw new InvalidOperationException("Unexpected download"), () => componentAvailable);
        await viewModel.SetEnabledAsync(true);
        Assert.False(viewModel.IsComponentDownloadVisible);
        await viewModel.DownloadComponentAsync();
    }

    [Fact]
    public async Task VerificationFailureKeepsTheDownloadActionAvailableForRetry()
    {
        var adapter = new MissingComponentAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter,
            _ => Task.FromException<string>(new InvalidDataException("The Razer signature could not be verified.")),
            () => false);
        await viewModel.SetEnabledAsync(true);
        await viewModel.DownloadComponentAsync();
        Assert.Contains("THX component download failed.", viewModel.StatusMessage);
        Assert.Contains("signature could not be verified", viewModel.StatusMessage);
        Assert.True(viewModel.CanDownloadComponent);
        Assert.False(viewModel.CanEdit);
        Assert.Equal(0, adapter.ReadCalls);
    }

    [Fact]
    public async Task PendingDownloadBlocksRepeatedClicksAndCancelsOnDisposal()
    {
        var adapter = new MissingComponentAdapter();
        int downloads = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new ThxAudioViewModel(adapter, async token =>
        {
            downloads++;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, () => false);
        await viewModel.SetEnabledAsync(true);
        Task download = viewModel.DownloadComponentAsync();
        await started.Task;
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanRead);
        Assert.False(viewModel.CanToggleIntegration);
        Assert.False(viewModel.CanDownloadComponent);
        Assert.Contains("Downloading", viewModel.DownloadComponentButtonLabel);
        await viewModel.DownloadComponentAsync();
        await viewModel.DisposeAsync();
        await download;
        Assert.Equal(1, downloads);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(0, adapter.ReadCalls);
    }

    [Fact]
    public async Task UncertainAudioResponseStillRequiresExplicitRecoveryRead()
    {
        var adapter = new MissingComponentAdapter { Status = ThxAudioOperationStatus.Indeterminate };
        await using var viewModel = new ThxAudioViewModel(adapter,
            _ => throw new InvalidOperationException("Unexpected download"), () => false);
        await viewModel.SetEnabledAsync(true);
        Assert.False(viewModel.CanDownloadComponent);
        Assert.Contains("Read the current settings before trying again", viewModel.StatusMessage);
    }

    private sealed class MissingComponentAdapter : IThxAudioAdapter
    {
        internal ThxAudioAvailability Availability { get; init; } = ThxAudioAvailability.ComponentsUnavailable;
        internal ThxAudioOperationStatus Status { get; init; } = ThxAudioOperationStatus.Unavailable;
        internal int ConnectCalls { get; private set; }
        internal int ReadCalls { get; private set; }

        public Task<SessionAudioResponse> ConnectAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            return DiscoverAsync(cancellationToken);
        }

        public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return DiscoverAsync(cancellationToken);
        }

        public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
                Status, Availability, "The THX native component is missing.", null));

        public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected audio mutation");
        public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected audio mutation");
        public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, IReadOnlyList<double> gainsDb, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected audio mutation");
        public Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint endpoint, ThxAudioEnhancementKind kind, bool enabled, int? levelPercent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected audio mutation");
    }
}
