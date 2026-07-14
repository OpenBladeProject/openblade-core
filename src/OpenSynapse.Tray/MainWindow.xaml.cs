using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace OpenSynapse.Tray;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = MainViewModel.CreateDisconnected();
    private readonly UiPreferenceStore preferenceStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenSynapse",
        "preferences.json"));

    public MainWindow()
    {
        InitializeComponent();
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

    private void OnClosed(object? sender, EventArgs e) =>
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        viewModel.Theme = (await preferenceStore.LoadAsync()).Theme;
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
