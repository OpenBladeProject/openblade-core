using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private const double InitialWidthDip = 840;
    private const double InitialHeightDip = 720;
    private const double MinimumWidthDip = 800;
    private const double MinimumHeightDip = 600;
    private const double DashboardStackThresholdDip = 600;
    private const double ProfilesStackThresholdDip = 900;
    private static readonly TimeSpan ChargeLimitCommitDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LightingCommitDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue dispatcherQueue;
    private readonly MicaBackdropHost micaBackdropHost;
    private readonly UiPreferenceStore preferenceStore;
    private readonly UiPreferenceSaveQueue preferenceSaveQueue;
    private readonly VisibleObservationLoop visibleObservationLoop;
    private readonly SessionDisplaySettingsAdapter displaySettingsAdapter;
    private readonly DispatcherQueueTimer chargeLimitCommitTimer;
    private readonly DispatcherQueueTimer lightingCommitTimer;
    private readonly DispatcherQueueTimer displayRefreshTimer;
    private readonly object initializationSync = new();
    private Task? initializationTask;
    private bool initialPlacementAttempted;
    private bool changingPerformanceSelection;
    private bool changingCustomPerformanceSelection;
    private bool changingFanSelection;
    private bool changingBatteryProtection;
    private bool changingGamingMode;
    private bool changingStartupAnimation;
    private bool changingExternalLightingPolicy;
    private bool changingDisplaySettings;
    private bool changingDashboardCards;
    private bool dashboardCardsReady;
    private bool chargeLimitCommitInProgress;
    private bool fanControlCommitInProgress;
    private bool lightingCommitInProgress;
    private bool chargeLimitPointerInteraction;
    private int? pendingChargeLimitPercent;
    private bool? dashboardIsStacked;
    private bool? profilesUseCompactLayout;
    private bool enforcingMinimumWindowSize;
    private int disposed;
    private int dashboardPlacementGeneration;
    private ContentDialog? dashboardCustomizerDialog;
    private Action? dashboardCustomizerRefresh;
    private Dictionary<DashboardCardKind, DashboardCardRegistration>
        dashboardCardRegistrations = [];
    private Dictionary<DashboardCardKind, NavigationViewItem> dashboardCardDestinations = [];

    public MainWindow()
    {
        dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray must start on a dispatcher thread.");
        dispatcherQueue.EnsureSystemDispatcherQueue();
        IServiceClient serviceClient = new ServiceClient();
        ViewModel = new MainViewModel(
            serviceClient,
            new WinUiDispatcher(dispatcherQueue),
            new DiagnosticArchiveSaver(new WinUiDiagnosticArchiveDestination(AppWindow.Id)),
            new WindowsStartupRegistration(),
            uiVersion: OpenBladeProductVersion.Read(typeof(MainWindow).Assembly));
        displaySettingsAdapter = new SessionDisplaySettingsAdapter();
        DisplayViewModel = new DisplaySettingsViewModel(displaySettingsAdapter);
        DisplayViewModel.UserSettingsChanged += OnDisplayUserSettingsChanged;
        ViewModel.SavedDisplayProfileRestoreRequested += OnSavedDisplayProfileRestoreRequested;
        visibleObservationLoop = new VisibleObservationLoop(ViewModel.ObserveStateAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade visible observation failed: {0}",
                exception),
            observationIntervalProvider: () =>
                ViewModel.LiveReadingsObservationInterval);
        displayRefreshTimer = dispatcherQueue.CreateTimer();
        displayRefreshTimer.Interval = DisplayRefreshInterval;
        displayRefreshTimer.IsRepeating = true;
        displayRefreshTimer.Tick += OnDisplayRefreshTimerTick;
        InitializeComponent();
        dashboardCardRegistrations = new Dictionary<
            DashboardCardKind,
            DashboardCardRegistration>
        {
            [DashboardCardKind.DeviceStatus] = new(
                DeviceStatusCard,
                DashboardUnpinnedCardsHost,
                0),
            [DashboardCardKind.PowerMode] = new(PowerModeCard, LaptopCardsHost, 0),
            [DashboardCardKind.Fans] = new(FansCard, LaptopCardsHost, 1),
            [DashboardCardKind.Battery] = new(BatteryCard, LaptopCardsHost, 2),
        };
        dashboardCardDestinations = new Dictionary<DashboardCardKind, NavigationViewItem>
        {
            [DashboardCardKind.KeyboardBehavior] = LaptopNavigationItem,
            [DashboardCardKind.StartupAnimation] = LaptopNavigationItem,
            [DashboardCardKind.Lighting] = LightingNavigationItem,
            [DashboardCardKind.Display] = DisplayNavigationItem,
        };
        DashboardCards.CollectionChanged += OnDashboardCardsCollectionChanged;
        if (OpenBladeIconAssets.ApplicationIconPath is { } applicationIconPath)
        {
            AppWindow.SetIcon(applicationIconPath);
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        chargeLimitCommitTimer = dispatcherQueue.CreateTimer();
        chargeLimitCommitTimer.Interval = ChargeLimitCommitDelay;
        chargeLimitCommitTimer.IsRepeating = false;
        chargeLimitCommitTimer.Tick += OnChargeLimitCommitTimerTick;
        lightingCommitTimer = dispatcherQueue.CreateTimer();
        lightingCommitTimer.Interval = LightingCommitDelay;
        lightingCommitTimer.IsRepeating = false;
        lightingCommitTimer.Tick += OnLightingCommitTimerTick;
        micaBackdropHost = new MicaBackdropHost(this, Root);
        Root.DataContext = ViewModel;
        Root.Loaded += OnRootLoaded;
        Root.SizeChanged += OnRootSizeChanged;
        AppWindow.Changed += OnAppWindowChanged;

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

    public DisplaySettingsViewModel DisplayViewModel { get; }

    public bool StartMinimized { get; private set; }

    public ObservableCollection<DashboardCardOption> DashboardCards { get; } = [];

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
        DashboardCards.CollectionChanged -= OnDashboardCardsCollectionChanged;
        foreach (DashboardCardOption option in DashboardCards)
        {
            option.PropertyChanged -= OnDashboardCardOptionChanged;
        }
        DisplayViewModel.UserSettingsChanged -= OnDisplayUserSettingsChanged;
        ViewModel.SavedDisplayProfileRestoreRequested -= OnSavedDisplayProfileRestoreRequested;
        AppWindow.Closing -= OnClosing;
        Root.Loaded -= OnRootLoaded;
        Root.SizeChanged -= OnRootSizeChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        chargeLimitCommitTimer.Stop();
        chargeLimitCommitTimer.Tick -= OnChargeLimitCommitTimerTick;
        lightingCommitTimer.Stop();
        lightingCommitTimer.Tick -= OnLightingCommitTimerTick;
        displayRefreshTimer.Stop();
        displayRefreshTimer.Tick -= OnDisplayRefreshTimerTick;
        micaBackdropHost.Dispose();
        await visibleObservationLoop.DisposeAsync();
        await preferenceSaveQueue.DisposeAsync();
        await DisplayViewModel.DisposeAsync();
        await ViewModel.DisposeAsync();
    }

    private async Task InitializeCoreAsync()
    {
        ThemePreference initialTheme = ThemePreference.FollowWindows;
        LiveReadingsCadence liveReadingsRefreshCadence =
            LiveReadingsCadence.EveryTwoSeconds;
        TrayHoverLabelMode trayHoverLabelMode =
            TrayHoverLabelMode.ProfileAndPowerFlow;
        IReadOnlyList<LiveReadingItemKind> dashboardTelemetryItems =
            TelemetryPreferenceDefaults.DashboardItems;
        IReadOnlyList<TrayHoverLabelItemPreference> trayHoverLabelItems =
            TelemetryPreferenceDefaults.TrayHoverItems;
        IReadOnlyList<DashboardCardPreference> dashboardCards =
            DashboardCardPreferenceDefaults.Cards;
        try
        {
            UiPreferences preferences = await preferenceStore.LoadAsync();
            ThemeSelectionPolicy.Resolve(preferences.Theme);
            initialTheme = preferences.Theme;
            liveReadingsRefreshCadence = preferences.EffectiveLiveReadingsCadence;
            trayHoverLabelMode = preferences.TrayHoverLabelMode;
            dashboardTelemetryItems = preferences.EffectiveDashboardTelemetryItems;
            trayHoverLabelItems = preferences.EffectiveTrayHoverLabelItems;
            dashboardCards = preferences.EffectiveDashboardCards;
            StartMinimized = preferences.StartMinimized;
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
        ViewModel.LiveReadingsRefreshCadence = liveReadingsRefreshCadence;
        ViewModel.TrayHoverLabelMode = trayHoverLabelMode;
        ViewModel.ApplyTelemetryPreferences(dashboardTelemetryItems, trayHoverLabelItems);
        ApplyDashboardPreferences(dashboardCards);
        StartMinimizedToggle.IsOn = StartMinimized;
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

        DisplayViewModel.ActivePowerSource = ViewModel.ActivePowerSource;
        DisplayViewModel.ActiveProfileSettings = ViewModel.ActiveSavedDisplayProfile;
        DisplayViewModel.HasPendingProfileChange = ViewModel.HasDisplayProfileDraft;
        DisplayViewModel.ProfilePersistenceAvailable = ViewModel.CanControlHardware;
        await RefreshDisplaySettingsAsync();
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

        if (args.PropertyName == nameof(MainViewModel.ActivePowerSource))
        {
            DisplayViewModel.ActivePowerSource = ViewModel.ActivePowerSource;
        }

        if (args.PropertyName is nameof(MainViewModel.ActivePowerSource)
            or nameof(MainViewModel.ActiveSavedDisplayProfile))
        {
            DisplayViewModel.ActiveProfileSettings = ViewModel.ActiveSavedDisplayProfile;
        }

        if (args.PropertyName == nameof(MainViewModel.HasDisplayProfileDraft))
        {
            DisplayViewModel.HasPendingProfileChange = ViewModel.HasDisplayProfileDraft;
        }

        if (args.PropertyName == nameof(MainViewModel.CanControlHardware))
        {
            DisplayViewModel.ProfilePersistenceAvailable = ViewModel.CanControlHardware;
        }

        if (args.PropertyName is nameof(MainViewModel.LiveReadingsRefreshCadence)
            or nameof(MainViewModel.TrayHoverLabelMode)
            or nameof(MainViewModel.EnabledDashboardTelemetryItems)
            or nameof(MainViewModel.TrayHoverLabelPreferences))
        {
            QueuePreferenceSave();
            if (args.PropertyName == nameof(MainViewModel.LiveReadingsRefreshCadence))
            {
                visibleObservationLoop.StartOrWake();
            }
        }

        if (args.PropertyName is nameof(MainViewModel.LightingDraft)
            or nameof(MainViewModel.IsLightingApplyPending)
            or nameof(MainViewModel.LightingPowerPolicyDraft)
            or nameof(MainViewModel.HasUnsavedLightingPowerPolicy)
            or nameof(MainViewModel.CanControlLighting))
        {
            ScheduleLightingCommit();
        }

        if (args.PropertyName == nameof(MainViewModel.IsLightingVisible)
            && !ViewModel.IsLightingVisible
            && LightingPage.Visibility == Visibility.Visible)
        {
            void ReturnToDashboard() => MainNavigation.SelectedItem = HomeNavigationItem;
            if (dispatcherQueue.HasThreadAccess)
            {
                ReturnToDashboard();
            }
            else
            {
                dispatcherQueue.TryEnqueue(ReturnToDashboard);
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
                ViewModel.LiveReadingsEnabled,
                ViewModel.TrayHoverLabelMode,
                ViewModel.LiveReadingsRefreshCadence,
                StartMinimized,
                ViewModel.EnabledDashboardTelemetryItems,
                ViewModel.TrayHoverLabelPreferences,
                DashboardCards
                    .Select(option => new DashboardCardPreference(
                        option.Kind,
                        option.IsPinned))
                    .ToArray()));
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

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || enforcingMinimumWindowSize)
        {
            return;
        }

        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            uint nativeDpi = GetDpiForWindow(hwnd);
            if (nativeDpi == 0)
            {
                return;
            }

            SizeInt32 size = sender.Size;
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                sender.Id,
                DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea.WorkArea;
            PixelRect constrained = WindowPlacementPolicy.EnforceMinimumSize(
                new PixelRect(0, 0, size.Width, size.Height),
                MinimumWidthDip,
                MinimumHeightDip,
                checked((int)nativeDpi),
                workArea.Width,
                workArea.Height);
            if (constrained.Width == size.Width && constrained.Height == size.Height)
            {
                return;
            }

            enforcingMinimumWindowSize = true;
            sender.Resize(new SizeInt32(constrained.Width, constrained.Height));
        }
        finally
        {
            enforcingMinimumWindowSize = false;
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

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle
            || Volatile.Read(ref disposed) != 0
            || toggle.IsOn == StartMinimized)
        {
            return;
        }

        StartMinimized = toggle.IsOn;
        QueuePreferenceSave();
    }

    private async void OnExternalLightingOverridePolicyToggled(
        object sender,
        RoutedEventArgs args)
    {
        if (changingExternalLightingPolicy
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.AlwaysOverrideExternalLightingChanges)
        {
            return;
        }

        changingExternalLightingPolicy = true;
        toggle.IsEnabled = false;
        try
        {
            await ViewModel.SetExternalLightingOverridePolicyAsync(
                toggle.IsOn,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.AlwaysOverrideExternalLightingChanges;
            toggle.IsEnabled = true;
            changingExternalLightingPolicy = false;
        }
    }

    private async void OnGamingModeToggled(object sender, RoutedEventArgs args)
    {
        if (changingGamingMode
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.GamingModeToggleIsOn)
        {
            return;
        }

        changingGamingMode = true;
        try
        {
            await ApplyLaptopBehaviorToggleAsync(
                toggle,
                toggle.IsOn
                    ? ViewModel.GamingModeOnAction
                    : ViewModel.GamingModeOffAction,
                () => ViewModel.GamingModeToggleIsOn,
                "Gaming Mode");
        }
        finally
        {
            changingGamingMode = false;
        }
    }

    private async void OnStartupAnimationToggled(object sender, RoutedEventArgs args)
    {
        if (changingStartupAnimation
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.StartupAnimationToggleIsOn)
        {
            return;
        }

        changingStartupAnimation = true;
        try
        {
            await ApplyLaptopBehaviorToggleAsync(
                toggle,
                toggle.IsOn
                    ? ViewModel.StartupAnimationOnAction
                    : ViewModel.StartupAnimationOffAction,
                () => ViewModel.StartupAnimationToggleIsOn,
                "startup animation");
        }
        finally
        {
            changingStartupAnimation = false;
        }
    }

    private async Task ApplyLaptopBehaviorToggleAsync(
        ToggleSwitch toggle,
        ApplyOneShotLaptopControl action,
        Func<bool> currentState,
        string label)
    {
        if (ViewModel.ApplyOneShotLaptopControlCommand is not AsyncRelayCommand command
            || !command.CanExecute(action))
        {
            toggle.IsOn = currentState();
            return;
        }

        try
        {
            await command.ExecuteAsync(action);
            await ViewModel.ObserveStateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade {0} change failed: {1}", label, exception);
        }
        finally
        {
            toggle.IsOn = currentState();
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

    private async void OnDashboardCustomCpuChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingCustomPerformanceSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not CustomPerformanceLevel selected
            || selected == ViewModel.SelectedCustomCpuLevel)
        {
            return;
        }

        await ApplyCustomPerformanceChangeAsync(new CustomPerformanceSettings(
            selected,
            ViewModel.SelectedCustomGpuLevel));
    }

    private async void OnDashboardCustomGpuChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingCustomPerformanceSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not CustomPerformanceLevel selected
            || selected == ViewModel.SelectedCustomGpuLevel)
        {
            return;
        }

        await ApplyCustomPerformanceChangeAsync(new CustomPerformanceSettings(
            ViewModel.SelectedCustomCpuLevel,
            selected));
    }

    private async Task ApplyCustomPerformanceChangeAsync(
        CustomPerformanceSettings requested)
    {
        changingCustomPerformanceSelection = true;
        DashboardCustomCpuSelector.IsEnabled = false;
        DashboardCustomGpuSelector.IsEnabled = false;
        try
        {
            if (ViewModel.SetCustomPerformanceCommand is AsyncRelayCommand command
                && command.CanExecute(requested))
            {
                await command.ExecuteAsync(requested);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade custom performance selection failed: {0}", exception);
        }
        finally
        {
            DashboardCustomCpuSelector.SelectedItem = ViewModel.SelectedCustomCpuLevel;
            DashboardCustomGpuSelector.SelectedItem = ViewModel.SelectedCustomGpuLevel;
            DashboardCustomCpuSelector.IsEnabled = ViewModel.CanControlHardware;
            DashboardCustomGpuSelector.IsEnabled = ViewModel.CanControlHardware;
            changingCustomPerformanceSelection = false;
        }
    }

    private async void OnDashboardFanModeSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingFanSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || args.AddedItems.FirstOrDefault() is not FanModeOption selected
            || selected.Value == ViewModel.SelectedFanMode)
        {
            return;
        }

        changingFanSelection = true;
        if (ViewModel.SetFanModeCommand is not AsyncRelayCommand command
            || !command.CanExecute(selected.Value))
        {
            selector.SelectedItem = ViewModel.SelectedFanModeOption;
            changingFanSelection = false;
            return;
        }

        selector.IsEnabled = false;
        try
        {
            await command.ExecuteAsync(selected.Value);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade fan selection failed: {0}", exception);
        }
        finally
        {
            selector.SelectedItem = ViewModel.SelectedFanModeOption;
            selector.IsEnabled = true;
            changingFanSelection = false;
        }
    }

    private async void OnFixedFanSpeedCommit(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is Slider slider)
        {
            ViewModel.FixedFanSpeedRpm = IntegerEditorValue.ToInt32(slider.Value);
            await CommitFanControlAsync();
        }
    }

    private async void OnFixedFanSpeedKeyUp(
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
            ViewModel.FixedFanSpeedRpm = IntegerEditorValue.ToInt32(slider.Value);
            await CommitFanControlAsync();
        }
    }

    private async void OnFanCurvePointCommit(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is Slider { Tag: FanCurveEditorPoint point } slider)
        {
            ViewModel.UpdateFanCurvePoint(
                point,
                IntegerEditorValue.ToInt32(slider.Value));
            await CommitFanControlAsync();
        }
    }

    private async void OnFanCurvePointKeyUp(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (sender is Slider { Tag: FanCurveEditorPoint point } slider
            && args.Key is Windows.System.VirtualKey.Left
                or Windows.System.VirtualKey.Right
                or Windows.System.VirtualKey.PageUp
                or Windows.System.VirtualKey.PageDown
                or Windows.System.VirtualKey.Home
                or Windows.System.VirtualKey.End)
        {
            ViewModel.UpdateFanCurvePoint(
                point,
                IntegerEditorValue.ToInt32(slider.Value));
            await CommitFanControlAsync();
        }
    }

    private async void OnManualFanCurveSettingChanged(object sender, RoutedEventArgs args) =>
        await CommitFanControlAsync();

    private async Task CommitFanControlAsync()
    {
        if (fanControlCommitInProgress
            || Volatile.Read(ref disposed) != 0
            || ViewModel.SetFanControlCommand is not AsyncRelayCommand command)
        {
            return;
        }

        FanControlSettings settings;
        try
        {
            settings = ViewModel.CurrentFanControlSettings;
        }
        catch (ArgumentException exception)
        {
            Trace.TraceWarning("OpenBlade rejected an invalid fan curve: {0}", exception.Message);
            return;
        }

        if (!command.CanExecute(settings))
        {
            return;
        }

        fanControlCommitInProgress = true;
        try
        {
            await command.ExecuteAsync(settings);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade fan-control change failed: {0}", exception);
        }
        finally
        {
            fanControlCommitInProgress = false;
        }
    }

    private void ScheduleLightingCommit()
    {
        if (Volatile.Read(ref disposed) != 0
            || (!ViewModel.IsLightingApplyPending
                && !ViewModel.HasUnsavedLightingPowerPolicy))
        {
            lightingCommitTimer.Stop();
            return;
        }

        lightingCommitTimer.Stop();
        lightingCommitTimer.Start();
    }

    private async void OnLightingCommitTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Volatile.Read(ref disposed) != 0
            || !ViewModel.CanControlLighting)
        {
            return;
        }
        if (lightingCommitInProgress)
        {
            sender.Start();
            return;
        }

        lightingCommitInProgress = true;
        try
        {
            if (ViewModel.IsLightingApplyPending
                && ViewModel.SetLightingCommand is AsyncRelayCommand lightingCommand)
            {
                LightingSettings draft = ViewModel.LightingDraft;
                if (lightingCommand.CanExecute(draft))
                {
                    await lightingCommand.ExecuteAsync(draft);
                }
            }

            if (ViewModel.HasUnsavedLightingPowerPolicy
                && ViewModel.SaveLightingPowerPolicyCommand is AsyncRelayCommand policyCommand)
            {
                LightingPowerPolicy policy = ViewModel.LightingPowerPolicyDraft;
                if (policyCommand.CanExecute(policy))
                {
                    await policyCommand.ExecuteAsync(policy);
                }
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade automatic lighting apply failed: {0}", exception);
        }
        finally
        {
            lightingCommitInProgress = false;
            if (Volatile.Read(ref disposed) == 0
                && ViewModel.CanControlLighting
                && (ViewModel.IsLightingApplyPending
                    || ViewModel.HasUnsavedLightingPowerPolicy))
            {
                sender.Start();
            }
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
        LaptopPage.Visibility = destination == "laptop"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LightingPage.Visibility = destination == "lighting" && ViewModel.IsLightingVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        DisplayPage.Visibility = destination == "display"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPage.Visibility = destination == "settings"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AboutPage.Visibility = destination == "about"
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
                () =>
                {
                    ArrangeDashboardCards();
                    UpdateDashboardLayout();
                });
        }
        else if (destination == "display")
        {
            _ = RefreshDisplaySettingsAsync();
        }

        UpdateDisplayRefreshState();
    }

    private async void OnDisplayRefreshTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (Volatile.Read(ref disposed) != 0
            || (DisplayPage.Visibility != Visibility.Visible
                && !(HomePage.Visibility == Visibility.Visible
                    && IsDashboardCardPinned(DashboardCardKind.Display)))
            || DisplayViewModel.IsBusy
            || DisplayRefreshRatePicker.IsDropDownOpen
            || DisplayColorProfilePicker.IsDropDownOpen)
        {
            return;
        }

        await RefreshDisplaySettingsAsync();
    }

    private bool IsDashboardCardPinned(DashboardCardKind kind) =>
        DashboardCards.Any(option => option.Kind == kind && option.IsPinned);

    private void UpdateDisplayRefreshState()
    {
        bool shouldRefresh = DisplayPage.Visibility == Visibility.Visible
            || (HomePage.Visibility == Visibility.Visible
                && IsDashboardCardPinned(DashboardCardKind.Display));
        if (shouldRefresh)
        {
            displayRefreshTimer.Start();
        }
        else
        {
            displayRefreshTimer.Stop();
        }
    }

    private async void OnDisplayRefreshRateChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.ComboBox
            { SelectedItem: DisplayRefreshRateOption option }
            || changingDisplaySettings
            || string.Equals(
                option.Key,
                DisplayViewModel.SelectedRefreshRate?.Key,
                StringComparison.Ordinal))
        {
            return;
        }

        await RunDisplayOperationAsync(
            () => DisplayViewModel.SelectRefreshRateAsync(option));
    }

    private async void OnDisplayHdrToggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle
            || changingDisplaySettings
            || toggle.IsOn == DisplayViewModel.IsHdrEnabled)
        {
            return;
        }

        await RunDisplayOperationAsync(
            () => DisplayViewModel.SetHdrEnabledAsync(toggle.IsOn));
    }

    private async void OnDisplayColorProfileChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.ComboBox
            { SelectedItem: DisplayColorProfileOption option }
            || changingDisplaySettings
            || string.Equals(
                option.Key,
                DisplayViewModel.SelectedColorProfile?.Key,
                StringComparison.Ordinal))
        {
            return;
        }

        await RunDisplayOperationAsync(
            () => DisplayViewModel.SelectColorProfileAsync(option));
    }

    private Task RefreshDisplaySettingsAsync() =>
        RunDisplayOperationAsync(() => DisplayViewModel.RefreshAsync());

    private void OnDisplayUserSettingsChanged(
        object? sender,
        DisplayProfileSettingsChangedEventArgs args) =>
        ViewModel.RecordDisplayProfileChange(
            args.PowerSource,
            args.Settings,
            args.PreviousSettings);

    private async void OnSaveCurrentDisplaySettingsToProfile(
        object sender,
        RoutedEventArgs args)
    {
        PowerSource sourceAtStart = ViewModel.ActivePowerSource;
        if (sourceAtStart is not (PowerSource.PluggedIn
            or PowerSource.Battery
            or PowerSource.UsbC))
        {
            return;
        }

        await RefreshDisplaySettingsAsync();
        if (sourceAtStart != ViewModel.ActivePowerSource
            || !DisplayViewModel.CanCaptureCurrentSettings)
        {
            return;
        }

        ViewModel.RecordDisplayProfileChange(
            sourceAtStart,
            DisplayViewModel.CurrentProfileSettings);
        if (ViewModel.SaveDisplayProfileDraftCommand is AsyncRelayCommand command
            && command.CanExecute(null))
        {
            await command.ExecuteAsync(null);
        }
    }

    private async Task<bool> OnSavedDisplayProfileRestoreRequested(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken token)
    {
        bool restored = await DisplayViewModel.ApplyProfileAsync(
            expectedPowerSource,
            settings,
            token);
        SynchronizeDisplayControls();
        return restored;
    }

    private async Task RunDisplayOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade display operation failed: {0}", exception);
        }
        finally
        {
            SynchronizeDisplayControls();
        }
    }

    private void SynchronizeDisplayControls()
    {
        bool wasChanging = changingDisplaySettings;
        changingDisplaySettings = true;
        try
        {
            if ((DisplayRefreshRatePicker.SelectedItem as DisplayRefreshRateOption)?.Key
                != DisplayViewModel.SelectedRefreshRate?.Key)
            {
                DisplayRefreshRatePicker.SelectedItem = DisplayViewModel.SelectedRefreshRate;
            }
            DisplayHdrToggle.IsOn = DisplayViewModel.IsHdrEnabled;
            if ((DisplayColorProfilePicker.SelectedItem as DisplayColorProfileOption)?.Key
                != DisplayViewModel.SelectedColorProfile?.Key)
            {
                DisplayColorProfilePicker.SelectedItem = DisplayViewModel.SelectedColorProfile;
            }
        }
        finally
        {
            changingDisplaySettings = wasChanging;
        }
    }

    private void OnProfileStateBadgeClick(object sender, RoutedEventArgs args)
    {
        MainNavigation.SelectedItem = AboutNavigationItem;
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

    private async void OnShutdownOpenBlade(object sender, RoutedEventArgs args)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Shut down OpenBlade?",
            Content = "This stops the app, display helper, and hardware service. "
                + "OpenBlade will remain off until Windows restarts or the service is started again.",
            PrimaryButtonText = "Shut down",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Root.Resources["DestructiveButtonStyle"],
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.ShutdownOpenBladeAsync(CancellationToken.None);
            _ = SessionAgentLauncher.TryStopSibling();
            if (Microsoft.UI.Xaml.Application.Current is App app)
            {
                await app.ExitTrayAsync();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade shutdown failed: {0}", exception);
            var failure = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "OpenBlade could not shut down",
                Content = exception.Message,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await failure.ShowAsync();
        }
    }

    private async void OnExportProfiles(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = $"OpenBlade-profiles-{DateTime.Now:yyyy-MM-dd}",
                DefaultFileExtension = ".json",
                ShowOverwritePrompt = true,
                Title = "Export OpenBlade profiles",
            };
            picker.FileTypeChoices.Add("JSON files", [".json"]);

            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is null)
            {
                return;
            }

            string applicationVersion = OpenBladeProductVersion.Read(
                typeof(MainWindow).Assembly);
            string json = ProfileExportSerializer.Serialize(
                ViewModel.Profiles,
                applicationVersion,
                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(result.Path, json);
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade profile export failed: {0}", exception);
            var failure = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Profiles could not be exported",
                Content = "OpenBlade could not save the selected JSON file.",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await failure.ShowAsync();
        }
    }

    private void ApplyDashboardPreferences(
        IReadOnlyList<DashboardCardPreference> preferences)
    {
        changingDashboardCards = true;
        try
        {
            foreach (DashboardCardOption option in DashboardCards)
            {
                option.PropertyChanged -= OnDashboardCardOptionChanged;
            }

            DashboardCards.Clear();
            foreach (DashboardCardOption option in
                     DashboardCardPreferenceDefaults.CreateOptions(preferences))
            {
                option.PropertyChanged += OnDashboardCardOptionChanged;
                DashboardCards.Add(option);
            }
        }
        finally
        {
            changingDashboardCards = false;
        }

        ArrangeDashboardCards();
        UpdateDisplayRefreshState();
    }

    private void OnDashboardCardOptionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (changingDashboardCards || args.PropertyName != nameof(DashboardCardOption.IsPinned))
        {
            return;
        }

        ArrangeDashboardCards();
        UpdateDisplayRefreshState();
        QueuePreferenceSave();
    }

    private void OnDashboardCardsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (changingDashboardCards)
        {
            return;
        }

        ArrangeDashboardCards();
        UpdateDisplayRefreshState();
        QueuePreferenceSave();
    }

    private async void OnCustomizeDashboard(object sender, RoutedEventArgs args)
    {
        if (dashboardCustomizerDialog is not null)
        {
            dashboardCustomizerRefresh?.Invoke();
            await dashboardCustomizerDialog.ShowAsync();
            return;
        }

        var cardList = new StackPanel { Spacing = 2 };
        AutomationProperties.SetName(cardList, "Dashboard cards");
        DashboardCardKind? draggedKind = null;
        double dragStartY = 0;
        Grid? draggedRow = null;
        FontIcon? draggedGrip = null;

        void EndPointerDrag()
        {
            if (draggedRow is not null)
            {
                draggedRow.Opacity = 1;
                draggedRow.Translation = System.Numerics.Vector3.Zero;
                Canvas.SetZIndex(draggedRow, 0);
            }

            draggedKind = null;
            draggedRow = null;
            draggedGrip = null;
        }

        void RefreshEditor()
        {
            cardList.Children.Clear();
            foreach (DashboardCardOption option in DashboardCards)
            {
                var row = new Grid
                {
                    ColumnSpacing = 10,
                    MinHeight = 58,
                    Padding = new Thickness(2, 6, 2, 6),
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var grip = new FontIcon
                {
                    Glyph = "\uE76F",
                    FontSize = 14,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(grip, $"Drag {option.Label}");
                row.Children.Add(grip);

                var label = new StackPanel { Spacing = 2 };
                label.Children.Add(new TextBlock
                {
                    Text = option.Label,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                label.Children.Add(new TextBlock
                {
                    Text = option.Description,
                    Style = (Style)Root.Resources["SecondaryTextStyle"],
                    TextWrapping = TextWrapping.Wrap,
                });
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                var isPinned = new Microsoft.UI.Xaml.Controls.CheckBox
                {
                    IsChecked = option.IsPinned,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(isPinned, option.Label);
                isPinned.Checked += (_, _) => option.IsPinned = true;
                isPinned.Unchecked += (_, _) => option.IsPinned = false;
                Grid.SetColumn(isPinned, 2);
                row.Children.Add(isPinned);

                grip.PointerPressed += (_, pointerArgs) =>
                {
                    if (!pointerArgs.GetCurrentPoint(cardList).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    draggedKind = option.Kind;
                    draggedRow = row;
                    draggedGrip = grip;
                    dragStartY = pointerArgs.GetCurrentPoint(cardList).Position.Y;
                    row.Opacity = 0.72;
                    Canvas.SetZIndex(row, 1);
                    grip.CapturePointer(pointerArgs.Pointer);
                    pointerArgs.Handled = true;
                };
                grip.PointerMoved += (_, pointerArgs) =>
                {
                    if (draggedRow != row
                        || !pointerArgs.GetCurrentPoint(cardList).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    double currentY = pointerArgs.GetCurrentPoint(cardList).Position.Y;
                    row.Translation = new System.Numerics.Vector3(
                        0,
                        (float)(currentY - dragStartY),
                        0);
                    pointerArgs.Handled = true;
                };
                grip.PointerReleased += (_, pointerArgs) =>
                {
                    if (draggedKind is not { } sourceKind || draggedRow != row)
                    {
                        return;
                    }

                    double releaseY = pointerArgs.GetCurrentPoint(cardList).Position.Y;
                    draggedGrip?.ReleasePointerCapture(pointerArgs.Pointer);
                    int sourceIndex = DashboardCards
                        .Select((item, index) => (item, index))
                        .First(pair => pair.item.Kind == sourceKind)
                        .index;
                    int targetIndex = cardList.Children.Count - 1;
                    for (int index = 0; index < cardList.Children.Count; index++)
                    {
                        FrameworkElement candidate = (FrameworkElement)cardList.Children[index];
                        double midpoint = candidate
                            .TransformToVisual(cardList)
                            .TransformPoint(new Windows.Foundation.Point())
                            .Y + (candidate.ActualHeight / 2);
                        if (releaseY < midpoint)
                        {
                            targetIndex = index;
                            break;
                        }
                    }

                    EndPointerDrag();
                    if (sourceIndex != targetIndex)
                    {
                        DashboardCards.Move(sourceIndex, targetIndex);
                    }

                    RefreshEditor();
                    pointerArgs.Handled = true;
                };
                grip.PointerCaptureLost += (_, _) => EndPointerDrag();
                cardList.Children.Add(row);
            }
        }

        RefreshEditor();
        dashboardCustomizerRefresh = RefreshEditor;

        var content = new StackPanel { Width = 400, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Check cards to show. Drag them into the order you want.",
            Style = (Style)Root.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new ScrollViewer
        {
            Content = cardList,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Customize Dashboard",
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };
        dashboardCustomizerDialog = dialog;
        await dialog.ShowAsync();
    }

    private void ArrangeDashboardCards()
    {
        if (!dashboardCardsReady)
        {
            return;
        }

        int generation = ++dashboardPlacementGeneration;
        bool showPinnedCards = HomePage.Visibility == Visibility.Visible;
        int dashboardIndex = 0;
        var placements = new List<DashboardCardPlacement>(dashboardCardRegistrations.Count);
        foreach (DashboardCardOption option in DashboardCards)
        {
            bool placeOnDashboard = showPinnedCards && option.IsPinned;
            if (dashboardCardRegistrations.TryGetValue(
                    option.Kind,
                    out DashboardCardRegistration? registration))
            {
                placements.Add(new DashboardCardPlacement(
                    registration.Element,
                    placeOnDashboard ? DashboardCardHost : registration.SourceHost,
                    placeOnDashboard ? dashboardIndex++ : registration.SourceIndex));
                continue;
            }

            if (placeOnDashboard
                && dashboardCardDestinations.TryGetValue(
                    option.Kind,
                    out NavigationViewItem? destination))
            {
                placements.Add(new DashboardCardPlacement(
                    CreateDashboardLinkCard(option, destination),
                    DashboardCardHost,
                    dashboardIndex++));
            }
        }

        DashboardEmptyState.Visibility = DashboardCards.Any(option => option.IsPinned)
            ? Visibility.Collapsed
            : Visibility.Visible;

        foreach (DashboardCardRegistration registration in dashboardCardRegistrations.Values)
        {
            if (registration.Element.Parent is Microsoft.UI.Xaml.Controls.Panel current)
            {
                current.Children.Remove(registration.Element);
            }
        }

        DashboardCardHost.Children.Clear();

        dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => CompleteDashboardArrangement(generation, placements));
    }

    private void CompleteDashboardArrangement(
        int generation,
        IReadOnlyList<DashboardCardPlacement> placements)
    {
        if (generation != dashboardPlacementGeneration || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        foreach (IGrouping<StackPanel, DashboardCardPlacement> group in
                 placements.GroupBy(placement => placement.Destination))
        {
            foreach (DashboardCardPlacement placement in group.OrderBy(placement => placement.Index))
            {
                if (placement.Element.Parent is Microsoft.UI.Xaml.Controls.Panel current)
                {
                    current.Children.Remove(placement.Element);
                }

                group.Key.Children.Add(placement.Element);
            }
        }
    }

    private Border CreateDashboardLinkCard(
        DashboardCardOption option,
        NavigationViewItem destination)
    {
        var label = new StackPanel { Spacing = 3 };
        label.Children.Add(new TextBlock
        {
            Text = option.Label,
            Style = (Style)Root.Resources["CardTitleStyle"],
        });
        label.Children.Add(new TextBlock
        {
            Text = option.Description,
            Style = (Style)Root.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        var open = new Microsoft.UI.Xaml.Controls.Button
        {
            Content = "Open",
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(open, $"Open {option.Label}");
        open.Click += (_, _) => MainNavigation.SelectedItem = destination;

        var layout = new Grid { ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(label);
        Grid.SetColumn(open, 1);
        layout.Children.Add(open);

        return new Border
        {
            Style = (Style)Root.Resources["CardStyle"],
            Child = layout,
        };
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        dashboardCardsReady = true;
        ArrangeDashboardCards();
        UpdateDisplayRefreshState();
    }

    private sealed record DashboardCardRegistration(
        FrameworkElement Element,
        StackPanel SourceHost,
        int SourceIndex);

    private sealed record DashboardCardPlacement(
        FrameworkElement Element,
        StackPanel Destination,
        int Index);

    private async void OnImportProfiles(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                Title = "Import OpenBlade profiles",
            };
            picker.FileTypeFilter.Add(".json");

            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is null)
            {
                return;
            }

            ProfileSet profiles = ProfileExportSerializer.Deserialize(
                await File.ReadAllTextAsync(result.Path));
            var confirmation = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Import profiles?",
                Content = "This replaces the saved AC, USB-C, battery, and battery-protection profiles. Current hardware settings are not changed until you apply or switch profiles.",
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!await ViewModel.ImportProfilesAsync(profiles, CancellationToken.None))
            {
                throw new InvalidDataException(
                    "The imported profiles include settings that are unavailable on this laptop.");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade profile import failed: {0}", exception);
            var failure = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Profiles could not be imported",
                Content = exception.Message,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await failure.ShowAsync();
        }
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
    }

    private void UpdateProfilesLayout(double fallbackWidth = double.NaN)
    {
        if (Volatile.Read(ref disposed) != 0
            || Root.FindName("ProfilesComparisonLayout") is not FrameworkElement comparisonLayout
            || Root.FindName("ProfilesCompactLayout") is not FrameworkElement compactLayout)
        {
            return;
        }

        double effectiveWidth = ProfilesPage.ActualWidth > 0
            ? ProfilesPage.ActualWidth
            : double.IsFinite(fallbackWidth)
                ? fallbackWidth
                : Root.ActualWidth;
        bool useCompactLayout = effectiveWidth < ProfilesStackThresholdDip;
        if (profilesUseCompactLayout == useCompactLayout)
        {
            return;
        }

        profilesUseCompactLayout = useCompactLayout;
        comparisonLayout.Visibility = useCompactLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        compactLayout.Visibility = useCompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
