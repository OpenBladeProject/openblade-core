using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Contracts;
using Windows.Graphics;
using Windows.Graphics.Display;
using WinRT.Interop;

namespace OpenBlade.Tray;

public sealed partial class TrayDashboardWindow : Window, IDisposable
{
    private const int PopupWidth = 360;
    private const int RevealOffsetDip = 24;
    private const uint MonitorDefaultToNearest = 2;

    private readonly Action openMainWindow;
    private TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;
    private TrayDashboardLayout layout = TrayDashboardLayoutPolicy.Resolve(
        TrayDashboardPreferenceDefaults.Preference.Controls!,
        TrayDashboardPreferenceDefaults.Preference.ExpandedControl);
    private bool changingPerformance;
    private bool changingFans;
    private bool chargeLimitPointerInteraction;
    private bool pinnedForEditing;
    private bool visible;
    private TrayDashboardPoint visibleAnchor;
    private DisplayArea? visibleDisplayArea;
    private long lastDeactivationHideTimestamp;
    private long revealStartedTimestamp;
    private nint revealWindowHandle;
    private nint revealTaskbarHandle;
    private TrayDashboardRect revealPlacement;
    private int revealStartY;
    private bool revealAnimationsEnabled;
    private bool revealAnimationActive;
    private bool awaitingRevealActivation;
    private int disposed;

    public TrayDashboardWindow(MainViewModel viewModel, Action openMainWindow)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.openMainWindow = openMainWindow
            ?? throw new ArgumentNullException(nameof(openMainWindow));
        InitializeComponent();
        Root.DataContext = ViewModel;

        ThemeManager.Apply(Root, ViewModel.Theme);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        DisplayInformation.DisplayContentsInvalidated +=
            OnDisplayContentsInvalidated;
        AppWindow.IsShownInSwitchers = false;
        try
        {
            OverlappedPresenter presenter = OverlappedPresenter.CreateForToolWindow();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
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
    }

    public MainViewModel ViewModel { get; }

