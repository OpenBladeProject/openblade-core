using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using XamlControl = Microsoft.UI.Xaml.Controls.Control;
using XamlScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;

namespace OpenBlade.Tray;

internal sealed record GlobalSearchResult(
    string Title,
    string Breadcrumb,
    NavigationViewItem? Navigation = null,
    XamlScrollViewer? Page = null,
    FrameworkElement? Anchor = null,
    string? RowId = null,
    string Aliases = "",
    Func<bool>? IsExposed = null)
{
    public string AccessibleName => Breadcrumb.Length == 0 ? Title : $"{Title}, {Breadcrumb}";
    public Visibility BreadcrumbVisibility => Breadcrumb.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class MainWindow
{
    // The app can be resized to 800 DIPs. Reserve space for the window
    // caption buttons, the app name and a draggable area on either side.
    private const double GlobalSearchCompactWidth = 820;
    private const int GlobalSearchMaxResults = 7;
    private string GlobalSearchShortcutLabel { get; } = "Ctrl+K";
    private IReadOnlyList<GlobalSearchResult>? globalSearchCatalog;
    private WeakReference<XamlControl>? globalSearchPreviousFocus;
    private string? globalSearchInlineResultsQuery;
    private XamlScrollViewer? globalSearchArrivalPage;
    private DispatcherQueueTimer? globalSearchArrivalTimer;
    private int globalSearchNavigationVersion;
    private bool globalSearchDragRefreshPending;

    private void OnGlobalSearchShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ClearGlobalSearchArrival();
        SetGlobalSearchCompact(args.NewSize.Width < GlobalSearchCompactWidth);
    }

    private void OnGlobalSearchTitleSlotSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (GlobalSearchTitleSlot.XamlRoot is null || GlobalSearchHost.Visibility != Visibility.Visible)
        {
            return;
        }

        // Center in the window while staying inside the native title bar's
        // content slot, which reserves space for the caption and drag regions.
        double origin = GlobalSearchTitleSlot.TransformToVisual(AppTitleBar).TransformPoint(default).X;
        double width = GlobalSearchHost.Width;
        double offset = Math.Clamp((AppTitleBar.ActualWidth - width) / 2 - origin,
            0, Math.Max(0, args.NewSize.Width - width));
        GlobalSearchHost.Margin = new Thickness(offset, 0, 0, 0);
        RefreshGlobalSearchDragRegions();
    }

    private void SetGlobalSearchCompact(bool compact)
    {
        Visibility inlineVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (GlobalSearchHost.Visibility == inlineVisibility)
        {
            return;
        }

        GlobalSearchBox.IsSuggestionListOpen = false;
        GlobalSearchFlyout.Hide();
        GlobalSearchHost.Visibility = inlineVisibility;
        GlobalSearchButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        RefreshGlobalSearchDragRegions();
    }

    private void RefreshGlobalSearchDragRegions()
    {
        if (hostAttached && !globalSearchDragRefreshPending)
        {
            globalSearchDragRefreshPending = dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                globalSearchDragRefreshPending = false;
                if (hostAttached)
                {
                    AppTitleBar.RecomputeDragRegions();
                }
            });
        }
    }

    private void OnGlobalSearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (GlobalSearchButton.Visibility == Visibility.Visible)
        {
            GlobalSearchFlyout.ShowAt(GlobalSearchButton);
        }
        else
        {
            RememberGlobalSearchFocus(FocusManager.GetFocusedElement(XamlRoot) as XamlControl);
            FocusGlobalSearchBox(GlobalSearchBox);
            UpdateGlobalSearchSuggestions(GlobalSearchBox);
        }
        args.Handled = true;
    }

    private void OnGlobalSearchGettingFocus(UIElement sender, GettingFocusEventArgs args) =>
        RememberGlobalSearchFocus(args.OldFocusedElement as XamlControl);

    private void OnGlobalSearchGotFocus(object sender, RoutedEventArgs args)
    {
        if (GlobalSearchBox.Text != globalSearchInlineResultsQuery)
        {
            UpdateGlobalSearchSuggestions(GlobalSearchBox);
        }
    }

    private void RememberGlobalSearchFocus(XamlControl? control)
    {
        if (control is not null
            && !IsGlobalSearchDescendant(control, GlobalSearchHost)
            && !IsGlobalSearchDescendant(control, CompactGlobalSearchHost))
        {
            globalSearchPreviousFocus = new WeakReference<XamlControl>(control);
        }
    }

    private static bool IsGlobalSearchDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private void OnGlobalSearchFlyoutOpening(object? sender, object args)
    {
        CompactGlobalSearchHost.Width = Math.Clamp(ActualWidth - 56, 280, 720);
    }

    private void OnGlobalSearchFlyoutOpened(object? sender, object args)
    {
        FocusGlobalSearchBox(CompactGlobalSearchBox);
        UpdateGlobalSearchSuggestions(CompactGlobalSearchBox);
    }

    private void OnGlobalSearchFlyoutClosed(object? sender, object args)
    {
        CompactGlobalSearchBox.IsSuggestionListOpen = false;
        CompactGlobalSearchBox.Text = string.Empty;
        CompactGlobalSearchBox.ItemsSource = null;
        globalSearchPreviousFocus = null;
    }

    private static void FocusGlobalSearchBox(AutoSuggestBox box)
    {
        box.ApplyTemplate();
        if (FindDescendant<Microsoft.UI.Xaml.Controls.TextBox>(box, "TextBox") is { } editor)
        {
            editor.Focus(FocusState.Keyboard);
            editor.SelectAll();
        }
        else
        {
            box.Focus(FocusState.Keyboard);
        }
    }

    private void OnGlobalSearchPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || sender is not AutoSuggestBox box)
        {
            return;
        }

        box.IsSuggestionListOpen = false;
        if (ReferenceEquals(box, CompactGlobalSearchBox))
        {
            GlobalSearchFlyout.Hide();
            args.Handled = true;
            return;
        }
        if (globalSearchPreviousFocus is null
            || !globalSearchPreviousFocus.TryGetTarget(out XamlControl? previous)
            || !previous.Focus(FocusState.Keyboard))
        {
            if (MainNavigation.SelectedItem is NavigationViewItem navigation)
            {
                navigation.Focus(FocusState.Keyboard);
            }
        }
        globalSearchPreviousFocus = null;
        args.Handled = true;
    }

    private void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (ReferenceEquals(sender, GlobalSearchBox))
        {
            GlobalSearchShortcutHint.Visibility =
                sender.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        bool active = ReferenceEquals(sender, GlobalSearchBox)
            ? GlobalSearchHost.Visibility == Visibility.Visible
            : GlobalSearchButton.Visibility == Visibility.Visible;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && active
            && sender.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
            && IsGlobalSearchDescendant(focused, sender))
        {
            UpdateGlobalSearchSuggestions(sender);
        }
    }

    private void UpdateGlobalSearchSuggestions(AutoSuggestBox box)
    {
        if (ReferenceEquals(box, GlobalSearchBox))
        {
            globalSearchInlineResultsQuery = box.Text;
        }

        if (string.IsNullOrWhiteSpace(box.Text))
        {
            box.ItemsSource = null;
            box.IsSuggestionListOpen = false;
            return;
        }

        GlobalSearchResult[] results = FindGlobalSearchResults(box.Text);
        box.ItemsSource = results.Length == 0
            ? [new GlobalSearchResult(
                LocalizedResources.Get("GlobalSearch_NoResults", "No matching pages or settings"), string.Empty)]
            : results;
        box.IsSuggestionListOpen = true;
    }

    private GlobalSearchResult[] FindGlobalSearchResults(string text)
    {
        string[] terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string query = string.Join(' ', terms);
        CompareInfo comparison = CultureInfo.CurrentUICulture.CompareInfo;
        globalSearchCatalog ??= BuildGlobalSearchCatalog();
        return globalSearchCatalog
            .Select(result => (Result: result, Score: GlobalSearchRanking.Score(
                terms, query, result.Title, result.Breadcrumb, result.Aliases, comparison)))
            .Where(entry => entry.Score.HasValue)
            .OrderBy(entry => entry.Score)
            .Select(entry => entry.Result)
            .Where(IsGlobalSearchResultAvailable)
            .Take(GlobalSearchMaxResults)
            .ToArray();
    }

    private bool IsGlobalSearchResultAvailable(GlobalSearchResult result)
    {
        if (result.Navigation is not { Visibility: Visibility.Visible }
            || result.Page is not { } page
            || (ReferenceEquals(page, LightingPage) && !localizationSnapshotMode && !ViewModel.IsLightingVisible)
            || result.IsExposed?.Invoke() == false)
        {
            return false;
        }
        if (result.Anchor is not { } anchor)
        {
            return true;
        }
        if (!HasVisibleSearchPath(anchor, page.Content))
        {
            return false;
        }

        if (result.RowId is not null && anchor is Expander { IsExpanded: false } expander)
        {
            // A collapsed expander may not realize its rows. Only offer a
            // nested result with an explicit, satisfied availability predicate.
            return expander.IsEnabled && result.IsExposed is not null;
        }

        // Canonical template rows may not be realized until the page opens.
        // Live visibility is authoritative as soon as the row exists.
        return result.RowId is not { } rowId
            || FindGlobalSearchRow(anchor, rowId) is not { } row
            || HasVisibleSearchPath(row, anchor);
    }

    private static bool HasVisibleSearchPath(FrameworkElement element, object? ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = GetGlobalSearchParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? GetGlobalSearchParent(DependencyObject element) =>
        (element as FrameworkElement)?.Parent ?? VisualTreeHelper.GetParent(element);

    private static FrameworkElement? FindGlobalSearchRow(DependencyObject root, string id)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element
                && (string.Equals(CardReorder.GetCardId(element), id, StringComparison.Ordinal)
                    || string.Equals(element.Name, id, StringComparison.Ordinal)))
            {
                return element;
            }
            if (FindGlobalSearchRow(child, id) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }

    private void OnGlobalSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        SubmitGlobalSearch(sender, args.QueryText, args.ChosenSuggestion as GlobalSearchResult);

    private void SubmitGlobalSearch(AutoSuggestBox sender, string query, GlobalSearchResult? chosen)
    {
        // The native text-change event is debounced. A click or Enter can
        // arrive while the list still shows results for the previous query.
        GlobalSearchResult[] current = FindGlobalSearchResults(query);
        GlobalSearchResult? result = chosen is null ? current.FirstOrDefault()
            : current.Contains(chosen) ? chosen : null;
        if (result is null)
        {
            UpdateGlobalSearchSuggestions(sender);
            return;
        }

        sender.IsSuggestionListOpen = false;
        sender.Text = string.Empty;
        sender.ItemsSource = null;
        if (ReferenceEquals(sender, CompactGlobalSearchBox))
        {
            GlobalSearchFlyout.Hide();
        }
        globalSearchPreviousFocus = null;
        OpenGlobalSearchResult(result);
    }

    private void OpenGlobalSearchResult(GlobalSearchResult result)
    {
        NavigationViewItem navigation = result.Navigation!;
        XamlScrollViewer page = result.Page!;
        if (ReferenceEquals(navigation, SettingsNavigationItem) && trayDashboardCustomizationActive)
        {
            EndTrayDashboardCustomization(showSettingsOverview: true);
        }
        if (!ReferenceEquals(MainNavigation.SelectedItem, navigation))
        {
            MainNavigation.SelectedItem = navigation;
        }
        else if (page.Visibility != Visibility.Visible)
        {
            ApplyNavigationDestination((string)navigation.Tag);
        }

        int version = ++globalSearchNavigationVersion;
        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != globalSearchNavigationVersion || Volatile.Read(ref disposed) != 0
                || !ReferenceEquals(MainNavigation.SelectedItem, navigation)
                || page.Visibility != Visibility.Visible || !IsGlobalSearchResultAvailable(result))
            {
                return;
            }

            if (result.Anchor is not { } anchor)
            {
                page.ChangeView(null, 0, null, disableAnimation: true);
                navigation.Focus(FocusState.Keyboard);
                return;
            }

            if (anchor is Expander { IsEnabled: true, IsExpanded: false } collapsedAnchor)
            {
                collapsedAnchor.IsExpanded = true;
            }
            anchor.UpdateLayout();
            FrameworkElement? target = result.RowId is { } rowId
                ? FindGlobalSearchRow(anchor, rowId) : anchor;
            if (target is null || !HasVisibleSearchPath(target, page.Content))
            {
                return;
            }

            bool expanded = false;
            for (DependencyObject? current = target; current is not null && !ReferenceEquals(current, page);
                current = GetGlobalSearchParent(current))
            {
                if (current is Expander { IsEnabled: true, IsExpanded: false } expander)
                {
                    expander.IsExpanded = true;
                    expanded = true;
                }
            }
            if (expanded)
            {
                target.UpdateLayout();
            }

            XamlControl? focusTarget = target as XamlControl
                ?? FocusManager.FindFirstFocusableElement(target) as XamlControl;
            if (focusTarget?.Focus(FocusState.Keyboard) != true)
            {
                navigation.Focus(FocusState.Keyboard);
            }
            target.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
            });
            dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (version == globalSearchNavigationVersion && Volatile.Read(ref disposed) == 0
                    && ReferenceEquals(MainNavigation.SelectedItem, navigation) && IsGlobalSearchResultAvailable(result))
                {
                    ShowGlobalSearchArrival(page, target);
                }
            });
        });
    }

    private void ShowGlobalSearchArrival(XamlScrollViewer page, FrameworkElement target)
    {
        if (!target.IsLoaded || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            return;
        }

        Rect bounds = target.TransformToVisual(GlobalSearchArrivalOverlay)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        Rect viewport = page.TransformToVisual(GlobalSearchArrivalOverlay)
            .TransformBounds(new Rect(0, 0, page.ActualWidth, page.ActualHeight));
        double left = Math.Max(bounds.Left, viewport.Left);
        double top = Math.Max(bounds.Top, viewport.Top);
        double right = Math.Min(bounds.Right, viewport.Right);
        double bottom = Math.Min(bounds.Bottom, viewport.Bottom);
        if (right <= left || bottom <= top)
        {
            return;
        }

        HideGlobalSearchArrival();
        globalSearchArrivalPage = page;
        page.ViewChanging += OnGlobalSearchArrivalViewChanging;
        Canvas.SetLeft(GlobalSearchArrivalOutline, left);
        Canvas.SetTop(GlobalSearchArrivalOutline, top);
        GlobalSearchArrivalOutline.Width = right - left;
        GlobalSearchArrivalOutline.Height = bottom - top;
        GlobalSearchArrivalOutline.Visibility = Visibility.Visible;
        if (globalSearchArrivalTimer is null)
        {
            globalSearchArrivalTimer = dispatcherQueue.CreateTimer();
            globalSearchArrivalTimer.Interval = TimeSpan.FromMilliseconds(900);
            globalSearchArrivalTimer.IsRepeating = false;
            globalSearchArrivalTimer.Tick += OnGlobalSearchArrivalExpired;
        }
        globalSearchArrivalTimer.Start();
    }

    private void OnGlobalSearchArrivalExpired(DispatcherQueueTimer sender, object args) =>
        HideGlobalSearchArrival();

    private void OnGlobalSearchArrivalViewChanging(object? sender, ScrollViewerViewChangingEventArgs args) =>
        HideGlobalSearchArrival();

    private void HideGlobalSearchArrival()
    {
        if (globalSearchArrivalPage is { } page)
        {
            page.ViewChanging -= OnGlobalSearchArrivalViewChanging;
            globalSearchArrivalPage = null;
            globalSearchArrivalTimer?.Stop();
            GlobalSearchArrivalOutline.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseGlobalSearch()
    {
        GlobalSearchFlyout.Hide();
        GlobalSearchBox.IsSuggestionListOpen = false;
        GlobalSearchBox.ItemsSource = null;
        globalSearchInlineResultsQuery = null;
        CompactGlobalSearchBox.IsSuggestionListOpen = false;
        CompactGlobalSearchBox.ItemsSource = null;
        ClearGlobalSearchArrival();
        if (globalSearchArrivalTimer is not null)
        {
            globalSearchArrivalTimer.Tick -= OnGlobalSearchArrivalExpired;
            globalSearchArrivalTimer = null;
        }
    }


    private void ReleaseGlobalSearch()
    {
        // A language change keeps the native window alive. Retire the
        // accelerator and the title bar's search controls with this view.
        Root.KeyboardAccelerators.Remove(GlobalSearchAccelerator);
        GlobalSearchAccelerator.Invoked -= OnGlobalSearchShortcut;
        Root.SizeChanged -= OnGlobalSearchShellSizeChanged;
        GlobalSearchTitleSlot.SizeChanged -= OnGlobalSearchTitleSlotSizeChanged;
        GlobalSearchBox.GettingFocus -= OnGlobalSearchGettingFocus;
        GlobalSearchBox.GotFocus -= OnGlobalSearchGotFocus;
        GlobalSearchBox.TextChanged -= OnGlobalSearchTextChanged;
        GlobalSearchBox.QuerySubmitted -= OnGlobalSearchSubmitted;
        GlobalSearchBox.PreviewKeyDown -= OnGlobalSearchPreviewKeyDown;
        CompactGlobalSearchBox.TextChanged -= OnGlobalSearchTextChanged;
        CompactGlobalSearchBox.QuerySubmitted -= OnGlobalSearchSubmitted;
        CompactGlobalSearchBox.PreviewKeyDown -= OnGlobalSearchPreviewKeyDown;
        GlobalSearchFlyout.Opening -= OnGlobalSearchFlyoutOpening;
        GlobalSearchFlyout.Opened -= OnGlobalSearchFlyoutOpened;
        GlobalSearchFlyout.Closed -= OnGlobalSearchFlyoutClosed;
        CloseGlobalSearch();
        GlobalSearchButton.Flyout = null;
        GlobalSearchFlyout.Content = null;
        AppTitleBar.Content = null;
        AppTitleBar.RightHeader = null;
        globalSearchCatalog = null;
        globalSearchPreviousFocus = null;
    }

    private void ClearGlobalSearchArrival()
    {
        globalSearchNavigationVersion++;
        HideGlobalSearchArrival();
    }

#if OPENBLADE_DEVELOPER_HARNESS
    internal void ShowGlobalSearchSnapshot(bool compact)
    {
        ApplyTheme(compact ? ThemePreference.Dark : ThemePreference.Light);
        Root.Background = new SolidColorBrush(compact ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        AppTitleBar.Foreground = new SolidColorBrush(compact ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        SetGlobalSearchCompact(compact);
        Root.UpdateLayout();
        if (compact)
        {
            GlobalSearchFlyout.ShowAt(GlobalSearchButton);
        }
    }

    internal void SetGlobalSearchSnapshotQuery(string query, bool compact)
    {
        AutoSuggestBox box = compact ? CompactGlobalSearchBox : GlobalSearchBox;
        if (box.XamlRoot is null)
        {
            throw new InvalidOperationException("The search box was not attached to the snapshot.");
        }
        box.ApplyTemplate();
        box.Text = query;
        UpdateGlobalSearchSuggestions(box);
        if (box.ItemsSource is not GlobalSearchResult[] results
            || results.FirstOrDefault()?.RowId != "MainWindow_TextBlockUpdateChannel")
        {
            throw new InvalidOperationException("Search did not resolve the localized Update channel row.");
        }
    }

    internal async Task VerifyGlobalSearchSnapshotNavigationAsync(bool compact)
    {
        AutoSuggestBox box = compact ? CompactGlobalSearchBox : GlobalSearchBox;
        GlobalSearchResult first = ((GlobalSearchResult[])box.ItemsSource)[0];
        SelectLocalizationSnapshotPage("laptop");
        SubmitGlobalSearch(box, "unmatched search term", first);
        if (!ReferenceEquals(MainNavigation.SelectedItem, LaptopNavigationItem))
        {
            throw new InvalidOperationException("Search opened a stale suggestion.");
        }

        SubmitGlobalSearch(box, "nightly", first);
        await Task.Delay(300);
        if (!ReferenceEquals(MainNavigation.SelectedItem, SettingsNavigationItem)
            || SettingsPage.VerticalOffset <= 0)
        {
            throw new InvalidOperationException("Search did not reveal the Update channel row.");
        }

        if (!compact)
        {
            VerifyGlobalSearchSuggestionsAfterDetach();
            VerifyGlobalSearchHiddenLightingBrightness();
            // The production page discovers audio capabilities on its first
            // visit. Realize that page before testing search from elsewhere.
            SelectLocalizationSnapshotPage("thx-audio");
            await ShowLocalizationSnapshotThxAudioAsync("thx-audio");
            await Task.Delay(300);
            SelectLocalizationSnapshotPage("settings");
            if (ThxViewModel.SupportsSavedAudio)
            {
                await VerifyGlobalSearchVoiceClarityAsync();
            }
            else
            {
                await VerifyGlobalSearchDisabledThxApplicationsAsync();
            }
        }
    }

    private void VerifyGlobalSearchSuggestionsAfterDetach()
    {
        GlobalSearchBox.Text = "nightly";
        UpdateGlobalSearchSuggestions(GlobalSearchBox);
        CloseGlobalSearch();
        OnGlobalSearchGotFocus(GlobalSearchBox, new RoutedEventArgs());
        if (GlobalSearchBox.ItemsSource is not GlobalSearchResult[] results
            || results.FirstOrDefault()?.RowId != "MainWindow_TextBlockUpdateChannel")
        {
            throw new InvalidOperationException("Search did not restore suggestions for the retained query.");
        }
    }

    private void VerifyGlobalSearchHiddenLightingBrightness()
    {
        ViewModel.LightingEffectEditor = OpenBlade.Contracts.LightingEffect.Off;
        GlobalSearchResult brightness = globalSearchCatalog!.Single(
            result => result.RowId == "DashboardLightingBrightnessSlider");
        if (!ViewModel.ShowOpenBladeKeyboardLightingControls || brightness.IsExposed?.Invoke() != false)
        {
            throw new InvalidOperationException("Search exposed lighting brightness when the keyboard effect was Off.");
        }
    }

    private async Task VerifyGlobalSearchVoiceClarityAsync()
    {
        if (!ThxViewModel.HasVoiceClarity || ThxAudioEnhancements.IsExpanded)
        {
            throw new InvalidOperationException("The Voice clarity fixture requires collapsed THX Enhancements.");
        }

        string query = LocalizedXamlResourceLoader.GetText("MainWindow_ThxAudioVoiceClarityTitle.Text");
        GlobalSearchResult result = FindGlobalSearchResults(query)
            .SingleOrDefault(candidate => candidate.RowId == nameof(ThxVoiceClarityCell))
            ?? throw new InvalidOperationException(
                $"Search did not offer Voice clarity. Navigation={ThxAudioNavigationItem.Visibility}, "
                + $"Enhancements={ThxAudioEnhancements.Visibility}, enabled={ThxAudioEnhancements.IsEnabled}, "
                + $"visible path={HasVisibleSearchPath(ThxAudioEnhancements, ThxAudioPage.Content)}.");
        SubmitGlobalSearch(GlobalSearchBox, query, result);
        await Task.Delay(300);
        if (!ReferenceEquals(MainNavigation.SelectedItem, ThxAudioNavigationItem)
            || !ThxAudioEnhancements.IsExpanded
            || ThxVoiceClarityCell.ActualHeight <= 0)
        {
            throw new InvalidOperationException("Search did not expand THX Enhancements and lay out Voice clarity.");
        }

        Rect bounds = ThxVoiceClarityCell.TransformToVisual(ThxAudioPage)
            .TransformBounds(new Rect(0, 0, ThxVoiceClarityCell.ActualWidth, ThxVoiceClarityCell.ActualHeight));
        if (bounds.Top < -1 || bounds.Bottom > ThxAudioPage.ActualHeight + 1)
        {
            throw new InvalidOperationException("Search left Voice clarity outside the visible THX page.");
        }
    }

    private async Task VerifyGlobalSearchDisabledThxApplicationsAsync()
    {
        if (ThxAudioApplications.IsEnabled || ThxAudioApplications.IsExpanded)
        {
            throw new InvalidOperationException("The THX Applications fixture requires a disabled, collapsed expander.");
        }

        string query = LocalizedXamlResourceLoader.GetText("MainWindow_ThxAudioApplicationsTitle.Text");
        GlobalSearchResult result = FindGlobalSearchResults(query)
            .Single(candidate => ReferenceEquals(candidate.Anchor, ThxAudioApplications));
        SubmitGlobalSearch(GlobalSearchBox, query, result);
        await Task.Delay(300);
        if (!ReferenceEquals(MainNavigation.SelectedItem, ThxAudioNavigationItem)
            || ThxAudioApplications.IsExpanded)
        {
            throw new InvalidOperationException("Search did not preserve the disabled THX Applications section.");
        }
    }

    internal FrameworkElement? GetGlobalSearchSnapshotResults()
    {
        foreach (Microsoft.UI.Xaml.Controls.Primitives.Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is FrameworkElement root
                && FindGlobalSearchRow(root, "SuggestionsList") is { } suggestions)
            {
                return suggestions;
            }
        }
        return null;
    }
#endif
}
