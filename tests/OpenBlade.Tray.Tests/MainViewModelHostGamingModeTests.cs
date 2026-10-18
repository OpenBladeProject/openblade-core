using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public async Task HostGamingMode_PersistsOnlyTheActiveProfileWithoutFirmwareCommand(
        PowerSource source)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
            }) with
        {
            ActivePowerSource = source,
            GamingModeEnabled = null,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanEditGamingMode);
        Assert.False(viewModel.GamingModeEnabled);
        Assert.False(viewModel.SupportsFunctionKeyPrimary);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand)
            .ExecuteAsync(new ApplyOneShotLaptopControl(
                OneShotLaptopControl.GamingMode, Enabled: true));

        ProfileSet saved = Assert.IsType<ProfileSet>(client.LastProfiles);
        Assert.True(saved.ForPowerSource(source).EffectiveLaptopBehavior.GamingModeEnabled);
        Assert.Equal(GamingModeKeyPolicy.Disabled,
            saved.ForPowerSource(source).EffectiveLaptopBehavior.EffectiveGamingModeKeyPolicy);
        foreach (PowerSource other in new[] { PowerSource.PluggedIn, PowerSource.Battery, PowerSource.UsbC }
            .Where(other => other != source))
        {
            Assert.Equal(status.Profiles.ForPowerSource(other), saved.ForPowerSource(other));
        }
        Assert.Null(client.OneShotLaptopControl);
        Assert.True(viewModel.GamingModeEnabled);
        Assert.False(viewModel.HasLaptopProfileDraft);
        Assert.Null(status.GamingModeEnabled);
    }

    [Fact]
    public async Task HostGamingMode_FailedProfileSaveKeepsPreviousPresentation()
    {
        var client = new FakeServiceClient
        {
            NextResult = new ControlOperationResult(ControlOperationStatus.Failed, "Storage failed."),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
            });
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanEditGamingMode);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ApplyOneShotLaptopControlCommand)
            .ExecuteAsync(new ApplyOneShotLaptopControl(
                OneShotLaptopControl.GamingMode, Enabled: true));

        Assert.False(viewModel.GamingModeEnabled);
        Assert.Equal(status.Profiles, viewModel.Profiles);
        Assert.Null(client.OneShotLaptopControl);
        Assert.True(viewModel.CanEditGamingMode);
    }
}
