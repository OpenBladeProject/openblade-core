using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Session;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string LocalizationLanguageVariable =
        "OPENBLADE_LOCALIZATION_LANGUAGE";
    private const string PerformanceNotificationReplacementTag = "performance";
    private static readonly TimeSpan RazerServiceStopCancellationTimeout =
        TimeSpan.FromSeconds(65);
    private static readonly TimeSpan RazerServiceStopHardExitTimeout =
        TimeSpan.FromSeconds(70);
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    private CrashReportWriter? crashReportWriter;
    private IDisposable? traceLogRegistration;
    private WindowsApplicationRestartRegistration? applicationRestartRegistration;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;
    private MainWindow? mainWindow;
    private TrayDashboardWindow? trayDashboardWindow;
    private WindowsTrayBrightnessOsdHost? brightnessOsdHost;
    private WindowsTrayPerformanceNotificationHost? performanceNotificationHost;
    private WindowsShellThemeSource? trayIconThemeSource;
    private ITrayIconHost? trayIconHost;
    private TrayExitLifecycle? exitLifecycle;
    private TrayExitController? trayExitController;
    private IStartupRegistration? startupRegistration;
    private SessionAgentEventEndpoint? sessionAgentEventEndpoint;
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private WindowsProcessorEfficiencyPolicy processorPolicy = null!;
    private readonly CancellationTokenSource serviceStartupCancellation = new();
    private TrayServiceStartupCoordinator? serviceStartupCoordinator;
    private int fatalCrashReported;
    private int languageRefreshInProgress;

    static App()
    {
        ApplyLocalizationPreference();
    }

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLineArguments = Environment.GetCommandLineArgs();
        base.OnLaunched(args);
        if (TryRunDeveloperHarness(commandLineArguments))
        {
            return;
        }
        RazerDriverHelperParseResult driverHelper = RazerDriverInstallLauncher.Parse(commandLineArguments);
        if (driverHelper != RazerDriverHelperParseResult.None)
        {
            if (driverHelper == RazerDriverHelperParseResult.Invalid)
            {
                Environment.Exit(2);
                return;
            }
            _ = RunRazerDriverInstallHelperAsync();
            return;
        }
        ElevatedServiceHelperParseResult helperParseResult =
            ElevatedServiceControlLauncher.ParseHelperAction(
            commandLineArguments,
            out ElevatedServiceControlAction helperAction);
        bool elevatedServiceHelper = helperParseResult == ElevatedServiceHelperParseResult.Valid;
        string componentName = elevatedServiceHelper
            ? helperAction switch
            {
                ElevatedServiceControlAction.Start => "OpenBlade.Tray.StartHelper",
                ElevatedServiceControlAction.Shutdown => "OpenBlade.Tray.ShutdownHelper",
                ElevatedServiceControlAction.StopRazerServices =>
                    "OpenBlade.Tray.StopRazerServicesHelper",
                _ => throw new ArgumentOutOfRangeException(nameof(helperAction)),
            }
            : "OpenBlade.Tray";
        RegisterCrashReporting(componentName);
        traceLogRegistration ??= PersistentTraceLog.RegisterDefault(
            componentName,
            OpenBladeProductVersion.Read(typeof(App).Assembly));
        if (helperParseResult == ElevatedServiceHelperParseResult.Invalid)
        {
            Trace.TraceWarning("OpenBlade rejected malformed elevated service helper arguments.");
            traceLogRegistration.Dispose();
            traceLogRegistration = null;
            Exit();
            return;
        }
        if (elevatedServiceHelper)
        {
            _ = RunElevatedServiceControlHelperAsync(helperAction);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var registration = new WindowsStartupRegistration();
        startupRegistration = registration;
        if (!registration.ShouldStartForLaunchArguments(
                commandLineArguments))
        {
            Trace.TraceInformation(
                "OpenBlade skipped the current user's disabled tray startup registration.");
            traceLogRegistration.Dispose();
            traceLogRegistration = null;
            Exit();
            return;
        }

        processorPolicy = WindowsProcessorEfficiencyPolicy.Discover();
        Trace.TraceInformation(
            "OpenBlade processor efficiency policy: {0}",
            processorPolicy.Status.Message);
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        using Process currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;
        TrayInstanceEndpoint instanceEndpoint = TrayInstanceEndpoint.Create(userSid, sessionId);
        if (!TrayInstanceCoordinator.TryStart(
                instanceEndpoint,
                () => Enqueue(ShowMainWindow),
                out instanceCoordinator))
        {
            Exit();
            return;
        }

        applicationRestartRegistration = new WindowsApplicationRestartRegistration();
        _ = applicationRestartRegistration.TryRegister();

        sessionAgentEventEndpoint =
            SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(userSid),
                sessionId);
        AttachWindowSurfaces(
            CreateMainWindow(registration),
            sessionAgentEventEndpoint);

        exitLifecycle = new TrayExitLifecycle(
            new TrayExitSteps(
                UnregisterApplicationRestart: () =>
                {
                    _ = applicationRestartRegistration?.TryUnregister();
                    applicationRestartRegistration = null;
                },
                UnregisterShutdown: () =>
                {
                    shutdownRegistration?.Unregister(null);
                    shutdownRegistration = null;
                },
                DisposeShutdownEvent: () =>
                {
                    shutdownEvent?.Dispose();
                    shutdownEvent = null;
                },
                DisposeTrayIcon: () =>
                {
                    trayIconHost?.Dispose();
                    trayIconHost = null;
                    trayIconThemeSource?.Dispose();
                    trayIconThemeSource = null;
                },
                DisposeViewModelAsync: async () =>
                {
                    performanceNotificationHost?.Dispose();
                    performanceNotificationHost = null;
                    brightnessOsdHost?.Dispose();
                    brightnessOsdHost = null;
                    if (mainWindow is not null)
                    {
                        mainWindow.TrayDashboardPreferenceChanged -=
                            OnTrayDashboardPreferenceChanged;
                        mainWindow.TrayDashboardEditSessionChanged -=
                            OnTrayDashboardEditSessionChanged;
                        mainWindow.LanguagePreferenceChanged -=
                            OnLanguagePreferenceChanged;
                    }
                    trayDashboardWindow?.Dispose();
                    trayDashboardWindow = null;
                    if (mainWindow is not null)
                    {
                        await mainWindow.DisposeAsync();
                    }
                },
                CloseWindow: () =>
                {
                    mainWindow?.Close();
                    mainWindow = null;
                },
                DisposeInstanceCoordinatorAsync: async () =>
                {
                    if (instanceCoordinator is not null)
                    {
                        await instanceCoordinator.DisposeAsync();
                        instanceCoordinator = null;
                    }
                },
                ExitApplication: () =>
                {
                    traceLogRegistration?.Dispose();
                    traceLogRegistration = null;
                    Exit();
                }),
            exception => Trace.TraceError(
                "OpenBlade tray shutdown cleanup failed: {0}",
                exception));

        shutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            TrayShutdownEndpoint.ForExecutablePath(executablePath));
        shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            shutdownEvent,
            (_, _) => Enqueue(() => _ = ExitTrayAsync()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        trayIconThemeSource = new WindowsShellThemeSource(uiDispatcherQueue);
        CreateTrayIconHost();
        StartSessionAgent();
        ShowMainWindow(initialLaunch: true);
        serviceStartupCoordinator = new TrayServiceStartupCoordinator(
            token => Task.Run(WindowsServiceStateConfirmation.ReadCurrentState, token),
            StartOpenBladeCoreAsync);
        _ = TryStartServiceOnLaunchAsync();
    }

    private static void ApplyLocalizationPreference()
    {
        string? language = RequestedLocalizationLanguage();
        if (string.IsNullOrWhiteSpace(language))
        {
            UiLanguagePreference preference =
                UiPreferenceStore.CreateDefault().LoadLanguagePreference();
            language = UiLanguagePreferencePolicy.ResolveCultureName(preference);
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            TrayLocalization.SetLanguageOverride(SystemUiCulture.Name);
            return;
        }

        ApplyLocalizationLanguage(language);
    }

    private static void ApplyRuntimeLocalizationPreference(
        UiLanguagePreference preference,
        bool commitTrayLanguage = true)
    {
        string? language = UiLanguagePreferencePolicy.ResolveCultureName(preference);
        if (string.IsNullOrWhiteSpace(language))
        {
            string resourceLanguage = SystemUiCulture.Name;
            ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            CultureInfo.CurrentUICulture = SystemUiCulture;
            LocalizedResources.SetLanguageOverride(resourceLanguage);
            if (commitTrayLanguage)
            {
                TrayLocalization.SetLanguageOverride(resourceLanguage);
            }

            return;
        }

        ApplyLocalizationLanguage(language, commitTrayLanguage);
    }

    private static void ApplyLocalizationLanguage(
        string language,
        bool commitTrayLanguage = true)
    {
        CultureInfo managedCulture = CultureInfo.GetCultureInfo(language);
        string resourceLanguage = managedCulture.Name;
        ApplicationLanguages.PrimaryLanguageOverride = resourceLanguage;
        CultureInfo.CurrentUICulture = managedCulture;
        LocalizedResources.SetLanguageOverride(resourceLanguage);
        if (commitTrayLanguage)
        {
            TrayLocalization.SetLanguageOverride(resourceLanguage);
        }
    }

    private static string? RequestedLocalizationLanguage() => string.Equals(
                Environment.GetEnvironmentVariable("OPENBLADE_PSEUDO_LOCALIZATION"),
                "1",
                StringComparison.Ordinal)
            ? "qps-ploc"
            : Environment.GetEnvironmentVariable(LocalizationLanguageVariable);

    private MainWindow CreateMainWindow(
        IStartupRegistration startupRegistration,
        UiLanguagePreference? initialLanguageOverride = null,
        UiPreferenceStore? preferenceStore = null,
        UpdateCheckInterval? initialUpdateCheckConsentOverride = null)
    {
        return new MainWindow(
            processorPolicy,
            startupRegistration,
            TryShowTrayNotification,
            initialLanguageOverride: initialLanguageOverride,
            preferenceStore: preferenceStore,
            initialUpdateCheckConsentOverride: initialUpdateCheckConsentOverride);
    }

    private void AttachWindowSurfaces(
        MainWindow window,
        SessionAgentEventEndpoint eventEndpoint)
    {
        WindowSurfaceSet surfaces = CreateWindowSurfaces(window, eventEndpoint);
        AttachWindowSurfaces(window, surfaces, window.InitializeAsync());
    }

    private WindowSurfaceSet CreateWindowSurfaces(
        MainWindow window,
        SessionAgentEventEndpoint eventEndpoint)
    {
        TrayDashboardWindow? dashboard = null;
        WindowsTrayBrightnessOsdHost? brightnessHost = null;
        WindowsTrayPerformanceNotificationHost? notificationHost = null;
        try
        {
            dashboard = new TrayDashboardWindow(
                window.ViewModel,
                WindowNative.GetWindowHandle(window),
                visible => window.SetTrayDashboardVisible(visible));
            dashboard.ApplyPreferences(window.TrayDashboardPreference);
            brightnessHost = WindowsTrayBrightnessOsdHost.CreateBestEffort(
                window,
                eventEndpoint,
                exception => Trace.TraceWarning(
                    "OpenBlade brightness OSD host failed: {0}",
                    exception.Message));
            notificationHost =
                WindowsTrayPerformanceNotificationHost.CreateBestEffort(
                    window,
                    eventEndpoint,
                    signal => Enqueue(() => ShowPerformanceNotification(signal)),
                    exception => Trace.TraceWarning(
                        "OpenBlade performance notification host failed: {0}",
                        exception.Message));
            return new WindowSurfaceSet(
                dashboard,
                brightnessHost,
                notificationHost);
        }
        catch
        {
            DisposeSurfaceSafely(
                "replacement performance notification host",
                notificationHost);
            DisposeSurfaceSafely("replacement brightness OSD host", brightnessHost);
            DisposeSurfaceSafely("replacement dashboard window", dashboard);
            throw;
        }
    }

    private void AttachWindowSurfaces(
        MainWindow window,
        WindowSurfaceSet surfaces,
        Task initializationTask)
    {
        window.LanguagePreferenceChanged += OnLanguagePreferenceChanged;
        window.TrayDashboardPreferenceChanged += OnTrayDashboardPreferenceChanged;
        window.TrayDashboardEditSessionChanged += OnTrayDashboardEditSessionChanged;
        mainWindow = window;
        trayDashboardWindow = surfaces.Dashboard;
        brightnessOsdHost = surfaces.BrightnessHost;
        performanceNotificationHost = surfaces.NotificationHost;
        windowInitializationTask = initializationTask;
    }

    private void DetachWindowEvents(MainWindow window)
    {
        window.LanguagePreferenceChanged -= OnLanguagePreferenceChanged;
        window.TrayDashboardPreferenceChanged -= OnTrayDashboardPreferenceChanged;
        window.TrayDashboardEditSessionChanged -= OnTrayDashboardEditSessionChanged;
    }

    private void OnLanguagePreferenceChanged(UiLanguagePreference preference)
    {
        if (Interlocked.CompareExchange(ref languageRefreshInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = RefreshLanguageAsync(preference);
    }

    private async Task RefreshLanguageAsync(
        UiLanguagePreference preference,
        Func<MainWindow, ITrayIconHost>? replacementTrayIconFactory = null,
        LiveLocalizationSmokeContext? liveLocalizationSmokeContext = null)
    {
        MainWindow? previousWindow = mainWindow;
        IStartupRegistration? registration = startupRegistration;
        SessionAgentEventEndpoint? eventEndpoint = sessionAgentEventEndpoint;
        if (previousWindow is null || registration is null || eventEndpoint is null)
        {
            Interlocked.Exchange(ref languageRefreshInProgress, 0);
            return;
        }

        bool wasVisible = previousWindow.IsWindowVisible;
        PointInt32 position = previousWindow.AppWindow.Position;
        SizeInt32 size = previousWindow.AppWindow.Size;
        bool wasMaximized = previousWindow.AppWindow.Presenter is OverlappedPresenter
        {
            State: OverlappedPresenterState.Maximized,
        };
        string previousLanguageOverride = ApplicationLanguages.PrimaryLanguageOverride;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        string previousTrayLanguage = TrayLocalization.CurrentLanguageName;
        MainWindow? replacement = null;
        WindowSurfaceSet? replacementSurfaces = null;
        ITrayIconHost? replacementTrayIcon = null;
        bool replacementCommitted = false;

        try
        {
            string replacementTrayLanguage = UiLanguagePreferencePolicy.ResolveCultureName(
                    preference)
                ?? SystemUiCulture.Name;
            using IDisposable temporaryTrayLanguage =
                TrayLocalization.BeginTemporaryLanguageOverride(replacementTrayLanguage);
            liveLocalizationSmokeContext?.Report(
                $"hide previous window for {preference}");
            previousWindow.StartHiddenObservation();
            previousWindow.AppWindow.Hide();
            ApplyRuntimeLocalizationPreference(
                preference,
                commitTrayLanguage: false);
            liveLocalizationSmokeContext?.Report(
                $"create {preference} replacement window");

            replacement = CreateMainWindow(
                registration,
                preference,
                liveLocalizationSmokeContext?.PreferenceStore,
                liveLocalizationSmokeContext is null
                    ? null
                    : UpdateCheckInterval.ManualOnly);
            Task replacementInitialization = replacement.InitializeAsync();
            liveLocalizationSmokeContext?.Report(
                $"initialize {preference} replacement window");
            await replacementInitialization;
            liveLocalizationSmokeContext?.Report(
                $"create {preference} replacement surfaces");
            replacementSurfaces = CreateWindowSurfaces(replacement, eventEndpoint);
            replacementTrayIcon = replacementTrayIconFactory?.Invoke(replacement)
                ?? CreateTrayIconHost(replacement);

            if (wasMaximized
                && replacement.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
            else
            {
                replacement.AppWindow.MoveAndResize(new RectInt32(
                    position.X,
                    position.Y,
                    size.Width,
                    size.Height));
            }

            if (exitLifecycle?.ExitRequested == true)
            {
                DisposeSurfaceSafely("replacement tray icon", replacementTrayIcon);
                replacementSurfaces.Dispose();
                liveLocalizationSmokeContext?.Report(
                    $"dispose replacement {preference} window during exit");
                await DisposeWindowSafelyAsync(replacement);
                await CloseWindowSafelyAsync(replacement);
                return;
            }

            if (wasVisible)
            {
                replacement.ShowAndActivate();
            }
            else
            {
                replacement.StartHiddenObservation();
            }

            ITrayIconHost? previousTrayIcon = trayIconHost;
            WindowSurfaceSet previousSurfaces = new(
                trayDashboardWindow,
                brightnessOsdHost,
                performanceNotificationHost);
            DetachWindowEvents(previousWindow);
            AttachWindowSurfaces(
                replacement,
                replacementSurfaces,
                replacementInitialization);
            trayIconHost = replacementTrayIcon;
            TrayLocalization.SetLanguageOverride(replacementTrayLanguage);
            replacementCommitted = true;
            replacementSurfaces = null;
            replacementTrayIcon = null;

            DisposeSurfaceSafely("previous tray icon", previousTrayIcon);
            previousSurfaces.Dispose();
            liveLocalizationSmokeContext?.Report(
                $"dispose previous window before committing {preference}");
            await DisposeWindowSafelyAsync(previousWindow);
            liveLocalizationSmokeContext?.Report(
                $"close previous window before completing {preference}");
            if (!await CloseWindowSafelyAsync(previousWindow))
            {
                throw new InvalidOperationException(
                    $"The previous window did not close while applying {preference}.");
            }
            liveLocalizationSmokeContext?.Report(
                $"previous window closed for {preference}");

            if (wasVisible)
            {
                replacement.ShowLanguageSettings();
                liveLocalizationSmokeContext?.Report(
                    $"start update checking after {preference} refresh");
                await replacement.StartUpdateCheckingAsync();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade could not apply the selected language: {0}",
                exception);
            if (replacementCommitted)
            {
                if (wasVisible && replacement is not null && !replacement.IsWindowVisible)
                {
                    replacement.ShowAndActivate();
                }
                if (liveLocalizationSmokeContext is not null)
                {
                    throw;
                }
                return;
            }
            DisposeSurfaceSafely("replacement tray icon", replacementTrayIcon);
            replacementSurfaces?.Dispose();
            if (replacement is not null)
            {
                liveLocalizationSmokeContext?.Report(
                    $"dispose failed {preference} replacement window");
                await DisposeWindowSafelyAsync(replacement);
                await CloseWindowSafelyAsync(replacement);
            }
            ApplicationLanguages.PrimaryLanguageOverride = previousLanguageOverride;
            CultureInfo.CurrentUICulture = previousUiCulture;
            LocalizedResources.SetLanguageOverride(previousUiCulture.Name);
            TrayLocalization.SetLanguageOverride(previousTrayLanguage);
            previousWindow.ShowAndActivate();
        }
        finally
        {
            Interlocked.Exchange(ref languageRefreshInProgress, 0);
        }
    }

    private bool TryShowTrayNotification(TrayNotification notification)
    {
        ITrayIconHost? host = trayIconHost;
        if (host is null)
        {
            return false;
        }

        host.ShowNotification(notification);
        return true;
    }

    private ITrayIconHost CreateTrayIconHost(MainWindow window)
    {
        ITrayIconThemeSource themeSource = trayIconThemeSource
            ?? throw new InvalidOperationException(
                "The tray icon theme source is not initialized.");

        trayExitController = new TrayExitController(
            ShowTrayExitDialog,
            ExitTrayAsync,
            ShutdownOpenBladeAsync);
        return new WinFormsTrayIconHost(
            point => Enqueue(() => _ = ToggleTrayDashboardWhenReadyAsync(point)),
            () => Enqueue(OpenMainWindowFromTrayIcon),
            () => Enqueue(() => _ = RequestTrayExitAsync()),
            window.ViewModel,
            themeSource,
            () => Enqueue(window.RequestTrayTelemetryRefresh),
            destination => Enqueue(
                () => _ = ShowNotificationDestinationAsync(destination)));
    }

    private void CreateTrayIconHost()
    {
        if (mainWindow is not null && trayIconThemeSource is not null)
        {
            trayIconHost = CreateTrayIconHost(mainWindow);
        }
    }

    private sealed record WindowSurfaceSet(
        TrayDashboardWindow? Dashboard,
        WindowsTrayBrightnessOsdHost? BrightnessHost,
        WindowsTrayPerformanceNotificationHost? NotificationHost) : IDisposable
    {
        public void Dispose()
        {
            DisposeSurfaceSafely("performance notification host", NotificationHost);
            DisposeSurfaceSafely("brightness OSD host", BrightnessHost);
            DisposeSurfaceSafely("dashboard window", Dashboard);
        }
    }

    private static void DisposeSurfaceSafely(string component, IDisposable? surface)
    {
        try
        {
            surface?.Dispose();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade {0} disposal failed: {1}",
                component,
                exception);
        }
    }

    private static async Task DisposeWindowSafelyAsync(MainWindow window)
    {
        try
        {
            await window.DisposeAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade main window disposal failed: {0}",
                exception);
        }
    }

    private static async Task<bool> CloseWindowSafelyAsync(MainWindow window)
    {
        var closed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object sender, WindowEventArgs args) => closed.TrySetResult();

        window.Closed += OnClosed;
        try
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade main window close failed: {0}",
                exception);
            return false;
        }
        finally
        {
            window.Closed -= OnClosed;
        }
    }

    private static void StartSessionAgent()
    {
        try
        {
            if (!SessionAgentLauncher.TryStartSibling())
            {
                Trace.TraceWarning(
                    "OpenBlade session agent is not present beside the tray executable.");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade session agent could not be started: {0}",
                exception);
        }
    }

    private void ShowPerformanceNotification(
        PerformanceShortcutNotificationSignal signal)
    {
        if (trayIconHost is null)
        {
            return;
        }

        NotificationCategory category = signal.Kind
            == PerformanceShortcutNotificationKind.Failed
                ? NotificationCategory.OperationFailures
                : NotificationCategory.PerformanceChanges;
        if (mainWindow?.IsNotificationEnabled(category) != true)
        {
            return;
        }

        TrayNotification notification;
        if (signal.Kind == PerformanceShortcutNotificationKind.Failed)
        {
            notification = new TrayNotification(
                LocalizedResources.Get(
                    "App_PerformanceNotification_FailedTitle",
                    "Performance mode could not be changed"),
                LocalizedResources.Get(
                    "App_PerformanceNotification_FailedBody",
                    "OpenBlade could not apply the selected mode."),
                TrayNotificationSeverity.Error,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Troubleshooting);
        }
        else
        {
            string body = signal.Preset is { } preset
                ? LocalizedResources.Format(
                    "App_PerformanceNotification_CurrentMode",
                    "Current mode: {0}",
                    PerformancePresetPresentation.Label(preset))
                : LocalizedResources.Get(
                    "App_PerformanceNotification_ChangedBody",
                    "The performance mode changed.");
            string? logoAssetRelativePath = signal.Preset is { } selectedPreset
                ? PerformanceNotificationIconAsset.Resolve(
                    mainWindow?.ViewModel.ActivePowerSource
                        ?? PowerSource.ExternalUnknown,
                    selectedPreset)
                : null;
            notification = new TrayNotification(
                LocalizedResources.Get(
                    "App_PerformanceNotification_ChangedTitle",
                    "Performance mode changed"),
                body,
                TrayNotificationSeverity.Information,
                LogoAssetRelativePath: logoAssetRelativePath,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Laptop);
        }

        trayIconHost.ShowNotification(notification);
    }

    public Task ExitTrayAsync()
    {
        serviceStartupCancellation.Cancel();
        if (exitLifecycle is null)
        {
            return Task.CompletedTask;
        }

        return exitLifecycle.ExitAsync();
    }

    private async Task TryStartServiceOnLaunchAsync()
    {
        try
        {
            await serviceStartupCoordinator!.TryStartOnceAsync(
                serviceStartupCancellation.Token);
        }
        catch (OperationCanceledException) when (serviceStartupCancellation.IsCancellationRequested)
        {
            // An exit or complete shutdown supersedes startup recovery.
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade's one-time service start attempt failed; manual recovery remains available: {0}",
                exception);
        }
    }

    internal Task<StartOpenBladeResult> StartOpenBladeAsync() =>
        ExitRequested
            ? Task.FromResult(StartOpenBladeResult.Canceled)
            : serviceStartupCoordinator?.StartAsync(CancellationToken.None)
                ?? Task.FromResult(StartOpenBladeResult.Canceled);

    private async Task<StartOpenBladeResult> StartOpenBladeCoreAsync(
        CancellationToken cancellationToken)
    {
        WindowsServiceStartPreflightResult preflight =
            await WindowsServiceStartOperation.PreflightAsync(cancellationToken);
        if (preflight == WindowsServiceStartPreflightResult.AlreadyRunning)
        {
            return StartOpenBladeResult.AlreadyRunning;
        }
        if (preflight == WindowsServiceStartPreflightResult.Unavailable)
        {
            throw new InvalidOperationException(
                "OpenBlade service is missing, inaccessible, or cannot be started from its current state.");
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var launcher = new ElevatedServiceControlLauncher();
        cancellationToken.ThrowIfCancellationRequested();
        var coordinator = new TrayServiceStartCoordinator(
            token => launcher.RequestAsync(
                executablePath,
                ElevatedServiceControlAction.Start,
                token),
            WindowsServiceStateConfirmation.WaitForRunningAsync);
        TrayServiceStartResult result = await coordinator.StartAsync(CancellationToken.None);
        if (result == TrayServiceStartResult.Canceled)
        {
            Trace.TraceInformation("OpenBlade service start elevation was canceled.");
            return StartOpenBladeResult.Canceled;
        }
        if (result == TrayServiceStartResult.Unconfirmed)
        {
            throw new InvalidOperationException(
                "The elevated service start could not be confirmed. OpenBlade did not retry the request.");
        }

        return StartOpenBladeResult.Started;
    }

    public async Task ShutdownOpenBladeAsync()
    {
        serviceStartupCancellation.Cancel();
        if (serviceStartupCoordinator is not null)
        {
            await serviceStartupCoordinator.SuspendStartsAsync();
        }
        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Tray executable path is unavailable.");
            var launcher = new ElevatedServiceControlLauncher();
            var coordinator = new TrayCompleteShutdownCoordinator(
                token => launcher.RequestAsync(
                    executablePath,
                    ElevatedServiceControlAction.Shutdown,
                    token),
                WindowsServiceStateConfirmation.WaitForStoppedAsync,
                () => _ = SessionAgentLauncher.TryStopSibling(),
                ExitTrayAsync);
            TrayCompleteShutdownResult result = await coordinator.ShutdownAsync(
                CancellationToken.None);
            if (result == TrayCompleteShutdownResult.Canceled)
            {
                Trace.TraceInformation("OpenBlade service shutdown elevation was canceled.");
                return;
            }
            if (result == TrayCompleteShutdownResult.Unconfirmed)
            {
                throw new InvalidOperationException(
                    "The elevated shutdown result could not be confirmed. OpenBlade did not retry the request.");
            }
        }
        finally
        {
            serviceStartupCoordinator?.ResumeManualStarts();
        }
    }

    internal async Task<ElevatedServiceControlOutcome> StopRazerServicesAsync()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var launcher = new ElevatedServiceControlLauncher();
        // The helper owns the post-consent deadline. A parent deadline would
        // start before UAC and could publish readback while an approved child
        // was still running.
        return await launcher.RequestAsync(
            executablePath,
            ElevatedServiceControlAction.StopRazerServices,
            CancellationToken.None);
    }

    private async Task RunRazerDriverInstallHelperAsync()
    {
        RazerDriverInstallOutcome outcome = RazerDriverInstallOutcome.Unavailable;
        try
        {
            if (RazerDriverInstallLauncher.IsInstalledExecutable(Environment.ProcessPath))
            {
                outcome = await Task.Run(() => new RazerKeyboardDriverInstaller().InstallAsync(CancellationToken.None));
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Razer driver setup failed: {0}", exception.GetType().Name);
            outcome = RazerDriverInstallOutcome.RecoveryRequired;
        }
        Environment.Exit((int)outcome);
    }

    private async Task RunElevatedServiceControlHelperAsync(
        ElevatedServiceControlAction action)
    {
        int exitCode = 1;
        try
        {
            if (action == ElevatedServiceControlAction.Start)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                await Task.Run(
                    () => WindowsServiceStartOperation.StartAsync(timeout.Token),
                    timeout.Token);
            }
            else if (action == ElevatedServiceControlAction.Shutdown)
            {
                await using var serviceClient = new ServiceClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ControlOperationResult result = await serviceClient.ShutdownOpenBladeAsync(
                    timeout.Token);
                if (result.Status != ControlOperationStatus.Stored)
                {
                    throw new InvalidOperationException(result.Message);
                }
            }
            else if (action == ElevatedServiceControlAction.StopRazerServices)
            {
                using var hardExit = new System.Threading.Timer(
                    _ => Environment.Exit(1),
                    null,
                    RazerServiceStopHardExitTimeout,
                    Timeout.InfiniteTimeSpan);
                using var timeout = new CancellationTokenSource(
                    RazerServiceStopCancellationTimeout);
                RazerServiceStopExecutionResult result = await Task.Run(
                    () => RazerServiceStopOperation.StopAllAsync(timeout.Token),
                    timeout.Token);
                if (!result.Succeeded)
                {
                    string failure = result.InventoryError
                        ?? string.Join(
                            "; ",
                            result.Failures.Select(entry =>
                                $"{entry.DisplayName}: {entry.Message}"));
                    throw new InvalidOperationException(failure);
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(action));
            }

            exitCode = 0;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade elevated service {0} failed: {1}",
                action.ToString().ToLowerInvariant(),
                exception);
        }
        finally
        {
            Environment.ExitCode = exitCode;
            traceLogRegistration?.Dispose();
            traceLogRegistration = null;
            Exit();
        }
    }

    private async Task RequestTrayExitAsync()
    {
        try
        {
            if (trayExitController is null)
            {
                Trace.TraceWarning(
                    "OpenBlade ignored a tray exit request before exit controls were initialized.");
                return;
            }

            await trayExitController.RequestExitAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade shutdown failed: {0}", exception);
            try
            {
                ShowTrayTaskDialog(new TaskDialogPage
                {
                    Caption = "OpenBlade",
                    Heading = LocalizedResources.Get(
                        "App_ExitError_Heading",
                        "OpenBlade could not shut down"),
                    Text = LocalizedResources.Format(
                        "App_ExitError_Text",
                        "OpenBlade could not shut down: {0}",
                        exception.Message),
                    Buttons = { TaskDialogButton.Close },
                    DefaultButton = TaskDialogButton.Close,
                });
            }
            catch (Exception dialogException)
            {
                Trace.TraceError(
                    "OpenBlade could not show the shutdown error dialog: {0}",
                    dialogException);
            }
        }
    }

    private TrayExitChoice ShowTrayExitDialog()
    {
        var keepRunning = new TaskDialogButton(LocalizedResources.Get(
            "App_ExitDialog_KeepRunning",
            "Keep background features running"));
        var shutDown = new TaskDialogButton(LocalizedResources.Get(
            "App_ExitDialog_ShutDown",
            "Shut down completely"));
        var page = new TaskDialogPage
        {
            Caption = "OpenBlade",
            Heading = LocalizedResources.Get(
                "App_ExitDialog_Heading",
                "Exit OpenBlade?"),
            Text = LocalizedResources.Get(
                "App_ExitDialog_Text",
                "Choose whether keyboard lighting, hotkeys, profiles, and automation should keep running in the background."),
            AllowCancel = true,
            DefaultButton = TaskDialogButton.Cancel,
            Buttons =
            {
                keepRunning,
                shutDown,
                TaskDialogButton.Cancel,
            },
        };

        TaskDialogButton result = ShowTrayTaskDialog(page);
        if (result == keepRunning)
        {
            return TrayExitChoice.KeepBackgroundRunning;
        }

        if (result == shutDown)
        {
            return TrayExitChoice.ShutDownCompletely;
        }

        return TrayExitChoice.Cancel;
    }

    private TaskDialogButton ShowTrayTaskDialog(TaskDialogPage page)
    {
        MainWindow? window = mainWindow;
        if (window?.IsWindowVisible == true)
        {
            return TaskDialog.ShowDialog(
                WindowNative.GetWindowHandle(window),
                page,
                TaskDialogStartupLocation.CenterOwner);
        }

        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }

    private void ShowMainWindow() => _ = ShowMainWindowWhenReadyAsync(initialLaunch: false);

    private void OpenMainWindowFromTrayIcon()
    {
        trayDashboardWindow?.DismissForMainWindow();
        ShowMainWindow();
    }

    private async Task ToggleTrayDashboardWhenReadyAsync(TrayDashboardPoint point)
    {
        try
        {
            await windowInitializationTask;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard initialization failed: {0}",
                exception);
        }

        if (exitLifecycle is null)
        {
            return;
        }

        exitLifecycle.TryRunWhileActive(() => trayDashboardWindow?.ToggleAt(point));
    }

    private void OnTrayDashboardPreferenceChanged(TrayDashboardPreference preference) =>
        trayDashboardWindow?.ApplyPreferences(preference);

    private void OnTrayDashboardEditSessionChanged(bool editing)
    {
        if (editing)
        {
            trayDashboardWindow?.BeginEditSession();
        }
        else
        {
            trayDashboardWindow?.EndEditSession();
        }
    }

    private async Task ShowNotificationDestinationAsync(
        TrayNotificationDestination destination)
    {
        await ShowMainWindowWhenReadyAsync(initialLaunch: false);
        mainWindow?.NavigateToNotificationDestination(destination);
    }

    private void ShowMainWindow(bool initialLaunch) =>
        _ = ShowMainWindowWhenReadyAsync(initialLaunch);

    private async Task ShowMainWindowWhenReadyAsync(bool initialLaunch)
    {
        try
        {
            await windowInitializationTask;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade window initialization failed; continuing with the usable shell: {0}",
                exception);
        }

        if (exitLifecycle is null)
        {
            return;
        }

        MainWindow? window = mainWindow;
        if (window is null)
        {
            return;
        }

        if (initialLaunch
            && window.StartMinimized
            && !window.UpdateViewModel.NeedsConsentPrompt
            && !window.ViewModel.ShouldForceWindowVisibleForThermalDisclosure)
        {
            window.StartHiddenObservation();
            await window.StartUpdateCheckingAsync();
            return;
        }

        bool shown = false;
        exitLifecycle.TryRunWhileActive(() =>
        {
            window.ShowAndActivate();
            shown = true;
        });
        if (shown)
        {
            await window.StartUpdateCheckingAsync();
        }
    }

    private void Enqueue(Action action)
    {
        if (!uiDispatcherQueue.TryEnqueue(() => action()))
        {
            // Shutdown won the race; no UI work remains valid.
        }
    }

    private void OnXamlUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteFatalCrashReport("WinUI unhandled exception", args.Exception);
        // Intentionally leave Handled false so WinUI terminates normally.
    }

    private void RegisterCrashReporting(string componentName)
    {
        if (crashReportWriter is not null)
        {
            return;
        }

        crashReportWriter = CrashReportWriter.CreateDefault(
            componentName,
            OpenBladeProductVersion.Read(typeof(App).Assembly));
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnAppDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        Exception exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException(
                $"Unhandled non-Exception object of type "
                    + (args.ExceptionObject?.GetType().FullName ?? "<null>"));
        WriteFatalCrashReport("AppDomain unhandled exception", exception);
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        crashReportWriter?.TryWrite(
            "Unobserved task exception",
            args.Exception,
            processTerminating: false);
        // Do not call SetObserved; reporting must not change failure semantics.
    }

    private void WriteFatalCrashReport(string source, Exception exception)
    {
        if (Interlocked.CompareExchange(ref fatalCrashReported, 1, 0) != 0)
        {
            return;
        }

        if (crashReportWriter?.TryWrite(source, exception, processTerminating: true) is null)
        {
            Interlocked.Exchange(ref fatalCrashReported, 0);
        }
    }
}
