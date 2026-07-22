using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public partial class App : Microsoft.UI.Xaml.Application
{
    private CrashReportWriter? crashReportWriter;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;
    private MainWindow? mainWindow;
    private WindowsShellThemeSource? trayIconThemeSource;
    private ITrayIconHost? trayIconHost;
    private TrayExitLifecycle? exitLifecycle;
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private int fatalCrashReported;
    private int dashboardRestartInProgress;
    private bool dashboardRestarting;

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        RegisterCrashReporting();
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        bool restartedDashboard = WaitForDashboardRestartParent();

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

        mainWindow = CreateMainWindow();
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
                    if (!dashboardRestarting && mainWindow is not null)
                    {
                        await mainWindow.DisposeAsync();
                    }
                },
                CloseWindow: () =>
                {
                    if (!dashboardRestarting)
                    {
                        mainWindow?.Close();
                    }

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
                    if (dashboardRestarting)
                    {
                        Environment.Exit(0);
                    }

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
        ShowMainWindow(initialLaunch: !restartedDashboard);
    }

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow();
        window.DashboardLayoutRestartRequested += OnDashboardLayoutRestartRequested;
        return window;
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

    private async void OnDashboardLayoutRestartRequested(object? sender, EventArgs args)
    {
        if (Interlocked.CompareExchange(ref dashboardRestartInProgress, 1, 0) != 0
            || exitLifecycle is null
            || exitLifecycle.ExitRequested)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (exitLifecycle is null || exitLifecycle.ExitRequested)
            {
                return;
            }

            MainWindow? currentWindow = mainWindow;
            string? executablePath = Environment.ProcessPath;
            if (currentWindow is null || string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            await currentWindow.FlushPreferencesForRestartAsync();
            dashboardRestarting = true;
            var restart = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
            };
            restart.ArgumentList.Add($"--restart-after={Environment.ProcessId}");
            using Process replacement = Process.Start(restart)
                ?? throw new InvalidOperationException(
                    "The replacement OpenBlade tray could not be started.");

            await ExitTrayAsync();
        }
        catch (Exception exception)
        {
            dashboardRestarting = false;
            Trace.TraceError("OpenBlade Dashboard restart failed: {0}", exception);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref dashboardRestartInProgress, 0);
        }
    }

    private static bool WaitForDashboardRestartParent()
    {
        const string prefix = "--restart-after=";
        string? argument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is null
            || !int.TryParse(argument[prefix.Length..], out int parentProcessId)
            || parentProcessId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            parent.WaitForExit(milliseconds: 15_000);
        }
        catch (ArgumentException)
        {
            // The previous tray has already exited.
        }

        return true;
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

        if (initialLaunch && mainWindow?.StartMinimized == true)
        {
            return;
        }

        exitLifecycle.TryRunWhileActive(() => mainWindow?.ShowAndActivate());
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
