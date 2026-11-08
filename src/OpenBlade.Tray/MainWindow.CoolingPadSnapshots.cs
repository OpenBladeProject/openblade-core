#if OPENBLADE_DEVELOPER_HARNESS
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    internal void ShowCoolingPadSnapshot(string variant)
    {
        if (!localizationSnapshotMode)
        {
            throw new InvalidOperationException("Cooling-pad previews require disconnected snapshot mode.");
        }
        ViewModel.CoolingPad.ApplyStatus(new CoolingPadStatusSnapshot
        {
            Connected = variant != "absent",
            Connection = variant == "absent" ? CoolingPadConnectionState.Absent : CoolingPadConnectionState.PresentExact,
            Identity = variant == "absent" ? null : "1532:0F47 preview",
            Active = variant != "connected",
            WritesPaused = variant == "paused",
            ConfirmedTargetRpm = variant == "connected" ? null : 1500,
            ConfirmedBrightnessPercent = variant == "connected" ? null : 70,
            Requested = CoolingPadSettings.Default with
            {
                FanMode = variant == "curves" ? CoolingPadFanMode.HostCurve : CoolingPadFanMode.Fixed,
                LightingMode = variant is "wave" or "narrow" or "wide" ? CoolingPadLightingMode.Wave : CoolingPadLightingMode.Breathing,
                BrightnessPercent = 70,
                AllowDemandApi = variant == "demand",
            },
        });
        SelectLocalizationSnapshotPage("cooling-pad");
        string coolingPadQuery = LocalizedResources.Get("MainWindow_CoolingPadNavigationItemText.Content", "Cooling pad");
        if (variant != "absent" && !FindGlobalSearchResults(coolingPadQuery)
                .Any(result => ReferenceEquals(result.Navigation, CoolingPadNavigationItem)))
        {
            throw new InvalidOperationException("Confirmed cooling-pad discovery must expose its localized search page.");
        }
        if (variant == "unplugged")
        {
            ViewModel.CoolingPad.ApplyStatus(new CoolingPadStatusSnapshot { Connection = CoolingPadConnectionState.Absent });
        }
        if (variant is "absent" or "unplugged")
        {
            if (CoolingPadNavigationItem.Visibility != Visibility.Collapsed
                || CoolingPadPage.Visibility != Visibility.Collapsed
                || !ReferenceEquals(MainNavigation.SelectedItem, HomeNavigationItem)
                || HomePage.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("A confirmed absent cooling pad must hide its navigation and return to Dashboard.");
            }
            if (FindGlobalSearchResults(coolingPadQuery).Any(result => ReferenceEquals(result.Navigation, CoolingPadNavigationItem)))
            {
                throw new InvalidOperationException("Confirmed absence must remove cached cooling-pad search results.");
            }
            return;
        }
        if (variant == "unknown")
        {
            ViewModel.CoolingPad.ApplyStatus(new CoolingPadStatusSnapshot());
            if (ViewModel.CoolingPad.CanApply || !ViewModel.CoolingPad.CanRefresh)
            {
                throw new InvalidOperationException("Unknown discovery must retain the page and allow only a fresh status read.");
            }
        }
        if (variant == "reconnecting")
        {
            ViewModel.CoolingPad.SetServiceAvailable(false);
        }
        if (variant == "curves")
        {
            CoolingPadCurves.IsExpanded = true;
        }
        if (variant == "hover")
        {
            // Render the existing shared pointer fill without moving the user's pointer.
            CoolingPadFanRow.Background = SettingsCardPointerVisual.GetPointerOverBackground(CoolingPadFanRow);
        }
        if (CoolingPadNavigationItem.Visibility != Visibility.Visible
            || CoolingPadPage.Visibility != Visibility.Visible
            || !ReferenceEquals(MainNavigation.SelectedItem, CoolingPadNavigationItem))
        {
            throw new InvalidOperationException("A confirmed connected cooling pad must expose its dedicated page.");
        }
        if (CoolingPadDemandToggle.IsOn != ViewModel.CoolingPad.AllowDemandApi)
        {
            throw new InvalidOperationException("The cooling-pad app-demand opt-in must match its explicit settings draft.");
        }
        if (!ReferenceEquals(CoolingPadFanSelector.SelectedItem, ViewModel.CoolingPad.FanMode)
            || !ReferenceEquals(CoolingPadLightingSelector.SelectedItem, ViewModel.CoolingPad.LightingMode)
            || !ReferenceEquals(CoolingPadTemperatureSource.SelectedItem, ViewModel.CoolingPad.TemperatureSource)
            || !ReferenceEquals(CoolingPadWaveDirection.SelectedItem, ViewModel.CoolingPad.WaveDirection))
        {
            throw new InvalidOperationException("Cooling-pad responsive selectors must retain their typed editor selections.");
        }
        CoolingPadSection.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 0,
        });
    }
}
#endif
