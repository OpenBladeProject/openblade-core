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

        viewModel.ApplyStatus(CreateStatus(
            deviceVerified: false,
            writesBlocked: true,
            conflicts: ["Razer Synapse"],
            message: "Razer Synapse conflict detected"));

        Assert.Equal("Razer Synapse conflict detected", viewModel.ConnectionStatus);
        Assert.True(viewModel.IsReadOnly);
    }

    [Fact]
    public void ApplyStatus_EnablesControlsOnlyForVerifiedConflictFreeDevice()
    {
        MainViewModel viewModel = MainViewModel.CreateDisconnected();

        viewModel.ApplyStatus(CreateStatus(
            deviceVerified: true,
            writesBlocked: false,
            conflicts: [],
            message: "Ready"));

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

    private static ServiceStatusSnapshot CreateStatus(
        bool deviceVerified,
        bool writesBlocked,
        IReadOnlyList<string> conflicts,
        string message)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            0,
            deviceVerified,
            writesBlocked,
            conflicts,
            message,
            deviceVerified ? DeviceCapabilities.SafeTargetDefaults() : DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                profiles.BatteryPolicy),
            new TelemetrySnapshot(DateTimeOffset.MinValue, PowerSource.PluggedIn, null, null, null, null),
            null,
            null,
            null,
            false);
    }
}
