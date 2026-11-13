using Microsoft.UI.Xaml;
using Button = Microsoft.UI.Xaml.Controls.Button;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    // Reuse the cell's already-bound value. Reading the view-model presentation
    // again for accessibility would repeat complete profile formatting per row.
    private string ProfileNavigationName(string title, object? value) =>
        value is ProfileValuePresentation profile
            ? $"{title}. {profile.AccessibilityDescription}" : title;

    private string ProfileNavigationName(
        string title,
        object? pluggedIn,
        object? usbC,
        object? battery) =>
        string.Join(". ", new[]
        {
            title,
            (pluggedIn as ProfileValuePresentation)?.AccessibilityDescription,
            (usbC as ProfileValuePresentation)?.AccessibilityDescription,
            (battery as ProfileValuePresentation)?.AccessibilityDescription,
        }.Where(value => !string.IsNullOrEmpty(value)));

    private void OnProfileSettingClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string setting }
            && GetProfileSettingDestination(setting) is { } destination
            && IsGlobalSearchPageAvailable(destination))
        {
            RevealProfileDestinationSection(destination);
            OpenGlobalSearchResult(destination);
        }
    }

    private void RevealProfileDestinationSection(GlobalSearchResult destination)
    {
        // Like a Labs guide, an explicit profile link requests its target even
        // when Customize removed that section. Lighting can still be unrealized.
        PageSectionKind? section = destination.RowId switch
        {
            "LightingKeyboardSection" => PageSectionKind.LightingKeyboard,
            "LightingLogoSection" => PageSectionKind.LightingLogo,
            "LightingPowerSavingSection" => PageSectionKind.LightingPowerSaving,
            _ => null,
        };
        for (DependencyObject? current = destination.Anchor; section is null && current is not null;
            current = GetGlobalSearchParent(current))
        {
            foreach (var presentation in pageSectionPresentations)
            {
                if (ReferenceEquals(presentation.Value, current))
                {
                    section = presentation.Key;
                    break;
                }
            }
        }
        if (section is { } kind && pageSections.Values.SelectMany(options => options)
            .FirstOrDefault(option => option.Kind == kind) is { } option)
        {
            option.IsVisible = true;
        }
    }

    private GlobalSearchResult? GetProfileSettingDestination(string setting) => setting switch
    {
        "PowerMode" or "WindowsPowerMode" or "GpuTuning" =>
            new("", "", LaptopNavigationItem, LaptopPage, PowerModeCard),
        "Fans" => new("", "", LaptopNavigationItem, LaptopPage, FansCard),
        "BatteryProtection" or "ChargeLimit" =>
            new("", "", LaptopNavigationItem, LaptopPage, BatteryCard),
        "FunctionKeyBehavior" or "GamingMode" or "GamingModeWindowsKey" or "GamingModeAltTab" =>
            new("", "", LaptopNavigationItem, LaptopPage, KeyboardBehaviorCard),
        "StartupAnimation" => new("", "", LaptopNavigationItem, LaptopPage, StartupAnimationCard),
        "DisplayRefreshRate" or "DisplayHdr" or "DisplayColorProfile" or "DisplayBrightness" =>
            new("", "", DisplayNavigationItem, DisplayPage, DisplayCard),
        "LightingEffect" or "LightingBrightness" or "LightingColor" =>
            new("", "", LightingNavigationItem, LightingPage, LightingCard, "LightingKeyboardSection"),
        "LogoLighting" =>
            new("", "", LightingNavigationItem, LightingPage, LightingCard, "LightingLogoSection"),
        "LightingDisplayOff" or "LightingIdle" =>
            new("", "", LightingNavigationItem, LightingPage, LightingCard, "LightingPowerSavingSection"),
        _ => null,
    };
