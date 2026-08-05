using System.ComponentModel;

namespace OpenBlade.Tray;

public sealed class WinFormsTrayIconHost : ITrayIconHost
{
    private readonly ContextMenuStrip menu;
    private readonly NotifyIcon trayIcon;
    private readonly MainViewModel viewModel;
    private readonly ITrayIconThemeSource themeSource;
    private readonly TrayHoverTelemetryRefreshGate hoverTelemetryRefreshGate = new();
    private readonly Action hoverTelemetryRefreshRequested;
    private readonly WindowsAppNotificationPresenter? notificationPresenter;
    private long liveReadingsObservationIntervalTicks;
    private Icon? currentIcon;
    private int disposed;

    public WinFormsTrayIconHost(
        Action openTray,
        Action exitTray,
        MainViewModel viewModel,
        ITrayIconThemeSource themeSource,
        Action hoverTelemetryRefreshRequested)
    {
        ArgumentNullException.ThrowIfNull(openTray);
        ArgumentNullException.ThrowIfNull(exitTray);
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
        this.hoverTelemetryRefreshRequested = hoverTelemetryRefreshRequested
            ?? throw new ArgumentNullException(nameof(hoverTelemetryRefreshRequested));
        liveReadingsObservationIntervalTicks = viewModel.LiveReadingsObservationInterval.Ticks;

        menu = BuildTrayMenu(openTray, exitTray);
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
        trayIcon.DoubleClick += (_, _) => openTray();
        trayIcon.MouseMove += OnTrayIconMouseMove;
        notificationPresenter = WindowsAppNotificationPresenter.CreateBestEffort(
            exception => System.Diagnostics.Trace.TraceWarning(
                "OpenBlade app notification presenter failed: {0}",
                exception.Message));
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
        trayIcon.MouseMove -= OnTrayIconMouseMove;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        notificationPresenter?.Dispose();
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

        if (notificationPresenter?.TryShow(notification) == true)
        {
            return;
        }

        trayIcon.ShowBalloonTip(
            timeout: 2000,
            notification.Title,
            notification.Body,
            ToolTipIcon.None);
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (args.PropertyName == nameof(MainViewModel.LiveReadingsObservationInterval))
        {
            Interlocked.Exchange(
                ref liveReadingsObservationIntervalTicks,
                viewModel.LiveReadingsObservationInterval.Ticks);
        }
        else if (args.PropertyName == nameof(MainViewModel.TrayHoverLabelText))
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

    private void OnTrayIconMouseMove(object? sender, MouseEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0
            && hoverTelemetryRefreshGate.TryAcquire(
                TimeSpan.FromTicks(Interlocked.Read(
                    ref liveReadingsObservationIntervalTicks))))
        {
            hoverTelemetryRefreshRequested();
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

    private static ContextMenuStrip BuildTrayMenu(Action openTray, Action exitTray)
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open");
        open.Click += (_, _) => openTray();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => exitTray();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }
}
