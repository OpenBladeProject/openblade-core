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
    private Task? exitTask;
    private DispatcherQueue uiDispatcherQueue = null!;

    public bool ExitRequested { get; private set; }

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
        trayIconHost = new WinFormsTrayIconHost(trayMenuController);
        ShowMainWindow();
    }

    public Task ExitTrayAsync() => exitTask ??= ExitTrayCoreAsync();

    private async Task ExitTrayCoreAsync()
    {
        ExitRequested = true;
        shutdownRegistration?.Unregister(null);
        shutdownEvent?.Dispose();
        trayIconHost?.Dispose();
        if (mainWindow is not null)
        {
            await mainWindow.DisposeAsync();
            mainWindow.Close();
        }

        if (instanceCoordinator is not null)
        {
            await instanceCoordinator.DisposeAsync();
        }

        Exit();
    }

    private void ShowMainWindow() => mainWindow?.ShowAndActivate();

    private void Enqueue(Action action)
    {
        if (!uiDispatcherQueue.TryEnqueue(() => action()))
        {
            // Shutdown won the race; no UI work remains valid.
        }
    }
}
