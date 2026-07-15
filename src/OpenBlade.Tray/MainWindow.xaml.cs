using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace OpenBlade.Tray;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly UiPreferenceStore preferenceStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade",
        "preferences.json"));

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel(
            new ServiceClient(),
            new WpfUiDispatcher(Dispatcher),
            new DiagnosticArchiveSaver(new WpfDiagnosticArchiveDestination()));
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (WindowsThemeChangePolicy.ShouldReapply(viewModel.Theme, e.Category))
        {
            Dispatcher.Invoke(() => ThemeManager.Apply(viewModel.Theme));
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        await viewModel.DisposeAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        viewModel.Theme = (await preferenceStore.LoadAsync()).Theme;
        ThemeManager.Apply(viewModel.Theme);
        await viewModel.StartAsync();
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Theme))
        {
            return;
        }

        ThemeManager.Apply(viewModel.Theme);
        try
        {
            await preferenceStore.SaveAsync(new UiPreferences(viewModel.Theme));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Appearance changes remain usable even when preferences cannot be persisted.
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

public sealed class BooleanNegationToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
