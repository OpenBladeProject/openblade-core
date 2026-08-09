using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class TrayDashboardWindow : Window, IDisposable
{
    private const int PopupWidth = 360;
    private const uint MonitorDefaultToNearest = 2;
    private const uint WindowMessageNonClientDestroy = 0x0082;
    private const nuint RevealSubclassId = 0x4F425244; // "OBRD"

    private static readonly ConcurrentDictionary<nint, TrayDashboardWindow>
        RevealHosts = new();
    private static readonly SubclassProcedure RevealSubclassCallback =
        ProcessRevealWindowMessage;

    private readonly Action openMainWindow;
    private readonly Action<bool> visibilityChanged;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer foregroundTimer;
    private readonly nint hostWindowHandle;
    private readonly uint advanceRevealMessage;
    private TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;
    private TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        TrayDashboardPreferenceDefaults.Preference.Controls!);
    private bool changingPerformance;
    private bool changingFans;
    private bool changingBattery;
    private TrayDashboardControlKind? detailControl;
    private bool pinnedForEditing;
    private bool visible;
    private TrayDashboardPoint visibleAnchor;
    private DisplayArea? visibleDisplayArea;
    private long lastDeactivationHideTimestamp;
    private long revealStartedTimestamp;
    private nint revealWindowHandle;
    private TrayDashboardRect revealPlacement;
    private int revealStartY;
    private int revealStagingY;
    private int revealCurrentY;
    private int revealPrimingFrames;
    private bool revealAnimationsEnabled;
    private bool revealAnimationActive;
    private bool revealHiding;
    private bool awaitingRevealActivation;
    private bool surfaceShown;
    private nint previousForegroundWindow;
    private bool foregroundEstablished;
    private CancellationTokenSource? revealFrameClockCancellation;
    private int revealFramePending;
    private bool revealSubclassAttached;
    private int disposed;

    public TrayDashboardWindow(
        MainViewModel viewModel,
        Action openMainWindow,
        Action<bool> visibilityChanged)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.openMainWindow = openMainWindow
            ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.visibilityChanged = visibilityChanged
            ?? throw new ArgumentNullException(nameof(visibilityChanged));
        InitializeComponent();
        Root.DataContext = ViewModel;
        dispatcherQueue = Root.DispatcherQueue;
        foregroundTimer = dispatcherQueue.CreateTimer();
        foregroundTimer.Interval = TimeSpan.FromMilliseconds(50);
        foregroundTimer.IsRepeating = true;
        foregroundTimer.Tick += OnForegroundTimerTick;
        hostWindowHandle = WindowNative.GetWindowHandle(this);
        advanceRevealMessage = RegisterWindowMessage(
            "OpenBlade.TrayDashboard.AdvanceReveal.v1");
        TryAttachRevealSubclass();

        ThemeManager.Apply(Root, ViewModel.Theme);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        AppWindow.IsShownInSwitchers = false;
        try
        {
            OverlappedPresenter presenter = OverlappedPresenter.CreateForToolWindow();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            AppWindow.SetPresenter(presenter);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard presenter setup failed: {0}",
                exception.Message);
        }

        ApplyPreferences(preference);
        TryPrimeSurface();
    }

    public MainViewModel ViewModel { get; }

    public void ApplyPreferences(TrayDashboardPreference preferences)
    {
        preference = TrayDashboardPreferenceDefaults.Normalize(preferences);
        layout = TrayDashboardLayoutPolicy.Resolve(preference.Controls!);
        IReadOnlyDictionary<TrayDashboardControlKind, FrameworkElement> tiles =
            TilesByKind();
        foreach (FrameworkElement tile in tiles.Values)
        {
            tile.Visibility = Visibility.Collapsed;
        }

        ControlGrid.RowDefinitions.Clear();
        foreach (TrayDashboardControlPlacement placement in layout.Controls)
        {
            FrameworkElement tile = tiles[placement.Kind];
            EnsureRow(placement.Row);
            Grid.SetRow(tile, placement.Row);
            Grid.SetColumn(tile, placement.Column);
            Grid.SetColumnSpan(tile, placement.ColumnSpan);
            tile.Visibility = Visibility.Visible;
        }

        if (detailControl is { } selected
            && preference.Controls!.All(control =>
                control.Kind != selected || !control.IsVisible))
        {
            ShowDashboardPage();
        }
        IReadOnlyDictionary<TrayDashboardStatusItemKind, FrameworkElement> statusItems =
            StatusItemsByKind();
        StatusItemsHost.ColumnDefinitions.Clear();
        int statusColumn = 0;
        foreach (TrayDashboardStatusItemPreference status in preference.StatusItems!)
        {
            FrameworkElement statusItem = statusItems[status.Kind];
            statusItem.Visibility = status.IsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (status.IsVisible)
            {
                StatusItemsHost.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                });
                Grid.SetColumn(statusItem, statusColumn++);
            }
        }
        StatusItemsHost.Visibility = preference.StatusItems.Any(status => status.IsVisible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (visible && visibleDisplayArea is not null)
        {
            TryPositionAt(visibleAnchor, visibleDisplayArea);
        }
    }

    public void ToggleAt(TrayDashboardPoint anchor)
    {
        if (!visible && lastDeactivationHideTimestamp != 0)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(lastDeactivationHideTimestamp);
            lastDeactivationHideTimestamp = 0;
            if (TrayDashboardInteractionPolicy.ShouldSuppressReopen(elapsed))
            {
                return;
            }
        }

        if (visible && !pinnedForEditing)
        {
            Hide();
            return;
        }

        ShowAt(anchor, editing: false);
    }

    public void BeginEditSession()
    {
        DisplayArea displayArea = DisplayArea.Primary;
        RectInt32 workArea = displayArea.WorkArea;
        ShowAt(
            new TrayDashboardPoint(
                checked(workArea.X + workArea.Width - 1),
                checked(workArea.Y + workArea.Height - 1)),
            editing: true,
            displayArea);
    }

    public void EndEditSession()
    {
        pinnedForEditing = false;
        Hide();
    }

    public void DismissForMainWindow()
    {
        if (!pinnedForEditing)
        {
            Hide();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Activated -= OnActivated;
        AppWindow.Closing -= OnClosing;
        foregroundTimer.Stop();
        foregroundTimer.Tick -= OnForegroundTimerTick;
        CancelRevealAnimation();
        DetachRevealSubclass();
        if (visible)
        {
            visible = false;
            visibilityChanged(false);
        }
        Close();
    }

    private void ShowAt(
        TrayDashboardPoint anchor,
        bool editing,
        DisplayArea? knownDisplayArea = null)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        pinnedForEditing = editing;
        ShowDashboardPage();
        DisplayArea displayArea = knownDisplayArea ?? DisplayArea.GetFromPoint(
            new PointInt32(anchor.X, anchor.Y),
            DisplayAreaFallback.Nearest);
        visibleAnchor = anchor;
        visibleDisplayArea = displayArea;
        previousForegroundWindow = WindowsPopupPositioner.GetForegroundWindowHandle();
        foregroundEstablished = false;
        visible = true;
        visibilityChanged(true);
        if (!editing)
        {
            foregroundTimer.Start();
        }
        if (TryResolvePlacement(anchor, displayArea, out ResolvedPlacement placement)
            && TryStartReveal(placement, displayArea, editing))
        {
            return;
        }

        if (placement.Rect.Width > 0)
        {
            AppWindow.MoveAndResize(
                ToRectInt32(placement.Rect),
                displayArea);
        }
        AppWindow.Show(activateWindow: !editing);
        surfaceShown = true;
        if (!editing)
        {
            Activate();
            _ = WindowsPopupPositioner.TryActivate(
                WindowNative.GetWindowHandle(this));
        }
    }

    private void TryPositionAt(TrayDashboardPoint anchor, DisplayArea displayArea)
    {
        CancelRevealAnimation();
        try
        {
            PositionAt(anchor, displayArea);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard placement failed; keeping the Windows placement: {0}",
                exception);
        }
    }

    private void PositionAt(TrayDashboardPoint anchor, DisplayArea displayArea)
    {
        ResolvedPlacement placement = ResolvePlacement(anchor, displayArea);
        revealPlacement = placement.Rect;
        revealStagingY = placement.Reveal.StagingY;
        revealCurrentY = placement.Rect.Y;
        revealWindowHandle = WindowNative.GetWindowHandle(this);
        WindowsPopupPositioner.PositionInFront(
            revealWindowHandle,
            placement.Rect.X,
            placement.Rect.Y,
            placement.Rect.Width,
            placement.Rect.Height);
    }

    private void TryPrimeSurface()
    {
        try
        {
            DisplayArea displayArea = DisplayArea.Primary;
            RectInt32 workArea = displayArea.WorkArea;
            var anchor = new TrayDashboardPoint(
                checked(workArea.X + workArea.Width - 1),
                checked(workArea.Y + workArea.Height - 1));
            ResolvedPlacement placement = ResolvePlacement(anchor, displayArea);
            revealPlacement = placement.Rect;
            revealStagingY = placement.Reveal.StagingY;
            revealCurrentY = revealStagingY;
            revealWindowHandle = WindowNative.GetWindowHandle(this);
            WindowsPopupPositioner.DisableTransitions(revealWindowHandle);
            WindowsPopupPositioner.PositionOffscreen(
                revealWindowHandle,
                placement.Rect.Width,
                placement.Rect.Height);
            Root.UpdateLayout();
            if (!surfaceShown)
            {
                AppWindow.Show(activateWindow: false);
                surfaceShown = true;
            }
            WindowsPopupPositioner.PositionOffscreen(
                revealWindowHandle,
                placement.Rect.Width,
                placement.Rect.Height);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard surface prewarm failed: {0}",
                exception.Message);
        }
    }

    private ResolvedPlacement ResolvePlacement(
        TrayDashboardPoint anchor,
        DisplayArea displayArea)
    {
        RectInt32 workArea = displayArea.WorkArea;
        int preferredHeightDip =
            TrayDashboardLayoutPolicy.CalculatePreferredHeight(layout);
        nint monitor = MonitorFromPoint(
            new NativePoint(anchor.X, anchor.Y),
            MonitorDefaultToNearest);
        if (monitor == 0)
        {
            throw new InvalidOperationException(
                "Windows could not resolve the tray dashboard target monitor.");
        }
        int dpiResult = GetDpiForMonitor(
            monitor,
            MonitorDpiType.Effective,
            out uint nativeDpi,
            out _);
        Marshal.ThrowExceptionForHR(dpiResult);
        if (nativeDpi == 0)
        {
            throw new InvalidOperationException(
                "Windows returned zero DPI for the tray dashboard target monitor.");
        }

        var physicalWorkArea = new TrayDashboardRect(
            workArea.X,
            workArea.Y,
            workArea.Width,
            workArea.Height);
        TrayDashboardRect placement = TrayDashboardPlacement.Resolve(
            anchor,
            physicalWorkArea,
            new TrayDashboardSize(PopupWidth, preferredHeightDip),
            checked((int)nativeDpi));
        TrayDashboardRevealGeometry reveal =
            TrayDashboardPlacement.ResolveRevealGeometry(
                anchor,
                physicalWorkArea,
                placement);
        return new ResolvedPlacement(placement, reveal);
    }

    private bool TryResolvePlacement(
        TrayDashboardPoint anchor,
        DisplayArea displayArea,
        out ResolvedPlacement placement)
    {
        try
        {
            placement = ResolvePlacement(anchor, displayArea);
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard placement failed; keeping the Windows placement: {0}",
                exception);
            placement = default;
            return false;
        }
    }

    private bool TryStartReveal(
        ResolvedPlacement placement,
        DisplayArea displayArea,
        bool editing)
    {
        try
        {
            CancelRevealAnimation();
            revealPlacement = placement.Rect;
            int stagingY = placement.Reveal.StagingY;
            revealStagingY = stagingY;
            revealStartY = stagingY;
            revealCurrentY = stagingY;
            revealWindowHandle = WindowNative.GetWindowHandle(this);
            revealStartedTimestamp = 0;
            revealPrimingFrames = 0;
            revealAnimationsEnabled = WindowsMotionSettings.AreAnimationsEnabled();
            revealHiding = false;
            awaitingRevealActivation = !editing;
            WindowsPopupPositioner.DisableTransitions(revealWindowHandle);

            var staged = new TrayDashboardRect(
                placement.Rect.X,
                stagingY,
                placement.Rect.Width,
                placement.Rect.Height);
            AppWindow.MoveAndResize(ToRectInt32(staged), displayArea);
            Root.UpdateLayout();
            if (!surfaceShown)
            {
                AppWindow.Show(activateWindow: false);
                surfaceShown = true;
            }
            WindowsPopupPositioner.PositionInFront(
                revealWindowHandle,
                staged.X,
                staged.Y,
                staged.Width,
                staged.Height);
            StartRevealFrameClock();
            return true;
        }
        catch (Exception exception)
        {
            CancelRevealAnimation();
            Trace.TraceWarning(
                "OpenBlade tray dashboard reveal failed; showing without animation: {0}",
                exception);
            return false;
        }
    }

    private void AdvanceRevealAnimation()
    {
        Interlocked.Exchange(ref revealFramePending, 0);
        if (!revealAnimationActive || (!visible && !revealHiding))
        {
            return;
        }

        if (!revealHiding && revealPrimingFrames < 2)
        {
            revealPrimingFrames++;
            if (revealPrimingFrames == 2)
            {
                revealStartedTimestamp = Stopwatch.GetTimestamp();
            }
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(revealStartedTimestamp);
        int endY = revealHiding
            ? revealStagingY
            : revealPlacement.Y;
        int y = revealAnimationsEnabled
            ? WindowsPopupSlidePolicy.ResolvePosition(
                revealStartY,
                endY,
                elapsed,
                hiding: revealHiding)
            : endY;
        revealCurrentY = y;
        try
        {
            WindowsPopupPositioner.PositionInFront(
                revealWindowHandle,
                revealPlacement.X,
                y,
                revealPlacement.Width,
                revealPlacement.Height);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard reveal frame failed: {0}",
                exception.Message);
            AppWindow.MoveAndResize(ToRectInt32(revealPlacement));
            y = revealPlacement.Y;
        }

        if (!revealHiding && awaitingRevealActivation)
        {
            AppWindow.Show(activateWindow: true);
            Activate();
            awaitingRevealActivation =
                !WindowsPopupPositioner.TryActivate(revealWindowHandle);
            foregroundEstablished = !awaitingRevealActivation;
            try
            {
                WindowsPopupPositioner.PositionInFront(
                    revealWindowHandle,
                    revealPlacement.X,
                    y,
                    revealPlacement.Width,
                    revealPlacement.Height);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "OpenBlade tray dashboard activation positioning failed: {0}",
                    exception.Message);
            }
        }

        if (!revealAnimationsEnabled
            || y == endY
            || WindowsPopupSlidePolicy.IsComplete(elapsed))
        {
            if (revealHiding)
            {
                ParkSurface();
                WindowsPopupPositioner.TryRestoreForeground(
                    revealWindowHandle,
                    previousForegroundWindow);
                CancelRevealAnimation();
                return;
            }

            try
            {
                WindowsPopupPositioner.PositionInFront(
                    revealWindowHandle,
                    revealPlacement.X,
                    revealPlacement.Y,
                    revealPlacement.Width,
                    revealPlacement.Height);
                if (awaitingRevealActivation)
                {
                    AppWindow.Show(activateWindow: true);
                    Activate();
                    foregroundEstablished =
                        WindowsPopupPositioner.TryActivate(revealWindowHandle);
                }
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "OpenBlade tray dashboard final positioning failed: {0}",
                    exception.Message);
            }
            CancelRevealAnimation();
        }
    }

    private void CancelRevealAnimation()
    {
        revealAnimationActive = false;
        CancellationTokenSource? cancellation = revealFrameClockCancellation;
        revealFrameClockCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        Interlocked.Exchange(ref revealFramePending, 0);
        revealHiding = false;
        awaitingRevealActivation = false;
        revealStartedTimestamp = 0;
        revealPrimingFrames = 0;
        revealAnimationsEnabled = false;
    }

    private void StartRevealFrameClock()
    {
        var cancellation = new CancellationTokenSource();
        revealFrameClockCancellation = cancellation;
        revealAnimationActive = true;
        _ = Task.Run(() => RunRevealFrameClock(cancellation.Token));
    }

    private void RunRevealFrameClock(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DwmFlush() < 0)
                {
                    Thread.Sleep(1);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref revealFramePending, 1) == 0
                    && !TryPostRevealFrame())
                {
                    Interlocked.Exchange(ref revealFramePending, 0);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard frame clock failed: {0}",
                exception.Message);
        }
    }

    private bool TryPostRevealFrame()
    {
        if (revealSubclassAttached)
        {
            return PostMessage(hostWindowHandle, advanceRevealMessage, 0, 0);
        }

        return dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.High,
            AdvanceRevealAnimation);
    }

    private void TryAttachRevealSubclass()
    {
        if (hostWindowHandle == 0 || advanceRevealMessage == 0)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard could not register its native frame host; "
                + "using the WinUI dispatcher fallback.");
            return;
        }

        if (!RevealHosts.TryAdd(hostWindowHandle, this))
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard already has a native frame host; "
                + "using the WinUI dispatcher fallback.");
            return;
        }

        if (!SetWindowSubclass(
                hostWindowHandle,
                RevealSubclassCallback,
                RevealSubclassId,
                0))
        {
            RevealHosts.TryRemove(hostWindowHandle, out _);
            Trace.TraceWarning(
                "OpenBlade tray dashboard could not attach its native frame host; "
                + "using the WinUI dispatcher fallback. Win32 error: {0}",
                Marshal.GetLastWin32Error());
            return;
        }

        revealSubclassAttached = true;
    }

    private void DetachRevealSubclass()
    {
        if (!revealSubclassAttached)
        {
            return;
        }

        revealSubclassAttached = false;
        _ = RemoveWindowSubclass(
            hostWindowHandle,
            RevealSubclassCallback,
            RevealSubclassId);
        RevealHosts.TryRemove(hostWindowHandle, out _);
    }

    private static nint ProcessRevealWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData)
    {
        if (RevealHosts.TryGetValue(window, out TrayDashboardWindow? dashboard))
        {
            if (message == dashboard.advanceRevealMessage)
            {
                dashboard.AdvanceRevealAnimation();
                return 1;
            }

            if (message == WindowMessageNonClientDestroy)
            {
                dashboard.revealSubclassAttached = false;
                RevealHosts.TryRemove(window, out _);
                _ = RemoveWindowSubclass(
                    window,
                    RevealSubclassCallback,
                    RevealSubclassId);
            }
        }

        return DefSubclassProc(window, message, windowParameter, longParameter);
    }

    private bool TryStartDismissAnimation()
    {
        if (!surfaceShown
            || revealWindowHandle == 0
            || revealPlacement.Width <= 0
            || revealPlacement.Height <= 0)
        {
            return false;
        }

        CancelRevealAnimation();
        revealStartY = revealCurrentY;
        revealStartedTimestamp = Stopwatch.GetTimestamp();
        revealPrimingFrames = 2;
        revealAnimationsEnabled = WindowsMotionSettings.AreAnimationsEnabled();
        revealHiding = true;
        StartRevealFrameClock();
        return true;
    }

    private void ParkSurface()
    {
        if (!surfaceShown || revealWindowHandle == 0)
        {
            return;
        }

        try
        {
            WindowsPopupPositioner.PositionOffscreen(
                revealWindowHandle,
                revealPlacement.Width,
                revealPlacement.Height);
            revealCurrentY = revealStagingY;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard parking failed: {0}",
                exception.Message);
        }

        ShowDashboardPage();
    }

    private static RectInt32 ToRectInt32(TrayDashboardRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private void EnsureRow(int row)
    {
        while (ControlGrid.RowDefinitions.Count <= row)
        {
            ControlGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto,
            });
        }
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        Thickness padding = ControlScroller.Padding;
        ControlGrid.Width = Math.Max(
            0,
            args.NewSize.Width - padding.Left - padding.Right);
    }

    private IReadOnlyDictionary<TrayDashboardControlKind, FrameworkElement> TilesByKind() =>
        new Dictionary<TrayDashboardControlKind, FrameworkElement>
        {
            [TrayDashboardControlKind.Performance] = PerformanceTile,
            [TrayDashboardControlKind.Fans] = FansTile,
            [TrayDashboardControlKind.Battery] = BatteryTile,
            [TrayDashboardControlKind.Lighting] = LightingTile,
        };

    private IReadOnlyDictionary<TrayDashboardStatusItemKind, FrameworkElement>
        StatusItemsByKind() =>
        new Dictionary<TrayDashboardStatusItemKind, FrameworkElement>
        {
            [TrayDashboardStatusItemKind.BatteryPower] = BatteryPowerStatus,
            [TrayDashboardStatusItemKind.Temperatures] = TemperatureStatus,
            [TrayDashboardStatusItemKind.Fans] = FansStatus,
        };

    private void Hide(bool suppressNextTrayToggle = false)
    {
        if (!visible)
        {
            return;
        }

        visible = false;
        foregroundTimer.Stop();
        visibilityChanged(false);
        if (suppressNextTrayToggle)
        {
            lastDeactivationHideTimestamp = Stopwatch.GetTimestamp();
        }

        if (!TryStartDismissAnimation())
        {
            ParkSurface();
            WindowsPopupPositioner.TryRestoreForeground(
                revealWindowHandle,
                previousForegroundWindow);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            foregroundEstablished = true;
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated
            && !pinnedForEditing
            && !awaitingRevealActivation)
        {
            Hide(suppressNextTrayToggle: true);
        }
    }

    private void OnForegroundTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!visible || pinnedForEditing)
        {
            sender.Stop();
            return;
        }

        if (awaitingRevealActivation)
        {
            return;
        }

        nint foreground = WindowsPopupPositioner.GetForegroundWindowHandle();
        if (foreground == revealWindowHandle)
        {
            foregroundEstablished = true;
            return;
        }

        if (!foregroundEstablished)
        {
            AppWindow.Show(activateWindow: true);
            Activate();
            foregroundEstablished =
                WindowsPopupPositioner.TryActivate(revealWindowHandle);
            return;
        }

        Hide(suppressNextTrayToggle: true);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            args.Cancel = true;
            Hide();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Theme))
        {
            ThemeManager.Apply(Root, ViewModel.Theme);
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        if (detailControl is not null)
        {
            ShowDashboardPage();
            args.Handled = true;
        }
        else if (!pinnedForEditing)
        {
            Hide();
            args.Handled = true;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs args)
    {
        pinnedForEditing = false;
        Hide();
        openMainWindow();
    }

    private async void OnPerformanceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingPerformance
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ListView selector
            || selector.SelectedItem is not PerformancePresetOption selected
            || selected.Value == ViewModel.SelectedPerformance)
        {
            return;
        }

        await ApplyPerformanceOptionAsync(selected);
    }

    private async Task ApplyPerformanceOptionAsync(PerformancePresetOption selected)
    {
        if (changingPerformance || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        changingPerformance = true;
        try
        {
            if (ViewModel.SetPerformanceCommand is AsyncRelayCommand command
                && command.CanExecute(selected.Value))
            {
                await command.ExecuteAsync(selected.Value);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard performance selection failed: {0}",
                exception);
        }
        finally
        {
            PerformanceDetail.SelectedItem = ViewModel.SelectedPerformanceOption;
            changingPerformance = false;
        }
    }

    private async void OnFanModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingFans
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ListView selector
            || selector.SelectedItem is not FanModeOption selected
            || selected.Value == ViewModel.SelectedFanMode)
        {
            return;
        }

        await ApplyFanModeOptionAsync(selected);
    }

    private async Task ApplyFanModeOptionAsync(FanModeOption selected)
    {
        if (changingFans || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        changingFans = true;
        try
        {
            if (ViewModel.SetFanModeCommand is AsyncRelayCommand command
                && command.CanExecute(selected.Value))
            {
                await command.ExecuteAsync(selected.Value);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard fan selection failed: {0}",
                exception);
        }
        finally
        {
            FansDetail.SelectedItem = ViewModel.SelectedFanModeOption;
            changingFans = false;
        }
    }

    private async void OnPerformanceCycleClick(object sender, RoutedEventArgs args)
    {
        IReadOnlyList<PerformancePresetOption> options =
            ViewModel.AvailableActivePerformanceOptions;
        int next = TrayDashboardInteractionPolicy.ResolveNextIndex(
            options.Count,
            FindIndex(options, option =>
                option.Value == ViewModel.SelectedPerformance));
        if (next >= 0)
        {
            await ApplyPerformanceOptionAsync(options[next]);
        }
    }

    private async void OnFansCycleClick(object sender, RoutedEventArgs args)
    {
        IReadOnlyList<FanModeOption> options = ViewModel.AvailableActiveFanModeOptions;
        int next = TrayDashboardInteractionPolicy.ResolveNextIndex(
            options.Count,
            FindIndex(options, option => option.Value == ViewModel.SelectedFanMode));
        if (next >= 0)
        {
            await ApplyFanModeOptionAsync(options[next]);
        }
    }

    private async void OnBatteryCycleClick(object sender, RoutedEventArgs args)
    {
        IReadOnlyList<int> options = ViewModel.ChargeLimitOptions;
        int next = TrayDashboardInteractionPolicy.ResolveNextIndex(
            options.Count,
            FindIndex(options, option => option == ViewModel.ChargeLimitPercent));
        if (next >= 0)
        {
            await CommitChargeLimitAsync(options[next]);
        }
    }

    private void OnLightingCycleClick(object sender, RoutedEventArgs args)
    {
        IReadOnlyList<LightingEffectOption> options =
            ViewModel.AvailableLightingEffectOptions;
        int next = TrayDashboardInteractionPolicy.ResolveNextIndex(
            options.Count,
            FindIndex(options, option =>
                option.Effect == ViewModel.LightingEffectEditor));
        if (next >= 0)
        {
            ViewModel.SelectedLightingEffectOption = options[next];
        }
    }

    private void OnPerformanceDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(TrayDashboardControlKind.Performance, "Performance");

    private void OnFansDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(TrayDashboardControlKind.Fans, "Fans");

    private void OnBatteryDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(TrayDashboardControlKind.Battery, "Charge limit");

    private void OnLightingDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(TrayDashboardControlKind.Lighting, "Lighting");

    private void OnDetailBackClick(object sender, RoutedEventArgs args) =>
        ShowDashboardPage();

    private void ShowDetail(TrayDashboardControlKind kind, string title)
    {
        detailControl = kind;
        DetailTitle.Text = title;
        PerformanceDetail.Visibility = kind == TrayDashboardControlKind.Performance
            ? Visibility.Visible
            : Visibility.Collapsed;
        FansDetail.Visibility = kind == TrayDashboardControlKind.Fans
            ? Visibility.Visible
            : Visibility.Collapsed;
        BatteryDetail.Visibility = kind == TrayDashboardControlKind.Battery
            ? Visibility.Visible
            : Visibility.Collapsed;
        LightingDetail.Visibility = kind == TrayDashboardControlKind.Lighting
            ? Visibility.Visible
            : Visibility.Collapsed;
        DashboardPage.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
    }

    private void ShowDashboardPage()
    {
        detailControl = null;
        DetailPage.Visibility = Visibility.Collapsed;
        DashboardPage.Visibility = Visibility.Visible;
        _ = ControlScroller.ChangeView(
            horizontalOffset: null,
            verticalOffset: 0,
            zoomFactor: null,
            disableAnimation: true);
    }

    private static int FindIndex<T>(
        IReadOnlyList<T> options,
        Func<T, bool> predicate)
    {
        for (int index = 0; index < options.Count; index++)
        {
            if (predicate(options[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private async void OnChargeLimitChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingBattery
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ListView selector
            || selector.SelectedItem is not int selected
            || selected == ViewModel.ChargeLimitPercent)
        {
            return;
        }

        changingBattery = true;
        try
        {
            await CommitChargeLimitAsync(selected);
        }
        finally
        {
            selector.SelectedItem = ViewModel.ChargeLimitPercent;
            changingBattery = false;
        }
    }

    private async Task CommitChargeLimitAsync(double sliderValue)
    {
        int value;
        try
        {
            value = IntegerEditorValue.ToInt32(sliderValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        ViewModel.ChargeLimitPercent = value;
        try
        {
            if (ViewModel.SetChargeLimitCommand is AsyncRelayCommand command
                && command.CanExecute(value))
            {
                await command.ExecuteAsync(value);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard charge-limit change failed: {0}",
                exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;

        public readonly int Y;
    }

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    private delegate nint SubclassProcedure(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData);

    private readonly record struct ResolvedPlacement(
        TrayDashboardRect Rect,
        TrayDashboardRevealGeometry Reveal);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterWindowMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint window,
        uint message,
        nuint windowParameter,
        nint longParameter);
}
