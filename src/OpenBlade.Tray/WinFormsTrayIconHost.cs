using System.ComponentModel;

namespace OpenBlade.Tray;

public sealed class WinFormsTrayIconHost : ITrayIconHost
{
    private readonly ContextMenuStrip menu;
    private readonly NotifyIcon trayIcon;
    private readonly MainViewModel viewModel;
    private readonly ITrayIconThemeSource themeSource;
    private Icon? currentIcon;
    private int disposed;

    public WinFormsTrayIconHost(
        TrayMenuController controller,
        MainViewModel viewModel,
        ITrayIconThemeSource themeSource)
    {
        ArgumentNullException.ThrowIfNull(controller);
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));

        menu = BuildTrayMenu(controller);
        TrayMenuItem open = controller.Find("Open");
        currentIcon = OpenBladeIconAssets.LoadTrayIcon(
            viewModel.TrayIconState,
            themeSource.UseLightTrayIconGlyphs);
        trayIcon = new NotifyIcon
        {
            Text = viewModel.TrayHoverLabelText,
            Icon = currentIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => open.Invoke();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        themeSource.TrayIconThemeChanged += OnTrayIconThemeChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        themeSource.TrayIconThemeChanged -= OnTrayIconThemeChanged;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        currentIcon?.Dispose();
        currentIcon = null;
        menu.Dispose();
    }

    public void ShowNotification(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        ToolTipIcon icon = notification.Severity == TrayNotificationSeverity.Error
            ? ToolTipIcon.Error
            : ToolTipIcon.Info;
        trayIcon.ShowBalloonTip(
            timeout: 2000,
            notification.Title,
            notification.Body,
            icon);
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (args.PropertyName == nameof(MainViewModel.TrayHoverLabelText))
        {
            trayIcon.Text = viewModel.TrayHoverLabelText;
        }
        else if (args.PropertyName == nameof(MainViewModel.TrayIconState))
        {
            UpdateTrayIcon();
        }
    }

    private void OnTrayIconThemeChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            UpdateTrayIcon();
        }
    }

    private void UpdateTrayIcon()
    {
        Icon replacement = OpenBladeIconAssets.LoadTrayIcon(
            viewModel.TrayIconState,
            themeSource.UseLightTrayIconGlyphs);
        Icon? previous = currentIcon;
        currentIcon = replacement;
        trayIcon.Icon = replacement;
        previous?.Dispose();
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
                view.Visible = model.IsVisible;
                view.Enabled = model.IsEnabled;
            }
        };
        return menu;
    }
}
