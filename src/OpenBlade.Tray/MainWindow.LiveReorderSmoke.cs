#if OPENBLADE_DEVELOPER_HARNESS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;


namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    internal void ConfigureLiveReorderSmoke()
    {
        // Use the real presentation and preference observers, without service/session IPC.
        ApplyDashboardPreferences(DashboardCardPreferenceDefaults.Cards
            .Select(card => card with { IsPinned = card.Kind != DashboardCardKind.Display }).ToArray());
        ApplyPageLayoutPreferences(PageLayoutPreferenceDefaults.Layouts);
        ApplyPrimaryNavigationPreferences(PrimaryNavigationPreferenceDefaults.Items);
    }

    internal async Task VerifyLiveReorderSmokeAsync(Func<string, Task> capture)
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        static DependencyObject DragOrigin(FrameworkElement presentation)
        {
            DependencyObject? FindHeader(DependencyObject root)
            {
                if (CardReorder.GetIsHeader(root))
                {
                    return root;
                }
                for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
                {
                    DependencyObject? header = FindHeader(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index));
                    if (header is not null)
                    {
                        return header;
                    }
                }
                return null;
            }
            return FindHeader(presentation) ?? presentation;
        }

        await VerifyProvisionalReflowAsync(capture);
        FrameworkElement status = dashboardCardPresentations[DashboardCardKind.DeviceStatus];
        DashboardCardOption first = DashboardCards[0];
        DashboardCardOption second = DashboardCards[1];
        FrameworkElement firstCard = GetOrCreateDashboardCard(first);
        FrameworkElement secondCard = GetOrCreateDashboardCard(second);
        var originalTranslation = ReorderTransformOffset(firstCard);
        object originalTransform = firstCard.ReadLocalValue(UIElement.RenderTransformProperty);
        double sourceOpacity = firstCard.Opacity;
        int sourceZIndex = Canvas.GetZIndex(firstCard);
        var neighborTranslation = ReorderTransformOffset(secondCard);
        Require(liveReorder.IsRegistered(firstCard) && liveReorder.IsRegistered(secondCard), "Dashboard dragging requires an editor.");
        await capture("dashboard-before");
        Windows.Foundation.Point start = firstCard.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
        DependencyObject firstOrigin = DragOrigin(firstCard);
        Require(liveReorder.TryBeginDrag(firstCard, firstOrigin),
            $"Dashboard drag did not begin: loaded={firstCard.IsLoaded}, size={firstCard.ActualWidth}x{firstCard.ActualHeight}, root={firstCard.XamlRoot.Content.GetType().Name}, origin={firstOrigin.GetType().Name}, passive={LiveReorderBehavior.CanStartFrom(firstCard, firstOrigin)}, heading={CanStartSectionReorder(firstOrigin, firstCard)}.");
        liveReorder.UpdateTarget(new Windows.Foundation.Point(-10000, -10000));
        Require(ReorderTransformOffset(secondCard) == neighborTranslation,
            "A pointer outside the window selected a drop target.");
        var overlay = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Grid.SetRowSpan(overlay, Math.Max(1, Root.RowDefinitions.Count));
        Root.Children.Add(overlay);
        Root.UpdateLayout();
        Windows.Foundation.Point coveredTarget = secondCard.TransformToVisual(Root)
            .TransformPoint(new Windows.Foundation.Point(10, 10));
        liveReorder.UpdateTarget(coveredTarget);
        Require(ReorderTransformOffset(secondCard) == neighborTranslation,
            "A card behind an overlay became a drop target.");
        Root.Children.Remove(overlay);
        var preview = liveReorder.DragPreview;
        Require(ReferenceEquals(preview, firstCard) && preview.Opacity == sourceOpacity * 0.5,
            "Dragging did not lift the actual card at half opacity.");
        liveReorder.UpdateTarget(new Windows.Foundation.Point(start.X + 36, start.Y + 80));
        Require(Math.Abs(ReorderTransformOffset(firstCard).X - originalTranslation.X - 36) < 0.1
            && Math.Abs(ReorderTransformOffset(firstCard).Y - originalTranslation.Y - 80) < 0.1,
            $"The lifted card did not follow the cursor: translation={ReorderTransformOffset(firstCard)}, start={start}, active={liveReorder.DragPreview is not null}.");
        liveReorder.ShowIndicator(secondCard, after: true);
        await capture("dashboard-preview");
        Require(ReorderTransformOffset(secondCard) != neighborTranslation && DashboardCards[0] == first,
            "Neighbors did not move before release, or the preview saved the order early.");
        liveReorder.UpdateTarget(new Windows.Foundation.Point(start.X + 41, start.Y + 85));
        Require(Math.Abs(ReorderTransformOffset(firstCard).X - originalTranslation.X - 41) < 0.1
            && Math.Abs(ReorderTransformOffset(firstCard).Y - originalTranslation.Y - 85) < 0.1,
            "Reflow changed the cursor's grip on the lifted card.");
        liveReorder.ShowIndicator(secondCard, after: true);
        liveReorder.EndDrag();
        Require(liveReorder.DragPreview is null, "Canceled drag retained its preview.");
        Require(ReorderTransformOffset(firstCard) == originalTranslation && ReorderTransformOffset(secondCard) == neighborTranslation
            && Equals(firstCard.ReadLocalValue(UIElement.RenderTransformProperty), originalTransform)
            && firstCard.Opacity == sourceOpacity && Canvas.GetZIndex(firstCard) == sourceZIndex,
            "Cancel did not restore the original presentation.");
        Require(!liveReorder.TryMove(secondCard, after: true)
            && DashboardCards[0] == first, "Cancel changed card order.");
        Require(ElementCompositionPreview.GetElementChildVisual(secondCard) is null,
            "Canceled drag retained insertion feedback.");

        Require(liveReorder.TryBeginDrag(firstCard, firstOrigin), "Second dashboard drag did not begin.");
        Require(liveReorder.TryMove(secondCard, after: true), "Dashboard drop failed.");
        Require(DashboardCards[0] == second && DashboardCards[1] == first
            && ReferenceEquals(GetOrCreateDashboardCard(first), firstCard)
            && ReferenceEquals(dashboardCardPresentations[DashboardCardKind.DeviceStatus], status),
            "Dashboard drop lost saved order or live card identity.");
        Require(!DashboardCards.Single(card => card.Kind == DashboardCardKind.Display).IsPinned,
            "Reordering enabled a hidden card.");
        await capture("dashboard-after");

        Require(!liveReorder.TryBeginDrag(HomeNavigationItem, HomeNavigationItem),
            "An unregistered source began a drag.");
        Require(liveReorder.TryBeginDrag(firstCard, firstOrigin), "Cross-group setup failed.");
        Require(!liveReorder.TryMove(ProfilesNavigationItem, false),
            "A dashboard card moved into navigation.");
        double originalOpacity = firstCard.Opacity;
        Require(liveReorder.TryBeginDrag(firstCard, firstOrigin), "Detached-source setup failed.");
        DashboardCardHost.Children.Remove(firstCard);
        liveReorder.UpdateTarget(new Windows.Foundation.Point(0, 0));
        bool movedAfterRemoval = liveReorder.TryMove(secondCard, true);
        Require(!movedAfterRemoval && firstCard.Opacity == originalOpacity,
            $"A detached source retained its drag: moved={movedAfterRemoval}, opacity={firstCard.Opacity}, expected={originalOpacity}.");
        RefreshDashboardCards();
        await capture("dashboard-restored");
        SelectLocalizationSnapshotPage("settings");
        await capture("settings-before");
        FrameworkElement appearance = pageSectionPresentations[PageSectionKind.SettingsAppearance];
        appearance.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await capture("settings-cards-before");
        var rowGroup = cardRowGroups.Values.First(group => group.Id.StartsWith("Page.SettingsAppearance.", StringComparison.Ordinal));
        FrameworkElement[] visibleRows = rowGroup.Items
            .Where(row => row.IsLoaded && row.Visibility == Visibility.Visible && row.ActualHeight > 0)
            .Take(2).ToArray();
        Require(visibleRows.Length == 2, "Settings row fixture did not expose two live cards.");
        FrameworkElement rowSource = visibleRows[0];
        FrameworkElement rowTarget = visibleRows[1];
        Require(!liveReorder.TryBeginDrag(appearance, rowSource), "A card body started a whole-section move.");
        Require(liveReorder.TryBeginDrag(rowSource, rowSource), "Within-section card drag did not begin.");
        FrameworkElement foreignRow = cardRowGroups.Values.First(group =>
            group.Id.StartsWith("Page.SettingsBehavior.", StringComparison.Ordinal)).Items.First(row =>
                row.IsLoaded && row.Visibility == Visibility.Visible && row.ActualHeight > 0);
        Require(!liveReorder.TryMove(foreignRow, true),
            "A card moved outside its section.");
        Require(liveReorder.TryBeginDrag(rowSource, rowSource)
            && liveReorder.TryMove(rowTarget, true)
            && rowGroup.Items.IndexOf(rowSource) > rowGroup.Items.IndexOf(rowTarget),
            "A card did not move within its section.");
        string[] savedRows = rowGroup.Items.Select(row => CardReorder.GetCardId(row)!).ToArray();
        // Fixed notices keep their exact slots even when multiple cards reverse around them.
        var fixedSlots = new StackPanel();
        var slotA = new Border();
        var noticeX = new Border();
        var slotB = new Border();
        var noticeY = new Border();
        var slotC = new Border();
        foreach (var child in new[] { slotA, noticeX, slotB, noticeY, slotC })
        {
            fixedSlots.Children.Add(child);
        }
        RefreshCardRows(fixedSlots, new CardRowGroup("fixture", new([slotC, slotB, slotA])));
        Require(fixedSlots.Children.SequenceEqual(new[] { slotC, noticeX, slotB, noticeY, slotA }),
            "Reordering cards shifted a fixed notice.");
        await capture("settings-cards-after");
        foreach (var pair in pageSectionHosts)
        {
            Require(pageSections[pair.Key].All(section =>
                liveReorder.IsRegistered(pageSectionPresentations[section.Kind])),
                $"Page {pair.Key} has a section without direct dragging.");
        }
        var sections = pageSections[CustomizablePageKind.Settings];
        PageSectionOption originalSection = sections[0];
        PageSectionOption targetSection = sections[2];
        sections[1].IsVisible = false;
        FrameworkElement sectionCard = pageSectionPresentations[originalSection.Kind];
        FrameworkElement sectionTarget = pageSectionPresentations[targetSection.Kind];
        Require(liveReorder.TryBeginDrag(sectionCard, DragOrigin(sectionCard)), "Section header drag failed.");
        Require(ReferenceEquals(liveReorder.DragPreview, sectionCard),
            "Section preview omitted its cards.");
        Require(liveReorder.TryMove(sectionTarget, true)
            && sections[2] == originalSection && !sections[0].IsVisible,
            "Section drop changed hidden siblings or did not preserve the canonical order.");
        Require(SettingsSectionHost.Children.IndexOf(sectionCard) > SettingsSectionHost.Children.IndexOf(sectionTarget),
            "Section presentation did not follow saved order.");
        Require(rowGroup.Items.Select(row => CardReorder.GetCardId(row)).SequenceEqual(savedRows),
            "Moving a section reset its inner card order.");
        await capture("settings-after");

        PrimaryNavigationItemOption navigationSource = PrimaryNavigationItems[0];
        PrimaryNavigationItemOption navigationTarget = PrimaryNavigationItems[2];
        NavigationViewItem sourceItem = GetPrimaryNavigationItem(navigationSource.Kind);
        NavigationViewItem targetItem = GetPrimaryNavigationItem(navigationTarget.Kind);
        MainNavigation.SelectedItem = sourceItem;
        Require(liveReorder.TryBeginDrag(sourceItem, sourceItem), "Menu drag failed.");
        liveReorder.ShowIndicator(targetItem, after: true);
        await capture("menu-insertion");
        Require(liveReorder.TryMove(targetItem, true), "Menu drop failed.");
        Require(PrimaryNavigationItems[2] == navigationSource && ReferenceEquals(MainNavigation.SelectedItem, sourceItem)
            && ReferenceEquals(MainNavigation.MenuItems[0], HomeNavigationItem)
            && MainNavigation.FooterMenuItems.Contains(AboutNavigationItem)
            && !liveReorder.IsRegistered(HomeNavigationItem) && !liveReorder.IsRegistered(AboutNavigationItem),
            "Menu reorder lost selection or moved a fixed endpoint.");
        await capture("menu-after");

        // Exercise origin classification on real, templated controls and selectable values.
        SelectLocalizationSnapshotPage("laptop");
        await capture("laptop");
        foreach (FrameworkElement control in new FrameworkElement[]
                 { new Slider(), new Microsoft.UI.Xaml.Controls.Button(), new ToggleSwitch(),
                   new Microsoft.UI.Xaml.Controls.TextBox(), new TextBlock { IsTextSelectionEnabled = true } })
        {
            var surface = new Border { Child = control };
            Require(!LiveReorderBehavior.CanStartFrom(surface, control),
                $"Dragging stole interaction from {control.GetType().Name}.");
        }
        var heading = new TextBlock { Text = "Card heading" };
        var passiveSurface = new Border { Child = heading };
        Root.Children.Add(passiveSurface);
        Root.UpdateLayout();
        Require(LiveReorderBehavior.CanStartFrom(passiveSurface, heading), "Passive card heading is not draggable.");
        Root.Children.Remove(passiveSurface);

        await preferenceSaveQueue.DisposeAsync();
        UiPreferences saved = await preferenceStore.LoadAsync();
        Require(saved.CardOrders?.Single(group => group.GroupId == rowGroup.Id).CardIds.SequenceEqual(savedRows) == true,
            "Within-section card ordering was not persisted.");
        Require(saved.EffectiveDashboardCards.Select(card => card.Kind).SequenceEqual(DashboardCards.Select(card => card.Kind))
            && saved.EffectivePrimaryNavigationItems.SequenceEqual(PrimaryNavigationItems.Select(item => item.Kind))
            && saved.EffectivePageLayouts.Single(layout => layout.Page == CustomizablePageKind.Settings)
                .Sections.Select(section => (section.Kind, section.IsVisible))
                .SequenceEqual(sections.Select(section => (section.Kind, section.IsVisible))),
            "Direct ordering was not persisted with hidden state.");
        liveReorder.Dispose();
        liveReorder.Register(firstCard, DashboardCards, first);
        Require(!liveReorder.TryBeginDrag(firstCard, firstCard),
            "A late refresh revived a disposed reorder behavior.");
        liveReorder.ShowIndicator(secondCard, false);
        Require(ElementCompositionPreview.GetElementChildVisual(secondCard) is null,
            "A disposed reorder behavior recreated insertion feedback.");
        Require(!liveReorder.IsRegistered(firstCard) && !liveReorder.IsRegistered(sourceItem), "Disposed reorder behavior retained input handlers.");
    }

    private async Task VerifyProvisionalReflowAsync(Func<string, Task> capture)
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        using var behavior = new LiveReorderBehavior();
        var host = new StackPanel
        {
            Width = 260,
            Spacing = 4,
            Margin = new Thickness(240, 50, 0, 0),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Border Card(string text, double height) => new()
        {
            Height = height,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray),
            Child = new TextBlock { Text = text },
        };
        Border a = Card("Short card", 40), b = Card("Tall card", 70), c = Card("Medium card", 50);
        Border noticeX = Card("Fixed notice", 15), noticeY = Card("Other fixed notice", 20);
        var cards = new System.Collections.ObjectModel.ObservableCollection<FrameworkElement> { a, b, c };
        var group = new CardRowGroup("reflow-fixture", cards);
        foreach (FrameworkElement child in new[] { a, noticeX, b, noticeY, c })
        {
            host.Children.Add(child);
        }
        foreach (FrameworkElement card in cards)
        {
            behavior.Register(card, cards, card);
        }
        int saves = 0;
        cards.CollectionChanged += (_, _) => { saves++; RefreshCardRows(host, group); };
        Grid.SetRowSpan(host, Math.Max(1, Root.RowDefinitions.Count));
        Grid.SetColumnSpan(host, Math.Max(1, Root.ColumnDefinitions.Count));
        Canvas.SetZIndex(host, 999);
        Root.Children.Add(host);
        Root.UpdateLayout();
        try
        {
            await capture("uneven-cards-before");
            var original = host.Children.OfType<FrameworkElement>().Select(ReorderTransformOffset).ToArray();
            Windows.Foundation.Point start = a.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
            Windows.Foundation.Point destination = c.TransformToVisual(Root)
                .TransformPoint(new Windows.Foundation.Point(20, c.ActualHeight - 10));
            Require(destination.X < Root.ActualWidth && destination.Y < Root.ActualHeight,
                $"Uneven-card fixture exceeds the root: target={destination}, root={Root.ActualWidth}x{Root.ActualHeight}.");
            Require(behavior.TryBeginDrag(a, a), "Uneven-card drag did not begin.");
            behavior.UpdateTarget(destination);
            await capture("uneven-cards-reflow");
            var translations = host.Children.OfType<FrameworkElement>().Select(ReorderTransformOffset).ToArray();
            Require(ReorderTransformOffset(b).Y != 0 && ReorderTransformOffset(c).Y != 0 && ReorderTransformOffset(noticeX).Y != 0
                && saves == 0 && cards.SequenceEqual(new[] { a, b, c }),
                $"Uneven cards/notices did not reflow provisionally: b={ReorderTransformOffset(b)}, c={ReorderTransformOffset(c)}, notice={ReorderTransformOffset(noticeX)}, saves={saves}, active={behavior.DragPreview is not null}.");
            behavior.UpdateTarget(destination);
            Require(host.Children.OfType<FrameworkElement>().Select(ReorderTransformOffset).SequenceEqual(translations),
                "A stationary cursor oscillated the proposed order.");
            Require(Math.Abs(ReorderTransformOffset(a).X - (destination.X - start.X)) < 0.1
                && Math.Abs(ReorderTransformOffset(a).Y - (destination.Y - start.Y)) < 0.1,
                "Uneven-card reflow lost the cursor grip.");
            behavior.EndDrag();
            Require(saves == 0 && host.Children.OfType<FrameworkElement>().Select(ReorderTransformOffset).SequenceEqual(original),
                "Cancel saved the provisional order or left a translated notice.");
            Require(behavior.TryBeginDrag(a, a), "Uneven-card commit did not begin.");
            behavior.ShowIndicator(c, true);
            Require(behavior.TryMove(c, true) && saves == 1 && cards.SequenceEqual(new[] { b, c, a }),
                "Release did not commit exactly one canonical move.");
            Root.UpdateLayout();
            Require(host.Children.SequenceEqual(new[] { b, noticeX, c, noticeY, a })
                && host.Children.OfType<FrameworkElement>().All(child => ReorderTransformOffset(child) == new Windows.Foundation.Point()),
                "Drop changed fixed notice slots or retained preview transforms.");
            Border appeared = Card("Newly visible card", 60);
            appeared.Visibility = Visibility.Collapsed;
            host.Children.Add(appeared);
            cards.Add(appeared);
            behavior.Register(appeared, cards, appeared);
            Root.UpdateLayout();
            Require(behavior.TryBeginDrag(a, a), "Changing-layout drag did not begin.");
            appeared.Visibility = Visibility.Visible;
            Root.UpdateLayout();
            behavior.UpdateTarget(destination);
            Require(behavior.DragPreview is null && a.Opacity == 1,
                "A newly visible trailing card did not cancel the stale layout.");
        }
        finally
        {
            behavior.EndDrag();
            Root.Children.Remove(host);
        }
    }

    private static Windows.Foundation.Point ReorderTransformOffset(FrameworkElement element) =>
        element.RenderTransform?.TransformPoint(new Windows.Foundation.Point()) ?? new Windows.Foundation.Point();
}
#endif
