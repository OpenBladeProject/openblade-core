using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.Stored, false)]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.Stored, true)]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.Rejected, false)]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.Rejected, true)]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.ReplyLost, false)]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary, ProfileSaveOutcome.ReplyLost, true)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.Stored, false)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.Stored, true)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.Rejected, false)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.Rejected, true)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.ReplyLost, false)]
    [InlineData(OneShotLaptopControl.GamingMode, ProfileSaveOutcome.ReplyLost, true)]
    public async Task HostLaptopPreference_StartupDraftSavePreservesAuthoritativeDirectSetting(
        OneShotLaptopControl control, ProfileSaveOutcome saveOutcome, bool dashboard)
    {
        ServiceStatusSnapshot initial = HostDraftStatus(hostManaged: true);
        var client = new HostDraftServiceClient(initial);
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanEditStartupAnimation
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);
        var command = Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand);
        await command.ExecuteAsync(new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: true));
        Assert.True(viewModel.HasLaptopProfileDraft);
        client.NextSaveOutcome = saveOutcome;
        await command.ExecuteAsync(ChangedHostPreference(control));
        Assert.True(viewModel.HasLaptopProfileDraft);
        Assert.Single(client.FirmwareRequests);
        bool committed = saveOutcome != ProfileSaveOutcome.Rejected;
        AssertHostPreference(client.Status.Profiles.PluggedIn.EffectiveLaptopBehavior, control, committed);
        await Assert.IsType<AsyncRelayCommand>(
            dashboard ? viewModel.SaveProfilesCommand : viewModel.SaveLaptopProfileDraftCommand).ExecuteAsync(null);

        LaptopBehaviorProfileSettings saved = client.Status.Profiles.PluggedIn.EffectiveLaptopBehavior;
        Assert.True(saved.StartupAnimationEnabled);
        AssertHostPreference(saved, control, committed);
        Assert.Equal(initial.Profiles.PluggedIn.EffectiveLaptopBehavior.GamingModeKeyPolicy, saved.GamingModeKeyPolicy);
        Assert.Equal(initial.Profiles.Battery, client.Status.Profiles.Battery);
        Assert.Equal(initial.Profiles.UsbC, client.Status.Profiles.UsbC);
        Assert.Equal(2, client.SavedCandidates.Count);
        Assert.True(client.StatusReads > 0);
        Assert.False(viewModel.HasLaptopProfileDraft);
    }

    [Theory]
    [InlineData(OneShotLaptopControl.FunctionKeyPrimary)]
    [InlineData(OneShotLaptopControl.GamingMode)]
    public async Task HardwareLaptopPreference_StillStagesUntilTheLaptopDraftIsSaved(
        OneShotLaptopControl control)
    {
        ServiceStatusSnapshot initial = HostDraftStatus(hostManaged: false);
        var client = new HostDraftServiceClient(initial);
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanEditStartupAnimation
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);
        var command = Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand);
        await command.ExecuteAsync(new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: true));
        await command.ExecuteAsync(ChangedHostPreference(control));
        Assert.Equal(2, client.FirmwareRequests.Count);
        Assert.Empty(client.SavedCandidates);
        AssertHostPreference(client.Status.Profiles.PluggedIn.EffectiveLaptopBehavior, control, changed: false);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveLaptopProfileDraftCommand).ExecuteAsync(null);
        Assert.Single(client.SavedCandidates);
        AssertHostPreference(client.Status.Profiles.PluggedIn.EffectiveLaptopBehavior, control, changed: true);
        Assert.True(client.Status.Profiles.PluggedIn.EffectiveLaptopBehavior.StartupAnimationEnabled);
    }

    [Fact]
    public async Task HostLaptopPreference_UnsetDefaultsDoNotCreateAStagedSaveOpportunity()
    {
        ServiceStatusSnapshot initial = HostDraftStatus(hostManaged: true);
        initial = initial with
        {
            Capabilities = initial.Capabilities! with { OneShotLaptopControls = new HashSet<OneShotLaptopControl>() },
            Profiles = ProfileDefaults.CreateSafeDefaults(),
        };
        var client = new HostDraftServiceClient(initial);
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.CanEditFunctionKeyPrimary
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);
        Assert.False(viewModel.HasLaptopPageProfileSaveOpportunity);
        Assert.False(viewModel.SaveLaptopProfileDraftCommand.CanExecute(null));
        Assert.Empty(client.SavedCandidates);
    }

    private static ApplyOneShotLaptopControl ChangedHostPreference(OneShotLaptopControl control) =>
        control == OneShotLaptopControl.FunctionKeyPrimary
            ? new(control, FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys)
            : new(control, Enabled: true);

    private static void AssertHostPreference(
        LaptopBehaviorProfileSettings settings, OneShotLaptopControl control, bool changed)
    {
        Assert.Equal(control == OneShotLaptopControl.FunctionKeyPrimary && changed
            ? FunctionKeyPrimaryMode.MultimediaKeys : FunctionKeyPrimaryMode.FunctionKeys, settings.FunctionKeyPrimary);
        Assert.Equal(control == OneShotLaptopControl.GamingMode && changed, settings.GamingModeEnabled);
    }

    private static ServiceStatusSnapshot HostDraftStatus(bool hostManaged)
    {
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                OneShotLaptopControls = hostManaged
                    ? new HashSet<OneShotLaptopControl> { OneShotLaptopControl.StartupAnimation }
                    : new HashSet<OneShotLaptopControl>(Enum.GetValues<OneShotLaptopControl>()),
                HostManagedLaptopControls = hostManaged
                    ? new HashSet<OneShotLaptopControl> { OneShotLaptopControl.FunctionKeyPrimary, OneShotLaptopControl.GamingMode }
                    : new HashSet<OneShotLaptopControl>(),
            });
        return initial with
        {
            StartupAnimationEnabled = false,
            FunctionKeyPrimary = hostManaged ? null : FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = hostManaged ? null : false,
            Profiles = initial.Profiles with
            {
                PluggedIn = initial.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(false, FunctionKeyPrimaryMode.FunctionKeys, false)
                    {
                        GamingModeKeyPolicy = new GamingModeKeyPolicy(true, false),
                    },
                },
            },
        };
    }

    public enum ProfileSaveOutcome
    {
        Stored,
        Rejected,
        ReplyLost,
    }

    private sealed class HostDraftServiceClient(ServiceStatusSnapshot initial) : OpenBlade.Testing.UnavailableServiceClient
    {
        public ServiceStatusSnapshot Status { get; private set; } = initial;
        public List<ApplyOneShotLaptopControl> FirmwareRequests { get; } = [];
        public List<ProfileSet> SavedCandidates { get; } = [];
        public ProfileSaveOutcome NextSaveOutcome { get; set; }
        public int StatusReads { get; private set; }

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new ServiceWatchUpdate.Connected(Status);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StatusReads++;
            return Task.FromResult(Status);
        }

        public override Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            FirmwareRequests.Add(value);
            Status = Status with
            {
                StartupAnimationEnabled = value.Control == OneShotLaptopControl.StartupAnimation ? value.Enabled : Status.StartupAnimationEnabled,
                FunctionKeyPrimary = value.Control == OneShotLaptopControl.FunctionKeyPrimary ? value.FunctionKeyMode : Status.FunctionKeyPrimary,
                GamingModeEnabled = value.Control == OneShotLaptopControl.GamingMode ? value.Enabled : Status.GamingModeEnabled,
            };
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Applied, "Applied."));
        }

        public override Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SavedCandidates.Add(profiles);
            ProfileSaveOutcome outcome = NextSaveOutcome;
            NextSaveOutcome = ProfileSaveOutcome.Stored;
            if (outcome == ProfileSaveOutcome.Rejected)
            {
                return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Failed, "Rejected."));
            }
            Status = Status with { Revision = Status.Revision + 1, Profiles = profiles };
            if (outcome == ProfileSaveOutcome.ReplyLost)
            {
                throw new IOException("The save committed before its reply was lost.");
            }
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Stored."));
        }
    }

    [Fact]
    public async Task HostFunctionRow_DiscardingFirmwareDraftDoesNotReplayHostPreferences()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                OneShotLaptopControls = new HashSet<OneShotLaptopControl> { OneShotLaptopControl.StartupAnimation },
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary, OneShotLaptopControl.GamingMode,
                },
            });
        ServiceStatusSnapshot status = initial with
        {
            StartupAnimationEnabled = false,
            Profiles = initial.Profiles with
            {
                PluggedIn = initial.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(false, FunctionKeyPrimaryMode.MultimediaKeys, true),
                },
            },
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanEditStartupAnimation
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.MultimediaKeys);
        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand).ExecuteAsync(
            new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: true));
        Assert.True(viewModel.HasLaptopProfileDraft);
        int before = client.MutationCalls;
        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardLaptopProfileDraftCommand).ExecuteAsync(null);
        Assert.Equal(before + 1, client.MutationCalls);
        Assert.Equal(new ApplyOneShotLaptopControl(OneShotLaptopControl.StartupAnimation, Enabled: false),
            client.OneShotLaptopControl);
        Assert.False(viewModel.HasLaptopProfileDraft);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, viewModel.FunctionKeyPrimary);
        Assert.True(viewModel.GamingModeEnabled);
    }
    [Theory]
    [InlineData(PowerSource.PluggedIn, false)]
    [InlineData(PowerSource.Battery, false)]
    [InlineData(PowerSource.UsbC, false)]
    [InlineData(PowerSource.PluggedIn, true)]
    public async Task HostFunctionRow_PersistsIntentWithoutFirmwareAndRetainsItOnSaveFailure(
        PowerSource power, bool reject)
    {
        var client = new FakeServiceClient();
        if (reject)
        {
            client.NextResult = new ControlOperationResult(ControlOperationStatus.Failed, "Storage failed.");
        }
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary,
                },
            }) with
        {
            ActivePowerSource = power,
            FunctionKeyPrimary = null,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanEditFunctionKeyPrimary
            && viewModel.FunctionKeyPrimary == FunctionKeyPrimaryMode.FunctionKeys);
        Assert.True(viewModel.SupportsFunctionKeyPrimary);
        Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, viewModel.FunctionKeyPrimary);
        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand).ExecuteAsync(
            new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary,
                FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys));
        Assert.Null(client.OneShotLaptopControl);
        Assert.Equal(reject ? FunctionKeyPrimaryMode.FunctionKeys : FunctionKeyPrimaryMode.MultimediaKeys,
            viewModel.FunctionKeyPrimary);
        Assert.False(viewModel.HasLaptopProfileDraft);
        if (reject)
        {
            Assert.Equal(status.Profiles, viewModel.Profiles);
            return;
        }
        ProfileSet saved = Assert.IsType<ProfileSet>(client.LastProfiles);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys,
            saved.ForPowerSource(power).EffectiveLaptopBehavior.FunctionKeyPrimary);
        foreach (PowerSource other in new[] { PowerSource.PluggedIn, PowerSource.Battery, PowerSource.UsbC }.Where(x => x != power))
        {
            Assert.Equal(status.Profiles.ForPowerSource(other), saved.ForPowerSource(other));
        }
        Assert.Null(status.FunctionKeyPrimary);
    }
}
