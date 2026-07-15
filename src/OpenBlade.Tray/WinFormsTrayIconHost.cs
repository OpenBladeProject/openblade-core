namespace OpenBlade.Tray;

public sealed class WinFormsTrayIconHost : ITrayIconHost
{
    private readonly ContextMenuStrip menu;
    private readonly NotifyIcon trayIcon;
    private int disposed;

    public WinFormsTrayIconHost(TrayMenuController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        menu = BuildTrayMenu(controller);
        TrayMenuItem open = controller.Find("Open");
        trayIcon = new NotifyIcon
        {
            Text = "OpenBlade",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => open.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        trayIcon.Visible = false;
        trayIcon.Dispose();
        menu.Dispose();
    }

    private static ContextMenuStrip BuildTrayMenu(TrayMenuController controller)
    {
        var menu = new ContextMenuStrip();
        var bindings = new List<(TrayMenuItem Model, ToolStripMenuItem View)>();
        TrayMenuGroup? previousGroup = null;
        foreach (TrayMenuItem item in controller.Items)
        {
            if (previousGroup is not null && previousGroup != item.Group)
            {
                menu.Items.Add(new ToolStripSeparator());
            }

            TrayMenuItem captured = item;
            var view = new ToolStripMenuItem(item.Label);
            view.Click += (_, _) => captured.Invoke();
            menu.Items.Add(view);
            bindings.Add((item, view));
            previousGroup = item.Group;
        }

        menu.Opening += (_, _) =>
        {
            foreach ((TrayMenuItem model, ToolStripMenuItem view) in bindings)
            {
                view.Enabled = model.IsEnabled;
            }
        };
        return menu;
    }
}
