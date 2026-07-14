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
}
