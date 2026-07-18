using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class DisplaySettingsViewModelTests
{
    private static readonly DisplayRefreshRateOption Fixed60 = new(
        "fixed-60",
        "60 Hz");
    private static readonly DisplayRefreshRateOption Fixed240 = new(
        "fixed-240",
        "240 Hz");
    private static readonly DisplayRefreshRateOption Dynamic = new(
        "dynamic-48-240",
        "Dynamic (48-240 Hz)",
        IsDynamic: true);
    private static readonly DisplayColorProfileOption Native = new(
        "native",
        "Native");
    private static readonly DisplayColorProfileOption Srgb = new(
        "srgb",
        "sRGB");

    [Fact]
    public async Task Refresh_HdrDisablesColorProfileAndExplainsWhy()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: true, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsHdrEnabled);
        Assert.False(viewModel.CanChangeColorProfile);
        Assert.Equal(
            "Turn off HDR to change color profiles.",
            viewModel.ColorProfileDescription);
        Assert.Equal(Fixed240.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.Equal(Native.Key, viewModel.SelectedColorProfile?.Key);
    }

    [Fact]
    public async Task Refresh_PreservesDynamicRefreshRateAsItsOwnSelection()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Dynamic.Key, hdr: false, colorKey: Srgb.Key));
        await using var viewModel = CreateViewModel(adapter);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.SelectedRefreshRate?.IsDynamic);
        Assert.Equal("dynamic-48-240", viewModel.SelectedRefreshRate?.Key);
        Assert.Equal("Dynamic (48-240 Hz)", viewModel.SelectedRefreshRate?.Label);
    }

    [Fact]
    public async Task Changes_AreSerializedAndUseAdapterReadback()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            BlockFirstRefreshChange = true,
        };
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();

        Task first = viewModel.SelectRefreshRateAsync(Fixed240);
        await adapter.FirstRefreshChangeStarted.Task;
        Task second = viewModel.SelectRefreshRateAsync(Dynamic);

        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeRefreshRate);
        Assert.False(viewModel.CanChangeHdr);
        Assert.False(viewModel.CanChangeColorProfile);
        adapter.ReleaseFirstRefreshChange.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([Fixed240.Key, Dynamic.Key], adapter.RefreshRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.Equal(Dynamic.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanChangeRefreshRate);
        Assert.True(viewModel.CanChangeHdr);
        Assert.True(viewModel.CanChangeColorProfile);
    }

    [Fact]
    public async Task UnchangedBackgroundRefresh_ReusesOptionsAndDoesNotToggleControls()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        IReadOnlyList<DisplayRefreshRateOption> refreshOptions = viewModel.RefreshRateOptions;
        IReadOnlyList<DisplayColorProfileOption> colorOptions = viewModel.ColorProfileOptions;
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        adapter.SetExternalState(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));

        await viewModel.RefreshAsync();

        Assert.Same(refreshOptions, viewModel.RefreshRateOptions);
        Assert.Same(colorOptions, viewModel.ColorProfileOptions);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.RefreshRateOptions), notifications);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.ColorProfileOptions), notifications);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.IsBusy), notifications);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.CanChangeRefreshRate), notifications);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.CanChangeHdr), notifications);
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.CanChangeColorProfile), notifications);
    }

    [Fact]
    public async Task Refresh_ReportsColorProfileChangedOutsideOpenBlade()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Srgb.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.SetExternalState(Snapshot(
            refreshKey: Fixed60.Key,
            hdr: false,
            colorKey: Native.Key));

        await viewModel.RefreshAsync();

        Assert.Equal(Native.Key, viewModel.SelectedColorProfile?.Key);
        Assert.Equal("Color profile changed outside OpenBlade.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulUserChange_RaisesProfileDraftWithFullCurrentDisplayState()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        DisplayProfileSettings? previous = null;
        DisplayProfileSettings? changed = null;
        PowerSource? changedSource = null;
        viewModel.UserSettingsChanged += (_, args) =>
        {
            previous = args.PreviousSettings;
            changed = args.Settings;
            changedSource = args.PowerSource;
        };

        await viewModel.SelectRefreshRateAsync(Dynamic);

        Assert.Equal(
            new DisplayProfileSettings(Dynamic.Key, false, Native.Key),
            changed);
        Assert.Equal(
            new DisplayProfileSettings(Fixed60.Key, false, Native.Key),
            previous);
        Assert.Equal(PowerSource.PluggedIn, changedSource);
    }

    [Fact]
    public async Task RefreshingExternalChange_DoesNotRaiseUserProfileDraft()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        int changedCount = 0;
        viewModel.UserSettingsChanged += (_, _) => changedCount++;
        adapter.SetExternalState(Snapshot(
            refreshKey: Dynamic.Key,
            hdr: false,
            colorKey: Srgb.Key));

        await viewModel.RefreshAsync();

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task ApplyProfile_UsesReadbackAndDoesNotRaiseUserProfileDraft()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: true, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        int changedCount = 0;
        viewModel.UserSettingsChanged += (_, _) => changedCount++;

        bool applied = await viewModel.ApplyProfileAsync(
            new DisplayProfileSettings(Dynamic.Key, false, Srgb.Key));

        Assert.Equal([Dynamic.Key], adapter.RefreshRequests);
        Assert.Equal([false], adapter.HdrRequests);
        Assert.Equal([Srgb.Key], adapter.ColorRequests);
        Assert.Equal(Dynamic.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.Equal(Srgb.Key, viewModel.SelectedColorProfile?.Key);
        Assert.False(viewModel.IsHdrEnabled);
        Assert.Equal(0, changedCount);
        Assert.True(applied);
    }

    [Fact]
    public async Task UserMutation_CapturesSourceAndUnknownSourcePreventsWrites()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            BlockFirstRefreshChange = true,
        };
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        PowerSource? changedSource = null;
        viewModel.UserSettingsChanged += (_, args) => changedSource = args.PowerSource;

        Task change = viewModel.SelectRefreshRateAsync(Fixed240);
        await adapter.FirstRefreshChangeStarted.Task;
        viewModel.ActivePowerSource = PowerSource.Battery;
        adapter.ReleaseFirstRefreshChange.TrySetResult();
        await change;

        Assert.Equal(PowerSource.PluggedIn, changedSource);
        viewModel.ActivePowerSource = PowerSource.ExternalUnknown;
        Assert.False(viewModel.CanChangeRefreshRate);
        await viewModel.SelectRefreshRateAsync(Dynamic);
        Assert.Equal([Fixed240.Key], adapter.RefreshRequests);
    }

    [Fact]
    public async Task ApplyProfile_ReturnsFalseWhenAdapterDoesNotConfirmMutation()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            FailNextRefreshChange = true,
        };
        await using var viewModel = CreateViewModel(adapter);

        bool applied = await viewModel.ApplyProfileAsync(
            new DisplayProfileSettings(Fixed240.Key, false, Native.Key));

        Assert.False(applied);
        Assert.Equal(Fixed60.Key, viewModel.SelectedRefreshRate?.Key);
    }

    [Fact]
    public async Task ApplyProfile_HdrTargetIgnoresDormantColorProfileKey()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);

        bool applied = await viewModel.ApplyProfileAsync(
            new DisplayProfileSettings(Fixed240.Key, true, Native.Key));

        Assert.True(applied);
        Assert.True(viewModel.IsHdrEnabled);
        Assert.Empty(adapter.ColorRequests);
    }

    private static DisplaySettingsViewModel CreateViewModel(
        IDisplaySettingsAdapter adapter) => new(adapter)
        {
            ActivePowerSource = PowerSource.PluggedIn,
        };

    private static DisplaySettingsSnapshot Snapshot(
        string refreshKey,
        bool hdr,
        string colorKey) => new(
            IsAvailable: true,
            RefreshRateOptions: [Fixed60, Fixed240, Dynamic],
            SelectedRefreshRateKey: refreshKey,
            IsHdrSupported: true,
            IsHdrEnabled: hdr,
            ColorProfileOptions: [Native, Srgb],
            SelectedColorProfileKey: colorKey,
            StatusMessage: "Built-in display detected.");

    private sealed class FakeDisplaySettingsAdapter(DisplaySettingsSnapshot initialState)
        : IDisplaySettingsAdapter
    {
        private DisplaySettingsSnapshot state = initialState;
        private int activeOperations;
        private int refreshChanges;

        public bool BlockFirstRefreshChange { get; init; }
        public bool FailNextRefreshChange { get; init; }
        public TaskCompletionSource FirstRefreshChangeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRefreshChange { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RefreshRequests { get; } = [];
        public List<bool> HdrRequests { get; } = [];
        public List<string> ColorRequests { get; } = [];
        public int MaximumConcurrentOperations { get; private set; }

        public void SetExternalState(DisplaySettingsSnapshot value) => state = value;

        public async Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token)
        {
            await EnterAsync(token);
            Exit();
            return state;
        }

        public async Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
            string refreshRateKey,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                RefreshRequests.Add(refreshRateKey);
                if (FailNextRefreshChange)
                {
                    return state with
                    {
                        StatusMessage = "Refresh change failed.",
                        OperationSucceeded = false,
                    };
                }

                if (BlockFirstRefreshChange && Interlocked.Increment(ref refreshChanges) == 1)
                {
                    FirstRefreshChangeStarted.TrySetResult();
                    await ReleaseFirstRefreshChange.Task.WaitAsync(token);
                }

                state = state with { SelectedRefreshRateKey = refreshRateKey };
                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
            bool enabled,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                HdrRequests.Add(enabled);
                state = state with { IsHdrEnabled = enabled };
                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplaySettingsSnapshot> SetColorProfileAsync(
            string colorProfileKey,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                ColorRequests.Add(colorProfileKey);
                state = state with { SelectedColorProfileKey = colorProfileKey };
                return state;
            }
            finally
            {
                Exit();
            }
        }

        private Task EnterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int active = Interlocked.Increment(ref activeOperations);
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, active);
            return Task.CompletedTask;
        }

        private void Exit() => Interlocked.Decrement(ref activeOperations);
    }
}
