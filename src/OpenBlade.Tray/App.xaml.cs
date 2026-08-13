using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Session;
using WinRT.Interop;

namespace OpenBlade.Tray;

internal enum StartOpenBladeResult
{
    Started,
    AlreadyRunning,
    Canceled,
}

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string PerformanceNotificationReplacementTag = "performance";

    private CrashReportWriter? crashReportWriter;
    private IDisposable? traceLogRegistration;
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
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private WindowsProcessorEfficiencyPolicy processorPolicy = null!;
    private int fatalCrashReported;

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        string[] commandLineArguments = Environment.GetCommandLineArgs();
        ElevatedServiceHelperParseResult helperParseResult =
            ElevatedServiceControlLauncher.ParseHelperAction(
            commandLineArguments,
            out ElevatedServiceControlAction helperAction);
        bool elevatedServiceHelper = helperParseResult == ElevatedServiceHelperParseResult.Valid;
        string componentName = elevatedServiceHelper
            ? helperAction == ElevatedServiceControlAction.Start
                ? "OpenBlade.Tray.StartHelper"
                : "OpenBlade.Tray.ShutdownHelper"
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
        var startupRegistration = new WindowsStartupRegistration();
        if (!startupRegistration.ShouldStartForLaunchArguments(
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

        mainWindow = CreateMainWindow(startupRegistration);
        trayDashboardWindow = new TrayDashboardWindow(
            mainWindow.ViewModel,
            WindowNative.GetWindowHandle(mainWindow),
            visible => mainWindow.SetTrayDashboardVisible(visible));
        trayDashboardWindow.ApplyPreferences(mainWindow.TrayDashboardPreference);
        mainWindow.TrayDashboardPreferenceChanged += OnTrayDashboardPreferenceChanged;
        mainWindow.TrayDashboardEditSessionChanged += OnTrayDashboardEditSessionChanged;
        SessionAgentEventEndpoint sessionEventEndpoint =
            SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(userSid),
                sessionId);
        brightnessOsdHost = WindowsTrayBrightnessOsdHost.CreateBestEffort(
            mainWindow,
            sessionEventEndpoint,
            exception => Trace.TraceWarning(
                "OpenBlade brightness OSD host failed: {0}",
                exception.Message));
        performanceNotificationHost =
            WindowsTrayPerformanceNotificationHost.CreateBestEffort(
                mainWindow,
                sessionEventEndpoint,
                signal => Enqueue(() => ShowPerformanceNotification(signal)),
                exception => Trace.TraceWarning(
                    "OpenBlade performance notification host failed: {0}",
                    exception.Message));
        windowInitializationTask = mainWindow.InitializeAsync();

        exitLifecycle = new TrayExitLifecycle(
            new TrayExitSteps(
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
    }

    private MainWindow CreateMainWindow(IStartupRegistration startupRegistration)
    {
        return new MainWindow(
            processorPolicy,
            startupRegistration,
            TryShowTrayNotification);
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

    private void CreateTrayIconHost()
    {
        if (mainWindow is null || trayIconThemeSource is null)
        {
            return;
        }

        trayExitController = new TrayExitController(
            ShowTrayExitDialog,
            ExitTrayAsync,
            ShutdownOpenBladeAsync);
        trayIconHost = new WinFormsTrayIconHost(
            point => Enqueue(() => _ = ToggleTrayDashboardWhenReadyAsync(point)),
            () => Enqueue(OpenMainWindowFromTrayIcon),
            () => Enqueue(() => _ = RequestTrayExitAsync()),
            mainWindow.ViewModel,
            trayIconThemeSource,
            () => Enqueue(mainWindow.RequestTrayTelemetryRefresh),
            destination => Enqueue(
                () => _ = ShowNotificationDestinationAsync(destination)));
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
                "Performance mode could not be changed",
                "OpenBlade could not apply the selected mode.",
                TrayNotificationSeverity.Error,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Troubleshooting);
        }
        else
        {
            string body = signal.Preset is { } preset
                ? $"Current mode: {PerformancePresetPresentation.Label(preset)}"
                : "The performance mode changed.";
            string? logoAssetRelativePath = signal.Preset is { } selectedPreset
                ? PerformanceNotificationIconAsset.Resolve(
                    mainWindow?.ViewModel.ActivePowerSource
                        ?? PowerSource.ExternalUnknown,
                    selectedPreset)
                : null;
            notification = new TrayNotification(
                "Performance mode changed",
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
        if (exitLifecycle is null)
        {
            return Task.CompletedTask;
        }

        return exitLifecycle.ExitAsync();
    }

    internal async Task<StartOpenBladeResult> StartOpenBladeAsync()
    {
        WindowsServiceStartPreflightResult preflight =
            await WindowsServiceStartOperation.PreflightAsync(CancellationToken.None);
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
            else
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
                    Heading = "OpenBlade could not shut down",
                    Text = exception.Message,
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
        var keepRunning = new TaskDialogButton("Keep background features running");
        var shutDown = new TaskDialogButton("Shut down completely");
        var page = new TaskDialogPage
        {
            Caption = "OpenBlade",
            Heading = "Exit OpenBlade?",
            Text = "Choose whether keyboard lighting, hotkeys, profiles, and automation "
                + "should keep running in the background.",
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
            && !window.UpdateViewModel.NeedsConsentPrompt)
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
