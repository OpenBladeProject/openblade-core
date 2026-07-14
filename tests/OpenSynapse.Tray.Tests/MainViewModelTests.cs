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
}
