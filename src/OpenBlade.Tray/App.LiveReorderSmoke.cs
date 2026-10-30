#if OPENBLADE_DEVELOPER_HARNESS
using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Platform;
using Application = Microsoft.UI.Xaml.Application;

namespace OpenBlade.Tray;

public partial class App
{
    // UI-only native-input fixture: no MainViewModel, service client or hardware commands.
    private static void ShowLiveReorderGestureWindow(string reportPath)
    {

        var window = new Window { Title = "OpenBlade reorder gesture test" };
        var behavior = new LiveReorderBehavior();
        var cards = new ObservableCollection<string> { "Card A", "Card B", "Card C" };
        var pages = new ObservableCollection<string> { "Page A", "Page B", "Page C" };
        var host = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        var navigation = new NavigationView
        {
            IsSettingsVisible = false,
            IsPaneOpen = true,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            Content = host,
        };
        var presentations = new Dictionary<string, Border>();
        var menu = new Dictionary<string, NavigationViewItem>();
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var button = new Microsoft.UI.Xaml.Controls.Button { Content = "Test button" };
        int clicks = 0;
        void Report() => File.WriteAllText(reportPath,
            $"cards={string.Join(',', cards)}; pages={string.Join(',', pages)}; slider={slider.Value}; clicks={clicks}");
        foreach (string card in cards)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = card, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            if (card == "Card A")
            {
                content.Children.Add(slider);
                content.Children.Add(button);
            }
            var border = new Border
            {
                Padding = new Thickness(20),
                MinHeight = 100,
                Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
                Child = content,
            };
            presentations.Add(card, border);
            host.Children.Add(border);
            behavior.Register(border, cards, card);
        }
        foreach (string page in pages)
        {
            var item = new NavigationViewItem { Content = page };
            menu.Add(page, item);
            navigation.MenuItems.Add(item);
            behavior.Register(item, pages, page);
        }
        cards.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < cards.Count; i++)
            {
                int index = host.Children.IndexOf(presentations[cards[i]]);
                host.Children.Move((uint)index, (uint)i);
            }
            Report();
        };
        pages.CollectionChanged += (_, _) =>
        {
            navigation.MenuItems.Clear();
            foreach (string page in pages)
            {
                navigation.MenuItems.Add(menu[page]);
            }
            Report();
        };
        slider.ValueChanged += (_, _) => Report();
        button.Click += (_, _) => { clicks++; Report(); };
        window.Content = navigation;
        window.Closed += (_, _) => { behavior.Dispose(); Environment.Exit(0); };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 650));
        Report();
        window.Activate();
    }



    private static async Task RunLiveReorderSmokeAsync(string reportPath)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string phase)
        {
            File.WriteAllText(reportPath, $"{elapsed.ElapsedMilliseconds}ms {phase}");
        }
        try
        {
            Phase("discover processor policy");
            var processor = WindowsProcessorEfficiencyPolicy.Discover();
            Phase("construct disconnected reorder window");
            var store = new UiPreferenceStore(reportPath + ".preferences.json");
            var window = new MainWindow(
                processor,
                new WindowsStartupRegistration(),
                localizationSnapshotMode: true,
                preferenceStore: store);
            Phase("constructor complete; apply fixture");
            ApplyControlAvailabilitySnapshotState(window);
            Phase("configure reorder fixture");
            window.ConfigureLiveReorderSmoke();
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 900));
            Phase("show reorder window");
            ShowNoninteractiveSnapshotWindow(window);
            Phase("wait initial layout");
            await WaitForSnapshotLayoutAsync();
            Phase("verify reorder");
            await window.VerifyLiveReorderSmokeAsync(async name =>
            {
                Phase("wait capture " + name);
                await WaitForSnapshotLayoutAsync();
                Phase("save capture " + name);
                await SaveSnapshotAsync((FrameworkElement)window.Content, reportPath + "." + name + ".png");
                Phase("saved capture " + name);
            }, Phase);
            Phase("close reorder window");
            window.Close();
            Phase("completed");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, exception.ToString());
            Environment.Exit(1);
        }
    }
}
#endif
