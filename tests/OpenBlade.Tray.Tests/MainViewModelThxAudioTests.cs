using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(ThxAudioProductIds.Rznb12)]
    [InlineData(ThxAudioProductIds.Rznb11)]
    public async Task AudioSavePreservesOtherProfilesAndUnsavedDisplayDraft(string productId)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with
        {
            Capabilities = status.Capabilities! with
            {
                ThxAudio = new(DeviceFeatureAccess.ReadWrite, productId),
            },
        });
        await WaitUntilAsync(() => viewModel.CanSaveThxAudioProfile);
        var displayDraft = new DisplayProfileSettings("dynamic-48-240", false, "srgb");
        viewModel.RecordDisplayProfileChange(PowerSource.PluggedIn, displayDraft);
        ThxAudioProfileSettings audio = AudioSettings();

        Assert.False(await viewModel.SaveThxAudioProfileAsync(audio, PowerSource.Battery, CancellationToken.None));
        Assert.Null(client.LastProfiles);
        Assert.True(await viewModel.SaveThxAudioProfileAsync(audio, PowerSource.PluggedIn, CancellationToken.None));

        ProfileSet saved = Assert.IsType<ProfileSet>(client.LastProfiles);
        Assert.Equal(audio, saved.PluggedIn.ThxAudio);
        Assert.Equal(status.Profiles.PluggedIn with { ThxAudio = audio }, saved.PluggedIn);
        Assert.Equal(status.Profiles.Battery, saved.Battery);
        Assert.Equal(status.Profiles.UsbC, saved.UsbC);
        Assert.True(viewModel.HasDisplayProfileDraft);
        Assert.True(viewModel.PluggedInProfilePresentation.DisplayRefreshRate.IsPending);
        Assert.Equal(audio, viewModel.ActiveSavedThxAudioProfile);
    }

    [Fact]
    public async Task AudioSaveRequiresExactCapabilityEvenOnVerifiedLaptop()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(verified: true, blocked: false));
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.False(viewModel.CanSaveThxAudioProfile);
        Assert.False(await viewModel.SaveThxAudioProfileAsync(AudioSettings(), PowerSource.PluggedIn, CancellationToken.None));
        Assert.Null(client.LastProfiles);
    }

    [Fact]
    public async Task BlockedAudioSaveRetainsConfirmedProfile()
    {
        var client = new FakeServiceClient
        {
            NextResult = new(ControlOperationStatus.Blocked, "Configuration is quarantined."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false);
        client.Publish(status with
        {
            Capabilities = status.Capabilities! with
            {
                ThxAudio = new(DeviceFeatureAccess.ReadWrite, ThxAudioProductIds.Rznb12),
            },
        });
        await WaitUntilAsync(() => viewModel.CanSaveThxAudioProfile);

        Assert.False(await viewModel.SaveThxAudioProfileAsync(
            AudioSettings(), PowerSource.PluggedIn, CancellationToken.None));
        Assert.NotNull(client.LastProfiles);
        Assert.Null(viewModel.ActiveSavedThxAudioProfile);
        Assert.Equal(status.Profiles, viewModel.Profiles);
    }

    private static ThxAudioProfileSettings AudioSettings()
    {
        static ThxAudioEndpointSettings Endpoint(int count, bool speaker) => new(false, false,
            ThxEqualizerPreset.Music, new(new double[count], new double[count], new double[count],
                new double[count], new double[count]), new(false, 50), speaker ? new(false, 50) : null);
        return new(false, Endpoint(7, true), Endpoint(10, false), []);
    }
}
