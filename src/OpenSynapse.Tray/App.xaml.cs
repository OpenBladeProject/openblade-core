using System.Windows;
using Forms = System.Windows.Forms;

namespace OpenSynapse.Tray;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? trayIcon;

    public bool ExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        ExitRequested = true;
        MainWindow?.Close();
        Shutdown();
    }
}