    public void ApplyPreferences(TrayDashboardPreference preferences)
    {
        preference = TrayDashboardPreferenceDefaults.Normalize(preferences);
        layout = TrayDashboardLayoutPolicy.Resolve(
            preference.Controls!,
            preference.ExpandedControl);
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

        SetExpandedControl(layout.ExpandedControl);
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
        DisplayInformation.DisplayContentsInvalidated -=
            OnDisplayContentsInvalidated;
        CancelRevealAnimation();
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
        DisplayArea displayArea = knownDisplayArea ?? DisplayArea.GetFromPoint(
            new PointInt32(anchor.X, anchor.Y),
            DisplayAreaFallback.Nearest);
        visibleAnchor = anchor;
        visibleDisplayArea = displayArea;
        visible = true;
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
        if (!editing)
        {
            Activate();
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
        AppWindow.MoveAndResize(
            ToRectInt32(placement.Rect),
            displayArea);
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
                placement,
                WindowPlacementPolicy.ScaleToPixels(
                    RevealOffsetDip,
                    checked((int)nativeDpi)));
        return new ResolvedPlacement(
            placement,
            monitor,
            reveal);
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
            revealStartY = placement.Reveal.StartY;
            revealWindowHandle = WindowNative.GetWindowHandle(this);
            revealTaskbarHandle =
                WindowsPopupPositioner.FindTaskbarForMonitor(placement.Monitor);
            revealStartedTimestamp = 0;
            revealAnimationsEnabled = WindowsMotionSettings.AreAnimationsEnabled();
            awaitingRevealActivation = !editing;

            var staged = new TrayDashboardRect(
                placement.Rect.X,
                stagingY,
                placement.Rect.Width,
                placement.Rect.Height);
            AppWindow.MoveAndResize(ToRectInt32(staged), displayArea);
            Root.UpdateLayout();
            AppWindow.Show(activateWindow: false);
            WindowsPopupPositioner.PositionBehindTaskbar(
                revealWindowHandle,
                revealTaskbarHandle,
                staged.X,
                staged.Y,
                staged.Width,
                staged.Height);
            revealAnimationActive = true;
            CompositionTarget.Rendering += OnRevealRendering;
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

    private void OnRevealRendering(object? sender, object args)
    {
        if (!revealAnimationActive || !visible)
        {
            return;
        }

        if (revealStartedTimestamp == 0)
        {
            revealStartedTimestamp = Stopwatch.GetTimestamp();
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(revealStartedTimestamp);
        int y = revealAnimationsEnabled
            ? WindowsPopupSlidePolicy.ResolvePosition(
                revealStartY,
                revealPlacement.Y,
                elapsed,
                hiding: false)
            : revealPlacement.Y;
        try
        {
            WindowsPopupPositioner.PositionBehindTaskbar(
                revealWindowHandle,
                revealTaskbarHandle,
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

        if (awaitingRevealActivation)
        {
            awaitingRevealActivation = false;
            Activate();
            try
            {
                WindowsPopupPositioner.PositionBehindTaskbar(
                    revealWindowHandle,
                    revealTaskbarHandle,
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
            || y == revealPlacement.Y
            || WindowsPopupSlidePolicy.IsComplete(elapsed))
        {
            try
            {
                WindowsPopupPositioner.PositionTopmost(
                    revealWindowHandle,
                    revealPlacement.X,
                    revealPlacement.Y,
                    revealPlacement.Width,
                    revealPlacement.Height);
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
        if (revealAnimationActive)
        {
            CompositionTarget.Rendering -= OnRevealRendering;
        }
        revealAnimationActive = false;
        awaitingRevealActivation = false;
        revealStartedTimestamp = 0;
        revealAnimationsEnabled = false;
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

    private void SetExpandedControl(TrayDashboardControlKind? kind)
    {
        ExpandedControlContainer.Visibility = kind is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ExpandedPerformance.Visibility = kind == TrayDashboardControlKind.Performance
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandedFans.Visibility = kind == TrayDashboardControlKind.Fans
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandedBattery.Visibility = kind == TrayDashboardControlKind.Battery
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandedLighting.Visibility = kind == TrayDashboardControlKind.Lighting
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Hide(bool suppressNextTrayToggle = false)
    {
        if (!visible)
        {
            return;
        }

        CancelRevealAnimation();
        AppWindow.Hide();
        visible = false;
        if (suppressNextTrayToggle)
        {
            lastDeactivationHideTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && !pinnedForEditing
            && !awaitingRevealActivation)
        {
            Hide(suppressNextTrayToggle: true);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            args.Cancel = true;
            Hide();
        }
    }

    private void OnDisplayContentsInvalidated(
        DisplayInformation sender,
        object args) =>
        WindowsPopupPositioner.InvalidateTaskbarCache();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Theme))
        {
            ThemeManager.Apply(Root, ViewModel.Theme);
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && !pinnedForEditing)
        {
            Hide();
            args.Handled = true;
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs args)
    {
        pinnedForEditing = false;
        Hide();
        openMainWindow();
    }

    private async void OnPerformanceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingPerformance
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not PerformancePresetOption selected
            || selected.Value == ViewModel.SelectedPerformance)
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
            selector.SelectedItem = ViewModel.SelectedPerformanceOption;
            changingPerformance = false;
        }
    }

    private async void OnFanModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingFans
            || Volatile.Read(ref disposed) != 0
            || sender is not Microsoft.UI.Xaml.Controls.ComboBox selector
            || selector.SelectedItem is not FanModeOption selected
            || selected.Value == ViewModel.SelectedFanMode)
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
            selector.SelectedItem = ViewModel.SelectedFanModeOption;
            changingFans = false;
        }
    }

    private void OnChargeLimitPointerPressed(object sender, PointerRoutedEventArgs args) =>
        chargeLimitPointerInteraction = Volatile.Read(ref disposed) == 0;

    private async void OnChargeLimitPointerReleased(object sender, PointerRoutedEventArgs args)
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

    private readonly record struct ResolvedPlacement(
        TrayDashboardRect Rect,
        nint Monitor,
        TrayDashboardRevealGeometry Reveal);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);
}
