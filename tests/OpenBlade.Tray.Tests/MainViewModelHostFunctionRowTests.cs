using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
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
