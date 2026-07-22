using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CapabilityPresentationTests
{
    [Fact]
    public void MainWindow_ShowsUnsavedNavigationBadgesAndStretchesFanCurveSliders()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        string[] expectedBadgeBindings =
        [
            "{x:Bind ViewModel.HasThermalProfileDraft, Mode=OneWay}",
            "{x:Bind ViewModel.HasProfileEditorChanges, Mode=OneWay}",
            "{x:Bind ViewModel.HasLaptopPageProfileDraft, Mode=OneWay}",
            "{x:Bind ViewModel.HasLightingProfileDraft, Mode=OneWay}",
            "{x:Bind DisplayViewModel.CanSaveCurrentSettingsToProfile, Mode=OneWay}",
        ];
        XElement[] badges = document.Descendants()
            .Where(element => element.Name.LocalName == "InfoBadge")
            .ToArray();
        Assert.Equal(expectedBadgeBindings.Length, badges.Length);
        Assert.Equal(
            expectedBadgeBindings.Order(),
            badges.Select(badge => Attribute(badge, "Visibility")).Order());
        XElement badgeStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "UnsavedNavigationBadgeStyle");
        Assert.Contains(
            badgeStyle.Descendants(),
            element => Attribute(element, "Property") == "Background"
                && Attribute(element, "Value")
                    == "{ThemeResource AccentFillColorDefaultBrush}");

        XElement[] curveSliders = document.Descendants()
            .Where(element => element.Name.LocalName == "Slider"
                && Attribute(element, "Tag") == "{x:Bind Mode=OneTime}")
            .ToArray();
        Assert.Equal(2, curveSliders.Length);
        Assert.All(curveSliders, slider => Assert.Equal("120", Attribute(slider, "MinWidth")));
        XElement[] curveLists = curveSliders
            .Select(slider => slider.Ancestors().First(element =>
                element.Name.LocalName == "ItemsControl"))
            .Distinct()
            .ToArray();
        Assert.Equal(2, curveLists.Length);
        Assert.All(curveLists, list => Assert.Equal(
            "{StaticResource FanCurveItemContainerStyle}",
            Attribute(list, "ItemContainerStyle")));
    }

    [Fact]
    public void MainWindow_AnimatesConditionalDashboardSubcardsAndExportsJsonProfiles()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement[] animated = document.Descendants()
            .Where(element => Attribute(element, "AnimatedVisibility.IsVisible") is not null)
            .ToArray();

        Assert.Equal(15, animated.Length);
        Assert.All(animated, element => Assert.Equal(
            "Collapsed",
            Attribute(element, "Visibility")));
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsCustomPerformanceVisible",
                StringComparison.Ordinal) is true);
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsFixedFanSpeedVisible",
                StringComparison.Ordinal) is true);
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsManualFanCurveVisible",
                StringComparison.Ordinal) is true);

        XElement export = Assert.Single(document.Descendants(), element =>
            Attribute(element, "AutomationProperties.Name") == "Export profiles to JSON");
        Assert.Equal("OnExportProfiles", Attribute(export, "Click"));
        Assert.Equal("Export profiles", Attribute(export, "Content"));
    }

    [Fact]
    public void MainWindow_UsesDashboardNavigationAndImmediateControls()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "NavigationViewItem"
            && Attribute(element, "Content") == "Dashboard");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "NavigationViewItem"
            && Attribute(element, "Content") == "Home");

        XElement performance = NamedElement(document, "ComboBox", "DashboardPerformanceSelector");
        XElement fans = NamedElement(document, "ComboBox", "DashboardFanModeSelector");
        XElement chargeLimit = NamedElement(document, "Slider", "DashboardChargeLimitSlider");
        XElement batteryProtection = NamedElement(document, "ToggleSwitch", "BatteryProtectionToggle");
        XElement temporaryFullCharge = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "Content")?.Contains(
                "ViewModel.FullChargeActionLabel",
                StringComparison.Ordinal) is true);
        Assert.Equal("OnDashboardPerformanceChanged", Attribute(performance, "SelectionChanged"));
        Assert.Equal("Power mode", Attribute(performance, "AutomationProperties.Name"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Power mode");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Performance");
        Assert.Equal(
            "{x:Bind ViewModel.AvailableActivePerformanceOptions, Mode=OneWay}",
            Attribute(performance, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedPerformanceOption, Mode=OneWay}",
            Attribute(performance, "SelectedItem"));
        Assert.Equal("OnDashboardFanModeSelectionChanged", Attribute(fans, "SelectionChanged"));
        Assert.Equal(
            "{x:Bind ViewModel.AvailableActiveFanModeOptions, Mode=OneWay}",
            Attribute(fans, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedFanModeOption, Mode=OneWay}",
            Attribute(fans, "SelectedItem"));
        Assert.Equal(
            "OnDashboardChargeLimitPointerPressed",
            Attribute(chargeLimit, "PointerPressed"));
        Assert.Equal(
            "OnDashboardChargeLimitPointerCaptureLost",
            Attribute(chargeLimit, "PointerCaptureLost"));
        Assert.Equal("OnDashboardChargeLimitKeyUp", Attribute(chargeLimit, "KeyUp"));
        Assert.Null(Attribute(chargeLimit, "ValueChanged"));
        Assert.Equal("OnBatteryProtectionToggled", Attribute(batteryProtection, "Toggled"));
        Assert.Equal("50", Attribute(chargeLimit, "Minimum"));
        Assert.Equal("80", Attribute(chargeLimit, "Maximum"));
        Assert.Equal("5", Attribute(chargeLimit, "StepFrequency"));
        Assert.Equal("StepValues", Attribute(chargeLimit, "SnapsTo"));
        Assert.Equal("Stretch", Attribute(chargeLimit, "HorizontalAlignment"));
        Assert.Equal(
            "{StaticResource SettingsRowStyle}",
            Attribute(
                batteryProtection.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Style"));
        Assert.Equal(
            "{StaticResource SettingsRowStyle}",
            Attribute(
                chargeLimit.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Style"));
        Assert.Equal(
            "{StaticResource SettingsLastRowStyle}",
            Attribute(
                temporaryFullCharge.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Style"));
        List<XElement> dashboardElements = document.Descendants().ToList();
        Assert.True(
            dashboardElements.IndexOf(batteryProtection)
            < dashboardElements.IndexOf(chargeLimit));

        Assert.DoesNotContain(
            NamedElement(document, "ScrollView", "HomePage")
                .Descendants()
                .Where(element => element.Name.LocalName == "Button"),
            button =>
            Attribute(button, "Content")?.StartsWith("Apply", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(xaml, "ViewModel.SetPerformanceCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetFanModeCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetChargeLimitCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.KeepManualChangesUntilCleared", StringComparison.Ordinal);
        Assert.Equal(
            6,
            document.Descendants().Count(element => element.Name.LocalName == "VisualState"));
        Assert.Equal(
            3,
            document.Descendants().Count(element =>
                element.Name.LocalName == "AdaptiveTrigger"
                && Attribute(element, "MinWindowWidth") == "900"));
        Assert.DoesNotContain("BalancedChoice", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BatteryActionColumn", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileChargeLimitEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCustomizeDashboard\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DashboardCardPicker\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LaptopCardsHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LightingCardsHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisplayCardsHost\"", xaml, StringComparison.Ordinal);
        XElement home = NamedElement(document, "ScrollView", "HomePage");
        XElement batteryTemplate = NamedTemplate(document, "DashboardBatteryCardTemplate");
        XElement fansTemplate = NamedTemplate(document, "DashboardFansCardTemplate");
        _ = Assert.Single(
            batteryTemplate.Descendants(),
            element => element.Name.LocalName == "Slider"
                && Attribute(element, "Name") == "DashboardChargeLimitSlider");
        Assert.Contains(fansTemplate.Descendants(), element =>
            element.Name.LocalName == "Slider"
            && Attribute(element, "Name") == "FixedFanSpeedSlider"
            && Attribute(element, "Minimum") == "2000"
            && Attribute(element, "Maximum") == "5400"
            && Attribute(element, "StepFrequency") == "100");
        Assert.DoesNotContain("\u00c2", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_UsesInteractionTrackerScrollViewsWithEdgeAlignedPadding()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());

        foreach (string pageName in new[]
                 {
                     "HomePage",
                     "ProfilesPage",
                     "LaptopPage",
                     "LightingPage",
                     "DisplayPage",
                     "SettingsPage",
                 })
        {
            XElement page = NamedElement(document, "ScrollView", pageName);
            Assert.Null(Attribute(page.Parent!, "Padding"));
            Assert.Equal("Disabled", Attribute(page, "HorizontalScrollMode"));
            Assert.Equal("Hidden", Attribute(page, "HorizontalScrollBarVisibility"));
            Assert.Equal("Enabled", Attribute(page, "VerticalScrollMode"));
            Assert.Equal("Auto", Attribute(page, "VerticalScrollBarVisibility"));
            XElement content = Assert.Single(
                page.Elements(),
                element => element.Name.LocalName == "StackPanel");
            Assert.Equal("28,20,28,28", Attribute(content, "Padding"));
        }
    }

    [Fact]
    public void MainWindow_DisplayPageUsesSettingsRowsAndHdrGatesColorProfiles()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement display = NamedElement(document, "ScrollView", "DisplayPage");
        XElement displayTemplate = NamedTemplate(document, "DashboardDisplayCardTemplate");

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "NavigationViewItem"
            && Attribute(element, "Content") == "Display"
            && Attribute(element, "Tag") == "display");
        Assert.Equal(
            2,
            displayTemplate.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsRowStyle}"));
        Assert.Single(displayTemplate.Descendants(), element =>
            Attribute(element, "Style") == "{StaticResource SettingsLastRowStyle}");

        XElement refresh = NamedElement(document, "ComboBox", "DisplayRefreshRatePicker");
        Assert.Equal(
            "{x:Bind DisplayViewModel.RefreshRateOptions, Mode=OneWay}",
            Attribute(refresh, "ItemsSource"));
        Assert.Equal(
            "{x:Bind DisplayViewModel.CanChangeRefreshRate, Mode=OneWay}",
            Attribute(refresh, "IsEnabled"));
        Assert.Equal("OnDisplayRefreshRateChanged", Attribute(refresh, "SelectionChanged"));

        XElement hdr = NamedElement(document, "ToggleSwitch", "DisplayHdrToggle");
        Assert.Equal("Right", Attribute(hdr, "HorizontalAlignment"));
        Assert.Equal("Center", Attribute(hdr, "VerticalAlignment"));
        Assert.Equal("OnDisplayHdrToggled", Attribute(hdr, "Toggled"));

        XElement color = NamedElement(document, "ComboBox", "DisplayColorProfilePicker");
        Assert.Equal(
            "{x:Bind DisplayViewModel.CanChangeColorProfile, Mode=OneWay}",
            Attribute(color, "IsEnabled"));
        Assert.Equal("OnDisplayColorProfileChanged", Attribute(color, "SelectionChanged"));
    }

    [Fact]
    public void MainWindow_UsesSettingsRowsDurationOptionsAndFlyoutDetails()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement settings = NamedElement(document, "ScrollView", "SettingsPage");
        XElement about = NamedElement(document, "ScrollView", "AboutPage");

        Assert.Equal(
            3,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsGroupStyle}"));
        Assert.Equal(
            8,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsRowStyle}"));
        Assert.Equal(
            2,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsLastRowStyle}"));

        foreach (XElement toggle in settings.Descendants()
                     .Where(element => element.Name.LocalName == "ToggleSwitch"))
        {
            Assert.Equal("Right", Attribute(toggle, "HorizontalAlignment"));
            Assert.Equal("0", Attribute(toggle, "MinWidth"));
            Assert.Equal(string.Empty, Attribute(toggle, "OffContent"));
            Assert.Equal(string.Empty, Attribute(toggle, "OnContent"));
        }

        Assert.Contains(about.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "AutomationProperties.Name") == "Export diagnostics"
            && Attribute(element, "Style") == "{StaticResource IconActionButtonStyle}");

        Assert.DoesNotContain("ViewModel.ManualChangeDurationOptions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedManualChangeDurationOption", xaml, StringComparison.Ordinal);
        XElement liveReadings = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ComboBox"
            && Attribute(element, "AutomationProperties.Name")
                == "Live readings refresh interval");
        Assert.Equal(
            "{StaticResource SettingsPickerStyle}",
            Attribute(liveReadings, "Style"));
        Assert.Equal(
            "{x:Bind ViewModel.LiveReadingsRefreshOptions, Mode=OneWay}",
            Attribute(liveReadings, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedLiveReadingsRefreshOption, Mode=TwoWay}",
            Attribute(liveReadings, "SelectedItem"));
        XElement appTheme = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ComboBox"
            && Attribute(element, "AutomationProperties.Name") == "App theme");
        Assert.Equal(
            "{StaticResource SettingsPickerStyle}",
            Attribute(appTheme, "Style"));
        XElement settingsPickerStyle = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style"
            && Attribute(element, "Key") == "SettingsPickerStyle");
        Assert.Equal("ComboBox", Attribute(settingsPickerStyle, "TargetType"));
        Assert.Equal(
            "{StaticResource DefaultComboBoxStyle}",
            Attribute(settingsPickerStyle, "BasedOn"));
        Assert.Contains(settingsPickerStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && Attribute(element, "Property") == "HorizontalAlignment"
            && Attribute(element, "Value") == "Right");
        Assert.Contains(settingsPickerStyle.Elements(), element =>
            element.Name.LocalName == "Setter"
            && Attribute(element, "Property") == "VerticalAlignment"
            && Attribute(element, "Value") == "Center");
        foreach (string pickerName in new[]
                 {
                     "Live readings refresh interval",
                     "App theme",
                 })
        {
            XElement picker = Assert.Single(settings.Descendants(), element =>
                element.Name.LocalName == "ComboBox"
                && Attribute(element, "AutomationProperties.Name") == pickerName);
            Assert.Equal(
                "{StaticResource SettingsPickerStyle}",
                Attribute(picker, "Style"));
            Assert.Null(Attribute(picker, "HorizontalAlignment"));
            Assert.Null(Attribute(picker, "VerticalAlignment"));
        }
        Assert.Contains(
            "Choose how often OpenBlade checks temperatures, fan speed, and net battery power.",
            xaml,
            StringComparison.Ordinal);
        XElement hoverContents = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ListView"
            && Attribute(element, "AutomationProperties.Name")
                == "Taskbar hover label contents");
        Assert.Equal("True", Attribute(hoverContents, "CanDragItems"));
        Assert.Equal("True", Attribute(hoverContents, "CanReorderItems"));
        Assert.Equal("Enabled", Attribute(hoverContents, "ReorderMode"));
        Assert.Contains(settings.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "{x:Bind ViewModel.TrayHoverLabelText, Mode=OneWay}");
        Assert.Contains(settings.Descendants(), element =>
            element.Name.LocalName == "Grid"
            && Attribute(element, "Visibility")
                == "{x:Bind ViewModel.HasTrayHoverLabelOverflow, Mode=OneWay}");
        Assert.Contains(settings.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text")
                == "{x:Bind ViewModel.TrayHoverLabelOverflowMessage, Mode=OneWay}");
        Assert.Contains(settings.Descendants(), element =>
            element.Name.LocalName == "ItemsControl"
            && Attribute(element, "ItemsSource")?.Contains(
                "DashboardTelemetryItems",
                StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            "Values may stay the same between checks.",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(settings.Descendants(), element =>
            element.Name.LocalName == "ToggleSwitch"
            && Attribute(element, "AutomationProperties.Name") == "Live readings");
        XElement startAtSignIn = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ToggleSwitch"
            && Attribute(element, "AutomationProperties.Name")
                == "Start OpenBlade when I sign in");
        Assert.Equal("OnStartAtSignInToggled", Attribute(startAtSignIn, "Toggled"));
        Assert.Contains("ViewModel.StartupDescription", xaml, StringComparison.Ordinal);
        XElement startMinimized = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ToggleSwitch"
            && Attribute(element, "AutomationProperties.Name")
                == "Start OpenBlade minimized");
        Assert.Equal("OnStartMinimizedToggled", Attribute(startMinimized, "Toggled"));
        XElement gamingMode = NamedElement(document, "ToggleSwitch", "GamingModeToggle");
        Assert.Equal("OnGamingModeToggled", Attribute(gamingMode, "Toggled"));
        Assert.Equal("Right", Attribute(gamingMode, "HorizontalAlignment"));
        Assert.Equal("Center", Attribute(gamingMode, "VerticalAlignment"));
        XElement startupAnimation = NamedElement(
            document,
            "ToggleSwitch",
            "StartupAnimationToggle");
        Assert.Equal("OnStartupAnimationToggled", Attribute(startupAnimation, "Toggled"));
        Assert.Equal("Right", Attribute(startupAnimation, "HorizontalAlignment"));
        Assert.Equal("Center", Attribute(startupAnimation, "VerticalAlignment"));
        Assert.Contains(
            "Open in the notification area without showing the window.",
            xaml,
            StringComparison.Ordinal);

        XElement warningIndicator = Assert.Single(document.Descendants(), element =>
            Attribute(element, "AutomationProperties.Name") == "Show warning details");
        Assert.Single(
            warningIndicator.Descendants(),
            element => element.Name.LocalName == "Flyout");
        Assert.Equal(
            "{StaticResource StatusPillButtonStyle}",
            Attribute(warningIndicator, "Style"));
        Assert.Null(Attribute(warningIndicator, "Background"));
        Assert.Contains(
            "ViewModel.AttentionSummary",
            Attribute(warningIndicator, "ToolTipService.ToolTip") ?? string.Empty,
            StringComparison.Ordinal);
        XElement warningLabel = NamedElement(document, "TextBlock", "AttentionIndicatorLabel");
        Assert.Contains(
            "ViewModel.AttentionBadgeLabel",
            Attribute(warningLabel, "Text") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            "{StaticResource StatusPillTextStyle}",
            Attribute(warningLabel, "Style"));
        XElement warningDot = Assert.Single(
            warningIndicator.Descendants(),
            element => element.Name.LocalName == "Ellipse");
        Assert.Equal("8", Attribute(warningDot, "Width"));
        Assert.Contains(
            "SystemFillColorCautionBrush",
            Attribute(warningDot, "Fill") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            warningIndicator.Descendants(),
            element => element.Name.LocalName == "FontIcon");
        XElement readinessBadge = NamedElement(document, "Button", "ProfileStateBadge");
        XElement readinessLabel = Assert.Single(readinessBadge.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text")?.Contains(
                "ViewModel.ProfileStateLabel",
                StringComparison.Ordinal) is true);
        Assert.Equal(
            "{StaticResource StatusPillTextStyle}",
            Attribute(readinessLabel, "Style"));
        Assert.Contains(warningIndicator.Ancestors(), element =>
            element.Name.LocalName == "Grid" && Attribute(element, "Name") == "HeroLayout");
        Assert.Contains(warningIndicator.Descendants(), element => element.Name.LocalName == "Flyout");
        XElement troubleshooting = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Expander"
                && Attribute(element, "AutomationProperties.Name") == "Troubleshooting details");
        Assert.Equal("{StaticResource CardContentExpanderStyle}", Attribute(troubleshooting, "Style"));
        Assert.Equal(
            "{StaticResource SettingsGroupStyle}",
            Attribute(
                troubleshooting.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Style"));
        foreach (string property in new[]
                 {
                     "ViewModel.TroubleshootingServiceStatus",
                     "ViewModel.TroubleshootingPowerSourceStatus",
                     "ViewModel.TroubleshootingControlsStatus",
                     "ViewModel.TroubleshootingOtherRazerSoftwareStatus",
                     "ViewModel.TroubleshootingRecentErrorStatus",
                 })
        {
            Assert.Contains(troubleshooting.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Style") == "{StaticResource TroubleshootingValueStyle}"
                && Attribute(element, "Text")?.Contains(property, StringComparison.Ordinal) is true);
        }
        XElement recentError = Assert.Single(
            troubleshooting.Descendants(),
            element => element.Name.LocalName == "Border"
                && Attribute(element, "Visibility")?.Contains(
                    "ViewModel.HasTroubleshootingRecentError",
                    StringComparison.Ordinal) is true);
        Assert.Contains(recentError.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Recent error");
        Assert.DoesNotContain(troubleshooting.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") is "Controller detection" or "Last operation");
        foreach (string label in new[]
                 {
                     "OpenBlade service",
                     "Laptop behavior",
                     "Power source",
                     "Other Razer software running",
                 })
        {
            Assert.Contains(troubleshooting.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Style") == "{StaticResource TroubleshootingLabelStyle}"
                && Attribute(element, "Text") == label);
        }
        Assert.Contains(troubleshooting.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Other Razer software running");
        Assert.DoesNotContain(troubleshooting.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") is "Versions" or "Laptop" or "Firmware");
        XElement settingsPage = NamedElement(document, "ScrollView", "SettingsPage");
        XElement aboutPage = NamedElement(document, "ScrollView", "AboutPage");
        XElement aboutNavigation = NamedElement(document, "NavigationViewItem", "AboutNavigationItem");
        Assert.Equal("about", Attribute(aboutNavigation, "Tag"));
        Assert.Contains(aboutNavigation.Ancestors(), element =>
            element.Name.LocalName == "NavigationView.FooterMenuItems");
        Assert.DoesNotContain(settingsPage.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Support");
        foreach (string section in new[] { "Versions", "Device information", "Support", "Troubleshooting" })
        {
            Assert.Contains(aboutPage.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text") == section);
        }
        Assert.Contains("ViewModel.AboutUiVersionStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("ViewModel.AboutServiceVersionStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Supported laptop", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Relationship to Razer", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("Independent open-source project; not affiliated with Razer.", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/Brand/OpenBlade.png", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Copy version information", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            4,
            aboutPage.Descendants().Count(element =>
                element.Name.LocalName == "HyperlinkButton"
                && Attribute(element, "Style") == "{StaticResource ExternalLinkButtonStyle}"));
        foreach (string styleKey in new[] { "ExternalLinkButtonStyle", "IconActionButtonStyle" })
        {
            XElement style = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "Style"
                && Attribute(element, "Key") == styleKey);
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "Background"
                && Attribute(setter, "Value") == "Transparent");
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "BorderThickness"
                && Attribute(setter, "Value") == "0");
        }
        Assert.Contains(
            "Save logs and system details with sensitive information removed.",
            xaml,
            StringComparison.Ordinal);

        XElement operationError = NamedElement(document, "InfoBar", "OperationErrorInfoBar");
        Assert.Equal("Error", Attribute(operationError, "Severity"));
        Assert.Equal("True", Attribute(operationError, "IsClosable"));
        Assert.Contains(
            "ViewModel.HasOperationFeedback",
            Attribute(operationError, "IsOpen") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "SymbolIcon" && Attribute(element, "Symbol") == "Accept");
    }

    [Fact]
    public void MainWindow_LaptopPageOwnsFirmwareWideLaptopBehavior()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement navigation = NamedElement(
            document,
            "NavigationViewItem",
            "LaptopNavigationItem");
        XElement laptop = NamedElement(document, "ScrollView", "LaptopPage");
        XElement keyboardTemplate = NamedTemplate(
            document,
            "DashboardKeyboardBehaviorCardTemplate");
        XElement startupTemplate = NamedTemplate(
            document,
            "DashboardStartupAnimationCardTemplate");
        XElement[] laptopCardElements = keyboardTemplate
            .Descendants()
            .Concat(startupTemplate.Descendants())
            .ToArray();
        XElement settings = NamedElement(document, "ScrollView", "SettingsPage");

        Assert.Equal("Laptop", Attribute(navigation, "Content"));
        Assert.Equal("laptop", Attribute(navigation, "Tag"));
        foreach (string controlName in new[]
                 {
                     "FunctionKeyFirmwareLoadingSlot",
                     "GamingModeToggle",
                     "StartupAnimationToggle",
                 })
        {
            Assert.Contains(laptopCardElements, element =>
                Attribute(element, "Name") == controlName);
            Assert.DoesNotContain(settings.Descendants(), element =>
                Attribute(element, "Name") == controlName);
        }

        Assert.DoesNotContain(settings.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Laptop behavior");
    }

    [Fact]
    public void Dashboard_HeroReadingsAreAFullWidthConfigurableFooter()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement readings = NamedElement(document, "Border", "HeroReadings");
        XElement hero = NamedElement(document, "Grid", "HeroLayout");

        Assert.Equal("1", Attribute(readings, "Grid.Row"));
        Assert.Equal("0", Attribute(readings, "Margin"));
        Assert.Equal("0,14,0,0", Attribute(readings, "Padding"));
        Assert.Null(Attribute(readings, "Grid.ColumnSpan"));
        Assert.DoesNotContain(
            hero.Elements(),
            element => element.Name.LocalName == "Grid.ColumnDefinitions");
        string? horizontalAlignment = Attribute(readings, "HorizontalAlignment");
        Assert.True(
            horizontalAlignment is null or "Stretch",
            $"Hero readings must use the native Stretch default or set it explicitly; found '{horizontalAlignment}'.");
        XElement readingsList = Assert.Single(
            readings.Descendants(),
            element => element.Name.LocalName == "ItemsRepeater"
                && Attribute(element, "ItemsSource")?.Contains(
                    "VisibleDashboardTelemetryGroups",
                    StringComparison.Ordinal) == true);
        XElement telemetryLayout = Assert.Single(
            readingsList.Descendants(),
            element => element.Name.LocalName == "UniformGridLayout");
        Assert.Equal("Horizontal", Attribute(telemetryLayout, "Orientation"));
        Assert.Equal("100", Attribute(telemetryLayout, "MinItemWidth"));
        Assert.Equal("0", Attribute(telemetryLayout, "MinColumnSpacing"));
        Assert.Equal("3", Attribute(telemetryLayout, "MaximumRowsOrColumns"));
        Assert.Equal("Fill", Attribute(telemetryLayout, "ItemsStretch"));
        Assert.DoesNotContain(
            readingsList.Descendants(),
            element => element.Name.LocalName == "StackLayout");
        Assert.Equal(
            "{x:Bind ViewModel.LiveReadingsRefreshToolTip, Mode=OneWay}",
            Attribute(readings, "AutomationProperties.HelpText"));
        XElement readingsGrid = Assert.Single(
            readings.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal("Transparent", Attribute(readingsGrid, "Background"));
        XElement toolTipProperty = Assert.Single(
            readingsGrid.Elements(),
            element => element.Name.LocalName == "ToolTipService.ToolTip");
        XElement toolTip = Assert.Single(
            toolTipProperty.Elements(),
            element => element.Name.LocalName == "ToolTip");
        XElement toolTipText = Assert.Single(
            toolTip.Elements(),
            element => element.Name.LocalName == "TextBlock");
        Assert.Equal("340", Attribute(toolTipText, "MaxWidth"));
        Assert.Equal("Wrap", Attribute(toolTipText, "TextWrapping"));
        Assert.Equal(
            "{x:Bind ViewModel.LiveReadingsRefreshToolTip, Mode=OneWay}",
            Attribute(toolTipText, "Text"));
        Assert.DoesNotContain(readings.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Live readings");
        Assert.Contains(readingsList.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "{x:Bind Label, Mode=OneTime}");
        Assert.Contains(readingsList.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "{x:Bind Value, Mode=OneTime}");
        XElement valueContainer = Assert.Single(
            readingsList.Descendants(),
            element => element.Name.LocalName == "StackPanel"
                && Attribute(element, "Spacing") == "3");
        Assert.Null(Attribute(valueContainer, "Margin"));
        Assert.Null(Attribute(valueContainer, "MinWidth"));
        Assert.Contains(valueContainer.Elements(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "TextWrapping") == "Wrap");

        string window = ReadTraySource("MainWindow.xaml.cs");
        Assert.DoesNotContain("Grid.SetRow(HeroReadings", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.SetColumn(HeroReadings", window, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroReadings.HorizontalAlignment", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TroubleshootingExpander_LocallyRemovesHeaderLayers()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement troubleshooting = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Expander"
                && Attribute(element, "AutomationProperties.Name") == "Troubleshooting details");
        XElement resources = Assert.Single(
            troubleshooting.Elements(),
            element => element.Name.LocalName == "Expander.Resources");
        XElement background = Assert.Single(
            resources.Descendants(),
            element => Attribute(element, "Key") == "ExpanderHeaderBackground");
        XElement borderThickness = Assert.Single(
            resources.Descendants(),
            element => Attribute(element, "Key") == "ExpanderHeaderBorderThickness");
        XElement header = Assert.Single(
            troubleshooting.Elements()
                .Where(element => element.Name.LocalName == "Expander.Header")
                .SelectMany(element => element.Elements()));

        Assert.Equal("Transparent", Attribute(background, "Color"));
        Assert.Equal("0", borderThickness.Value.Trim());
        Assert.Equal("0,14,0,14", Attribute(header, "Margin"));
    }

    [Fact]
    public void MainWindow_UsesResponsiveProfileNamesAndNativeTypography()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);

        XElement comparisonProfiles = NamedElement(document, "StackPanel", "ProfilesComparisonLayout");
        XElement compactProfiles = NamedElement(document, "StackPanel", "ProfilesCompactLayout");
        Assert.Equal("Collapsed", Attribute(compactProfiles, "Visibility"));
        XElement[] pendingLabels = comparisonProfiles.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text") == "Changes pending")
            .ToArray();
        Assert.Equal(3, pendingLabels.Length);
        Assert.All(pendingLabels, label =>
        {
            Assert.Equal("StackPanel", label.Parent?.Name.LocalName);
            Assert.Null(Attribute(label.Parent!, "Orientation"));
        });
        XElement[] activeProfilePills = comparisonProfiles.Descendants()
            .Where(element => element.Name.LocalName == "Border"
                && Attribute(element, "Style") == "{StaticResource ActiveProfilePillStyle}")
            .ToArray();
        Assert.Equal(3, activeProfilePills.Length);
        Assert.All(activeProfilePills, pill =>
        {
            Assert.Equal("StackPanel", pill.Parent?.Name.LocalName);
            Assert.Equal("Horizontal", Attribute(pill.Parent!, "Orientation"));
        });
        _ = NamedElement(document, "Grid", "Root");
        XElement profileStateBadge = NamedElement(document, "Button", "ProfileStateBadge");
        Assert.Equal(
            "{StaticResource StatusPillButtonStyle}",
            Attribute(profileStateBadge, "Style"));
        Assert.Equal("OnProfileStateBadgeClick", Attribute(profileStateBadge, "Click"));
        Assert.Equal(
            "Open troubleshooting details",
            Attribute(profileStateBadge, "AutomationProperties.Name"));
        Assert.Equal(
            3,
            profileStateBadge.Descendants().Count(element =>
                element.Name.LocalName == "Ellipse"));
        foreach (string property in new[]
                 {
                     "ViewModel.IsReadinessReady",
                     "ViewModel.IsReadinessWarning",
                     "ViewModel.IsReadinessError",
                 })
        {
            Assert.Contains(profileStateBadge.Descendants(), element =>
                element.Name.LocalName == "Ellipse"
                && Attribute(element, "Visibility")?.Contains(
                    property,
                    StringComparison.Ordinal) is true);
        }
        Assert.Contains(profileStateBadge.Descendants(), element =>
            element.Name.LocalName == "ProgressRing"
            && Attribute(element, "Visibility")?.Contains(
                "ViewModel.IsReadinessChecking",
                StringComparison.Ordinal) is true
            && Attribute(element, "IsActive")?.Contains(
                "ViewModel.IsReadinessChecking",
                StringComparison.Ordinal) is true);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "Border"
            && Attribute(element, "Name") == "ProfileStateBadge");
        foreach (XElement activeBadgeText in document.Descendants().Where(element =>
                     element.Name.LocalName == "TextBlock"
                     && Attribute(element, "Text") == "Active"))
        {
            Assert.NotNull(activeBadgeText.Parent);
            Assert.Equal("Border", activeBadgeText.Parent!.Name.LocalName);
            Assert.Equal(
                "{StaticResource ActiveProfilePillStyle}",
                Attribute(activeBadgeText.Parent, "Style"));
            Assert.Equal(
                "{StaticResource ActiveProfilePillTextStyle}",
                Attribute(activeBadgeText, "Style"));
            Assert.NotNull(activeBadgeText.Parent.Parent);
            Assert.Equal("StackPanel", activeBadgeText.Parent.Parent!.Name.LocalName);
            Assert.Equal("Horizontal", Attribute(activeBadgeText.Parent.Parent, "Orientation"));
        }
        Assert.DoesNotContain("ProfileChargeLimitEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsBatteryProfileActive", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsUsbCProfileActive", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PluggedInProfilePresentation.PowerMode", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UsbCProfilePresentation.LightingEffect", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.BatteryProfilePresentation.DisplayRefreshRate", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedProfilePresentation.PowerMode", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.AvailableUsbCPerformanceOptions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.AvailableUsbCFanModes", xaml, StringComparison.Ordinal);

        string window = ReadTraySource("MainWindow.xaml.cs");
        Assert.Contains("ProfilesStackThresholdDip", window, StringComparison.Ordinal);
        Assert.Contains("AttentionIndicatorLabel.Visibility = stack", window, StringComparison.Ordinal);
        Assert.Contains("AttentionIndicator.Padding = stack", window, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged += OnRootSizeChanged", window, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged -= OnRootSizeChanged", window, StringComparison.Ordinal);
        Assert.Contains("UpdateProfilesLayout", window, StringComparison.Ordinal);
        int badgeHandler = window.IndexOf(
            "private void OnProfileStateBadgeClick",
            StringComparison.Ordinal);
        Assert.True(badgeHandler >= 0);
        string badgeHandlerSource = window[badgeHandler..];
        int selectAbout = badgeHandlerSource.IndexOf(
            "MainNavigation.SelectedItem = AboutNavigationItem",
            StringComparison.Ordinal);
        int expandTroubleshooting = badgeHandlerSource.IndexOf(
            "TroubleshootingExpander.IsExpanded = true",
            StringComparison.Ordinal);
        int focusTroubleshooting = badgeHandlerSource.IndexOf(
            "TroubleshootingExpander.Focus(FocusState.Programmatic)",
            StringComparison.Ordinal);
        int bringIntoView = badgeHandlerSource.IndexOf(
            "TroubleshootingExpander.StartBringIntoView",
            StringComparison.Ordinal);
        Assert.True(selectAbout >= 0);
        Assert.True(expandTroubleshooting > selectAbout);
        Assert.True(focusTroubleshooting > expandTroubleshooting);
        Assert.True(bringIntoView > focusTroubleshooting);
        Assert.Contains("DispatcherQueuePriority.Low", badgeHandlerSource, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignmentRatio = 0", badgeHandlerSource, StringComparison.Ordinal);

        AssertStyleBasedOn(document, "PageTitleStyle", "{StaticResource TitleTextBlockStyle}");
        AssertStyleBasedOn(document, "SectionHeaderStyle", "{StaticResource SubtitleTextBlockStyle}");
        AssertStyleBasedOn(document, "CardTitleStyle", "{StaticResource BodyStrongTextBlockStyle}");
        AssertStyleBasedOn(document, "SecondaryTextStyle", "{StaticResource BodyTextBlockStyle}");
        Assert.Equal(
            "{x:Bind ViewModel.HasOperationFeedback, Mode=OneWay}",
            Attribute(NamedElement(document, "InfoBar", "OperationErrorInfoBar"), "Visibility"));
        Assert.Equal(
            "{x:Bind ViewModel.IsLightingControlUnavailable, Mode=OneWay}",
            Attribute(NamedElement(document, "InfoBar", "LightingUnavailableInfoBar"), "Visibility"));
        Assert.Contains("{StaticResource TitleLargeTextBlockStyle}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource AccentButtonStyle}", xaml, StringComparison.Ordinal);
        Assert.Equal(
            7,
            document.Descendants().Count(element =>
                element.Name.LocalName == "EntranceThemeTransition"));
    }

    [Fact]
    public void ApplicationLoadsNativeControlResources()
    {
        string xaml = ReadTraySource("App.xaml");

        Assert.Contains("XamlControlsResources", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericEditorConverterDelegatesToTheTestedCorePolicy()
    {
        string source = ReadTraySource("Int32DoubleConverter.cs");

        Assert.Contains(": IValueConverter", source, StringComparison.Ordinal);
        Assert.Contains("IntegerEditorValue.ToDouble", source, StringComparison.Ordinal);
        Assert.Contains("IntegerEditorValue.ToInt32", source, StringComparison.Ordinal);
        Assert.Contains("throw new ArgumentException", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedCapabilities_KeepLightingPageVisibleButUnavailable()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(
            verified: false,
            blocked: true,
            capabilities: DeviceCapabilities.Unavailable()));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.Empty(viewModel.AvailableLightingEffects);
        Assert.Empty(viewModel.AvailableLightingSettings);
        Assert.NotEmpty(viewModel.AvailableLightingEffectOptions);
        Assert.Equal(LightingEffect.Off, viewModel.AvailableLightingEffectOptions[0].Effect);
        Assert.Equal(
            viewModel.AvailableLightingEffectOptions
                .Skip(1)
                .Select(option => option.Label)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase),
            viewModel.AvailableLightingEffectOptions
                .Skip(1)
                .Select(option => option.Label));
        Assert.True(viewModel.IsLightingVisible);
        Assert.False(viewModel.HasLightingCapability);
        Assert.False(viewModel.CanControlLighting);
        Assert.True(viewModel.IsLightingControlUnavailable);
        Assert.Contains(
            "can’t find a supported laptop",
            viewModel.LightingControlUnavailableMessage,
            StringComparison.Ordinal);
        Assert.True(viewModel.EnableFullChargeOverride);
    }

    [Fact]
    public async Task UnavailableLightingInterface_ExplainsPotentialRazerConflict()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();

        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.Unavailable(),
            conflicts: ["Razer Synapse"]));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.IsLightingVisible);
        Assert.False(viewModel.CanControlLighting);
        Assert.Contains(
            "Another Razer app may be using",
            viewModel.LightingControlUnavailableMessage,
            StringComparison.Ordinal);
        Assert.False(viewModel.SetLightingCommand.CanExecute(
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting));
    }

    [Fact]
    public async Task UnsupportedPerformanceAndFanValuesAreDisabledInSharedTrayMenu()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        var controller = new TrayMenuController(viewModel, () => { }, () => { });

        Assert.False(controller.Find("Silent").IsEnabled);
        Assert.True(controller.Find("Balanced").IsEnabled);
        Assert.False(controller.Find("Performance").IsEnabled);
        Assert.True(controller.Find("Automatic").IsEnabled);
        Assert.False(controller.Find("Maximum").IsEnabled);
    }

    [Fact]
    public async Task ControllerAdvisoryDoesNotDisableAdmittedMutationOrOpenAndExit()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            conflicts: ["RazerAppEngine"],
            capabilities: DeviceCapabilities.SafeTargetDefaults()));
        await WaitUntilAsync(() => viewModel.HasControllerAdvisory);
        var controller = new TrayMenuController(viewModel, () => { }, () => { });

        Assert.True(controller.Find("Automatic").IsEnabled);
        Assert.True(controller.Find("Open").IsEnabled);
        Assert.True(controller.Find("Exit").IsEnabled);
        Assert.True(viewModel.HasControllerAdvisory);
        Assert.Contains("RazerAppEngine", viewModel.ControllerAdvisoryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileEditorsBuildAndSaveBothPowerProfiles()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            }));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        viewModel.PluggedInPerformanceEditor = PerformancePreset.Balanced;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        viewModel.BatteryPerformanceEditor = PerformancePreset.BatterySaver;
        viewModel.BatteryFanModeEditor = FanMode.Automatic;
        viewModel.ProfileChargeLimitEditor = 75;

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.NotNull(client.SavedProfiles);
        Assert.Equal(PerformancePreset.Balanced, client.SavedProfiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Maximum, client.SavedProfiles.PluggedIn.Fans);
        Assert.Equal(PerformancePreset.BatterySaver, client.SavedProfiles.Battery.Performance);
        Assert.Equal(75, client.SavedProfiles.BatteryPolicy.ChargeLimitPercent);
    }

    [Fact]
    public async Task ProfileEditorsHydrateFromServiceAndPreserveUnsavedEdits()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        ProfileSet initialProfiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
            },
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            }) with
        {
            Profiles = initialProfiles,
        };

        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.ProfileChargeLimitEditor == 75);

        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(FanMode.Maximum, viewModel.PluggedInFanModeEditor);
        viewModel.BatteryPerformanceEditor = PerformancePreset.Balanced;
        viewModel.BatteryFanModeEditor = FanMode.Maximum;

        ProfileSet refreshedProfiles = ProfileDefaults.CreateSafeDefaults() with
        {
            PluggedIn = ProfileDefaults.CreateSafeDefaults().PluggedIn with
            {
                Performance = PerformancePreset.Silent,
            },
            BatteryPolicy = BatteryPolicy.Create(70),
        };
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Profiles refreshed",
            Profiles = refreshedProfiles,
        });
        await WaitUntilAsync(() => viewModel.Profiles == refreshedProfiles);

        Assert.Equal(refreshedProfiles, viewModel.Profiles);
        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(FanMode.Maximum, viewModel.PluggedInFanModeEditor);
        Assert.Equal(FanMode.Maximum, viewModel.BatteryFanModeEditor);
        Assert.Equal(75, viewModel.ProfileChargeLimitEditor);
    }

    [Fact]
    public async Task SuccessfulProfileSaveReleasesDraftGuardForLaterServiceProfiles()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        viewModel.PluggedInPerformanceEditor = PerformancePreset.Balanced;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        viewModel.ProfileChargeLimitEditor = 75;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand)
            .ExecuteAsync(null);

        Assert.Equal(client.SavedProfiles, viewModel.Profiles);
        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(FanMode.Maximum, viewModel.PluggedInFanModeEditor);
        Assert.Equal(75, viewModel.ProfileChargeLimitEditor);

        ProfileSet laterProfiles = ProfileDefaults.CreateSafeDefaults() with
        {
            Battery = ProfileDefaults.CreateSafeDefaults().Battery with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
            },
            BatteryPolicy = BatteryPolicy.Create(70),
        };
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Later profiles",
            Profiles = laterProfiles,
        });
        await WaitUntilAsync(() => viewModel.Profiles == laterProfiles);

        Assert.Equal(laterProfiles, viewModel.Profiles);
        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(FanMode.Automatic, viewModel.PluggedInFanModeEditor);
        Assert.Equal(FanMode.Maximum, viewModel.BatteryFanModeEditor);
        Assert.Equal(70, viewModel.ProfileChargeLimitEditor);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public async Task SaveProfiles_RejectsNonBalancedMaximumCombination(
        PerformancePreset performance)
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            }));
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        viewModel.PluggedInPerformanceEditor = performance;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(
            viewModel.SaveProfilesCommand);

        Assert.False(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.Null(client.SavedProfiles);
    }

    [Fact]
    public async Task SaveProfiles_IsDisabledWhenEitherDraftUsesUnsupportedCapabilities()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware && viewModel.AvailableLightingSettings.Count > 0);
        viewModel.PluggedInPerformanceEditor = PerformancePreset.Performance;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand);

        Assert.False(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.Null(client.SavedProfiles);
    }

    [Fact]
    public async Task SaveProfiles_AllowsBatteryChangeWhenUnavailableFieldsAreUnchanged()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset>(),
                new HashSet<FanMode>(),
                new HashSet<LightingEffect>(),
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.CanControlHardware);
        viewModel.ProfileChargeLimitEditor = 75;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(viewModel.SaveProfilesCommand);

        Assert.False(viewModel.IsProfileControlVisible);
        Assert.True(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.NotNull(client.SavedProfiles);
        Assert.Equal(75, client.SavedProfiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(
            ProfileDefaults.CreateSafeDefaults().PluggedIn,
            client.SavedProfiles.PluggedIn);
        Assert.Equal(
            ProfileDefaults.CreateSafeDefaults().Battery,
            client.SavedProfiles.Battery);
    }

    [Fact]
    public async Task UndefinedPerformanceAndFanCapabilitiesAreNotExposedOrExecutable()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        var invalidPerformance = (PerformancePreset)999;
        var invalidFan = (FanMode)999;
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced, invalidPerformance },
                new HashSet<FanMode> { FanMode.Automatic, invalidFan },
                new HashSet<LightingEffect> { LightingEffect.Off },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.AvailableFanModes.Count > 0);

        Assert.Equal([PerformancePreset.Balanced], viewModel.AvailablePerformancePresets);
        Assert.Equal([FanMode.Automatic], viewModel.AvailableFanModes);
        Assert.False(viewModel.SetPerformanceCommand.CanExecute(invalidPerformance));
        Assert.False(viewModel.SetFanModeCommand.CanExecute(invalidFan));
    }

    [Fact]
    public async Task UndefinedLightingCapabilityIsNotExposedOrExecutable()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        var invalidEffect = (LightingEffect)999;
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off, invalidEffect },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.AvailableLightingEffects.Count > 0);

        Assert.Equal([LightingEffect.Off], viewModel.AvailableLightingEffects);
        Assert.All(
            viewModel.AvailableLightingSettings,
            setting => Assert.Equal(LightingEffect.Off, setting.Effect));
        Assert.False(viewModel.SetLightingCommand.CanExecute(
            new LightingSettings(invalidEffect, default, 0)));
    }

    [Fact]
    public async Task MissingBatteryCapabilityDisablesOnlyBatteryCommands()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int>(), false, false)));
        await WaitUntilAsync(() => viewModel.IsConnected);

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.Empty(viewModel.ChargeLimitOptions);
        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(75));
        Assert.False(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Fact]
    public async Task InvalidBatteryCapabilitySetFailsClosedWithoutPartialOptions()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int> { 50, 79 }, true, true)));
        await WaitUntilAsync(() => viewModel.Capabilities.ChargeLimits.Contains(79));

        Assert.True(viewModel.SetFanModeCommand.CanExecute(FanMode.Automatic));
        Assert.Empty(viewModel.ChargeLimitOptions);
        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(50));
        Assert.False(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
    }

    [Fact]
    public async Task SparseBatteryCapabilitiesExposeOnlyAdmittedIndependentActions()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int> { 80 },
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: false)));
        await WaitUntilAsync(() => viewModel.ChargeLimitOptions.Count > 0);

        Assert.Equal([80], viewModel.ChargeLimitOptions);
        Assert.True(viewModel.SetChargeLimitCommand.CanExecute(80));
        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(75));
        Assert.True(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
        Assert.Equal("Unavailable", viewModel.FullChargeActionLabel);

        await Assert.IsType<AsyncRelayCommand>(viewModel.ToggleBatteryProtectionCommand)
            .ExecuteAsync(null);
        Assert.False(client.SavedBatteryProtection);
    }

    [Fact]
    public async Task UnknownTemporaryStateDisablesEveryBatteryMutation()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Off },
                new HashSet<int> { 80 },
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true),
            confirmedBattery: new ConfirmedBatteryState(true, 80, null)));
        await WaitUntilAsync(() => viewModel.ChargeLimitOptions.Count > 0);

        Assert.False(viewModel.SetChargeLimitCommand.CanExecute(80));
        Assert.False(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.False(viewModel.ToggleFullChargeCommand.CanExecute(true));
        Assert.Contains("state unavailable", viewModel.BatterySourceLabel);
    }

    [Fact]
    public async Task DashboardChargeLimitDoesNotMutateSettingsDraftAndSendsDashboardValue()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        viewModel.ProfileChargeLimitEditor = 70;
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
                SupportsTemporaryFullCharge = true,
            }));
        await WaitUntilAsync(() => viewModel.IsConnected);

        viewModel.ChargeLimitPercent = 75;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetChargeLimitCommand)
            .ExecuteAsync(viewModel.ChargeLimitPercent);

        Assert.Equal(70, viewModel.ProfileChargeLimitEditor);
        Assert.Equal(75, client.SavedBatteryPolicy?.ChargeLimitPercent);
    }

    [Fact]
    public async Task LightingEditorBuildsTypedSettingsOnlyForAdmittedEffects()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Static },
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 64;
        viewModel.LightingRedEditor = 12;
        viewModel.LightingGreenEditor = 34;
        viewModel.LightingBlueEditor = 56;

        Assert.Equal(
            LightingSettingsFactory.Static(new RgbColor(12, 34, 56), 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));

        viewModel.LightingEffectEditor = LightingEffect.Spectrum;
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
    }

    [Fact]
    public async Task LightingEditorBuildsEffectSpecificCoreParityParameters()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        client.Publish(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
                ChargeLimits(), true, true)));
        await WaitUntilAsync(() =>
            viewModel.CanControlHardware
            && viewModel.AvailableLightingEffectOptions.Count ==
                Enum.GetValues<LightingEffect>().Length);
        Assert.Equal(LightingEffect.Off, viewModel.AvailableLightingEffectOptions[0].Effect);
        Assert.Equal(
            viewModel.AvailableLightingEffectOptions
                .Skip(1)
                .Select(option => option.Label)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase),
            viewModel.AvailableLightingEffectOptions
                .Skip(1)
                .Select(option => option.Label));

        viewModel.LightingBrightnessEditor = 64;
        viewModel.LightingRedEditor = 12;
        viewModel.LightingGreenEditor = 34;
        viewModel.LightingBlueEditor = 56;
        viewModel.LightingSecondaryRedEditor = 78;
        viewModel.LightingSecondaryGreenEditor = 90;
        viewModel.LightingSecondaryBlueEditor = 123;

        viewModel.LightingEffectEditor = LightingEffect.Breathing;
        viewModel.LightingBreathingColorModeEditor = BreathingColorMode.Dual;
        Assert.Equal(
            LightingSettingsFactory.BreathingDual(
                new RgbColor(12, 34, 56),
                new RgbColor(78, 90, 123),
                64),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.True(viewModel.LightingShowsSecondaryColor);

        viewModel.LightingBreathingColorModeEditor = BreathingColorMode.Random;
        Assert.Equal(
            LightingSettingsFactory.BreathingRandom(64),
            viewModel.LightingDraft);
        Assert.False(viewModel.LightingShowsPrimaryColor);
        Assert.False(viewModel.LightingShowsSecondaryColor);

        viewModel.LightingEffectEditor = LightingEffect.Reactive;
        viewModel.LightingReactiveDurationEditor = ReactiveLightingDuration.Long;
        Assert.Equal(
            LightingSettingsFactory.Reactive(
                new RgbColor(12, 34, 56),
                64,
                ReactiveLightingDuration.Long),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsReactiveDuration);

        viewModel.LightingEffectEditor = LightingEffect.Ripple;
        Assert.Equal(
            LightingSettingsFactory.Ripple(new RgbColor(12, 34, 56), 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.False(viewModel.LightingShowsReactiveDuration);

        viewModel.LightingEffectEditor = LightingEffect.Tidal;
        viewModel.LightingDirectionEditor = LightingDirection.Leftward;
        viewModel.LightingRandomColorsEditor = false;
        Assert.Equal(
            LightingSettingsFactory.Tidal(
                new RgbColor(12, 34, 56),
                new RgbColor(78, 90, 123),
                randomColors: false,
                LightingDirection.Leftward,
                brightness: 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.True(viewModel.LightingShowsSecondaryColor);
        Assert.True(viewModel.LightingShowsDirection);
        Assert.Equal(
            ["Outward", "Inward"],
            viewModel.LightingDirectionOptions.Select(option => option.Label));
        Assert.Equal("Outward", viewModel.SelectedLightingDirectionOption.Label);
        Assert.Equal("Primary color", viewModel.LightingPrimaryColorLabel);

        viewModel.LightingRandomColorsEditor = true;
        Assert.Equal(
            LightingSettingsFactory.Tidal(
                new RgbColor(12, 34, 56),
                new RgbColor(78, 90, 123),
                randomColors: true,
                LightingDirection.Leftward,
                brightness: 64),
            viewModel.LightingDraft);
        Assert.False(viewModel.LightingShowsPrimaryColor);
        Assert.False(viewModel.LightingShowsSecondaryColor);

        viewModel.LightingEffectEditor = LightingEffect.Starlight;
        viewModel.LightingStarlightColorModeEditor = StarlightColorMode.Dual;
        viewModel.LightingStarlightDurationEditor = StarlightLightingDuration.Long;
        Assert.Equal(
            LightingSettingsFactory.StarlightDual(
                new RgbColor(12, 34, 56),
                new RgbColor(78, 90, 123),
                64,
                StarlightLightingDuration.Long),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsStarlightColorMode);
        Assert.True(viewModel.LightingShowsStarlightDuration);
        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.True(viewModel.LightingShowsSecondaryColor);

        viewModel.LightingStarlightColorModeEditor = StarlightColorMode.Random;
        Assert.Equal(
            LightingSettingsFactory.StarlightRandom(
                64,
                StarlightLightingDuration.Long),
            viewModel.LightingDraft);
        Assert.False(viewModel.LightingShowsPrimaryColor);
        Assert.False(viewModel.LightingShowsSecondaryColor);

        viewModel.LightingEffectEditor = LightingEffect.Wave;
        viewModel.LightingDirectionEditor = LightingDirection.Leftward;
        Assert.Equal(
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsDirection);
        Assert.False(viewModel.LightingShowsPrimaryColor);

        viewModel.LightingEffectEditor = LightingEffect.Wheel;
        viewModel.LightingRotationDirectionEditor =
            LightingRotationDirection.Counterclockwise;
        Assert.Equal(
            LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                64),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsRotationDirection);
        Assert.False(viewModel.LightingShowsPrimaryColor);

        viewModel.LightingEffectEditor = LightingEffect.Spectrum;
        Assert.Equal(
            LightingSettingsFactory.SpectrumCycling(64),
            viewModel.LightingDraft);
        Assert.Equal(
            "Spectrum Cycling",
            viewModel.SelectedLightingEffectOption?.Label);
    }

    [Fact]
    public void MainWindow_LightingPageIsAlwaysVisibleAndDisablesItsControlsAsOneGroup()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement navigation = NamedElement(
            document,
            "NavigationViewItem",
            "LightingNavigationItem");
        XElement page = NamedElement(document, "ScrollView", "LightingPage");
        XElement lightingTemplate = NamedTemplate(document, "DashboardLightingCardTemplate");
        XElement effectPicker = NamedElement(document, "ComboBox", "LightingEffectPicker");
        XElement controls = NamedElement(document, "ContentControl", "LightingControls");
        XElement infoBar = NamedElement(document, "InfoBar", "LightingUnavailableInfoBar");
        XElement displayOffToggle = NamedElement(
            document,
            "ToggleSwitch",
            "LightingDisplayOffToggle");
        XElement idleTimeoutToggle = NamedElement(
            document,
            "ToggleSwitch",
            "LightingIdleTimeoutToggle");
        XElement idleTimeoutNumberBox = NamedElement(
            document,
            "NumberBox",
            "LightingIdleTimeoutNumberBox");

        Assert.Equal("lighting", Attribute(navigation, "Tag"));
        Assert.Null(Attribute(navigation, "Visibility"));
        Assert.Equal("Collapsed", Attribute(page, "Visibility"));
        Assert.Equal(
            "{x:Bind ViewModel.CanControlLighting, Mode=OneWay}",
            Attribute(controls, "IsEnabled"));
        Assert.Equal(
            "{x:Bind ViewModel.IsLightingControlUnavailable, Mode=OneWay}",
            Attribute(infoBar, "IsOpen"));
        Assert.Equal(
            "{x:Bind ViewModel.LightingControlUnavailableMessage, Mode=OneWay}",
            Attribute(infoBar, "Message"));
        Assert.Equal(
            "{x:Bind ViewModel.AvailableLightingEffectOptions, Mode=OneWay}",
            Attribute(effectPicker, "ItemsSource"));
        Assert.All([displayOffToggle, idleTimeoutToggle], toggle =>
        {
            Assert.Equal("Right", Attribute(toggle, "HorizontalAlignment"));
            Assert.Equal("Center", Attribute(toggle, "VerticalAlignment"));
            Assert.Equal("0", Attribute(toggle, "MinWidth"));
            Assert.Equal(string.Empty, Attribute(toggle, "OffContent"));
            Assert.Equal(string.Empty, Attribute(toggle, "OnContent"));
        });
        Assert.Equal("Right", Attribute(idleTimeoutNumberBox, "HorizontalAlignment"));
        Assert.Equal("Center", Attribute(idleTimeoutNumberBox, "VerticalAlignment"));
        Assert.Equal("Center", Attribute(idleTimeoutNumberBox, "VerticalContentAlignment"));
        string lightingXaml = lightingTemplate.ToString();
        Assert.Contains("Breathing colors", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Reactive duration", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Random colors", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Lighting direction", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Wheel rotation direction", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Starlight colors", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Starlight duration", lightingXaml, StringComparison.Ordinal);
        XElement[] colorPickers = lightingTemplate.Descendants()
            .Where(element => element.Name.LocalName == "ColorPicker")
            .ToArray();
        Assert.Equal(2, colorPickers.Length);
        Assert.All(colorPickers, picker =>
        {
            Assert.Equal("False", Attribute(picker, "IsAlphaEnabled"));
            Assert.Equal("True", Attribute(picker, "IsColorChannelTextInputVisible"));
            Assert.Equal("True", Attribute(picker, "IsHexInputVisible"));
        });
        Assert.Equal(
            0,
            page.Descendants().Count(element =>
                element.Name.LocalName == "Expander"
                && Attribute(element, "Header") == "Advanced RGB values"));
        Assert.Contains("LightingDirectionOptions", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedLightingDirectionOption", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("LightingPrimaryColorEditor", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("LightingSecondaryColorEditor", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Ambient Awareness screen region", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Choose which part of the display colors the keyboard.", lightingXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Audio", lightingXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fire", lightingXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ripple", lightingXaml, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadMainWindowXaml() => ReadTraySource("MainWindow.xaml");

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    private static XElement NamedElement(
        XDocument document,
        string elementName,
        string name) => Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == elementName
            && Attribute(element, "Name") == name);

    private static XElement NamedTemplate(XDocument document, string key) =>
        Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate"
            && Attribute(element, "Key") == key);

    private static void AssertStyleBasedOn(
        XDocument document,
        string key,
        string basedOn)
    {
        XElement style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style"
            && Attribute(element, "Key") == key);
        Assert.Equal(basedOn, Attribute(style, "BasedOn"));
    }

    private static string ReadTraySource(
        string fileName,
        [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            "..",
            "src",
            "OpenBlade.Tray",
            fileName)));

    private static string ReadTrayCoreSource(
        string fileName,
        [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            "..",
            "src",
            "OpenBlade.Tray.Core",
            fileName)));

    private static ServiceStatusSnapshot CreateStatus(
        bool verified,
        bool blocked,
        DeviceCapabilities capabilities,
        IReadOnlyList<string>? conflicts = null,
        bool includeConfirmedBattery = true,
        ConfirmedBatteryState? confirmedBattery = null)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1, verified, blocked, conflicts ?? [], null, [], blocked ? "Blocked" : "Ready",
            capabilities,
            verified && includeConfirmedBattery
                ? new ConfirmedDeviceState(
                    null,
                    null,
                    confirmedBattery ?? new ConfirmedBatteryState(true, 80, false),
                    null)
                : null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null, null, null, false);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int index = 0; index < 2_000 && !predicate(); index++)
        {
            await Task.Delay(1);
        }
        Assert.True(predicate());
    }

    private static HashSet<int> ChargeLimits() => [50, 55, 60, 65, 70, 75, 80];

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class PresentationClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> statuses = Channel.CreateUnbounded<ServiceWatchUpdate>();
        public ProfileSet? SavedProfiles { get; private set; }
        public BatteryPolicy? SavedBatteryPolicy { get; private set; }
        public bool? SavedBatteryProtection { get; private set; }
        public void Publish(ServiceStatusSnapshot status) =>
            statuses.Writer.TryWrite(new ServiceWatchUpdate.Connected(status));
        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (ServiceWatchUpdate status in statuses.Reader.ReadAllAsync(token)) yield return status;
        }
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token)
        {
            SavedProfiles = profiles;
            return Result();
        }
        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token)
        {
            SavedBatteryPolicy = value;
            return Result();
        }
        public Task<ControlOperationResult> SetBatteryProtectionAsync(bool enabled, CancellationToken token)
        {
            SavedBatteryProtection = enabled;
            return Result();
        }
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => Result();
        private static Task<ControlOperationResult> Result() => Task.FromResult(
            new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));
    }
}
