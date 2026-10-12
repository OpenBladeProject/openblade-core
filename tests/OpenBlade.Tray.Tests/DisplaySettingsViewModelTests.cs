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
        Assert.Equal(
            "ATNA60HU06-0 · 2560 × 1600 · Dynamic (48-240 Hz) · 10-bit · HDR off · sRGB",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refresh_WithProfilesButNoActiveKey_PreservesNoSelectionAndAllowsChoice()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: false, colorKey: null));
        await using var viewModel = CreateViewModel(adapter);

        await viewModel.RefreshAsync();

        Assert.Equal([Native, Srgb], viewModel.ColorProfileOptions);
        Assert.Null(viewModel.SelectedColorProfile);
        Assert.True(viewModel.CanChangeColorProfile);
        Assert.Empty(adapter.ColorRequests);
    }

    [Fact]
    public async Task ProfileRelationship_TracksSavedCurrentAndPendingDisplayState()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        viewModel.ProfilePersistenceAvailable = true;
        viewModel.ActiveProfileSettings = new DisplayProfileSettings(
            Fixed60.Key,
            false,
            Native.Key);

        await viewModel.RefreshAsync();

        Assert.Equal("Matches saved profile", viewModel.ProfileRelationshipStatus);
        Assert.False(viewModel.CanSaveCurrentSettingsToProfile);
        Assert.False(viewModel.HasProfileDifference);

        adapter.SetExternalState(Snapshot(
            refreshKey: Fixed240.Key,
            hdr: false,
            colorKey: Native.Key));
        await viewModel.RefreshAsync();

        Assert.Equal("Different from saved profile", viewModel.ProfileRelationshipStatus);
        Assert.True(viewModel.CanSaveCurrentSettingsToProfile);
        Assert.True(viewModel.HasProfileDifference);

        viewModel.HasPendingProfileChange = true;

        Assert.Equal(
            "Temporary change not saved to this profile",
            viewModel.ProfileRelationshipStatus);
        Assert.True(viewModel.CanSaveCurrentSettingsToProfile);
    }

    [Fact]
    public async Task PartiallyUnsetProfile_CanSaveKnownCurrentField()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        viewModel.ProfilePersistenceAvailable = true;
        viewModel.ActiveProfileSettings = new DisplayProfileSettings(
            Fixed60.Key,
            false,
            null);

        await viewModel.RefreshAsync();

        Assert.Equal(
            "Current settings are not saved to this profile",
            viewModel.ProfileRelationshipStatus);
        Assert.True(viewModel.CanSaveCurrentSettingsToProfile);
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
        Assert.False(viewModel.CanCaptureCurrentSettings);
        adapter.ReleaseFirstRefreshChange.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([Fixed240.Key, Dynamic.Key], adapter.RefreshRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.Equal(Dynamic.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanChangeRefreshRate);
        Assert.True(viewModel.CanChangeHdr);
        Assert.True(viewModel.CanChangeColorProfile);
        Assert.True(viewModel.CanCaptureCurrentSettings);
    }

    [Fact]
    public async Task RefreshRateChange_WaitsForBackgroundRefreshAndAppliesAfterward()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.BlockNextStateRead = true;

        Task refresh = viewModel.RefreshAsync();
        await adapter.StateReadStarted.Task;
        Task change = viewModel.SelectRefreshRateAsync(Fixed240);

        Assert.Empty(adapter.RefreshRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeRefreshRate);
        Assert.False(viewModel.CanChangeHdr);
        Assert.False(viewModel.CanChangeColorProfile);
        adapter.ReleaseStateRead.TrySetResult();
        await Task.WhenAll(refresh, change);

        Assert.Equal([Fixed240.Key], adapter.RefreshRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.Equal(Fixed240.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ColorProfileChange_WaitsForBackgroundRefreshAndAppliesAfterward()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.BlockNextStateRead = true;

        Task refresh = viewModel.RefreshAsync();
        await adapter.StateReadStarted.Task;
        Task change = viewModel.SelectColorProfileAsync(Srgb);

        Assert.Empty(adapter.ColorRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeRefreshRate);
        Assert.False(viewModel.CanChangeHdr);
        Assert.False(viewModel.CanChangeColorProfile);
        adapter.ReleaseStateRead.TrySetResult();
        await Task.WhenAll(refresh, change);

        Assert.Equal([Srgb.Key], adapter.ColorRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.Equal(Srgb.Key, viewModel.SelectedColorProfile?.Key);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task HdrChange_WaitsForBackgroundRefreshAndAppliesAfterward()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.BlockNextStateRead = true;

        Task refresh = viewModel.RefreshAsync();
        await adapter.StateReadStarted.Task;
        Task change = viewModel.SetHdrEnabledAsync(enabled: true);

        Assert.Empty(adapter.HdrRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeRefreshRate);
        Assert.False(viewModel.CanChangeHdr);
        Assert.False(viewModel.CanChangeColorProfile);
        adapter.ReleaseStateRead.TrySetResult();
        await Task.WhenAll(refresh, change);

        Assert.Equal([true], adapter.HdrRequests);
        Assert.Equal(1, adapter.MaximumConcurrentOperations);
        Assert.True(viewModel.IsHdrEnabled);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task QueuedWrites_KeepBusyUntilLastWriteCompletes()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            BlockFirstRefreshChange = true,
            BlockSecondRefreshChange = true,
        };
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();

        Task first = viewModel.SelectRefreshRateAsync(Fixed240);
        await adapter.FirstRefreshChangeStarted.Task;
        Task second = viewModel.SelectRefreshRateAsync(Dynamic);

        adapter.ReleaseFirstRefreshChange.TrySetResult();
        await adapter.SecondRefreshChangeStarted.Task;
        await first;

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanChangeRefreshRate);
        adapter.ReleaseSecondRefreshChange.TrySetResult();
        await second;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanChangeRefreshRate);
        Assert.Equal([Fixed240.Key, Dynamic.Key], adapter.RefreshRequests);
    }

    [Fact]
    public async Task CanceledQueuedWrite_ClearsBusyWithoutEnteringAdapter()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.BlockNextStateRead = true;
        using var cancellation = new CancellationTokenSource();

        Task refresh = viewModel.RefreshAsync();
        await adapter.StateReadStarted.Task;
        Task change = viewModel.SetHdrEnabledAsync(enabled: true, cancellation.Token);
        Assert.True(viewModel.IsBusy);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);

        Assert.Empty(adapter.HdrRequests);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanChangeHdr);
        adapter.ReleaseStateRead.TrySetResult();
        await refresh;
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
        Assert.DoesNotContain(nameof(DisplaySettingsViewModel.CanCaptureCurrentSettings), notifications);
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
    public async Task FailedUnavailableRefresh_PreservesLastKnownDisplayState()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: true, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        IReadOnlyList<DisplayRefreshRateOption> refreshOptions =
            viewModel.RefreshRateOptions;
        IReadOnlyList<DisplayColorProfileOption> colorOptions =
            viewModel.ColorProfileOptions;
        adapter.SetExternalState(new DisplaySettingsSnapshot(
            IsAvailable: false,
            RefreshRateOptions: [],
            SelectedRefreshRateKey: null,
            IsHdrSupported: false,
            IsHdrEnabled: false,
            ColorProfileOptions: [],
            SelectedColorProfileKey: null,
            StatusMessage: "The display session agent is not responding.",
            OperationSucceeded: false,
            FailureKind: DisplaySettingsFailureKind.SessionAgentUnavailable));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsAvailable);
        Assert.True(viewModel.IsHdrSupported);
        Assert.True(viewModel.IsHdrEnabled);
        Assert.Equal(Fixed240.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.Equal(Native.Key, viewModel.SelectedColorProfile?.Key);
        Assert.Same(refreshOptions, viewModel.RefreshRateOptions);
        Assert.Same(colorOptions, viewModel.ColorProfileOptions);
        Assert.Equal(
            "The display session agent is not responding.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedFirstRefresh_ReportsReadFailureInsteadOfWriteFailure()
    {
        var adapter = new FakeDisplaySettingsAdapter(new DisplaySettingsSnapshot(
            IsAvailable: false,
            RefreshRateOptions: [],
            SelectedRefreshRateKey: null,
            IsHdrSupported: false,
            IsHdrEnabled: false,
            ColorProfileOptions: [],
            SelectedColorProfileKey: null,
            StatusMessage: "The display session agent is not responding.",
            OperationSucceeded: false,
            FailureKind: DisplaySettingsFailureKind.SessionAgentUnavailable));
        await using var viewModel = CreateViewModel(adapter);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsAvailable);
        Assert.Equal(
            "The display session agent is not responding.",
            viewModel.StatusMessage);
        Assert.NotEqual("Display setting failed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task UnexpectedWriteFailure_RecordsEverySanitizedOperationDiagnostic()
    {
        const string sensitiveDetail = @"Sensitive path C:\Users\Example\secret.txt";
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedRefreshChangeFailure =
                new InvalidOperationException(sensitiveDetail),
        };
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            TimeProvider.System,
            diagnostics.Add)
        {
            ActivePowerSource = PowerSource.PluggedIn,
        };

        await viewModel.SelectRefreshRateAsync(Dynamic);

        string diagnostic = Assert.Single(diagnostics);
        Assert.Contains("refresh-rate change", diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, diagnostic, StringComparison.Ordinal);
        Assert.Equal("Display setting failed.", viewModel.StatusMessage);

        await viewModel.SelectRefreshRateAsync(Dynamic);

        Assert.Equal([diagnostic, diagnostic], diagnostics);
    }

    [Fact]
    public void DisposeDuringWriteNotification_DoesNotReportAPreEntryFailure()
    {
        PumpingSynchronizationContext.Run(async _ =>
        {
            var adapter = new FakeDisplaySettingsAdapter(
                Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
            var diagnostics = new List<string>();
            await using var viewModel = new DisplaySettingsViewModel(
                adapter,
                TimeProvider.System,
                diagnostics.Add)
            {
                ActivePowerSource = PowerSource.PluggedIn,
            };
            bool hasStartedDisposal = false;
            viewModel.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(DisplaySettingsViewModel.IsBusy)
                    && !hasStartedDisposal)
                {
                    hasStartedDisposal = true;
                    PumpingSynchronizationContext.Run(context => viewModel.DisposeAsync().AsTask());
                }
            };

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                viewModel.SelectRefreshRateAsync(Dynamic));

            Assert.True(hasStartedDisposal);
            Assert.Empty(adapter.RefreshRequests);
            Assert.Empty(diagnostics);
        });
    }

    [Fact]
    public async Task RecurringUnexpectedRefreshFailure_IsThrottledUntilIntervalExpires()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("Read failed."),
        };
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            time,
            diagnostics.Add);

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        Assert.Single(diagnostics);
        Assert.Contains("display refresh", diagnostics[0], StringComparison.Ordinal);

        time.AdvanceTimestamp(DisplaySettingsViewModel.RefreshFailureDiagnosticInterval);
        await viewModel.RefreshAsync();

        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public async Task UnexpectedRefreshFailure_RecordsDistinctExceptionTypesAndHResults()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("Sensitive first failure.", 1),
        };
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            new MutableTimeProvider(DateTimeOffset.UnixEpoch),
            diagnostics.Add);

        await viewModel.RefreshAsync();
        adapter.UnexpectedStateReadFailure = new IOException("Sensitive second failure.", 2);
        await viewModel.RefreshAsync();
        adapter.UnexpectedStateReadFailure = new System.Runtime.InteropServices.ExternalException(
            "Sensitive third failure.",
            2);
        await viewModel.RefreshAsync();

        Assert.Equal(3, diagnostics.Count);
        Assert.Contains("IOException, HRESULT 0x00000001", diagnostics[0], StringComparison.Ordinal);
        Assert.Contains("IOException, HRESULT 0x00000002", diagnostics[1], StringComparison.Ordinal);
        Assert.Contains("ExternalException, HRESULT 0x00000002", diagnostics[2], StringComparison.Ordinal);
        Assert.All(diagnostics, diagnostic =>
            Assert.DoesNotContain("Sensitive", diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedRefreshFailure_SuppressesDuplicatesAndCapsTheFixedWindow()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(adapter, time, diagnostics.Add);

        for (int hResult = 1; hResult <= 8; hResult++)
        {
            adapter.UnexpectedStateReadFailure = new IOException("Read failed.", hResult);
            await viewModel.RefreshAsync();
            adapter.UnexpectedStateReadFailure = new IOException("Same signature, different message.", hResult);
            await viewModel.RefreshAsync();
            time.AdvanceTimestamp(TimeSpan.FromSeconds(30));
        }

        adapter.UnexpectedStateReadFailure = new IOException("Window is full.", 9);
        await viewModel.RefreshAsync();
        Assert.Equal(8, diagnostics.Count);

        time.AdvanceTimestamp(
            DisplaySettingsViewModel.RefreshFailureDiagnosticInterval - TimeSpan.FromMinutes(4));
        adapter.UnexpectedStateReadFailure = new IOException("First signature in the next window.", 1);
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();
        Assert.Equal(9, diagnostics.Count);

        adapter.UnexpectedStateReadFailure = new IOException("Previously suppressed signature.", 9);
        await viewModel.RefreshAsync();
        Assert.Equal(10, diagnostics.Count);
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public async Task UnexpectedRefreshFailure_ThrottleIgnoresWallClockChanges(
        int wallClockHours)
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("Read failed."),
        };
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            time,
            diagnostics.Add);

        await viewModel.RefreshAsync();
        time.AdjustUtcNow(TimeSpan.FromHours(wallClockHours));
        await viewModel.RefreshAsync();

        Assert.Single(diagnostics);

        time.AdvanceTimestamp(DisplaySettingsViewModel.RefreshFailureDiagnosticInterval);
        await viewModel.RefreshAsync();

        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public async Task DiagnosticSinkFailure_DoesNotReplaceTheDisplayErrorState()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("Read failed."),
        };
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            TimeProvider.System,
            _ => throw new InvalidOperationException("Diagnostic sink failed."));

        await viewModel.RefreshAsync();

        Assert.Equal("Display setting failed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task DiagnosticSinkFailure_RetriesUntilTheRefreshDiagnosticIsRecorded()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("Read failed."),
        };
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
        int attempts = 0;
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            time,
            message =>
            {
                if (++attempts == 1)
                {
                    throw new IOException("Diagnostic sink failed.");
                }

                diagnostics.Add(message);
            });

        await viewModel.RefreshAsync();
        time.AdvanceTimestamp(TimeSpan.FromMinutes(1));
        await viewModel.RefreshAsync();
        time.AdvanceTimestamp(
            DisplaySettingsViewModel.RefreshFailureDiagnosticInterval - TimeSpan.FromMinutes(1));
        await viewModel.RefreshAsync();

        Assert.Equal(2, attempts);
        Assert.Single(diagnostics);
        Assert.Equal("Display setting failed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task DiagnosticSinkFailure_DistinctSignatureAndExpiredWindowRemainRetryable()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("First failure.", 1),
        };
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        int attempts = 0;
        var diagnostics = new List<string>();
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            time,
            message =>
            {
                if (++attempts is 2 or 4)
                {
                    throw new IOException("Diagnostic sink failed.");
                }

                diagnostics.Add(message);
            });

        await viewModel.RefreshAsync();
        adapter.UnexpectedStateReadFailure = new IOException("Distinct failure.", 2);
        await viewModel.RefreshAsync();
        adapter.UnexpectedStateReadFailure = new IOException("Already recorded.", 1);
        await viewModel.RefreshAsync();
        adapter.UnexpectedStateReadFailure = new IOException("Retry the distinct failure.", 2);
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        Assert.Equal(3, attempts);
        Assert.Equal(2, diagnostics.Count);

        time.AdvanceTimestamp(DisplaySettingsViewModel.RefreshFailureDiagnosticInterval);
        adapter.UnexpectedStateReadFailure = new IOException("The next window's failure.", 3);
        await viewModel.RefreshAsync();
        time.AdvanceTimestamp(TimeSpan.FromMinutes(1));
        await viewModel.RefreshAsync();
        time.AdvanceTimestamp(
            DisplaySettingsViewModel.RefreshFailureDiagnosticInterval - TimeSpan.FromMinutes(1));
        await viewModel.RefreshAsync();

        Assert.Equal(5, attempts);
        Assert.Equal(3, diagnostics.Count);

        time.AdvanceTimestamp(TimeSpan.FromMinutes(1));
        await viewModel.RefreshAsync();

        Assert.Equal(6, attempts);
        Assert.Equal(4, diagnostics.Count);
    }

    [Fact]
    public async Task SuccessfulRefresh_DoesNotResetTheUnexpectedFailureWindow()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key))
        {
            UnexpectedStateReadFailure = new IOException("First failure."),
        };
        var diagnostics = new List<string>();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var viewModel = new DisplaySettingsViewModel(
            adapter,
            time,
            diagnostics.Add);

        await viewModel.RefreshAsync();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            adapter.UnexpectedStateReadFailure = null;
            await viewModel.RefreshAsync();
            adapter.UnexpectedStateReadFailure = new IOException("Another failure of the same type.");
            await viewModel.RefreshAsync();
        }

        Assert.Single(diagnostics);

        time.AdvanceTimestamp(DisplaySettingsViewModel.RefreshFailureDiagnosticInterval);
        await viewModel.RefreshAsync();

        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public async Task FailedAvailableRefresh_AppliesReadbackWithoutReportingExternalChange()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed60.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.SetExternalState(Snapshot(
            refreshKey: Dynamic.Key,
            hdr: true,
            colorKey: Srgb.Key) with
        {
            StatusMessage = "Refresh failed after fallback query.",
            OperationSucceeded = false,
        });

        await viewModel.RefreshAsync();

        Assert.Equal(Dynamic.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.True(viewModel.IsHdrEnabled);
        Assert.Equal(
            "Display settings could not be refreshed.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulUnavailableRefresh_ClearsLastKnownDisplayState()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: true, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.SetExternalState(new DisplaySettingsSnapshot(
            IsAvailable: false,
            RefreshRateOptions: [],
            SelectedRefreshRateKey: null,
            IsHdrSupported: false,
            IsHdrEnabled: false,
            ColorProfileOptions: [],
            SelectedColorProfileKey: null,
            StatusMessage: "No built-in display is available."));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsAvailable);
        Assert.False(viewModel.IsHdrSupported);
        Assert.False(viewModel.IsHdrEnabled);
        Assert.Null(viewModel.SelectedRefreshRate);
        Assert.Null(viewModel.SelectedColorProfile);
        Assert.Empty(viewModel.RefreshRateOptions);
        Assert.Empty(viewModel.ColorProfileOptions);
        Assert.Equal("Display settings are unavailable.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedRefresh_UsesLocalizedSummaryWithoutPresentingRawDiagnostic()
    {
        using var culture = new CultureScope("qps-ploc");
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: true, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.SetExternalState(new DisplaySettingsSnapshot(
            IsAvailable: false,
            RefreshRateOptions: [],
            SelectedRefreshRateKey: null,
            IsHdrSupported: false,
            IsHdrEnabled: false,
            ColorProfileOptions: [],
            SelectedColorProfileKey: null,
            StatusMessage: "Invariant backend diagnostic",
            OperationSucceeded: false));

        await viewModel.RefreshAsync();

        Assert.Equal(
            TrayText.Get("Display settings could not be refreshed."),
            viewModel.StatusMessage);
        Assert.DoesNotContain(
            "Invariant backend diagnostic",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
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
            PowerSource.PluggedIn,
            new DisplayProfileSettings(Dynamic.Key, false, Srgb.Key));

        Assert.Equal(
            [(PowerSource.PluggedIn, new DisplayProfileSettings(Dynamic.Key, false, Srgb.Key))],
            adapter.ProfileRequests);
        Assert.Empty(adapter.RefreshRequests);
        Assert.Empty(adapter.HdrRequests);
        Assert.Empty(adapter.ColorRequests);
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
        Assert.Equal([PowerSource.PluggedIn], adapter.RefreshRequestSources);
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
            PowerSource.PluggedIn,
            new DisplayProfileSettings(Fixed240.Key, false, Native.Key));

        Assert.False(applied);
        Assert.Equal(Fixed60.Key, viewModel.SelectedRefreshRate?.Key);
        Assert.Equal("Display setting failed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ApplyProfile_HdrTargetIgnoresDormantColorProfileKey()
    {
        var adapter = new FakeDisplaySettingsAdapter(
            Snapshot(refreshKey: Fixed240.Key, hdr: false, colorKey: Native.Key));
        await using var viewModel = CreateViewModel(adapter);

        bool applied = await viewModel.ApplyProfileAsync(
            PowerSource.PluggedIn,
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
        string? colorKey) => new(
            IsAvailable: true,
            RefreshRateOptions: [Fixed60, Fixed240, Dynamic],
            SelectedRefreshRateKey: refreshKey,
            IsHdrSupported: true,
            IsHdrEnabled: hdr,
            ColorProfileOptions: [Native, Srgb],
            SelectedColorProfileKey: colorKey,
            StatusMessage: null,
            DisplayName: "ATNA60HU06-0",
            Width: 2560,
            Height: 1600,
            BitsPerColorChannel: 10);

    private sealed class FakeDisplaySettingsAdapter(DisplaySettingsSnapshot initialState)
        : IDisplaySettingsAdapter
    {
        private DisplaySettingsSnapshot state = initialState;
        private int activeOperations;
        private int refreshChanges;

        public bool BlockFirstRefreshChange { get; init; }
        public bool BlockSecondRefreshChange { get; init; }
        public bool FailNextRefreshChange { get; init; }
        public bool BlockNextStateRead { get; set; }
        public Exception? UnexpectedStateReadFailure { get; set; }
        public Exception? UnexpectedRefreshChangeFailure { get; init; }
        public TaskCompletionSource FirstRefreshChangeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRefreshChange { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRefreshChangeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondRefreshChange { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StateReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStateRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RefreshRequests { get; } = [];
        public List<PowerSource> RefreshRequestSources { get; } = [];
        public List<bool> HdrRequests { get; } = [];
        public List<PowerSource> HdrRequestSources { get; } = [];
        public List<string> ColorRequests { get; } = [];
        public List<PowerSource> ColorRequestSources { get; } = [];
        public List<(PowerSource Source, DisplayProfileSettings Settings)> ProfileRequests { get; } = [];
        public int MaximumConcurrentOperations { get; private set; }

        public void SetExternalState(DisplaySettingsSnapshot value) => state = value;

        public async Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                if (UnexpectedStateReadFailure is not null)
                {
                    throw UnexpectedStateReadFailure;
                }

                if (BlockNextStateRead)
                {
                    BlockNextStateRead = false;
                    StateReadStarted.TrySetResult();
                    await ReleaseStateRead.Task.WaitAsync(token);
                }

                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                RefreshRequests.Add(refreshRateKey);
                RefreshRequestSources.Add(expectedPowerSource);
                if (UnexpectedRefreshChangeFailure is not null)
                {
                    throw UnexpectedRefreshChangeFailure;
                }

                int refreshChangeNumber = Interlocked.Increment(ref refreshChanges);
                if (FailNextRefreshChange)
                {
                    return state with
                    {
                        StatusMessage = "Refresh change failed.",
                        OperationSucceeded = false,
                    };
                }

                if (BlockFirstRefreshChange && refreshChangeNumber == 1)
                {
                    FirstRefreshChangeStarted.TrySetResult();
                    await ReleaseFirstRefreshChange.Task.WaitAsync(token);
                }
                else if (BlockSecondRefreshChange && refreshChangeNumber == 2)
                {
                    SecondRefreshChangeStarted.TrySetResult();
                    await ReleaseSecondRefreshChange.Task.WaitAsync(token);
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
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                HdrRequests.Add(enabled);
                HdrRequestSources.Add(expectedPowerSource);
                state = state with { IsHdrEnabled = enabled };
                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplaySettingsSnapshot> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                ColorRequests.Add(colorProfileKey);
                ColorRequestSources.Add(expectedPowerSource);
                state = state with { SelectedColorProfileKey = colorProfileKey };
                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplaySettingsSnapshot> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken token)
        {
            await EnterAsync(token);
            try
            {
                ProfileRequests.Add((expectedPowerSource, settings));
                if (FailNextRefreshChange)
                {
                    return state with
                    {
                        StatusMessage = "Profile change failed.",
                        OperationSucceeded = false,
                    };
                }

                state = state with
                {
                    SelectedRefreshRateKey = settings.RefreshRateKey
                        ?? state.SelectedRefreshRateKey,
                    IsHdrEnabled = settings.HdrEnabled ?? state.IsHdrEnabled,
                };
                if (settings.ColorProfileKey is not null && !state.IsHdrEnabled)
                {
                    state = state with
                    {
                        SelectedColorProfileKey = settings.ColorProfileKey,
                    };
                }

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() => now;

        internal void AdvanceTimestamp(TimeSpan elapsed) => timestamp += elapsed.Ticks;

        internal void AdjustUtcNow(TimeSpan offset) => now += offset;
    }
}
