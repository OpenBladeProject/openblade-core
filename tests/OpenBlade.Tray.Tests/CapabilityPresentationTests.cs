using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CapabilityPresentationTests
{
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
        Assert.Equal(
            "{x:Bind ViewModel.AvailableActivePerformanceOptions, Mode=OneWay}",
            Attribute(performance, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedPerformanceOption, Mode=OneWay}",
            Attribute(performance, "SelectedItem"));
        Assert.Equal("OnDashboardFanModeSelectionChanged", Attribute(fans, "SelectionChanged"));
        Assert.Equal(
            "{x:Bind ViewModel.AvailableActiveFanModes, Mode=OneWay}",
            Attribute(fans, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedFanMode, Mode=OneWay}",
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
            document.Descendants().Where(element => element.Name.LocalName == "Button"),
            button =>
            Attribute(button, "Content")?.StartsWith("Apply", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(xaml, "ViewModel.SetPerformanceCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetFanModeCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetChargeLimitCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.KeepManualChangesUntilCleared", StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "VisualState");
        Assert.DoesNotContain("BalancedChoice", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BatteryActionColumn", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileChargeLimitEditor", xaml, StringComparison.Ordinal);
        _ = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Slider");
        Assert.DoesNotContain("\u00c2", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_KeepsPagePaddingInsideEdgeAlignedScrollViewers()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());

        foreach (string pageName in new[] { "HomePage", "ProfilesPage", "SettingsPage" })
        {
            XElement page = NamedElement(document, "ScrollViewer", pageName);
            Assert.Null(Attribute(page.Parent!, "Padding"));
            XElement content = Assert.Single(
                page.Elements(),
                element => element.Name.LocalName == "StackPanel");
            Assert.Equal("28,20,28,28", Attribute(content, "Padding"));
        }
    }

    [Fact]
    public void MainWindow_UsesSettingsRowsDurationOptionsAndFlyoutDetails()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement settings = NamedElement(document, "ScrollViewer", "SettingsPage");

        Assert.Equal(
            2,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsGroupStyle}"));
        Assert.Equal(
            3,
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

        Assert.Contains(settings.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "AutomationProperties.Name") == "Export diagnostics"
            && Attribute(element, "HorizontalAlignment") == "Right"
            && Attribute(element, "Width") == "40"
            && Attribute(element, "Height") == "32");

        Assert.Contains("ViewModel.ManualChangeDurationOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedManualChangeDurationOption", xaml, StringComparison.Ordinal);
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
                     "Manual change duration",
                     "Live readings refresh interval",
                     "App theme",
                     "Taskbar hover label",
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
            "Choose how often OpenBlade checks fan speed and net battery power.",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Values may stay the same between checks.",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(settings.Descendants(), element =>
            element.Name.LocalName == "ToggleSwitch"
            && Attribute(element, "AutomationProperties.Name") == "Live readings");
        string viewModel = ReadTrayCoreSource("MainViewModel.cs");
        Assert.Contains("Until the power source changes", viewModel, StringComparison.Ordinal);
        Assert.Contains("Until I reset them", viewModel, StringComparison.Ordinal);

        XElement startAtSignIn = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ToggleSwitch"
            && Attribute(element, "AutomationProperties.Name")
                == "Start OpenBlade when I sign in");
        Assert.Equal("OnStartAtSignInToggled", Attribute(startAtSignIn, "Toggled"));
        Assert.Contains("ViewModel.StartupDescription", xaml, StringComparison.Ordinal);

        Assert.Single(document.Descendants(), element => element.Name.LocalName == "Flyout");
        XElement warningIndicator = Assert.Single(document.Descendants(), element =>
            Attribute(element, "AutomationProperties.Name") == "Show warning details");
        Assert.Equal(
            "{StaticResource StatusPillButtonStyle}",
            Attribute(warningIndicator, "Style"));
        Assert.Equal(
            "{ThemeResource SystemFillColorCautionBackgroundBrush}",
            Attribute(warningIndicator, "Background"));
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
            "{StaticResource CardStyle}",
            Attribute(
                troubleshooting.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Style"));
        Assert.Equal(
            "0",
            Attribute(
                troubleshooting.Ancestors().First(element => element.Name.LocalName == "Border"),
                "Padding"));
        foreach (string property in new[]
                 {
                     "ViewModel.TroubleshootingServiceStatus",
                     "ViewModel.TroubleshootingLaptopStatus",
                     "ViewModel.TroubleshootingPowerSourceStatus",
                     "ViewModel.TroubleshootingControlsStatus",
                     "ViewModel.TroubleshootingOtherRazerSoftwareStatus",
                     "ViewModel.TroubleshootingDetectionStatus",
                     "ViewModel.TroubleshootingLastOperationStatus",
                 })
        {
            Assert.Contains(troubleshooting.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "IsTextSelectionEnabled") == "True"
                && Attribute(element, "Text")?.Contains(property, StringComparison.Ordinal) is true);
        }
        Assert.Contains(troubleshooting.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Other Razer software running");
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
    public void Dashboard_HeroReadingsAreAFullWidthThreeColumnFooter()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement readings = NamedElement(document, "Border", "HeroReadings");
        XElement hero = NamedElement(document, "Grid", "HeroLayout");

        Assert.Equal("1", Attribute(readings, "Grid.Row"));
        Assert.Null(Attribute(readings, "Grid.ColumnSpan"));
        Assert.DoesNotContain(
            hero.Elements(),
            element => element.Name.LocalName == "Grid.ColumnDefinitions");
        string? horizontalAlignment = Attribute(readings, "HorizontalAlignment");
        Assert.True(
            horizontalAlignment is null or "Stretch",
            $"Hero readings must use the native Stretch default or set it explicitly; found '{horizontalAlignment}'.");
        Assert.Equal(
            3,
            readings.Descendants().Count(element =>
                element.Name.LocalName == "ColumnDefinition"));
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
        foreach (string property in new[]
                 {
                     "ViewModel.BatteryPowerFlowLabel",
                     "ViewModel.Fan1RpmValue",
                     "ViewModel.Fan2RpmValue",
                 })
        {
            Assert.Contains(readings.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text")?.Contains(property, StringComparison.Ordinal) is true);
        }

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
        Assert.Equal("2,18,0,18", Attribute(header, "Margin"));
    }

    [Fact]
    public void MainWindow_UsesResponsiveProfileNamesAndNativeTypography()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);

        _ = NamedElement(document, "Grid", "ProfilesLayout");
        _ = NamedElement(document, "ColumnDefinition", "ProfilesSecondColumn");
        _ = NamedElement(document, "ColumnDefinition", "ProfilesThirdColumn");
        _ = NamedElement(document, "Border", "BatteryProfileCard");
        _ = NamedElement(document, "Border", "UsbCProfileCard");
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
            4,
            profileStateBadge.Descendants().Count(element =>
                element.Name.LocalName == "Ellipse"));
        foreach (string property in new[]
                 {
                     "ViewModel.IsReadinessReady",
                     "ViewModel.IsReadinessChecking",
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
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "Border"
            && Attribute(element, "Name") == "ProfileStateBadge");
        foreach (XElement activeBadgeText in document.Descendants().Where(element =>
                     element.Name.LocalName == "TextBlock"
                     && Attribute(element, "Text") == "Active"))
        {
            Assert.NotNull(activeBadgeText.Parent);
            Assert.Equal("Border", activeBadgeText.Parent!.Name.LocalName);
            Assert.Equal("Center", Attribute(activeBadgeText.Parent, "VerticalAlignment"));
        }
        Assert.DoesNotContain("ProfileChargeLimitEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsBatteryProfileActive", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsUsbCProfileActive", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AvailableUsbCPerformanceOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AvailableUsbCFanModes", xaml, StringComparison.Ordinal);

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
        int selectSettings = badgeHandlerSource.IndexOf(
            "MainNavigation.SelectedItem = SettingsNavigationItem",
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
        Assert.True(selectSettings >= 0);
        Assert.True(expandTroubleshooting > selectSettings);
        Assert.True(focusTroubleshooting > expandTroubleshooting);
        Assert.True(bringIntoView > focusTroubleshooting);
        Assert.Contains("DispatcherQueuePriority.Low", badgeHandlerSource, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignmentRatio = 0", badgeHandlerSource, StringComparison.Ordinal);

        AssertStyleBasedOn(document, "PageTitleStyle", "{StaticResource TitleTextBlockStyle}");
        AssertStyleBasedOn(document, "SectionHeaderStyle", "{StaticResource SubtitleTextBlockStyle}");
        AssertStyleBasedOn(document, "CardTitleStyle", "{StaticResource BodyStrongTextBlockStyle}");
        AssertStyleBasedOn(document, "SecondaryTextStyle", "{StaticResource BodyTextBlockStyle}");
        Assert.Contains("{StaticResource TitleLargeTextBlockStyle}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource AccentButtonStyle}", xaml, StringComparison.Ordinal);
        Assert.Equal(
            3,
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
    public async Task UnverifiedCapabilities_OmitEveryLightingChoice()
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
        Assert.False(viewModel.IsLightingVisible);
        Assert.True(viewModel.EnableFullChargeOverride);
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
        await WaitUntilAsync(() => viewModel.IsConnected);
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
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Later profiles");

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
        await WaitUntilAsync(() => viewModel.IsLightingVisible);

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
            new LightingSettings(LightingEffect.Static, new RgbColor(12, 34, 56), 64),
            viewModel.LightingDraft);
        Assert.True(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));

        viewModel.LightingEffectEditor = LightingEffect.Spectrum;
        Assert.False(viewModel.SetLightingCommand.CanExecute(viewModel.LightingDraft));
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
