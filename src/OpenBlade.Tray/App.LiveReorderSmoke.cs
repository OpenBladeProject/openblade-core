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
        var cardHost = new StackPanel { Spacing = 12 };
        var header = new TextBlock { Text = "Section A", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        var otherHeader = new TextBlock { Text = "Section B", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(header);
        section.Children.Add(cardHost);
        var otherSection = new StackPanel { Spacing = 8 };
        otherSection.Children.Add(otherHeader);
        var otherCard = new Border
        {
            Padding = new Thickness(20),
            MinHeight = 100,
            Background = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            Child = new TextBlock { Text = "Card D" },
        };
        otherSection.Children.Add(otherCard);
        var sections = new ObservableCollection<StackPanel> { section, otherSection };
        host.Children.Add(section);
        host.Children.Add(otherSection);
        behavior.Register(section, sections, section, origin => ReferenceEquals(origin, header));
        behavior.Register(otherSection, sections, otherSection, origin => ReferenceEquals(origin, otherHeader));
        var otherCards = new ObservableCollection<string> { "Card D" };
        behavior.Register(otherCard, otherCards, otherCards[0]);
        var navigation = new NavigationView
        {
            IsSettingsVisible = false,
            IsPaneOpen = true,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            Content = new ScrollViewer { Content = host },
        };
        var presentations = new Dictionary<string, Border>();
        var menu = new Dictionary<string, NavigationViewItem>();
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var button = new Microsoft.UI.Xaml.Controls.Button { Content = "Test button" };
        var showPreview = new Microsoft.UI.Xaml.Controls.Button { Content = "Show drag preview" };
        var clearPreview = new Microsoft.UI.Xaml.Controls.Button { Content = "Clear drag preview" };
        int clicks = 0;
        void Report() => File.WriteAllText(reportPath,
            $"cards={string.Join(',', cards)}; sections={string.Join(',', sections.Select(item => ReferenceEquals(item, section) ? "A" : "B"))}; pages={string.Join(',', pages)}; slider={slider.Value}; clicks={clicks}");
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
            cardHost.Children.Add(border);
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
                int index = cardHost.Children.IndexOf(presentations[cards[i]]);
                cardHost.Children.Move((uint)index, (uint)i);
            }
            Report();
        };
        sections.CollectionChanged += (_, _) =>
        {
            for (int index = 0; index < sections.Count; index++)
            {
                int current = host.Children.IndexOf(sections[index]);
                host.Children.Move((uint)current, (uint)index);
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
        showPreview.Click += (_, _) =>
        {
            behavior.EndDrag();
            behavior.TryBeginDrag(presentations["Card A"], presentations["Card A"]);
            var previewRoot = (UIElement)window.Content;
            Windows.Foundation.Point start = presentations["Card A"].TransformToVisual(previewRoot)
                .TransformPoint(new Windows.Foundation.Point());
            behavior.UpdateTarget(new Windows.Foundation.Point(start.X + 32, start.Y + 90));
            behavior.ShowIndicator(presentations["Card B"], after: true);
        };
        clearPreview.Click += (_, _) => behavior.EndDrag();
        host.Children.Add(showPreview);
        host.Children.Add(clearPreview);
        var root = new Grid();
        root.Children.Add(navigation);
        window.Content = root;
        window.Closed += (_, _) => { behavior.Dispose(); Environment.Exit(0); };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1250));
        Report();
        window.Activate();
    }



    private static async Task RunLiveReorderSmokeAsync(string reportPath)
    {
        try
        {
            File.WriteAllText(reportPath, "construct disconnected reorder window");
            var store = new UiPreferenceStore(reportPath + ".preferences.json");
            var window = new MainWindow(
                WindowsProcessorEfficiencyPolicy.Discover(),
                new WindowsStartupRegistration(),
                localizationSnapshotMode: true,
                preferenceStore: store);
            ApplyControlAvailabilitySnapshotState(window);
            window.ConfigureLiveReorderSmoke();
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 900));
            ShowNoninteractiveSnapshotWindow(window);
            // Exercise XAML hit testing on the private test desktop. Native no-activate
            // input guards remain in place; ordinary snapshot windows disable this.
            ((UIElement)window.Content).IsHitTestVisible = true;
            await WaitForSnapshotLayoutAsync();
            await window.VerifyLiveReorderSmokeAsync(async name =>
            {
                File.WriteAllText(reportPath, $"capture {name}");
                await WaitForSnapshotLayoutAsync();
                await SaveSnapshotAsync((FrameworkElement)window.Content, reportPath + "." + name + ".png");
            });
            window.Close();
            File.WriteAllText(reportPath, "completed");
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
