using OpenSynapse.Contracts;
using OpenSynapse.Tray;

namespace OpenSynapse.Tray.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void CreateDisconnected_IsReadOnlyAndUsesSafeDefaults()
    {
        MainViewModel viewModel = MainViewModel.CreateDisconnected();

        Assert.True(viewModel.IsReadOnly);
        Assert.Equal("Service unavailable", viewModel.ConnectionStatus);
        Assert.Equal(PerformancePreset.Balanced, viewModel.SelectedPerformance);
        Assert.Equal(FanMode.Automatic, viewModel.SelectedFanMode);
        Assert.Equal(80, viewModel.ChargeLimitPercent);
        Assert.Equal(ThemePreference.FollowWindows, viewModel.Theme);
        Assert.Equal("Telemetry unavailable", viewModel.TelemetryStatus);
        Assert.False(viewModel.CanControlHardware);
    }

    [Fact]
    public void ServiceStatus_IsUsedEverywhereConnectionStateIsShown()
    {
        MainViewModel viewModel = MainViewModel.CreateDisconnected();

        viewModel.ApplyStatus(new ServiceStatusSnapshot(
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: ["Razer Synapse"],
            Message: "Razer Synapse conflict detected"));

        Assert.Equal("Razer Synapse conflict detected", viewModel.ConnectionStatus);
        Assert.True(viewModel.IsReadOnly);
    }

    [Fact]
    public void ApplyStatus_EnablesControlsOnlyForVerifiedConflictFreeDevice()
    {
        MainViewModel viewModel = MainViewModel.CreateDisconnected();

        viewModel.ApplyStatus(new ServiceStatusSnapshot(
            DeviceVerified: true,
            WritesBlocked: false,
            Conflicts: [],
            Message: "Ready"));

        Assert.False(viewModel.IsReadOnly);
        Assert.Equal("Ready", viewModel.ConnectionStatus);
    }

    [Fact]
    public void Theme_CanSelectLightDarkOrWindowsFollowing()
    {
        MainViewModel viewModel = MainViewModel.CreateDisconnected();

        viewModel.Theme = ThemePreference.Dark;

        Assert.Equal(ThemePreference.Dark, viewModel.Theme);
        Assert.Contains(ThemePreference.Light, viewModel.ThemeOptions);
        Assert.Contains(ThemePreference.FollowWindows, viewModel.ThemeOptions);
    }
}
