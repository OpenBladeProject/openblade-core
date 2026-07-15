using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private int disposed;

    public MainWindow()
    {
        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray must start on a dispatcher thread.");
        ViewModel = new MainViewModel(
            new ServiceClient(),
            new WinUiDispatcher(dispatcherQueue));
        InitializeComponent();
        Root.DataContext = ViewModel;
        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    public MainViewModel ViewModel { get; }

    public PerformancePreset SilentPreset { get; } = PerformancePreset.Silent;

    public PerformancePreset BalancedPreset { get; } = PerformancePreset.Balanced;

    public PerformancePreset PerformancePreset { get; } =
        OpenBlade.Contracts.PerformancePreset.Performance;

    public void ShowAndActivate() => AppWindow.Show(true);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Root.Loaded -= OnLoaded;
        AppWindow.Closing -= OnClosing;
        await ViewModel.DisposeAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        await ViewModel.StartAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        bool exitRequested = Microsoft.UI.Xaml.Application.Current is App app && app.ExitRequested;
        if (WindowLifecyclePolicy.Decide(exitRequested) == WindowCloseDecision.Hide)
        {
            args.Cancel = true;
            AppWindow.Hide();
        }
    }
}

public sealed class BooleanNegationToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
