using System.Globalization;
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
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
}

public sealed class BooleanNegationConverter : IValueConverter
{
    public static BooleanNegationConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
