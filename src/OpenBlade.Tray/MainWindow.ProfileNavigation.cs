using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using Button = Microsoft.UI.Xaml.Controls.Button;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private void SelectActiveProfileComparison()
    {
        if (ViewModel.ActivePowerSource is PowerSource.PluggedIn or PowerSource.UsbC or PowerSource.Battery)
        {
            ViewModel.SelectedProfileComparisonOption = ViewModel.ProfileComparisonOptions[0];
        }
    }

    private void OnProfileSettingClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string setting }
            && GetProfileSettingDestination(setting) is { } destination)
        {
            OpenGlobalSearchResult(destination);
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
        SelectLocalizationSnapshotPage("profiles");
        await Task.Delay(100);
        FrameworkElement layout = (FrameworkElement)Root.FindName(
            compact ? "ProfilesCompactLayout" : "ProfilesComparisonLayout");
        Button[] rows = Descendants(layout).OfType<Button>()
            .Where(button => button.Tag is string).ToArray();
        if (layout.Visibility != Visibility.Visible || rows.Length != 21)
        {
            throw new InvalidOperationException("The expected profile comparison layout was not realized.");
        }
        ViewModel.SelectedProfileComparisonOption = ViewModel.ProfileComparisonOptions[1];
        SelectLocalizationSnapshotPage("laptop");
        SelectLocalizationSnapshotPage("profiles");
        if (!ViewModel.IsSelectedProfileActive)
        {
            throw new InvalidOperationException("Profiles did not open with the active profile selected.");
        }
        foreach (Button row in rows)
        {
            SelectLocalizationSnapshotPage("profiles");
            GlobalSearchResult destination = GetProfileSettingDestination((string)row.Tag)
                ?? throw new InvalidOperationException($"Missing destination: {row.Tag}");
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
