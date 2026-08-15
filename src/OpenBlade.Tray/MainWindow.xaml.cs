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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.Storage.Pickers;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Session;
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
    private const string AppUpdateNotificationTag = "app-update";
    private const string FirmwareUpdateNotificationTag = "firmware-update";
    private const string ProfileNotificationTag = "profile-power-source";
    private const string ControllerAdvisoryNotificationTag = "controller-advisory";
    private const string OperationFailureNotificationTag = "operation-failure";
    private static readonly TimeSpan ChargeLimitCommitDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LightingCommitDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GpuClockOffsetCommitDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GpuAppObservationInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue dispatcherQueue;
    private readonly SemaphoreSlim contentDialogGate = new(1, 1);
    private readonly MicaBackdropHost micaBackdropHost;
    private readonly AccentColorManager accentColorManager;
    private readonly UiPreferenceStore preferenceStore;
    private readonly UiPreferenceSaveQueue preferenceSaveQueue;
    private readonly VisibleObservationLoop visibleObservationLoop;
    private readonly VisibleObservationLoop trayObservationLoop;
    private readonly VisibleObservationLoop gpuAppObservationLoop;
    private readonly WindowsProcessorEfficiencyPolicy processorPolicy;
    private readonly Func<TrayNotification, bool> showNotification;
    private readonly SessionDisplaySettingsAdapter displaySettingsAdapter;
    private readonly SessionGpuAppAdapter gpuAppAdapter;
    private readonly DispatcherQueueTimer chargeLimitCommitTimer;
    private readonly DispatcherQueueTimer lightingCommitTimer;
    private readonly DispatcherQueueTimer gpuClockOffsetCommitTimer;
    private readonly DispatcherQueueTimer displayRefreshTimer;
    private readonly CoalescingCommitState<ChargeLimitCommitRequest> chargeLimitCommits = new();
    private readonly CoalescingCommitState<LightingCommitRequest> lightingCommits = new();
    private readonly CoalescingCommitState<GpuClockOffsetSettings> gpuClockOffsetCommits = new();
    private readonly object initializationSync = new();
    private Task? initializationTask;
    private bool initialPlacementAttempted;
    private bool changingPerformanceSelection;
    private bool changingWindowsPowerModeSelection;
    private bool changingCustomPerformanceSelection;
    private bool changingGpuClockTuning;
    private bool changingFanSelection;
    private bool changingBatteryProtection;
    private bool changingGamingMode;
    private bool changingGamingModeKeyPolicy;
    private bool changingStartupAnimation;
    private bool changingExternalLightingPolicy;
    private bool changingFirmwareCompatibilityOverride;
    private bool changingWindowsDynamicLighting;
    private bool changingDisplaySettings;
    private bool changingDashboardCards;
    private bool trayDashboardCustomizationActive;
    private bool changingPageSections;
    private bool changingPrimaryNavigationItems;
    private bool changingPrimaryNavigationOrder;
    private bool changingSpecialKeyBindings;
    private bool changingAccentColor;
    private bool changingNotificationPreferences;
    private bool notificationObservationReady;
    private bool troubleshootingHeaderPointerPressed;
    private bool dashboardCardsReady;
    private bool canonicalLaptopCardsPlaced;
    private bool fanControlCommitInProgress;
    private bool chargeLimitPointerInteraction;
    private bool? dashboardIsStacked;
    private bool? profilesUseCompactLayout;
    private bool enforcingMinimumWindowSize;
    private bool windowVisible;
    private bool trayDashboardVisible;
    private int trayDashboardLiveReadingSensors =
        (int)TrayDashboardPreferenceDefaults.ResolveLiveReadingSensors(
            TrayDashboardPreferenceDefaults.Preference);
    private int openDisplayPickerCount;
    private int updateCheckingStarted;
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
    private NotificationPreferences notificationPreferences = NotificationPreferences.Default;
    private TrayDashboardPreference trayDashboardPreference =
        TrayDashboardPreferenceDefaults.Preference;
    private IReadOnlyList<TrayDashboardControlOption> trayDashboardAllControlOptions = [];
    private IReadOnlyList<TrayDashboardSliderOption> trayDashboardAllSliderOptions = [];
    private bool changingTrayDashboardEditor;
    private string? lastNotifiedReleaseTag;
    private string? lastNotifiedFirmwareSignature;
    private PowerSource lastObservedPowerSource = PowerSource.ExternalUnknown;
    private string lastObservedControllerAdvisory = string.Empty;
    private string lastObservedOperationFailure = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
        : this(
            WindowsProcessorEfficiencyPolicy.Discover(),
            new WindowsStartupRegistration(),
            showNotification: null)
    {
    }

    internal MainWindow(
        WindowsProcessorEfficiencyPolicy processorPolicy,
        IStartupRegistration startupRegistration,
        Func<TrayNotification, bool>? showNotification = null)
    {
        this.processorPolicy = processorPolicy
            ?? throw new ArgumentNullException(nameof(processorPolicy));
        ArgumentNullException.ThrowIfNull(startupRegistration);
        this.showNotification = showNotification ?? (_ => false);
        dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray must start on a dispatcher thread.");
        dispatcherQueue.EnsureSystemDispatcherQueue();
        IServiceClient serviceClient = new ServiceClient();
        string uiVersion = OpenBladeProductVersion.Read(typeof(MainWindow).Assembly);
        ViewModel = new MainViewModel(
            serviceClient,
            new WinUiDispatcher(dispatcherQueue),
            new DiagnosticArchiveSaver(new WinUiDiagnosticArchiveDestination(AppWindow.Id)),
            startupRegistration,
            uiVersion: uiVersion,
            sessionAgentVersion: InstalledComponentVersion.ReadSibling(
                "OpenBlade.SessionAgent.exe"),
            installedServiceVersion: InstalledComponentVersion.ReadSibling(
                "OpenBlade.Service.exe"));
        UpdateViewModel = new UpdateCheckViewModel(
            new GitHubReleaseUpdateClient(),
            uiVersion);
        UpdateViewModel.PreferencesChanged += OnUpdatePreferencesChanged;
        UpdateViewModel.UpdateAvailable += OnAppUpdateAvailable;
        FirmwareUpdateViewModel = new FirmwareUpdateAssistantViewModel(
            new RazerFirmwareUpdateClient());
        FirmwareUpdateViewModel.PreferencesChanged +=
            OnFirmwareUpdatePreferencesChanged;
        FirmwareUpdateViewModel.UpdateAvailable += OnFirmwareUpdateAvailable;
        displaySettingsAdapter = new SessionDisplaySettingsAdapter();
        gpuAppAdapter = new SessionGpuAppAdapter();
        DisplayViewModel = new DisplaySettingsViewModel(displaySettingsAdapter);
        DisplayViewModel.UserSettingsChanged += OnDisplayUserSettingsChanged;
        ViewModel.SavedDisplayProfileRestoreRequested += OnSavedDisplayProfileRestoreRequested;
        visibleObservationLoop = new VisibleObservationLoop(ObserveVisibleSurfacesAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade visible observation failed: {0}",
                exception),
            observationIntervalProvider: () =>
                ViewModel.LiveReadingsObservationInterval);
        trayObservationLoop = new VisibleObservationLoop(ViewModel.ObserveTrayStateAsync,
            errorHandler: exception => Trace.TraceError(
                "OpenBlade on-demand tray telemetry refresh failed: {0}",
                exception),
            observationIntervalProvider: () => Timeout.InfiniteTimeSpan);
        gpuAppObservationLoop = new VisibleObservationLoop(
            ObserveGpuAppActivityAsync,
            errorHandler: exception => Trace.TraceWarning(
                "OpenBlade GPU app visibility refresh failed: {0}",
                exception),
            observationIntervalProvider: () => GpuAppObservationInterval);
        displayRefreshTimer = dispatcherQueue.CreateTimer();
        displayRefreshTimer.Interval = DisplayRefreshInterval;
        displayRefreshTimer.IsRepeating = true;
        displayRefreshTimer.Tick += OnDisplayRefreshTimerTick;
        InitializeComponent();
        DashboardCards.CollectionChanged += OnDashboardCardsCollectionChanged;
        TrayDashboardEditorControls.CollectionChanged +=
            OnTrayDashboardEditorOrderChanged;
        TrayDashboardEditorSliders.CollectionChanged +=
            OnTrayDashboardEditorOrderChanged;
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
        gpuClockOffsetCommitTimer = dispatcherQueue.CreateTimer();
        gpuClockOffsetCommitTimer.Interval = GpuClockOffsetCommitDelay;
        gpuClockOffsetCommitTimer.IsRepeating = false;
        gpuClockOffsetCommitTimer.Tick += OnGpuClockOffsetCommitTimerTick;
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

    public UpdateCheckViewModel UpdateViewModel { get; }

    public FirmwareUpdateAssistantViewModel FirmwareUpdateViewModel { get; }

    public bool StartMinimized { get; private set; }

    public bool PerformanceNotificationsEnabled
    {
        get => notificationPreferences.PerformanceChangesEnabled;
        set => SetNotificationPreference(NotificationCategory.PerformanceChanges, value);
    }

    public bool ProfileAndPowerSourceNotificationsEnabled
    {
        get => notificationPreferences.ProfileAndPowerSourceChangesEnabled;
        set => SetNotificationPreference(
            NotificationCategory.ProfileAndPowerSourceChanges,
            value);
    }

    public bool AppUpdateNotificationsEnabled
    {
        get => notificationPreferences.AppUpdatesEnabled;
        set => SetNotificationPreference(NotificationCategory.AppUpdates, value);
    }

    public bool FirmwareUpdateNotificationsEnabled
    {
        get => notificationPreferences.FirmwareUpdatesEnabled;
        set => SetNotificationPreference(NotificationCategory.FirmwareUpdates, value);
    }

    public bool ControllerAdvisoryNotificationsEnabled
    {
        get => notificationPreferences.ControllerAdvisoriesEnabled;
        set => SetNotificationPreference(NotificationCategory.ControllerAdvisories, value);
    }

    public bool OperationFailureNotificationsEnabled
    {
        get => notificationPreferences.OperationFailuresEnabled;
        set => SetNotificationPreference(NotificationCategory.OperationFailures, value);
    }

    public bool IsNotificationEnabled(NotificationCategory category) =>
        notificationPreferences.IsEnabled(category);

    public ObservableCollection<DashboardCardOption> DashboardCards { get; } = [];

    public TrayDashboardPreference TrayDashboardPreference => trayDashboardPreference;

    public ObservableCollection<TrayDashboardControlOption> TrayDashboardEditorControls
    { get; } = [];

    public ObservableCollection<TrayDashboardSliderOption> TrayDashboardEditorSliders
    { get; } = [];

    public ObservableCollection<TrayDashboardStatusItemOption> TrayDashboardEditorStatusItems
    { get; } = [];

    public event Action<TrayDashboardPreference>? TrayDashboardPreferenceChanged;

    public event Action<bool>? TrayDashboardEditSessionChanged;

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

    public bool IsWindowVisible => windowVisible;

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
        Volatile.Write(ref windowVisible, true);
        ApplyProcessorVisibilityPolicy();
        UpdateDashboardTelemetryPresentationState();
        trayObservationLoop.Stop();
        UpdateActiveObservationLoop();
        UpdateGpuAppObservationState();
        UpdateDisplayRefreshState();
        AppWindow.Show(true);
    }

    public void StartHiddenObservation()
    {
        Volatile.Write(ref windowVisible, false);
        ApplyProcessorVisibilityPolicy();
        UpdateDashboardTelemetryPresentationState();
        UpdateActiveObservationLoop();
        trayObservationLoop.Stop();
        gpuAppObservationLoop.Stop();
        displayRefreshTimer.Stop();
    }

    public void NavigateToNotificationDestination(
        TrayNotificationDestination destination)
    {
        FrameworkElement? target = null;
        switch (destination)
        {
            case TrayNotificationDestination.Laptop:
                MainNavigation.SelectedItem = LaptopNavigationItem;
                break;
            case TrayNotificationDestination.Updates:
                MainNavigation.SelectedItem = SettingsNavigationItem;
                target = SettingsUpdatesSection;
                break;
            case TrayNotificationDestination.Firmware:
                MainNavigation.SelectedItem = AboutNavigationItem;
                target = OfficialFirmwareUpdatesCard;
                break;
            case TrayNotificationDestination.Troubleshooting:
                MainNavigation.SelectedItem = AboutNavigationItem;
                TroubleshootingExpander.IsExpanded = true;
                target = TroubleshootingExpander;
                break;
            default:
                MainNavigation.SelectedItem = HomeNavigationItem;
                break;
        }

        if (target is not null)
        {
            dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => target.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0,
                }));
        }
    }

    public async Task StartUpdateCheckingAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref updateCheckingStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await ShowUpdateCheckConsentIfNeededAsync();
            UpdateViewModel.StartAutomaticCheck();
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref updateCheckingStarted, 0);
            Trace.TraceError(
                "OpenBlade update-check initialization failed: {0}",
                exception);
        }
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
        UpdateViewModel.PreferencesChanged -= OnUpdatePreferencesChanged;
        UpdateViewModel.UpdateAvailable -= OnAppUpdateAvailable;
        FirmwareUpdateViewModel.PreferencesChanged -=
            OnFirmwareUpdatePreferencesChanged;
        FirmwareUpdateViewModel.UpdateAvailable -= OnFirmwareUpdateAvailable;
        DashboardCards.CollectionChanged -= OnDashboardCardsCollectionChanged;
        TrayDashboardEditorControls.CollectionChanged -=
            OnTrayDashboardEditorOrderChanged;
        TrayDashboardEditorSliders.CollectionChanged -=
            OnTrayDashboardEditorOrderChanged;
        foreach (TrayDashboardStatusItemOption option in TrayDashboardEditorStatusItems)
        {
            option.PropertyChanged -= OnTrayDashboardEditorStatusChanged;
        }
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
        chargeLimitCommits.Dispose();
        lightingCommitTimer.Stop();
        lightingCommitTimer.Tick -= OnLightingCommitTimerTick;
        lightingCommits.Dispose();
        gpuClockOffsetCommitTimer.Stop();
        gpuClockOffsetCommitTimer.Tick -= OnGpuClockOffsetCommitTimerTick;
        gpuClockOffsetCommits.Dispose();
        displayRefreshTimer.Stop();
        displayRefreshTimer.Tick -= OnDisplayRefreshTimerTick;
        micaBackdropHost.Dispose();
        accentColorManager.ColorChanged -= OnAccentColorChanged;
        accentColorManager.Dispose();
        await DisposeComponentSafelyAsync(
            "visible observation loop",
            visibleObservationLoop.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "on-demand tray observation loop",
            trayObservationLoop.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "GPU app observation loop",
            gpuAppObservationLoop.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "update checker",
            UpdateViewModel.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "firmware update assistant",
            FirmwareUpdateViewModel.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "preference save queue",
            preferenceSaveQueue.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "display view model",
            DisplayViewModel.DisposeAsync);
        await DisposeComponentSafelyAsync(
            "main view model",
            ViewModel.DisposeAsync);
    }

    private static async ValueTask DisposeComponentSafelyAsync(
        string component,
        Func<ValueTask> dispose)
    {
        try
        {
            await dispose();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade {0} disposal failed: {1}",
                component,
                exception);
        }
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
        TrayDashboardPreference initialTrayDashboard =
            TrayDashboardPreferenceDefaults.Preference;
        AppAccentColorMode initialAccentColorMode = AppAccentColorMode.RazerGreen;
        string initialCustomAccentColor = AppAccentColorPreferenceDefaults.RazerGreenHex;
        UpdateChannel initialUpdateChannel = UpdateChannel.Stable;
        UpdateCheckInterval initialUpdateCheckInterval = UpdateCheckInterval.Daily;
        bool initialUpdateCheckPromptShown = false;
        DateTimeOffset? initialLastSuccessfulUpdateCheckAt = null;
        DateTimeOffset? initialUpdateCheckRetryAfter = null;
        string? initialUpdateCheckEntityTag = null;
        string? initialLastKnownReleaseTag = null;
        string? initialLastKnownReleaseUrl = null;
        UpdateCheckInterval initialFirmwareUpdateCheckInterval =
            UpdateCheckInterval.ManualOnly;
        DateTimeOffset? initialLastSuccessfulFirmwareUpdateCheckAt = null;
        DateTimeOffset? initialFirmwareUpdateCheckRetryAfter = null;
        string? initialLastKnownFirmwareModelNumber = null;
        string? initialLastKnownPublishedBiosVersion = null;
        string? initialLastKnownPublishedEcVersion = null;
        string? initialLastKnownPublishedMcuVersion = null;
        RazerPublishedFirmwareVersionStatus? initialLastKnownPublishedBiosStatus = null;
        RazerPublishedFirmwareVersionStatus? initialLastKnownPublishedEcStatus = null;
        RazerPublishedFirmwareVersionStatus? initialLastKnownPublishedMcuStatus = null;
        NotificationPreferences initialNotifications = NotificationPreferences.Default;
        string? initialLastNotifiedReleaseTag = null;
        string? initialLastNotifiedFirmwareSignature = null;
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
            try
            {
                initialTrayDashboard = preferences.EffectiveTrayDashboard;
            }
            catch (InvalidDataException exception)
            {
                Trace.TraceWarning(
                    "OpenBlade tray dashboard preferences require a newer version; using dashboard defaults without overwriting the stored preference: {0}",
                    exception.Message);
            }
            initialAccentColorMode = preferences.EffectiveAccentColorMode;
            initialCustomAccentColor = preferences.EffectiveCustomAccentColor;
            StartMinimized = preferences.StartMinimized;
            initialUpdateChannel = preferences.EffectiveUpdateChannel;
            initialUpdateCheckInterval = preferences.EffectiveUpdateCheckInterval;
            initialUpdateCheckPromptShown = preferences.UpdateCheckPromptShown;
            initialLastSuccessfulUpdateCheckAt =
                preferences.LastSuccessfulUpdateCheckAt;
            initialUpdateCheckRetryAfter = preferences.UpdateCheckRetryAfter;
            initialUpdateCheckEntityTag = preferences.UpdateCheckEntityTag;
            initialLastKnownReleaseTag = preferences.LastKnownReleaseTag;
            initialLastKnownReleaseUrl = preferences.LastKnownReleaseUrl;
            initialFirmwareUpdateCheckInterval =
                preferences.EffectiveFirmwareUpdateCheckInterval;
            initialLastSuccessfulFirmwareUpdateCheckAt =
                preferences.LastSuccessfulFirmwareUpdateCheckAt;
            initialFirmwareUpdateCheckRetryAfter =
                preferences.FirmwareUpdateCheckRetryAfter;
            initialLastKnownFirmwareModelNumber =
                preferences.LastKnownFirmwareModelNumber;
            initialLastKnownPublishedBiosVersion =
                preferences.LastKnownPublishedBiosVersion;
            initialLastKnownPublishedEcVersion =
                preferences.LastKnownPublishedEcVersion;
            initialLastKnownPublishedMcuVersion =
                preferences.LastKnownPublishedMcuVersion;
            initialLastKnownPublishedBiosStatus =
                preferences.LastKnownPublishedBiosStatus;
            initialLastKnownPublishedEcStatus =
                preferences.LastKnownPublishedEcStatus;
            initialLastKnownPublishedMcuStatus =
                preferences.LastKnownPublishedMcuStatus;
            initialNotifications = preferences.EffectiveNotifications;
            initialLastNotifiedReleaseTag = preferences.LastNotifiedReleaseTag;
            initialLastNotifiedFirmwareSignature =
                preferences.LastNotifiedFirmwareSignature;
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
        ApplyTrayDashboardPreference(initialTrayDashboard, queueSave: false);
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
        ApplyNotificationPreferences(initialNotifications);
        lastNotifiedReleaseTag = initialLastNotifiedReleaseTag;
        lastNotifiedFirmwareSignature = initialLastNotifiedFirmwareSignature;
        UpdateViewModel.ApplyPreferences(
            initialUpdateChannel,
            initialUpdateCheckInterval,
            initialUpdateCheckPromptShown,
            initialLastSuccessfulUpdateCheckAt,
            initialUpdateCheckRetryAfter,
            initialUpdateCheckEntityTag,
            initialLastKnownReleaseTag,
            initialLastKnownReleaseUrl);
        FirmwareUpdateViewModel.ApplyPreferences(
            initialFirmwareUpdateCheckInterval,
            initialLastSuccessfulFirmwareUpdateCheckAt,
            initialFirmwareUpdateCheckRetryAfter,
            initialLastKnownFirmwareModelNumber,
            initialLastKnownPublishedBiosVersion,
            initialLastKnownPublishedEcVersion,
            initialLastKnownPublishedMcuVersion,
            initialLastKnownPublishedBiosStatus,
            initialLastKnownPublishedEcStatus,
            initialLastKnownPublishedMcuStatus);
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

        FirmwareUpdateViewModel.UpdateInstalledFirmware(ViewModel.DetectedFirmware);
        FirmwareUpdateViewModel.StartAutomaticCheck();

        PrimeNotificationObservation();

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

        if (args.PropertyName == nameof(MainViewModel.DetectedFirmware))
        {
            FirmwareUpdateViewModel.UpdateInstalledFirmware(ViewModel.DetectedFirmware);
            FirmwareUpdateViewModel.StartAutomaticCheck();
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

        if (args.PropertyName == nameof(MainViewModel.IsSpecialKeyBindingsEditable))
        {
            if (dispatcherQueue.HasThreadAccess)
            {
                RefreshSpecialKeyBindingAccess();
            }
            else
            {
                dispatcherQueue.TryEnqueue(RefreshSpecialKeyBindingAccess);
            }
        }

        if (args.PropertyName == nameof(MainViewModel.CanEditGpuClockTuning)
            && !ViewModel.CanEditGpuClockTuning)
        {
            gpuClockOffsetCommits.ClearPending();
            gpuClockOffsetCommitTimer.Stop();
        }

        if (args.PropertyName == nameof(MainViewModel.HasAttention)
            && !ViewModel.HasAttention)
        {
            if (dispatcherQueue.HasThreadAccess)
            {
                AttentionFlyout.Hide();
            }
            else
            {
                dispatcherQueue.TryEnqueue(AttentionFlyout.Hide);
            }
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

        ObserveNotificationState(args.PropertyName);
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
                    .ToArray(),
                UpdateChannel: UpdateViewModel.Channel,
                UpdateCheckInterval: UpdateViewModel.Interval,
                UpdateCheckPromptShown: !UpdateViewModel.NeedsConsentPrompt,
                LastSuccessfulUpdateCheckAt: UpdateViewModel.LastSuccessfulCheckAt,
                UpdateCheckRetryAfter: UpdateViewModel.RetryAfter,
                UpdateCheckEntityTag: UpdateViewModel.EntityTag,
                LastKnownReleaseTag: UpdateViewModel.CachedReleaseTag,
                LastKnownReleaseUrl: UpdateViewModel.CachedReleaseUrl,
                FirmwareUpdateCheckInterval: FirmwareUpdateViewModel.Interval,
                LastSuccessfulFirmwareUpdateCheckAt:
                    FirmwareUpdateViewModel.LastSuccessfulCheckAt,
                FirmwareUpdateCheckRetryAfter: FirmwareUpdateViewModel.RetryAfter,
                LastKnownFirmwareModelNumber: FirmwareUpdateViewModel.CachedModelNumber,
                LastKnownPublishedBiosVersion: FirmwareUpdateViewModel.CachedBiosVersion,
                LastKnownPublishedEcVersion: FirmwareUpdateViewModel.CachedEcVersion,
                LastKnownPublishedMcuVersion: FirmwareUpdateViewModel.CachedMcuVersion,
                LastKnownPublishedBiosStatus: FirmwareUpdateViewModel.CachedBiosStatus,
                LastKnownPublishedEcStatus: FirmwareUpdateViewModel.CachedEcStatus,
                LastKnownPublishedMcuStatus: FirmwareUpdateViewModel.CachedMcuStatus,
                Notifications: notificationPreferences,
                LastNotifiedReleaseTag: lastNotifiedReleaseTag,
                LastNotifiedFirmwareSignature: lastNotifiedFirmwareSignature,
                TrayDashboard: trayDashboardPreference));
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race; no preference work remains valid.
        }
        catch (InvalidOperationException exception)
        {
            Trace.TraceError(
                "OpenBlade preference save queue failed: {0}",
                exception);
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
            EndTrayDashboardCustomization(showSettingsOverview: true);
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

    private async void OnStartAtSignInToggled(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleStartAtSignInToggledAsync(sender),
            nameof(OnStartAtSignInToggled));

    private async Task HandleStartAtSignInToggledAsync(object sender)
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

    private void OnUpdatePreferencesChanged(object? sender, EventArgs args) =>
        QueuePreferenceSave();

    private void OnFirmwareUpdatePreferencesChanged(object? sender, EventArgs args) =>
        QueuePreferenceSave();

    private void OnAppUpdateAvailable(
        object? sender,
        AppUpdateAvailableEventArgs args)
    {
        if (!IsNotificationEnabled(NotificationCategory.AppUpdates)
            || string.Equals(
                lastNotifiedReleaseTag,
                args.ReleaseTag,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryShowNotification(new TrayNotification(
            "OpenBlade update available",
            $"OpenBlade {args.ReleaseTag.TrimStart('v', 'V')} is ready to download.",
            TrayNotificationSeverity.Information,
            ReplacementTag: AppUpdateNotificationTag,
            Destination: TrayNotificationDestination.Updates)))
        {
            return;
        }
        lastNotifiedReleaseTag = args.ReleaseTag;
        QueuePreferenceSave();
    }

    private void OnFirmwareUpdateAvailable(
        object? sender,
        FirmwareUpdateAvailableEventArgs args)
    {
        if (!IsNotificationEnabled(NotificationCategory.FirmwareUpdates)
            || string.Equals(
                lastNotifiedFirmwareSignature,
                args.Signature,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!TryShowNotification(new TrayNotification(
            "Razer firmware update available",
            args.Summary,
            TrayNotificationSeverity.Information,
            ReplacementTag: FirmwareUpdateNotificationTag,
            Destination: TrayNotificationDestination.Firmware)))
        {
            return;
        }
        lastNotifiedFirmwareSignature = args.Signature;
        QueuePreferenceSave();
    }

    private void ApplyNotificationPreferences(NotificationPreferences preferences)
    {
        changingNotificationPreferences = true;
        try
        {
            notificationPreferences = preferences;
            NotifyNotificationPreferencePropertiesChanged();
        }
        finally
        {
            changingNotificationPreferences = false;
        }
    }

    private void SetNotificationPreference(
        NotificationCategory category,
        bool enabled)
    {
        NotificationPreferences updated = notificationPreferences.WithEnabled(
            category,
            enabled);
        if (updated == notificationPreferences)
        {
            return;
        }

        notificationPreferences = updated;
        NotifyNotificationPreferencePropertiesChanged();
        if (!changingNotificationPreferences)
        {
            QueuePreferenceSave();
        }
    }

    private void NotifyNotificationPreferencePropertiesChanged()
    {
        OnPropertyChanged(nameof(PerformanceNotificationsEnabled));
        OnPropertyChanged(nameof(ProfileAndPowerSourceNotificationsEnabled));
        OnPropertyChanged(nameof(AppUpdateNotificationsEnabled));
        OnPropertyChanged(nameof(FirmwareUpdateNotificationsEnabled));
        OnPropertyChanged(nameof(ControllerAdvisoryNotificationsEnabled));
        OnPropertyChanged(nameof(OperationFailureNotificationsEnabled));
    }

    private void PrimeNotificationObservation()
    {
        lastObservedPowerSource = ViewModel.ActivePowerSource;
        lastObservedControllerAdvisory = ViewModel.HasControllerAdvisory
            ? ViewModel.ControllerAdvisoryText
            : string.Empty;
        lastObservedOperationFailure = ViewModel.TroubleshootingRecentErrorStatus;
        notificationObservationReady = true;
    }

    private void ObserveNotificationState(string? propertyName)
    {
        if (!notificationObservationReady)
        {
            return;
        }

        if (propertyName == nameof(MainViewModel.ActivePowerSource))
        {
            PowerSource current = ViewModel.ActivePowerSource;
            bool changed = current != lastObservedPowerSource;
            lastObservedPowerSource = current;
            if (changed
                && current != PowerSource.ExternalUnknown
                && IsNotificationEnabled(
                    NotificationCategory.ProfileAndPowerSourceChanges))
            {
                string profile = current switch
                {
                    PowerSource.PluggedIn => "AC profile",
                    PowerSource.UsbC => "USB-C profile",
                    PowerSource.Battery => "Battery profile",
                    _ => "active power profile",
                };
                TryShowNotification(new TrayNotification(
                    "Power profile changed",
                    $"OpenBlade is now using the {profile}.",
                    TrayNotificationSeverity.Information,
                    ReplacementTag: ProfileNotificationTag,
                    Destination: TrayNotificationDestination.Laptop));
            }
        }
        else if (propertyName == nameof(MainViewModel.ControllerAdvisoryText))
        {
            string current = ViewModel.HasControllerAdvisory
                ? ViewModel.ControllerAdvisoryText
                : string.Empty;
            bool changed = !string.Equals(
                current,
                lastObservedControllerAdvisory,
                StringComparison.Ordinal);
            lastObservedControllerAdvisory = current;
            if (changed
                && !string.IsNullOrWhiteSpace(current)
                && IsNotificationEnabled(NotificationCategory.ControllerAdvisories))
            {
                TryShowNotification(new TrayNotification(
                    "Razer controller detected",
                    current,
                    TrayNotificationSeverity.Information,
                    ReplacementTag: ControllerAdvisoryNotificationTag,
                    Destination: TrayNotificationDestination.Troubleshooting));
            }
        }
        else if (propertyName == nameof(MainViewModel.TroubleshootingRecentErrorStatus))
        {
            string current = ViewModel.TroubleshootingRecentErrorStatus;
            bool changed = !string.Equals(
                current,
                lastObservedOperationFailure,
                StringComparison.Ordinal);
            lastObservedOperationFailure = current;
            if (changed
                && !string.IsNullOrWhiteSpace(current)
                && IsNotificationEnabled(NotificationCategory.OperationFailures))
            {
                TryShowNotification(new TrayNotification(
                    "OpenBlade operation failed",
                    current,
                    TrayNotificationSeverity.Error,
                    ReplacementTag: OperationFailureNotificationTag,
                    Destination: TrayNotificationDestination.Troubleshooting));
            }
        }
    }

    private bool TryShowNotification(TrayNotification notification)
    {
        try
        {
            return showNotification(notification);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade could not show a notification: {0}",
                exception.Message);
            return false;
        }
    }

    private async Task ShowUpdateCheckConsentIfNeededAsync()
    {
        if (!UpdateViewModel.NeedsConsentPrompt || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Check for OpenBlade updates?",
            Content = "OpenBlade can check GitHub for a new release once per day when "
                + "the tray starts. The check sends no hardware state, profiles, "
                + "settings, or diagnostics. You can change this later in Settings.",
            PrimaryButtonText = "Use daily checks",
            CloseButtonText = "Manual only",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult? result = await ShowContentDialogAsync(dialog);
        if (result is null)
        {
            return;
        }

        UpdateViewModel.RecordConsent(result == ContentDialogResult.Primary
            ? UpdateCheckInterval.Daily
            : UpdateCheckInterval.ManualOnly);
    }

    private async void OnExternalLightingOverridePolicyToggled(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleExternalLightingOverridePolicyToggledAsync(sender),
            nameof(OnExternalLightingOverridePolicyToggled));

    private async Task HandleExternalLightingOverridePolicyToggledAsync(object sender)
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

    private async void OnFirmwareCompatibilityOverrideToggled(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleFirmwareCompatibilityOverrideToggledAsync(sender),
            nameof(OnFirmwareCompatibilityOverrideToggled));

    private async Task HandleFirmwareCompatibilityOverrideToggledAsync(object sender)
    {
        if (changingFirmwareCompatibilityOverride
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.AllowUntestedFirmware)
        {
            return;
        }

        changingFirmwareCompatibilityOverride = true;
        toggle.IsEnabled = false;
        try
        {
            await ViewModel.SetFirmwareCompatibilityOverrideAsync(
                toggle.IsOn,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.AllowUntestedFirmware;
            toggle.IsEnabled = true;
            changingFirmwareCompatibilityOverride = false;
        }
    }

    private async void OnReapplyChangedSettingsClick(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => ViewModel.ReapplyChangedSettingsAsync(CancellationToken.None),
            nameof(OnReapplyChangedSettingsClick));

    private async void OnCheckCurrentStateClick(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => ViewModel.CheckCurrentStateAsync(CancellationToken.None),
            nameof(OnCheckCurrentStateClick));

    private async void OnWindowsDynamicLightingToggled(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleWindowsDynamicLightingToggledAsync(sender),
            nameof(OnWindowsDynamicLightingToggled));

    private async Task HandleWindowsDynamicLightingToggledAsync(object sender)
    {
        if (changingWindowsDynamicLighting
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.UseWindowsDynamicLighting)
        {
            return;
        }

        changingWindowsDynamicLighting = true;
        toggle.IsEnabled = false;
        try
        {
            await ViewModel.SetKeyboardLightingControlModeAsync(
                toggle.IsOn
                    ? KeyboardLightingControlMode.WindowsDynamicLighting
                    : KeyboardLightingControlMode.OpenBlade,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.UseWindowsDynamicLighting;
            toggle.IsEnabled = true;
            changingWindowsDynamicLighting = false;
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

    private void OnWindowsPowerModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingWindowsPowerModeSelection
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not WindowsPowerModeOption selected
            || selected == ViewModel.SelectedWindowsPowerModeOption)
        {
            return;
        }

        changingWindowsPowerModeSelection = true;
        try
        {
            ViewModel.StageWindowsPowerMode(selected.Value);
            selector.SelectedItem = ViewModel.SelectedWindowsPowerModeOption;
        }
        finally
        {
            changingWindowsPowerModeSelection = false;
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

    private async void OnGpuClockTuningToggled(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleGpuClockTuningToggledAsync(sender),
            nameof(OnGpuClockTuningToggled));

    private async void OnCloseGpuAppsClick(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleCloseGpuAppsAsync(sender),
            nameof(OnCloseGpuAppsClick));

    private async Task HandleCloseGpuAppsAsync(object sender)
    {
        if (Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.Button button)
        {
            return;
        }

        button.SetValue(Microsoft.UI.Xaml.Controls.Control.IsEnabledProperty, false);
        try
        {
            SessionGpuResponse scan = await gpuAppAdapter.QueryAsync(CancellationToken.None);
            await ApplyGpuAppVisibilityAsync(scan, CancellationToken.None);

            if (scan.Status == SessionGpuOperationStatus.Failed)
            {
                await ShowGpuAppsMessageAsync(
                    "GPU activity unavailable",
                    scan.Error ?? "Windows GPU activity counters are unavailable.");
                return;
            }

            SessionGpuApp[] eligible = scan.Apps.Where(app => app.CanClose).ToArray();
            SessionGpuApp[] blocked = scan.Apps.Where(app => !app.CanClose).ToArray();
            if (eligible.Length == 0)
            {
                string message = blocked.Length == 0
                    ? "No current-user apps are holding an NVIDIA GPU engine."
                    : "No GPU users can be closed safely.\n\n"
                        + FormatGpuApps(blocked, includeBlockReason: true);
                await ShowGpuAppsMessageAsync("No GPU apps to close", message);
                return;
            }

            var confirmation = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Close apps using the NVIDIA GPU?",
                Content = CreateGpuAppsDialogContent(
                    "OpenBlade will ask these apps to close. Save your work first.\n\n"
                    + FormatGpuApps(eligible, includeBlockReason: false)
                    + (blocked.Length == 0
                        ? string.Empty
                        : "\n\nNot closeable:\n"
                            + FormatGpuApps(blocked, includeBlockReason: true))),
                PrimaryButtonText = "Close apps",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _ = await gpuAppAdapter.CloseAsync(
                eligible,
                force: false,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            SessionGpuResponse afterClose = await gpuAppAdapter.QueryAsync(
                CancellationToken.None);
            await ApplyGpuAppVisibilityAsync(afterClose, CancellationToken.None);
            if (afterClose.Status == SessionGpuOperationStatus.Failed)
            {
                await ShowGpuAppsMessageAsync(
                    "GPU activity unavailable",
                    afterClose.Error ?? "GPU activity could not be checked after closing apps.");
                return;
            }
            SessionGpuApp[] remaining = MatchTargets(afterClose.Apps, eligible);
            if (remaining.Length == 0)
            {
                await ShowGpuAppsMessageAsync(
                    "GPU apps closed",
                    "The selected apps released their NVIDIA GPU engines. The GPU may take a moment to return to sleep.");
                return;
            }

            var forceConfirmation = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Force close remaining apps?",
                Content = CreateGpuAppsDialogContent(
                    "These apps did not close. Force closing can discard unsaved work.\n\n"
                    + FormatGpuApps(remaining, includeBlockReason: false)),
                PrimaryButtonText = "Force close",
                CloseButtonText = "Keep running",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await forceConfirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SessionGpuResponse forced = await gpuAppAdapter.CloseAsync(
                remaining,
                force: true,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            SessionGpuResponse finalScan = await gpuAppAdapter.QueryAsync(CancellationToken.None);
            await ApplyGpuAppVisibilityAsync(finalScan, CancellationToken.None);
            if (finalScan.Status == SessionGpuOperationStatus.Failed)
            {
                await ShowGpuAppsMessageAsync(
                    "GPU activity unavailable",
                    finalScan.Error ?? "GPU activity could not be checked after force closing apps.");
                return;
            }
            SessionGpuApp[] survivors = MatchTargets(finalScan.Apps, remaining);
            string finalMessage = survivors.Length == 0
                ? "The selected apps were force closed. The GPU may take a moment to return to sleep."
                : "Some apps could not be closed:\n\n"
                    + FormatGpuApps(survivors, includeBlockReason: true);
            if (forced.Results.Any(result => !result.Succeeded))
            {
                string failures = string.Join(
                    Environment.NewLine,
                    forced.Results
                        .Where(result => !result.Succeeded)
                        .Select(result => $"- PID {result.ProcessId}: {result.Error}"));
                finalMessage += $"\n\nReported failures:\n{failures}";
            }
            await ShowGpuAppsMessageAsync(
                survivors.Length == 0 ? "GPU apps force closed" : "GPU apps still running",
                finalMessage);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            await ShowGpuAppsMessageAsync(
                "GPU activity unavailable",
                $"The Session Agent could not complete the GPU app operation: {exception.Message}");
        }
        finally
        {
            button.SetValue(Microsoft.UI.Xaml.Controls.Control.IsEnabledProperty, true);
        }
    }

    private async Task ObserveGpuAppActivityAsync(CancellationToken cancellationToken)
    {
        SessionGpuResponse scan = await gpuAppAdapter.QueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await ApplyGpuAppVisibilityAsync(scan, cancellationToken).ConfigureAwait(false);
    }

    private Task ApplyGpuAppVisibilityAsync(
        SessionGpuResponse scan,
        CancellationToken cancellationToken) =>
        scan.Status == SessionGpuOperationStatus.Failed
            ? Task.CompletedTask
            : ViewModel.SetGpuAppCleanupVisibleAsync(
                scan.Apps.Count > 0,
                cancellationToken);

    private async Task ShowGpuAppsMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = CreateGpuAppsDialogContent(message),
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        _ = await dialog.ShowAsync();
    }

    private static TextBlock CreateGpuAppsDialogContent(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        MaxWidth = 560,
    };

    private static string FormatGpuApps(
        IEnumerable<SessionGpuApp> apps,
        bool includeBlockReason) => string.Join(
            Environment.NewLine,
            apps.Select(app => includeBlockReason && app.BlockReason is not null
                ? $"- {app.Name} (PID {app.ProcessId}): {app.BlockReason}"
                : $"- {app.Name} (PID {app.ProcessId})"));

    private static SessionGpuApp[] MatchTargets(
        IReadOnlyList<SessionGpuApp> current,
        IReadOnlyList<SessionGpuApp> targets)
    {
        HashSet<(int ProcessId, long StartTimeUtcTicks)> identities = targets
            .Select(app => (app.ProcessId, app.StartTimeUtcTicks))
            .ToHashSet();
        return current
            .Where(app => identities.Contains((app.ProcessId, app.StartTimeUtcTicks)))
            .ToArray();
    }

    private async Task HandleGpuClockTuningToggledAsync(object sender)
    {
        if (changingGpuClockTuning
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.IsGpuClockTuningEnabled)
        {
            return;
        }

        changingGpuClockTuning = true;
        try
        {
            await ViewModel.SetGpuClockTuningEnabledAsync(
                toggle.IsOn,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.IsGpuClockTuningEnabled;
            changingGpuClockTuning = false;
        }
    }

    private async void OnBlockWindowsKeyInGamingModeToggled(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleBlockWindowsKeyInGamingModeToggledAsync(sender),
            nameof(OnBlockWindowsKeyInGamingModeToggled));

    private async Task HandleBlockWindowsKeyInGamingModeToggledAsync(object sender)
    {
        if (changingGamingModeKeyPolicy
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.BlockWindowsKeyInGamingMode)
        {
            return;
        }

        changingGamingModeKeyPolicy = true;
        try
        {
            await ViewModel.SetGamingModeKeyPolicyAsync(
                toggle.IsOn,
                ViewModel.BlockAltTabInGamingMode,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.BlockWindowsKeyInGamingMode;
            changingGamingModeKeyPolicy = false;
        }
    }

    private async void OnBlockAltTabInGamingModeToggled(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleBlockAltTabInGamingModeToggledAsync(sender),
            nameof(OnBlockAltTabInGamingModeToggled));

    private async Task HandleBlockAltTabInGamingModeToggledAsync(object sender)
    {
        if (changingGamingModeKeyPolicy
            || Volatile.Read(ref disposed) != 0
            || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.BlockAltTabInGamingMode)
        {
            return;
        }

        changingGamingModeKeyPolicy = true;
        try
        {
            await ViewModel.SetGamingModeKeyPolicyAsync(
                ViewModel.BlockWindowsKeyInGamingMode,
                toggle.IsOn,
                CancellationToken.None);
        }
        finally
        {
            toggle.IsOn = ViewModel.BlockAltTabInGamingMode;
            changingGamingModeKeyPolicy = false;
        }
    }

    private void OnGpuCoreClockOffsetChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        gpuClockOffsetCommits.TryPeekPending(out GpuClockOffsetSettings? pending);
        ScheduleGpuClockOffsetCommit(
            coreMegahertz: checked((int)Math.Round(args.NewValue)),
            memoryMegahertz: pending?.MemoryMegahertz
                ?? ViewModel.GpuMemoryClockOffsetMegahertz);
    }

    private void OnGpuMemoryClockOffsetChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        gpuClockOffsetCommits.TryPeekPending(out GpuClockOffsetSettings? pending);
        ScheduleGpuClockOffsetCommit(
            coreMegahertz: pending?.CoreMegahertz
                ?? ViewModel.GpuCoreClockOffsetMegahertz,
            memoryMegahertz: checked((int)Math.Round(args.NewValue)));
    }

    private void ScheduleGpuClockOffsetCommit(
        int coreMegahertz,
        int memoryMegahertz)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var requested = new GpuClockOffsetSettings(
            coreMegahertz,
            memoryMegahertz);
        if (!GpuClockOffsetCommitPolicy.CanCommit(
                ViewModel.CanEditGpuClockTuning,
                ViewModel.GpuClockOffsetCapabilities,
                requested))
        {
            gpuClockOffsetCommits.ClearPending();
            gpuClockOffsetCommitTimer.Stop();
            return;
        }

        if (requested == ViewModel.CurrentGpuClockOffsets)
        {
            gpuClockOffsetCommits.ClearPending();
            gpuClockOffsetCommitTimer.Stop();
            return;
        }

        gpuClockOffsetCommits.Schedule(requested);
        gpuClockOffsetCommitTimer.Stop();
        gpuClockOffsetCommitTimer.Start();
    }

    private async void OnGpuClockOffsetCommitTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        CoalescingCommitBeginStatus status =
            gpuClockOffsetCommits.TryBegin(out GpuClockOffsetSettings? requested);
        if (status == CoalescingCommitBeginStatus.InProgress)
        {
            sender.Start();
            return;
        }
        if (status != CoalescingCommitBeginStatus.Started
            || requested is null)
        {
            return;
        }

        try
        {
            if (GpuClockOffsetCommitPolicy.CanCommit(
                    ViewModel.CanEditGpuClockTuning,
                    ViewModel.GpuClockOffsetCapabilities,
                    requested))
            {
                await ViewModel.SetGpuClockOffsetsAsync(
                    requested,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade GPU clock-offset change failed: {0}", exception);
        }
        finally
        {
            if (gpuClockOffsetCommits.Complete())
            {
                sender.Start();
            }
        }
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
        var request = new LightingCommitRequest(
            ViewModel.IsLightingApplyPending
                ? ViewModel.LightingDraft
                : null,
            ViewModel.HasUnsavedLightingPowerPolicy
                ? ViewModel.LightingPowerPolicyDraft
                : null);
        if (Volatile.Read(ref disposed) != 0 || !request.HasWork)
        {
            lightingCommits.ClearPending();
            lightingCommitTimer.Stop();
            return;
        }

        lightingCommits.Schedule(request);
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
        CoalescingCommitBeginStatus status =
            lightingCommits.TryBegin(out LightingCommitRequest? request);
        if (status == CoalescingCommitBeginStatus.InProgress)
        {
            sender.Start();
            return;
        }
        if (status != CoalescingCommitBeginStatus.Started
            || request is null)
        {
            return;
        }

        try
        {
            if (request.Lighting is { } draft
                && ViewModel.SetLightingCommand is AsyncRelayCommand lightingCommand)
            {
                if (lightingCommand.CanExecute(draft))
                {
                    await lightingCommand.ExecuteAsync(draft);
                }
            }

            if (request.PowerPolicy is { } policy
                && ViewModel.SaveLightingPowerPolicyCommand is AsyncRelayCommand policyCommand)
            {
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
            bool hasQueuedCommit = lightingCommits.Complete();
            if (Volatile.Read(ref disposed) == 0 && ViewModel.CanControlLighting)
            {
                if (hasQueuedCommit)
                {
                    sender.Start();
                }
                else if (ViewModel.IsLightingApplyPending
                    || ViewModel.HasUnsavedLightingPowerPolicy)
                {
                    ScheduleLightingCommit();
                }
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
        chargeLimitCommits.Schedule(new ChargeLimitCommitRequest(
            value,
            ViewModel.ChargeLimitEditorRevision));
        chargeLimitCommitTimer.Stop();
        chargeLimitCommitTimer.Start();
    }

    private async void OnChargeLimitCommitTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        CoalescingCommitBeginStatus status =
            chargeLimitCommits.TryBegin(out ChargeLimitCommitRequest commit);
        if (status == CoalescingCommitBeginStatus.InProgress)
        {
            sender.Start();
            return;
        }
        if (status != CoalescingCommitBeginStatus.Started)
        {
            return;
        }

        try
        {
            if (ViewModel.SetChargeLimitCommand is AsyncRelayCommand command
                && command.CanExecute(commit.Value))
            {
                await command.ExecuteAsync(commit);
            }
            else
            {
                ViewModel.RevertUnappliedChargeLimitEdit(
                    commit.Value,
                    commit.EditorRevision);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade charge-limit change failed: {0}", exception);
            ViewModel.RevertUnappliedChargeLimitEdit(
                commit.Value,
                commit.EditorRevision);
        }
        finally
        {
            if (chargeLimitCommits.Complete()
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

    private void OnTroubleshootingHeaderPointerEntered(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
        UpdateTroubleshootingHeaderPointerState(args);

    private void OnTroubleshootingHeaderPointerMoved(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) =>
        UpdateTroubleshootingHeaderPointerState(args);

    private void OnTroubleshootingHeaderPointerExited(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        troubleshootingHeaderPointerPressed = false;
        SetTroubleshootingHeaderPointerVisual(hovered: false, pressed: false);
    }

    private void OnTroubleshootingHeaderPointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        troubleshootingHeaderPointerPressed = IsPointerOverTroubleshootingHeader(args);
        UpdateTroubleshootingHeaderPointerState(args);
    }

    private void OnTroubleshootingHeaderPointerReleased(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        troubleshootingHeaderPointerPressed = false;
        UpdateTroubleshootingHeaderPointerState(args);
    }

    private void OnTroubleshootingHeaderPointerCanceled(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        troubleshootingHeaderPointerPressed = false;
        SetTroubleshootingHeaderPointerVisual(hovered: false, pressed: false);
    }

    private void UpdateTroubleshootingHeaderPointerState(
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        bool hovered = IsPointerOverTroubleshootingHeader(args);
        SetTroubleshootingHeaderPointerVisual(
            hovered,
            pressed: hovered && troubleshootingHeaderPointerPressed);
    }

    private bool IsPointerOverTroubleshootingHeader(
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        double pointerY = args
            .GetCurrentPoint(TroubleshootingExpander)
            .Position
            .Y;
        return pointerY >= 0
            && pointerY <= TroubleshootingHeaderSurface.ActualHeight;
    }

    private void SetTroubleshootingHeaderPointerVisual(
        bool hovered,
        bool pressed)
    {
        TroubleshootingHeaderHoverBackground.Opacity = hovered && !pressed ? 1 : 0;
        TroubleshootingHeaderPressedBackground.Opacity = pressed ? 1 : 0;
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
                var binding = new SpecialKeyBindingViewModel(
                    preference,
                    ViewModel.IsSpecialKeyBindingSlotEditable(preference.Slot));
                binding.PropertyChanged += OnSpecialKeyBindingChanged;
                SpecialKeyBindings.Add(binding);
            }
        }
        finally
        {
            changingSpecialKeyBindings = false;
        }
    }

    private void OnSpecialKeyActionSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (changingSpecialKeyBindings
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox
            {
                SelectedItem: SpecialKeyActionOption selectedAction,
            } comboBox
            || comboBox.Tag is not SpecialKeySlot slot)
        {
            return;
        }

        SpecialKeyBindingViewModel? binding = SpecialKeyBindings
            .FirstOrDefault(candidate => candidate.Slot == slot);
        if (binding is not null && binding.SelectedAction != selectedAction)
        {
            binding.SelectedAction = selectedAction;
        }
    }

    private void RefreshSpecialKeyBindingAccess()
    {
        foreach (SpecialKeyBindingViewModel binding in SpecialKeyBindings)
        {
            binding.SetEditable(
                ViewModel.IsSpecialKeyBindingSlotEditable(binding.Slot));
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
        if (trayDashboardCustomizationActive && destination != "settings")
        {
            EndTrayDashboardCustomization(showSettingsOverview: false);
        }

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
            && !trayDashboardCustomizationActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        TrayDashboardCustomizationPage.Visibility = destination == "settings"
            && trayDashboardCustomizationActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        AboutPage.Visibility = destination == "about"
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateDashboardTelemetryPresentationState();
        UpdateGpuAppObservationState();
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

    private void UpdateGpuAppObservationState()
    {
        if (windowVisible && HomePage.Visibility == Visibility.Visible)
        {
            gpuAppObservationLoop.StartOrWake();
        }
        else
        {
            gpuAppObservationLoop.Stop();
        }
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
        if (windowVisible || trayDashboardVisible)
        {
            visibleObservationLoop.StartOrWake();
        }
    }

    private Task ObserveVisibleSurfacesAsync(CancellationToken cancellationToken)
    {
        bool dashboardVisible = Volatile.Read(ref trayDashboardVisible);
        return dashboardVisible
            ? ViewModel.ObserveTrayDashboardStateAsync(
                includeMainDashboardReadings: Volatile.Read(ref windowVisible),
                dashboardLiveReadings: (LiveReadingSensors)Volatile.Read(
                    ref trayDashboardLiveReadingSensors),
                cancellationToken)
            : ViewModel.ObserveStateAsync(cancellationToken);
    }

    private void UpdateActiveObservationLoop()
    {
        if (windowVisible || trayDashboardVisible)
        {
            visibleObservationLoop.StartOrWake();
        }
        else
        {
            visibleObservationLoop.Stop();
        }
    }

    private void ApplyProcessorVisibilityPolicy()
    {
        if (windowVisible || trayDashboardVisible)
        {
            _ = processorPolicy.TryAllowAllProcessorsForCurrentThread();
            _ = processorPolicy.TrySetProcessEcoQos(enabled: false);
        }
        else
        {
            _ = processorPolicy.TryPreferEfficientProcessorsForCurrentThread();
            _ = processorPolicy.TrySetProcessEcoQos(enabled: true);
        }
    }

    public void SetTrayDashboardVisible(bool visible)
    {
        if (trayDashboardVisible == visible)
        {
            return;
        }

        Volatile.Write(ref trayDashboardVisible, visible);
        ViewModel.SetTrayDashboardTelemetryPresentationActive(visible);
        ApplyProcessorVisibilityPolicy();
        UpdateDashboardTelemetryPresentationState();
        if (visible)
        {
            trayObservationLoop.Stop();
        }
        UpdateActiveObservationLoop();
    }

    private void UpdateDashboardTelemetryPresentationState() =>
        ViewModel.SetDashboardTelemetryPresentationActive(
            windowVisible && HomePage.Visibility == Visibility.Visible);

    public void RequestTrayTelemetryRefresh()
    {
        if (windowVisible
            || !ViewModel.HasEnabledTrayHoverLiveReadings)
        {
            return;
        }

        trayObservationLoop.StartOrWake();
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
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            HandleSaveCurrentDisplaySettingsToProfileAsync,
            nameof(OnSaveCurrentDisplaySettingsToProfile));

    private async Task HandleSaveCurrentDisplaySettingsToProfileAsync()
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

    private async void OnUseDisplayProfileSettings(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => ViewModel.UseActiveDisplayProfileSettingsAsync(),
            nameof(OnUseDisplayProfileSettings));

    private async Task RunHandlerAsync(Func<Task> operation, string label)
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
            Trace.TraceError("OpenBlade {0} failed: {1}", label, exception);
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

    private async void OnTestedPlatformWarningClick(
        object sender,
        RoutedEventArgs args) =>
        await RunHandlerAsync(
            ShowTestedPlatformWarningAsync,
            nameof(OnTestedPlatformWarningClick));

    private async Task ShowTestedPlatformWarningAsync()
    {
        if (!ViewModel.HasIncompatibleFirmware)
        {
            NavigateToAboutTestedPlatformWarning();
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 440,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            Text = ViewModel.DashboardTestedPlatformWarningMessage,
            TextWrapping = TextWrapping.Wrap,
        });

        Microsoft.UI.Xaml.Controls.CheckBox? runAnyway = null;
        if (ViewModel.HasIncompatibleFirmware)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Forcing control may send captured commands to firmware they were not validated against. Exact laptop and USB identity checks remain required.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Root.Resources["SecondaryTextStyle"],
            });
            runAnyway = new Microsoft.UI.Xaml.Controls.CheckBox
            {
                Content = "Ignore this firmware mismatch and run anyway",
                IsChecked = ViewModel.AllowUntestedFirmware,
            };
            content.Children.Add(runAnyway);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = ViewModel.PlatformCompatibilityWarningTitle,
            Content = content,
            PrimaryButtonText = runAnyway is null ? string.Empty : "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowContentDialogAsync(dialog) != ContentDialogResult.Primary
            || runAnyway?.IsChecked is not bool enabled
            || enabled == ViewModel.AllowUntestedFirmware)
        {
            return;
        }

        await ViewModel.SetFirmwareCompatibilityOverrideAsync(
            enabled,
            CancellationToken.None);
    }

    private void NavigateToAboutTestedPlatformWarning()
    {
        MainNavigation.SelectedItem = AboutNavigationItem;
        dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                AboutTestedPlatformWarning.StartBringIntoView(
                    new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0,
                    });
            });
    }

    private async void OnStopRazerServices(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            HandleStopRazerServicesAsync,
            nameof(OnStopRazerServices));

    private async Task HandleStopRazerServicesAsync()
    {
        if (!StopRazerServicesButton.IsEnabled)
        {
            return;
        }

        SetStopRazerServicesProgress(isBusy: true, busyLabel: "Checking…");
        try
        {
            RazerServiceInventory before = await RazerServiceStopOperation.ObserveAsync(
                CancellationToken.None);
            if (!before.Complete)
            {
                throw new InvalidOperationException(before.Error);
            }

            IReadOnlyList<RazerServiceSnapshot> candidates =
                RazerServiceStopOperation.ActiveServices(before);
            if (candidates.Count == 0)
            {
                await ShowMessageDialogAsync(
                    "No known Razer controller services are running",
                    "OpenBlade found no active Synapse or Chroma controller services in its reviewed allowlist.");
                return;
            }

            string serviceList = string.Join(
                Environment.NewLine,
                candidates.Select(service => $"• {service.DisplayName}"));
            if (Volatile.Read(ref disposed) != 0 || Root.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirmation = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Stop Razer services?",
                Content = "These known controller services are currently active:\n\n"
                    + serviceList
                    + "\n\nAfter approval, OpenBlade will freshly check the same reviewed allowlist "
                    + "and stop any active matches. This does not uninstall Razer software or disable service startup. "
                    + "Razer software may start the services again, and features that depend on them may stop working until then.",
                PrimaryButtonText = "Stop services",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonStyle = (Style)Root.Resources["DestructiveButtonStyle"],
            };
            if (await ShowContentDialogAsync(confirmation) != ContentDialogResult.Primary
                || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            SetStopRazerServicesProgress(isBusy: true, busyLabel: "Stopping…");

            if (Microsoft.UI.Xaml.Application.Current is not App app)
            {
                throw new InvalidOperationException("The OpenBlade application is unavailable.");
            }

            ElevatedServiceControlOutcome outcome = await app.StopRazerServicesAsync();
            if (outcome == ElevatedServiceControlOutcome.Canceled)
            {
                await ShowMessageDialogAsync(
                    "Razer service stop canceled",
                    "Windows did not grant administrator permission. OpenBlade did not retry the request.");
                return;
            }

            RazerServiceInventory after = await RazerServiceStopOperation.ObserveAsync(
                CancellationToken.None);
            await ShowRazerServiceStopResultAsync(candidates, after, outcome);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Razer service stop failed: {0}", exception);
            await ShowMessageDialogAsync(
                "OpenBlade could not stop Razer services",
                exception.Message);
        }
        finally
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                SetStopRazerServicesProgress(isBusy: false, busyLabel: null);
            }
        }
    }

    private async Task ShowRazerServiceStopResultAsync(
        IReadOnlyList<RazerServiceSnapshot> candidates,
        RazerServiceInventory after,
        ElevatedServiceControlOutcome helperOutcome)
    {
        if (!after.Complete)
        {
            await ShowMessageDialogAsync(
                "Razer service status is unconfirmed",
                "The elevated request finished, but OpenBlade could not read the final service state. "
                    + after.Error);
            return;
        }

        RazerServiceStopReadback readback = RazerServiceStopOperation.EvaluateReadback(
            candidates,
            after);
        IReadOnlyList<RazerServiceSnapshot> remaining = readback.RemainingActive;
        IReadOnlyList<string> stopped = readback.StoppedOrRemoved;

        if (remaining.Count == 0)
        {
            string note = helperOutcome == ElevatedServiceControlOutcome.Indeterminate
                ? "\n\nThe helper result was indeterminate, but fresh readback confirms the services are stopped or no longer installed."
                : string.Empty;
            await ShowMessageDialogAsync(
                "Razer services stopped",
                string.Join(Environment.NewLine, stopped.Select(service => $"• {service}"))
                    + note);
            return;
        }

        string content = stopped.Count == 0
            ? string.Empty
            : "Stopped or no longer installed:\n"
                + string.Join(Environment.NewLine, stopped.Select(service => $"• {service}"))
                + "\n\n";
        content += "Still active or pending:\n"
            + string.Join(
                Environment.NewLine,
                remaining.Select(service => $"• {service.DisplayName}"))
            + "\n\nOpenBlade did not retry any stop request.";
        await ShowMessageDialogAsync(
            "Some Razer services could not be confirmed stopped",
            content);
    }

    private async Task<ContentDialogResult?> ShowContentDialogAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (Volatile.Read(ref disposed) != 0)
        {
            return null;
        }

        await contentDialogGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref disposed) != 0 || Root.XamlRoot is not { } xamlRoot)
            {
                return null;
            }

            dialog.XamlRoot = xamlRoot;
            ContentDialogResult result = await dialog.ShowAsync();
            return Volatile.Read(ref disposed) == 0 && Root.XamlRoot is not null
                ? result
                : null;
        }
        catch (Exception exception)
            when (Volatile.Read(ref disposed) != 0 || Root.XamlRoot is null)
        {
            Trace.TraceWarning(
                "OpenBlade dialog closed during window teardown: {0}",
                exception.Message);
            return null;
        }
        finally
        {
            contentDialogGate.Release();
        }
    }

    private async Task ShowMessageDialogAsync(string title, string content)
    {
        if (Volatile.Read(ref disposed) != 0 || Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await ShowContentDialogAsync(dialog);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade could not show the '{0}' dialog: {1}",
                title,
                exception.Message);
        }
    }

    private void SetStopRazerServicesProgress(bool isBusy, string? busyLabel)
    {
        StopRazerServicesButton.IsEnabled = !isBusy;
        StopRazerServicesProgressRing.IsActive = isBusy;
        StopRazerServicesProgressRing.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        StopRazerServicesIcon.Visibility = isBusy
            ? Visibility.Collapsed
            : Visibility.Visible;
        StopRazerServicesButtonLabel.Text = isBusy
            ? busyLabel ?? "Working…"
            : "Stop services";
        AutomationProperties.SetName(
            StopRazerServicesButton,
            isBusy ? busyLabel ?? "Working with Razer services" : "Stop Razer services");
    }

    private async void OnShutdownOpenBlade(object sender, RoutedEventArgs args)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Shut down OpenBlade?",
            Content = "This stops the app, display helper, and hardware service. "
                + "Reopen OpenBlade and use Start OpenBlade in troubleshooting to turn it back on.",
            PrimaryButtonText = "Shut down",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Root.Resources["DestructiveButtonStyle"],
        };
        if (await ShowContentDialogAsync(confirmation) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (Microsoft.UI.Xaml.Application.Current is App app)
            {
                await app.ShutdownOpenBladeAsync();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade shutdown failed: {0}", exception);
            await ShowMessageDialogAsync(
                "OpenBlade could not shut down",
                exception.Message);
        }
    }

    private async void OnStartOpenBlade(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.IsServiceUnavailable || !StartOpenBladeButton.IsEnabled)
        {
            return;
        }

        SetStartOpenBladeProgress(isStarting: true);
        try
        {
            if (Microsoft.UI.Xaml.Application.Current is App app)
            {
                StartOpenBladeResult result = await app.StartOpenBladeAsync();
                if (result == StartOpenBladeResult.AlreadyRunning)
                {
                    SetStartOpenBladeProgress(isStarting: false);
                    await ShowMessageDialogAsync(
                        "OpenBlade service is already running",
                        "The tray will reconnect automatically. This can take a few seconds.");
                }
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade service start failed: {0}", exception);
            await ShowMessageDialogAsync(
                "OpenBlade could not start",
                exception.Message);
        }
        finally
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                SetStartOpenBladeProgress(isStarting: false);
            }
        }
    }

    private void SetStartOpenBladeProgress(bool isStarting)
    {
        StartOpenBladeButton.IsEnabled = !isStarting;
        AutomationProperties.SetName(
            StartOpenBladeButton,
            isStarting ? "Starting OpenBlade" : "Start OpenBlade");
        StartOpenBladeProgressRing.IsActive = isStarting;
        StartOpenBladeProgressRing.Opacity = isStarting ? 1 : 0;
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
            await ShowMessageDialogAsync(
                "Profiles could not be exported",
                "OpenBlade could not save the selected JSON file.");
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

    private async void OnCustomizeDashboard(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            HandleCustomizeDashboardAsync,
            nameof(OnCustomizeDashboard));

    private async Task HandleCustomizeDashboardAsync()
    {
        if (dashboardCustomizerDialog is not null)
        {
            dashboardCustomizerRefresh?.Invoke();
            if (await ShowContentDialogAsync(dashboardCustomizerDialog) is null)
            {
                return;
            }

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
        if (await ShowContentDialogAsync(dialog) is null)
        {
            return;
        }

        ApplyDashboardCustomizerChanges();
    }

    private void ApplyDashboardCustomizerChanges() =>
        dashboardCustomizerApply?.Invoke();

    private void OnCustomizeTrayDashboard(object sender, RoutedEventArgs args)
    {
        if (trayDashboardCustomizationActive)
        {
            return;
        }

        trayDashboardCustomizationActive = true;
        LoadTrayDashboardEditor(trayDashboardPreference);
        SettingsPage.Visibility = Visibility.Collapsed;
        TrayDashboardCustomizationPage.Visibility = Visibility.Visible;
        TrayDashboardCustomizationPage.ChangeView(null, 0, null, disableAnimation: true);
        TrayDashboardEditSessionChanged?.Invoke(true);
    }

    private void OnExitTrayDashboardCustomization(object sender, RoutedEventArgs args) =>
        EndTrayDashboardCustomization(showSettingsOverview: true);

    private void EndTrayDashboardCustomization(bool showSettingsOverview)
    {
        if (!trayDashboardCustomizationActive)
        {
            return;
        }

        trayDashboardCustomizationActive = false;
        TrayDashboardCustomizationPage.Visibility = Visibility.Collapsed;
        if (showSettingsOverview)
        {
            SettingsPage.Visibility = Visibility.Visible;
        }

        TrayDashboardEditSessionChanged?.Invoke(false);
    }

    private void LoadTrayDashboardEditor(TrayDashboardPreference preference)
    {
        changingTrayDashboardEditor = true;
        try
        {
            foreach (TrayDashboardStatusItemOption option in TrayDashboardEditorStatusItems)
            {
                option.PropertyChanged -= OnTrayDashboardEditorStatusChanged;
            }

            trayDashboardAllControlOptions =
                TrayDashboardPreferenceDefaults.CreateControlOptions(preference);
            trayDashboardAllSliderOptions =
                TrayDashboardPreferenceDefaults.CreateSliderOptions(preference);

            TrayDashboardEditorControls.Clear();
            foreach (TrayDashboardControlOption option in
                     trayDashboardAllControlOptions.Where(option => option.IsVisible))
            {
                TrayDashboardEditorControls.Add(option);
            }

            TrayDashboardEditorSliders.Clear();
            foreach (TrayDashboardSliderOption option in
                     trayDashboardAllSliderOptions.Where(option => option.IsVisible))
            {
                TrayDashboardEditorSliders.Add(option);
            }

            TrayDashboardEditorStatusItems.Clear();
            foreach (TrayDashboardStatusItemOption option in
                     TrayDashboardPreferenceDefaults.CreateStatusOptions(preference))
            {
                option.PropertyChanged += OnTrayDashboardEditorStatusChanged;
                TrayDashboardEditorStatusItems.Add(option);
            }
        }
        finally
        {
            changingTrayDashboardEditor = false;
        }

        UpdateTrayDashboardEditorAvailability();
    }

    private void OnAddTrayDashboardControl(object sender, RoutedEventArgs args)
    {
        var flyout = new MenuFlyout();
        foreach (TrayDashboardControlOption option in
                 trayDashboardAllControlOptions.Where(option => !option.IsVisible))
        {
            var item = new MenuFlyoutItem
            {
                Text = option.Label,
                Tag = option,
            };
            item.Click += OnTrayDashboardControlAddItemClicked;
            flyout.Items.Add(item);
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnTrayDashboardControlAddItemClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not FrameworkElement
            {
                Tag: TrayDashboardControlOption option,
            }
            || option.IsVisible)
        {
            return;
        }

        option.IsVisible = true;
        TrayDashboardEditorControls.Add(option);
    }

    private void OnRemoveTrayDashboardControl(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement
            {
                DataContext: TrayDashboardControlOption option,
            }
            && TrayDashboardEditorControls.Contains(option))
        {
            changingTrayDashboardEditor = true;
            try
            {
                TrayDashboardEditorControls.Remove(option);
                option.IsVisible = false;
            }
            finally
            {
                changingTrayDashboardEditor = false;
            }

            ApplyTrayDashboardEditor();
        }
    }

    private void OnAddTrayDashboardSlider(object sender, RoutedEventArgs args)
    {
        var flyout = new MenuFlyout();
        foreach (TrayDashboardSliderOption option in
                 trayDashboardAllSliderOptions.Where(option => !option.IsVisible))
        {
            var item = new MenuFlyoutItem
            {
                Text = option.Label,
                Tag = option,
            };
            item.Click += OnTrayDashboardSliderAddItemClicked;
            flyout.Items.Add(item);
        }

        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnTrayDashboardSliderAddItemClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (TrayDashboardEditorSliders.Count
                >= TrayDashboardPreferenceDefaults.MaximumVisibleSliders
            || sender is not FrameworkElement
            {
                Tag: TrayDashboardSliderOption option,
            }
            || option.IsVisible)
        {
            return;
        }

        option.IsVisible = true;
        TrayDashboardEditorSliders.Add(option);
    }

    private void OnRemoveTrayDashboardSlider(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement
            {
                DataContext: TrayDashboardSliderOption option,
            }
            && TrayDashboardEditorSliders.Contains(option))
        {
            changingTrayDashboardEditor = true;
            try
            {
                TrayDashboardEditorSliders.Remove(option);
                option.IsVisible = false;
            }
            finally
            {
                changingTrayDashboardEditor = false;
            }

            ApplyTrayDashboardEditor();
        }
    }

    private void OnTrayDashboardEditorStatusChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (changingTrayDashboardEditor
            || args.PropertyName != nameof(TrayDashboardStatusItemOption.IsVisible))
        {
            return;
        }

        if (sender is TrayDashboardStatusItemOption { IsVisible: true } option
            && TrayDashboardEditorStatusItems.Count(status => status.IsVisible)
                > TrayDashboardPreferenceDefaults.MaximumVisibleStatusItems)
        {
            changingTrayDashboardEditor = true;
            try
            {
                option.IsVisible = false;
            }
            finally
            {
                changingTrayDashboardEditor = false;
            }

            UpdateTrayDashboardEditorAvailability();
            return;
        }

        ApplyTrayDashboardEditor();
    }

    private void OnTrayDashboardEditorOrderChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (!changingTrayDashboardEditor)
        {
            ApplyTrayDashboardEditor();
        }
    }

    private void OnResetTrayDashboard(object sender, RoutedEventArgs args)
    {
        LoadTrayDashboardEditor(TrayDashboardPreferenceDefaults.Preference);
        ApplyTrayDashboardEditor();
    }

    private void ApplyTrayDashboardEditor()
    {
        HashSet<TrayDashboardControlKind> activeControls =
            TrayDashboardEditorControls.Select(option => option.Kind).ToHashSet();
        HashSet<TrayDashboardSliderKind> activeSliders =
            TrayDashboardEditorSliders.Select(option => option.Kind).ToHashSet();
        ApplyTrayDashboardPreference(new TrayDashboardPreference(
            Controls: TrayDashboardEditorControls
                .Select(option => new TrayDashboardControlPreference(option.Kind, true))
                .Concat(trayDashboardAllControlOptions
                    .Where(option => !activeControls.Contains(option.Kind))
                    .Select(option => new TrayDashboardControlPreference(
                        option.Kind,
                        false)))
                .ToArray(),
            StatusItems: TrayDashboardEditorStatusItems
                .Select(option => new TrayDashboardStatusItemPreference(
                    option.Kind,
                    option.IsVisible))
                .ToArray(),
            Sliders: TrayDashboardEditorSliders
                .Select(option => new TrayDashboardSliderPreference(option.Kind, true))
                .Concat(trayDashboardAllSliderOptions
                    .Where(option => !activeSliders.Contains(option.Kind))
                    .Select(option => new TrayDashboardSliderPreference(
                        option.Kind,
                        false)))
                .ToArray(),
            SchemaVersion: TrayDashboardPreferenceDefaults.CurrentSchemaVersion),
            queueSave: true);
        UpdateTrayDashboardEditorAvailability();
    }

    private void UpdateTrayDashboardEditorAvailability()
    {
        AddTrayDashboardControlButton.IsEnabled =
            trayDashboardAllControlOptions.Any(option => !option.IsVisible);
        AddTrayDashboardSliderButton.IsEnabled =
            TrayDashboardEditorSliders.Count
                < TrayDashboardPreferenceDefaults.MaximumVisibleSliders
            && trayDashboardAllSliderOptions.Any(option => !option.IsVisible);
        TrayDashboardSliderCountText.Text =
            $"{TrayDashboardEditorSliders.Count} of "
            + TrayDashboardPreferenceDefaults.MaximumVisibleSliders;
        int visibleStatusItemCount =
            TrayDashboardEditorStatusItems.Count(option => option.IsVisible);
        foreach (TrayDashboardStatusItemOption option in TrayDashboardEditorStatusItems)
        {
            option.IsEnabled = option.IsVisible
                || visibleStatusItemCount
                    < TrayDashboardPreferenceDefaults.MaximumVisibleStatusItems;
        }
        TrayDashboardStatusCountText.Text =
            $"{visibleStatusItemCount} of "
            + TrayDashboardPreferenceDefaults.MaximumVisibleStatusItems;
    }

    private void ApplyTrayDashboardPreference(
        TrayDashboardPreference preference,
        bool queueSave)
    {
        trayDashboardPreference = TrayDashboardPreferenceDefaults.Normalize(preference);
        int previousSensors = Volatile.Read(ref trayDashboardLiveReadingSensors);
        int sensors = (int)TrayDashboardPreferenceDefaults.ResolveLiveReadingSensors(
            trayDashboardPreference);
        Volatile.Write(ref trayDashboardLiveReadingSensors, sensors);
        TrayDashboardPreferenceChanged?.Invoke(trayDashboardPreference);
        if (trayDashboardVisible && sensors != previousSensors)
        {
            visibleObservationLoop.StartOrWake();
        }
        if (queueSave)
        {
            QueuePreferenceSave();
        }
    }

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

    private async void OnCustomizePageSections(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            () => HandleCustomizePageSectionsAsync(sender),
            nameof(OnCustomizePageSections));

    private async Task HandleCustomizePageSectionsAsync(object sender)
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
        if (await ShowContentDialogAsync(dialog) is null)
        {
            return;
        }

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
        string itemTemplateKey,
        FrameworkElement? additionalContent = null,
        double maxListHeight = 480)
    {
        var reorderList = new Microsoft.UI.Xaml.Controls.ListView
        {
            ItemsSource = items,
            SelectionMode = ListViewSelectionMode.None,
            CanDragItems = true,
            CanReorderItems = true,
            ReorderMode = ListViewReorderMode.Enabled,
            AllowDrop = true,
            MaxHeight = maxListHeight,
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
        if (additionalContent is not null)
        {
            content.Children.Add(additionalContent);
        }

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

    private async void OnChooseCustomAccentColor(object sender, RoutedEventArgs args) =>
        await RunHandlerAsync(
            HandleChooseCustomAccentColorAsync,
            nameof(OnChooseCustomAccentColor));

    private async Task HandleChooseCustomAccentColorAsync()
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
        if (await ShowContentDialogAsync(dialog) != ContentDialogResult.Primary)
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
        pageSectionPresentations[PageSectionKind.SettingsUpdates] =
            SettingsUpdatesSection;
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
            if (await ShowContentDialogAsync(confirmation) != ContentDialogResult.Primary)
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
            await ShowMessageDialogAsync(
                "Profiles could not be imported",
                exception.Message);
        }
    }

    private async void OnImportKeyboardLighting(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                Title = "Import OpenBlade keyboard lighting",
            };
            picker.FileTypeFilter.Add(".oblight");
            picker.FileTypeFilter.Add(".chroma");
            picker.FileTypeFilter.Add(".json");

            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is null)
            {
                return;
            }

            bool isChroma = string.Equals(
                Path.GetExtension(result.Path),
                ".chroma",
                StringComparison.OrdinalIgnoreCase);
            int maximumFileSize = isChroma
                ? RazerChromaFileAdapter.MaximumFileSizeBytes
                : KeyboardLightingFileSerializer.MaximumFileSizeBytes;
            byte[] content = await ReadBoundedKeyboardLightingFileAsync(
                result.Path,
                maximumFileSize);
            KeyboardLightingPreset preset = isChroma
                ? RazerChromaFileAdapter.Deserialize(
                    content,
                    Path.GetFileNameWithoutExtension(result.Path))
                : KeyboardLightingFileSerializer.Deserialize(content);
            if (!ViewModel.CanImportKeyboardLighting(preset))
            {
                throw new InvalidDataException(
                    $"The {preset.Lighting.Effect} effect is unavailable on this laptop right now.");
            }

            string brightness = preset.Lighting.Effect == LightingEffect.Off
                ? "off"
                : $"{preset.Lighting.Brightness}% brightness";
            var confirmation = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Import keyboard lighting?",
                Content = $"{preset.Name} uses the {preset.Lighting.Effect} effect at "
                    + $"{brightness}. Importing applies it through OpenBlade and keeps it "
                    + "as an unsaved change for the active power profile.",
                PrimaryButtonText = "Import and apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowContentDialogAsync(confirmation) != ContentDialogResult.Primary)
            {
                return;
            }

            if (!ViewModel.TryImportKeyboardLighting(preset))
            {
                throw new InvalidDataException(
                    "Lighting control became unavailable before the preset could be imported.");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade keyboard lighting import failed: {0}", exception);
            await ShowMessageDialogAsync(
                "Keyboard lighting could not be imported",
                exception.Message);
        }
    }

    private static async Task<byte[]> ReadBoundedKeyboardLightingFileAsync(
        string path,
        int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        if (stream.Length > maximum)
        {
            throw new InvalidDataException(
                $"The selected lighting file cannot exceed {FormatFileSize(maximum)}.");
        }

        byte[] buffer = new byte[maximum + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                break;
            }
            length += read;
        }

        if (length > maximum)
        {
            throw new InvalidDataException(
                $"The selected lighting file cannot exceed {FormatFileSize(maximum)}.");
        }
        return buffer.AsSpan(0, length).ToArray();
    }

    private static string FormatFileSize(int bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024 * 1024)} MiB"
        : $"{bytes / 1024} KiB";

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
