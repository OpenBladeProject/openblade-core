using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Xml.Linq;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CapabilityPresentationTests
{
    private static readonly TimeSpan TestDeadlockGuard = TimeSpan.FromSeconds(5);
    private static readonly LightingEffect[] ExpectedStandardLightingEffectOrder =
    [
        LightingEffect.Off,
        LightingEffect.AmbientAwareness,
        LightingEffect.AudioMeter,
        LightingEffect.Breathing,
        LightingEffect.Fire,
        LightingEffect.Reactive,
        LightingEffect.Ripple,
        LightingEffect.Spectrum,
        LightingEffect.Starlight,
        LightingEffect.Static,
        LightingEffect.Tidal,
        LightingEffect.Wave,
        LightingEffect.Wheel,
    ];

    [Fact]
    public void MainWindow_HyperBoostUsesCapabilityGatedLaptopPerformanceChoice()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement selector = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "DashboardPerformanceSelector");
        XElement row = selector.Ancestors().First(element =>
            element.Name.LocalName == "Border");

        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "Name") == "HyperBoostCard"
            || Attribute(element, "Name") == "HyperBoostToggle");
        Assert.Equal(
            "{x:Bind AvailableLaptopPerformanceOptions, Mode=OneWay}",
            Attribute(selector, "ItemsSource"));
        Assert.Equal(
            "{Binding SelectedLaptopPerformanceOption, Mode=OneWay}",
            Attribute(selector, "SelectedItem"));
        Assert.Equal(
            "{x:Bind CanSelectLaptopPerformance, Mode=OneWay}",
            Attribute(selector, "IsEnabled"));
        XElement guidance = Assert.Single(row.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "{x:Bind HyperBoostDescription, Mode=OneWay}");
        Assert.Equal(
            "{Binding HyperBoostActive}",
            Attribute(guidance, "AnimatedVisibility.IsVisible"));
        XElement progress = Assert.Single(
            row.Descendants(),
            element => element.Name.LocalName == "ProgressRing");
        Assert.Equal(
            "{x:Bind IsLaptopPerformanceOperationInProgress, Mode=OneWay}",
            Attribute(progress, "IsActive"));
    }

    [Fact]
    public void MainWindow_HyperBoostSelectionRoutesThroughUnifiedOperation()
    {
        string window = ReadTraySource("MainWindow.xaml.cs");
        int handlerStart = window.IndexOf(
            "private async void OnDashboardPerformanceChanged(",
            StringComparison.Ordinal);
        int handlerEnd = window.IndexOf(
            "private void OnWindowsPowerModeChanged(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = window[handlerStart..handlerEnd];

        Assert.Contains("LaptopPerformanceOption selected", handler, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanSelectLaptopPerformance", handler, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.SetLaptopPerformanceOptionAsync(",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("selector.RestoreAuthoritativeSelection();", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("HyperBoostToggle", window, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHyperBoostToggled", window, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FirmwareAssistantIsReadOnlyAndModelScoped()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement card = NamedElement(document, "Border", "OfficialFirmwareUpdatesCard");
        string presentation = card.ToString();

        Assert.Equal(
            "{x:Bind FirmwareUpdateViewModel.IsSupported, Mode=OneWay}",
            Attribute(card, "Visibility"));
        Assert.Contains("FirmwareUpdateViewModel.CheckCommand", presentation);
        Assert.Contains("Check for updates", presentation);
        Assert.DoesNotContain("Check Razer", presentation);
        Assert.Contains("FirmwareUpdateViewModel.OfficialSupportUri", presentation);
        Assert.Contains("FirmwareUpdateViewModel.IntervalOptions", presentation);
        Assert.Contains("FirmwareUpdateViewModel.SelectedIntervalOption", presentation);
        Assert.DoesNotContain(
            "MCU versions use Razer's updater label format where it applies. OpenBlade never downloads or installs firmware.",
            presentation);
        Assert.DoesNotContain("Published updater", presentation);
        Assert.DoesNotContain(
            "OpenBlade does not download or install firmware.",
            presentation);
        XElement versionsSection = NamedElement(
            document,
            "Border",
            "FirmwareVersionsSection");
        Assert.Single(versionsSection.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Published by Razer");
        Assert.DoesNotContain(
            "Published only:",
            versionsSection.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(2, document.Descendants().Count(element =>
            Attribute(element, "Text") == "Checks at startup when due."));
        Assert.DoesNotContain(
            "Read-only check of Razer's model-specific support page",
            presentation);
        Assert.DoesNotContain(
            "After you choose an interval, OpenBlade checks GitHub at startup when that interval is due.",
            document.ToString());
        XElement progressRing = Assert.Single(
            card.Descendants(),
            element => element.Name.LocalName == "ProgressRing");
        Assert.Equal(
            "{StaticResource FirmwareProgressRingStyle}",
            Attribute(progressRing, "Style"));
        Assert.Equal(
            "{x:Bind FirmwareUpdateViewModel.IsChecking, Mode=OneWay}",
            Attribute(progressRing, "Visibility"));
        Assert.Equal(
            "Checking Razer for firmware versions",
            Attribute(progressRing, "AutomationProperties.Name"));
        Assert.DoesNotContain(card.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "Content") is string content
            && (content.Contains("Download", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Install", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MainWindow_FirmwareAssistantUsesResponsiveSettingsGroupLayout()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement card = NamedElement(document, "Border", "OfficialFirmwareUpdatesCard");
        Assert.Equal("{StaticResource SettingsGroupStyle}", Attribute(card, "Style"));
        Assert.Null(Attribute(card, "Padding"));

        string[] sectionNames =
        [
            "FirmwareHeaderSection",
            "FirmwareCheckIntervalSection",
            "FirmwareVersionsSection",
            "FirmwareFooterSection",
        ];
        XElement host = Assert.Single(card.Elements());
        Assert.Equal(
            sectionNames,
            host.Elements().Select(section => Attribute(section, "Name")));

        XElement footerSection = NamedElement(
            document,
            "Border",
            "FirmwareFooterSection");
        Assert.Equal(
            "{StaticResource SettingsLastRowStyle}",
            Attribute(footerSection, "Style"));
        Assert.Equal("0", Attribute(footerSection, "Padding"));
        XElement supportLink = Assert.Single(footerSection.Elements());
        Assert.Equal("HyperlinkButton", supportLink.Name.LocalName);
        Assert.Equal(
            "{StaticResource ExternalLinkButtonStyle}",
            Attribute(supportLink, "Style"));
        Assert.Equal("0,0,8,8", Attribute(supportLink, "CornerRadius"));
        Assert.Contains(supportLink.Descendants(), element =>
            Attribute(element, "Text") == "Open official Razer page");
        XElement assurance = Assert.Single(supportLink.Descendants(), element =>
            Attribute(element, "Text")
                == "View firmware downloads and release details on Razer's site.");
        Assert.Equal("Wrap", Attribute(assurance, "TextWrapping"));
        Assert.Contains(supportLink.Descendants(), element =>
            Attribute(element, "Style") == "{StaticResource CardActionIconStyle}");

        XElement versionsSection = NamedElement(
            document,
            "Border",
            "FirmwareVersionsSection");
        XElement versionsGrid = Assert.Single(versionsSection.Elements());
        Assert.Contains(versionsGrid.Elements(), element =>
            Attribute(element, "Text") == "Firmware");
        Assert.Null(Attribute(versionsGrid, "RowSpacing"));

        XElement[] valueGrids = versionsGrid.Elements()
            .Where(element => element.Name.LocalName == "Grid")
            .ToArray();
        Assert.Equal(7, valueGrids.Length);
        Assert.All(valueGrids, grid => Assert.Equal(
            2,
            grid.Elements()
                .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .Count()));

        Assert.DoesNotContain(
            versionsSection.Descendants()
                .Where(element => element.Name.LocalName == "ColumnDefinition"),
            column => double.TryParse(Attribute(column, "Width"), out _));

        XElement[] versionValues = versionsSection.Descendants()
            .Where(element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text") is string text
                && (text.Contains(
                        "FirmwareUpdateViewModel.Installed",
                        StringComparison.Ordinal)
                    || text.Contains(
                        "FirmwareUpdateViewModel.Published",
                        StringComparison.Ordinal)))
            .ToArray();
        Assert.Equal(12, versionValues.Length);
        Assert.All(versionValues, value =>
            Assert.Equal("Wrap", Attribute(value, "TextWrapping")));
        string[] rowVisibilityProperties =
        [
            "ShowsBiosFirmware",
            "ShowsEcFirmware",
            "ShowsMcuFirmware",
            "ShowsVbiosFirmware",
            "ShowsThunderboltFirmware",
            "ShowsTouchpadFirmware",
        ];
        Assert.All(rowVisibilityProperties, property => Assert.Equal(
            2,
            versionsSection.Descendants()
                .Count(element => Attribute(element, "Visibility")?.Contains(
                    property,
                    StringComparison.Ordinal) == true)));
        Assert.All(
            versionsSection.Descendants().Where(element =>
                Attribute(element, "Visibility")?.Contains(
                    "Shows",
                    StringComparison.Ordinal) == true),
            element => Assert.Equal("0,8,0,0", Attribute(element, "Margin")));
    }

    [Fact]
    public void MainWindow_PresentsVbiosOnlyInTheFirmwareSection()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        (string RowName, string PropertyName)[] rows =
        [
            ("DeviceInformationBiosFirmwareRow", "ShowsBiosInDeviceInformation"),
            ("DeviceInformationEcFirmwareRow", "ShowsEcInDeviceInformation"),
            ("DeviceInformationMcuFirmwareRow", "ShowsMcuInDeviceInformation"),
        ];

        Assert.All(rows, row =>
        {
            XElement element = NamedElement(document, "Border", row.RowName);
            Assert.Equal(
                $"{{x:Bind FirmwareUpdateViewModel.{row.PropertyName}, Mode=OneWay}}",
                Attribute(element, "Visibility"));
        });

        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "Name") == "DeviceInformationVbiosFirmwareRow");
        Assert.DoesNotContain(
            document.ToString(),
            "ShowsVbiosInDeviceInformation",
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.ToString(),
            "AboutVbiosVersionStatus",
            StringComparison.Ordinal);

        XElement versionsSection = NamedElement(
            document,
            "Border",
            "FirmwareVersionsSection");
        XElement vbiosLabel = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "VBIOS");
        Assert.Contains(vbiosLabel, versionsSection.Descendants());
        Assert.Equal("MainWindow_TextBlockVBIOS", Attribute(vbiosLabel, "Uid"));
        Assert.Equal("4", Attribute(vbiosLabel, "Grid.Row"));
        XElement vbiosValues = Assert.Single(versionsSection.Descendants(), element =>
            element.Name.LocalName == "Grid"
            && Attribute(element, "Grid.Row") == "4"
            && Attribute(element, "Grid.Column") == "1");
        Assert.Contains(
            "FirmwareUpdateViewModel.InstalledVbiosVersion",
            vbiosValues.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "FirmwareUpdateViewModel.PublishedVbiosVersion",
            vbiosValues.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SettingsRowsConfigureTheSharedPointerOverHighlight()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement style = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsRowStyle");
        Assert.Equal("Border", Attribute(style, "TargetType"));

        Assert.Contains(
            style.Descendants(),
            element => Attribute(element, "Property") == "Background"
                && Attribute(element, "Value") == "Transparent");
        Assert.Contains(
            style.Descendants(),
            element => Attribute(element, "Property")
                    == "local:SettingsCardPointerVisual.IsEnabled"
                && Attribute(element, "Value") == "True");
        Assert.Contains(
            style.Descendants(),
            element => Attribute(element, "Property")
                    == "local:SettingsCardPointerVisual.PointerOverBackground"
                && Attribute(element, "Value")
                    == "{ThemeResource SubtleFillColorSecondaryBrush}");

        XElement lastRowStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsLastRowStyle");
        Assert.Equal(
            "{StaticResource SettingsRowStyle}",
            Attribute(lastRowStyle, "BasedOn"));
    }

    [Fact]
    public void SettingsCardPointerVisual_ClearsInterruptedAndPreviousHighlights()
    {
        string behavior = ReadTraySource("SettingsCardPointerVisual.cs");

        Assert.Contains("UIElement.PointerCanceledEvent", behavior);
        Assert.Contains("UIElement.PointerCaptureLostEvent", behavior);
        Assert.Contains("UIElement.PointerWheelChangedEvent", behavior);
        Assert.Equal(3, behavior.Split("handledEventsToo: true").Length - 1);
        Assert.Equal(3, behavior.Split("border.RemoveHandler(").Length - 1);
        Assert.Contains("pointerOverBorder?.TryGetTarget", behavior);
        Assert.Contains("RestoreBackground(previous)", behavior);
    }

    [Fact]
    public void MainWindow_SettingsExpanderHeadersUseTheSharedHoverHighlight()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style"
            && Attribute(element, "Key") == "SettingsExpanderHeaderStyle");

        Assert.Equal("Border", Attribute(style, "TargetType"));
        Assert.Contains(style.Elements(), setter =>
            Attribute(setter, "Property")
                == "local:SettingsCardPointerVisual.IsEnabled"
            && Attribute(setter, "Value") == "True");
        Assert.Contains(style.Elements(), setter =>
            Attribute(setter, "Property")
                == "local:SettingsCardPointerVisual.PointerOverBackground"
            && Attribute(setter, "Value")
                == "{ThemeResource SubtleFillColorSecondaryBrush}");

        foreach (string name in new[]
                 {
                     "NavigationMenuExpander",
                     "TaskbarHoverLabelExpander",
                     "ThxAudioEnhancements",
                     "ThxAudioApplications",
                 })
        {
            XElement expander = NamedElement(document, "Expander", name);
            XElement header = Assert.Single(
                expander.Elements()
                    .Where(element => element.Name.LocalName == "Expander.Header")
                    .SelectMany(element => element.Elements()));
            Assert.Equal("Border", header.Name.LocalName);
            Assert.Equal(
                "{StaticResource SettingsExpanderHeaderStyle}",
                Attribute(header, "Style"));
        }
    }

    [Fact]
    public void MainWindow_ThxUsesRecoveryOnlyReadAndSharedStatusAndSettingCells()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement status = NamedElement(document, "Grid", "ThxAudioStatusArea");
        XElement measure = Assert.Single(status.Elements(), element =>
            element.Name.LocalName == "TextBlock" && Attribute(element, "Opacity") == "0");
        Assert.Equal("Raw", Attribute(measure, "AutomationProperties.AccessibilityView"));
        Assert.Null(Attribute(measure, "Visibility"));
        Assert.Single(status.Elements(), element => element.Name.LocalName == "TextBlock"
            && Attribute(element, "Visibility") == "{x:Bind ThxViewModel.ShowStatusMessage, Mode=OneWay}");
        Assert.Single(status.Elements(), element => element.Name.LocalName == "ProgressBar");
        Assert.DoesNotContain(status.Descendants(), element => element.Name.LocalName == "RowDefinition");
        XElement read = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Uid") == "MainWindow_ThxAudioReadSettings");
        Assert.Equal("{x:Bind ThxViewModel.IsReadSettingsVisible, Mode=OneWay}", Attribute(read, "Visibility"));
        foreach (string cell in new[] { "ThxNormalizationCell", "ThxVoiceClarityCell" })
        {
            XElement border = NamedElement(document, "Border", cell);
            Assert.Equal("{StaticResource SettingsNeutralSubcardStyle}", Attribute(border, "Style"));
            Assert.Single(border.Descendants(), element => element.Name.LocalName == "Slider");
            Assert.Single(border.Descendants(), element => element.Name.LocalName == "ToggleSwitch");
        }
        XElement expanderStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "CardContentExpanderStyle");
        Assert.Contains(expanderStyle.Elements(), setter =>
            Attribute(setter, "Property") == "Padding" && Attribute(setter, "Value") == "0");
        foreach (string cell in new[] { "ThxNormalizationCell", "ThxVoiceClarityCell",
            "ThxAudioRoutingCell", "ThxAudioProfileActionsCell" })
        {
            XElement border = NamedElement(document, "Border", cell);
            Assert.Equal("{StaticResource DashboardSingleLineRowPadding}", Attribute(border, "Padding"));
            XElement content = Assert.Single(border.Elements());
            Assert.Equal("{StaticResource DashboardSingleLineRowContentMinHeight}", Attribute(content, "MinHeight"));
            Assert.Null(Attribute(border, "Height"));
        }
        XElement applications = NamedElement(document, "Expander", "ThxAudioApplications");
        Assert.DoesNotContain(applications.Descendants(), element =>
            Attribute(element, "Style")?.Contains("EmphasizedSubcard", StringComparison.Ordinal) == true);
        Assert.Single(applications.Descendants(), element => element.Name.LocalName == "ToggleSwitch");
    }

    [Fact]
    public void MainWindow_ShowsUnsavedNavigationBadgesAndStretchesFanCurveSliders()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        string[] expectedBadgeBindings =
        [
            "{x:Bind ViewModel.HasDashboardProfileDraft, Mode=OneWay}",
            "{x:Bind ViewModel.HasUnsavedProfileChanges, Mode=OneWay}",
            "{x:Bind ViewModel.HasLaptopPageProfileSaveOpportunity, Mode=OneWay}",
            "{x:Bind ViewModel.HasLightingProfileSaveOpportunity, Mode=OneWay}",
            "{x:Bind DisplayViewModel.CanSaveCurrentSettingsToProfile, Mode=OneWay}",
        ];
        XElement navigation = NamedElement(document, "NavigationView", "MainNavigation");
        XElement[] badges = navigation.Descendants()
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

        Assert.Equal(28, animated.Length);
        Assert.All(animated, element => Assert.Equal(
            "Collapsed",
            Attribute(element, "Visibility")));
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsCustomPerformanceVisible",
                StringComparison.Ordinal) is true);
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsGpuAppCleanupVisible",
                StringComparison.Ordinal) is true);
        Assert.Equal(
            1,
            animated.Count(element =>
                Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                    "IsGpuClockTuningVisible",
                    StringComparison.Ordinal) is true));
        Assert.Equal(
            2,
            animated.Count(element =>
                Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                    "IsGpuClockTuningEnabled",
                    StringComparison.Ordinal) is true));
        XElement gpuClockTuningToggle = document.Descendants()
            .Single(element => Attribute(element, "Name") == "GpuClockTuningToggle");
        Assert.Equal(
            "{x:Bind IsGpuClockTuningEnabled, Mode=OneWay}",
            Attribute(gpuClockTuningToggle, "IsOn"));
        Assert.Equal(
            "{x:Bind CanToggleGpuClockTuning, Mode=OneWay}",
            Attribute(gpuClockTuningToggle, "IsEnabled"));
        Assert.Equal(
            "OnGpuClockTuningToggled",
            Attribute(gpuClockTuningToggle, "Toggled"));
        Assert.Equal(
            "{Binding IsGpuClockTuningVisible}",
            Attribute(
                gpuClockTuningToggle.Ancestors()
                    .First(element => element.Name.LocalName == "Border"),
                "AnimatedVisibility.IsVisible"));
        XElement customCpuSelector = document.Descendants()
            .Single(element => Attribute(element, "Name") == "DashboardCustomCpuSelector");
        Assert.Equal(
            "{Binding IsCustomPerformanceVisible}",
            Attribute(
                customCpuSelector.Ancestors()
                    .First(element => element.Name.LocalName == "Border"),
                "AnimatedVisibility.IsVisible"));
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsFixedFanSpeedVisible",
                StringComparison.Ordinal) is true);
        Assert.Contains(animated, element =>
            Attribute(element, "AnimatedVisibility.IsVisible")?.Contains(
                "IsManualFanCurveVisible",
                StringComparison.Ordinal) is true);
        XElement[] gamingModePolicies = animated
            .Where(element => element.Descendants().Any(descendant =>
                Attribute(descendant, "AutomationProperties.Name") is
                    "Block the Windows key in Gaming mode"
                    or "Block Alt+Tab in Gaming mode"))
            .ToArray();
        Assert.Equal(2, gamingModePolicies.Length);
        Assert.All(gamingModePolicies, element => Assert.Equal(
            "{x:Bind GamingModeToggleIsOn, Mode=OneWay}",
            Attribute(element, "AnimatedVisibility.IsVisible")));

        XElement export = Assert.Single(document.Descendants(), element =>
            Attribute(element, "AutomationProperties.Name") == "Export profiles to JSON");
        Assert.Equal("OnExportProfiles", Attribute(export, "Click"));
        Assert.Equal("Export profiles", Attribute(export, "Content"));
    }

    [Fact]
    public void MainWindow_DashboardRowsCompactOnlySingleLineContent()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        string[] sharedStyleKeys =
        [
            "SettingsRowStyle",
            "SettingsSubcardBaseStyle",
        ];

        foreach (string styleKey in sharedStyleKeys)
        {
            XElement style = Assert.Single(document.Descendants(), element =>
                Attribute(element, "Key") == styleKey);
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "Padding"
                && Attribute(setter, "Value") == "16,14");
            Assert.DoesNotContain(style.Elements(), setter =>
                Attribute(setter, "Property") is "Height" or "MinHeight");
        }

        XElement compactContentMinHeight = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "DashboardSingleLineRowContentMinHeight");
        XElement compactPadding = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "DashboardSingleLineRowPadding");
        XElement wrappedPadding = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "DashboardWrappedTitleRowPadding");
        Assert.Equal("Double", compactContentMinHeight.Name.LocalName);
        Assert.Equal("40", compactContentMinHeight.Value);
        Assert.Equal("Thickness", compactPadding.Name.LocalName);
        Assert.Equal("16,4", compactPadding.Value);
        Assert.Equal("Thickness", wrappedPadding.Name.LocalName);
        Assert.Equal("16,14", wrappedPadding.Value);

        const string compactContentMinHeightReference =
            "{StaticResource DashboardSingleLineRowContentMinHeight}";
        const string compactPaddingReference =
            "{StaticResource DashboardSingleLineRowPadding}";
        XElement[] compactRows = document.Descendants()
            .Where(element =>
                element.Name.LocalName == "Border"
                && Attribute(element, "Padding") == compactPaddingReference
                && element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"))
            .ToArray();

        Assert.Equal(16, compactRows.Length);
        Assert.All(compactRows, row =>
        {
            Assert.Null(Attribute(row, "Height"));
            Assert.Null(Attribute(row, "MinHeight"));
            XElement content = Assert.Single(row.Elements());
            if (content.Name.LocalName == "StackPanel")
            {
                XElement[] children = content.Elements().ToArray();
                Assert.Equal(2, children.Length);
                Assert.Equal("InfoBar", children[1].Name.LocalName);
                Assert.Equal("HyperBoostUnavailableInfoBar", Attribute(children[1], "Name"));
                Assert.Equal("{x:Bind HasHyperBoostUnavailableReason, Mode=OneWay}",
                    Attribute(children[1], "Visibility"));
                content = children[0];
            }
            Assert.Equal("Grid", content.Name.LocalName);
            Assert.Equal(
                compactContentMinHeightReference,
                Attribute(content, "MinHeight"));
            XElement title = content.Descendants().First(element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Style") == "{StaticResource CardTitleStyle}");
            if (Attribute(title, "Uid") == "MainWindow_TextBlockLaptopPerformance")
            {
                Assert.NotNull(title.Parent);
                Assert.Equal(
                    "OnDashboardTextStackSizeChanged",
                    Attribute(title.Parent, "SizeChanged"));
            }
            else
            {
                Assert.Equal(
                    "OnDashboardSingleLineTitleSizeChanged",
                    Attribute(title, "SizeChanged"));
            }
        });
        Assert.Equal(
            [
                "Laptop performance",
                "GPU core clock offset",
                "GPU memory clock offset",
                "CPU",
                "GPU",
                "Maximum fans",
                "Fans",
                "Fan speed",
                "Battery protection",
                "Charge limit",
                "Temporary full charge",
                "Function key behavior",
                "Gaming mode",
                "Block the Windows key",
                "Block Alt+Tab",
                "Startup animation",
            ],
            compactRows.Select(row => row.Descendants().First(element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Style") == "{StaticResource CardTitleStyle}")
                .Attribute("Text")?.Value));

        XElement[] animatedCompactRows = compactRows
            .Where(row => Attribute(row, "AnimatedVisibility.IsVisible") is not null)
            .ToArray();
        Assert.Equal(10, animatedCompactRows.Length);
        Assert.All(animatedCompactRows, row =>
        {
            Assert.Null(Attribute(row, "Height"));
            Assert.Null(Attribute(row, "MinHeight"));
        });

        (string elementName, string controlName, string expectedStyle)[] describedControls =
        [
            (
                "ResponsiveChoiceSelector",
                "LightingEffectPicker",
                "{StaticResource SettingsLastRowStyle}"),
            (
                "Slider",
                "DashboardLightingBrightnessSlider",
                "{StaticResource SettingsRowStyle}"),
        ];
        foreach ((string elementName, string controlName, string expectedStyle) in describedControls)
        {
            XElement control = NamedElement(document, elementName, controlName);
            XElement row = control.Ancestors()
                .First(element => element.Name.LocalName == "Border");
            Assert.Equal(expectedStyle, Attribute(row, "Style"));
            Assert.Null(Attribute(row, "MinHeight"));
            Assert.Null(Attribute(row, "Padding"));
            Assert.Contains(row.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Style") == "{StaticResource SecondaryTextStyle}");
        }

        XElement sliderStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "InlineCardSliderStyle");
        Assert.DoesNotContain(sliderStyle.Elements(), setter =>
            Attribute(setter, "Property") is "Height" or "MinHeight");
    }

    [Fact]
    public void MainWindow_FoldsGpuOffsetsUnderANeutralTuningRow()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement toggle = NamedElement(document, "ToggleSwitch", "GpuClockTuningToggle");
        XElement toggleRow = toggle.Ancestors()
            .First(element => element.Name.LocalName == "Border");
        XElement coreSlider = NamedElement(document, "Slider", "GpuCoreClockOffsetSlider");
        XElement memorySlider = NamedElement(document, "Slider", "GpuMemoryClockOffsetSlider");
        XElement coreRow = coreSlider.Ancestors()
            .First(element => element.Name.LocalName == "Border");
        XElement memoryRow = memorySlider.Ancestors()
            .First(element => element.Name.LocalName == "Border");

        Assert.Equal(
            "{StaticResource SettingsNeutralSubcardStyle}",
            Attribute(toggleRow, "Style"));
        Assert.Equal(
            "{StaticResource CompactCardToggleSwitchStyle}",
            Attribute(toggle, "Style"));
        Assert.Equal(
            [coreRow, memoryRow],
            toggleRow.ElementsAfterSelf()
                .Where(element => element.Name.LocalName == "Border")
                .Take(2));
        Assert.All([coreRow, memoryRow], row =>
        {
            Assert.Equal(
                "{StaticResource SettingsNeutralSubcardStyle}",
                Attribute(row, "Style"));
            Assert.Equal(
                "{Binding IsGpuClockTuningEnabled}",
                Attribute(row, "AnimatedVisibility.IsVisible"));
        });

        XElement neutralStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsNeutralSubcardStyle");
        Assert.Equal(
            "{StaticResource SettingsSubcardBaseStyle}",
            Attribute(neutralStyle, "BasedOn"));
        Assert.Contains(neutralStyle.Elements(), setter =>
            Attribute(setter, "Property") == "Background"
            && Attribute(setter, "Value") == "Transparent");
        Assert.Contains(neutralStyle.Elements(), setter =>
            Attribute(setter, "Property")
                == "local:SettingsCardPointerVisual.IsEnabled"
            && Attribute(setter, "Value") == "True");
        Assert.Contains(neutralStyle.Elements(), setter =>
            Attribute(setter, "Property")
                == "local:SettingsCardPointerVisual.PointerOverBackground"
            && Attribute(setter, "Value")
                == "{ThemeResource SubtleFillColorSecondaryBrush}");
    }

    [Fact]
    public void MainWindow_FanEditorSubcardsUseHoverFreeBackground()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        string[] visibilityBindings =
        [
            "{Binding IsFixedFanSpeedVisible}",
            "{Binding IsManualFanCurveVisible}",
        ];

        XElement[] fanEditorRows = document.Descendants()
            .Where(element => visibilityBindings.Contains(
                Attribute(element, "AnimatedVisibility.IsVisible"),
                StringComparer.Ordinal))
            .ToArray();

        Assert.Equal(2, fanEditorRows.Length);
        Assert.All(fanEditorRows, row =>
        {
            Assert.Equal(
                "{StaticResource SettingsLastEditorSubcardStyle}",
                Attribute(row, "Style"));
        });

        XElement editorStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsEditorSubcardStyle");
        Assert.Equal(
            "{StaticResource SettingsSubcardBaseStyle}",
            Attribute(editorStyle, "BasedOn"));
        Assert.Contains(editorStyle.Elements(), setter =>
            Attribute(setter, "Property") == "Background"
            && Attribute(setter, "Value") == "Transparent");
        Assert.DoesNotContain(editorStyle.Elements(), setter =>
            Attribute(setter, "Property")?.StartsWith(
                "local:SettingsCardPointerVisual.",
                StringComparison.Ordinal) is true);

        XElement lastEditorStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsLastEditorSubcardStyle");
        Assert.Equal(
            "{StaticResource SettingsEditorSubcardStyle}",
            Attribute(lastEditorStyle, "BasedOn"));
        Assert.Contains(lastEditorStyle.Elements(), setter =>
            Attribute(setter, "Property") == "CornerRadius"
            && Attribute(setter, "Value") == "0,0,8,8");
    }

    [Fact]
    public void MainWindow_DashboardSliderCardsKeepValuesInline()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement sliderStyle = Assert.Single(document.Descendants(), element =>
            Attribute(element, "Key") == "InlineCardSliderStyle");
        Assert.Equal(
            "{StaticResource DefaultSliderStyle}",
            Attribute(sliderStyle, "BasedOn"));
        string[] sliderNames =
        [
            "GpuCoreClockOffsetSlider",
            "GpuMemoryClockOffsetSlider",
            "FixedFanSpeedSlider",
            "DashboardChargeLimitSlider",
            "DashboardLightingBrightnessSlider",
            "DashboardAudioMeterColorBoostSlider",
            "DashboardAudioMeterDecaySlider",
        ];

        foreach (string name in sliderNames)
        {
            XElement slider = NamedElement(document, "Slider", name);
            XElement row = Assert.IsType<XElement>(slider.Parent);

            Assert.Equal("Grid", row.Name.LocalName);
            Assert.Equal("12", Attribute(row, "ColumnSpacing"));
            Assert.Equal(
                "{StaticResource InlineCardSliderStyle}",
                Attribute(slider, "Style"));
            Assert.Contains(row.Elements(), element =>
                Attribute(element, "Grid.Column") == "1"
                && element.DescendantsAndSelf().Any(descendant =>
                    descendant.Name.LocalName == "TextBlock"));
        }

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

        XElement performance = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "DashboardPerformanceSelector");
        XElement windowsPowerMode = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "WindowsPowerModeSelector");
        XElement fans = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "DashboardFanModeSelector");
        XElement chargeLimit = NamedElement(document, "Slider", "DashboardChargeLimitSlider");
        XElement batteryProtection = NamedElement(document, "ToggleSwitch", "BatteryProtectionToggle");
        XElement temporaryFullCharge = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "Content") == "{x:Bind FullChargeActionLabel, Mode=OneWay}");
        Assert.Equal("OnDashboardPerformanceChanged", Attribute(performance, "SelectionChanged"));
        Assert.Equal("Laptop performance", Attribute(performance, "AccessibleName"));
        Assert.Equal("OnWindowsPowerModeChanged", Attribute(windowsPowerMode, "SelectionChanged"));
        Assert.Equal(
            "OnWindowsPowerModeSelectorLoaded",
            Attribute(windowsPowerMode, "Loaded"));
        Assert.Equal(
            "{x:Bind AvailableWindowsPowerModeOptions, Mode=OneWay}",
            Attribute(windowsPowerMode, "ItemsSource"));
        Assert.Equal("Label", Attribute(windowsPowerMode, "DisplayMemberPath"));
        XElement windowsPowerModeDescription = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "{x:Bind SelectedWindowsPowerModeDescription, Mode=OneWay}");
        Assert.Equal("Left", Attribute(windowsPowerModeDescription, "HorizontalAlignment"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Laptop performance");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Windows power mode");
        Assert.Contains(
            "SelectedWindowsPowerModeDescription",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Energy Saver is separate", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "CPU power limit");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "CPU voltage optimization");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "GPU clock tuning");
        Assert.DoesNotContain("Not available in OpenBlade yet", xaml, StringComparison.Ordinal);
        foreach (string redundantDescription in new[]
        {
            "Balance speed, power use, and noise.",
            "Automatic is recommended for everyday use.",
            "Limit charging to help preserve battery health.",
            "Choose the maximum charge level while protection is on.",
            "Charge to 100%, then restore the limit after unplugging.",
            "Set whether F1 through F12 or media actions work without holding Fn.",
            "Turn Gaming mode on or off.",
            "Prevent the Start menu from opening while Gaming mode is on.",
            "Prevent switching to another app while Gaming mode is on.",
            "Set whether the keyboard animation plays when the laptop starts.",
        })
        {
            Assert.DoesNotContain(redundantDescription, xaml, StringComparison.Ordinal);
        }
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Performance");
        Assert.Equal(
            "{x:Bind AvailableLaptopPerformanceOptions, Mode=OneWay}",
            Attribute(performance, "ItemsSource"));
        Assert.Equal(
            "{Binding SelectedLaptopPerformanceOption, Mode=OneWay}",
            Attribute(performance, "SelectedItem"));
        Assert.Equal(
            "{x:Bind CanSelectLaptopPerformance, Mode=OneWay}",
            Attribute(performance, "IsEnabled"));
        Assert.All(
            new[]
            {
                NamedElement(document, "ResponsiveChoiceSelector", "DashboardCustomCpuSelector"),
                NamedElement(document, "ResponsiveChoiceSelector", "DashboardCustomGpuSelector"),
            },
            selector => Assert.Equal(
                "{x:Bind CanEditPerformance, Mode=OneWay}",
                Attribute(selector, "IsEnabled")));
        Assert.Equal(
            "{Binding IsPerformanceVisible}",
            Attribute(
                performance.Ancestors().First(element =>
                    element.Name.LocalName == "Border"
                    && Attribute(element, "AnimatedVisibility.IsVisible") is not null),
                "AnimatedVisibility.IsVisible"));
        Assert.Equal("OnDashboardFanModeSelectionChanged", Attribute(fans, "SelectionChanged"));
        Assert.Equal(
            "{x:Bind AvailableActiveFanModeOptions, Mode=OneWay}",
            Attribute(fans, "ItemsSource"));
        Assert.Equal(
            "{Binding SelectedFanModeOption, Mode=OneWay}",
            Attribute(fans, "SelectedItem"));
        Assert.Equal("{x:Bind CanChangeFanMode, Mode=OneWay}", Attribute(fans, "IsEnabled"));
        Assert.Equal(
            "{Binding IsFanControlVisible}",
            Attribute(
                fans.Ancestors().First(element =>
                    element.Name.LocalName == "Border"
                    && Attribute(element, "AnimatedVisibility.IsVisible") is not null),
                "AnimatedVisibility.IsVisible"));
        Assert.Equal(
            "{x:Bind CanChangeBatteryProtection, Mode=OneWay}",
            Attribute(batteryProtection, "IsEnabled"));
        Assert.Equal(
            "{x:Bind BatteryProtectionEnabled, Mode=OneWay}",
            Attribute(batteryProtection, "IsOn"));
        Assert.Equal("{x:Bind CanAdjustChargeLimit, Mode=OneWay}", Attribute(chargeLimit, "IsEnabled"));
        Assert.Equal(
            "OnDashboardChargeLimitPointerPressed",
            Attribute(chargeLimit, "PointerPressed"));
        Assert.Equal(
            "OnDashboardChargeLimitPointerCaptureLost",
            Attribute(chargeLimit, "PointerCaptureLost"));
        Assert.Equal("OnDashboardChargeLimitKeyUp", Attribute(chargeLimit, "KeyUp"));
        Assert.Null(Attribute(chargeLimit, "ValueChanged"));
        Assert.Equal("OnBatteryProtectionToggled", Attribute(batteryProtection, "Toggled"));
        Assert.Equal(
            "{x:Bind ChargeLimitMinimumPercent, Mode=OneWay}",
            Attribute(chargeLimit, "Minimum"));
        Assert.Equal(
            "{x:Bind ChargeLimitMaximumPercent, Mode=OneWay}",
            Attribute(chargeLimit, "Maximum"));
        Assert.Equal(
            "{x:Bind ChargeLimitStepPercent, Mode=OneWay}",
            Attribute(chargeLimit, "StepFrequency"));
        Assert.Equal(
            "{x:Bind ChargeLimitStepPercent, Mode=OneWay}",
            Attribute(chargeLimit, "SmallChange"));
        Assert.Equal("StepValues", Attribute(chargeLimit, "SnapsTo"));
        Assert.Equal(
            "{StaticResource InlineCardSliderStyle}",
            Attribute(chargeLimit, "Style"));
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
            NamedElement(document, "ScrollViewer", "HomePage")
                .Descendants()
                .Where(element => element.Name.LocalName == "Button"),
            button =>
            Attribute(button, "Content")?.StartsWith("Apply", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(xaml, "ViewModel.SetPerformanceCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetFanModeCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.SetChargeLimitCommand", StringComparison.Ordinal);
        Assert.DoesNotContain(xaml, "ViewModel.KeepManualChangesUntilCleared", StringComparison.Ordinal);
        Assert.True(
            document.Descendants().Count(element => element.Name.LocalName == "VisualState") >= 6);
        Assert.Equal(
            0,
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
        XElement home = NamedElement(document, "ScrollViewer", "HomePage");
        XElement batteryTemplate = NamedTemplate(document, "DashboardBatteryCardTemplate");
        XElement fansTemplate = NamedTemplate(document, "DashboardFansCardTemplate");
        _ = Assert.Single(
            batteryTemplate.Descendants(),
            element => element.Name.LocalName == "Slider"
                && Attribute(element, "Name") == "DashboardChargeLimitSlider");
        Assert.Contains(fansTemplate.Descendants(), element =>
            element.Name.LocalName == "Slider"
            && Attribute(element, "Name") == "FixedFanSpeedSlider"
            && Attribute(element, "Minimum") == "{x:Bind FixedFanSpeedMinimumRpm, Mode=OneWay}"
            && Attribute(element, "Maximum") == "{x:Bind FixedFanSpeedMaximumRpm, Mode=OneWay}"
            && Attribute(element, "StepFrequency") == "{x:Bind FixedFanSpeedStepRpm, Mode=OneWay}");
        Assert.DoesNotContain("\u00c2", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOptionPicker_HasAStableSelectionAnchor()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement[] optionPickers = document.Descendants()
            .Where(element =>
                element.Name.LocalName == "ComboBox"
                && Attribute(element, "ItemsSource") is not null)
            .ToArray();

        Assert.NotEmpty(optionPickers);
        Assert.All(optionPickers, picker =>
        {
            Assert.NotNull(Attribute(picker, "SelectedItem"));
            Assert.NotNull(Attribute(picker, "StableComboBoxSelection.Anchor"));
        });
    }

    [Fact]
    public void ResponsiveChoiceSelector_DeclaresNativeWideAndCompactPresentations()
    {
        XDocument mainWindow = XDocument.Parse(ReadMainWindowXaml());
        XDocument selectorDocument = XDocument.Parse(
            ReadTraySource("ResponsiveChoiceSelector.xaml"));
        string selectorSource = ReadTraySource("ResponsiveChoiceSelector.xaml.cs");
        XElement expanded = NamedElement(
            selectorDocument,
            "SelectorBar",
            "ExpandedSelector");
        XElement compact = NamedElement(
            selectorDocument,
            "ComboBox",
            "CompactSelector");

        Assert.Equal("Collapsed", Attribute(expanded, "Visibility"));
        Assert.Equal(
            "OnExpandedSelectionChanged",
            Attribute(expanded, "SelectionChanged"));
        Assert.Equal(
            "OnExpandedSelectorSizeChanged",
            Attribute(expanded, "SizeChanged"));
        Assert.Equal(
            "OnCompactSelectionChanged",
            Attribute(compact, "SelectionChanged"));
        Assert.Equal("OnCompactDropDownOpened", Attribute(compact, "DropDownOpened"));
        Assert.Equal("OnCompactDropDownClosed", Attribute(compact, "DropDownClosed"));
        Assert.Null(Attribute(expanded, "Style"));
        Assert.Null(Attribute(compact, "Style"));
        Assert.DoesNotContain(
            selectorDocument.Descendants(),
            element => element.Name.LocalName is "ControlTemplate" or "ToggleButton");
        Assert.DoesNotContain("MaxExpandedItems", selectorSource, StringComparison.Ordinal);
        Assert.Contains(
            "availableWidth >= minimumHostWidth",
            selectorSource,
            StringComparison.Ordinal);

        XElement[] usages = mainWindow.Descendants()
            .Where(element => element.Name.LocalName == "ResponsiveChoiceSelector")
            .ToArray();
        Assert.Equal(28, usages.Length);
        Assert.All(usages, usage =>
        {
            Assert.NotNull(Attribute(usage, "ItemsSource"));
            Assert.NotNull(Attribute(usage, "SelectedItem"));
            Assert.NotNull(Attribute(usage, "AccessibleName"));
            Assert.NotNull(Attribute(usage, "ExpandedMinWindowWidth"));
            Assert.Null(Attribute(usage, "SelectedIndex"));
        });

        // Selectors whose handler rejects a pick must keep a runtime OneWay
        // {Binding} on SelectedItem: ResponsiveChoiceSelector.PublishSelection
        // leaves the property untouched only for that binding kind, which is
        // what RestoreAuthoritativeSelection needs to put the authoritative
        // value back. A compiled binding takes the direct-write path instead,
        // and the rejected pick would stay on screen.
        XElement[] runtimeBoundUsages = usages
            .Where(usage => Attribute(usage, "SelectedItem")?.StartsWith(
                "{Binding ",
                StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, runtimeBoundUsages.Length);
        XElement[] immediateUsages = new[]
            {
                "DashboardPerformanceSelector",
                "WindowsPowerModeSelector",
                "DashboardCustomCpuSelector",
                "DashboardCustomGpuSelector",
                "DashboardFanModeSelector",
            }
            .Select(name => NamedElement(mainWindow, "ResponsiveChoiceSelector", name))
            .ToArray();
        Assert.Equal(
            runtimeBoundUsages.OrderBy(usage => Attribute(usage, "Name"), StringComparer.Ordinal),
            immediateUsages.OrderBy(usage => Attribute(usage, "Name"), StringComparer.Ordinal));
        Assert.All(immediateUsages, usage =>
        {
            Assert.StartsWith(
                "{Binding ",
                Assert.IsType<string>(Attribute(usage, "SelectedItem")),
                StringComparison.Ordinal);
            string selectedItemBinding = Assert.IsType<string>(
                Attribute(usage, "SelectedItem"));
            Assert.Contains(
                "Mode=OneWay",
                selectedItemBinding,
                StringComparison.Ordinal);
            Assert.NotNull(Attribute(usage, "SelectionChanged"));
        });

        foreach (string name in new[]
                 {
                     "DashboardPerformanceSelector",
                     "WindowsPowerModeSelector",
                     "FunctionKeyPrimarySelector",
                     "LightingEffectPicker",
                     "DisplayRefreshRatePicker",
                     "DisplayColorProfilePicker",
                     "LaptopLogoModeSelector",
                 })
        {
            XElement usage = NamedElement(
                mainWindow,
                "ResponsiveChoiceSelector",
                name);
            Assert.Equal("Label", Attribute(usage, "DisplayMemberPath"));
        }
    }

    [Fact]
    public void MainWindow_WindowsPowerModeSelectionFollowsViewModelNotifications()
    {
        string source = ReadTraySource("MainWindow.xaml.cs");

        Assert.Contains(
            "args.PropertyName == nameof(MainViewModel.SelectedWindowsPowerModeOption)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "selector.RestoreAuthoritativeSelection();",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "selector.SelectedItem = ViewModel.SelectedWindowsPowerModeOption;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_RuntimeBoundResponsiveChoicesDoNotReplaceBindings()
    {
        string source = ReadTraySource("MainWindow.xaml.cs");
        string[] forbiddenAssignments =
        [
            "selector.SelectedItem = ViewModel.SelectedPerformanceOption;",
            "selector.SelectedItem = ViewModel.SelectedLaptopPerformanceOption;",
            "selector.SelectedItem = ViewModel.SelectedWindowsPowerModeOption;",
            "selector.SelectedItem = selector.Name == \"DashboardCustomCpuSelector\"",
            "selector.SelectedItem = ViewModel.SelectedFanModeOption;",
        ];

        Assert.All(forbiddenAssignments, assignment =>
            Assert.DoesNotContain(assignment, source, StringComparison.Ordinal));
        Assert.Contains(
            "selector.RestoreAuthoritativeSelection();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ExtendsResponsiveChoicesToRemainingSmallOptionPickers()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        string[] expected =
        [
            "Custom CPU level",
            "Custom GPU level",
            "Fan mode",
            "Ambient Awareness screen region",
            "Breathing color mode",
            "Starlight color mode",
            "Lighting direction",
            "Wheel rotation direction",
            "Reactive lighting duration",
            "Starlight lighting duration",
            "Laptop logo lighting",
            "Display color profile",
            "Live readings refresh interval",
            "App theme",
            "App language",
            "App accent color",
            "Update channel",
            "Update check interval",
            "Firmware update check interval",
        ];

        foreach (string accessibleName in expected)
        {
            XElement selector = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "ResponsiveChoiceSelector"
                && Attribute(element, "AccessibleName") == accessibleName);
            Assert.Null(Attribute(selector, "MaxExpandedItems"));
        }

        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "MaxExpandedItems") is not null);

        XElement[] retainedDropdowns = document.Descendants()
            .Where(element => element.Name.LocalName == "ComboBox")
            .ToArray();
        Assert.Equal(2, retainedDropdowns.Length);
        Assert.Contains(retainedDropdowns, element =>
            Attribute(element, "AutomationProperties.Name") == "Profile to review");
        Assert.Contains(retainedDropdowns, element =>
            Attribute(element, "SelectionChanged") == "OnSpecialKeyActionSelectionChanged");
    }

    [Fact]
    public void MainWindow_UsesAutoHidingScrollViewersWithEdgeAlignedPadding()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());

        foreach (string pageName in new[]
                 {
                     "HomePage",
                     "ProfilesPage",
                     "LaptopPage",
                     "ThxAudioPage",
                     "LightingPage",
                     "DisplayPage",
                     "SettingsPage",
                     "AboutPage",
                 })
        {
            XElement page = NamedElement(document, "ScrollViewer", pageName);
            Assert.Null(Attribute(page.Parent!, "Padding"));
            Assert.Equal("Disabled", Attribute(page, "HorizontalScrollMode"));
            Assert.Equal("Hidden", Attribute(page, "HorizontalScrollBarVisibility"));
            Assert.Equal("Enabled", Attribute(page, "VerticalScrollMode"));
            Assert.Equal("Auto", Attribute(page, "VerticalScrollBarVisibility"));
            XElement content = Assert.Single(
                page.Elements(),
                element => element.Name.LocalName == "StackPanel");
            Assert.Equal("28,20,28,28", Attribute(content, "Padding"));
            Assert.Equal(
                $"{{Binding ElementName={pageName}, Path=ViewportWidth, Mode=OneWay}}",
                Attribute(content, "Width"));
        }
    }

    [Fact]
    public void MainWindow_DisplayPageUsesSettingsRowsAndHdrGatesColorProfiles()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement display = NamedElement(document, "ScrollViewer", "DisplayPage");
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

        XElement refresh = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "DisplayRefreshRatePicker");
        Assert.Equal(
            "{x:Bind RefreshRateOptions, Mode=OneWay}",
            Attribute(refresh, "ItemsSource"));
        Assert.Equal(
            "{x:Bind CanChangeRefreshRate, Mode=OneWay}",
            Attribute(refresh, "IsEnabled"));
        Assert.Equal("OnDisplayRefreshRateChanged", Attribute(refresh, "SelectionChanged"));

        XElement hdr = NamedElement(document, "ToggleSwitch", "DisplayHdrToggle");
        Assert.Equal(
            "{StaticResource CompactCardToggleSwitchStyle}",
            Attribute(hdr, "Style"));
        Assert.Equal("OnDisplayHdrToggled", Attribute(hdr, "Toggled"));

        XElement color = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "DisplayColorProfilePicker");
        Assert.Equal(
            "{x:Bind CanChangeColorProfile, Mode=OneWay}",
            Attribute(color, "IsEnabled"));
        Assert.Equal("OnDisplayPickerOpened", Attribute(color, "DropDownOpened"));
        Assert.Equal("OnDisplayPickerClosed", Attribute(color, "DropDownClosed"));
        Assert.Equal("OnDisplayColorProfileChanged", Attribute(color, "SelectionChanged"));
    }

    [Fact]
    public void MainWindow_UsesSettingsRowsDurationOptionsAndFlyoutDetails()
    {
        string xaml = ReadMainWindowXaml();
        XDocument document = XDocument.Parse(xaml);
        XElement settings = NamedElement(document, "ScrollViewer", "SettingsPage");
        XElement about = NamedElement(document, "ScrollViewer", "AboutPage");

        Assert.Equal(
            5,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsGroupStyle}"));
        Assert.Equal(
            19,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsRowStyle}"));
        Assert.Equal(
            2,
            settings.Descendants().Count(element =>
                element.Name.LocalName == "Expander"
                && Attribute(element, "IsExpanded") == "False"));
        Assert.Equal(
            4,
            settings.Descendants().Count(element =>
                Attribute(element, "Style") == "{StaticResource SettingsLastRowStyle}"));

        foreach (XElement toggle in settings.Descendants()
                     .Where(element => element.Name.LocalName == "ToggleSwitch"))
        {
            Assert.Equal(
                "{StaticResource CompactCardToggleSwitchStyle}",
                Attribute(toggle, "Style"));
        }

        Assert.Contains(about.Descendants(), element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "AutomationProperties.Name") == "Export diagnostics"
            && Attribute(element, "Style") == "{StaticResource IconActionButtonStyle}");

        Assert.DoesNotContain("ViewModel.ManualChangeDurationOptions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedManualChangeDurationOption", xaml, StringComparison.Ordinal);
        XElement liveReadings = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ResponsiveChoiceSelector"
            && Attribute(element, "AccessibleName")
                == "Live readings refresh interval");
        Assert.Equal(
            "{x:Bind ViewModel.LiveReadingsRefreshOptions, Mode=OneWay}",
            Attribute(liveReadings, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedLiveReadingsRefreshOption, Mode=TwoWay}",
            Attribute(liveReadings, "SelectedItem"));
        XElement theme = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ResponsiveChoiceSelector"
            && Attribute(element, "AccessibleName") == "App theme");
        XElement language = Assert.Single(settings.Descendants(), element =>
            element.Name.LocalName == "ResponsiveChoiceSelector"
            && Attribute(element, "AccessibleName") == "App language");
        Assert.Equal(
            "{x:Bind ViewModel.LanguageSelectionOptions, Mode=OneWay}",
            Attribute(language, "ItemsSource"));
        Assert.Equal(
            "{x:Bind ViewModel.SelectedLanguageOption, Mode=TwoWay}",
            Attribute(language, "SelectedItem"));
        Assert.Equal("210", Attribute(theme, "CompactMinWidth"));
        Assert.Equal(
            Attribute(theme, "CompactMinWidth"),
            Attribute(language, "CompactMinWidth"));
        Assert.Null(Attribute(theme, "CompactWidth"));
        Assert.Null(Attribute(language, "CompactWidth"));
        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "Key") == "SettingsPickerStyle");
        foreach (string pickerName in new[]
                 {
                     "Live readings refresh interval",
                     "App theme",
                     "App language",
                     "App accent color",
                     "Update channel",
                     "Update check interval",
                 })
        {
            XElement picker = Assert.Single(settings.Descendants(), element =>
                element.Name.LocalName == "ResponsiveChoiceSelector"
                && Attribute(element, "AccessibleName") == pickerName);
            Assert.Null(Attribute(picker, "MaxExpandedItems"));
        }
        Assert.Contains(
            "Choose how often OpenBlade refreshes temperatures, fan speed, and net battery power while the window is open or the tray icon is hovered.",
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
        Assert.Equal(
            "{StaticResource CompactCardToggleSwitchStyle}",
            Attribute(gamingMode, "Style"));
        XElement startupAnimation = NamedElement(
            document,
            "ToggleSwitch",
            "StartupAnimationToggle");
        Assert.Equal("OnStartupAnimationToggled", Attribute(startupAnimation, "Toggled"));
        Assert.Equal(
            "{StaticResource CompactCardToggleSwitchStyle}",
            Attribute(startupAnimation, "Style"));
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
        foreach ((string attribute, string handler) in new[]
        {
            ("PointerEntered", "OnTroubleshootingHeaderPointerEntered"),
            ("PointerMoved", "OnTroubleshootingHeaderPointerMoved"),
            ("PointerExited", "OnTroubleshootingHeaderPointerExited"),
            ("PointerPressed", "OnTroubleshootingHeaderPointerPressed"),
            ("PointerReleased", "OnTroubleshootingHeaderPointerReleased"),
            ("PointerCanceled", "OnTroubleshootingHeaderPointerCanceled"),
            ("PointerCaptureLost", "OnTroubleshootingHeaderPointerCanceled"),
        })
        {
            Assert.Equal(handler, Attribute(troubleshooting, attribute));
        }
        XElement troubleshootingHeaderSurface = NamedElement(
            document,
            "Grid",
            "TroubleshootingHeaderSurface");
        Assert.Equal("-16,0,-60,0", Attribute(troubleshootingHeaderSurface, "Margin"));
        foreach ((string name, string background) in new[]
        {
            (
                "TroubleshootingHeaderHoverBackground",
                "{ThemeResource SubtleFillColorSecondaryBrush}"),
            (
                "TroubleshootingHeaderPressedBackground",
                "{ThemeResource SubtleFillColorTertiaryBrush}"),
        })
        {
            XElement layer = NamedElement(document, "Border", name);
            Assert.Equal(background, Attribute(layer, "Background"));
            Assert.Equal("0", Attribute(layer, "Opacity"));
        }
        foreach (string property in new[]
                 {
                     "ViewModel.TroubleshootingServiceStatus",
                     "ViewModel.TroubleshootingPowerSourceStatus",
                     "ViewModel.TroubleshootingControlsStatus",
                     "ViewModel.TroubleshootingKeyboardDriverModeStatus",
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
        XElement startService = NamedElement(document, "Button", "StartOpenBladeButton");
        Assert.Contains(troubleshooting.Descendants(), element => ReferenceEquals(element, startService));
        Assert.Equal("OnStartOpenBlade", Attribute(startService, "Click"));
        Assert.Equal("Start service", Attribute(startService, "AutomationProperties.Name"));
        XElement startLabel = NamedElement(
            document,
            "TextBlock",
            "StartOpenBladeButtonLabel");
        Assert.Contains(startService.Descendants(), element => ReferenceEquals(element, startLabel));
        Assert.Equal("Start service", Attribute(startLabel, "Text"));
        Assert.Equal("1", Attribute(startLabel, "Opacity"));
        XElement startProgress = NamedElement(
            document,
            "ProgressRing",
            "StartOpenBladeProgressRing");
        Assert.Contains(startService.Descendants(), element => ReferenceEquals(element, startProgress));
        Assert.Same(startLabel.Parent, startProgress.Parent);
        Assert.Equal("Grid", startProgress.Parent?.Name.LocalName);
        Assert.Equal("False", Attribute(startProgress, "IsActive"));
        Assert.Equal("Collapsed", Attribute(startProgress, "Visibility"));
        Assert.Equal("Center", Attribute(startProgress, "HorizontalAlignment"));
        Assert.Equal("Center", Attribute(startProgress, "VerticalAlignment"));
        Assert.Equal("Polite", Attribute(startService, "AutomationProperties.LiveSetting"));
        Assert.Contains(
            "ViewModel.IsServiceUnavailable",
            Attribute(startService, "Visibility"),
            StringComparison.Ordinal);
        XElement stopRazerServices = NamedElement(
            document,
            "Button",
            "StopRazerServicesButton");
        Assert.Contains(
            troubleshooting.Descendants(),
            element => ReferenceEquals(element, stopRazerServices));
        Assert.Equal("OnStopRazerServices", Attribute(stopRazerServices, "Click"));
        Assert.Equal(
            "{StaticResource DestructiveButtonStyle}",
            Attribute(stopRazerServices, "Style"));
        Assert.Equal(
            "Stop Razer services",
            Attribute(stopRazerServices, "AutomationProperties.Name"));
        XElement razerServicesRow = stopRazerServices.Ancestors().First(element =>
            element.Name.LocalName == "Border"
            && Attribute(element, "Style") == "{StaticResource TroubleshootingSectionStyle}");
        Assert.Contains(razerServicesRow.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Other Razer software running");
        Assert.Contains(razerServicesRow.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text")?.Contains(
                "ViewModel.TroubleshootingOtherRazerSoftwareStatus",
                StringComparison.Ordinal) is true);
        Assert.DoesNotContain(razerServicesRow.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Razer background services");
        foreach (string label in new[]
                 {
                     "OpenBlade service",
                     "Control access",
                     "Keyboard driver mode",
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
        Assert.DoesNotContain(troubleshooting.Descendants(), element =>
            element.Name.LocalName == "Ellipse");
        XElement settingsPage = NamedElement(document, "ScrollViewer", "SettingsPage");
        XElement aboutPage = NamedElement(document, "ScrollViewer", "AboutPage");
        XElement settingsUpdates = NamedElement(
            document,
            "StackPanel",
            "SettingsUpdatesSection");
        XElement aboutVersions = NamedElement(document, "StackPanel", "AboutVersionsSection");
        XElement aboutNavigation = NamedElement(document, "NavigationViewItem", "AboutNavigationItem");
        Assert.Equal("about", Attribute(aboutNavigation, "Tag"));
        Assert.Contains(aboutNavigation.Ancestors(), element =>
            element.Name.LocalName == "NavigationView.FooterMenuItems");
        Assert.DoesNotContain(settingsPage.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Support");
        foreach (string section in new[]
                 {
                     "Versions",
                     "Device information",
                     "Support",
                     "Troubleshooting",
                 })
        {
            Assert.Contains(aboutPage.Descendants(), element =>
                element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text") == section);
        }
        Assert.Contains(settingsUpdates.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Updates");
        Assert.DoesNotContain(aboutPage.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Updates");
        Assert.Contains("ViewModel.AboutUiVersionStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("ViewModel.AboutSessionAgentVersionStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("ViewModel.AboutServiceVersionStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains("ViewModel.AboutBatteryHealthStatus", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.AboutDisplayBrightnessDriverStatus",
            aboutPage.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.AboutDisplayBrightnessDriverToolTip",
            aboutPage.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.TroubleshootingUsbDeviceStatus",
            aboutPage.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("VID 1532 · PID 02E0", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Contains(aboutPage.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Battery health");
        Assert.Contains(aboutPage.Descendants(), element =>
            element.Name.LocalName == "TextBlock"
            && Attribute(element, "Text") == "Razer brightness driver");
        Assert.Equal(
            new string?[] { "OpenBlade app", "Session agent", "Service" },
            aboutVersions
                .Descendants()
                .Where(element =>
                    element.Name.LocalName == "TextBlock"
                    && Attribute(element, "Style") == "{StaticResource CardTitleStyle}")
                .Select(element => Attribute(element, "Text"))
                .ToArray());
        Assert.DoesNotContain("OpenBlade installer version", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Supported laptop", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Relationship to Razer", aboutPage.ToString(), StringComparison.Ordinal);
        XElement communityTagline = Assert.Single(
            aboutPage.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && Attribute(element, "Text") == "For gamers. By the community.");
        Assert.Equal(
            "MainWindow_TextBlockForGamersByTheCommunity",
            Attribute(communityTagline, "Uid"));
        Assert.Equal(
            "{StaticResource SecondaryTextStyle}",
            Attribute(communityTagline, "Style"));
        Assert.Contains("ms-appx:///Assets/Brand/OpenBlade.png", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Copy version information", aboutPage.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            5,
            aboutPage.Descendants().Count(element =>
                element.Name.LocalName == "HyperlinkButton"
                && Attribute(element, "Style") == "{StaticResource ExternalLinkButtonStyle}"));
        XElement[] supportActions = aboutPage.Descendants()
            .Where(element =>
                element.Name.LocalName is "Button" or "HyperlinkButton"
                && Attribute(element, "Style") is
                    "{StaticResource IconActionButtonStyle}"
                    or "{StaticResource ExternalLinkButtonStyle}")
            .ToArray();
        Assert.Equal(6, supportActions.Length);
        Assert.All(
            supportActions,
            action =>
            {
                XElement row = Assert.IsType<XElement>(action.Parent);
                Assert.Equal("Border", row.Name.LocalName);
                Assert.Equal("0", Attribute(row, "Padding"));
                XElement content = Assert.Single(action.Elements());
                Assert.Equal("Grid", content.Name.LocalName);
                Assert.Contains(
                    content.Elements(),
                    element => element.Name.LocalName == "FontIcon"
                        && Attribute(element, "Style")
                            == "{StaticResource CardActionIconStyle}");
            });
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
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "Padding"
                && Attribute(setter, "Value") == "16,14");
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "HorizontalAlignment"
                && Attribute(setter, "Value") == "Stretch");
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "HorizontalContentAlignment"
                && Attribute(setter, "Value") == "Stretch");
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "UseSystemFocusVisuals"
                && Attribute(setter, "Value") == "True");
            Assert.Contains(style.Elements(), setter =>
                Attribute(setter, "Property") == "Template");
        }
        foreach (string templateKey in new[]
        {
            "CardActionButtonTemplate",
            "CardActionHyperlinkButtonTemplate",
        })
        {
            XElement template = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "ControlTemplate"
                && Attribute(element, "Key") == templateKey);
            Assert.Contains(
                template.Descendants(),
                setter => setter.Name.LocalName == "Setter"
                    && Attribute(setter, "Target") == "CardActionBackground.Background"
                    && Attribute(setter, "Value")
                        == "{ThemeResource SubtleFillColorSecondaryBrush}");
            Assert.Contains(
                template.Descendants(),
                setter => setter.Name.LocalName == "Setter"
                    && Attribute(setter, "Target") == "CardActionBackground.Background"
                    && Attribute(setter, "Value")
                        == "{ThemeResource SubtleFillColorTertiaryBrush}");
        }
        foreach (string resourceKey in new[]
        {
            "ExpanderChevronPointerOverBackground",
            "ExpanderChevronPressedBackground",
            "ExpanderChevronBorderPointerOverBrush",
            "ExpanderChevronBorderPressedBrush",
        })
        {
            XElement brush = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "SolidColorBrush"
                && Attribute(element, "Key") == resourceKey);
            Assert.Equal("Transparent", Attribute(brush, "Color"));
        }
        XElement cardActionIconStyle = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style"
            && Attribute(element, "Key") == "CardActionIconStyle");
        Assert.Contains(cardActionIconStyle.Elements(), setter =>
            Attribute(setter, "Property") == "FontSize"
            && Attribute(setter, "Value") == "{ThemeResource ExpanderChevronGlyphSize}");
        XElement[] cardActionIcons = supportActions
            .SelectMany(element => element.Descendants()
                .Where(descendant => descendant.Name.LocalName == "FontIcon"))
            .ToArray();
        Assert.Equal(6, cardActionIcons.Length);
        Assert.All(
            cardActionIcons,
            icon =>
            {
                Assert.Equal(
                    "{StaticResource CardActionIconStyle}",
                    Attribute(icon, "Style"));
                Assert.Null(Attribute(icon, "FontSize"));
            });
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
    public void MainWindow_ThxAudioOwnsDedicatedModelGatedPageAndNavigationLogo()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement navigation = NamedElement(document, "NavigationViewItem", "ThxAudioNavigationItem");
        XElement audio = NamedElement(document, "ScrollViewer", "ThxAudioPage");
        XElement laptop = NamedElement(document, "ScrollViewer", "LaptopPage");
        XElement section = NamedElement(document, "StackPanel", "ThxAudioSection");

        Assert.Equal("thx-audio", Attribute(navigation, "Tag"));
        Assert.Equal("{x:Bind ThxAudioNavigationVisibility, Mode=OneWay}", Attribute(navigation, "Visibility"));
        Assert.Contains(section, audio.Descendants());
        Assert.DoesNotContain(section, laptop.Descendants());
        Assert.Single(navigation.Descendants(), element => element.Name.LocalName == "PathIcon");
        Assert.Contains(audio.Descendants(), element =>
            Attribute(element, "Uid") == "MainWindow_ThxAudioSpatial");
        Assert.Contains(audio.Descendants(), element =>
            Attribute(element, "Uid") == "MainWindow_ThxAudioEqualizer");

        string window = ReadTraySource("MainWindow.xaml.cs");
        Assert.Contains(
            "else if (destination == \"thx-audio\" && !localizationSnapshotMode)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "private async void OnThxEndpointChanged(",
            window,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            window.Split("ThxViewModel.ConnectIfNeededAsync", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void MainWindow_LaptopPageOwnsFirmwareWideLaptopBehavior()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement navigation = NamedElement(
            document,
            "NavigationViewItem",
            "LaptopNavigationItem");
        XElement laptop = NamedElement(document, "ScrollViewer", "LaptopPage");
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
        XElement settings = NamedElement(document, "ScrollViewer", "SettingsPage");

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
        Assert.Equal("TroubleshootingHeaderSurface", Attribute(header, "Name"));
        Assert.Equal("-16,0,-60,0", Attribute(header, "Margin"));
        XElement content = Assert.Single(
            header.Elements(),
            element => element.Name.LocalName == "StackPanel");
        Assert.Equal("16,14,60,14", Attribute(content, "Margin"));
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
        Assert.Contains("ViewModel.PluggedInProfilePresentation.WindowsPowerMode", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UsbCProfilePresentation.LightingEffect", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.BatteryProfilePresentation.DisplayRefreshRate", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedProfilePresentation.PowerMode", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedProfilePresentation.WindowsPowerMode", xaml, StringComparison.Ordinal);
        XElement profileValueTemplate = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate"
            && Attribute(element, "Key") == "ProfileValuePresentationTemplate");
        XElement compactProfileValueTemplate = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate"
            && Attribute(element, "Key") == "CompactProfileValuePresentationTemplate");
        const string inlineValueIndicatorStart =
            "<Run Text=\"{x:Bind Text, Mode=OneTime}\" /><InlineUIContainer>";
        Assert.Equal(
            2,
            xaml.Split(inlineValueIndicatorStart, StringSplitOptions.None).Length - 1);
        foreach (XElement template in new[]
                 {
                     profileValueTemplate,
                     compactProfileValueTemplate,
                 })
        {
            XElement presenter = Assert.Single(template.Elements(), element =>
                element.Name.LocalName == "ContentControl");
            Assert.Equal(
                "{x:Bind IsPending, Mode=OneTime}",
                Attribute(presenter, "IsTabStop"));
            Assert.Equal(
                "{x:Bind PendingDescription, Mode=OneTime}",
                Attribute(presenter, "ToolTipService.ToolTip"));
            Assert.Equal(
                "{x:Bind AccessibilityDescription, Mode=OneTime}",
                Attribute(presenter, "AutomationProperties.Name"));
            Assert.Equal(
                "{x:Bind PendingDescription, Mode=OneTime}",
                Attribute(presenter, "AutomationProperties.HelpText"));
            XElement valueText = Assert.Single(template.Descendants(), element =>
                element.Name.LocalName == "RichTextBlock");
            Assert.Equal("Wrap", Attribute(valueText, "TextWrapping"));
            Assert.Equal("False", Attribute(valueText, "IsTextSelectionEnabled"));
            Assert.Equal(
                "Raw",
                Attribute(valueText, "AutomationProperties.AccessibilityView"));
            XElement paragraph = Assert.Single(valueText.Elements(), element =>
                element.Name.LocalName == "Paragraph");
            XElement valueRun = Assert.Single(paragraph.Elements(), element =>
                element.Name.LocalName == "Run");
            Assert.Equal(
                "{x:Bind Text, Mode=OneTime}",
                Attribute(valueRun, "Text"));
            XElement inlineIndicator = Assert.Single(paragraph.Elements(), element =>
                element.Name.LocalName == "InlineUIContainer");
            XElement indicator = Assert.Single(template.Descendants(), element =>
                element.Name.LocalName == "InfoBadge");
            Assert.Same(inlineIndicator, indicator.Parent);
            Assert.Equal("6,0,0,0", Attribute(indicator, "Margin"));
            Assert.Equal(
                "{StaticResource UnsavedNavigationBadgeStyle}",
                Attribute(indicator, "Style"));
            Assert.Equal(
                "{x:Bind IsPending, Mode=OneTime}",
                Attribute(indicator, "Visibility"));
            Assert.Equal(
                "Raw",
                Attribute(indicator, "AutomationProperties.AccessibilityView"));
        }
        XElement compactValueText = Assert.Single(
            compactProfileValueTemplate.Descendants(),
            element => element.Name.LocalName == "RichTextBlock");
        Assert.Equal("Right", Attribute(compactValueText, "TextAlignment"));
        XElement[] comparisonValues = comparisonProfiles.Descendants()
            .Where(element => element.Name.LocalName == "ContentControl"
                && Attribute(element, "ContentTemplate")
                    == "{StaticResource ProfileValuePresentationTemplate}")
            .ToArray();
        XElement[] compactValues = compactProfiles.Descendants()
            .Where(element => element.Name.LocalName == "ContentControl"
                && Attribute(element, "ContentTemplate")
                    == "{StaticResource CompactProfileValuePresentationTemplate}")
            .ToArray();
        Assert.Equal(60, comparisonValues.Length);
        Assert.Equal(20, compactValues.Length);
        string[] expectedProperties =
        [
            "PowerMode",
            "WindowsPowerMode",
            "GpuTuning",
            "Fans",
            "BatteryProtection",
            "LightingEffect",
            "LightingBrightness",
            "LightingColor",
            "LogoLighting",
            "LightingDisplayOff",
            "LightingIdle",
            "DisplayRefreshRate",
            "DisplayHdr",
            "DisplayColorProfile",
            "FunctionKeyBehavior",
            "GamingMode",
            "GamingModeWindowsKey",
            "GamingModeAltTab",
            "StartupAnimation",
        ];
        foreach (string property in expectedProperties)
        {
            Assert.Equal(3, comparisonValues.Count(value =>
                Attribute(value, "Content")?.Contains(
                    $"ProfilePresentation.{property},",
                    StringComparison.Ordinal) is true));
            Assert.Single(compactValues, value =>
                Attribute(value, "Content")?.Contains(
                    $"ProfilePresentation.{property},",
                    StringComparison.Ordinal) is true);
        }
        XElement[] chargeLimitValues = comparisonValues
            .Where(value => Attribute(value, "Content")?.Contains(
                "BatteryPolicyPresentation,",
                StringComparison.Ordinal) is true)
            .ToArray();
        Assert.Equal(3, chargeLimitValues.Length);
        Assert.Equal(
            ["1", "2", "3"],
            chargeLimitValues.Select(value => Attribute(value, "Grid.Column")!));
        Assert.All(chargeLimitValues, value =>
            Assert.Null(Attribute(value, "Grid.ColumnSpan")));
        Assert.Single(compactValues, value =>
            Attribute(value, "Content")?.Contains(
                "BatteryPolicyPresentation,",
                StringComparison.Ordinal) is true);
        Assert.All(comparisonValues, value =>
            Assert.Equal("Stretch", Attribute(value, "HorizontalContentAlignment")));
        Assert.All(compactValues, value =>
            Assert.Equal("Stretch", Attribute(value, "HorizontalContentAlignment")));
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
            "{x:Bind IsLightingControlUnavailableNoticeVisible, Mode=OneWay}",
            Attribute(NamedElement(document, "InfoBar", "LightingUnavailableInfoBar"), "Visibility"));
        Assert.Contains("{StaticResource TitleLargeTextBlockStyle}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource AccentButtonStyle}", xaml, StringComparison.Ordinal);
        Assert.Equal(
            9,
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
        Assert.Equal(
            ExpectedStandardLightingEffectOrder,
            viewModel.AvailableLightingEffectOptions.Select(option => option.Effect));
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
        await client.PublishAndWaitAsync(initial);

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
        await client.PublishAndWaitAsync(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Later profiles",
            Profiles = laterProfiles,
        });

        Assert.Equal(laterProfiles, viewModel.Profiles);
        Assert.Equal(PerformancePreset.Balanced, viewModel.PluggedInPerformanceEditor);
        Assert.Equal(FanMode.Automatic, viewModel.PluggedInFanModeEditor);
        Assert.Equal(FanMode.Maximum, viewModel.BatteryFanModeEditor);
        Assert.Equal(70, viewModel.ProfileChargeLimitEditor);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public async Task SaveProfiles_LegacyCapabilitiesRejectNonBalancedMaximumCombination(
        PerformancePreset performance)
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        await client.PublishAndWaitAsync(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            }));
        viewModel.PluggedInPerformanceEditor = performance;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(
            viewModel.SaveProfilesCommand);

        Assert.False(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.Null(client.SavedProfiles);
    }

    [Fact]
    public async Task SaveProfiles_DirectManualFanTransitionsPersistIndependentAcChoices()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot legacy = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = ChargeLimits(),
            });
        await client.PublishAndWaitAsync(legacy);
        viewModel.PluggedInPerformanceEditor = PerformancePreset.Performance;
        viewModel.PluggedInFanModeEditor = FanMode.Maximum;
        AsyncRelayCommand save = Assert.IsType<AsyncRelayCommand>(
            viewModel.SaveProfilesCommand);

        Assert.False(save.CanExecute(null));
        await client.PublishAndWaitAsync(legacy with
        {
            Revision = legacy.Revision + 1,
            Capabilities = legacy.Capabilities! with
            {
                SupportsDirectManualFanTransitions = true,
            },
        });

        Assert.True(save.CanExecute(null));
        await save.ExecuteAsync(null);

        Assert.NotNull(client.SavedProfiles);
        Assert.Equal(
            PerformancePreset.Performance,
            client.SavedProfiles.PluggedIn.Performance);
        Assert.Equal(FanMode.Maximum, client.SavedProfiles.PluggedIn.Fans);
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
        await client.PublishAndWaitAsync(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset>(),
                new HashSet<FanMode>(),
                new HashSet<LightingEffect>(),
                ChargeLimits(), true, true)));
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
                new HashSet<int> { 50, 101 }, true, true)));
        await WaitUntilAsync(() => viewModel.Capabilities.ChargeLimits.Contains(101));

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

    [Theory]
    [InlineData(70, 80)]
    [InlineData(80, 75)]
    public async Task ProtectionOffCommandDoesNotRequireCurrentLimitToMatchStoredPolicy(
        int currentLimit, int storedLimit)
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = new HashSet<int> { 70, 80 },
                SupportsBatteryProtectionOff = true,
            },
            confirmedBattery: new ConfirmedBatteryState(true, currentLimit, false));
        status = status with
        {
            DesiredState = status.DesiredState with
            {
                Battery = new DesiredBatteryState(BatteryPolicy.Create(storedLimit), true, false),
            },
        };
        await client.PublishAndWaitAsync(status);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(
            viewModel.ToggleBatteryProtectionCommand);

        Assert.True(viewModel.CanChangeBatteryProtection);
        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync(null);

        Assert.False(client.SavedBatteryProtection);
        Assert.Null(client.SavedBatteryPolicy);
    }

    [Fact]
    public async Task ProtectionOffCommandRejectsUnadmittedCurrentLimit()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        await client.PublishAndWaitAsync(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = new HashSet<int> { 80 },
                SupportsBatteryProtectionOff = true,
            },
            confirmedBattery: new ConfirmedBatteryState(true, 75, false)));

        Assert.False(viewModel.CanChangeBatteryProtection);
        Assert.False(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.Null(client.SavedBatteryProtection);
    }

    [Fact]
    public async Task ProtectionOnCommandRequiresAdmissionOfStoredLimit()
    {
        var client = new PresentationClient();
        await using var viewModel = new MainViewModel(client, new ImmediateDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = new HashSet<int> { 80 },
                SupportsBatteryProtectionOff = true,
            },
            confirmedBattery: new ConfirmedBatteryState(false, null, false));
        await client.PublishAndWaitAsync(status with
        {
            DesiredState = status.DesiredState with
            {
                Battery = new DesiredBatteryState(BatteryPolicy.Create(75), true, false),
            },
        });

        Assert.False(viewModel.CanChangeBatteryProtection);
        Assert.False(viewModel.ToggleBatteryProtectionCommand.CanExecute(null));
        Assert.Null(client.SavedBatteryProtection);
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
        await client.PublishAndWaitAsync(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect> { LightingEffect.Static },
                ChargeLimits(), true, true)));

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
        await client.PublishAndWaitAsync(CreateStatus(
            verified: true,
            blocked: false,
            capabilities: new DeviceCapabilities(
                new HashSet<PerformancePreset> { PerformancePreset.Balanced },
                new HashSet<FanMode> { FanMode.Automatic },
                new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()
                    .Where(effect => effect != LightingEffect.Sequence)),
                ChargeLimits(), true, true)));
        Assert.True(viewModel.CanControlHardware);
        Assert.Equal(
            Enum.GetValues<LightingEffect>()
                .Count(effect => effect != LightingEffect.Sequence),
            viewModel.AvailableLightingEffectOptions.Count);
        Assert.Equal(
            ExpectedStandardLightingEffectOrder,
            viewModel.AvailableLightingEffectOptions.Select(option => option.Effect));

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

        viewModel.LightingEffectEditor = LightingEffect.AudioMeter;
        viewModel.LightingAudioMeterColorBoostEditor = 1.25;
        viewModel.LightingAudioMeterDecayMillisecondsEditor = 750;
        Assert.Equal(
            LightingSettingsFactory.AudioMeter(
                colorBoost: 1.25,
                brightness: 64,
                decayMilliseconds: 750),
            viewModel.LightingDraft);
        Assert.True(viewModel.LightingShowsAudioMeterControls);
        Assert.Equal("Up to 0.75 seconds", viewModel.LightingAudioMeterDecayValue);
        viewModel.LightingAudioMeterDecayMillisecondsEditor = 1000;
        Assert.Equal("Up to 1 second", viewModel.LightingAudioMeterDecayValue);
        viewModel.LightingAudioMeterDecayMillisecondsEditor = 2000;
        Assert.Equal("Up to 2 seconds", viewModel.LightingAudioMeterDecayValue);
        Assert.Equal("Audio Meter", viewModel.SelectedLightingEffectOption?.Label);

        viewModel.LightingEffectEditor = LightingEffect.Fire;
        Assert.Equal(
            LightingSettingsFactory.Fire(64),
            viewModel.LightingDraft);
        Assert.False(viewModel.LightingShowsAudioMeterControls);
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
        XElement page = NamedElement(document, "ScrollViewer", "LightingPage");
        XElement lightingTemplate = NamedTemplate(document, "DashboardLightingCardTemplate");
        XElement effectPicker = NamedElement(
            document,
            "ResponsiveChoiceSelector",
            "LightingEffectPicker");
        XElement controls = NamedElement(document, "ContentControl", "LightingControls");
        XElement keyboardSection = NamedElement(
            document,
            "StackPanel",
            "LightingKeyboardSection");
        XElement powerSavingSection = NamedElement(
            document,
            "StackPanel",
            "LightingPowerSavingSection");
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
            "{x:Bind CanRequestHardwareControl, Mode=OneWay}",
            Attribute(controls, "IsEnabled"));
        Assert.Contains(controls, effectPicker.Ancestors());
        Assert.Equal(
            "{x:Bind IsLightingControlUnavailableNoticeVisible, Mode=OneWay}",
            Attribute(infoBar, "IsOpen"));
        Assert.Equal(
            "{x:Bind LightingControlUnavailableMessage, Mode=OneWay}",
            Attribute(infoBar, "Message"));
        Assert.Equal(
            "{x:Bind AvailableLightingEffectOptions, Mode=OneWay}",
            Attribute(effectPicker, "ItemsSource"));
        Assert.Equal(
            "{x:Bind SelectedLightingEffectOption, Mode=TwoWay}",
            Attribute(effectPicker, "SelectedItem"));
        Assert.All([keyboardSection, powerSavingSection], section =>
        {
            Assert.Null(Attribute(section, "IsEnabled"));
            Assert.Equal(
                "{x:Bind ShowOpenBladeKeyboardLightingControls, Mode=OneWay}",
                Attribute(section, "Visibility"));
        });
        Assert.All([displayOffToggle, idleTimeoutToggle], toggle =>
        {
            Assert.Equal(
                "{StaticResource CompactCardToggleSwitchStyle}",
                Attribute(toggle, "Style"));
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
        Assert.DoesNotContain(
            "Windows Dynamic Lighting interface detected",
            lightingXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Quick effects observed in Synapse",
            lightingXaml,
            StringComparison.Ordinal);
        Assert.Contains("Audio Meter color boost", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("LightingShowsAudioMeterControls", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Smooth quiet changes", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("when sound gets quieter or pauses", lightingXaml, StringComparison.Ordinal);
        Assert.Contains("Louder sounds still respond immediately.", lightingXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ripple", lightingXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindow_SpecialKeyBindingsRequireExplicitWritableCapability()
    {
        XDocument document = XDocument.Parse(ReadMainWindowXaml());
        XElement section = NamedElement(document, "StackPanel", "ShortcutKeysSection");
        XElement itemTemplate = section.Descendants().Single(element =>
            element.Name.LocalName == "DataTemplate"
            && Attribute(element, "DataType") == "local:SpecialKeyBindingViewModel");
        XElement row = itemTemplate.Descendants().First(element =>
            element.Name.LocalName == "Border");

        Assert.Equal(
            "{x:Bind ViewModel.IsSpecialKeyBindingsEditable, Mode=OneWay}",
            Attribute(section, "Visibility"));
        Assert.Equal(
            "{x:Bind IsEditable, Mode=OneWay}",
            Attribute(row, "Visibility"));
        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "Name") == "SpecialKeyBindingsObservedOnlyInfoBar");
        Assert.DoesNotContain(document.Descendants(), element =>
            Attribute(element, "Text") == "{x:Bind AccessLabel, Mode=OneWay}");
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

    private sealed class PresentationClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<PublishedStatus> statuses =
            Channel.CreateUnbounded<PublishedStatus>();
        public ProfileSet? SavedProfiles { get; private set; }
        public BatteryPolicy? SavedBatteryPolicy { get; private set; }
        public bool? SavedBatteryProtection { get; private set; }
        public void Publish(ServiceStatusSnapshot status) =>
            Assert.True(statuses.Writer.TryWrite(new(
                new ServiceWatchUpdate.Connected(status),
                Delivered: null)));
        public async Task PublishAndWaitAsync(ServiceStatusSnapshot status)
        {
            var delivered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(statuses.Writer.TryWrite(new(
                new ServiceWatchUpdate.Connected(status),
                delivered)));
            await delivered.Task.WaitAsync(TestDeadlockGuard);
        }
        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (PublishedStatus status in statuses.Reader.ReadAllAsync(token))
            {
                yield return status.Update;
                status.Delivered?.TrySetResult();
            }
        }

        private sealed record PublishedStatus(
            ServiceWatchUpdate Update,
            TaskCompletionSource? Delivered);

        public override Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token)
        {
            SavedProfiles = profiles;
            return Result();
        }
        public override Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken token) => throw new NotSupportedException();
        public override Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token) => throw new NotSupportedException();
        public override Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) => Result();
        public override Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token) => Result();
        public override Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token) => Result();
        public override Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token)
        {
            SavedBatteryPolicy = value;
            return Result();
        }
        public override Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token)
        {
            SavedBatteryProtection = enabled;
            return Result();
        }
        public override Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => Result();
        private static Task<ControlOperationResult> Result() => Task.FromResult(
            new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));
    }
}
