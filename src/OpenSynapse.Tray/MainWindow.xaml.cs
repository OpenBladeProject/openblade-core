using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace OpenSynapse.Tray;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = MainViewModel.CreateDisconnected();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainViewModel.Theme))
            {
                ThemeManager.Apply(viewModel.Theme);
            }
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(viewModel.Theme);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

        try
        {
            viewModel.ApplyStatus(await new ServiceClient().GetStatusAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or OperationCanceledException)
        {
            // The disconnected model is intentionally fail-closed.
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        bool exitRequested = System.Windows.Application.Current is App app && app.ExitRequested;
        if (WindowLifecyclePolicy.Decide(exitRequested) == WindowCloseDecision.Hide)
        {
            e.Cancel = true;
            Hide();
        }
    }
}

public sealed class BooleanNegationConverter : IValueConverter
{
    public static BooleanNegationConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
