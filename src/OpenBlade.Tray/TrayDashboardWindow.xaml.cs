using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenBlade.Contracts;
using Windows.Graphics;

namespace OpenBlade.Tray;

public sealed partial class TrayDashboardWindow : Window, IDisposable
{
    private const int PopupWidth = 420;

    private readonly Action openMainWindow;
    private TrayDashboardPreference preference = TrayDashboardPreferenceDefaults.Preference;
    private bool changingPerformance;
    private bool changingFans;
    private bool chargeLimitPointerInteraction;
    private bool pinnedForEditing;
    private bool visible;
    private TrayDashboardPoint visibleAnchor;
    private DisplayArea? visibleDisplayArea;
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
        AppWindow.IsShownInSwitchers = false;
        try
        {
            OverlappedPresenter presenter = OverlappedPresenter.CreateForToolWindow();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
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
        IReadOnlyDictionary<TrayDashboardControlKind, Border> tiles = TilesByKind();
        foreach (Border tile in tiles.Values)
        {
            tile.Visibility = Visibility.Collapsed;
        }

        ControlGrid.RowDefinitions.Clear();
        int row = 0;
        int column = 0;
        foreach (TrayDashboardControlPreference control in preference.Controls!
                     .Where(control => control.IsVisible))
        {
            Border tile = tiles[control.Kind];
            bool wide = control.Size == TrayDashboardControlSize.Wide;
            if (wide && column != 0)
            {
                row++;
                column = 0;
            }

            EnsureRow(row);
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            Grid.SetColumnSpan(tile, wide ? 2 : 1);
            tile.Visibility = Visibility.Visible;

            if (wide || column == 1)
            {
                row++;
                column = 0;
            }
            else
            {
                column = 1;
            }
        }

        SetExpandedControl(preference.ExpandedControl);
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
            PositionAt(visibleAnchor, visibleDisplayArea);
        }
    }

    public void ToggleAt(TrayDashboardPoint anchor)
    {
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
                checked(workArea.X + workArea.Width),
                checked(workArea.Y + workArea.Height)),
            editing: true,
            displayArea);
    }

    public void EndEditSession()
    {
        pinnedForEditing = false;
        Hide();
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
        PositionAt(anchor, displayArea);
        AppWindow.Show(true);
        visible = true;
    }

    private void PositionAt(TrayDashboardPoint anchor, DisplayArea displayArea)
    {
        RectInt32 workArea = displayArea.WorkArea;
        int preferredHeight = CalculatePreferredHeight();
        TrayDashboardRect placement = TrayDashboardPlacement.Resolve(
            anchor,
            new TrayDashboardRect(
                workArea.X,
                workArea.Y,
                workArea.Width,
                workArea.Height),
            new TrayDashboardSize(PopupWidth, preferredHeight));
        AppWindow.MoveAndResize(
            new RectInt32(
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height),
            displayArea);
    }

    private int CalculatePreferredHeight()
    {
        int rows = 0;
        int column = 0;
        foreach (TrayDashboardControlPreference control in preference.Controls!
                     .Where(control => control.IsVisible))
        {
            if (control.Size == TrayDashboardControlSize.Wide)
            {
                if (column != 0)
                {
                    rows++;
                    column = 0;
                }
                rows++;
            }
            else if (column == 1)
            {
                rows++;
                column = 0;
            }
            else
            {
                column = 1;
            }
        }
        if (column != 0)
        {
            rows++;
        }

        int expandedHeight = preference.ExpandedControl is null ? 0 : 118;
        return Math.Clamp(118 + (rows * 122) + expandedHeight, 230, 720);
    }

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

    private IReadOnlyDictionary<TrayDashboardControlKind, Border> TilesByKind() =>
        new Dictionary<TrayDashboardControlKind, Border>
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

    private void Hide()
    {
        if (!visible)
        {
            return;
        }

        AppWindow.Hide();
        visible = false;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && !pinnedForEditing)
        {
            Hide();
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
}
