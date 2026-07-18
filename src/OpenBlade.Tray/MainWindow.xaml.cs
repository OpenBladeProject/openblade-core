using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private const double InitialWidthDip = 840;
    private const double InitialHeightDip = 720;
    private const double DashboardStackThresholdDip = 600;
    private const double ProfilesStackThresholdDip = 720;
    private static readonly TimeSpan ChargeLimitCommitDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherQueue dispatcherQueue;
    private readonly MicaBackdropHost micaBackdropHost;
    private readonly UiPreferenceStore preferenceStore;
    private readonly UiPreferenceSaveQueue preferenceSaveQueue;
    private readonly VisibleObservationLoop visibleObservationLoop;
    private readonly DispatcherQueueTimer chargeLimitCommitTimer;
    private readonly object initializationSync = new();
    private Task? initializationTask;
    private bool initialPlacementAttempted;
    private bool changingPerformanceSelection;
    private bool changingFanSelection;
    private bool changingBatteryProtection;
    private bool chargeLimitCommitInProgress;
    private bool chargeLimitPointerInteraction;
    private int? pendingChargeLimitPercent;
    private bool? dashboardIsStacked;
    private bool? profilesAreStacked;
    private int disposed;

    public MainWindow()
    {
        dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray must start on a dispatcher thread.");
        dispatcherQueue.EnsureSystemDispatcherQueue();
        ViewModel = new MainViewModel(
            new ServiceClient(),
            new WinUiDispatcher(dispatcherQueue),
            new DiagnosticArchiveSaver(new WinUiDiagnosticArchiveDestination(AppWindow.Id)),
            new WindowsStartupRegistration());
        visibleObservationLoop = new VisibleObservationLoop(ViewModel.ObserveStateAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade visible observation failed: {0}",
                exception),
            observationIntervalProvider: () =>
                ViewModel.LiveReadingsObservationInterval);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        chargeLimitCommitTimer = dispatcherQueue.CreateTimer();
        chargeLimitCommitTimer.Interval = ChargeLimitCommitDelay;
        chargeLimitCommitTimer.IsRepeating = false;
        chargeLimitCommitTimer.Tick += OnChargeLimitCommitTimerTick;
        micaBackdropHost = new MicaBackdropHost(this, Root);
        Root.DataContext = ViewModel;
        Root.SizeChanged += OnRootSizeChanged;

        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        preferenceStore = new UiPreferenceStore(Path.Combine(
            localApplicationData,
            "OpenBlade",
            "preferences.json"));
        preferenceSaveQueue = new UiPreferenceSaveQueue(
            preferenceStore.SaveAsync,
            exception => Trace.TraceError(
                "OpenBlade preference save failed: {0}",
                exception));
        AppWindow.Closing += OnClosing;
    }

    public MainViewModel ViewModel { get; }

    public PerformancePreset SilentPreset { get; } = PerformancePreset.Silent;

    public PerformancePreset BalancedPreset { get; } = PerformancePreset.Balanced;

    public PerformancePreset PerformancePreset { get; } =
        OpenBlade.Contracts.PerformancePreset.Performance;

    public Task InitializeAsync()
    {
        lock (initializationSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return initializationTask ??= InitializeCoreAsync();
        }
    }

    public void ShowAndActivate()
    {
        visibleObservationLoop.StartOrWake();
        AppWindow.Show(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Task? activeInitialization;
        lock (initializationSync)
        {
            activeInitialization = initializationTask;
        }

        if (activeInitialization is not null)
        {
            await activeInitialization;
        }

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        AppWindow.Closing -= OnClosing;
        Root.SizeChanged -= OnRootSizeChanged;
        chargeLimitCommitTimer.Stop();
        chargeLimitCommitTimer.Tick -= OnChargeLimitCommitTimerTick;
        micaBackdropHost.Dispose();
        await visibleObservationLoop.DisposeAsync();
        await preferenceSaveQueue.DisposeAsync();
        await ViewModel.DisposeAsync();
    }

    private async Task InitializeCoreAsync()
    {
        ThemePreference initialTheme = ThemePreference.FollowWindows;
        bool keepManualChangesUntilCleared = false;
        LiveReadingsCadence liveReadingsRefreshCadence =
            LiveReadingsCadence.EveryTwoSeconds;
        TrayHoverLabelMode trayHoverLabelMode =
            TrayHoverLabelMode.ProfileAndPowerFlow;
        try
        {
            UiPreferences preferences = await preferenceStore.LoadAsync();
            ThemeSelectionPolicy.Resolve(preferences.Theme);
            initialTheme = preferences.Theme;
            keepManualChangesUntilCleared = preferences.KeepManualChangesUntilCleared;
            liveReadingsRefreshCadence = preferences.EffectiveLiveReadingsCadence;
            trayHoverLabelMode = preferences.TrayHoverLabelMode;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade preference initialization failed; using Follow Windows: {0}",
                exception);
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        ViewModel.Theme = initialTheme;
        ViewModel.KeepManualChangesUntilCleared = keepManualChangesUntilCleared;
        ViewModel.LiveReadingsRefreshCadence = liveReadingsRefreshCadence;
        ViewModel.TrayHoverLabelMode = trayHoverLabelMode;
        try
        {
            ApplyTheme(ViewModel.Theme);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade initial theme application failed; using Follow Windows: {0}",
                exception);
            ViewModel.Theme = ThemePreference.FollowWindows;
            try
            {
                ApplyTheme(ViewModel.Theme);
            }
            catch (Exception fallbackException)
            {
                Trace.TraceError(
                    "OpenBlade fallback theme application failed: {0}",
                    fallbackException);
            }
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PlaceInitialWindow();
        try
        {
            await ViewModel.StartAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade status watch initialization failed: {0}",
                exception);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (args.PropertyName == nameof(MainViewModel.Theme))
        {
            ThemePreference selectedTheme = ViewModel.Theme;
            if (dispatcherQueue.HasThreadAccess)
            {
                ApplyThemeAndQueueSave(selectedTheme);
                return;
            }

            dispatcherQueue.TryEnqueue(() => ApplyThemeAndQueueSave(selectedTheme));
            return;
        }

        if (args.PropertyName is nameof(MainViewModel.KeepManualChangesUntilCleared)
            or nameof(MainViewModel.LiveReadingsRefreshCadence)
            or nameof(MainViewModel.TrayHoverLabelMode))
        {
            QueuePreferenceSave();
            if (args.PropertyName == nameof(MainViewModel.LiveReadingsRefreshCadence))
            {
                visibleObservationLoop.StartOrWake();
            }
        }
    }

    private void ApplyThemeAndQueueSave(ThemePreference selectedTheme)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            ApplyTheme(selectedTheme);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade runtime theme application failed: {0}", exception);
            return;
        }

        QueuePreferenceSave();
    }

    private void ApplyTheme(ThemePreference selectedTheme)
    {
        ThemeManager.Apply(Root, selectedTheme);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindow.TitleBar.PreferredTheme =
            ThemeSelectionPolicy.Resolve(selectedTheme) switch
            {
                ResolvedTheme.Default => TitleBarTheme.UseDefaultAppMode,
                ResolvedTheme.Light => TitleBarTheme.Light,
                ResolvedTheme.Dark => TitleBarTheme.Dark,
                _ => throw new ArgumentOutOfRangeException(nameof(selectedTheme)),
            };
    }

    private void QueuePreferenceSave()
    {
        try
        {
            preferenceSaveQueue.Enqueue(new UiPreferences(
                ViewModel.Theme,
                ViewModel.KeepManualChangesUntilCleared,
                ViewModel.LiveReadingsEnabled,
                ViewModel.TrayHoverLabelMode,
                ViewModel.LiveReadingsRefreshCadence));
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race; no preference work remains valid.
        }
    }

    private void PlaceInitialWindow()
    {
        if (initialPlacementAttempted)
        {
            return;
        }

        initialPlacementAttempted = true;
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            uint nativeDpi = GetDpiForWindow(hwnd);
            if (nativeDpi == 0)
            {
                throw new InvalidOperationException("Windows returned zero DPI for the tray window.");
            }

            int dpi = checked((int)nativeDpi);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                AppWindow.Id,
                DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea.WorkArea;
            PixelRect placement = WindowPlacementPolicy.Center(
                InitialWidthDip,
                InitialHeightDip,
                dpi,
                new PixelRect(
                    workArea.X,
                    workArea.Y,
                    workArea.Width,
                    workArea.Height));
            var rect = new RectInt32(
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height);
            AppWindow.MoveAndResize(rect, displayArea);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade initial window placement failed; using the Windows default: {0}",
                exception);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        bool exitRequested = Microsoft.UI.Xaml.Application.Current is App app && app.ExitRequested;
        if (WindowLifecyclePolicy.Decide(exitRequested) == WindowCloseDecision.Hide)
        {
            args.Cancel = true;
            visibleObservationLoop.Stop();
            AppWindow.Hide();
        }
    }

    private async void OnStartAtSignInToggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle
            || Volatile.Read(ref disposed) != 0
            || toggle.IsOn == ViewModel.StartAtSignIn)
        {
            return;
        }

        bool requested = toggle.IsOn;
        if (ViewModel.SetStartAtSignInCommand is not AsyncRelayCommand command
            || !command.CanExecute(requested))
        {
            toggle.IsOn = ViewModel.StartAtSignIn;
            return;
        }

        toggle.IsEnabled = false;
        try
        {
            await command.ExecuteAsync(requested);
        }
        finally
        {
            toggle.IsOn = ViewModel.StartAtSignIn;
            toggle.IsEnabled = true;
        }
    }

    private async void OnDashboardPerformanceChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingPerformanceSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not PerformancePresetOption selected
            || selected.Value == ViewModel.SelectedPerformance)
        {
            return;
        }

        changingPerformanceSelection = true;
        if (ViewModel.SetPerformanceCommand is not AsyncRelayCommand command
            || !command.CanExecute(selected.Value))
        {
            selector.SelectedItem = ViewModel.SelectedPerformanceOption;
            changingPerformanceSelection = false;
            return;
        }

        selector.IsEnabled = false;
        try
        {
            await command.ExecuteAsync(selected.Value);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade performance selection failed: {0}", exception);
        }
        finally
        {
            selector.SelectedItem = ViewModel.SelectedPerformanceOption;
            selector.IsEnabled = true;
            changingPerformanceSelection = false;
        }
    }

    private async void OnDashboardFanModeSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingFanSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not FanMode selected
            || selected == ViewModel.SelectedFanMode)
        {
            return;
        }

        changingFanSelection = true;
        if (ViewModel.SetFanModeCommand is not AsyncRelayCommand command
            || !command.CanExecute(selected))
        {
            selector.SelectedItem = ViewModel.SelectedFanMode;
            changingFanSelection = false;
            return;
        }

        selector.IsEnabled = false;
        try
        {
            await command.ExecuteAsync(selected);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade fan selection failed: {0}", exception);
        }
        finally
        {
            selector.SelectedItem = ViewModel.SelectedFanMode;
            selector.IsEnabled = true;
            changingFanSelection = false;
        }
    }

    private void OnDashboardChargeLimitPointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            chargeLimitPointerInteraction = true;
        }
    }

    private void OnDashboardChargeLimitPointerCaptureLost(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!chargeLimitPointerInteraction)
        {
            return;
        }

        chargeLimitPointerInteraction = false;
        if (sender is Slider slider)
        {
            ScheduleChargeLimitCommit(slider.Value);
        }
    }

    private void OnDashboardChargeLimitKeyUp(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (sender is Slider slider
            && args.Key is Windows.System.VirtualKey.Left
                or Windows.System.VirtualKey.Right
                or Windows.System.VirtualKey.Up
                or Windows.System.VirtualKey.Down
                or Windows.System.VirtualKey.PageUp
                or Windows.System.VirtualKey.PageDown
                or Windows.System.VirtualKey.Home
                or Windows.System.VirtualKey.End)
        {
            ScheduleChargeLimitCommit(slider.Value);
        }
    }

    private void ScheduleChargeLimitCommit(double sliderValue)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        int value;
        try
        {
            value = IntegerEditorValue.ToInt32(sliderValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        ViewModel.ChargeLimitPercent = value;
        chargeLimitCommitTimer.Stop();
        pendingChargeLimitPercent = value;
        chargeLimitCommitTimer.Start();
    }

    private async void OnChargeLimitCommitTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Volatile.Read(ref disposed) != 0 || pendingChargeLimitPercent is not int value)
        {
            return;
        }
        if (chargeLimitCommitInProgress)
        {
            sender.Start();
            return;
        }

        pendingChargeLimitPercent = null;
        if (ViewModel.SetChargeLimitCommand is not AsyncRelayCommand command
            || !command.CanExecute(value))
        {
            return;
        }

        chargeLimitCommitInProgress = true;
        try
        {
            await command.ExecuteAsync(value);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade charge-limit change failed: {0}", exception);
        }
        finally
        {
            chargeLimitCommitInProgress = false;
            if (pendingChargeLimitPercent is not null
                && Volatile.Read(ref disposed) == 0)
            {
                sender.Stop();
                sender.Start();
            }
        }
    }

    private async void OnBatteryProtectionToggled(object sender, RoutedEventArgs args)
    {
        if (changingBatteryProtection
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.BatteryProtectionEnabled)
        {
            return;
        }

        if (ViewModel.ToggleBatteryProtectionCommand is not AsyncRelayCommand command
            || !command.CanExecute(null))
        {
            SynchronizeBatteryProtectionToggle(toggle);
            return;
        }

        changingBatteryProtection = true;
        toggle.IsEnabled = false;
        try
        {
            await command.ExecuteAsync(null);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade battery-protection change failed: {0}", exception);
        }
        finally
        {
            SynchronizeBatteryProtectionToggle(toggle);
            toggle.IsEnabled = true;
            changingBatteryProtection = false;
        }
    }

    private void SynchronizeBatteryProtectionToggle(ToggleSwitch toggle)
    {
        bool previouslyChanging = changingBatteryProtection;
        changingBatteryProtection = true;
        toggle.IsOn = ViewModel.BatteryProtectionEnabled;
        changingBatteryProtection = previouslyChanging;
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        string destination = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string
            ?? "home";
        HomePage.Visibility = destination == "home"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfilesPage.Visibility = destination == "profiles"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings"
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (destination == "profiles")
        {
            dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => UpdateProfilesLayout());
        }
        else if (destination == "home")
        {
            dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => UpdateDashboardLayout());
        }
    }

    private void OnProfileStateBadgeClick(object sender, RoutedEventArgs args)
    {
        MainNavigation.SelectedItem = SettingsNavigationItem;
        TroubleshootingExpander.IsExpanded = true;
        dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                TroubleshootingExpander.Focus(FocusState.Programmatic);
                TroubleshootingExpander.StartBringIntoView(
                    new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0,
                    });
            });
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateDashboardLayout(args.NewSize.Width);
        UpdateProfilesLayout(args.NewSize.Width);
    }

    private void UpdateDashboardLayout(double fallbackWidth = double.NaN)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        double effectiveWidth = HomePage.ActualWidth > 0
            ? HomePage.ActualWidth
            : double.IsFinite(fallbackWidth)
                ? fallbackWidth
                : Root.ActualWidth;
        bool stack = effectiveWidth < DashboardStackThresholdDip;
        if (dashboardIsStacked == stack)
        {
            return;
        }

        dashboardIsStacked = stack;
        AttentionIndicatorLabel.Visibility = stack
            ? Visibility.Collapsed
            : Visibility.Visible;
        AttentionIndicator.Padding = stack
            ? new Thickness(7, 3, 7, 3)
            : new Thickness(10, 3, 10, 3);
        Grid.SetRow(UseProfileSettingsButton, stack ? 1 : 0);
        Grid.SetColumn(UseProfileSettingsButton, stack ? 0 : 1);
        UseProfileSettingsButton.HorizontalAlignment = stack
            ? Microsoft.UI.Xaml.HorizontalAlignment.Left
            : Microsoft.UI.Xaml.HorizontalAlignment.Right;
    }

    private void UpdateProfilesLayout(double fallbackWidth = double.NaN)
    {
        if (Volatile.Read(ref disposed) != 0
            || Root.FindName("ProfilesLayout") is not Grid profilesLayout
            || Root.FindName("ProfilesSecondColumn") is not ColumnDefinition profilesSecondColumn
            || Root.FindName("ProfilesThirdColumn") is not ColumnDefinition profilesThirdColumn
            || Root.FindName("BatteryProfileCard") is not FrameworkElement batteryProfileCard
            || Root.FindName("UsbCProfileCard") is not FrameworkElement usbCProfileCard)
        {
            return;
        }

        double effectiveWidth = profilesLayout.ActualWidth > 0
            ? profilesLayout.ActualWidth
            : double.IsFinite(fallbackWidth)
                ? fallbackWidth
                : Root.ActualWidth;
        bool stack = effectiveWidth < ProfilesStackThresholdDip;
        if (profilesAreStacked == stack)
        {
            return;
        }

        profilesAreStacked = stack;
        profilesLayout.ColumnSpacing = stack ? 0 : 12;
        profilesSecondColumn.Width = stack
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        profilesThirdColumn.Width = stack
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(batteryProfileCard, stack ? 1 : 0);
        Grid.SetColumn(batteryProfileCard, stack ? 0 : 1);
        Grid.SetRow(usbCProfileCard, stack ? 2 : 0);
        Grid.SetColumn(usbCProfileCard, stack ? 0 : 2);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}

public sealed class BooleanNegationToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
