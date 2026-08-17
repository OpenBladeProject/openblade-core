using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenBlade.Contracts;
using Windows.Graphics;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class TrayDashboardWindow : Window, IDisposable
{
    private const int PopupWidth = 360;
    private static readonly TimeSpan RevealCompletionDeadline =
        TimeSpan.FromMilliseconds(
            WindowsPopupSlidePolicy.DurationMilliseconds + 100);
    private static readonly TimeSpan GpuClockOffsetCommitDelay =
        TimeSpan.FromMilliseconds(500);
    private const uint MonitorDefaultToNearest = 2;
    private const uint WindowMessageNonClientDestroy = 0x0082;
    private const nuint RevealSubclassId = 0x4F425244; // "OBRD"

    private static readonly ConcurrentDictionary<nint, TrayDashboardWindow>
        RevealHosts = new();
    private static readonly SubclassProcedure RevealSubclassCallback =
        ProcessRevealWindowMessage;

    private readonly Action<bool> visibilityChanged;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly DispatcherQueueTimer foregroundTimer;
    private readonly DispatcherQueueTimer revealCompletionTimer;
    private readonly nint editOwnerWindowHandle;
    private DispatcherQueueTimer? gpuClockOffsetCommitTimer;
    private readonly CoalescingCommitState<GpuClockOffsetSettings>
        gpuClockOffsetCommits = new();
    private readonly nint hostWindowHandle;
    private readonly uint advanceRevealMessage;
    private TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;
    private TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        TrayDashboardPreferenceDefaults.Preference.Controls!);
    private bool changingPerformance;
    private bool changingFans;
    private bool changingBatteryProtection;
    private bool changingGpuClockTuning;
    private bool changingGamingModePolicy;
    private bool chargeLimitPointerInteraction;
    private TrayDashboardControlKind? detailControl;
    private bool pinnedForEditing;
    private bool visible;
    private bool visibilityReported;
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
    private bool revealCompletionPending;
    private bool revealHiding;
    private bool awaitingRevealActivation;
    private bool revealActivationAttempted;
    private bool surfaceShown;
    private nint previousForegroundWindow;
    private bool foregroundEstablished;
    private long foregroundActivationStartedTimestamp;
    private int revealFrameClockGeneration;
    private int revealFramePending;
    private readonly object revealFrameClockSync = new();
    private Thread? revealFrameClockThread;
    private bool revealSubclassAttached;
    private int disposed;

    public TrayDashboardWindow(
        MainViewModel viewModel,
        nint editOwnerWindowHandle,
        Action<bool> visibilityChanged)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.editOwnerWindowHandle = editOwnerWindowHandle != 0
            ? editOwnerWindowHandle
            : throw new ArgumentOutOfRangeException(nameof(editOwnerWindowHandle));
        this.visibilityChanged = visibilityChanged
            ?? throw new ArgumentNullException(nameof(visibilityChanged));
        InitializeComponent();
        Title = LocalizedResources.Get(
            "TrayDashboardWindow_WindowTitle",
            "OpenBlade quick controls");
        Root.DataContext = ViewModel;
        dispatcherQueue = Root.DispatcherQueue;
        foregroundTimer = dispatcherQueue.CreateTimer();
        foregroundTimer.Interval = TimeSpan.FromMilliseconds(50);
        foregroundTimer.IsRepeating = false;
        foregroundTimer.Tick += OnForegroundTimerTick;
        revealCompletionTimer = dispatcherQueue.CreateTimer();
        revealCompletionTimer.Interval = RevealCompletionDeadline;
        revealCompletionTimer.IsRepeating = false;
        revealCompletionTimer.Tick += OnRevealCompletionTimerTick;
        gpuClockOffsetCommitTimer = dispatcherQueue.CreateTimer();
        gpuClockOffsetCommitTimer.Interval = GpuClockOffsetCommitDelay;
        gpuClockOffsetCommitTimer.Tick += OnGpuClockOffsetCommitTimerTick;
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
        UpdateSliderRows();
        UpdateFansDetailsAvailability();
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
        TrySetEditOwner(editOwnerWindowHandle);
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
        try
        {
            Hide();
        }
        finally
        {
            TrySetEditOwner(0);
        }
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
        revealCompletionTimer.Stop();
        revealCompletionTimer.Tick -= OnRevealCompletionTimerTick;
        gpuClockOffsetCommits.Dispose();
        if (gpuClockOffsetCommitTimer is { } gpuTimer)
        {
            gpuTimer.Stop();
            gpuTimer.Tick -= OnGpuClockOffsetCommitTimerTick;
            gpuClockOffsetCommitTimer = null;
        }
        CancelRevealAnimation();
        DetachRevealSubclass();
        TrySetEditOwner(0);
        visible = false;
        ReportVisibility(false);
        Close();
    }

    private void TrySetEditOwner(nint owner)
    {
        try
        {
            WindowsPopupPositioner.SetOwner(hostWindowHandle, owner);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard edit owner change failed: {0}",
                exception.Message);
        }
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
        foregroundActivationStartedTimestamp = Stopwatch.GetTimestamp();
        foregroundTimer.Stop();
        visible = true;
        ReportVisibility(true);
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
            if (!TryEstablishForeground())
            {
                ScheduleForegroundCheckOrDismiss();
            }
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
            TrayDashboardLayoutPolicy.CalculatePreferredHeight();
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
        TrayDashboardResolvedPlacement resolvedPlacement =
            TrayDashboardPlacement.Resolve(
                anchor,
                physicalWorkArea,
                new TrayDashboardSize(PopupWidth, preferredHeightDip),
                checked((int)nativeDpi));
        return new ResolvedPlacement(
            resolvedPlacement.Rect,
            resolvedPlacement.RevealGeometry);
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
            revealActivationAttempted = false;
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

        if (!revealHiding
            && awaitingRevealActivation
            && !revealActivationAttempted)
        {
            revealActivationAttempted = true;
            awaitingRevealActivation = !TryEstablishForeground();
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
            CompleteRevealAnimation();
        }
    }

    private void CompleteRevealAnimation()
    {
        if (revealHiding)
        {
            ParkSurface();
            ReportVisibility(false);
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
                foregroundEstablished = TryEstablishForeground();
                awaitingRevealActivation = false;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "OpenBlade tray dashboard final positioning failed: {0}",
                exception.Message);
        }
        CancelRevealAnimation();
        if (!foregroundEstablished && visible && !pinnedForEditing)
        {
            ScheduleForegroundCheckOrDismiss();
        }
    }

    private void OnRevealCompletionTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (!revealCompletionPending)
        {
            return;
        }

        CompleteRevealAnimation();
    }

    private void CancelRevealAnimation()
    {
        revealCompletionPending = false;
        revealCompletionTimer.Stop();
        Volatile.Write(ref revealAnimationActive, false);
        Interlocked.Increment(ref revealFrameClockGeneration);
        Interlocked.Exchange(ref revealFramePending, 0);
        revealHiding = false;
        awaitingRevealActivation = false;
        revealActivationAttempted = false;
        revealStartedTimestamp = 0;
        revealPrimingFrames = 0;
        revealAnimationsEnabled = false;
    }

    private void StartRevealFrameClock()
    {
        Interlocked.Increment(ref revealFrameClockGeneration);
        Volatile.Write(ref revealAnimationActive, true);
        revealCompletionPending = true;
        revealCompletionTimer.Stop();
        revealCompletionTimer.Start();
        EnsureRevealFrameClock();
    }

    private void EnsureRevealFrameClock()
    {
        lock (revealFrameClockSync)
        {
            if (revealFrameClockThread is { IsAlive: true })
            {
                return;
            }

            revealFrameClockThread = new Thread(RunRevealFrameClock)
            {
                IsBackground = true,
                Name = "OpenBlade tray dashboard reveal",
            };
            revealFrameClockThread.Start();
        }
    }

    private void RunRevealFrameClock()
    {
        try
        {
            while (Volatile.Read(ref revealAnimationActive))
            {
                int generation = Volatile.Read(ref revealFrameClockGeneration);
                if (DwmFlush() < 0)
                {
                    Thread.Sleep(16);
                }
                if (!Volatile.Read(ref revealAnimationActive))
                {
                    return;
                }
                if (Volatile.Read(ref revealFrameClockGeneration) != generation)
                {
                    continue;
                }
                if (Interlocked.Exchange(ref revealFramePending, 1) == 0
                    && !TryPostRevealFrame())
                {
                    Interlocked.Exchange(ref revealFramePending, 0);
                    Volatile.Write(ref revealAnimationActive, false);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref revealAnimationActive, false);
            Trace.TraceWarning(
                "OpenBlade tray dashboard frame clock failed: {0}",
                exception.Message);
        }
        finally
        {
            bool restart;
            lock (revealFrameClockSync)
            {
                revealFrameClockThread = null;
                restart = Volatile.Read(ref revealAnimationActive)
                    && Volatile.Read(ref disposed) == 0;
            }
            if (restart)
            {
                EnsureRevealFrameClock();
            }
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

    private IReadOnlyDictionary<TrayDashboardControlKind, FrameworkElement> TilesByKind() =>
        new Dictionary<TrayDashboardControlKind, FrameworkElement>
        {
            [TrayDashboardControlKind.Performance] = PerformanceTile,
            [TrayDashboardControlKind.Fans] = FansTile,
            [TrayDashboardControlKind.Lighting] = LightingTile,
            [TrayDashboardControlKind.BatteryProtection] = BatteryProtectionTile,
            [TrayDashboardControlKind.FunctionRow] = FunctionRowTile,
            [TrayDashboardControlKind.GamingMode] = GamingModeTile,
            [TrayDashboardControlKind.GpuTuning] = GpuTuningTile,
        };

    private IReadOnlyDictionary<TrayDashboardSliderKind, FrameworkElement>
        SlidersByKind() =>
        new Dictionary<TrayDashboardSliderKind, FrameworkElement>
        {
            [TrayDashboardSliderKind.ChargeLimit] = ChargeLimitSliderRow,
            [TrayDashboardSliderKind.LightingBrightness] = LightingBrightnessSliderRow,
            [TrayDashboardSliderKind.GpuCoreClock] = GpuCoreClockSliderRow,
            [TrayDashboardSliderKind.GpuMemoryClock] = GpuMemoryClockSliderRow,
        };

    private void UpdateSliderRows()
    {
        IReadOnlyDictionary<TrayDashboardSliderKind, FrameworkElement> sliders =
            SlidersByKind();
        foreach (FrameworkElement slider in sliders.Values)
        {
            slider.Visibility = Visibility.Collapsed;
        }

        SliderGrid.RowDefinitions.Clear();
        int row = 0;
        foreach (TrayDashboardSliderPreference slider in preference.Sliders!)
        {
            FrameworkElement element = sliders[slider.Kind];
            if (!slider.IsVisible)
            {
                continue;
            }

            SliderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, row++);
            element.Visibility = Visibility.Visible;
        }

        SliderSection.Visibility = row == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateFansDetailsAvailability()
    {
        bool hasChoices = ViewModel.AvailableActiveFanModeOptions.Count > 1;
        FansDetailsColumn.Width = hasChoices
            ? new GridLength(36)
            : new GridLength(0);
        FansDetailsHost.Visibility = hasChoices
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private IReadOnlyDictionary<TrayDashboardStatusItemKind, FrameworkElement>
        StatusItemsByKind() =>
        new Dictionary<TrayDashboardStatusItemKind, FrameworkElement>
        {
            [TrayDashboardStatusItemKind.BatteryPower] = BatteryPowerStatus,
            [TrayDashboardStatusItemKind.Temperatures] = TemperatureStatus,
            [TrayDashboardStatusItemKind.PowerSource] = PowerSourceStatus,
            [TrayDashboardStatusItemKind.FanSpeed] = FanSpeedStatus,
        };

    private void Hide(bool suppressNextTrayToggle = false)
    {
        if (!visible)
        {
            return;
        }

        visible = false;
        foregroundTimer.Stop();
        if (suppressNextTrayToggle)
        {
            lastDeactivationHideTimestamp = Stopwatch.GetTimestamp();
        }

        if (!TryStartDismissAnimation())
        {
            ParkSurface();
            ReportVisibility(false);
            WindowsPopupPositioner.TryRestoreForeground(
                revealWindowHandle,
                previousForegroundWindow);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (visible
                && !pinnedForEditing
                && !awaitingRevealActivation
                && foregroundEstablished)
            {
                foregroundTimer.Stop();
                foregroundTimer.Start();
            }
            return;
        }

        foregroundEstablished = true;
        awaitingRevealActivation = false;
        foregroundTimer.Stop();
        MoveFocusOffInitialTile();
    }

    private bool TryEstablishForeground()
    {
        AppWindow.Show(activateWindow: true);
        Activate();
        bool activated = WindowsPopupPositioner.TryActivate(revealWindowHandle)
            || WindowsPopupPositioner.GetForegroundWindowHandle() == revealWindowHandle;
        foregroundEstablished = activated;
        if (activated)
        {
            foregroundTimer.Stop();
        }
        return activated;
    }

    private void ScheduleForegroundCheckOrDismiss()
    {
        if (TrayDashboardInteractionPolicy.ShouldRetryForegroundActivation(
                Stopwatch.GetElapsedTime(foregroundActivationStartedTimestamp)))
        {
            foregroundTimer.Stop();
            foregroundTimer.Start();
        }
        else
        {
            Hide();
        }
    }

    private void MoveFocusOffInitialTile()
    {
        if (!visible || pinnedForEditing || detailControl is not null)
        {
            return;
        }

        _ = InitialFocusTarget.Focus(FocusState.Programmatic);
        _ = dispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (visible && !pinnedForEditing && detailControl is null)
                {
                    _ = InitialFocusTarget.Focus(FocusState.Programmatic);
                }
            });
    }

    private void OnForegroundTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!visible || pinnedForEditing)
        {
            return;
        }

        if (awaitingRevealActivation)
        {
            ScheduleForegroundCheckOrDismiss();
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
            if (!TryEstablishForeground())
            {
                ScheduleForegroundCheckOrDismiss();
            }
            return;
        }

        Hide(
            suppressNextTrayToggle:
                WindowsPopupPositioner.IsShellTrayWindow(foreground));
    }

    private void ReportVisibility(bool isVisible)
    {
        if (visibilityReported == isVisible)
        {
            return;
        }

        visibilityReported = isVisible;
        visibilityChanged(isVisible);
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
        else if (args.PropertyName == nameof(MainViewModel.AvailableActiveFanModeOptions))
        {
            UpdateFansDetailsAvailability();
        }
        else if (args.PropertyName == nameof(MainViewModel.CanEditGpuClockTuning)
            && !ViewModel.CanEditGpuClockTuning)
        {
            gpuClockOffsetCommits.ClearPending();
            gpuClockOffsetCommitTimer?.Stop();
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

    private async void OnFunctionRowCycleClick(object sender, RoutedEventArgs args)
    {
        ApplyOneShotLaptopControl? action = ViewModel.FunctionKeyPrimary switch
        {
            FunctionKeyPrimaryMode.FunctionKeys => ViewModel.MultimediaKeysPrimaryAction,
            FunctionKeyPrimaryMode.MultimediaKeys => ViewModel.FunctionKeysPrimaryAction,
            _ => null,
        };
        if (action is not null)
        {
            await ApplyOneShotControlAsync(action);
        }
    }

    private async void OnGamingModeCycleClick(object sender, RoutedEventArgs args)
    {
        ApplyOneShotLaptopControl? action = ViewModel.GamingModeEnabled switch
        {
            true => ViewModel.GamingModeOffAction,
            false => ViewModel.GamingModeOnAction,
            null => null,
        };
        if (action is not null)
        {
            await ApplyOneShotControlAsync(action);
        }
    }

    private async Task ApplyOneShotControlAsync(ApplyOneShotLaptopControl action)
    {
        try
        {
            if (ViewModel.ApplyOneShotLaptopControlCommand is AsyncRelayCommand command
                && command.CanExecute(action))
            {
                await command.ExecuteAsync(action);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard laptop-control change failed: {0}",
                exception);
        }
    }

    private void OnPerformanceDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(
            TrayDashboardControlKind.Performance,
            LocalizedResources.Get("TrayDashboard_DetailTitle_Performance", "Performance"));

    private void OnFansDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(
            TrayDashboardControlKind.Fans,
            LocalizedResources.Get("TrayDashboard_DetailTitle_Fans", "Fans"));

    private void OnLightingDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(
            TrayDashboardControlKind.Lighting,
            LocalizedResources.Get("TrayDashboard_DetailTitle_Lighting", "Lighting"));

    private void OnGamingModeDetailsClick(object sender, RoutedEventArgs args) =>
        ShowDetail(
            TrayDashboardControlKind.GamingMode,
            LocalizedResources.Get("TrayDashboard_DetailTitle_GamingMode", "Gaming mode"));

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
        LightingDetail.Visibility = kind == TrayDashboardControlKind.Lighting
            ? Visibility.Visible
            : Visibility.Collapsed;
        GamingModeDetail.Visibility = kind == TrayDashboardControlKind.GamingMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (kind == TrayDashboardControlKind.GamingMode)
        {
            SynchronizeGamingModePolicyToggles();
        }
        DashboardPage.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
    }

    private void SynchronizeGamingModePolicyToggles()
    {
        changingGamingModePolicy = true;
        try
        {
            GamingModeWindowsKeyToggle.IsOn = ViewModel.BlockWindowsKeyInGamingMode;
            GamingModeAltTabToggle.IsOn = ViewModel.BlockAltTabInGamingMode;
        }
        finally
        {
            changingGamingModePolicy = false;
        }
    }

    private async void OnGamingModeKeyPolicyToggled(object sender, RoutedEventArgs args)
    {
        if (changingGamingModePolicy || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        changingGamingModePolicy = true;
        try
        {
            _ = await ViewModel.SetGamingModeKeyPolicyAsync(
                GamingModeWindowsKeyToggle.IsOn,
                GamingModeAltTabToggle.IsOn,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard gaming-mode policy change failed: {0}",
                exception);
        }
        finally
        {
            GamingModeWindowsKeyToggle.IsOn = ViewModel.BlockWindowsKeyInGamingMode;
            GamingModeAltTabToggle.IsOn = ViewModel.BlockAltTabInGamingMode;
            changingGamingModePolicy = false;
        }
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

    private async void OnBatteryProtectionClick(object sender, RoutedEventArgs args)
    {
        if (changingBatteryProtection
            || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        changingBatteryProtection = true;
        try
        {
            if (ViewModel.ToggleBatteryProtectionCommand is AsyncRelayCommand command
                && command.CanExecute(null))
            {
                await command.ExecuteAsync(null);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard battery-protection change failed: {0}",
                exception);
        }
        finally
        {
            changingBatteryProtection = false;
        }
    }

    private void OnChargeLimitPointerPressed(object sender, PointerRoutedEventArgs args) =>
        chargeLimitPointerInteraction = Volatile.Read(ref disposed) == 0;

    private async void OnChargeLimitPointerReleased(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (!chargeLimitPointerInteraction)
        {
            return;
        }

        chargeLimitPointerInteraction = false;
        if (sender is Slider slider)
        {
            await CommitChargeLimitAsync(slider.Value);
        }
    }

    private async void OnChargeLimitKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (sender is Slider slider
            && args.Key is Windows.System.VirtualKey.Left
                or Windows.System.VirtualKey.Right
                or Windows.System.VirtualKey.Up
                or Windows.System.VirtualKey.Down
                or Windows.System.VirtualKey.PageUp
                or Windows.System.VirtualKey.PageDown
                or Windows.System.VirtualKey.Home
                or Windows.System.VirtualKey.End)
        {
            await CommitChargeLimitAsync(slider.Value);
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

    private async void OnGpuClockTuningClick(object sender, RoutedEventArgs args)
    {
        if (changingGpuClockTuning
            || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        changingGpuClockTuning = true;
        try
        {
            await ViewModel.SetGpuClockTuningEnabledAsync(
                !ViewModel.IsGpuClockTuningEnabled,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard GPU tuning change failed: {0}",
                exception);
        }
        finally
        {
            changingGpuClockTuning = false;
        }
    }

    private void OnGpuCoreClockOffsetChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        gpuClockOffsetCommits.TryPeekPending(out GpuClockOffsetSettings? pending);
        ScheduleGpuClockOffsetCommit(
            coreMegahertz: checked((int)Math.Round(args.NewValue)),
            memoryMegahertz: pending?.MemoryMegahertz
                ?? ViewModel.GpuMemoryClockOffsetMegahertz);
    }

    private void OnGpuMemoryClockOffsetChanged(
        object sender,
        RangeBaseValueChangedEventArgs args)
    {
        gpuClockOffsetCommits.TryPeekPending(out GpuClockOffsetSettings? pending);
        ScheduleGpuClockOffsetCommit(
            coreMegahertz: pending?.CoreMegahertz
                ?? ViewModel.GpuCoreClockOffsetMegahertz,
            memoryMegahertz: checked((int)Math.Round(args.NewValue)));
    }

    private void ScheduleGpuClockOffsetCommit(
        int coreMegahertz,
        int memoryMegahertz)
    {
        if (gpuClockOffsetCommitTimer is not { } timer
            || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var requested = new GpuClockOffsetSettings(coreMegahertz, memoryMegahertz);
        if (!GpuClockOffsetCommitPolicy.CanCommit(
                ViewModel.CanEditGpuClockTuning,
                ViewModel.GpuClockOffsetCapabilities,
                requested))
        {
            gpuClockOffsetCommits.ClearPending();
            timer.Stop();
            return;
        }

        if (requested == ViewModel.CurrentGpuClockOffsets)
        {
            gpuClockOffsetCommits.ClearPending();
            timer.Stop();
            return;
        }

        gpuClockOffsetCommits.Schedule(requested);
        timer.Stop();
        timer.Start();
    }

    private async void OnGpuClockOffsetCommitTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        CoalescingCommitBeginStatus status =
            gpuClockOffsetCommits.TryBegin(out GpuClockOffsetSettings? requested);
        if (status == CoalescingCommitBeginStatus.InProgress)
        {
            sender.Start();
            return;
        }
        if (status != CoalescingCommitBeginStatus.Started || requested is null)
        {
            return;
        }

        try
        {
            if (GpuClockOffsetCommitPolicy.CanCommit(
                    ViewModel.CanEditGpuClockTuning,
                    ViewModel.GpuClockOffsetCapabilities,
                    requested))
            {
                await ViewModel.SetGpuClockOffsetsAsync(
                    requested,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard GPU clock-offset change failed: {0}",
                exception);
        }
        finally
        {
            if (gpuClockOffsetCommits.Complete())
            {
                sender.Start();
            }
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
