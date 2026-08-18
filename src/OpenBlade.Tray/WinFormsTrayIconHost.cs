using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray;

public sealed class WinFormsTrayIconHost : ITrayIconHost
{
    private readonly ContextMenuStrip menu;
    private readonly NotifyIcon trayIcon;
    private readonly MainViewModel viewModel;
    private readonly ITrayIconThemeSource themeSource;
    private readonly TrayHoverTelemetryRefreshGate hoverTelemetryRefreshGate = new();
    private readonly Action hoverTelemetryRefreshRequested;
    private readonly Action<TrayDashboardPoint> openDashboard;
    private readonly Action openTray;
    private readonly Action<TrayNotificationDestination> notificationInvoked;
    private readonly WindowsAppNotificationPresenter? notificationPresenter;
    private readonly NotificationDeduplicator notificationDeduplicator = new();
    private long liveReadingsObservationIntervalTicks;
    private long lastDoubleClickTimestamp;
    private Icon? currentIcon;
    private TrayNotificationDestination lastBalloonDestination;
    private int disposed;

    public WinFormsTrayIconHost(
        Action<TrayDashboardPoint> openDashboard,
        Action openTray,
        Action exitTray,
        MainViewModel viewModel,
        ITrayIconThemeSource themeSource,
        Action hoverTelemetryRefreshRequested,
        Action<TrayNotificationDestination> notificationInvoked)
    {
        this.openDashboard = openDashboard
            ?? throw new ArgumentNullException(nameof(openDashboard));
        this.openTray = openTray ?? throw new ArgumentNullException(nameof(openTray));
        ArgumentNullException.ThrowIfNull(exitTray);
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.themeSource = themeSource ?? throw new ArgumentNullException(nameof(themeSource));
        this.hoverTelemetryRefreshRequested = hoverTelemetryRefreshRequested
            ?? throw new ArgumentNullException(nameof(hoverTelemetryRefreshRequested));
        this.notificationInvoked = notificationInvoked
            ?? throw new ArgumentNullException(nameof(notificationInvoked));
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
        trayIcon.MouseClick += OnTrayIconMouseClick;
        trayIcon.MouseDoubleClick += OnTrayIconMouseDoubleClick;
        trayIcon.MouseMove += OnTrayIconMouseMove;
        trayIcon.BalloonTipClicked += OnBalloonTipClicked;
        notificationPresenter = WindowsAppNotificationPresenter.CreateBestEffort(
            notificationInvoked,
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
        trayIcon.MouseClick -= OnTrayIconMouseClick;
        trayIcon.MouseDoubleClick -= OnTrayIconMouseDoubleClick;
        trayIcon.MouseMove -= OnTrayIconMouseMove;
        trayIcon.BalloonTipClicked -= OnBalloonTipClicked;
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

        if (!string.IsNullOrWhiteSpace(notification.ReplacementTag)
            && !notificationDeduplicator.ShouldEmit(
                notification.ReplacementTag,
                notification.Title,
                notification.Body))
        {
            return;
        }

        if (notificationPresenter?.TryShow(notification) == true)
        {
            return;
        }

        lastBalloonDestination = notification.Destination;
        trayIcon.ShowBalloonTip(
            timeout: 2000,
            notification.Title,
            notification.Body,
            ToolTipIcon.None);
    }

    private void OnBalloonTipClicked(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            notificationInvoked(lastBalloonDestination);
        }
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

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs args)
    {
        TrayIconPointerButton button = ToTrayIconPointerButton(args.Button);
        if (button == TrayIconPointerButton.Left
            && ShouldSuppressTrailingLeftClick())
        {
            return;
        }

        if (Volatile.Read(ref disposed) == 0
            && TrayIconActivationPolicy.Resolve(button)
                == TrayIconActivation.ShowDashboard)
        {
            System.Drawing.Point cursorPosition = Cursor.Position;
            openDashboard(new TrayDashboardPoint(cursorPosition.X, cursorPosition.Y));
        }
    }

    private void OnTrayIconMouseDoubleClick(object? sender, MouseEventArgs args)
    {
        TrayIconPointerButton button = ToTrayIconPointerButton(args.Button);
        if (Volatile.Read(ref disposed) == 0
            && TrayIconActivationPolicy.ResolveDoubleClick(button)
                == TrayIconActivation.OpenMainWindow)
        {
            Interlocked.Exchange(
                ref lastDoubleClickTimestamp,
                Stopwatch.GetTimestamp());
            openTray();
        }
    }

    private bool ShouldSuppressTrailingLeftClick()
    {
        long timestamp = Interlocked.Exchange(ref lastDoubleClickTimestamp, 0);
        return timestamp != 0
            && TrayIconActivationPolicy.ShouldSuppressTrailingClick(
                Stopwatch.GetElapsedTime(timestamp),
                TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime));
    }

    private static TrayIconPointerButton ToTrayIconPointerButton(MouseButtons button) =>
        button switch
        {
            MouseButtons.Left => TrayIconPointerButton.Left,
            MouseButtons.Right => TrayIconPointerButton.Right,
            MouseButtons.Middle => TrayIconPointerButton.Middle,
            _ => TrayIconPointerButton.Other,
        };

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
        var open = new ToolStripMenuItem(LocalizedResources.Get(
            "TrayIconMenu_Open",
            "Open"))
        {
            ShortcutKeyDisplayString = LocalizedResources.Get(
                "TrayIconMenu_DoubleClick",
                "Double-click"),
        };
        open.Click += (_, _) => openTray();
        var exit = new ToolStripMenuItem(LocalizedResources.Get(
            "TrayIconMenu_Exit",
            "Exit"));
        exit.Click += (_, _) => exitTray();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }
}
