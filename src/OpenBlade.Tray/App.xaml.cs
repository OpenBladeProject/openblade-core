using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public partial class App : Microsoft.UI.Xaml.Application
{
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;
    private MainWindow? mainWindow;
    private ITrayIconHost? trayIconHost;
    private TrayExitLifecycle? exitLifecycle;
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        TrayInstanceEndpoint instanceEndpoint = TrayInstanceEndpoint.Create(
            executablePath,
            userSid);
        if (!TrayInstanceCoordinator.TryStart(
            instanceEndpoint,
            () => Enqueue(ShowMainWindow),
            out instanceCoordinator))
        {
            Exit();
            return;
        }

        mainWindow = new MainWindow();
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
                },
                DisposeViewModelAsync: async () =>
                {
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
                ExitApplication: Exit),
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

        var trayMenuController = new TrayMenuController(
            mainWindow.ViewModel,
            () => Enqueue(ShowMainWindow),
            () => Enqueue(() => _ = ExitTrayAsync()));
        trayIconHost = new WinFormsTrayIconHost(
            trayMenuController,
            mainWindow.ViewModel);
        ShowMainWindow();
    }

    public Task ExitTrayAsync()
    {
        if (exitLifecycle is null)
        {
            return Task.CompletedTask;
        }

        return exitLifecycle.ExitAsync();
    }

    private void ShowMainWindow() => _ = ShowMainWindowWhenReadyAsync();

    private async Task ShowMainWindowWhenReadyAsync()
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

        exitLifecycle.TryRunWhileActive(() => mainWindow?.ShowAndActivate());
    }

    private void Enqueue(Action action)
    {
        if (!uiDispatcherQueue.TryEnqueue(() => action()))
        {
            // Shutdown won the race; no UI work remains valid.
        }
    }
}
