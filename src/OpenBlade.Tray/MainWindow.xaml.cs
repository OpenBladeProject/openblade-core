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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class MainWindow : Window, IAsyncDisposable, INotifyPropertyChanged
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
    private readonly AccentColorManager accentColorManager;
    private readonly UiPreferenceStore preferenceStore;
    private readonly UiPreferenceSaveQueue preferenceSaveQueue;
    private readonly VisibleObservationLoop visibleObservationLoop;
    private readonly VisibleObservationLoop trayObservationLoop;
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
    private bool changingPageSections;
    private bool changingPrimaryNavigationItems;
    private bool changingPrimaryNavigationOrder;
    private bool changingSpecialKeyBindings;
    private bool changingAccentColor;
    private bool dashboardCardsReady;
    private bool canonicalLaptopCardsPlaced;
    private bool chargeLimitCommitInProgress;
    private bool fanControlCommitInProgress;
    private bool lightingCommitInProgress;
    private bool chargeLimitPointerInteraction;
    private int? pendingChargeLimitPercent;
    private bool? dashboardIsStacked;
    private bool? profilesUseCompactLayout;
    private bool enforcingMinimumWindowSize;
    private bool windowVisible;
    private int openDisplayPickerCount;
    private int disposed;
    private ContentDialog? dashboardCustomizerDialog;
    private Action? dashboardCustomizerRefresh;
    private Action? dashboardCustomizerApply;
    private readonly Dictionary<DashboardCardKind, FrameworkElement> dashboardCardPresentations = [];
    private readonly Dictionary<CustomizablePageKind, ObservableCollection<PageSectionOption>>
        pageSections = [];
    private readonly Dictionary<CustomizablePageKind, Microsoft.UI.Xaml.Controls.Panel>
        pageSectionHosts = [];
    private readonly Dictionary<PageSectionKind, FrameworkElement> pageSectionPresentations = [];
    private AppAccentColorMode accentColorMode = AppAccentColorMode.RazerGreen;
    private string customAccentColor = AppAccentColorPreferenceDefaults.RazerGreenHex;

    public event PropertyChangedEventHandler? PropertyChanged;

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
        DisplayViewModel.PropertyChanged += OnDisplayViewModelPropertyChanged;
        DisplayViewModel.UserSettingsChanged += OnDisplayUserSettingsChanged;
        ViewModel.SavedDisplayProfileRestoreRequested += OnSavedDisplayProfileRestoreRequested;
        visibleObservationLoop = new VisibleObservationLoop(ViewModel.ObserveStateAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade visible observation failed: {0}",
                exception),
            observationIntervalProvider: () =>
                ViewModel.LiveReadingsObservationInterval);
        trayObservationLoop = new VisibleObservationLoop(ViewModel.ObserveTrayStateAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade tray observation failed: {0}",
                exception),
            observationIntervalProvider: () =>
                ViewModel.LiveReadingsObservationInterval);
        displayRefreshTimer = dispatcherQueue.CreateTimer();
        displayRefreshTimer.Interval = DisplayRefreshInterval;
        displayRefreshTimer.IsRepeating = true;
        displayRefreshTimer.Tick += OnDisplayRefreshTimerTick;
        InitializeComponent();
        DashboardCards.CollectionChanged += OnDashboardCardsCollectionChanged;
        PrimaryNavigationItems.CollectionChanged += OnPrimaryNavigationItemsCollectionChanged;
        accentColorManager = new AccentColorManager(Root, dispatcherQueue);
        accentColorManager.ColorChanged += OnAccentColorChanged;
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

    public ObservableCollection<PrimaryNavigationItemOption> PrimaryNavigationItems { get; } = [];

    public ObservableCollection<SpecialKeyBindingViewModel> SpecialKeyBindings { get; } = [];

    public IReadOnlyList<AppAccentColorOption> AccentColorOptions { get; } =
    [
        new(AppAccentColorMode.RazerGreen, "OpenBlade green"),
        new(AppAccentColorMode.Windows, "Windows accent"),
        new(AppAccentColorMode.Custom, "Custom"),
    ];

    public AppAccentColorOption SelectedAccentColorOption
    {
        get => AccentColorOptions.Single(option => option.Mode == accentColorMode);
        set
        {
            if (value is null || value.Mode == accentColorMode)
            {
                return;
            }

            accentColorMode = value.Mode;
            OnPropertyChanged(nameof(SelectedAccentColorOption));
            OnPropertyChanged(nameof(CustomAccentColorVisibility));
            ApplyAccentColorAndQueueSave();
        }
    }

    public string AccentColorSummary => accentColorMode switch
    {
        AppAccentColorMode.RazerGreen => "OpenBlade green (#47E10C)",
        AppAccentColorMode.Windows => "Windows accent",
        AppAccentColorMode.Custom => $"Custom ({customAccentColor})",
        _ => "OpenBlade green (#47E10C)",
    };

    public Microsoft.UI.Xaml.Media.Brush AccentColorPreviewBrush =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(accentColorManager.CurrentColor);

    public Visibility CustomAccentColorVisibility => accentColorMode == AppAccentColorMode.Custom
        ? Visibility.Visible
        : Visibility.Collapsed;

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
        windowVisible = true;
        trayObservationLoop.Stop();
        visibleObservationLoop.StartOrWake();
        UpdateDisplayRefreshState();
        AppWindow.Show(true);
    }

    public void StartHiddenObservation()
    {
        windowVisible = false;
        visibleObservationLoop.Stop();
        displayRefreshTimer.Stop();
        trayObservationLoop.StartOrWake();
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
        DisplayViewModel.PropertyChanged -= OnDisplayViewModelPropertyChanged;
        DashboardCards.CollectionChanged -= OnDashboardCardsCollectionChanged;
        PrimaryNavigationItems.CollectionChanged -= OnPrimaryNavigationItemsCollectionChanged;
        foreach (DashboardCardOption option in DashboardCards)
        {
            option.PropertyChanged -= OnDashboardCardOptionChanged;
        }
        foreach (ObservableCollection<PageSectionOption> sections in pageSections.Values)
        {
            sections.CollectionChanged -= OnPageSectionsCollectionChanged;
            foreach (PageSectionOption section in sections)
            {
                section.PropertyChanged -= OnPageSectionOptionChanged;
            }
        }
        foreach (SpecialKeyBindingViewModel binding in SpecialKeyBindings)
        {
            binding.PropertyChanged -= OnSpecialKeyBindingChanged;
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
        accentColorManager.ColorChanged -= OnAccentColorChanged;
        accentColorManager.Dispose();
        await visibleObservationLoop.DisposeAsync();
        await trayObservationLoop.DisposeAsync();
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
        IReadOnlyList<PrimaryNavigationItemKind> primaryNavigationItems =
            PrimaryNavigationPreferenceDefaults.Items;
        IReadOnlyList<SpecialKeyBindingPreference> specialKeyBindings =
            SpecialKeyBindingDefaults.Bindings;
        IReadOnlyList<PageLayoutPreference> pageLayouts =
            PageLayoutPreferenceDefaults.Layouts;
        AppAccentColorMode initialAccentColorMode = AppAccentColorMode.RazerGreen;
        string initialCustomAccentColor = AppAccentColorPreferenceDefaults.RazerGreenHex;
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
            primaryNavigationItems = preferences.EffectivePrimaryNavigationItems;
            specialKeyBindings = preferences.EffectiveSpecialKeyBindings;
            pageLayouts = preferences.EffectivePageLayouts;
            initialAccentColorMode = preferences.EffectiveAccentColorMode;
            initialCustomAccentColor = preferences.EffectiveCustomAccentColor;
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
        ApplyPageLayoutPreferences(pageLayouts);
        ApplyPrimaryNavigationPreferences(primaryNavigationItems);
        ApplySpecialKeyBindingPreferences(specialKeyBindings);
        changingAccentColor = true;
        accentColorMode = initialAccentColorMode;
        customAccentColor = initialCustomAccentColor;
        accentColorManager.Apply(accentColorMode, customAccentColor);
        changingAccentColor = false;
        NotifyAccentColorPropertiesChanged();
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

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(ViewModel)));

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
            if (args.PropertyName is nameof(MainViewModel.LiveReadingsRefreshCadence)
                or nameof(MainViewModel.EnabledDashboardTelemetryItems)
                or nameof(MainViewModel.TrayHoverLabelPreferences))
            {
                WakeActiveObservationLoop();
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

    private void OnDisplayViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(DisplayViewModel)));
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

    private void ApplyAccentColorAndQueueSave()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        accentColorManager.Apply(accentColorMode, customAccentColor);
        NotifyAccentColorPropertiesChanged();
        if (!changingAccentColor)
        {
            QueuePreferenceSave();
        }
    }

    private void OnAccentColorChanged(object? sender, EventArgs args) =>
        NotifyAccentColorPropertiesChanged();

    private void NotifyAccentColorPropertiesChanged()
    {
        OnPropertyChanged(nameof(AccentColorSummary));
        OnPropertyChanged(nameof(AccentColorPreviewBrush));
        OnPropertyChanged(nameof(CustomAccentColorVisibility));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void QueuePreferenceSave()
    {
        try
        {
            preferenceSaveQueue.Enqueue(new UiPreferences(
                Theme: ViewModel.Theme,
                LiveReadingsEnabled: ViewModel.LiveReadingsEnabled,
                TrayHoverLabelMode: ViewModel.TrayHoverLabelMode,
                LiveReadingsRefreshCadence: ViewModel.LiveReadingsRefreshCadence,
                StartMinimized: StartMinimized,
                DashboardTelemetryItems: ViewModel.EnabledDashboardTelemetryItems,
                TrayHoverLabelItems: ViewModel.TrayHoverLabelPreferences,
                DashboardCards: DashboardCards
                    .Select(option => new DashboardCardPreference(
                        option.Kind,
                        option.IsPinned))
                    .ToArray(),
                PrimaryNavigationItems: PrimaryNavigationItems
                    .Select(option => option.Kind)
                    .ToArray(),
                AccentColorMode: accentColorMode,
                CustomAccentColor: customAccentColor,
                SpecialKeyBindings: SpecialKeyBindings
                    .Select(binding => binding.ToPreference())
                    .ToArray(),
                PageLayouts: pageSections
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new PageLayoutPreference(
                        pair.Key,
                        pair.Value
                            .Select(section => new PageSectionPreference(
                                section.Kind,
                                section.IsVisible))
                            .ToArray()))
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
            StartHiddenObservation();
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

        await ApplyCustomPerformanceChangeAsync(
            new CustomPerformanceSettings(
                selected,
                ViewModel.SelectedCustomGpuLevel),
            selector);
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

        await ApplyCustomPerformanceChangeAsync(
            new CustomPerformanceSettings(
                ViewModel.SelectedCustomCpuLevel,
                selected),
            selector);
    }

    private async Task ApplyCustomPerformanceChangeAsync(
        CustomPerformanceSettings requested,
        Microsoft.UI.Xaml.Controls.ComboBox selector)
    {
        changingCustomPerformanceSelection = true;
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
            selector.SelectedItem = selector.Name == "DashboardCustomCpuSelector"
                ? ViewModel.SelectedCustomCpuLevel
                : ViewModel.SelectedCustomGpuLevel;
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
        if (changingPrimaryNavigationOrder)
        {
            return;
        }

        string destination = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string
            ?? "home";
        ApplyNavigationDestination(destination);
    }

    private void ApplySpecialKeyBindingPreferences(
        IReadOnlyList<SpecialKeyBindingPreference> preferences)
    {
        changingSpecialKeyBindings = true;
        try
        {
            foreach (SpecialKeyBindingViewModel existing in SpecialKeyBindings)
            {
                existing.PropertyChanged -= OnSpecialKeyBindingChanged;
            }
            SpecialKeyBindings.Clear();
            foreach (SpecialKeyBindingPreference preference in
                     SpecialKeyBindingDefaults.Normalize(preferences))
            {
                var binding = new SpecialKeyBindingViewModel(preference);
                binding.PropertyChanged += OnSpecialKeyBindingChanged;
                SpecialKeyBindings.Add(binding);
            }
        }
        finally
        {
            changingSpecialKeyBindings = false;
        }
    }

    private void OnSpecialKeyBindingChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (!changingSpecialKeyBindings)
        {
            QueuePreferenceSave();
        }
    }

    private void ApplyNavigationDestination(string destination)
    {
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
        else if (destination == "lighting")
        {
            dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    LightingCard.UpdateLayout();
                    TryRegisterLightingPageSections();
                });
        }
        else if (destination == "home")
        {
            dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    RefreshDashboardCards();
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
            || openDisplayPickerCount > 0)
        {
            return;
        }

        await RefreshDisplaySettingsAsync();
    }

    private bool IsDashboardCardPinned(DashboardCardKind kind) =>
        DashboardCards.Any(option => option.Kind == kind && option.IsPinned);

    private void UpdateDisplayRefreshState()
    {
        bool shouldRefresh = windowVisible
            && (DisplayPage.Visibility == Visibility.Visible
                || (HomePage.Visibility == Visibility.Visible
                    && IsDashboardCardPinned(DashboardCardKind.Display)));
        if (shouldRefresh)
        {
            displayRefreshTimer.Start();
        }
        else
        {
            displayRefreshTimer.Stop();
        }
    }

    private void WakeActiveObservationLoop()
    {
        if (windowVisible)
        {
            visibleObservationLoop.StartOrWake();
        }
        else
        {
            trayObservationLoop.StartOrWake();
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

    private void OnDisplayPickerOpened(object sender, object args) =>
        openDisplayPickerCount++;

    private void OnDisplayPickerClosed(object sender, object args) =>
        openDisplayPickerCount = Math.Max(0, openDisplayPickerCount - 1);

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
        bool wasChanging = changingDisplaySettings;
        changingDisplaySettings = true;
        try
        {
            return await DisplayViewModel.ApplyProfileAsync(
                expectedPowerSource,
                settings,
                token);
        }
        finally
        {
            changingDisplaySettings = wasChanging;
        }
    }

    private async Task RunDisplayOperationAsync(Func<Task> operation)
    {
        bool wasChanging = changingDisplaySettings;
        changingDisplaySettings = true;
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

        RefreshDashboardCards();
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

        RefreshDashboardCards();
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

        RefreshDashboardCards();
        UpdateDisplayRefreshState();
        QueuePreferenceSave();
    }

    private async void OnCustomizeDashboard(object sender, RoutedEventArgs args)
    {
        if (dashboardCustomizerDialog is not null)
        {
            dashboardCustomizerRefresh?.Invoke();
            await dashboardCustomizerDialog.ShowAsync();
            ApplyDashboardCustomizerChanges();
            return;
        }

        var editorCards = new ObservableCollection<DashboardCardOption>();
        void LoadEditorFromDashboard()
        {
            editorCards.Clear();
            foreach (DashboardCardOption option in DashboardCards)
            {
                editorCards.Add(new DashboardCardOption(
                    option.Kind,
                    option.Label,
                    option.Description,
                    option.IsPinned));
            }

        }

        void ApplyEditorToDashboard()
        {
            changingDashboardCards = true;
            try
            {
                ApplyCustomizedOrder(
                    DashboardCards,
                    editorCards,
                    option => option.Kind,
                    (current, edited) => current.IsPinned = edited.IsPinned);
            }
            finally
            {
                changingDashboardCards = false;
            }

            UpdateDisplayRefreshState();
            QueuePreferenceSave();
            RefreshDashboardCards();
        }

        LoadEditorFromDashboard();
        dashboardCustomizerRefresh = LoadEditorFromDashboard;
        dashboardCustomizerApply = ApplyEditorToDashboard;

        ContentDialog dialog = CreateCustomizationDialog(
            "Customize Dashboard",
            "Dashboard cards",
            "Check cards to show. Drag them into the order you want.",
            editorCards,
            "DashboardCardOptionTemplate");
        dashboardCustomizerDialog = dialog;
        await dialog.ShowAsync();
        ApplyDashboardCustomizerChanges();
    }

    private void ApplyDashboardCustomizerChanges() =>
        dashboardCustomizerApply?.Invoke();

    private void ApplyPageLayoutPreferences(
        IReadOnlyList<PageLayoutPreference> preferences)
    {
        changingPageSections = true;
        try
        {
            foreach (ObservableCollection<PageSectionOption> sections in pageSections.Values)
            {
                sections.CollectionChanged -= OnPageSectionsCollectionChanged;
                foreach (PageSectionOption section in sections)
                {
                    section.PropertyChanged -= OnPageSectionOptionChanged;
                }
            }

            pageSections.Clear();
            foreach (PageLayoutPreference layout in
                     PageLayoutPreferenceDefaults.Normalize(preferences))
            {
                var sections = new ObservableCollection<PageSectionOption>(
                    PageLayoutPreferenceDefaults.CreateOptions(
                        layout.Page,
                        layout.Sections));
                sections.CollectionChanged += OnPageSectionsCollectionChanged;
                foreach (PageSectionOption section in sections)
                {
                    section.PropertyChanged += OnPageSectionOptionChanged;
                }

                pageSections.Add(layout.Page, sections);
            }
        }
        finally
        {
            changingPageSections = false;
        }

        RefreshAllPageSections();
    }

    private void OnPageSectionOptionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (changingPageSections
            || args.PropertyName != nameof(PageSectionOption.IsVisible)
            || sender is not PageSectionOption option)
        {
            return;
        }

        CustomizablePageKind page = pageSections
            .First(pair => pair.Value.Contains(option))
            .Key;
        RefreshPageSections(page);
        QueuePreferenceSave();
    }

    private void OnPageSectionsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (changingPageSections
            || sender is not ObservableCollection<PageSectionOption> sections)
        {
            return;
        }

        CustomizablePageKind page = pageSections
            .First(pair => ReferenceEquals(pair.Value, sections))
            .Key;
        RefreshPageSections(page);
        QueuePreferenceSave();
    }

    private async void OnCustomizePageSections(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element
            || element.Tag is not string pageName
            || !Enum.TryParse(pageName, ignoreCase: false, out CustomizablePageKind page)
            || (page == CustomizablePageKind.Lighting
                && !TryRegisterLightingPageSections())
            || !pageSections.TryGetValue(
                page,
                out ObservableCollection<PageSectionOption>? currentSections))
        {
            return;
        }

        var editorSections = new ObservableCollection<PageSectionOption>(
            currentSections.Select(section => new PageSectionOption(
                section.Kind,
                section.Label,
                section.Description,
                section.IsVisible)));
        ContentDialog dialog = CreateCustomizationDialog(
            $"Customize {GetCustomizablePageLabel(page)}",
            $"{page} page sections",
            "Check sections to show. Drag them into the order you want.",
            editorSections,
            "PageSectionOptionTemplate");
        await dialog.ShowAsync();

        changingPageSections = true;
        try
        {
            ApplyCustomizedOrder(
                currentSections,
                editorSections,
                section => section.Kind,
                (current, edited) => current.IsVisible = edited.IsVisible);
        }
        finally
        {
            changingPageSections = false;
        }

        RefreshPageSections(page);
        QueuePreferenceSave();
    }

    private ContentDialog CreateCustomizationDialog<T>(
        string title,
        string automationName,
        string instructions,
        ObservableCollection<T> items,
        string itemTemplateKey)
    {
        var reorderList = new Microsoft.UI.Xaml.Controls.ListView
        {
            ItemsSource = items,
            SelectionMode = ListViewSelectionMode.None,
            CanDragItems = true,
            CanReorderItems = true,
            ReorderMode = ListViewReorderMode.Enabled,
            AllowDrop = true,
            MaxHeight = 480,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            ItemTemplate = (DataTemplate)Root.Resources[itemTemplateKey],
            ItemContainerStyle = (Style)Root.Resources["DashboardReorderListItemStyle"],
        };
        AutomationProperties.SetName(reorderList, automationName);

        var content = new StackPanel { Width = 600, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = instructions,
            Style = (Style)Root.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(reorderList);

        return new ContentDialog
        {
            MinWidth = 640,
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };
    }

    private static void ApplyCustomizedOrder<TOption, TKey>(
        ObservableCollection<TOption> currentItems,
        IReadOnlyList<TOption> editedItems,
        Func<TOption, TKey> keySelector,
        Action<TOption, TOption> applyState)
        where TKey : notnull
    {
        for (int targetIndex = 0; targetIndex < editedItems.Count; targetIndex++)
        {
            TOption edited = editedItems[targetIndex];
            TKey key = keySelector(edited);
            int currentIndex = currentItems
                .Select((item, index) => (item, index))
                .First(pair => EqualityComparer<TKey>.Default.Equals(
                    keySelector(pair.item),
                    key))
                .index;
            if (currentIndex != targetIndex)
            {
                currentItems.Move(currentIndex, targetIndex);
            }

            applyState(currentItems[targetIndex], edited);
        }
    }

    private static string GetCustomizablePageLabel(CustomizablePageKind page) =>
        page switch
        {
            CustomizablePageKind.Profiles => "Profiles",
            CustomizablePageKind.Display => "Display",
            CustomizablePageKind.Laptop => "Laptop",
            CustomizablePageKind.Lighting => "Lighting",
            CustomizablePageKind.Settings => "Settings",
            CustomizablePageKind.About => "About",
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
        };

    private void RefreshAllPageSections()
    {
        foreach (CustomizablePageKind page in pageSections.Keys)
        {
            RefreshPageSections(page);
        }
    }

    private void RefreshPageSections(CustomizablePageKind page)
    {
        if (!pageSectionHosts.TryGetValue(
                page,
                out Microsoft.UI.Xaml.Controls.Panel? host)
            || !pageSections.TryGetValue(
                page,
                out ObservableCollection<PageSectionOption>? sections))
        {
            return;
        }

        FrameworkElement[] desiredSections = sections
            .Where(section => section.IsVisible)
            .Select(section => pageSectionPresentations[section.Kind])
            .ToArray();
        var desiredSet = desiredSections.ToHashSet();

        foreach (FrameworkElement child in host.Children
                     .OfType<FrameworkElement>()
                     .Where(child => !desiredSet.Contains(child))
                     .ToArray())
        {
            host.Children.Remove(child);
        }

        foreach (FrameworkElement section in desiredSections)
        {
            if (section.Parent is Microsoft.UI.Xaml.Controls.Panel currentParent
                && !ReferenceEquals(currentParent, host))
            {
                currentParent.Children.Remove(section);
            }

            if (!host.Children.Contains(section))
            {
                host.Children.Add(section);
            }
        }

        for (int targetIndex = 0; targetIndex < desiredSections.Length; targetIndex++)
        {
            FrameworkElement section = desiredSections[targetIndex];
            int currentIndex = host.Children.IndexOf(section);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                host.Children.Move(
                    checked((uint)currentIndex),
                    checked((uint)targetIndex));
            }
        }
    }

    private void ApplyPrimaryNavigationPreferences(
        IReadOnlyList<PrimaryNavigationItemKind> preferences)
    {
        changingPrimaryNavigationItems = true;
        try
        {
            PrimaryNavigationItems.Clear();
            foreach (PrimaryNavigationItemKind kind in
                     PrimaryNavigationPreferenceDefaults.Normalize(preferences))
            {
                PrimaryNavigationItems.Add(new PrimaryNavigationItemOption(
                    kind,
                    GetPrimaryNavigationLabel(kind)));
            }
        }
        finally
        {
            changingPrimaryNavigationItems = false;
        }

        ApplyPrimaryNavigationOrder();
    }

    private void ApplyPrimaryNavigationOrder()
    {
        NavigationViewItem? selectedItem = MainNavigation.SelectedItem as NavigationViewItem;
        string destination = selectedItem?.Tag as string ?? "home";
        changingPrimaryNavigationOrder = true;
        try
        {
            for (int targetIndex = 0; targetIndex < PrimaryNavigationItems.Count; targetIndex++)
            {
                NavigationViewItem item = GetPrimaryNavigationItem(
                    PrimaryNavigationItems[targetIndex].Kind);
                int menuTargetIndex = targetIndex + 1;
                int currentIndex = MainNavigation.MenuItems.IndexOf(item);
                if (currentIndex < 0 || currentIndex == menuTargetIndex)
                {
                    continue;
                }

                MainNavigation.MenuItems.RemoveAt(currentIndex);
                MainNavigation.MenuItems.Insert(menuTargetIndex, item);
            }

            if (selectedItem is not null)
            {
                MainNavigation.SelectedItem = selectedItem;
            }
        }
        finally
        {
            changingPrimaryNavigationOrder = false;
        }

        ApplyNavigationDestination(destination);
    }

    private NavigationViewItem GetPrimaryNavigationItem(PrimaryNavigationItemKind kind) =>
        kind switch
        {
            PrimaryNavigationItemKind.Profiles => ProfilesNavigationItem,
            PrimaryNavigationItemKind.Laptop => LaptopNavigationItem,
            PrimaryNavigationItemKind.Lighting => LightingNavigationItem,
            PrimaryNavigationItemKind.Display => DisplayNavigationItem,
            PrimaryNavigationItemKind.Settings => SettingsNavigationItem,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string GetPrimaryNavigationLabel(PrimaryNavigationItemKind kind) =>
        kind switch
        {
            PrimaryNavigationItemKind.Profiles => "Profiles",
            PrimaryNavigationItemKind.Laptop => "Laptop",
            PrimaryNavigationItemKind.Lighting => "Lighting",
            PrimaryNavigationItemKind.Display => "Display",
            PrimaryNavigationItemKind.Settings => "Settings",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private void OnPrimaryNavigationItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (changingPrimaryNavigationItems)
        {
            return;
        }

        ApplyPrimaryNavigationOrder();
        QueuePreferenceSave();
    }

    private void OnResetNavigationOrder(object sender, RoutedEventArgs args)
    {
        ApplyPrimaryNavigationPreferences(PrimaryNavigationPreferenceDefaults.Items);
        QueuePreferenceSave();
    }

    private async void OnChooseCustomAccentColor(object sender, RoutedEventArgs args)
    {
        var picker = new ColorPicker
        {
            Color = AccentColorManager.ParseHexColor(customAccentColor),
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Choose an accent color",
            Content = picker,
            PrimaryButtonText = "Use color",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        accentColorMode = AppAccentColorMode.Custom;
        customAccentColor = AccentColorManager.ToHexColor(picker.Color);
        OnPropertyChanged(nameof(SelectedAccentColorOption));
        ApplyAccentColorAndQueueSave();
    }

    private void RefreshDashboardCards()
    {
        if (!dashboardCardsReady)
        {
            return;
        }

        FrameworkElement[] desiredCards = DashboardCards
            .Where(option => option.IsPinned)
            .Select(GetOrCreateDashboardCard)
            .ToArray();
        var desiredSet = desiredCards.ToHashSet();

        foreach (FrameworkElement child in DashboardCardHost.Children
                     .OfType<FrameworkElement>()
                     .Where(child => !desiredSet.Contains(child))
                     .ToArray())
        {
            DashboardCardHost.Children.Remove(child);
        }

        foreach (FrameworkElement card in desiredCards)
        {
            if (!DashboardCardHost.Children.Contains(card))
            {
                DashboardCardHost.Children.Add(card);
            }
        }

        for (int targetIndex = 0; targetIndex < desiredCards.Length; targetIndex++)
        {
            FrameworkElement card = desiredCards[targetIndex];
            int currentIndex = DashboardCardHost.Children.IndexOf(card);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                DashboardCardHost.Children.Move(
                    checked((uint)currentIndex),
                    checked((uint)targetIndex));
            }
        }

        DashboardCardHost.RowDefinitions.Clear();
        for (int row = 0; row < desiredCards.Length; row++)
        {
            DashboardCardHost.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto,
            });
            Grid.SetRow(desiredCards[row], row);
        }

        DashboardEmptyState.Visibility = DashboardCards.Any(option => option.IsPinned)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private FrameworkElement GetOrCreateDashboardCard(DashboardCardOption option)
    {
        if (dashboardCardPresentations.TryGetValue(option.Kind, out FrameworkElement? card))
        {
            return card;
        }

        card = option.Kind == DashboardCardKind.DeviceStatus
            ? DeviceStatusCard
            : CreateDashboardLiveCard(option);
        dashboardCardPresentations.Add(option.Kind, card);
        return card;
    }

    private FrameworkElement CreateDashboardLiveCard(DashboardCardOption option)
    {
        string templateKey = option.Kind switch
        {
            DashboardCardKind.PowerMode => "DashboardPowerModeCardTemplate",
            DashboardCardKind.Fans => "DashboardFansCardTemplate",
            DashboardCardKind.Battery => "DashboardBatteryCardTemplate",
            DashboardCardKind.KeyboardBehavior => "DashboardKeyboardBehaviorCardTemplate",
            DashboardCardKind.StartupAnimation => "DashboardStartupAnimationCardTemplate",
            DashboardCardKind.Lighting => "DashboardLightingCardTemplate",
            DashboardCardKind.Display => "DashboardDisplayCardTemplate",
            _ => throw new ArgumentOutOfRangeException(
                nameof(option),
                option.Kind,
                "The dashboard card does not have a live template."),
        };

        object content = option.Kind == DashboardCardKind.Display
            ? DisplayViewModel
            : ViewModel;

        return new ContentControl
        {
            Content = content,
            ContentTemplate = (DataTemplate)Root.Resources[templateKey],
            HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        };
    }

    private void MoveCanonicalLaptopCardsIntoPlace()
    {
        if (canonicalLaptopCardsPlaced)
        {
            return;
        }

        foreach (FrameworkElement card in new[] { PowerModeCard, FansCard, BatteryCard })
        {
            if (card.Parent is Microsoft.UI.Xaml.Controls.Panel current)
            {
                current.Children.Remove(card);
            }
        }

        LaptopCardsHost.Children.Insert(0, BatteryCard);
        LaptopCardsHost.Children.Insert(0, FansCard);
        LaptopCardsHost.Children.Insert(0, PowerModeCard);
        canonicalLaptopCardsPlaced = true;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        MoveCanonicalLaptopCardsIntoPlace();
        RegisterPageSections();
        dashboardCardPresentations[DashboardCardKind.DeviceStatus] = DeviceStatusCard;
        dashboardCardsReady = true;
        RefreshDashboardCards();
        RefreshAllPageSections();
        UpdateDisplayRefreshState();
    }

    private void RegisterPageSections()
    {
        pageSectionHosts[CustomizablePageKind.Profiles] = ProfilesSectionHost;
        pageSectionHosts[CustomizablePageKind.Display] = DisplayCardsHost;
        pageSectionHosts[CustomizablePageKind.Laptop] = LaptopCardsHost;
        pageSectionHosts[CustomizablePageKind.Settings] = SettingsSectionHost;
        pageSectionHosts[CustomizablePageKind.About] = AboutSectionHost;
        foreach (Microsoft.UI.Xaml.Controls.Panel host in pageSectionHosts.Values)
        {
            ConfigureSectionHostTransitions(host);
        }

        pageSectionPresentations[PageSectionKind.ProfilesComparison] =
            ProfilesComparisonSection;
        pageSectionPresentations[PageSectionKind.DisplayControls] = DisplayCard;
        pageSectionPresentations[PageSectionKind.LaptopPowerMode] = PowerModeCard;
        pageSectionPresentations[PageSectionKind.LaptopFans] = FansCard;
        pageSectionPresentations[PageSectionKind.LaptopBattery] = BatteryCard;
        pageSectionPresentations[PageSectionKind.LaptopKeyboardBehavior] =
            KeyboardBehaviorCard;
        pageSectionPresentations[PageSectionKind.LaptopStartupAnimation] =
            StartupAnimationCard;
        pageSectionPresentations[PageSectionKind.LaptopShortcutKeys] =
            ShortcutKeysSection;
        pageSectionPresentations[PageSectionKind.SettingsBehavior] =
            SettingsBehaviorSection;
        pageSectionPresentations[PageSectionKind.SettingsAppearance] =
            SettingsAppearanceSection;
        pageSectionPresentations[PageSectionKind.SettingsShutdown] =
            SettingsShutdownSection;
        pageSectionPresentations[PageSectionKind.AboutOverview] =
            AboutOverviewSection;
        pageSectionPresentations[PageSectionKind.AboutVersions] =
            AboutVersionsSection;
        pageSectionPresentations[PageSectionKind.AboutDeviceInformation] =
            AboutDeviceInformationSection;
        pageSectionPresentations[PageSectionKind.AboutSupport] =
            AboutSupportSection;
        pageSectionPresentations[PageSectionKind.AboutTroubleshooting] =
            AboutTroubleshootingSection;
        TryRegisterLightingPageSections();
    }

    private bool TryRegisterLightingPageSections()
    {
        if (pageSectionHosts.ContainsKey(CustomizablePageKind.Lighting))
        {
            return true;
        }

        LightingCard.ApplyTemplate();
        StackPanel? host = FindDescendant<StackPanel>(
            LightingCard,
            "LightingSectionHost");
        StackPanel? keyboard = FindDescendant<StackPanel>(
            LightingCard,
            "LightingKeyboardSection");
        StackPanel? logo = FindDescendant<StackPanel>(
            LightingCard,
            "LightingLogoSection");
        StackPanel? powerSaving = FindDescendant<StackPanel>(
            LightingCard,
            "LightingPowerSavingSection");
        if (host is null || keyboard is null || logo is null || powerSaving is null)
        {
            return false;
        }

        pageSectionHosts[CustomizablePageKind.Lighting] = host;
        ConfigureSectionHostTransitions(host);
        pageSectionPresentations[PageSectionKind.LightingKeyboard] = keyboard;
        pageSectionPresentations[PageSectionKind.LightingLogo] = logo;
        pageSectionPresentations[PageSectionKind.LightingPowerSaving] = powerSaving;
        RefreshPageSections(CustomizablePageKind.Lighting);
        return true;
    }

    private static void ConfigureSectionHostTransitions(
        Microsoft.UI.Xaml.Controls.Panel host)
    {
        host.ChildrenTransitions =
        [
            new AddDeleteThemeTransition(),
            new RepositionThemeTransition
            {
                IsStaggeringEnabled = false,
            },
        ];
    }

    private static TElement? FindDescendant<TElement>(
        DependencyObject root,
        string name)
        where TElement : FrameworkElement
    {
        int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child =
                Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is TElement element
                && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            TElement? descendant = FindDescendant<TElement>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

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
