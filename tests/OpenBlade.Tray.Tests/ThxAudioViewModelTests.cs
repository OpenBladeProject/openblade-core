using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioViewModelTests
{
    [Fact]
    public async Task PreferenceRestoreAndShutdownDoNotContactAudioBackend()
    {
        var adapter = new FakeAdapter();
        await using (var viewModel = new ThxAudioViewModel(adapter))
        {
            Assert.False(viewModel.IsEnabled);
            viewModel.ApplyPreference(true);
            Assert.True(viewModel.CanRead);
            Assert.False(viewModel.CanEdit);
        }
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task EnablingDiscoversComponentsWithoutRegisteringNativeDevice()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        await viewModel.SetEnabledAsync(true);
        Assert.Equal(["discover"], adapter.Calls);
        Assert.False(viewModel.CanEdit);
        Assert.Empty(viewModel.Presets);
    }

    [Fact]
    public async Task ExplicitReadEnablesOnlyConfirmedEndpointControls()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.SetSpatialAsync(true);
        Assert.Empty(adapter.Calls);
        await viewModel.RefreshAsync();
        Assert.Equal(["discover", "read:Speakers"], adapter.Calls);
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.SpatialEnabled);
        Assert.Equal("music", viewModel.SelectedPreset?.Id);
        viewModel.SelectEndpoint(ThxAudioEndpoint.Headphones);
        Assert.False(viewModel.CanEdit);
        Assert.Empty(viewModel.Presets);
        Assert.Equal(2, adapter.Calls.Count);
    }

    [Fact]
    public async Task IndeterminateMutationRequiresExplicitReadAndIsNeverReplayed()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Indeterminate);
        await viewModel.SetSpatialAsync(true);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.SpatialEnabled);
        await viewModel.SetSpatialAsync(true);
        Assert.Equal(1, adapter.Calls.Count(call => call == "spatial:True"));
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanEdit);
    }

    [Fact]
    public async Task MismatchedReadbackDoesNotEnableControls()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, State(ThxAudioEndpoint.Headphones)),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Assert.False(viewModel.CanEdit);
    }

    [Fact]
    public async Task MissingComponentsNeverReachNativeReadOrMutation()
    {
        var adapter = new FakeAdapter
        {
            DiscoveryResponse = new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
                ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, null, null),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SetEqualizerAsync(true);
        Assert.Equal(["discover"], adapter.Calls);
        Assert.False(viewModel.CanEdit);
    }

    [Fact]
    public async Task OnlyAdvertisedPresetsCanBeSubmitted()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SelectPresetAsync(new ThxAudioPreset("unknown", "Unknown"));
        await viewModel.SelectPresetAsync(viewModel.Presets[1]);
        Assert.Equal(["discover", "read:Speakers", "preset:game"], adapter.Calls);
    }

    [Fact]
    public async Task DisconnectDuringMutationClearsConfirmationWithoutRetry()
    {
        var adapter = new FakeAdapter { ThrowOnMutation = true };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SetSpatialAsync(true);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(1, adapter.Calls.Count(call => call == "spatial:True"));
    }

    private static ThxAudioState State(ThxAudioEndpoint endpoint = ThxAudioEndpoint.Speakers) =>
        new(endpoint, false, true, "music", [new("music", "Music"), new("game", "Game")]);

    private static SessionAudioResponse Response(ThxAudioOperationStatus status, ThxAudioState? state = null) =>
        new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), status,
            ThxAudioAvailability.Available, null, state);

    private sealed class FakeAdapter : IThxAudioAdapter
    {
        public List<string> Calls { get; } = [];
        public SessionAudioResponse DiscoveryResponse { get; init; } = Response(ThxAudioOperationStatus.Succeeded);
        public SessionAudioResponse ReadResponse { get; init; } = Response(ThxAudioOperationStatus.Succeeded, State());
        public SessionAudioResponse MutationResponse { get; set; } = Response(ThxAudioOperationStatus.Succeeded, State());
        public bool ThrowOnMutation { get; init; }

        public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken)
        {
            Calls.Add("discover");
            return Task.FromResult(DiscoveryResponse);
        }
        public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls.Add("read:" + endpoint);
            return Task.FromResult(ReadResponse);
        }
        public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            Mutate("spatial:" + enabled);
        public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            Mutate("equalizer:" + enabled);
        public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, CancellationToken cancellationToken) =>
            Mutate("preset:" + presetId);
        private Task<SessionAudioResponse> Mutate(string operation)
        {
            Calls.Add(operation);
            return ThrowOnMutation
                ? Task.FromException<SessionAudioResponse>(new IOException("Disconnected"))
                : Task.FromResult(MutationResponse);
        }
    }
}
