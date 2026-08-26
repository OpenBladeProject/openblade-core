using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
    [Fact]
    public void FirmwareVersionsSectionHidesWhenEveryFirmwareRowIsHidden()
    {
        XDocument document = XDocument.Parse(
            ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml"));
        XElement versionsSection = NamedElement(
            document,
            "Border",
            "FirmwareVersionsSection");

        Assert.Equal(
            "{x:Bind FirmwareUpdateViewModel.ShowsFirmwareVersions, Mode=OneWay}",
            Attribute(versionsSection, "Visibility"));

        XElement footerSection = NamedElement(
            document,
            "Border",
            "FirmwareFooterSection");
        Assert.Null(Attribute(footerSection, "Visibility"));
    }

    [Fact]
    public void LocalizedPageHeadersKeepProfileActionsResponsiveAndOrdered()
    {
        XDocument document = XDocument.Parse(
            ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml"));
        foreach ((string headerName, string actionName, string customizeUid, string visibilityBinding) in new[]
        {
            (
                "DashboardPageHeader",
                "DashboardProfileSaveActions",
                "MainWindow_ButtonCustomize",
                "{x:Bind ViewModel.HasDashboardProfileActionOpportunity, Mode=OneWay}"),
            (
                "LaptopPageHeader",
                "LaptopHeaderActions",
                "MainWindow_ButtonCustomize2",
                "{x:Bind ViewModel.HasLaptopPageProfileActionOpportunity, Mode=OneWay}"),
            (
                "LightingPageHeader",
                "LightingHeaderActions",
                "MainWindow_ButtonCustomize3",
                "{x:Bind ViewModel.HasLightingProfileActionOpportunity, Mode=OneWay}"),
            (
                "DisplayPageHeader",
                "DisplayHeaderActions",
                "MainWindow_ButtonCustomize4",
                "{x:Bind DisplayViewModel.CanSaveCurrentSettingsToProfile, Mode=OneWay}"),
        })
        {
            XElement header = NamedElement(document, "ResponsivePageHeader", headerName);
            XElement profileActions = NamedElement(document, "StackPanel", actionName);
            XElement customizeButton = UidElement(document, "Button", customizeUid);

            Assert.Same(header, profileActions.Parent);
            Assert.Equal("1", Attribute(profileActions, "Grid.Column"));
            Assert.Null(Attribute(profileActions, "Grid.Row"));
            Assert.Null(Attribute(profileActions, "Grid.ColumnSpan"));
            Assert.Null(Attribute(profileActions, "Margin"));
            Assert.Equal("Right", Attribute(profileActions, "HorizontalAlignment"));
            Assert.Equal(
                "ProfileActions",
                Attribute(profileActions, "ResponsivePageHeader.Role"));
            Assert.Equal(visibilityBinding, Attribute(profileActions, "Visibility"));
            Assert.Null(Attribute(header, "RowSpacing"));
            Assert.Null(Attribute(header, "ColumnSpacing"));

            XElement[] profileButtons = profileActions.Elements()
                .Where(element => element.Name.LocalName == "Button")
                .ToArray();
            Assert.Equal(2, profileButtons.Length);
            Assert.Equal(
                "{StaticResource ProfileSaveButtonStyle}",
                Attribute(profileButtons[0], "Style"));
            Assert.Equal("Use profile settings", Attribute(profileButtons[1], "Content"));
            Assert.Equal(
                "{StaticResource PageHeaderSecondaryButtonStyle}",
                Attribute(profileButtons[1], "Style"));

            Assert.Same(header, customizeButton.Parent);
            Assert.Equal("2", Attribute(customizeButton, "Grid.Column"));
            Assert.Null(Attribute(customizeButton, "Grid.Row"));
            Assert.Equal(
                "Customize",
                Attribute(customizeButton, "ResponsivePageHeader.Role"));

            List<XElement> children = header.Elements().ToList();
            Assert.True(children.IndexOf(profileActions) < children.IndexOf(customizeButton));
        }

        XElement contentTemplate = KeyedElement(
            document,
            "DataTemplate",
            "PageHeaderButtonContentTemplate");
        XElement templateText = Assert.Single(
            contentTemplate.Elements(),
            element => element.Name.LocalName == "TextBlock");
        Assert.Equal("Wrap", Attribute(templateText, "TextWrapping"));
        foreach (string styleKey in new[]
        {
            "ProfileSaveButtonStyle",
            "PageHeaderSecondaryButtonStyle",
        })
        {
            XElement style = KeyedElement(document, "Style", styleKey);
            XElement[] setters = style.Elements()
                .Where(element => element.Name.LocalName == "Setter")
                .ToArray();
            Assert.Contains(setters, setter =>
                Attribute(setter, "Property") == "MinHeight"
                && Attribute(setter, "Value") == "32");
            Assert.Contains(setters, setter =>
                Attribute(setter, "Property") == "Padding"
                && Attribute(setter, "Value") == "14,4");
            Assert.Contains(setters, setter =>
                Attribute(setter, "Property") == "ContentTemplate"
                && Attribute(setter, "Value") ==
                    "{StaticResource PageHeaderButtonContentTemplate}");
            Assert.DoesNotContain(setters, setter =>
                Attribute(setter, "Property") == "Height");
        }

        XElement profilesHeader = NamedElement(
            document,
            "ResponsivePageHeader",
            "ProfilesPageHeader");
        XElement profilesTitle = UidElement(
            document,
            "TextBlock",
            "MainWindow_TextBlockProfiles2");
        XElement profilesActions = NamedElement(document, "StackPanel", "ProfilesHeaderActions");
        Assert.Same(profilesHeader, profilesTitle.Parent);
        Assert.Equal(
            "Title",
            Attribute(profilesTitle, "ResponsivePageHeader.Role"));
        Assert.Same(profilesHeader, profilesActions.Parent);
        Assert.Equal("1", Attribute(profilesActions, "Grid.Column"));
        Assert.Null(Attribute(profilesActions, "Grid.Row"));
        Assert.Null(Attribute(profilesActions, "Grid.ColumnSpan"));
        Assert.Null(Attribute(profilesActions, "Margin"));
        Assert.Equal("Right", Attribute(profilesActions, "HorizontalAlignment"));
        Assert.Equal(
            "ProfileActions",
            Attribute(profilesActions, "ResponsivePageHeader.Role"));
        Assert.Null(Attribute(profilesHeader, "RowSpacing"));
        Assert.Null(Attribute(profilesHeader, "ColumnSpacing"));

        XElement[] profileFileActions = profilesActions.Elements()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        Assert.Equal(2, profileFileActions.Length);
        Assert.Equal("MainWindow_ButtonImportProfiles", Attribute(profileFileActions[0], "Uid"));
        Assert.Equal("Import profiles", Attribute(profileFileActions[0], "Content"));
        Assert.Equal("OnImportProfiles", Attribute(profileFileActions[0], "Click"));
        Assert.Equal(
            "Import saved profiles from JSON",
            Attribute(profileFileActions[0], "ToolTipService.ToolTip"));
        Assert.Equal(
            "Import profiles from JSON",
            Attribute(profileFileActions[0], "AutomationProperties.Name"));
        Assert.Equal("MainWindow_ButtonExportProfiles", Attribute(profileFileActions[1], "Uid"));
        Assert.Equal("Export profiles", Attribute(profileFileActions[1], "Content"));
        Assert.Equal("OnExportProfiles", Attribute(profileFileActions[1], "Click"));
        Assert.Equal(
            "Export saved profiles as JSON",
            Attribute(profileFileActions[1], "ToolTipService.ToolTip"));
        Assert.Equal(
            "Export profiles to JSON",
            Attribute(profileFileActions[1], "AutomationProperties.Name"));
        Assert.All(profileFileActions, button => Assert.Equal(
            "{StaticResource PageHeaderSecondaryButtonStyle}",
            Attribute(button, "Style")));

        XElement aboutCustomize = UidElement(document, "Button", "MainWindow_ButtonCustomize7");
        Assert.Equal("Grid", aboutCustomize.Parent?.Name.LocalName);
        Assert.Equal("1", Attribute(aboutCustomize, "Grid.Column"));
        Assert.Null(Attribute(aboutCustomize, "Grid.Row"));
    }

    [Fact]
    public void DashboardCustomPerformanceRowsUseNeutralSubcardBackgrounds()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string powerModeCard = SliceMethod(
            xaml,
            "<DataTemplate x:Key=\"DashboardPowerModeCardTemplate\"",
            "<DataTemplate x:Key=\"DashboardFansCardTemplate\"");

        Assert.Equal(
            6,
            CountOccurrences(
                powerModeCard,
                "Style=\"{StaticResource SettingsNeutralSubcardStyle}\""));
        Assert.Equal(
            1,
            CountOccurrences(
                powerModeCard,
                "Style=\"{StaticResource SettingsLastNeutralSubcardStyle}\""));
        Assert.DoesNotContain("SettingsEmphasizedSubcardStyle", powerModeCard, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardGpuTuningDescriptionUsesDefinedSharedStyle()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string gpuTuningCard = SliceMethod(
            xaml,
            "Text=\"GPU tuning\"",
            "x:Name=\"GpuClockTuningToggle\"");

        Assert.Contains(
            "Style=\"{StaticResource SecondaryTextStyle}\"",
            gpuTuningCard,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CardDescriptionStyle", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerModeCardOffersConfirmedGpuAppCleanup()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string codeBehind = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");
        string powerModeCard = SliceMethod(
            xaml,
            "<DataTemplate x:Key=\"DashboardPowerModeCardTemplate\"",
            "<DataTemplate x:Key=\"DashboardFansCardTemplate\"");

        Assert.Contains("Content=\"Close GPU apps\"", powerModeCard, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCloseGpuAppsClick\"", powerModeCard, StringComparison.Ordinal);
        int gpuAppsLabel = powerModeCard.IndexOf(
            "Text=\"Apps using the NVIDIA GPU\"",
            StringComparison.Ordinal);
        int gpuAppsRowStart = powerModeCard.LastIndexOf(
            "<Border",
            gpuAppsLabel,
            StringComparison.Ordinal);
        int gpuAppsRowEnd = powerModeCard.IndexOf(
            "Click=\"OnCloseGpuAppsClick\"",
            gpuAppsLabel,
            StringComparison.Ordinal);
        Assert.True(gpuAppsRowStart >= 0 && gpuAppsRowEnd > gpuAppsRowStart);
        string gpuAppsRow = powerModeCard[gpuAppsRowStart..gpuAppsRowEnd];
        Assert.Contains(
            "SettingsLastNeutralSubcardStyle",
            gpuAppsRow,
            StringComparison.Ordinal);
        Assert.Contains(
            "local:AnimatedVisibility.IsVisible=\"{Binding IsGpuAppCleanupVisible}\"",
            gpuAppsRow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SettingsLastEmphasizedSubcardStyle",
            gpuAppsRow,
            StringComparison.Ordinal);
        Assert.Contains("Close apps using the NVIDIA GPU?", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Force close remaining apps?", codeBehind, StringComparison.Ordinal);
        string handler = SliceMethod(
            codeBehind,
            "private async Task HandleCloseGpuAppsAsync",
            "private async Task ShowGpuAppsMessageAsync");
        Assert.Contains(
            "afterClose.Status == SessionGpuOperationStatus.Failed",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "finalScan.Status == SessionGpuOperationStatus.Failed",
            handler,
            StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("catch (Exception exception)", StringComparison.Ordinal)
                > handler.IndexOf("SessionGpuResponse finalScan", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutableProjectsInheritTheSharedOpenBladeIcon()
    {
        string targets = ReadRepositoryFile("src", "Directory.Build.targets");
        string publish = ReadRepositoryFile("build", "Publish.ps1");

        Assert.Contains("'$(OutputType)' == 'Exe'", targets, StringComparison.Ordinal);
        Assert.Contains("'$(OutputType)' == 'WinExe'", targets, StringComparison.Ordinal);
        Assert.Contains("'$(ApplicationIcon)' == ''", targets, StringComparison.Ordinal);
        Assert.Contains(
            "OpenBlade.Tray\\Assets\\Brand\\OpenBlade.ico",
            targets,
            StringComparison.Ordinal);
        Assert.True(File.Exists(RepositoryPath(
            "src",
            "OpenBlade.Tray",
            "Assets",
            "Brand",
            "OpenBlade.ico")));

        string[] executableProjects =
        [
            "OpenBlade.Service",
            "OpenBlade.SessionAgent",
            "OpenBlade.Tray",
            "OpenBlade.Capture",
            "OpenBlade.Setup",
        ];
        foreach (string project in executableProjects)
        {
            Assert.Contains(
                $"src\\{project}\\{project}.csproj",
                publish,
                StringComparison.Ordinal);
            Assert.True(File.Exists(RepositoryPath("src", project, $"{project}.csproj")));
        }
    }

    [Fact]
    public void TrayStartsOnlyItsExactSiblingSessionAgent()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string launcher = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "SessionAgentLauncher.cs");

        Assert.Contains("SessionAgentLauncher.TryStartSibling()", app, StringComparison.Ordinal);
        Assert.Contains("AppContext.BaseDirectory", launcher, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.SessionAgent.exe", launcher, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesPageIsReadOnlyAndSubsystemPagesOwnProfileActions()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string profilesPage = SliceMethod(
            xaml,
            "<ScrollViewer x:Name=\"ProfilesPage\"",
            "<ScrollViewer x:Name=\"LaptopPage\"");

        Assert.Contains("x:Name=\"ProfilesComparisonLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProfilesCompactLayout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Import profiles\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnImportProfiles\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Import profiles from JSON\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PluggedInProfilePresentation.PowerMode", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.BatteryProfilePresentation.LightingEffect", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UsbCProfilePresentation.DisplayColorProfile", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedProfileComparisonOption", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedProfilePresentation.PowerMode", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsPluggedInProfileDraft", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsBatteryProfileDraft", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsUsbCProfileDraft", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SavedActiveProfileSummary", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.PendingActiveProfileSummary", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.DiscardProfileDraftCommand",
            profilesPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.SaveProfilesCommand",
            profilesPage,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.DiscardProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveProfilesCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Current display\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCaptureCurrentDisplaySettings", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveLaptopProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveLightingProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.CanSaveCurrentSettingsToProfile", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DashboardPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LightingPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisplayPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(
            xaml,
            "Content=\"{x:Bind ViewModel.SaveToActiveProfileLabel, Mode=OneWay}\""));
        Assert.Contains(
            "Content=\"{x:Bind ViewModel.DashboardProfileSaveLabel, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Not saved\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(xaml, "Content=\"Use profile settings\""));
        Assert.Equal(1, CountOccurrences(
            xaml,
            "Visibility=\"{x:Bind ViewModel.HasLaptopPageProfileDifference, Mode=OneWay}\""));
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.HasDashboardProfileRestoreOpportunity, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.HasLightingProfileDifference, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind DisplayViewModel.HasProfileDifference, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Style=\"{StaticResource ProfileSaveStatusTextStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(
            xaml,
            "Style=\"{StaticResource ProfileSaveButtonStyle}\""));
        Assert.True(CountOccurrences(
            xaml,
            "Style=\"{StaticResource PageHeaderSecondaryButtonStyle}\"") >= 8);
        Assert.DoesNotContain(
            "Style=\"{StaticResource ProfileSaveOverflowButtonStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AutomationProperties.Name=\"More profile actions\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Fill=\"{ThemeResource SystemFillColorSuccessBrush}\" Visibility=\"{x:Bind ViewModel.IsConnected, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Fill=\"{ThemeResource SystemFillColorSuccessBrush}\" Visibility=\"{x:Bind ViewModel.IsReadinessReady, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderActions.Margin", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.ThermalProfileRelationshipStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.LightingProfileRelationshipStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayViewModel.ProfileRelationshipStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Save to profile\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTipService.ToolTip=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Text=\"Unsaved\""));
        Assert.Equal(3, CountOccurrences(xaml, "Text=\"Changes pending\""));
        Assert.Equal(4, CountOccurrences(
            xaml,
            "Style=\"{StaticResource DraftStatusTextStyle}\""));
        string draftStatusStyle = SliceMethod(
            xaml,
            "<Style x:Key=\"DraftStatusTextStyle\"",
            "</Style>");
        Assert.Contains(
            "<Setter Property=\"TextWrapping\" Value=\"Wrap\" />",
            draftStatusStyle,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "x:Name=\"LightingEffectPicker\""));
        Assert.DoesNotContain("PluggedInLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BatteryLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UsbCLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedManualChangeDurationOption", xaml, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("ViewModel.SaveDisplayProfileDraftCommand", window, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.UserSettingsChanged +=", window, StringComparison.Ordinal);
        Assert.Contains(
            "args.PowerSource,",
            window,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.SavedDisplayProfileRestoreRequested +=", window, StringComparison.Ordinal);
        Assert.Contains("new SessionDisplaySettingsAdapter()", window, StringComparison.Ordinal);
        Assert.Contains("expectedPowerSource,", window, StringComparison.Ordinal);
        Assert.Contains("nameof(MainViewModel.ActivePowerSource)", window, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.CurrentProfileSettings", window, StringComparison.Ordinal);
        Assert.Contains("sourceAtStart != ViewModel.ActivePowerSource", window, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsDisplaySettingsController", window, StringComparison.Ordinal);
        Assert.DoesNotContain("observedDisplayProfilePowerSource", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ActiveSavedDisplayProfile", window, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoAndFirmwareOnlyLaptopActionsUseTruthfulSettingsPatterns()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("Text=\"Laptop logo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Laptop behavior\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AccessibleName=\"Set function key behavior\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GamingModeToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupAnimationToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind AvailableFunctionKeyPrimaryOptions, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{x:Bind SelectedFunctionKeyPrimaryOption, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaceholderText=\"{x:Bind FunctionKeyPrimaryStateLabel, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectionChanged=\"OnFunctionKeyPrimaryChanged\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanEditFunctionKeyPrimary, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsFunctionKeyFieldLoading, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind GamingModeToggleIsOn, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanEditGamingMode, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsGamingModeFieldLoading, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind StartupAnimationToggleIsOn, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanEditStartupAnimation, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsStartupAnimationFieldLoading, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FunctionKeyFirmwareLoadingSlot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GamingModeFirmwareLoadingSlot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupAnimationFirmwareLoadingSlot\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(xaml, "FirmwareLoadingSlot\""));
        Assert.True(
            xaml.IndexOf("FunctionKeyFirmwareLoadingSlot", StringComparison.Ordinal)
            < xaml.IndexOf("AccessibleName=\"Set function key behavior\"", StringComparison.Ordinal));
        Assert.True(
            xaml.IndexOf("GamingModeFirmwareLoadingSlot", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"GamingModeToggle\"", StringComparison.Ordinal));
        Assert.True(
            xaml.IndexOf("StartupAnimationFirmwareLoadingSlot", StringComparison.Ordinal)
            < xaml.IndexOf("x:Name=\"StartupAnimationToggle\"", StringComparison.Ordinal));
        Assert.Contains("Toggled=\"OnGamingModeToggled\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnBlockWindowsKeyInGamingModeToggled\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnBlockAltTabInGamingModeToggled\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Toggled=\"OnStartupAnimationToggled\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Set&#x2026;\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Change\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"Laptop logo\" Style=\"{StaticResource CardTitleStyle}\"",
            xaml,
            StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string firmwareToggleHelper = SliceMethod(
            window,
            "private async Task ApplyLaptopBehaviorToggleAsync(",
            "private async void OnDashboardPerformanceChanged(");
        Assert.DoesNotContain("toggle.IsEnabled = false;", firmwareToggleHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("toggle.IsEnabled = true;", firmwareToggleHelper, StringComparison.Ordinal);

        int functionKeys = xaml.IndexOf("AccessibleName=\"Set function key behavior\"", StringComparison.Ordinal);
        int gamingMode = xaml.IndexOf("x:Name=\"GamingModeToggle\"", StringComparison.Ordinal);
        int startupAnimation = xaml.IndexOf("x:Name=\"StartupAnimationToggle\"", StringComparison.Ordinal);
        Assert.True(functionKeys < gamingMode);
        Assert.True(gamingMode < startupAnimation);

        Assert.Contains("selected.Action", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectedFunctionKeyPrimaryOption", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ApplyOneShotLaptopControlCommand", window, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareControlHandlers_DoNotOverrideBoundAvailability()
    {
        string window = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");
        string hardwareHandlers = SliceMethod(
            window,
            "private async void OnDashboardPerformanceChanged(",
            "private void OnNavigationSelectionChanged(");

        Assert.DoesNotContain(
            ".IsEnabled =",
            hardwareHandlers,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AboutPageOwnsSupportAndUsesBorderlessIconActions()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");

        Assert.Contains("x:Name=\"AboutNavigationItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<NavigationView.FooterMenuItems>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutPage\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy version information", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCopyVersionInformation", window, StringComparison.Ordinal);
        Assert.DoesNotContain("DataTransferClipboard", window, StringComparison.Ordinal);
        Assert.Contains("AboutPage.Visibility = destination == \"about\"", window, StringComparison.Ordinal);
        Assert.Contains("LaptopPage.Visibility = destination == \"laptop\"", window, StringComparison.Ordinal);
        Assert.Contains("MainNavigation.SelectedItem = AboutNavigationItem", window, StringComparison.Ordinal);
        Assert.Contains("https://github.com/OSSBlade/openblade-core/issues", xaml, StringComparison.Ordinal);
        Assert.Contains("https://github.com/OSSBlade/openblade-docs", xaml, StringComparison.Ordinal);
        Assert.Contains("https://github.com/OSSBlade/openblade-core/blob/main/CHANGELOG.md", xaml, StringComparison.Ordinal);
        Assert.Contains("https://github.com/OSSBlade/openblade-core\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/Brand/OpenBlade.png", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE8A7;\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IconActionButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Open\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"View\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnShutdownOpenBlade\"", xaml, StringComparison.Ordinal);
        Assert.Contains("app.ShutdownOpenBladeAsync()", window, StringComparison.Ordinal);
        Assert.Contains("new ElevatedServiceControlLauncher()", app, StringComparison.Ordinal);
        Assert.Contains("app.StartOpenBladeAsync()", window, StringComparison.Ordinal);
        Assert.Contains("StartOpenBladeResult.AlreadyRunning", window, StringComparison.Ordinal);
        Assert.Contains("OpenBlade service is already running", window, StringComparison.Ordinal);
        Assert.Contains("StartOpenBladeProgressRing.IsActive", window, StringComparison.Ordinal);
        Assert.Contains("StartOpenBladeProgressRing.Visibility", window, StringComparison.Ordinal);
        Assert.DoesNotContain("StartOpenBladeProgressRing.Opacity", window, StringComparison.Ordinal);
        Assert.Contains("StartOpenBladeButtonLabel.Opacity", window, StringComparison.Ordinal);
        Assert.Contains("MainWindow_Start_Starting", window, StringComparison.Ordinal);
        Assert.Contains("MainWindow_Start_Button", window, StringComparison.Ordinal);
        Assert.Contains("SessionAgentLauncher.TryStopSibling()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TestedPlatformWarningUsesDashboardPillAndAboutDetails()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"TestedPlatformWarningBadge\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{x:Bind ViewModel.PlatformCompatibilityWarningTitle, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.HasUntestedPlatformVersion, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"OnTestedPlatformWarningClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"DashboardTestedPlatformWarning\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutTestedPlatformWarning\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<InfoBar x:Name=\"AboutTestedPlatformWarning\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource SystemFillColorCautionBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Tested versions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutTestedPlatformCard", xaml, StringComparison.Ordinal);

        Assert.Contains(
            "private async void OnTestedPlatformWarningClick",
            window,
            StringComparison.Ordinal);
        Assert.Contains("ShowTestedPlatformWarningAsync", window, StringComparison.Ordinal);
        Assert.Contains(
            "NavigateToAboutTestedPlatformWarning",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!ViewModel.HasIncompatibleFirmware)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ignore this firmware mismatch and run anyway",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetFirmwareCompatibilityOverrideAsync",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Force run on incompatible firmware\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.HasIncompatibleFirmware, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind ViewModel.AllowUntestedFirmware, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnFirmwareCompatibilityOverrideToggled\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LightingPageOffersCapabilityGatedPresetImportWithConfirmation()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");
        string lightingPage = SliceMethod(
            xaml,
            "<ScrollViewer x:Name=\"LightingPage\"",
            "<ScrollViewer x:Name=\"DisplayPage\"");
        string lightingHeader = SliceMethod(
            lightingPage,
            "<local:ResponsivePageHeader x:Name=\"LightingPageHeader\"",
            "<StackPanel x:Name=\"LightingCardsHost\"");
        string lightingTemplate = SliceMethod(
            xaml,
            "<DataTemplate x:Key=\"DashboardLightingCardTemplate\"",
            "<DataTemplate x:Key=\"DashboardDisplayCardTemplate\"");
        string keyboardSection = SliceMethod(
            lightingTemplate,
            "<StackPanel x:Name=\"LightingKeyboardSection\"",
            "<StackPanel x:Name=\"LightingLogoSection\"");

        Assert.DoesNotContain(
            "Import keyboard lighting preset",
            lightingHeader,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ImportKeyboardLightingPresetCard\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"Import preset\"", keyboardSection, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Import keyboard lighting preset\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource IconActionButtonStyle}\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"OnImportKeyboardLighting\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind LabFeaturesEnabled, Mode=OneWay}\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind CanControlLighting, Mode=OneWay}\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"LightingRefreshRateCard\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Uid=\"MainWindow_TextBlockSixtyHzLighting\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Uid=\"MainWindow_LightingRefreshRateToggleText\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnLightingRefreshRateToggled\"",
            keyboardSection,
            StringComparison.Ordinal);
        Assert.True(
            keyboardSection.IndexOf(
                "x:Name=\"ImportKeyboardLightingPresetCard\"",
                StringComparison.Ordinal)
            < keyboardSection.IndexOf(
                "x:Name=\"LightingRefreshRateCard\"",
                StringComparison.Ordinal));
        Assert.Contains("FileTypeFilter.Add(\".oblight\")", window, StringComparison.Ordinal);
        Assert.Contains("FileTypeFilter.Add(\".chroma\")", window, StringComparison.Ordinal);
        Assert.Contains("FileTypeFilter.Add(\".json\")", window, StringComparison.Ordinal);
        Assert.Contains("ReadBoundedKeyboardLightingFileAsync", window, StringComparison.Ordinal);
        Assert.Contains("MainWindow_LightingImport_ImportApplyButton", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanImportKeyboardLighting", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TryImportKeyboardLighting", window, StringComparison.Ordinal);
        Assert.Contains(
            "if (!ViewModel.LabFeaturesEnabled)",
            SliceMethod(
                window,
                "private async void OnImportKeyboardLighting",
                "private static async Task<byte[]> ReadBoundedKeyboardLightingFileAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LightingAutoAppliesWithDebounceAndHasNoSeparateApplyAction()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.DoesNotContain("Apply lighting", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Apply switch-off behavior", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TimeSpan.FromMilliseconds(500)", window, StringComparison.Ordinal);
        Assert.Contains("ScheduleLightingCommit", window, StringComparison.Ordinal);
        Assert.Contains(
            "CoalescingCommitState<LightingCommitRequest>",
            window,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.SetLightingCommand", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveLightingPowerPolicyCommand", window, StringComparison.Ordinal);
        Assert.DoesNotContain("lightingCommitInProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("chargeLimitCommitInProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("gpuClockOffsetCommitInProgress", window, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingChargeLimitPercent", window, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingGpuClockOffsets", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayCardSwitchesUseCompactRightAlignedPresentation()
    {
        string trayRoot = Path.GetDirectoryName(ProjectPath)!;
        string[] xamlFiles = Directory
            .EnumerateFiles(trayRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(xamlFiles);
        List<(string Path, XElement Toggle)> switches = [];
        foreach (string path in xamlFiles)
        {
            XDocument document = XDocument.Load(path);
            XElement[] fileSwitches = document
                .Descendants()
                .Where(element => element.Name.LocalName == "ToggleSwitch")
                .ToArray();
            if (fileSwitches.Length == 0)
            {
                continue;
            }

            XElement style = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "Style"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key"
                    && attribute.Value == "CompactCardToggleSwitchStyle"));
            Assert.Equal("ToggleSwitch", style.Attribute("TargetType")?.Value);
            Assert.Equal(
                "{StaticResource DefaultToggleSwitchStyle}",
                style.Attribute("BasedOn")?.Value);

            Dictionary<string, string> setters = style
                .Elements()
                .Where(element => element.Name.LocalName == "Setter")
                .ToDictionary(
                    element => element.Attribute("Property")!.Value,
                    element => element.Attribute("Value")!.Value,
                    StringComparer.Ordinal);
            Assert.Equal("Right", setters["HorizontalAlignment"]);
            Assert.Equal("Center", setters["VerticalAlignment"]);
            Assert.Equal("0", setters["MinWidth"]);
            Assert.Equal(string.Empty, setters["OffContent"]);
            Assert.Equal(string.Empty, setters["OnContent"]);

            switches.AddRange(fileSwitches.Select(element => (path, element)));
        }

        Assert.NotEmpty(switches);
        foreach ((string path, XElement toggle) in switches)
        {
            XAttribute? style = toggle.Attribute("Style");
            Assert.True(
                style is not null,
                $"{Path.GetRelativePath(trayRoot, path)} contains an unstyled ToggleSwitch.");
            Assert.Equal("{StaticResource CompactCardToggleSwitchStyle}", style.Value);
        }
    }

    [Fact]
    public void WindowsLightingOwnershipUsesCompactSettingsRow()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        int cardStart = xaml.IndexOf(
            "Text=\"Windows Dynamic Lighting\"",
            StringComparison.Ordinal);
        int cardEnd = xaml.IndexOf(
            "x:Name=\"LightingControls\"",
            cardStart,
            StringComparison.Ordinal);

        Assert.True(cardStart >= 0 && cardEnd > cardStart);
        string card = xaml[cardStart..cardEnd];
        Assert.Contains(
            "Text=\"Use Windows for keyboard effects and brightness.\"",
            card,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Open settings\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NavigateUri=\"ms-settings:personalization-lighting\"",
            card,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WindowsDynamicLightingToggle\"", card, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", card, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Choose the lighting mode for the Razer logo on the lid.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenBlade controls the lid logo, even when Windows controls the keyboard.",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AutomationProperties.HelpText=\"Independent of Windows Dynamic Lighting.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenBlade stops all keyboard-lighting writes",
            card,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Windows Dynamic Lighting interface detected",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Quick effects observed in Synapse",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("M1 through M5 are", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsExposeReorderableTelemetryAndTroubleshootingServiceControl()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("ViewModel.DashboardTelemetryItems", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TrayHoverLabelItems", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsControl Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanDragItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ReorderMode=\"Enabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DestructiveButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemFillColorCriticalBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Shutdown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StopRazerServicesButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnStopRazerServices\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Stop Razer services?", window, StringComparison.Ordinal);
        Assert.Contains("Razer software may start the services again", window, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonStyle = (Style)Root.Resources[\"DestructiveButtonStyle\"]", window, StringComparison.Ordinal);
        Assert.Contains("RazerServiceStopOperation.ActiveServices", window, StringComparison.Ordinal);
        Assert.Contains("ElevatedServiceControlOutcome.Canceled", window, StringComparison.Ordinal);
        Assert.Contains("OpenBlade did not retry any stop request", window, StringComparison.Ordinal);
        Assert.Contains("StopRazerServicesProgressRing.IsActive = isBusy", window, StringComparison.Ordinal);
        Assert.Contains("MainWindow_StopRazer_Checking", window, StringComparison.Ordinal);
        Assert.Contains("MainWindow_StopRazer_Stopping", window, StringComparison.Ordinal);
        Assert.Contains("HandleStopRazerServicesAsync", window, StringComparison.Ordinal);
        Assert.Contains(
            "await RunHandlerAsync(\r\n            HandleStopRazerServicesAsync",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Volatile.Read(ref disposed) != 0 || Root.XamlRoot is not { } xamlRoot",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenBlade could not show the '{0}' dialog",
            window,
            StringComparison.Ordinal);
        Assert.Contains("contentDialogGate.WaitAsync()", window, StringComparison.Ordinal);
        Assert.Contains("contentDialogGate.Release()", window, StringComparison.Ordinal);
        Assert.Contains("dialog.XamlRoot = xamlRoot", window, StringComparison.Ordinal);
        Assert.Contains(
            "Volatile.Read(ref disposed) == 0 && Root.XamlRoot is not null",
            window,
            StringComparison.Ordinal);
        string contentDialog = SliceMethod(
            window,
            "private async Task<ContentDialogResult?> ShowContentDialogAsync",
            "private async Task ShowMessageDialogAsync");
        Assert.Equal(1, CountOccurrences(contentDialog, "await dialog.ShowAsync()"));
        Assert.Contains("#47E10C", app, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x:Name=\"SettingsPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Update channel\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind UpdateViewModel.ChannelOptions, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{x:Bind UpdateViewModel.SelectedChannelOption, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(xaml, "Margin=\"0,0,0,20\""));
        Assert.Contains(
            "IsOn=\"{x:Bind ViewModel.AlwaysOverrideExternalLightingChanges, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnExternalLightingOverridePolicyToggled\"",
            xaml,
            StringComparison.Ordinal);

        int settingsShutdown = xaml.IndexOf(
            "x:Name=\"SettingsShutdownSection\"",
            StringComparison.Ordinal);
        int troubleshooting = xaml.IndexOf(
            "x:Name=\"TroubleshootingExpander\"",
            StringComparison.Ordinal);
        int stopRazerServices = xaml.IndexOf(
            "x:Name=\"StopRazerServicesButton\"",
            StringComparison.Ordinal);
        Assert.True(settingsShutdown >= 0);
        Assert.True(troubleshooting > settingsShutdown);
        Assert.True(stopRazerServices > troubleshooting);

        int dashboardSelector = xaml.IndexOf(
            "Text=\"Shown on Dashboard\"",
            StringComparison.Ordinal);
        int previousSubcard = xaml.LastIndexOf(
            "Style=\"{StaticResource SettingsEmphasizedSubcardStyle}\"",
            dashboardSelector,
            StringComparison.Ordinal);
        int previousRow = xaml.LastIndexOf(
            "Style=\"{StaticResource SettingsRowStyle}\"",
            dashboardSelector,
            StringComparison.Ordinal);
        Assert.True(previousRow > previousSubcard);
    }

    [Fact]
    public void DashboardCustomizerUsesTheSameNativeListViewReorderingAsTaskbarHover()
    {
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("new ObservableCollection<DashboardCardOption>()", window, StringComparison.Ordinal);
        Assert.Contains("CreateCustomizationDialog(", window, StringComparison.Ordinal);
        Assert.Contains("var reorderList = new Microsoft.UI.Xaml.Controls.ListView", window, StringComparison.Ordinal);
        Assert.Contains("CanDragItems = true", window, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems = true", window, StringComparison.Ordinal);
        Assert.Contains("ReorderMode = ListViewReorderMode.Enabled", window, StringComparison.Ordinal);
        Assert.Contains("AllowDrop = true", window, StringComparison.Ordinal);
        Assert.Contains("DashboardCardOptionTemplate", window, StringComparison.Ordinal);
        Assert.Contains("ApplyDashboardCustomizerChanges()", window, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch", window, StringComparison.Ordinal);
        Assert.Contains("content = new StackPanel { Width = 600", window, StringComparison.Ordinal);
        Assert.Contains("MinWidth = 640", window, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Translation = new System.Numerics.Vector3(80, 0, 0)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("grip.PointerMoved +=", window, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DashboardCardOptionTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"580\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("<CheckBox Grid.Column=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshDashboardCards();", window, StringComparison.Ordinal);
        Assert.Contains("Drag them into the order you want", window, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDashboardOrderButton", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservableCollection<Grid>", window, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardCustomizationPreservesTheOriginalStatusCardWithoutRestarting()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("CreateDashboardLiveCard(option)", window, StringComparison.Ordinal);
        Assert.Contains("DashboardPowerModeCardTemplate", window, StringComparison.Ordinal);
        Assert.Contains("DashboardFansCardTemplate", window, StringComparison.Ordinal);
        Assert.Contains("DashboardBatteryCardTemplate", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Content = \"Open\"", window, StringComparison.Ordinal);
        Assert.Contains("IAsyncDisposable, INotifyPropertyChanged", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DisplayViewModel.PropertyChanged += OnDisplayViewModelPropertyChanged",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new PropertyChangedEventArgs(nameof(ViewModel))",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new PropertyChangedEventArgs(nameof(DisplayViewModel))",
            window,
            StringComparison.Ordinal);
        Assert.Contains("dashboardCardPresentations", window, StringComparison.Ordinal);
        Assert.Contains("? DeviceStatusCard", window, StringComparison.Ordinal);
        Assert.Contains("DashboardCardHost.Children.Move", window, StringComparison.Ordinal);
        Assert.Contains("RefreshDashboardCards();", window, StringComparison.Ordinal);
        Assert.Contains("MoveCanonicalLaptopCardsIntoPlace();", window, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteDashboardArrangement", window, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardLayoutRestartRequested", window, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardLayoutRestartRequested", app, StringComparison.Ordinal);
        Assert.DoesNotContain("--restart-after=", app, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForDashboardRestartParent", app, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReusableDashboardCardBindsDirectlyToItsViewModel()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string[] mainViewModelTemplates =
        [
            "DashboardPowerModeCardTemplate",
            "DashboardFansCardTemplate",
            "DashboardBatteryCardTemplate",
            "DashboardKeyboardBehaviorCardTemplate",
            "DashboardStartupAnimationCardTemplate",
            "DashboardLightingCardTemplate",
        ];

        foreach (string templateKey in mainViewModelTemplates)
        {
            string template = SliceMethod(
                xaml,
                $"<DataTemplate x:Key=\"{templateKey}\"",
                "</DataTemplate>");
            Assert.Contains("x:DataType=\"local:MainViewModel\"", template, StringComparison.Ordinal);
            Assert.DoesNotContain("x:Bind ViewModel.", template, StringComparison.Ordinal);
            Assert.DoesNotContain("DataContext=\"{x:Bind ViewModel", template, StringComparison.Ordinal);
        }

        string displayTemplate = SliceMethod(
            xaml,
            "<DataTemplate x:Key=\"DashboardDisplayCardTemplate\"",
            "</DataTemplate>");
        Assert.Contains(
            "x:DataType=\"local:DisplaySettingsViewModel\"",
            displayTemplate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("x:Bind DisplayViewModel.", displayTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext=\"{x:Bind ViewModel", displayTemplate, StringComparison.Ordinal);

        string factory = SliceMethod(
            window,
            "private FrameworkElement CreateDashboardLiveCard",
            "private void MoveCanonicalLaptopCardsIntoPlace");
        Assert.Contains("? DisplayViewModel", factory, StringComparison.Ordinal);
        Assert.Contains(": ViewModel", factory, StringComparison.Ordinal);
        Assert.Contains("Content = content", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("Content = this", factory, StringComparison.Ordinal);

        foreach (string cardName in new[]
        {
            "PowerModeCard",
            "FansCard",
            "BatteryCard",
            "KeyboardBehaviorCard",
            "StartupAnimationCard",
            "LightingCard",
        })
        {
            Assert.Contains(
                $"x:Name=\"{cardName}\"\n                                        Content=\"{{x:Bind ViewModel}}\"",
                xaml.Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "x:Name=\"DisplayCard\"\n                                        Content=\"{x:Bind DisplayViewModel}\"",
            xaml.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionPublishRemainsSelfContainedWithoutLooseSetupRuntimeOrSymbols()
    {
        string publish = ReadRepositoryFile("build", "Publish.ps1");
        string validation = ReadRepositoryFile("build", "PublishPayloadValidation.ps1");
        string serviceProject = ReadRepositoryFile("src", "OpenBlade.Service", "OpenBlade.Service.csproj");

        Assert.Contains("'--self-contained', 'true'", publish, StringComparison.Ordinal);
        Assert.Contains("'--property:PublishTrimmed=false'", publish, StringComparison.Ordinal);
        Assert.Contains("'--property:DebugType=None'", publish, StringComparison.Ordinal);
        Assert.Contains("'--property:DebugSymbols=false'", publish, StringComparison.Ordinal);
        Assert.Contains(
            "'--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU'",
            publish,
            StringComparison.Ordinal);
        Assert.Contains("'--property:PublishSingleFile=true'", publish, StringComparison.Ordinal);
        Assert.Contains("'--property:EnableCompressionInSingleFile=true'", publish, StringComparison.Ordinal);
        Assert.Contains(
            "'src\\OpenBlade.Capture\\OpenBlade.Capture.csproj') `\n        $stagingDeveloperTools $productionPublishArguments",
            publish.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("Assert-ProductionBundleFootprint", publish, StringComparison.Ordinal);
        Assert.Contains("OpenBlade setup must publish as one compressed executable", validation, StringComparison.Ordinal);
        Assert.Contains("must not pre-copy Setup into the payload", validation, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.AI.", validation, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.Widgets", validation, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", validation, StringComparison.Ordinal);
        Assert.Contains("CopyToPublishDirectory=\"Never\"", serviceProject, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing marker: {endMarker}");
        return source[start..end];
    }

    [Fact]
    public void DashboardTelemetryUsesResponsiveSingleRowWithoutClipping()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string heroReadings = SliceMethod(
            xaml,
            "<Border x:Name=\"HeroReadings\"",
            ">");

        Assert.Contains("x:Name=\"HeroReadings\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"0,14,0,0\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Margin=\"0,18,0,0\"",
            heroReadings,
            StringComparison.Ordinal);
        Assert.Contains("<UniformGridLayout Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinItemWidth=\"100\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinColumnSpacing=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaximumRowsOrColumns=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsStretch=\"Fill\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<StackPanel Spacing=\"3\">", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackLayout Orientation=\"Horizontal\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"220\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardTelemetryPreferencesUseAConstrainedFullWidthRow()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.ColumnSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemWidth=\"190\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileComparisonIncludesCurrentLightingAndPowerPolicySettings()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("Laptop lid logo", xaml, StringComparison.Ordinal);
        Assert.Contains("PluggedInProfilePresentation.LightingDisplayOff", xaml, StringComparison.Ordinal);
        Assert.Contains("UsbCProfilePresentation.LightingDisplayOff", xaml, StringComparison.Ordinal);
        Assert.Contains("BatteryProfilePresentation.LightingDisplayOff", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedProfilePresentation.LightingIdle", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardStatusPillsShareOneNeutralContainerStyle()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Equal(3, CountOccurrences(
            xaml,
            "Style=\"{StaticResource StatusPillButtonStyle}\""));
        string warningBadge = SliceMethod(
            xaml,
            "x:Name=\"TestedPlatformWarningBadge\"",
            ">");
        Assert.Contains(
            "Style=\"{StaticResource StatusPillButtonStyle}\"",
            warningBadge,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorSecondaryBrush}\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Background=\"{ThemeResource SystemFillColorCautionBackgroundBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Foreground\" Value=\"{ThemeResource TextFillColorPrimaryBrush}\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Visibility=\"{x:Bind ViewModel.HasActiveProfileDraft, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Fill=\"{ThemeResource AccentFillColorDefaultBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "<ProgressRing Width=\"12\" Height=\"12\""));
    }

    [Fact]
    public void ExternalChangeFlyoutOffersTheScopedManualRecoveryAction()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");

        Assert.Contains(
            "AutomationProperties.Name=\"Reapply OpenBlade settings\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind ViewModel.CanReapplyChangedSettings, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.ShowReapplyChangedSettings, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("OnReapplyChangedSettingsClick", window, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.ReapplyChangedSettingsAsync(CancellationToken.None)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Check current state\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.HasUnconfirmedWrites, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("OnCheckCurrentStateClick", window, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.CheckCurrentStateAsync(CancellationToken.None)",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AttentionFlyoutIsDismissedWhenAttentionClears()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs").Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("x:Name=\"AttentionFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "args.PropertyName == nameof(MainViewModel.HasAttention)\n"
                + "            && !ViewModel.HasAttention",
            window,
            StringComparison.Ordinal);
        Assert.Contains("AttentionFlyout.Hide()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void StableComboBoxSelection_ReplaysTheViewModelAnchorAfterSourceChanges()
    {
        string source = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "StableComboBoxSelection.cs");

        Assert.Contains("ItemsControl.ItemsSourceProperty", source, StringComparison.Ordinal);
        Assert.Contains("comboBox.SelectionChanged += OnSelectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("comboBox.Loaded += OnLoaded", source, StringComparison.Ordinal);
        Assert.Contains("QueueReconcile(comboBox, state)", source, StringComparison.Ordinal);
        Assert.Contains("TryFindEquivalentItem", source, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(item, anchor) || Equals(item, anchor)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveProfileAndNestedControlStylesHaveSingleSemanticRoles()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Equal(4, CountOccurrences(xaml, "Style=\"{StaticResource ActiveProfilePillStyle}\""));
        Assert.Equal(4, CountOccurrences(xaml, "Style=\"{StaticResource ActiveProfilePillTextStyle}\""));
        Assert.Contains("Text=\"Battery\" Style=\"{StaticResource CardTitleStyle}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Laptop performance\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Windows power mode\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(xaml, "Style=\"{StaticResource SettingsEmphasizedSubcardStyle}\""));
        Assert.Equal(2, CountOccurrences(xaml, "Style=\"{StaticResource SettingsLastEmphasizedSubcardStyle}\""));
        Assert.Equal(6, CountOccurrences(xaml, "Style=\"{StaticResource SettingsNeutralSubcardStyle}\""));
        Assert.Equal(1, CountOccurrences(xaml, "Style=\"{StaticResource SettingsLastNeutralSubcardStyle}\""));
        Assert.Equal(2, CountOccurrences(xaml, "Style=\"{StaticResource SettingsLastEditorSubcardStyle}\""));
        Assert.DoesNotContain("x:Key=\"SettingsSubcardStyle\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"SettingsLastSubcardStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0,1,0,0\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"Margin\" Value=\"16,0,16,14\" />", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"On battery\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactiveRawInputBelongsToAlwaysRunningSessionAgent()
    {
        string source = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "WindowsReactiveKeyboardSource.cs");
        string agent = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("MessageOnlyWindow", source, StringComparison.Ordinal);
        Assert.Contains("RawInputDeviceInputSink", source, StringComparison.Ordinal);
        Assert.Contains("RawInputDeviceNotify", source, StringComparison.Ordinal);
        Assert.Contains("WindowMessageInputDeviceChange", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveInputDevice", source, StringComparison.Ordinal);
        Assert.Contains("RazerBladeReactiveInputDevice", source, StringComparison.Ordinal);
        Assert.Contains("GetRawInputHeader", source, StringComparison.Ordinal);
        Assert.Contains("EnsureRawInputBuffer", source, StringComparison.Ordinal);
        Assert.Contains("stackalloc byte[3]", source, StringComparison.Ordinal);
        Assert.Contains("readSpecialKeyInputAdmitted", source, StringComparison.Ordinal);
        Assert.Contains(
            "readSpecialKeyInputAdmitted(specialKey)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Rz09_0528PerformanceShortcutRawHidInput.TryCreateFunctionPTrigger",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VirtualKeyPageUp",
            source,
            StringComparison.Ordinal);
        Assert.Contains("header.Type != RawInputTypeKeyboard", source, StringComparison.Ordinal);
        Assert.Contains("&& !targetDevice", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("hookHealth.Consume(", StringComparison.Ordinal)
                < source.IndexOf("EvaluateTargetPacket(", StringComparison.Ordinal),
            "Every keyboard packet must drain hook correlation before target recovery is evaluated.");
        Assert.DoesNotContain(
            "nint buffer = Marshal.AllocHGlobal(checked((int)size))",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("header.Type != RawInputTypeKeyboard", StringComparison.Ordinal)
                < source.IndexOf("EnsureRawInputBuffer(size)", StringComparison.Ordinal),
            "Non-target non-keyboard packets must be rejected before the full packet buffer is acquired.");
        Assert.DoesNotContain("GetForegroundWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rawInput.Keyboard.VirtualKey", source, StringComparison.Ordinal);
        Assert.Contains("ServiceReactiveLightingLoop", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("ReactiveKeyPulsePump", window, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsRawKeyboardSource", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedWindowInputDoesNotShipAUiAccessBroker()
    {
        string sessionProgram = ReadRepositoryFile(
            "src",
            "OpenBlade.SessionAgent",
            "Program.cs");
        string publish = ReadRepositoryFile("build", "Publish.ps1");
        string validation = ReadRepositoryFile(
            "build",
            "PublishPayloadValidation.ps1");

        Assert.Contains("WindowsReactiveKeyboardSourceFactory.Instance", sessionProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("UiAccessInputKeyboardSourceFactory", sessionProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenBlade.InputAgent.csproj", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("UiAccessSigningCertificateThumbprint", publish, StringComparison.Ordinal);
        Assert.Contains("SigningThumbprint", publish, StringComparison.Ordinal);
        Assert.Contains("Invoke-OpenBladeSigning", publish, StringComparison.Ordinal);
        Assert.Contains("must not contain the abandoned UIAccess input agent", validation, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPath(
            "src",
            "OpenBlade.InputAgent",
            "OpenBlade.InputAgent.csproj")));
        Assert.False(File.Exists(RepositoryPath(
            "tests",
            "OpenBlade.InputAgent.Tests",
            "OpenBlade.InputAgent.Tests.csproj")));
    }

    [Fact]
    public void SelfContainedPublishRequiresAndCopiesWinUiCompiledResources()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");

        Assert.Contains("Name=\"PublishApplicationResourceIndex\"", project, StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"Publish\"", project, StringComparison.Ordinal);
        Assert.Contains("$(TargetDir)$(AssemblyName).pri", project, StringComparison.Ordinal);
        Assert.Contains("$(PublishDir)$(AssemblyName).pri", project, StringComparison.Ordinal);
        Assert.Contains("<OpenBladeCompiledXaml Include=\"$(TargetDir)*.xbf\" />", project, StringComparison.Ordinal);
        Assert.Contains("Condition=\"'@(OpenBladeCompiledXaml)' == ''\"", project, StringComparison.Ordinal);
        Assert.Contains("SourceFiles=\"@(OpenBladeCompiledXaml)\"", project, StringComparison.Ordinal);
        Assert.Contains("DestinationFolder=\"$(PublishDir)\"", project, StringComparison.Ordinal);
        Assert.Contains("<Error", project, StringComparison.Ordinal);
        Assert.Contains("!Exists('$(TargetDir)$(AssemblyName).pri')", project, StringComparison.Ordinal);
        Assert.Contains("<Copy", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayExecutableIsPinnedUnpackagedSelfContainedWinUiAndContainsNoWpf()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");
        string packageVersions = ReadRepositoryFile("Directory.Packages.props");
        Assert.Contains("<OutputType>WinExe</OutputType>", project, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<SupportedOSPlatformVersion>10.0.26100.0</SupportedOSPlatformVersion>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWinUI>true</UseWinUI>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsPackageType>None</WindowsPackageType>", project, StringComparison.Ordinal);
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", project, StringComparison.Ordinal);
        Assert.Contains("<Platforms>x64</Platforms>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", project, StringComparison.Ordinal);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon Condition=\"Exists('Assets\\Brand\\OpenBlade.ico')\">", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Brand\\**\\*.ico\" Condition=\"Exists('Assets\\Brand')", project, StringComparison.Ordinal);
        Assert.Contains("<Content Update=\"Assets\\Brand\\OpenBlade.png\">", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.WinUI\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Microsoft.WindowsAppSDK.Runtime\" />", project, StringComparison.Ordinal);
        Assert.Contains(
            "<PackageVersion Include=\"Microsoft.WindowsAppSDK.WinUI\" Version=\"[2.2.1]\" />",
            packageVersions,
            StringComparison.Ordinal);
        Assert.Contains(
            "<PackageVersion Include=\"Microsoft.WindowsAppSDK.Runtime\" Version=\"[2.2.0]\" />",
            packageVersions,
            StringComparison.Ordinal);
        Assert.Contains(
            "The umbrella package also deploys",
            packageVersions,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Microsoft.WindowsAppSDK\"", project, StringComparison.Ordinal);
        Assert.Contains("<ImportFrameworkWinFXTargets>true</ImportFrameworkWinFXTargets>", project, StringComparison.Ordinal);
        Assert.Contains("prevents the Windows Desktop SDK from sending WinUI XAML through the WPF compiler", project, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWPF", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationCore", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.WindowsDesktop.App.WPF", project, StringComparison.OrdinalIgnoreCase);

        string manifest = ReadRepositoryFile("src", "OpenBlade.Tray", "app.manifest");
        Assert.Contains("<supportedOS Id=\"{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}\" />", manifest, StringComparison.Ordinal);
        Assert.Contains("<maxversiontested Id=\"10.0.26100.0\" />", manifest, StringComparison.Ordinal);
        Assert.Contains(">PerMonitorV2</dpiAwareness>", manifest, StringComparison.Ordinal);
        Assert.Contains(">true</longPathAware>", manifest, StringComparison.Ordinal);

        string trayRoot = Path.GetDirectoryName(ProjectPath)!;
        foreach (string file in Directory.EnumerateFiles(trayRoot, "*", SearchOption.AllDirectories)
                     .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml"))
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("using System.Windows;", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.Controls", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DynamicResource", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Wpf", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WinUiSpecificAdaptersPreserveDispatchAndNotificationAreaContracts()
    {
        string dispatcher = ReadRepositoryFile("src", "OpenBlade.Tray", "WinUiDispatcher.cs");
        Assert.Contains("dispatcherQueue.HasThreadAccess", dispatcher, StringComparison.Ordinal);
        Assert.Contains("token.ThrowIfCancellationRequested()", dispatcher, StringComparison.Ordinal);
        Assert.Contains("dispatcherQueue.TryEnqueue", dispatcher, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetCanceled", dispatcher, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetException(exception)", dispatcher, StringComparison.Ordinal);

        string trayHost = ReadRepositoryFile("src", "OpenBlade.Tray", "WinFormsTrayIconHost.cs");
        Assert.Contains("TrayIconMenu_Open", trayHost, StringComparison.Ordinal);
        Assert.Contains("TrayIconMenu_Exit", trayHost, StringComparison.Ordinal);
        Assert.Contains("menu.Items.Add(new ToolStripSeparator())", trayHost, StringComparison.Ordinal);
        Assert.Contains(
            "trayIcon.MouseDoubleClick += OnTrayIconMouseDoubleClick",
            trayHost,
            StringComparison.Ordinal);
        Assert.Contains(
            "trayIcon.MouseDoubleClick -= OnTrayIconMouseDoubleClick",
            trayHost,
            StringComparison.Ordinal);
        Assert.Contains("viewModel.TrayHoverLabelText", trayHost, StringComparison.Ordinal);
        Assert.Contains("viewModel.PropertyChanged +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("viewModel.PropertyChanged -=", trayHost, StringComparison.Ordinal);
        Assert.Contains("nameof(MainViewModel.TrayIconState)", trayHost, StringComparison.Ordinal);
        Assert.Contains("OpenBladeIconAssets.LoadTrayIcon", trayHost, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref disposed, 1)", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Visible = false", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Dispose()", trayHost, StringComparison.Ordinal);
        Assert.Contains("notificationPresenter?.TryShow(notification)", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Icon = replacement", trayHost, StringComparison.Ordinal);
        Assert.Contains("ToolTipIcon.None", trayHost, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTipIcon.Info", trayHost, StringComparison.Ordinal);
        Assert.DoesNotContain("notificationIconRestoreTimer", trayHost, StringComparison.Ordinal);
        Assert.DoesNotContain("trayIcon.Icon = notificationIcon", trayHost, StringComparison.Ordinal);
        Assert.Contains("menu.Dispose()", trayHost, StringComparison.Ordinal);

        string notificationPresenter = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WindowsAppNotificationPresenter.cs");
        Assert.Contains("AppNotificationManager.Default", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("manager.Register()", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("builder.SetAppLogoOverride(logoUri)", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("builder.SetTag(notification.ReplacementTag)", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains(".AddArgument(\"destination\"", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("notificationInvoked(destination)", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("Enum.IsDefined(parsed)", notificationPresenter, StringComparison.Ordinal);
        Assert.Contains("\"OpenBlade.png\"", notificationPresenter, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("OpenBladeIconAssets.ApplicationIconPath is { } applicationIconPath", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon(applicationIconPath)", window, StringComparison.Ordinal);
        Assert.Contains(
            "args.SelectedItem is not FanModeOption selected",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "args.AddedItems.FirstOrDefault() is not FanModeOption selected",
            window,
            StringComparison.Ordinal);

        string shellThemeSource = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WindowsShellThemeSource.cs");
        Assert.Contains("SystemUsesLightTheme", shellThemeSource, StringComparison.Ordinal);
        Assert.Contains("ShellThemeSelectionPolicy.ShouldUseLightTrayIconGlyphs", shellThemeSource, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.UserPreferenceChanged +=", shellThemeSource, StringComparison.Ordinal);
        Assert.Contains("dispatcherQueue.TryEnqueue(RefreshTheme)", shellThemeSource, StringComparison.Ordinal);
        Assert.Contains("TrayIconThemeChanged?.Invoke", shellThemeSource, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.UserPreferenceChanged -=", shellThemeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer()", shellThemeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceNotificationsShareAReplacementTag()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains(
            "PerformanceNotificationReplacementTag = \"performance\"",
            app,
            StringComparison.Ordinal);

        int failureStart = app.IndexOf(
            "\"Performance mode could not be changed\"",
            StringComparison.Ordinal);
        int successStart = app.IndexOf(
            "\"Performance mode changed\"",
            StringComparison.Ordinal);
        int showNotification = app.IndexOf(
            "trayIconHost.ShowNotification(notification)",
            StringComparison.Ordinal);
        Assert.True(
            failureStart >= 0 && successStart > failureStart && showNotification > successStart,
            "The performance notification branches must remain discoverable.");

        Assert.Contains(
            "ReplacementTag: PerformanceNotificationReplacementTag",
            app[failureStart..successStart],
            StringComparison.Ordinal);
        Assert.Contains(
            "ReplacementTag: PerformanceNotificationReplacementTag",
            app[successStart..showNotification],
            StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationSettingsKeepDurableFailureStatusIndependentFromToasts()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("Notification preferences", xaml, StringComparison.Ordinal);
        Assert.Contains("AppUpdateNotificationsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("FirmwareUpdateNotificationsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("ControllerAdvisoryNotificationsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("OperationFailureNotificationsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TroubleshootingRecentErrorStatus", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ControllerAdvisoryText", window, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedUpdateNotificationDeliveryDoesNotConsumeThePersistedKey()
    {
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int appHandlerStart = window.IndexOf(
            "private void OnAppUpdateAvailable(",
            StringComparison.Ordinal);
        int firmwareHandlerStart = window.IndexOf(
            "private void OnFirmwareUpdateAvailable(",
            appHandlerStart,
            StringComparison.Ordinal);
        int preferencesStart = window.IndexOf(
            "private void ApplyNotificationPreferences(",
            firmwareHandlerStart,
            StringComparison.Ordinal);
        Assert.True(
            appHandlerStart >= 0
                && firmwareHandlerStart > appHandlerStart
                && preferencesStart > firmwareHandlerStart,
            "The update notification handlers must remain discoverable.");

        string appHandler = window[appHandlerStart..firmwareHandlerStart];
        string firmwareHandler = window[firmwareHandlerStart..preferencesStart];
        Assert.Contains("if (!TryShowNotification(", appHandler, StringComparison.Ordinal);
        Assert.Contains("if (!TryShowNotification(", firmwareHandler, StringComparison.Ordinal);
        Assert.True(
            appHandler.IndexOf("lastNotifiedReleaseTag =", StringComparison.Ordinal)
                > appHandler.IndexOf("if (!TryShowNotification(", StringComparison.Ordinal));
        Assert.True(
            firmwareHandler.LastIndexOf(
                "lastNotifiedFirmwareSignature =",
                StringComparison.Ordinal)
                > firmwareHandler.IndexOf("if (!TryShowNotification(", StringComparison.Ordinal));
        Assert.Contains("Func<TrayNotification, bool>", window, StringComparison.Ordinal);

        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains("if (host is null)", app, StringComparison.Ordinal);
        Assert.Contains("return false;", app, StringComparison.Ordinal);
        Assert.Contains("return true;", app, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareNotificationUsesBehavioralLegacyMigrationGuard()
    {
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int handlerStart = window.IndexOf(
            "private void OnFirmwareUpdateAvailable(",
            StringComparison.Ordinal);
        int handlerEnd = window.IndexOf(
            "private void ApplyNotificationPreferences(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);

        string handler = window[handlerStart..handlerEnd];
        int legacyMatch = handler.IndexOf(
            "args.CanMigrateLegacySignature(lastNotifiedFirmwareSignature)",
            StringComparison.Ordinal);
        int migration = handler.IndexOf(
            "lastNotifiedFirmwareSignature = args.Signature;",
            legacyMatch,
            StringComparison.Ordinal);
        int migrationSave = handler.IndexOf(
            "QueuePreferenceSave();",
            migration,
            StringComparison.Ordinal);
        int notification = handler.IndexOf("if (!TryShowNotification(", StringComparison.Ordinal);

        Assert.True(
            legacyMatch >= 0
                && migration > legacyMatch
                && migrationSave > migration
                && notification > migrationSave,
            "A legacy signature must migrate and return before notification delivery.");
        Assert.Contains(
            "return;",
            handler[migrationSave..notification],
            StringComparison.Ordinal);
    }

    [Fact]
    public void WinUiAppRoutesEveryExitSourceThroughLifecycleAndGuardsActivation()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains("exitLifecycle.ExitAsync()", app, StringComparison.Ordinal);
        Assert.Contains("exitLifecycle.TryRunWhileActive", app, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceError", app, StringComparison.Ordinal);
        Assert.Contains("RequestTrayExitAsync()", app, StringComparison.Ordinal);
        Assert.Contains("Keep background features running", app, StringComparison.Ordinal);
        Assert.Contains("Shut down completely", app, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Forms.Application.EnableVisualStyles()", app, StringComparison.Ordinal);
        Assert.Contains("ShowTrayTaskDialog", app, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(window)", app, StringComparison.Ordinal);
        Assert.Contains("service shutdown elevation was canceled", app, StringComparison.Ordinal);
        string compactApp = string.Concat(app.Where(character => !char.IsWhiteSpace(character)));
        Assert.Contains(
            "(_,_)=>Enqueue(()=>_=ExitTrayAsync())",
            compactApp,
            StringComparison.Ordinal);
        Assert.Contains(
            "newTrayExitController(ShowTrayExitDialog,ExitTrayAsync,ShutdownOpenBladeAsync);",
            compactApp,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsServiceStateConfirmation.WaitForStoppedAsync,()=>_=SessionAgentLauncher.TryStopSibling(),ExitTrayAsync);",
            compactApp,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedServiceHelpersRunBeforeTrayAndSessionInitialization()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        int helperGate = app.IndexOf(
            "ElevatedServiceControlLauncher.ParseHelperAction",
            StringComparison.Ordinal);
        int helperBranch = app.IndexOf("if (elevatedServiceHelper)", StringComparison.Ordinal);
        int helperCall = app.IndexOf(
            "_ = RunElevatedServiceControlHelperAsync(helperAction);",
            helperBranch,
            StringComparison.Ordinal);
        int helperReturn = app.IndexOf("return;", helperCall, StringComparison.Ordinal);
        int invalidHelperBranch = app.IndexOf(
            "if (helperParseResult == ElevatedServiceHelperParseResult.Invalid)",
            StringComparison.Ordinal);
        int invalidHelperExit = app.IndexOf("Exit();", invalidHelperBranch, StringComparison.Ordinal);
        int invalidHelperReturn = app.IndexOf(
            "return;",
            invalidHelperExit,
            StringComparison.Ordinal);
        int normalInitialization = app.IndexOf(
            "System.Windows.Forms.Application.EnableVisualStyles()",
            StringComparison.Ordinal);
        int instanceClaim = app.IndexOf(
            "TrayInstanceCoordinator.TryStart",
            StringComparison.Ordinal);
        int windowCreation = app.IndexOf("CreateMainWindow(", StringComparison.Ordinal);
        int sessionAgentStart = app.IndexOf("StartSessionAgent();", StringComparison.Ordinal);

        Assert.True(helperGate >= 0);
        Assert.True(helperGate < helperBranch);
        Assert.True(helperBranch < helperCall);
        Assert.True(helperCall < helperReturn);
        Assert.True(helperReturn < normalInitialization);
        Assert.True(invalidHelperBranch >= 0);
        Assert.True(invalidHelperBranch < invalidHelperExit);
        Assert.True(invalidHelperExit < invalidHelperReturn);
        Assert.True(invalidHelperReturn < normalInitialization);
        Assert.Contains("ElevatedServiceHelperParseResult.Invalid", app, StringComparison.Ordinal);
        Assert.Contains("rejected malformed elevated service helper arguments", app, StringComparison.Ordinal);
        Assert.True(normalInitialization < instanceClaim);
        Assert.True(normalInitialization < windowCreation);
        Assert.True(normalInitialization < sessionAgentStart);
        Assert.Contains("new ServiceClient()", app, StringComparison.Ordinal);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(10))", app, StringComparison.Ordinal);
        Assert.Contains("serviceClient.ShutdownOpenBladeAsync", app, StringComparison.Ordinal);
        Assert.Contains("WindowsServiceStartOperation.StartAsync", app, StringComparison.Ordinal);
        Assert.Contains("RazerServiceStopOperation.StopAllAsync", app, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.Tray.StopRazerServicesHelper", app, StringComparison.Ordinal);
        Assert.Contains("RazerServiceStopHardExitTimeout", app, StringComparison.Ordinal);
        Assert.Contains("_ => Environment.Exit(1)", app, StringComparison.Ordinal);
        Assert.Contains("new CancellationTokenSource(TimeSpan.FromSeconds(65))", app, StringComparison.Ordinal);
        string helper = SliceMethod(
            app,
            "private async Task RunElevatedServiceControlHelperAsync(",
            "private async Task RequestTrayExitAsync()");
        string compactHelper = string.Concat(
            helper.Where(character => !char.IsWhiteSpace(character)));
        Assert.Contains(
            "awaitTask.Run(()=>WindowsServiceStartOperation.StartAsync(timeout.Token),timeout.Token);",
            compactHelper,
            StringComparison.Ordinal);
        Assert.Contains("new TrayServiceStartCoordinator", app, StringComparison.Ordinal);
        Assert.Contains("WindowsServiceStateConfirmation.WaitForRunningAsync", app, StringComparison.Ordinal);
        Assert.Contains("TrayServiceStartResult.Unconfirmed", app, StringComparison.Ordinal);
        Assert.Contains("new TrayCompleteShutdownCoordinator", app, StringComparison.Ordinal);
        Assert.Contains("WindowsServiceStateConfirmation.WaitForStoppedAsync", app, StringComparison.Ordinal);
        Assert.Contains("TrayCompleteShutdownResult.Unconfirmed", app, StringComparison.Ordinal);
        Assert.Contains("did not retry the request", app, StringComparison.Ordinal);
        string launcher = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "ElevatedServiceControlLauncher.cs");
        int taskRun = launcher.IndexOf("await Task.Run(", StringComparison.Ordinal);
        int processStart = launcher.IndexOf("Process.Start(startInfo)", StringComparison.Ordinal);
        int waitForExit = launcher.IndexOf("WaitForExitAsync", StringComparison.Ordinal);
        Assert.True(taskRun >= 0);
        Assert.True(taskRun < processStart);
        Assert.True(processStart < waitForExit);
        Assert.DoesNotContain(
            "window.ViewModel.ShutdownOpenBladeAsync",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeThemeUsesRootRequestedThemeWithoutLegacyThemeWatchers()
    {
        string themeManager = ReadRepositoryFile("src", "OpenBlade.Tray", "ThemeManager.cs");
        string appXaml = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml");
        Assert.Contains("ThemeSelectionPolicy.Resolve", themeManager, StringComparison.Ordinal);
        Assert.Contains("root.RequestedTheme", themeManager, StringComparison.Ordinal);

        string windowXaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        Assert.Contains(
            "Background=\"Transparent\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"NavigationViewDefaultPaneBackground\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"NavigationViewContentBackground\" Color=\"Transparent\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"NavigationViewExpandedPaneBackground\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"NavigationViewSelectionIndicatorForeground\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains("MinimumWidthDip = 800", ReadRepositoryFile(
            "src", "OpenBlade.Tray", "MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("MinimumHeightDip = 600", ReadRepositoryFile(
            "src", "OpenBlade.Tray", "MainWindow.xaml.cs"), StringComparison.Ordinal);

        Assert.DoesNotContain("RequestedTheme", appXaml, StringComparison.Ordinal);

        string trayRoot = Path.GetDirectoryName(ProjectPath)!;
        foreach (string file in Directory.EnumerateFiles(trayRoot, "*", SearchOption.AllDirectories)
                     .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml"))
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string source = File.ReadAllText(file);
            bool isRegistryReader = file.EndsWith(
                "WindowsRegistryReader.cs",
                StringComparison.OrdinalIgnoreCase);
            bool isShellThemeSource = file.EndsWith(
                "WindowsShellThemeSource.cs",
                StringComparison.OrdinalIgnoreCase);
            bool isTextScaleConsumer = file.EndsWith(
                "MainWindow.xaml.cs",
                StringComparison.OrdinalIgnoreCase);
            if (!isRegistryReader && !isShellThemeSource)
            {
                Assert.DoesNotContain(
                    "Microsoft.Win32",
                    source,
                    StringComparison.Ordinal);
            }
            if (!isShellThemeSource)
            {
                Assert.DoesNotContain("SystemEvents", source, StringComparison.Ordinal);
            }
            bool isAccentColorManager = file.EndsWith(
                "AccentColorManager.cs",
                StringComparison.OrdinalIgnoreCase);
            if (!isAccentColorManager && !isTextScaleConsumer)
            {
                Assert.DoesNotContain("UISettings", source, StringComparison.Ordinal);
            }
            if (!isAccentColorManager)
            {
                Assert.DoesNotContain("ColorValuesChanged", source, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("Application.RequestedTheme", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PersonalizationSupportsAnimatedDashboardNavigationOrderAndAccentColor()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string accentManager = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "AccentColorManager.cs");
        Assert.Contains("<AddDeleteThemeTransition", xaml, StringComparison.Ordinal);
        Assert.Contains("<RepositionThemeTransition", xaml, StringComparison.Ordinal);
        Assert.Contains("dashboardCardPresentations", window, StringComparison.Ordinal);
        Assert.Contains("DashboardCardHost.Children.Move", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavigationMenuExpander\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind PrimaryNavigationItems, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Navigation menu pages\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnPrimaryNavigationItemsCollectionChanged", window, StringComparison.Ordinal);
        Assert.Contains("changingPrimaryNavigationOrder", window, StringComparison.Ordinal);
        Assert.Contains("ApplyNavigationDestination(destination)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCustomizeNavigation", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = \"Customize navigation\"", window, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskbarHoverLabelExpander\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "IsExpanded=\"False\""));
        Assert.Contains("App accent color", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenBlade green", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Razer green", window, StringComparison.Ordinal);
        Assert.DoesNotContain("OnResetAccentColor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnResetAccentColor", window, StringComparison.Ordinal);
        Assert.Contains("Windows accent", window, StringComparison.Ordinal);
        Assert.Contains("ColorValuesChanged +=", accentManager, StringComparison.Ordinal);
        Assert.Contains("existing.Color = color", accentManager, StringComparison.Ordinal);
        Assert.Contains("GetAccentResourceDictionaries(applicationResources)", accentManager,
            StringComparison.Ordinal);
        Assert.Contains("ApplyBrushPalette(applicationResources)", accentManager,
            StringComparison.Ordinal);
        Assert.Contains("current.MergedDictionaries", accentManager,
            StringComparison.Ordinal);
        Assert.Contains("new[] { \"Light\", \"Dark\" }", accentManager,
            StringComparison.Ordinal);
        Assert.Contains("#47E10C", ReadRepositoryFile(
            "src",
            "OpenBlade.Tray.Core",
            "AppearancePreferences.cs"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeThemeSynchronizesMicaBackdropWithRootActualTheme()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.DoesNotContain("<Window.SystemBackdrop>", xaml, StringComparison.Ordinal);
        Assert.Contains("new MicaBackdropHost(this, Root)", window, StringComparison.Ordinal);
        Assert.Contains("micaBackdropHost.Dispose()", window, StringComparison.Ordinal);
        int ensureDispatcher = window.IndexOf(
            "dispatcherQueue.EnsureSystemDispatcherQueue()",
            StringComparison.Ordinal);
        int createBackdrop = window.IndexOf(
            "new MicaBackdropHost(this, Root)",
            StringComparison.Ordinal);
        Assert.True(ensureDispatcher >= 0 && ensureDispatcher < createBackdrop);

        string host = ReadRepositoryFile("src", "OpenBlade.Tray", "MicaBackdropHost.cs");
        Assert.Contains("MicaController.IsSupported()", host, StringComparison.Ordinal);
        Assert.Contains("new SystemBackdropConfiguration", host, StringComparison.Ordinal);
        Assert.Contains("window.Activated += OnWindowActivated", host, StringComparison.Ordinal);
        Assert.Contains("window.Activated -= OnWindowActivated", host, StringComparison.Ordinal);
        Assert.Contains("root.ActualThemeChanged += OnActualThemeChanged", host, StringComparison.Ordinal);
        Assert.Contains("root.ActualThemeChanged -= OnActualThemeChanged", host, StringComparison.Ordinal);
        Assert.Contains("configuration.Theme = ToBackdropTheme(root.ActualTheme)", host, StringComparison.Ordinal);
        Assert.Contains(
            "args.WindowActivationState != WindowActivationState.Deactivated",
            host,
            StringComparison.Ordinal);
        Assert.Contains("ElementTheme.Light => SystemBackdropTheme.Light", host, StringComparison.Ordinal);
        Assert.Contains("ElementTheme.Dark => SystemBackdropTheme.Dark", host, StringComparison.Ordinal);
        Assert.Contains("ElementTheme.Default => SystemBackdropTheme.Default", host, StringComparison.Ordinal);
        Assert.Contains("controller.Dispose()", host, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowExtendsMicaThroughNativeCaptionArea()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("x:Name=\"AppTitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"OpenBlade\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainNavigation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsTitleBarAutoPaddingEnabled=\"False\"", xaml, StringComparison.Ordinal);

        int initialize = window.IndexOf("InitializeComponent()", StringComparison.Ordinal);
        int extend = window.IndexOf("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal);
        int setTitleBar = window.IndexOf("SetTitleBar(AppTitleBar)", StringComparison.Ordinal);
        int createBackdrop = window.IndexOf("new MicaBackdropHost(this, Root)", StringComparison.Ordinal);
        Assert.True(initialize >= 0 && initialize < extend);
        Assert.True(extend < setTitleBar && setTitleBar < createBackdrop);
        Assert.DoesNotContain("SetDragRectangles", window, StringComparison.Ordinal);
        Assert.DoesNotContain("InputNonClientPointerSource", window, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonForegroundColor", window, StringComparison.Ordinal);
        Assert.Contains("AppWindowTitleBar.IsCustomizationSupported()", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.TitleBar.PreferredTheme", window, StringComparison.Ordinal);
        Assert.Contains("TitleBarTheme.UseDefaultAppMode", window, StringComparison.Ordinal);
        Assert.Contains("TitleBarTheme.Light", window, StringComparison.Ordinal);
        Assert.Contains("TitleBarTheme.Dark", window, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizationSnapshotsDoNotActivateDormantApplicationResources()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string capture = ReadRepositoryFile("build", "Capture-LocalizationSnapshots.ps1");

        Assert.DoesNotContain("InitializeComponent()", app, StringComparison.Ordinal);
        Assert.Contains(
            "Root.ActualTheme == ElementTheme.Dark",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "LaptopPageContent.Transitions.Clear()",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Application.Current.Resources[",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateSet('en-US', 'qps-ploc', 'zh-CN', 'ru-RU')]",
            capture,
            StringComparison.Ordinal);
        Assert.Contains(
            "Root.RequestedTheme = ElementTheme.Light",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadPresentedProductVersion(localizationSnapshotMode)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "version.IndexOf('+', StringComparison.Ordinal)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "return metadataSeparator < 0 ? version : version[..metadataSeparator]",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanonicalizeLocalizationSnapshotPixels(\r\n            pixelBytes,\r\n            bitmap.PixelWidth,\r\n            bitmap.PixelHeight)",
            app,
            StringComparison.Ordinal);
        Assert.Contains("majorityCount >= 6", app, StringComparison.Ordinal);
        Assert.Contains("channelDelta > 16", app, StringComparison.Ordinal);
        Assert.Contains(
            "$env:OPENBLADE_LOCALIZATION_LANGUAGE = $Culture",
            capture,
            StringComparison.Ordinal);
        int activate = app.IndexOf("snapshotMainWindow.Activate()", StringComparison.Ordinal);
        int initialLayoutDrain = app.IndexOf(
            "await WaitForSnapshotLayoutAsync()",
            activate,
            StringComparison.Ordinal);
        int pageLoop = app.IndexOf(
            "foreach (string destination",
            activate,
            StringComparison.Ordinal);
        Assert.True(activate >= 0 && activate < initialLayoutDrain);
        Assert.True(initialLayoutDrain < pageLoop);
        Assert.Contains(
            "OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE",
            app,
            StringComparison.Ordinal);
        Assert.Contains("foreach ($trayPage", capture, StringComparison.Ordinal);
        Assert.Contains("'settings-appearance'", capture, StringComparison.Ordinal);
        Assert.Contains("'profiles-narrow'", capture, StringComparison.Ordinal);
        Assert.Contains("'profiles-wide'", capture, StringComparison.Ordinal);
        Assert.Contains(
            "ShowLocalizationSnapshotSettingsAppearance()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsAppearanceSection.StartBringIntoView()",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "? new Windows.Graphics.SizeInt32(1920, 1000)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "new Windows.Graphics.SizeInt32(800, 820)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "new Windows.Graphics.SizeInt32(1600, 900)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            ": new Windows.Graphics.SizeInt32(1120, 820)",
            app,
            StringComparison.Ordinal);
        Assert.Contains("\"profiles-narrow\"", app, StringComparison.Ordinal);
        Assert.Contains("\"profiles-wide\"", app, StringComparison.Ordinal);
        Assert.Contains(
            "snapshotMainWindow.AppWindow.Resize(snapshotSize)",
            app,
            StringComparison.Ordinal);
        int perPageResize = app.IndexOf(
            "snapshotMainWindow.AppWindow.Resize(snapshotSize)",
            pageLoop,
            StringComparison.Ordinal);
        int pageSelection = app.IndexOf(
            "snapshotMainWindow.SelectLocalizationSnapshotPage(navigationDestination)",
            pageLoop,
            StringComparison.Ordinal);
        Assert.True(pageLoop < perPageResize && perPageResize < pageSelection);
        int appearanceSnapshot = window.IndexOf(
            "internal void ShowLocalizationSnapshotSettingsAppearance()",
            StringComparison.Ordinal);
        int nextSnapshotMember = window.IndexOf(
            "internal bool HasLocalizedCheckNowButton",
            appearanceSnapshot,
            StringComparison.Ordinal);
        Assert.True(appearanceSnapshot >= 0 && appearanceSnapshot < nextSnapshotMember);
        Assert.DoesNotContain(
            "AppWindow.Resize",
            window[appearanceSnapshot..nextSnapshotMember],
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE = $trayPage",
            capture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SimplifiedChineseUsesOneLiveReadingsTermAcrossTraySurfaces()
    {
        string trayCore = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray.Core",
            "Properties",
            "Resources.zh-CN.resx");
        string tray = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "Strings",
            "zh-CN",
            "Resources.resw");

        Assert.DoesNotContain("实时监测", trayCore, StringComparison.Ordinal);
        Assert.DoesNotContain("实时监测", tray, StringComparison.Ordinal);
        Assert.Contains("<value>设备状态和实时数据</value>", trayCore, StringComparison.Ordinal);
        Assert.Contains("<value>实时数据</value>", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void AppAppliesPersistedLanguageBeforeWinUiResourcesLoad()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains("static App()", app, StringComparison.Ordinal);
        Assert.Contains("ApplyLocalizationPreference();", app, StringComparison.Ordinal);
        Assert.Contains(
            "UiPreferenceStore.CreateDefault().LoadLanguagePreference()",
            app,
            StringComparison.Ordinal);

        int environment = app.IndexOf(
            "string? language = RequestedLocalizationLanguage()",
            StringComparison.Ordinal);
        int preference = app.IndexOf(
            "UiPreferenceStore.CreateDefault().LoadLanguagePreference()",
            StringComparison.Ordinal);
        int resourceOverride = app.IndexOf(
            "ApplicationLanguages.PrimaryLanguageOverride = resourceLanguage",
            StringComparison.Ordinal);
        Assert.True(environment >= 0 && environment < preference);
        Assert.True(preference < resourceOverride);
    }

    [Fact]
    public void LanguageChangesRebuildEveryLocalizedTraySurfaceInProcess()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string window = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");

        int refreshStart = app.IndexOf(
            "private async Task RefreshLanguageAsync",
            StringComparison.Ordinal);
        int refreshEnd = app.IndexOf(
            "private bool TryShowTrayNotification",
            refreshStart,
            StringComparison.Ordinal);
        Assert.True(refreshStart >= 0 && refreshStart < refreshEnd);
        string refresh = app[refreshStart..refreshEnd];

        int tryBlock = refresh.IndexOf(
            "        try",
            StringComparison.Ordinal);
        int temporaryOverride = refresh.IndexOf(
            "TrayLocalization.BeginTemporaryLanguageOverride(replacementTrayLanguage)",
            StringComparison.Ordinal);
        int apply = refresh.IndexOf(
            "ApplyRuntimeLocalizationPreference(",
            temporaryOverride,
            StringComparison.Ordinal);
        int create = refresh.IndexOf(
            "replacement = CreateMainWindow(registration, preference)",
            StringComparison.Ordinal);
        int initialize = refresh.IndexOf(
            "await replacementInitialization",
            StringComparison.Ordinal);
        int prepareSurfaces = refresh.IndexOf(
            "replacementSurfaces = CreateWindowSurfaces(replacement, eventEndpoint)",
            StringComparison.Ordinal);
        int attach = refresh.IndexOf(
            "AttachWindowSurfaces(",
            prepareSurfaces,
            StringComparison.Ordinal);
        int commit = refresh.IndexOf(
            "replacementCommitted = true",
            StringComparison.Ordinal);
        int commitTrayLanguage = refresh.IndexOf(
            "TrayLocalization.SetLanguageOverride(replacementTrayLanguage)",
            StringComparison.Ordinal);
        int disposePrevious = refresh.IndexOf(
            "await DisposeWindowSafelyAsync(previousWindow)",
            StringComparison.Ordinal);
        Assert.True(
            tryBlock >= 0
            && tryBlock < temporaryOverride
            && temporaryOverride < apply
            && apply < create
            && create < initialize
            && initialize < prepareSurfaces
            && prepareSurfaces < attach
            && attach < commitTrayLanguage
            && commitTrayLanguage < commit
            && commit < disposePrevious);
        Assert.Contains("commitTrayLanguage: false", refresh, StringComparison.Ordinal);
        Assert.Contains("CreateTrayIconHost(replacement)", refresh, StringComparison.Ordinal);
        Assert.Contains("replacementCommitted = true", refresh, StringComparison.Ordinal);
        Assert.Contains("if (replacementCommitted)", refresh, StringComparison.Ordinal);
        Assert.Contains("OverlappedPresenterState.Maximized", refresh, StringComparison.Ordinal);
        Assert.Contains("presenter.Maximize()", refresh, StringComparison.Ordinal);
        Assert.Contains("replacement.ShowLanguageSettings()", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionAgentLauncher", refresh, StringComparison.Ordinal);

        int smokeStart = app.IndexOf(
            "private async Task RunLiveLocalizationSmokeAsync",
            StringComparison.Ordinal);
        int smokeEnd = app.IndexOf(
            "private static void AssertLiveLocalizationValue",
            smokeStart,
            StringComparison.Ordinal);
        Assert.True(smokeStart >= 0 && smokeStart < smokeEnd);
        string smoke = app[smokeStart..smokeEnd];
        Assert.Contains("await RefreshLanguageAsync(", smoke, StringComparison.Ordinal);
        Assert.Contains("Injected replacement tray icon failure", smoke, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(trayIconHost, initialTrayIcon)", smoke, StringComparison.Ordinal);
        Assert.Contains("expectMaximized", smoke, StringComparison.Ordinal);
        Assert.Contains(
            "The language refresh changed invariant M-key bindings.",
            smoke,
            StringComparison.Ordinal);
        Assert.Contains("ExpectedKeyboardShortcutAction", smoke, StringComparison.Ordinal);
        Assert.Contains("ExpectedActionAccessibleName", smoke, StringComparison.Ordinal);

        int languageChange = window.IndexOf(
            "if (args.PropertyName == nameof(MainViewModel.LanguagePreference))",
            StringComparison.Ordinal);
        int nextProperty = window.IndexOf(
            "if (args.PropertyName == nameof(MainViewModel.ActivePowerSource))",
            languageChange,
            StringComparison.Ordinal);
        Assert.True(languageChange >= 0 && languageChange < nextProperty);
        string languageHandler = window[languageChange..nextProperty];
        int save = languageHandler.IndexOf("QueuePreferenceSave()", StringComparison.Ordinal);
        int notify = languageHandler.IndexOf(
            "LanguagePreferenceChanged?.Invoke",
            StringComparison.Ordinal);
        Assert.True(save >= 0 && save < notify);
        Assert.Contains(
            "initialLanguageOverride ?? preferences.EffectiveLanguage",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowLoadsThemeBeforeWatchingAndOwnsPreferenceSaveLifetime()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("UiPreferenceStore.CreateDefault()", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged += OnViewModelPropertyChanged", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged -= OnViewModelPropertyChanged", source, StringComparison.Ordinal);
        Assert.Contains("preferenceSaveQueue.Enqueue", source, StringComparison.Ordinal);
        Assert.Contains("StartMinimized = preferences.StartMinimized", source, StringComparison.Ordinal);
        Assert.Contains("StartMinimizedToggle.IsOn = StartMinimized", source, StringComparison.Ordinal);
        Assert.Contains(
            "initialLabFeaturesEnabled = preferences.LabFeaturesEnabled",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LabFeaturesEnabled: ViewModel.LabFeaturesEnabled",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "initialLanguage = initialLanguageOverride ?? preferences.EffectiveLanguage",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.LanguagePreference = initialLanguage",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Language: ViewModel.LanguagePreference",
            source,
            StringComparison.Ordinal);
        Assert.Contains("\"preference save queue\"", source, StringComparison.Ordinal);
        Assert.Contains("preferenceSaveQueue.DisposeAsync", source, StringComparison.Ordinal);

        int load = source.IndexOf("await preferenceStore.LoadAsync", StringComparison.Ordinal);
        int apply = source.IndexOf("ApplyTheme(ViewModel.Theme)", StringComparison.Ordinal);
        int subscribe = source.IndexOf(
            "ViewModel.PropertyChanged += OnViewModelPropertyChanged",
            StringComparison.Ordinal);
        int watch = source.IndexOf("await ViewModel.StartAsync()", StringComparison.Ordinal);
        Assert.True(load >= 0 && load < apply && apply < subscribe && subscribe < watch);

        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        Assert.Contains("Text=\"Lab features\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Turn on experimental features.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Show experimental features, including custom keyboard-lighting preset formats.",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LabFeaturesInfoButton\"", xaml, StringComparison.Ordinal);
        string labFeaturesInfoButton = SliceMethod(
            xaml,
            "x:Name=\"LabFeaturesInfoButton\"",
            "<Button.Flyout>");
        Assert.Contains("Width=\"20\"", labFeaturesInfoButton, StringComparison.Ordinal);
        Assert.Contains("Height=\"20\"", labFeaturesInfoButton, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"28\"", labFeaturesInfoButton, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"28\"", labFeaturesInfoButton, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"About Lab features\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("<Button.Flyout>", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Current Lab features\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Keyboard lighting preset import\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Import .oblight and Razer .chroma files.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Uid=\"MainWindow_TextBlockSixtyHzKeyboardLighting\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Uid=\"MainWindow_TextBlockRenderSupportedEffectsMoreSmoothly\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Lab features may change or be removed.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LabFeaturesToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind ViewModel.LabFeaturesEnabled, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Toggled=\"OnLabFeaturesToggled\"", xaml, StringComparison.Ordinal);

        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains(
            "AttachWindowSurfaces(window, surfaces, window.InitializeAsync())",
            app,
            StringComparison.Ordinal);
        Assert.Contains("await windowInitializationTask", app, StringComparison.Ordinal);
        Assert.True(
            app.IndexOf("await windowInitializationTask", StringComparison.Ordinal)
            < app.LastIndexOf("exitLifecycle.TryRunWhileActive", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindowStartsInitialDisplayRefreshWithoutBlockingReadiness()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int initializeCore = source.IndexOf(
            "private async Task InitializeCoreAsync()",
            StringComparison.Ordinal);
        int propertyChanged = source.IndexOf(
            "private void OnViewModelPropertyChanged",
            initializeCore,
            StringComparison.Ordinal);
        Assert.True(initializeCore >= 0 && initializeCore < propertyChanged);

        string readinessPath = source[initializeCore..propertyChanged];
        Assert.Contains(
            "_ = RefreshDisplaySettingsAsync();",
            readinessPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await RefreshDisplaySettingsAsync();",
            readinessPath,
            StringComparison.Ordinal);

        int refreshMethod = source.IndexOf(
            "private Task RefreshDisplaySettingsAsync()",
            StringComparison.Ordinal);
        int displaySettingsChanged = source.IndexOf(
            "private void OnDisplayUserSettingsChanged",
            refreshMethod,
            StringComparison.Ordinal);
        Assert.True(refreshMethod >= 0 && refreshMethod < displaySettingsChanged);
        string refreshPath = source[refreshMethod..displaySettingsChanged];
        Assert.Contains(
            "displayRefreshTask is { IsCompleted: false } activeRefresh",
            refreshPath,
            StringComparison.Ordinal);
        Assert.Contains("return activeRefresh;", refreshPath, StringComparison.Ordinal);

        int timerTick = source.IndexOf(
            "private async void OnDisplayRefreshTimerTick",
            StringComparison.Ordinal);
        int pinnedCheck = source.IndexOf(
            "private bool IsDashboardCardPinned",
            timerTick,
            StringComparison.Ordinal);
        Assert.True(timerTick >= 0 && timerTick < pinnedCheck);
        string timerPath = source[timerTick..pinnedCheck];
        Assert.Contains(
            "|| IsDisplayRefreshInProgress",
            timerPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowUsesPostRequestRefreshBeforeCapturingProfile()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int captureRefresh = source.IndexOf(
            "private async Task RefreshDisplaySettingsBeforeCaptureAsync()",
            StringComparison.Ordinal);
        int displaySettingsChanged = source.IndexOf(
            "private void OnDisplayUserSettingsChanged",
            captureRefresh,
            StringComparison.Ordinal);
        Assert.True(captureRefresh >= 0 && captureRefresh < displaySettingsChanged);

        string captureRefreshPath = source[captureRefresh..displaySettingsChanged];
        int snapshot = captureRefreshPath.IndexOf(
            "Task? refreshAtRequest = displayRefreshTask;",
            StringComparison.Ordinal);
        int joinExisting = captureRefreshPath.IndexOf(
            "await refreshAtRequest;",
            StringComparison.Ordinal);
        int refreshAgain = captureRefreshPath.IndexOf(
            "await RefreshDisplaySettingsAsync();",
            StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && snapshot < joinExisting && joinExisting < refreshAgain);

        int saveHandler = source.IndexOf(
            "private async Task HandleSaveCurrentDisplaySettingsToProfileAsync()",
            StringComparison.Ordinal);
        int applyHandler = source.IndexOf(
            "private async void OnUseDisplayProfileSettings",
            saveHandler,
            StringComparison.Ordinal);
        Assert.True(saveHandler >= 0 && saveHandler < applyHandler);

        string savePath = source[saveHandler..applyHandler];
        Assert.Contains(
            "await RefreshDisplaySettingsBeforeCaptureAsync();",
            savePath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await RefreshDisplaySettingsAsync();",
            savePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleObservationFollowsEveryShowAndStopsBeforeHideAndViewModelDisposal()
    {
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string trayHost = ReadRepositoryFile(
            "src", "OpenBlade.Tray", "WinFormsTrayIconHost.cs");
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("new VisibleObservationLoop(ObserveVisibleSurfacesAsync", window, StringComparison.Ordinal);
        Assert.Contains("new VisibleObservationLoop(ViewModel.ObserveTrayStateAsync", window, StringComparison.Ordinal);
        Assert.Contains("ObserveGpuAppActivityAsync", window, StringComparison.Ordinal);
        Assert.Contains("gpuAppObservationLoop.StartOrWake()", window, StringComparison.Ordinal);
        Assert.Contains("gpuAppObservationLoop.Stop()", window, StringComparison.Ordinal);
        Assert.Contains("gpuAppObservationLoop.DisposeAsync", window, StringComparison.Ordinal);
        Assert.Contains("observationIntervalProvider: () => Timeout.InfiniteTimeSpan", window, StringComparison.Ordinal);
        Assert.Contains("trayIcon.MouseMove += OnTrayIconMouseMove", trayHost, StringComparison.Ordinal);
        int mouseMoveHandler = trayHost.IndexOf(
            "private void OnTrayIconMouseMove",
            StringComparison.Ordinal);
        int rateGate = trayHost.IndexOf(
            "hoverTelemetryRefreshGate.TryAcquire",
            mouseMoveHandler,
            StringComparison.Ordinal);
        int enqueueRequest = trayHost.IndexOf(
            "hoverTelemetryRefreshRequested()",
            mouseMoveHandler,
            StringComparison.Ordinal);
        Assert.True(mouseMoveHandler >= 0 && rateGate > mouseMoveHandler && enqueueRequest > rateGate);
        int mouseMoveHandlerEnd = trayHost.IndexOf(
            "private void UpdateTrayIcon()",
            mouseMoveHandler,
            StringComparison.Ordinal);
        string mouseMoveBody = trayHost[mouseMoveHandler..mouseMoveHandlerEnd];
        Assert.Contains(
            "Interlocked.Read(",
            mouseMoveBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "viewModel.LiveReadingsObservationInterval",
            mouseMoveBody,
            StringComparison.Ordinal);
        int propertyChangedHandler = trayHost.IndexOf(
            "private void OnViewModelPropertyChanged",
            StringComparison.Ordinal);
        int propertyChangedHandlerEnd = trayHost.IndexOf(
            "private void OnTrayIconThemeChanged",
            propertyChangedHandler,
            StringComparison.Ordinal);
        string propertyChangedBody =
            trayHost[propertyChangedHandler..propertyChangedHandlerEnd];
        Assert.Contains(
            "nameof(MainViewModel.LiveReadingsObservationInterval)",
            propertyChangedBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.Exchange(",
            propertyChangedBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "ref liveReadingsObservationIntervalTicks",
            propertyChangedBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "{x:Bind ViewModel.LiveReadings,",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "{x:Bind ViewModel.LiveReadings}",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "{x:Bind ViewModel.LiveReadings.",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppWindow.Show(true)", app, StringComparison.Ordinal);
        int showMethod = window.IndexOf("public void ShowAndActivate()", StringComparison.Ordinal);
        int start = window.IndexOf("UpdateActiveObservationLoop()", showMethod, StringComparison.Ordinal);
        int show = window.IndexOf("AppWindow.Show(true)", showMethod, StringComparison.Ordinal);
        Assert.True(showMethod >= 0 && start > showMethod && show > start);

        int closing = window.IndexOf("private void OnClosing", StringComparison.Ordinal);
        int stop = window.IndexOf("StartHiddenObservation()", closing, StringComparison.Ordinal);
        int hide = window.IndexOf("AppWindow.Hide()", closing, StringComparison.Ordinal);
        Assert.True(closing >= 0 && stop > closing && hide > stop);

        Assert.Contains("window.StartHiddenObservation()", app, StringComparison.Ordinal);
        int hiddenMethod = window.IndexOf("public void StartHiddenObservation()", StringComparison.Ordinal);
        int hiddenMethodEnd = window.IndexOf(
            "public async Task StartUpdateCheckingAsync()",
            hiddenMethod,
            StringComparison.Ordinal);
        string hiddenBody = window[hiddenMethod..hiddenMethodEnd];
        Assert.DoesNotContain("trayObservationLoop.StartOrWake()", hiddenBody, StringComparison.Ordinal);
        Assert.Contains("ApplyProcessorVisibilityPolicy()", hiddenBody, StringComparison.Ordinal);
        Assert.Contains(
            "!window.UpdateViewModel.NeedsConsentPrompt",
            app,
            StringComparison.Ordinal);
        Assert.Contains("await window.StartUpdateCheckingAsync()", app, StringComparison.Ordinal);

        int disposeLoop = window.IndexOf(
            "visibleObservationLoop.DisposeAsync",
            StringComparison.Ordinal);
        int disposeTrayLoop = window.IndexOf(
            "trayObservationLoop.DisposeAsync",
            StringComparison.Ordinal);
        int disposeViewModel = window.IndexOf(
            "\"main view model\"",
            StringComparison.Ordinal);
        Assert.True(disposeLoop >= 0 && disposeTrayLoop > disposeLoop && disposeViewModel > disposeTrayLoop);
        Assert.DoesNotContain("trayDashboardObservationLoop", window, StringComparison.Ordinal);
        Assert.Contains("DisposeComponentSafelyAsync", window, StringComparison.Ordinal);
        int processorPolicy = window.IndexOf(
            "private void ApplyProcessorVisibilityPolicy()",
            StringComparison.Ordinal);
        int dashboardVisibility = window.IndexOf(
            "public void SetTrayDashboardVisible(bool visible)",
            StringComparison.Ordinal);
        string processorPolicyBody = window[processorPolicy..dashboardVisibility];
        Assert.Contains("windowVisible || trayDashboardVisible", processorPolicyBody, StringComparison.Ordinal);
        Assert.Contains("TrySetProcessEcoQos(enabled: false)", processorPolicyBody, StringComparison.Ordinal);
        Assert.Contains("TrySetProcessEcoQos(enabled: true)", processorPolicyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationAndSetupShutdownEndpointsExistWhileInitializationIsPending()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        int initializationAwait = app.IndexOf(
            "await windowInitializationTask",
            StringComparison.Ordinal);
        int exitLifecycle = app.IndexOf("exitLifecycle = new TrayExitLifecycle", StringComparison.Ordinal);
        int setupShutdown = app.IndexOf("shutdownRegistration =", StringComparison.Ordinal);
        int trayIcon = app.IndexOf("trayIconHost =", StringComparison.Ordinal);
        int initialShow = app.LastIndexOf(
            "ShowMainWindow(initialLaunch: true);",
            StringComparison.Ordinal);
        Assert.True(exitLifecycle >= 0 && exitLifecycle < initializationAwait);
        Assert.True(setupShutdown >= 0 && setupShutdown < initializationAwait);
        Assert.True(trayIcon >= 0 && trayIcon < initializationAwait);
        Assert.True(exitLifecycle < initialShow);
        Assert.True(setupShutdown < initialShow);
        Assert.True(trayIcon < initialShow);
        Assert.Contains("exitLifecycle.TryRunWhileActive", app, StringComparison.Ordinal);
        Assert.Contains(
            "&& window.StartMinimized",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowMainWindowWhenReadyAsync(initialLaunch: false)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledStartupLaunchExitsBeforeClaimingTheTrayInstance()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        int startupGate = app.IndexOf(
            "registration.ShouldStartForLaunchArguments(",
            StringComparison.Ordinal);
        int commandLineArguments = app.IndexOf(
            "string[] commandLineArguments = Environment.GetCommandLineArgs()",
            StringComparison.Ordinal);
        int helperGate = app.IndexOf(
            "ElevatedServiceControlLauncher.ParseHelperAction",
            StringComparison.Ordinal);
        int instanceClaim = app.IndexOf(
            "TrayInstanceCoordinator.TryStart",
            StringComparison.Ordinal);

        Assert.True(
            commandLineArguments >= 0
            && commandLineArguments < helperGate
            && helperGate < startupGate
            && startupGate < instanceClaim);
        Assert.DoesNotContain("args.Arguments", app, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialPlacementUsesWindowDpiAndDisplayRelativeMoveAndResize()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("WindowNative.GetWindowHandle(this)", source, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", source, StringComparison.Ordinal);
        Assert.Contains("DisplayArea.GetFromWindowId", source, StringComparison.Ordinal);
        Assert.Contains("DisplayAreaFallback.Primary", source, StringComparison.Ordinal);
        Assert.Contains("WindowPlacementPolicy.Center", source, StringComparison.Ordinal);
        Assert.Contains("AppWindow.MoveAndResize(rect, displayArea)", source, StringComparison.Ordinal);
        Assert.Contains("initialPlacementAttempted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Root.MinWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Root.MinHeight", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowCodeBehindProvidesResponsiveProfilesAndImmediateDashboardHandlers()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains("ProfilesStackThresholdDip = 900", source, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged += OnRootSizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged -= OnRootSizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"ProfilesComparisonLayout\")", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"ProfilesCompactLayout\")", source, StringComparison.Ordinal);
        Assert.Contains("comparisonLayout.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("compactLayout.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("ProfilesPage.ActualWidth", source, StringComparison.Ordinal);

        Assert.Contains("OnDashboardPerformanceChanged", source, StringComparison.Ordinal);
        Assert.Contains("OnDashboardFanModeSelectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("OnDashboardChargeLimitPointerPressed", source, StringComparison.Ordinal);
        Assert.Contains("OnDashboardChargeLimitPointerCaptureLost", source, StringComparison.Ordinal);
        Assert.Contains("OnDashboardChargeLimitKeyUp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDashboardChargeLimitValueChanged", source, StringComparison.Ordinal);
        Assert.Contains("OnBatteryProtectionToggled", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(400)", source, StringComparison.Ordinal);
        Assert.Contains("dispatcherQueue.CreateTimer()", source, StringComparison.Ordinal);
        Assert.Contains("chargeLimitCommitTimer.IsRepeating = false", source, StringComparison.Ordinal);
        Assert.Contains("chargeLimitCommits.Schedule(new ChargeLimitCommitRequest(", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ChargeLimitEditorRevision", source, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.RevertUnappliedChargeLimitEdit(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pendingChargeLimitPercent", source, StringComparison.Ordinal);
        Assert.Contains("command.ExecuteAsync(commit)", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeBatteryProtectionToggle(toggle)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayRefreshSelectionQueuesBehindRefreshAndRestoresConfirmedState()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int handlerStart = source.IndexOf(
            "private async void OnDisplayRefreshRateChanged(",
            StringComparison.Ordinal);
        int handlerEnd = source.IndexOf(
            "private void OnDisplayPickerOpened(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];

        Assert.DoesNotContain("changingDisplaySettings", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RunDisplayOperationAsync", handler, StringComparison.Ordinal);
        Assert.Contains("RunHandlerAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.SelectRefreshRateAsync(option)", handler, StringComparison.Ordinal);
        Assert.Contains(
            "selector.SelectedItem = DisplayViewModel.SelectedRefreshRate",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayHdrToggleQueuesBehindRefreshAndRestoresConfirmedState()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int handlerStart = source.IndexOf(
            "private async void OnDisplayHdrToggled(",
            StringComparison.Ordinal);
        int handlerEnd = source.IndexOf(
            "private async void OnDisplayColorProfileChanged(",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];

        Assert.DoesNotContain("RunDisplayOperationAsync", handler, StringComparison.Ordinal);
        Assert.Contains("bool requested = toggle.IsOn;", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("toggle.IsEnabled =", handler, StringComparison.Ordinal);
        Assert.Contains("RunHandlerAsync(", handler, StringComparison.Ordinal);
        Assert.Contains(
            "DisplayViewModel.SetHdrEnabledAsync(requested)",
            handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("finally", handler, StringComparison.Ordinal);
        Assert.Contains(
            "toggle.IsOn = DisplayViewModel.IsHdrEnabled;",
            handler,
            StringComparison.Ordinal);

        int capture = handler.IndexOf("bool requested = toggle.IsOn;", StringComparison.Ordinal);
        int queue = handler.IndexOf("await RunHandlerAsync(", StringComparison.Ordinal);
        int restore = handler.IndexOf(
            "toggle.IsOn = DisplayViewModel.IsHdrEnabled;",
            StringComparison.Ordinal);
        Assert.True(capture < queue && queue < restore);
        Assert.DoesNotContain("RunDisplayOperationAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayColorProfileSelectionUsesResponsiveEventAndRestoresConfirmedState()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        int handlerStart = source.IndexOf(
            "private async void OnDisplayColorProfileChanged(",
            StringComparison.Ordinal);
        int handlerEnd = source.IndexOf(
            "private Task RefreshDisplaySettingsAsync()",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];

        Assert.Contains(
            "ResponsiveChoiceSelectionChangedEventArgs args",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "sender is not ResponsiveChoiceSelector selector",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "args.SelectedItem is not DisplayColorProfileOption option",
            handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("changingDisplaySettings", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RunDisplayOperationAsync", handler, StringComparison.Ordinal);
        Assert.Contains("RunHandlerAsync(", handler, StringComparison.Ordinal);
        Assert.Contains(
            "DisplayViewModel.SelectColorProfileAsync(option)",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "selector.SelectedItem = DisplayViewModel.SelectedColorProfile",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LaptopPageHostsPersistentSpecialKeyBindings()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string bindingViewModel = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "SpecialKeyBindingViewModel.cs");

        Assert.Contains("Text=\"Shortcut keys\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind SpecialKeyBindings, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsEditable, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SpecialKeyBindingsObservedOnlyInfoBar",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{x:Bind AccessLabel, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{x:Bind SelectedAction, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind AvailableActions, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{x:Bind SlotLabel, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind ActionAccessibleName, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Tag=\"{x:Bind Slot}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectionChanged=\"OnSpecialKeyActionSelectionChanged\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{x:Bind PhysicalKeyLabel}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{x:Bind Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "public ObservableCollection<SpecialKeyBindingViewModel> SpecialKeyBindings",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplySpecialKeyBindingPreferences(specialKeyBindings)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Select(binding => binding.ToPreference())",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "comboBox.Tag is not SpecialKeySlot slot",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AutomationProperties.GetName(comboBox)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "binding.SelectedAction = selectedAction;",
            window,
            StringComparison.Ordinal);
        Assert.Contains("CreateActionOptions()", bindingViewModel, StringComparison.Ordinal);
        Assert.Contains(
            "availableActions.Single(option => option.Kind == normalized.Kind)",
            bindingViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public static IReadOnlyList<SpecialKeyActionOption> ActionOptions",
            bindingViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Slot.ToString()", bindingViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecialKeyEditorResourcesCoverEveryActionAndSlotInEveryCatalog()
    {
        string source = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "SpecialKeyBindingViewModel.cs");
        string[] resourceKeys = System.Text.RegularExpressions.Regex.Matches(
                source,
                "LocalizedResources\\.(?:Get|Format)\\(\\s*\\\"(?<key>SpecialKey_[^\\\"]+)\\\"")
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        System.Text.RegularExpressions.Match[] actionOptions =
            System.Text.RegularExpressions.Regex.Matches(
                source,
                "new\\(SpecialKeyActionKind\\.(?<kind>\\w+),\\s*"
                + "LocalizedResources\\.Get\\(\\s*\\\"(?<key>SpecialKey_Action_[^\\\"]+)\\\"")
            .ToArray();
        System.Text.RegularExpressions.Match[] slotLabels =
            System.Text.RegularExpressions.Regex.Matches(
                source,
                "SpecialKeySlot\\.(?<slot>\\w+)\\s*=>\\s*"
                + "LocalizedResources\\.Get\\(\\s*\\\"(?<key>SpecialKey_Slot_[^\\\"]+)\\\"")
            .ToArray();
        string[] actionKeys = actionOptions
            .Select(match => match.Groups["key"].Value)
            .ToArray();
        string[] slotKeys = slotLabels
            .Select(match => match.Groups["key"].Value)
            .ToArray();

        Assert.Equal(
            Enum.GetNames<SpecialKeyActionKind>().Order(StringComparer.Ordinal),
            actionOptions
                .Select(match => match.Groups["kind"].Value)
                .Order(StringComparer.Ordinal));
        Assert.Equal(actionKeys.Length, actionKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Enum.GetNames<SpecialKeySlot>().Order(StringComparer.Ordinal),
            slotLabels
                .Select(match => match.Groups["slot"].Value)
                .Order(StringComparer.Ordinal));
        Assert.Equal(slotKeys.Length, slotKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("SpecialKey_Action_AccessibleName", resourceKeys);

        foreach (string culture in new[] { "en-US", "ru-RU", "zh-CN", "qps-ploc" })
        {
            XDocument catalog = XDocument.Load(RepositoryPath(
                "src",
                "OpenBlade.Tray",
                "Strings",
                culture,
                "Resources.resw"));
            HashSet<string> catalogKeys = catalog.Root!
                .Elements("data")
                .Select(element => (string)element.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(resourceKeys, key => Assert.Contains(key, catalogKeys));
        }
    }

    [Fact]
    public void TrayDashboardUsesSingleClickAndSettingsOwnedLiveCustomization()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        string trayHost = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WinFormsTrayIconHost.cs");
        string mainXaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string mainWindow = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "MainWindow.xaml.cs");
        string dashboardXaml = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "TrayDashboardWindow.xaml");
        string dashboardWindow = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "TrayDashboardWindow.xaml.cs");
        string popupPositioner = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WindowsPopupPositioner.cs");
        string dashboardPreferences = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray.Core",
            "TrayDashboardPreferences.cs");
        int dashboardDispose = dashboardWindow.IndexOf(
            "public void Dispose()",
            StringComparison.Ordinal);
        int dashboardTrySetOwner = dashboardWindow.IndexOf(
            "private void TrySetEditOwner(",
            dashboardDispose,
            StringComparison.Ordinal);
        Assert.Contains(
            "gpuClockOffsetCommits.Dispose();",
            dashboardWindow[dashboardDispose..dashboardTrySetOwner],
            StringComparison.Ordinal);
        Assert.Contains("trayIcon.MouseClick += OnTrayIconMouseClick", trayHost, StringComparison.Ordinal);
        Assert.Contains(
            "trayIcon.MouseDoubleClick += OnTrayIconMouseDoubleClick",
            trayHost,
            StringComparison.Ordinal);
        Assert.Contains("TrayIconActivationPolicy.Resolve(button)", trayHost, StringComparison.Ordinal);
        Assert.Contains(
            "TrayIconActivationPolicy.ResolveDoubleClick(button)",
            trayHost,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrayIconActivationPolicy.ShouldSuppressTrailingClick(",
            trayHost,
            StringComparison.Ordinal);
        Assert.Contains("SystemInformation.DoubleClickTime", trayHost, StringComparison.Ordinal);
        Assert.Contains("System.Drawing.Point cursorPosition = Cursor.Position", trayHost, StringComparison.Ordinal);
        Assert.Contains("TrayIconMenu_Open", trayHost, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TrayIconMenu_DoubleClick",
            trayHost,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutKeyDisplayString", trayHost, StringComparison.Ordinal);
        Assert.Contains("TrayIconMenu_Exit", trayHost, StringComparison.Ordinal);
        Assert.Contains("new TrayDashboardWindow(", app, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(window)", app, StringComparison.Ordinal);
        Assert.Contains("window.SetTrayDashboardVisible(visible)", app, StringComparison.Ordinal);
        Assert.Contains("OpenMainWindowFromTrayIcon", app, StringComparison.Ordinal);
        Assert.Contains("DismissForMainWindow()", app, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardEditSessionChanged +=", app, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCustomizeTrayDashboard\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TrayDashboardCustomizationPage\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind TrayDashboardEditorControls, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind TrayDashboardEditorSliders, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddTrayDashboardControl\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAddTrayDashboardSlider\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnResetTrayDashboard\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardEditSessionChanged?.Invoke(true)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardEditSessionChanged?.Invoke(false)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("EndTrayDashboardCustomization(showSettingsOverview: false)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleCustomizeTrayDashboardAsync", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Tray dashboard controls", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Show(activateWindow: !editing)", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("private const int PopupWidth = 360", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("presenter.IsAlwaysOnTop = false", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("MonitorFromPoint(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("GetDpiForMonitor(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("MonitorDpiType.Effective", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("TryPositionAt(", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDpiForWindow(", dashboardWindow, StringComparison.Ordinal);
        int beginEdit = dashboardWindow.IndexOf(
            "public void BeginEditSession()",
            StringComparison.Ordinal);
        int endEdit = dashboardWindow.IndexOf(
            "public void EndEditSession()",
            beginEdit,
            StringComparison.Ordinal);
        string beginEditBody = dashboardWindow[beginEdit..endEdit];
        Assert.True(
            beginEditBody.IndexOf("TrySetEditOwner(editOwnerWindowHandle)", StringComparison.Ordinal)
            < beginEditBody.IndexOf("ShowAt(", StringComparison.Ordinal));
        Assert.Contains(
            "checked(workArea.X + workArea.Width - 1)",
            beginEditBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "checked(workArea.Y + workArea.Height - 1)",
            beginEditBody,
            StringComparison.Ordinal);
        int dismissForMainWindow = dashboardWindow.IndexOf(
            "public void DismissForMainWindow()",
            endEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "TrySetEditOwner(0)",
            dashboardWindow[endEdit..dismissForMainWindow],
            StringComparison.Ordinal);
        int showAt = dashboardWindow.IndexOf(
            "private void ShowAt(",
            StringComparison.Ordinal);
        int tryPosition = dashboardWindow.IndexOf(
            "private void TryPositionAt(",
            showAt,
            StringComparison.Ordinal);
        string showAtBody = dashboardWindow[showAt..tryPosition];
        Assert.Contains("if (!editing)", showAtBody, StringComparison.Ordinal);
        Assert.Contains("TryEstablishForeground()", showAtBody, StringComparison.Ordinal);
        Assert.Contains("TryStartReveal(placement, displayArea, editing)", showAtBody, StringComparison.Ordinal);
        int establishForeground = dashboardWindow.IndexOf(
            "private bool TryEstablishForeground()",
            tryPosition,
            StringComparison.Ordinal);
        Assert.Contains(
            "Activate();",
            dashboardWindow[establishForeground..],
            StringComparison.Ordinal);
        int position = dashboardWindow.IndexOf(
            "private void PositionAt(",
            tryPosition,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception exception)",
            dashboardWindow[tryPosition..position],
            StringComparison.Ordinal);
        Assert.Contains("TrayDashboardInteractionPolicy.ShouldSuppressReopen", dashboardWindow, StringComparison.Ordinal);
        int updateSliderRows = dashboardWindow.IndexOf(
            "private void UpdateSliderRows()",
            StringComparison.Ordinal);
        int updateFansDetails = dashboardWindow.IndexOf(
            "private void UpdateFansDetailsAvailability()",
            updateSliderRows,
            StringComparison.Ordinal);
        string updateSliderRowsBody = dashboardWindow[updateSliderRows..updateFansDetails];
        Assert.Contains("if (!slider.IsVisible)", updateSliderRowsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("IsGpuClockTuningVisible", updateSliderRowsBody, StringComparison.Ordinal);
        Assert.Contains("WindowOwnerIndex = -8", popupPositioner, StringComparison.Ordinal);
        Assert.Contains("SetWindowLongPtr", popupPositioner, StringComparison.Ordinal);
        Assert.Contains("DwmFlush()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("PostMessage(hostWindowHandle", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("SetWindowSubclass(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.High", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("StartRevealFrameClock()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("Thread? revealFrameClockThread", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("new Thread(RunRevealFrameClock)", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("revealFrameClockThread is { IsAlive: true }", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("IsBackground = true", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(16)", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(() => RunRevealFrameClock", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("revealFrameClockCancellation", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsPopupSlidePolicy.DurationMilliseconds + 100",
            dashboardWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "revealCompletionTimer.IsRepeating = false",
            dashboardWindow,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                dashboardWindow,
                "revealCompletionPending = false;"));
        Assert.Equal(
            1,
            CountOccurrences(
                dashboardWindow,
                "revealCompletionPending = true;"));
        Assert.Contains("WindowsPopupSlidePolicy.ResolvePosition(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardResolvedPlacement resolvedPlacement", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("resolvedPlacement.RevealGeometry", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveRevealGeometry", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("Root.UpdateLayout()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("WindowsMotionSettings.AreAnimationsEnabled()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("WindowsPopupPositioner.PositionInFront(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("NotTopmostWindow", popupPositioner, StringComparison.Ordinal);
        Assert.Contains("GetSystemMetrics(VirtualScreenLeftMetric)", popupPositioner, StringComparison.Ordinal);
        Assert.Contains("GetSystemMetrics(VirtualScreenTopMetric)", popupPositioner, StringComparison.Ordinal);
        Assert.DoesNotContain("TopmostWindow = new(-1)", popupPositioner, StringComparison.Ordinal);
        Assert.Contains("WindowsPopupPositioner.DisableTransitions(", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrimeSurface", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("TryStartDismissAnimation()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("hiding: revealHiding", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("HideSurface();", dashboardWindow, StringComparison.Ordinal);
        int hideSurface = dashboardWindow.IndexOf(
            "private void HideSurface()",
            StringComparison.Ordinal);
        int toRect = dashboardWindow.IndexOf(
            "private static RectInt32 ToRectInt32(",
            hideSurface,
            StringComparison.Ordinal);
        string hideSurfaceBody = dashboardWindow[hideSurface..toRect];
        int offscreenPosition = hideSurfaceBody.IndexOf(
            "WindowsPopupPositioner.PositionOffscreen(",
            StringComparison.Ordinal);
        int hideWindow = hideSurfaceBody.IndexOf(
            "AppWindow.Hide();",
            StringComparison.Ordinal);
        int releaseShownState = hideSurfaceBody.IndexOf(
            "surfaceShown = false;",
            StringComparison.Ordinal);
        Assert.True(offscreenPosition < hideWindow);
        Assert.True(hideWindow < releaseShownState);
        int tryStartReveal = dashboardWindow.IndexOf(
            "private bool TryStartReveal(",
            StringComparison.Ordinal);
        int advanceReveal = dashboardWindow.IndexOf(
            "private void AdvanceRevealAnimation()",
            tryStartReveal,
            StringComparison.Ordinal);
        string tryStartRevealBody = dashboardWindow[tryStartReveal..advanceReveal];
        Assert.True(
            tryStartRevealBody.IndexOf("Root.UpdateLayout();", StringComparison.Ordinal)
            < tryStartRevealBody.IndexOf(
                "AppWindow.Show(activateWindow: false);",
                StringComparison.Ordinal));
        Assert.Contains("surfaceShown = true;", tryStartRevealBody, StringComparison.Ordinal);
        Assert.Contains("foregroundTimer.IsRepeating = false", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("ScheduleForegroundCheckOrDismiss()", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("ShouldRetryForegroundActivation(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("WindowsPopupPositioner.TryActivate(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow", popupPositioner, StringComparison.Ordinal);
        int onActivated = dashboardWindow.IndexOf(
            "private void OnActivated(",
            StringComparison.Ordinal);
        int moveFocus = dashboardWindow.IndexOf(
            "private void MoveFocusOffInitialTile()",
            onActivated,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Hide(",
            dashboardWindow[onActivated..establishForeground],
            StringComparison.Ordinal);
        Assert.Contains(
            "foregroundTimer.Start();",
            dashboardWindow[onActivated..establishForeground],
            StringComparison.Ordinal);
        int foregroundTimerTick = dashboardWindow.IndexOf(
            "private void OnForegroundTimerTick(",
            moveFocus,
            StringComparison.Ordinal);
        int onClosing = dashboardWindow.IndexOf(
            "private void OnClosing(",
            foregroundTimerTick,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsPopupPositioner.IsShellTrayWindow(foreground)",
            dashboardWindow[foregroundTimerTick..onClosing],
            StringComparison.Ordinal);
        Assert.Contains("GetClassName", popupPositioner, StringComparison.Ordinal);
        Assert.Contains(
            "TrayDashboardInteractionPolicy.IsShellTrayWindowClass",
            popupPositioner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("trayDashboardObservationLoop", mainWindow, StringComparison.Ordinal);
        Assert.Contains("private Task ObserveVisibleSurfacesAsync(", mainWindow, StringComparison.Ordinal);
        Assert.Contains("windowVisible || trayDashboardVisible", mainWindow, StringComparison.Ordinal);
        Assert.Contains("UpdateActiveObservationLoop();", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ObserveTrayDashboardStateAsync", mainWindow, StringComparison.Ordinal);
        int trayDashboardVisibility = mainWindow.IndexOf(
            "public void SetTrayDashboardVisible(bool visible)",
            StringComparison.Ordinal);
        int telemetryPresentation = mainWindow.IndexOf(
            "private void UpdateDashboardTelemetryPresentationState()",
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.SetTrayDashboardTelemetryPresentationActive(visible)",
            mainWindow[trayDashboardVisibility..telemetryPresentation],
            StringComparison.Ordinal);
        int requestTrayRefresh = mainWindow.IndexOf(
            "public void RequestTrayTelemetryRefresh()",
            telemetryPresentation,
            StringComparison.Ordinal);
        string telemetryPresentationBody =
            mainWindow[telemetryPresentation..requestTrayRefresh];
        Assert.Contains(
            "windowVisible && HomePage.Visibility == Visibility.Visible",
            telemetryPresentationBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "trayDashboardVisible",
            telemetryPresentationBody,
            StringComparison.Ordinal);
        int hideDashboard = dashboardWindow.IndexOf(
            "private void Hide(bool suppressNextTrayToggle = false)",
            StringComparison.Ordinal);
        int activatedDashboard = dashboardWindow.IndexOf(
            "private void OnActivated(",
            hideDashboard,
            StringComparison.Ordinal);
        string hideDashboardBody =
            dashboardWindow[hideDashboard..activatedDashboard];
        int startDismiss = hideDashboardBody.IndexOf(
            "TryStartDismissAnimation()",
            StringComparison.Ordinal);
        int reportWithoutAnimation = hideDashboardBody.IndexOf(
            "ReportVisibility(false)",
            StringComparison.Ordinal);
        Assert.True(startDismiss >= 0 && reportWithoutAnimation > startDismiss);
        int revealRendering = dashboardWindow.IndexOf(
            "private void AdvanceRevealAnimation()",
            StringComparison.Ordinal);
        int cancelReveal = dashboardWindow.IndexOf(
            "private void CancelRevealAnimation()",
            revealRendering,
            StringComparison.Ordinal);
        string revealRenderingBody = dashboardWindow[revealRendering..cancelReveal];
        int hiddenDismiss = revealRenderingBody.IndexOf(
            "HideSurface();",
            StringComparison.Ordinal);
        int reportedDismissed = revealRenderingBody.IndexOf(
            "ReportVisibility(false);",
            hiddenDismiss,
            StringComparison.Ordinal);
        Assert.True(hiddenDismiss >= 0 && reportedDismissed > hiddenDismiss);
        int completionTimerTick = revealRenderingBody.IndexOf(
            "private void OnRevealCompletionTimerTick(",
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!revealCompletionPending)",
            revealRenderingBody[completionTimerTick..],
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteRevealAnimation();",
            revealRenderingBody[completionTimerTick..],
            StringComparison.Ordinal);
        int startRevealClock = dashboardWindow.IndexOf(
            "private void StartRevealFrameClock()",
            cancelReveal,
            StringComparison.Ordinal);
        Assert.Contains(
            "revealCompletionPending = false;",
            dashboardWindow[cancelReveal..startRevealClock],
            StringComparison.Ordinal);
        Assert.Contains(
            "revealCompletionTimer.Stop();",
            dashboardWindow[cancelReveal..startRevealClock],
            StringComparison.Ordinal);
        int ensureRevealClock = dashboardWindow.IndexOf(
            "private void EnsureRevealFrameClock()",
            startRevealClock,
            StringComparison.Ordinal);
        Assert.Contains(
            "revealCompletionPending = true;",
            dashboardWindow[startRevealClock..ensureRevealClock],
            StringComparison.Ordinal);
        Assert.Contains(
            "revealCompletionTimer.Start();",
            dashboardWindow[startRevealClock..ensureRevealClock],
            StringComparison.Ordinal);
        int primeFrame = revealRenderingBody.IndexOf(
            "if (!revealHiding && revealPrimingFrames < 2)",
            StringComparison.Ordinal);
        int elapsedFrame = revealRenderingBody.IndexOf(
            "TimeSpan elapsed =",
            primeFrame,
            StringComparison.Ordinal);
        Assert.Contains(
            "revealStartedTimestamp = Stopwatch.GetTimestamp();",
            revealRenderingBody[primeFrame..elapsedFrame],
            StringComparison.Ordinal);
        Assert.Contains(
            "return;",
            revealRenderingBody[primeFrame..elapsedFrame],
            StringComparison.Ordinal);
        Assert.Contains(
            "TryEstablishForeground();",
            revealRenderingBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DisplayInformation.DisplayContentsInvalidated",
            dashboardWindow,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlGrid\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlScroller\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", dashboardXaml, StringComparison.Ordinal);
        int controlGrid = dashboardXaml.IndexOf(
            "x:Name=\"ControlGrid\"",
            StringComparison.Ordinal);
        int performanceTile = dashboardXaml.IndexOf(
            "x:Name=\"PerformanceTile\"",
            controlGrid,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                dashboardXaml[controlGrid..performanceTile],
                "<ColumnDefinition Width=\"*\" />"));
        Assert.Contains("QuickTileSurfaceStyle", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Height\" Value=\"48\" />", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6\" />", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"CornerRadius\" Value=\"8\" />", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains(
            "<x:Double x:Key=\"QuickTileIconSize\">16</x:Double>",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<x:Double x:Key=\"DashboardPrimaryIconSize\">20</x:Double>",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"FontSize\" Value=\"{StaticResource DashboardPrimaryIconSize}\" />",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"12\" />", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"11\" />", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains(
            "<x:Double x:Key=\"SliderHorizontalThumbWidth\">16</x:Double>",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<x:Double x:Key=\"SliderHorizontalThumbHeight\">16</x:Double>",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"SliderTrackThemeHeight\"", dashboardXaml, StringComparison.Ordinal);
        int quickSliderIconStyle = dashboardXaml.IndexOf(
            "<Style x:Key=\"QuickSliderIconStyle\"",
            StringComparison.Ordinal);
        int quickSliderButtonStyle = dashboardXaml.IndexOf(
            "<Style x:Key=\"QuickSliderIconButtonStyle\"",
            quickSliderIconStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"FontSize\" Value=\"{StaticResource DashboardPrimaryIconSize}\" />",
            dashboardXaml[quickSliderIconStyle..quickSliderButtonStyle],
            StringComparison.Ordinal);
        Assert.Contains("Padding=\"20,14,20,10\"", dashboardXaml, StringComparison.Ordinal);
        int dashboardPage = dashboardXaml.IndexOf(
            "<Grid x:Name=\"DashboardPage\">",
            StringComparison.Ordinal);
        int controlScroller = dashboardXaml.IndexOf(
            "<ScrollViewer x:Name=\"ControlScroller\"",
            dashboardPage,
            StringComparison.Ordinal);
        string dashboardRows = dashboardXaml[dashboardPage..controlScroller];
        Assert.Contains("<RowDefinition Height=\"*\" />", dashboardRows, StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", dashboardRows, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"211\"", dashboardRows, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SliderSection\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SliderGrid\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SliderScroller\"", dashboardXaml, StringComparison.Ordinal);
        int sliderSection = dashboardXaml.IndexOf(
            "<Border x:Name=\"SliderSection\"",
            StringComparison.Ordinal);
        int detailPage = dashboardXaml.IndexOf(
            "<Grid x:Name=\"DetailPage\"",
            sliderSection,
            StringComparison.Ordinal);
        string sliderSectionXaml = dashboardXaml[sliderSection..detailPage];
        Assert.Contains("CanToggleGpuClockTuning", sliderSectionXaml, StringComparison.Ordinal);
        Assert.Contains("CanEditGpuClockTuning", sliderSectionXaml, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(sliderSectionXaml, "<ColumnDefinition"));
        Assert.Equal(
            4,
            CountOccurrences(sliderSectionXaml, "<ColumnDefinition Width=\"36\" />"));
        Assert.Equal(4, CountOccurrences(sliderSectionXaml, "ColumnSpacing=\"8\""));
        Assert.Equal(4, CountOccurrences(sliderSectionXaml, "Margin=\"0,0,10,0\""));
        Assert.Contains("Padding=\"12,7\"", sliderSectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"28\" />", sliderSectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=\"2\"", sliderSectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickSliderValueStyle", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TrayDashboardChargeLimitLabel", sliderSectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(" MHz", sliderSectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(" %", sliderSectionXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ChargeLimitSliderRow\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LightingBrightnessSliderRow\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Keyboard brightness\"",
            dashboardXaml,
            StringComparison.Ordinal);
        int lightingBrightnessRow = dashboardXaml.IndexOf(
            "x:Name=\"LightingBrightnessSliderRow\"",
            StringComparison.Ordinal);
        int gpuCoreClockRow = dashboardXaml.IndexOf(
            "x:Name=\"GpuCoreClockSliderRow\"",
            lightingBrightnessRow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Glyph=\"&#xED39;\"",
            dashboardXaml[lightingBrightnessRow..gpuCoreClockRow],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Glyph=\"&#xE706;\"",
            dashboardXaml[lightingBrightnessRow..gpuCoreClockRow],
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GpuCoreClockSliderRow\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GpuMemoryClockSliderRow\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BatteryTile\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BatteryDetail\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BatteryProtectionTile\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FunctionRowTile\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GamingModeTile\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GpuTuningTile\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnFunctionRowCycleClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnGamingModeCycleClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnGamingModeDetailsClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnBatteryProtectionClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding BatteryProtectionEnabled, Mode=OneWay}\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanChangeBatteryProtection, Mode=OneWay}\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BatteryPowerStatus\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PowerSourceStatus\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FanSpeedStatus\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"FansStatus\"", dashboardXaml, StringComparison.Ordinal);
        int statusIconStyle = dashboardXaml.IndexOf(
            "x:Key=\"StatusIconStyle\"",
            StringComparison.Ordinal);
        int endStatusIconStyle = dashboardXaml.IndexOf(
            "x:Key=\"QuickSliderIconButtonStyle\"",
            statusIconStyle,
            StringComparison.Ordinal);
        string statusIconStyleBody = dashboardXaml[statusIconStyle..endStatusIconStyle];
        Assert.Contains(
            "<Setter Property=\"Width\" Value=\"{StaticResource DashboardPrimaryIconSize}\" />",
            statusIconStyleBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Height\" Value=\"{StaticResource DashboardPrimaryIconSize}\" />",
            statusIconStyleBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ColumnDefinition Width=\"{StaticResource DashboardPrimaryIconSize}\" />",
            dashboardXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE713;\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Open OpenBlade\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnSettingsClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"Label\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeChanged=\"OnRootSizeChanged\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("private void OnRootSizeChanged(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailPage\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDetailBackClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPerformanceCycleClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPerformanceDetailsClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnFansCycleClick\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnLightingCycleClick\"", dashboardXaml, StringComparison.Ordinal);
        int fansTile = dashboardXaml.IndexOf(
            "x:Name=\"FansTile\"",
            performanceTile,
            StringComparison.Ordinal);
        int lightingTile = dashboardXaml.IndexOf(
            "x:Name=\"LightingTile\"",
            fansTile,
            StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xEC4A;\"", dashboardXaml[performanceTile..fansTile], StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE7F4;\"", dashboardXaml[performanceTile..fansTile], StringComparison.Ordinal);
        Assert.Contains("Data=\"M8,6.5", dashboardXaml[fansTile..lightingTile], StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE895;\"", dashboardXaml[fansTile..lightingTile], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FansDetailsColumn\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("AvailableActiveFanModeOptions.Count > 1", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("UpdateSliderRows();", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("SliderSection.Visibility = row == 0", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("MaximumVisibleSliders", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Choose up to two sliders", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MaximumVisibleStatusItems", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Choose up to three live readings", mainXaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{x:Bind IsEnabled, Mode=OneWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("gpuClockOffsetCommits.Schedule(requested)", dashboardWindow, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                dashboardWindow,
                "GpuClockOffsetCommitPolicy.CanCommit("));
        Assert.Equal(
            2,
            CountOccurrences(
                mainWindow,
                "GpuClockOffsetCommitPolicy.CanCommit("));
        Assert.Contains("CreateSliderOptions", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardSliderOptionTemplate", mainXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardInteractionPolicy.ResolveNextIndex(", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InitialFocusTarget\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", dashboardXaml[dashboardXaml.IndexOf("x:Name=\"InitialFocusTarget\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("TabIndex=\"2147483647\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("MoveFocusOffInitialTile();", dashboardWindow, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE895;\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(dashboardXaml, "Data=\"M8,6.5"));
        Assert.DoesNotContain("Glyph=\"&#xE9D0;\"", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardBatteryPowerFlowLabel", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardTemperatureLabel", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardPowerSourceLabel", dashboardXaml, StringComparison.Ordinal);
        Assert.Contains("TrayDashboardFanSpeedLabel", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TrayDashboardFanModeLabel", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Customize", dashboardXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preview", dashboardXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ControlGrid.Children.Remove", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlGrid.Children.Add", dashboardWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ExpandedControlContainer\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"More\"", dashboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSizeEnabled", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSizeAvailability", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEffectiveExpandedControl", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("TrayDashboardControlSize", dashboardPreferences, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandedControl", dashboardPreferences, StringComparison.Ordinal);
        Assert.Contains("CurrentSchemaVersion = 2", dashboardPreferences, StringComparison.Ordinal);
        Assert.Contains("The first six controls remain visible.", mainXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticDestinationUsesWindowOwnedWindowsAppSdkPicker()
    {
        string source = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WinUiDiagnosticArchiveDestination.cs");
        Assert.Contains("Microsoft.Windows.Storage.Pickers", source, StringComparison.Ordinal);
        Assert.Contains("new FileSavePicker(windowId)", source, StringComparison.Ordinal);
        Assert.Contains(
            "Path.GetFileNameWithoutExtension(suggestedFileName)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SuggestedFileName = suggestedBaseName", source, StringComparison.Ordinal);
        Assert.Contains("DefaultFileExtension = \".zip\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticArchivePicker_ZipArchives",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ShowOverwritePrompt = true", source, StringComparison.Ordinal);
        Assert.Contains(
            "DiagnosticArchivePicker_Title",
            source,
            StringComparison.Ordinal);
        Assert.Contains("PickSaveFileAsync().AsTask(token)", source, StringComparison.Ordinal);
        Assert.Contains("return result?.Path", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Windows.Storage.Pickers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Win32", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ZipArchive(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".tmp", source, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains(
            "new DiagnosticArchiveSaver(new WinUiDiagnosticArchiveDestination(AppWindow.Id))",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "IServiceClient serviceClient = new ServiceClient();",
            window.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "string uiVersion = ReadPresentedProductVersion(localizationSnapshotMode);",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "uiVersion: uiVersion",
            window,
            StringComparison.Ordinal);
    }

    private static string ProjectPath => GetProjectPath();

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(RepositoryPath(segments));

    private static string RepositoryPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ProjectPath)!,
            "..",
            "..",
            Path.Combine(segments)));

    private static string GetProjectPath([CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testPath)!,
            "..",
            "..",
            "src",
            "OpenBlade.Tray",
            "OpenBlade.Tray.csproj"));

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

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

    private static XElement UidElement(
        XDocument document,
        string elementName,
        string uid) => Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == elementName
            && Attribute(element, "Uid") == uid);

    private static XElement KeyedElement(
        XDocument document,
        string elementName,
        string key) => Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == elementName
            && Attribute(element, "Key") == key);
}
