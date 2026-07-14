using System.Windows;
using System.Security.Principal;
using OpenSynapse.Contracts;
using Forms = System.Windows.Forms;

namespace OpenSynapse.Tray;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? trayIcon;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;

    public bool ExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        TrayInstanceEndpoint instanceEndpoint = TrayInstanceEndpoint.Create(
            executablePath,
            userSid);
        if (!TrayInstanceCoordinator.TryStart(
            instanceEndpoint,
            () => Dispatcher.BeginInvoke(ShowMainWindow),
            out instanceCoordinator))
        {
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();

        shutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            TrayShutdownEndpoint.ForExecutablePath(executablePath));
        shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            shutdownEvent,
            (_, _) => Dispatcher.BeginInvoke(ExitTray),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitTray());
        trayIcon = new Forms.NotifyIcon
        {
            Text = "OpenSynapse",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceCoordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        shutdownRegistration?.Unregister(null);
        shutdownEvent?.Dispose();
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        MainWindow?.Show();
        MainWindow?.Activate();
    }

    private void ExitTray()
    {
        if (ExitRequested)
        {
            return;
        }

        ExitRequested = true;
        MainWindow?.Close();
        Shutdown();
    }
}