#if OPENBLADE_DEVELOPER_HARNESS
    internal async Task VerifyProfileNavigationAsync(bool compact)
    {
        double previousWidth = ProfilesPage.Width;
        try
        {
            // Hosted desktops may constrain the outer snapshot window. The
            // image has already been captured; give this interaction check a
            // deterministic page width so it exercises both responsive layouts.
            SelectLocalizationSnapshotPage("profiles");
            ProfilesPage.Width = ProfilesStackThresholdDip + (compact ? -100 : 100);
            ProfilesPage.UpdateLayout();
            UpdateProfilesLayout();
            await VerifyProfileNavigationCoreAsync(compact);
        }
        finally
        {
            ProfilesPage.Width = previousWidth;
            ProfilesPage.UpdateLayout();
            UpdateProfilesLayout();
        }
    }

    private async Task VerifyProfileNavigationCoreAsync(bool compact)
    {
        if (pageSections.Count == 0)
        {
            ApplyPageLayoutPreferences([]);
        }
        SelectLocalizationSnapshotPage("profiles");
        await Task.Delay(100);
        FrameworkElement layout = (FrameworkElement)Root.FindName(
            compact ? "ProfilesCompactLayout" : "ProfilesComparisonLayout");
        Button[] rows = Descendants(layout).OfType<Button>()
            .Where(button => button.Tag is string).ToArray();
        if (layout.Visibility != Visibility.Visible || rows.Length != 21)
        {
            throw new InvalidOperationException(
                $"The profile comparison layout was not realized: compact={compact}, "
                + $"width={ProfilesPage.ActualWidth}, visibility={layout.Visibility}, rows={rows.Length}.");
        }
        ViewModel.SelectedProfileComparisonOption = ViewModel.ProfileComparisonOptions[1];
        SelectLocalizationSnapshotPage("laptop");
        SelectLocalizationSnapshotPage("profiles");
        if (!ViewModel.IsSelectedProfileActive)
        {
            throw new InvalidOperationException("Profiles did not open with the active profile selected.");
        }
        // Hidden destinations must preserve the current page, including when an
        // availability change arrives after the profile summaries were rendered.
        Visibility lightingVisibility = LightingNavigationItem.Visibility;
        LightingNavigationItem.Visibility = Visibility.Collapsed;
        OnProfileSettingClick(rows.Single(row => (string)row.Tag == "LightingEffect"), new RoutedEventArgs());
        if (!ReferenceEquals(MainNavigation.SelectedItem, ProfilesNavigationItem)
            || ProfilesPage.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("A hidden profile destination blanked the current page.");
        }
        LightingNavigationItem.Visibility = lightingVisibility;
        PageSectionOption fansSection = pageSections[CustomizablePageKind.Laptop]
            .Single(option => option.Kind == PageSectionKind.LaptopFans);
        fansSection.IsVisible = false;
        OnProfileSettingClick(rows.Single(row => (string)row.Tag == "Fans"), new RoutedEventArgs());
        await Task.Delay(150);
        if (!fansSection.IsVisible || !ReferenceEquals(MainNavigation.SelectedItem, LaptopNavigationItem)
            || !HasVisibleSearchPath(FansCard, LaptopPage.Content))
        {
            throw new InvalidOperationException("The profile link did not reveal its hidden Fans section.");
        }
        SelectLocalizationSnapshotPage("profiles");
        PageSectionOption lightingSection = pageSections[CustomizablePageKind.Lighting]
            .Single(option => option.Kind == PageSectionKind.LightingKeyboard);
        lightingSection.IsVisible = false;
        OnProfileSettingClick(rows.Single(row => (string)row.Tag == "LightingEffect"), new RoutedEventArgs());
        await Task.Delay(150);
        FrameworkElement? keyboardSection = FindGlobalSearchRow(LightingCard, "LightingKeyboardSection");
        if (!lightingSection.IsVisible || !ReferenceEquals(MainNavigation.SelectedItem, LightingNavigationItem)
            || keyboardSection is null || !HasVisibleSearchPath(keyboardSection, LightingPage.Content))
        {
            throw new InvalidOperationException("The profile link did not reveal its hidden Lighting section.");
        }
        foreach (Button row in rows)
        {
            SelectLocalizationSnapshotPage("profiles");
            GlobalSearchResult destination = GetProfileSettingDestination((string)row.Tag)
                ?? throw new InvalidOperationException($"Missing destination: {row.Tag}");
            if (!IsGlobalSearchResultAvailable(destination))
            {
                continue;
            }
            string accessibleName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(row);
            if (string.IsNullOrWhiteSpace(accessibleName) || !accessibleName.Contains(". ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Missing profile summary accessibility: {row.Tag}");
            }
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(row);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(150);
            if (!ReferenceEquals(MainNavigation.SelectedItem, destination.Navigation)
                || destination.Page!.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException($"Profile navigation failed: {row.Tag}");
            }
            FrameworkElement target = destination.RowId is { } id
                ? FindGlobalSearchRow(destination.Anchor!, id)! : destination.Anchor!;
            if (target is not null && HasVisibleSearchPath(target, destination.Page.Content))
            {
                var bounds = target.TransformToVisual(destination.Page).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, target.ActualWidth, target.ActualHeight));
                if (bounds.Bottom <= 0 || bounds.Top >= destination.Page.ActualHeight)
                {
                    throw new InvalidOperationException($"Profile destination did not scroll into view: {row.Tag}");
                }
            }
        }
        SelectLocalizationSnapshotPage("profiles");

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
#endif
}
