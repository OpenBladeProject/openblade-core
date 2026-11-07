using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XamlScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private IReadOnlyList<GlobalSearchResult> BuildGlobalSearchCatalog()
    {
        var results = new List<GlobalSearchResult>();
        Page(HomeNavigationItem, HomePage, "MainWindow_HomeNavigationItemText.Content");
        GlobalSearchResult profiles = Page(ProfilesNavigationItem, ProfilesPage, "MainWindow_TextBlockProfiles.Text");
        GlobalSearchResult laptop = Page(LaptopNavigationItem, LaptopPage, "MainWindow_LaptopNavigationItemText.Content");
        GlobalSearchResult display = Page(DisplayNavigationItem, DisplayPage, "MainWindow_DisplayNavigationItemText.Content");
        GlobalSearchResult lighting = Page(LightingNavigationItem, LightingPage, "MainWindow_LightingNavigationItemText.Content");
        GlobalSearchResult audio = Page(ThxAudioNavigationItem, ThxAudioPage, "MainWindow_ThxAudioHeading.Text");
        GlobalSearchResult telemetry = Page(TelemetryNavigationItem, TelemetryPage, "Telemetry_Title.Text");
        GlobalSearchResult labs = Page(LabsNavigationItem, LabsPage, "MainWindow_LabsTitle.Text");
        GlobalSearchResult settings = Page(SettingsNavigationItem, SettingsPage, "MainWindow_SettingsNavigationItemText.Content");
        GlobalSearchResult about = Page(AboutNavigationItem, AboutPage, "MainWindow_AboutNavigationItemText.Content");

        Section(profiles, ApplicationProfilesSection, "Applications_Title.Text");

        GlobalSearchResult performance = Section(laptop, PowerModeCard, "MainWindow_TextBlockPerformance.Text");
        Row(performance, "MainWindow_TextBlockLaptopPerformance.Text", "DashboardPerformanceSelector",
            aliases: "HyperBoost", exposed: () => ViewModel.IsPerformanceVisible);
        Row(performance, "MainWindow_TextBlockWindowsPowerMode.Text", "WindowsPowerModeSelector");
        Row(performance, "MainWindow_TextBlockCPU.Text", "DashboardCustomCpuSelector",
            exposed: () => ViewModel.IsCustomPerformanceVisible);
        Row(performance, "MainWindow_TextBlockGPU.Text", "DashboardCustomGpuSelector",
            exposed: () => ViewModel.IsCustomPerformanceVisible);
        Row(performance, "MainWindow_TextBlockCustomMaximumFans.Text", "MainWindow_TextBlockCustomMaximumFans",
            exposed: () => ViewModel.IsCustomMaximumFansVisible);

        GlobalSearchResult fans = Section(laptop, FansCard, "MainWindow_TextBlockFans.Text",
            aliases: LocalizedResources.Get("GlobalSearch_FansAliases", "cooling RPM"));
        Row(fans, "MainWindow_TextBlockFanSpeed.Text", "FixedFanSpeedSlider",
            exposed: () => ViewModel.IsFanControlVisible && ViewModel.IsFixedFanSpeedVisible);

        Row(fans, "MainWindow_ExpanderCPUCurve.Header", "GlobalSearchCpuFanCurve",
            exposed: () => ViewModel.IsFanControlVisible && ViewModel.IsManualFanCurveVisible);
        Row(fans, "MainWindow_ExpanderGPUCurve.Header", "GlobalSearchGpuFanCurve",
            exposed: () => ViewModel.IsFanControlVisible && ViewModel.IsManualFanCurveVisible);

        GlobalSearchResult battery = Section(laptop, BatteryCard, "MainWindow_TextBlockBattery.Text");
        Row(battery, "MainWindow_TextBlockBatteryProtection.Text", "BatteryProtectionToggle",
            aliases: LocalizedResources.Get("GlobalSearch_BatteryAliases", "charge limit battery health"));
        Row(battery, "MainWindow_TextBlockChargeLimit.Text", "DashboardChargeLimitSlider");
        TextRows(battery, "MainWindow_TextBlockTemporaryFullCharge");

        GlobalSearchResult keyboard = Section(laptop, KeyboardSection, "SnapTap_KeyboardHeader.Text");
        Row(keyboard, "MainWindow_TextBlockFunctionKeyBehavior.Text", "FunctionKeyBehaviorExpander",
            anchor: KeyboardBehaviorCard, exposed: () => ViewModel.SupportsFunctionKeyPrimary);
        Row(keyboard, "MainWindow_TextBlockGamingMode.Text", "GamingModeToggle", anchor: KeyboardBehaviorCard,
            exposed: () => ViewModel.SupportsGamingMode);
        Row(keyboard, "Touchpad_Title.Text", anchor: TouchpadRow);
        Row(keyboard, "SnapTap_Title.Text", anchor: SnapTapExpander,
            aliases: LocalizedResources.Get("GlobalSearch_SnapTapAliases", "A/D SOCD counter-strafe"));
        Section(laptop, StartupAnimationCard, "MainWindow_TextBlockStartupAnimation.Text",
            exposed: () => ViewModel.SupportsStartupAnimation);
        Section(laptop, ShortcutKeysSection, "MainWindow_TextBlockShortcutKeys.Text");

        GlobalSearchResult displayControls = Section(display, DisplayCard, "MainWindow_TextBlockBuiltInDisplay.Text");
        Row(displayControls, "MainWindow_TextBlockRefreshRate.Text", "DisplayRefreshRatePicker");
        Row(displayControls, "MainWindow_TextBlockUseHDR.Text", "DisplayHdrToggle");
        Row(displayControls, "MainWindow_TextBlockColorProfile.Text", "DisplayColorProfilePicker");
        Row(displayControls, "MainWindow_DisplayBrightnessTitle.Text", "DisplayBrightnessSlider",
            aliases: LocalizedResources.Get("GlobalSearch_BrightnessAliases", "screen backlight"));

        GlobalSearchResult keyboardLighting = Section(lighting, LightingCard, "MainWindow_TextBlockKeyboard2.Text",
            "LightingKeyboardSection",
            exposed: () => IsSearchLightingSectionShown(PageSectionKind.LightingKeyboard));
        Row(keyboardLighting, "MainWindow_TextBlockWindowsDynamicLighting.Text", "WindowsDynamicLightingToggle",
            exposed: () => ViewModel.IsWindowsDynamicLightingAvailable);
        Row(keyboardLighting, "MainWindow_TextBlockEffect.Text", "LightingEffectPicker",
            exposed: () => ViewModel.ShowOpenBladeKeyboardLightingControls && ViewModel.IsKeyboardEffectSelectionVisible);
        Row(keyboardLighting, "MainWindow_TextBlockBrightness.Text", "DashboardLightingBrightnessSlider",
            exposed: () => ViewModel.ShowOpenBladeKeyboardLightingControls && ViewModel.LightingShowsBrightness);
        Row(keyboardLighting, "MainWindow_TextBlockImportPreset.Text", "ImportKeyboardLightingPresetCard",
            exposed: () => ViewModel.ShowOpenBladeKeyboardLightingControls && ViewModel.ShowLightingEffectLabControls);
        Row(keyboardLighting, "MainWindow_TextBlockSixtyHzLighting.Text", "LightingRefreshRateCard",
            exposed: () => ViewModel.ShowOpenBladeKeyboardLightingControls && ViewModel.ShowLightingEffectLabControls);
        Section(lighting, LightingCard, "MainWindow_TextBlockLaptopLogo.Text", "LightingLogoSection",
            exposed: () => ViewModel.IsLogoLightingVisible && IsSearchLightingSectionShown(PageSectionKind.LightingLogo));
        GlobalSearchResult lightingPower = Section(lighting, LightingCard, "MainWindow_TextBlockSwitchOffLighting.Text",
            "LightingPowerSavingSection",
            exposed: () => ViewModel.ShowOpenBladeKeyboardLightingControls
                && IsSearchLightingSectionShown(PageSectionKind.LightingPowerSaving));
        Row(lightingPower, "MainWindow_TextBlockWhenTheDisplayTurnsOff.Text", "LightingDisplayOffToggle");
        Row(lightingPower, "MainWindow_TextBlockAfterInactivity.Text", "LightingIdleTimeoutToggle");

        Row(audio, "MainWindow_ThxAudioIntegrationTitle.Text", anchor: ThxAudioIntegrationRow);
        Row(audio, "MainWindow_ThxAudioSpatialTitle.Text", anchor: ThxAudioSpatialRow);
        Row(audio, "MainWindow_ThxAudioEqualizerTitle.Text", anchor: ThxAudioEqualizerRow);
        Row(audio, "MainWindow_ThxAudioPresetTitle.Text", anchor: ThxAudioPresetRow);
        GlobalSearchResult enhancements = Section(audio, ThxAudioEnhancements, "MainWindow_ThxAudioEnhancementsTitle.Text");
        Row(enhancements, "MainWindow_ThxAudioNormalizationTitle.Text", nameof(ThxNormalizationCell),
            exposed: () => ThxViewModel.HasSoundNormalization);
        Row(enhancements, "MainWindow_ThxAudioVoiceClarityTitle.Text", nameof(ThxVoiceClarityCell),
            exposed: () => ThxViewModel.HasVoiceClarity);
        Section(audio, ThxAudioApplications, "MainWindow_ThxAudioApplicationsTitle.Text");

        Row(telemetry, "Telemetry_Enable.Text", anchor: TelemetryEnabledToggle);
        Section(telemetry, BatteryHistorySection, "BatteryHistory_Report.Header");

        GlobalSearchResult labsKeyboard = Section(labs, LabsKeyboardSection, "MainWindow_TextBlockKeyboard2.Text",
            exposed: () => ViewModel.ShowLightingEffectLabControls);
        Row(labsKeyboard, "MainWindow_LabsImportTitle.Text", nameof(LabsImportGuideButton));
        Row(labsKeyboard, "MainWindow_TextBlockSixtyHzLighting.Text", nameof(LabsLightingRateGuideButton));

        GlobalSearchResult behavior = Section(settings, SettingsBehaviorSection, "MainWindow_TextBlockBehavior.Text");
        RowTitle(behavior, ViewModel.ThermalControlPolicyHeading, "ThermalControlPolicySelector",
            exposed: () => ViewModel.IsThermalControlPolicyAvailable);
        TextRows(behavior, "MainWindow_TextBlockLiveReadings", "MainWindow_TextBlockShownOnDashboard",
            "MainWindow_TextBlockStartAtSignIn", "MainWindow_TextBlockForceRunOnIncompatibleFirmware",
            "MainWindow_TextBlockOverrideExternalLightingChanges");
        Row(behavior, "MainWindow_TextBlockStartMinimized.Text", "StartMinimizedToggle");

        GlobalSearchResult notifications = Section(settings, SettingsNotificationsSection, "MainWindow_TextBlockNotifications.Text");
        TextRows(notifications, "MainWindow_TextBlockPerformanceChanges", "MainWindow_TextBlockPowerProfileChanges",
            "MainWindow_TextBlockExternalPowerDischarge", "MainWindow_TextBlockAppUpdates",
            "MainWindow_TextBlockFirmwareUpdates", "MainWindow_TextBlockControllerAdvisories",
            "MainWindow_TextBlockOperationFailures");

        GlobalSearchResult appearance = Section(settings, SettingsAppearanceSection, "MainWindow_TextBlockAppearance.Text");
        TextRows(appearance, "MainWindow_TextBlockTrayDashboard", "MainWindow_TextBlockAppTheme",
            "MainWindow_TextBlockAppAccentColor", "MainWindow_TextBlockCustomColor");
        Row(appearance, "MainWindow_TextBlockAppLanguage.Text", "AppLanguageHeading");
        Row(appearance, "MainWindow_TextBlockNavigationMenu.Text", anchor: NavigationMenuExpander);
        Row(appearance, "MainWindow_TextBlockTaskbarHoverLabel.Text", anchor: TaskbarHoverLabelExpander);

        GlobalSearchResult updates = Section(settings, SettingsUpdatesSection, "MainWindow_TextBlockUpdates.Text");
        Row(updates, "MainWindow_TextBlockUpdateChannel.Text", "MainWindow_TextBlockUpdateChannel",
            aliases: LocalizedResources.Get("GlobalSearch_UpdateChannelAliases", "nightly stable releases"));
        TextRows(updates, "MainWindow_TextBlockCheckInterval");
        Row(updates, "MainWindow_TextBlockLatestRelease.Text", "CheckNowButton");
        Section(settings, SettingsShutdownSection, "MainWindow_TextBlockShutDownOpenBlade.Text");

        Section(about, AboutVersionsSection, "MainWindow_TextBlockVersions.Text");
        Section(about, AboutDeviceInformationSection, "MainWindow_TextBlockDeviceInformation.Text");
        Section(about, OfficialFirmwareUpdatesCard, "MainWindow_TextBlockOfficialFirmware.Text");
        GlobalSearchResult support = Section(about, AboutSupportSection, "MainWindow_TextBlockSupport.Text");
        Row(support, "MainWindow_TextBlockExportDiagnostics.Text", "MainWindow_ButtonExportDiagnostics");
        Row(support, "MainWindow_HyperlinkButtonOpenDocumentation.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", "MainWindow_HyperlinkButtonOpenDocumentation");
        Row(about, "MainWindow_TextBlockTroubleshooting.Text", anchor: TroubleshootingExpander);
        return results;

        GlobalSearchResult Page(NavigationViewItem navigation, XamlScrollViewer page, string title)
        {
            var result = new GlobalSearchResult(LocalizedXamlResourceLoader.GetText(title), string.Empty, navigation, page);
            results.Add(result);
            return result;
        }

        GlobalSearchResult Section(
            GlobalSearchResult page, FrameworkElement anchor, string title, string? rowId = null,
            string aliases = "", Func<bool>? exposed = null)
        {
            GlobalSearchResult result = page with
            {
                Title = LocalizedXamlResourceLoader.GetText(title),
                Breadcrumb = page.Title,
                Anchor = anchor,
                RowId = rowId,
                Aliases = aliases,
                IsExposed = exposed,
            };
            results.Add(result);
            return result;
        }

        void Row(
            GlobalSearchResult scope, string title, string? rowId = null, FrameworkElement? anchor = null,
            string aliases = "", Func<bool>? exposed = null) =>
            RowTitle(scope, LocalizedXamlResourceLoader.GetText(title), rowId, anchor, aliases, exposed);

        void RowTitle(
            GlobalSearchResult scope, string title, string? rowId = null, FrameworkElement? anchor = null,
            string aliases = "", Func<bool>? exposed = null)
        {
            results.Add(scope with
            {
                Title = title,
                Breadcrumb = scope.Breadcrumb.Length == 0 ? scope.Title : $"{scope.Breadcrumb} › {scope.Title}",
                Anchor = anchor ?? scope.Anchor,
                RowId = rowId,
                Aliases = aliases,
                IsExposed = scope.IsExposed is { } parentExposed && exposed is not null
                    ? () => parentExposed() && exposed()
                    : scope.IsExposed ?? exposed,
            });
        }

        void TextRows(GlobalSearchResult section, params string[] ids)
        {
            foreach (string id in ids)
            {
                Row(section, $"{id}.Text", id);
            }
        }
    }

    private bool IsSearchLightingSectionShown(PageSectionKind section) =>
        !pageSections.TryGetValue(CustomizablePageKind.Lighting, out var sections)
        || sections.Any(option => option.Kind == section && option.IsVisible);
}
