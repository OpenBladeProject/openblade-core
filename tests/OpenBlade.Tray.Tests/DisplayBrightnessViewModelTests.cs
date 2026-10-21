using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class DisplayBrightnessViewModelTests
{
    [Fact]
    public async Task BrightnessChange_ConfirmsSupportedLevelAndStagesPriorReadback()
    {
        var adapter = new BrightnessAdapter();
        await using var viewModel = new DisplaySettingsViewModel(adapter)
        {
            ActivePowerSource = PowerSource.PluggedIn,
            ProfilePersistenceAvailable = true,
        };
        await viewModel.RefreshAsync();
        DisplayProfileSettingsChangedEventArgs? change = null;
        viewModel.UserSettingsChanged += (_, args) => change = args;

        await viewModel.SetBrightnessAsync(25);

        Assert.Equal((byte)20, viewModel.BrightnessPercent);
        Assert.Equal(PowerSource.PluggedIn, adapter.RequestedSource);
        Assert.Equal(1, adapter.Writes);
        Assert.NotNull(change);
        Assert.Equal((byte)70, change.PreviousSettings.BrightnessPercent);
        Assert.Equal((byte)20, change.Settings.BrightnessPercent);
        viewModel.ActiveProfileSettings = new(null, null, null) { BrightnessPercent = 25 };
        Assert.False(viewModel.HasProfileDifference);
    }

    [Fact]
    public async Task BrightnessChange_FailedReadbackDoesNotStageRequestedValue()
    {
        var adapter = new BrightnessAdapter { ConfirmWrite = false };
        await using var viewModel = new DisplaySettingsViewModel(adapter) { ActivePowerSource = PowerSource.Battery };
        await viewModel.RefreshAsync();
        int changes = 0;
        viewModel.UserSettingsChanged += (_, _) => changes++;

        await viewModel.SetBrightnessAsync(20);

        Assert.Equal(0, changes);
        Assert.Equal((byte)70, viewModel.BrightnessPercent);
        Assert.Equal(1, adapter.Writes);
    }

    [Fact]
    public async Task UnknownPowerAndUnavailableBrightness_LeaveWindowsUnchanged()
    {
        var adapter = new BrightnessAdapter();
        await using var viewModel = new DisplaySettingsViewModel(adapter);
        await viewModel.RefreshAsync();
        await viewModel.SetBrightnessAsync(20);
        Assert.Equal(0, adapter.Writes);
        viewModel.ActivePowerSource = PowerSource.Battery;
        adapter.State = adapter.State with { BrightnessPercent = null, SupportedBrightnessLevels = [] };
        await viewModel.RefreshAsync();
        Assert.False(viewModel.CanChangeBrightness);
        await viewModel.SetBrightnessAsync(20);
        Assert.Equal(0, adapter.Writes);
        Assert.Null(viewModel.CurrentProfileSettings.BrightnessPercent);
    }

    [Fact]
    public void ProfilePresentation_TracksBrightnessAndItsPendingValue()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        PowerProfile saved = profiles.Battery with { Display = new(null, null, null) { BrightnessPercent = 70 } };
        PowerProfile pending = saved with { Display = saved.Display! with { BrightnessPercent = 25 } };
        ProfilePresentation Present(PowerProfile value) => ProfilePresentationFactory.Create(
            value, profiles.WindowsPowerModes, "Battery", LaptopBehaviorProfileSettings.Unconfigured());

        ProfilePresentation result = ProfilePresentationFactory.WithPending(Present(saved), Present(pending), false);

        Assert.Equal("70%", result.DisplayBrightness.Text);
        Assert.True(result.DisplayBrightness.IsPending);
        Assert.Equal("25%", result.DisplayBrightness.PendingText);
    }

    private sealed class BrightnessAdapter : IDisplaySettingsAdapter
    {
        internal DisplaySettingsSnapshot State { get; set; } = new(true, [], null, false, false, [], null)
        {
            BrightnessPercent = 70,
            SupportedBrightnessLevels = [0, 20, 30, 70, 100],
        };
        internal int Writes { get; private set; }
        internal bool ConfirmWrite { get; init; } = true;
        internal PowerSource? RequestedSource { get; private set; }
        public Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token) => Task.FromResult(State);
        public Task<DisplaySettingsSnapshot> SetBrightnessAsync(PowerSource source, byte percent, CancellationToken token)
        {
            Writes++;
            RequestedSource = source;
            if (ConfirmWrite)
            {
                State = State with { BrightnessPercent = percent };
            }
            return Task.FromResult(State with { OperationSucceeded = ConfirmWrite });
        }
        public Task<DisplaySettingsSnapshot> SetRefreshRateAsync(PowerSource source, string key, CancellationToken token) => throw new NotSupportedException();
        public Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(PowerSource source, bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task<DisplaySettingsSnapshot> SetColorProfileAsync(PowerSource source, string key, CancellationToken token) => throw new NotSupportedException();
        public Task<DisplaySettingsSnapshot> ApplyProfileAsync(PowerSource source, DisplayProfileSettings settings, CancellationToken token) => throw new NotSupportedException();
    }
}
