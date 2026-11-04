using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task ProfilePersistence_QueuedRowEditsPreserveBothAcceptedChanges()
    {
        ApplicationProfileDefinition first = PersistenceApplication("first");
        ApplicationProfileDefinition second = PersistenceApplication("second");
        var client = new GatedProfileServiceClient(PersistenceStatus(first, second));
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(first.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> secondSave = viewModel.SetApplicationProfileEnabledAsync(second.Id, false, CancellationToken.None);
        Assert.Single(client.SavedCandidates);
        client.AllowFirstSave.TrySetResult();

        Assert.True(await firstSave);
        Assert.True(await secondSave);
        Assert.All(viewModel.ApplicationProfiles, profile => Assert.False(profile.Enabled));
        Assert.All(client.SavedCandidates[^1].ApplicationProfiles, profile => Assert.False(profile.Enabled));
    }

    [Fact]
    public async Task ProfilePersistence_QueuedToggleCannotRecreateADeletedRow()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile));
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task<bool> removal = viewModel.RemoveApplicationProfileAsync(profile.Id, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> toggle = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        client.AllowFirstSave.TrySetResult();

        Assert.True(await removal);
        Assert.True(await toggle);
        Assert.Empty(viewModel.ApplicationProfiles);
        Assert.All(client.SavedCandidates, candidate => Assert.Empty(candidate.ApplicationProfiles));
    }

    [Fact]
    public async Task ProfilePersistence_AudioSaveRebasesOnAcceptedApplicationEdit()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        ServiceStatusSnapshot status = PersistenceStatus(profile);
        status = status with
        {
            Capabilities = status.Capabilities! with
            {
                ThxAudio = new(DeviceFeatureAccess.ReadWrite, ThxAudioProductIds.Rznb12),
            },
        };
        var client = new GatedProfileServiceClient(status);
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanSaveThxAudioProfile);

        Task<bool> applicationSave = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ThxAudioProfileSettings audio = AudioSettings();
        Task<bool> audioSave = viewModel.SaveThxAudioProfileAsync(audio, PowerSource.PluggedIn, CancellationToken.None);
        Assert.Single(client.SavedCandidates);
        client.AllowFirstSave.TrySetResult();

        Assert.True(await applicationSave);
        Assert.True(await audioSave);
        Assert.False(Assert.Single(viewModel.ApplicationProfiles).Enabled);
        Assert.Equal(audio, viewModel.Profiles.PluggedIn.ThxAudio);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public async Task ProfilePersistence_RejectedImportPreservesConfirmedProfilesAndDraft(
        ControlOperationStatus result)
    {
        ServiceStatusSnapshot status = PersistenceStatus();
        var client = new GatedProfileServiceClient(status) { Result = result };
        client.AllowFirstSave.TrySetResult();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        viewModel.RecordDisplayProfileChange(PowerSource.PluggedIn, new("dynamic-48-240", false, "srgb"));

        bool saved = await viewModel.ImportProfilesAsync(
            status.Profiles with { BatteryPolicy = BatteryPolicy.Create(60) }, CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(status.Profiles, viewModel.Profiles);
        Assert.True(viewModel.HasDisplayProfileDraft);
    }

    [Fact]
    public async Task ProfilePersistence_LostReplyReadsCommittedStateBeforeTheNextEdit()
    {
        ApplicationProfileDefinition first = PersistenceApplication("first");
        ApplicationProfileDefinition second = PersistenceApplication("second");
        var client = new GatedProfileServiceClient(PersistenceStatus(first, second))
        {
            LoseFirstReply = true,
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(first.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> secondSave = viewModel.SetApplicationProfileEnabledAsync(second.Id, false, CancellationToken.None);
        client.AllowFirstSave.TrySetResult();

        Assert.False(await firstSave);
        Assert.True(await secondSave);
        Assert.Equal(1, client.StatusReads);
        Assert.Equal(2, client.SavedCandidates.Count);
        Assert.All(viewModel.ApplicationProfiles, profile => Assert.False(profile.Enabled));
    }

    [Fact]
    public async Task ProfilePersistence_BufferedPreSaveStatusCannotEraseTheAcceptedEdit()
    {
        ApplicationProfileDefinition first = PersistenceApplication("first");
        ApplicationProfileDefinition second = PersistenceApplication("second");
        ServiceStatusSnapshot baseline = PersistenceStatus(first, second);
        var client = new GatedProfileServiceClient(baseline);
        client.AllowFirstSave.TrySetResult();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Assert.True(await viewModel.SetApplicationProfileEnabledAsync(first.Id, false, CancellationToken.None));

        // This metadata change was read before the save but delivered after its
        // reply. A revision-only snapshot correctly takes the live-readings shortcut.
        client.PublishBufferedStatus(baseline with
        {
            Revision = baseline.Revision + 1,
            Message = "Pre-save observation completed.",
        });
        await WaitUntilAsync(() => viewModel.ApplicationProfiles.Single(profile => profile.Id == first.Id).Enabled);
        Assert.True(await viewModel.SetApplicationProfileEnabledAsync(second.Id, false, CancellationToken.None));

        Assert.Equal(1, client.StatusReads);
        Assert.All(client.SavedCandidates[^1].ApplicationProfiles, profile => Assert.False(profile.Enabled));
        Assert.All(viewModel.ApplicationProfiles, profile => Assert.False(profile.Enabled));
    }

    [Fact]
    public async Task ProfilePersistence_CanceledQueuedEditDoesNotSubmit()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile));
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<bool> queuedSave = viewModel.RemoveApplicationProfileAsync(profile.Id, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedSave);
        client.AllowFirstSave.TrySetResult();

        Assert.True(await firstSave);
        Assert.Single(client.SavedCandidates);
        Assert.Single(viewModel.ApplicationProfiles);
    }

    [Fact]
    public async Task ProfilePersistence_UnavailableReconciliationStopsTheQueuedWrite()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile))
        {
            LoseFirstReply = true,
            FailStatusReads = true,
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> queuedSave = viewModel.RemoveApplicationProfileAsync(profile.Id, CancellationToken.None);
        client.AllowFirstSave.TrySetResult();

        Assert.False(await firstSave);
        Assert.False(await queuedSave);
        Assert.Contains("The service status is unavailable.", viewModel.OperationStatusMessage, StringComparison.Ordinal);
        Assert.Single(client.SavedCandidates);
    }

    [Fact]
    public async Task ProfilePersistence_CommandRefreshFailureReportsStatusAndStillDisposes()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile))
        {
            LoseFirstReply = true,
            FailStatusReads = true,
        };
        client.AllowFirstSave.TrySetResult();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Assert.False(await viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None));
        viewModel.RecordDisplayProfileChange(PowerSource.PluggedIn, new("dynamic-48-240", false, "srgb"));
        var command = Assert.IsType<AsyncRelayCommand>(viewModel.SaveDisplayProfileDraftCommand);
        Assert.True(command.CanExecute(null));

        command.Execute(null);
        await WaitUntilAsync(() => client.StatusReads == 1 && !command.IsRunning);

        Assert.Contains("The service status is unavailable.", viewModel.OperationStatusMessage, StringComparison.Ordinal);
        Assert.True(viewModel.HasDisplayProfileDraft);
        Assert.Single(client.SavedCandidates);
        await viewModel.DisposeAsync();
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task ProfilePersistence_UnsupportedImportReplacesPriorOperationFeedback()
    {
        ServiceStatusSnapshot status = PersistenceStatus();
        status = status with { Capabilities = status.Capabilities! with { ChargeLimits = new HashSet<int> { 80 } } };
        var client = new GatedProfileServiceClient(status) { Result = ControlOperationStatus.Blocked };
        client.AllowFirstSave.TrySetResult();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Assert.False(await viewModel.ImportProfilesAsync(status.Profiles, CancellationToken.None));
        ProfileSet unsupported = status.Profiles with { BatteryPolicy = BatteryPolicy.Create(60) };

        Assert.False(await viewModel.ImportProfilesAsync(unsupported, CancellationToken.None));

        Assert.Equal(TrayText.Get("The imported profiles include settings that are unavailable on this laptop."),
            viewModel.OperationStatusMessage);
        Assert.Single(client.SavedCandidates);
    }

    [Fact]
    public async Task ProfilePersistence_QueuedGamingPolicyRefusesAChangedPowerSource()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        ServiceStatusSnapshot status = PersistenceStatus(profile);
        status = status with
        {
            Capabilities = status.Capabilities! with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.GamingMode },
            },
        };
        var client = new GatedProfileServiceClient(status);
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanEditGamingModeKeyPolicy);
        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> policy = viewModel.SetGamingModeKeyPolicyAsync(true, true, CancellationToken.None);
        client.PublishCurrentStatus(status with { Revision = status.Revision + 1, ActivePowerSource = PowerSource.Battery });
        await WaitUntilAsync(() => viewModel.ActivePowerSource == PowerSource.Battery);
        client.AllowFirstSave.TrySetResult();

        Assert.True(await firstSave);
        Assert.False(await policy);
        Assert.Single(client.SavedCandidates);
        Assert.Equal(TrayText.Get("The power source changed. Review the current profile and try again."),
            viewModel.OperationStatusMessage);
    }

    [Fact]
    public async Task ProfilePersistence_DisconnectedReconciliationReportsWhyTheEditWasRefused()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile));
        client.AllowFirstSave.TrySetResult();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Assert.True(await viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None));
        client.CompleteWatch();
        await WaitUntilAsync(() => !viewModel.IsConnected);

        Assert.False(await viewModel.RemoveApplicationProfileAsync(profile.Id, CancellationToken.None));

        Assert.Equal(TrayText.Get("Reconnecting to service"), viewModel.OperationStatusMessage);
        Assert.Single(client.SavedCandidates);
    }

    [Fact]
    public async Task ProfilePersistence_DisposeCancelsActiveAndQueuedDirectActions()
    {
        ApplicationProfileDefinition profile = PersistenceApplication("game");
        var client = new GatedProfileServiceClient(PersistenceStatus(profile));
        var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        Task<bool> firstSave = viewModel.SetApplicationProfileEnabledAsync(profile.Id, false, CancellationToken.None);
        await client.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> queuedSave = viewModel.RemoveApplicationProfileAsync(profile.Id, CancellationToken.None);

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSave);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedSave);
        Assert.Single(client.SavedCandidates);
    }

    private static ApplicationProfileDefinition PersistenceApplication(string name) => new(
        Guid.NewGuid(), name, $@"C:\Games\{name}.exe", ApplicationActivationMode.Foreground,
        new ApplicationProfileOverrideSet
        {
            Battery = new() { Performance = PerformancePreset.BatterySaver },
        });

    private static ServiceStatusSnapshot PersistenceStatus(params ApplicationProfileDefinition[] applications)
    {
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        return status with { Profiles = status.Profiles with { ApplicationProfiles = applications } };
    }

    private sealed class GatedProfileServiceClient(ServiceStatusSnapshot status)
        : OpenBlade.Testing.UnavailableServiceClient, IAsyncDisposable
    {
        private readonly Channel<ServiceWatchUpdate> bufferedStatuses = Channel.CreateUnbounded<ServiceWatchUpdate>();
        public List<ProfileSet> SavedCandidates { get; } = [];
        public TaskCompletionSource FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ControlOperationStatus Result { get; init; } = ControlOperationStatus.Stored;
        public bool LoseFirstReply { get; init; }
        public bool FailStatusReads { get; init; }
        public int StatusReads { get; private set; }
        public bool Disposed { get; private set; }

        public void PublishBufferedStatus(ServiceStatusSnapshot snapshot) =>
            bufferedStatuses.Writer.TryWrite(new ServiceWatchUpdate.Connected(snapshot));

        public void PublishCurrentStatus(ServiceStatusSnapshot snapshot)
        {
            status = snapshot;
            PublishBufferedStatus(snapshot);
        }

        public void CompleteWatch() => bufferedStatuses.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ServiceWatchUpdate.Connected(status);
            await foreach (ServiceWatchUpdate update in bufferedStatuses.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StatusReads++;
            if (FailStatusReads)
            {
                throw new IOException("The service status is unavailable.");
            }
            return Task.FromResult(status);
        }

        public override async Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token)
        {
            SavedCandidates.Add(profiles);
            bool first = SavedCandidates.Count == 1;
            if (first)
            {
                FirstSaveStarted.TrySetResult();
                await AllowFirstSave.Task.WaitAsync(token);
            }
            if (Result is ControlOperationStatus.Stored or ControlOperationStatus.Applied)
            {
                status = status with { Revision = status.Revision + 2, Profiles = profiles };
            }
            if (first && LoseFirstReply)
            {
                throw new IOException("The service committed the profile before the connection closed.");
            }
            return new(Result, "Profile save result.");
        }
    }
}
