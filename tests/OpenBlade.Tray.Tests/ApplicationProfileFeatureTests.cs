using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task ApplicationContextOnlyChange_UpdatesNameWithoutRedundantNotifications()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        var profile = new ApplicationProfileDefinition(Guid.NewGuid(), "Game", @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet());
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false) with
        {
            Profiles = ProfileDefaults.CreateSafeDefaults() with { ApplicationProfiles = [profile] },
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        var names = new System.Collections.Concurrent.ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ActiveApplicationProfileName))
            {
                names.Enqueue(viewModel.ActiveApplicationProfileName);
            }
        };

        client.Publish(baseline with { Revision = baseline.Revision + 1, ActiveApplicationProfileId = profile.Id });
        await WaitUntilAsync(() => names.Count == 1);

        Assert.Equal(["Game"], names.ToArray());
    }

    [Fact]
    public void ApplicationDraft_EmitsNullForUnconfiguredPowerSources()
    {
        var draft = new ApplicationProfileDraft
        {
            Name = "Game",
            ExecutablePath = @"C:\Games\game.exe",
        };
        draft.SetSource(PowerSource.Battery, new ApplicationPowerProfileOverride
        {
            Performance = PerformancePreset.BatterySaver,
        });

        ApplicationProfileDefinition profile = draft.Build();

        Assert.Null(profile.Overrides.PluggedIn);
        Assert.Equal(
            PerformancePreset.BatterySaver,
            profile.Overrides.Battery?.Performance);
        Assert.Null(profile.Overrides.UsbC);
    }

    [Fact]
    public void ApplicationDraft_RejectsAProfileWithNoSettings()
    {
        var draft = new ApplicationProfileDraft
        {
            Name = "Game",
            ExecutablePath = @"C:\Games\game.exe",
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(draft.Build);

        Assert.Equal("Choose at least one application profile setting.", exception.Message);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public async Task ApplicationProfileSave_RejectedResultKeepsConfirmedProfiles(
        ControlOperationStatus status)
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(status, "Not stored."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        var profile = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                Battery = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.BatterySaver,
                },
            });

        bool saved = await viewModel.SaveApplicationProfileAsync(
            profile,
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(baseline.Profiles, viewModel.Profiles);
        Assert.Contains(profile, Assert.IsType<ProfileSet>(client.LastProfiles).ApplicationProfiles);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Applied)]
    public async Task ApplicationProfileSave_PersistedResultAdvancesConfirmedProfiles(
        ControlOperationStatus status)
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(status, "Stored."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(verified: true, blocked: false);
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        var profile = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                Battery = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.BatterySaver,
                },
            });

        bool saved = await viewModel.SaveApplicationProfileAsync(
            profile,
            CancellationToken.None);

        Assert.True(saved);
        Assert.Contains(profile, viewModel.Profiles.ApplicationProfiles);
    }
}
