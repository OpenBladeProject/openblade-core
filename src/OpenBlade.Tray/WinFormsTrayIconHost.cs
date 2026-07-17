using System.ComponentModel;

namespace OpenBlade.Tray;

public sealed class WinFormsTrayIconHost : ITrayIconHost
{
    private readonly ContextMenuStrip menu;
    private readonly NotifyIcon trayIcon;
    private readonly MainViewModel viewModel;
    private int disposed;

    public WinFormsTrayIconHost(
        TrayMenuController controller,
        MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(controller);
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        menu = BuildTrayMenu(controller);
        TrayMenuItem open = controller.Find("Open");
        trayIcon = new NotifyIcon
        {
            Text = viewModel.TrayHoverLabelText,
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => open.Invoke();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        menu.Dispose();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0
            && args.PropertyName == nameof(MainViewModel.TrayHoverLabelText))
        {
            trayIcon.Text = viewModel.TrayHoverLabelText;
        }
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
