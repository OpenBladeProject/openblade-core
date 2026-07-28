using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public partial class App : Microsoft.UI.Xaml.Application
{
    private CrashReportWriter? crashReportWriter;
    private IDisposable? traceLogRegistration;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;
    private MainWindow? mainWindow;
    private WindowsTrayBrightnessOsdHost? brightnessOsdHost;
    private WindowsTrayPerformanceNotificationHost? performanceNotificationHost;
    private WindowsShellThemeSource? trayIconThemeSource;
    private ITrayIconHost? trayIconHost;
    private TrayExitLifecycle? exitLifecycle;
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private int fatalCrashReported;

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        RegisterCrashReporting();
        traceLogRegistration ??= PersistentTraceLog.RegisterDefault(
            "OpenBlade.Tray",
            OpenBladeProductVersion.Read(typeof(App).Assembly));
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        TrayInstanceEndpoint instanceEndpoint = TrayInstanceEndpoint.Create(userSid);
        if (!TrayInstanceCoordinator.TryStart(
            instanceEndpoint,
            () => Enqueue(ShowMainWindow),
            out instanceCoordinator))
        {
            Exit();
            return;
        }

        mainWindow = CreateMainWindow();
        SessionAgentEventEndpoint sessionEventEndpoint =
            SessionAgentEventEndpoint.Create(new SecurityIdentifier(userSid));
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

    private MainWindow CreateMainWindow()
    {
        return new MainWindow();
    }

    private void CreateTrayIconHost()
    {
        if (mainWindow is null || trayIconThemeSource is null)
        {
            return;
        }

        var trayMenuController = new TrayMenuController(
            mainWindow.ViewModel,
            () => Enqueue(ShowMainWindow),
            () => Enqueue(() => _ = ExitTrayAsync()));
        trayIconHost = new WinFormsTrayIconHost(
            trayMenuController,
            mainWindow.ViewModel,
            trayIconThemeSource);
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

        TrayNotification notification;
        if (signal.Kind == PerformanceShortcutNotificationKind.Failed)
        {
            notification = new TrayNotification(
                "Performance mode could not be changed",
                "OpenBlade could not apply the selected mode.",
                TrayNotificationSeverity.Error);
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
                logoAssetRelativePath);
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

    private void ShowMainWindow() => _ = ShowMainWindowWhenReadyAsync(initialLaunch: false);

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

    private void RegisterCrashReporting()
    {
        if (crashReportWriter is not null)
        {
            return;
        }

        crashReportWriter = CrashReportWriter.CreateDefault(
            "OpenBlade.Tray",
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
