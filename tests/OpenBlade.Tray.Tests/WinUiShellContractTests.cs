using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
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
        Assert.DoesNotContain("ViewModel.DiscardProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SaveProfilesCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Current display\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnCaptureCurrentDisplaySettings", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveLaptopProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SaveLightingProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.CanSaveCurrentSettingsToProfile", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DashboardPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LightingPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisplayPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(
            xaml,
            "Content=\"{x:Bind ViewModel.SaveToActiveProfileLabel, Mode=OneWay}\""));
        Assert.DoesNotContain("Text=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Not saved\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(xaml, "Content=\"Use profile settings\""));
        Assert.Equal(2, CountOccurrences(
            xaml,
            "Visibility=\"{x:Bind ViewModel.HasLaptopPageProfileDifference, Mode=OneWay}\""));
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
        Assert.Contains("<Setter Property=\"Height\" Value=\"32\" />", xaml, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(xaml, "HeaderActions.Margin\" Value=\"0,2,0,0\""));
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
        Assert.Contains("AutomationProperties.Name=\"Set function key behavior\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GamingModeToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartupAnimationToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{x:Bind FunctionKeyPrimaryStateLabel, Mode=OneWay}\"",
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
            < xaml.IndexOf("AutomationProperties.Name=\"Set function key behavior\"", StringComparison.Ordinal));
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

        int functionKeys = xaml.IndexOf("AutomationProperties.Name=\"Set function key behavior\"", StringComparison.Ordinal);
        int gamingMode = xaml.IndexOf("x:Name=\"GamingModeToggle\"", StringComparison.Ordinal);
        int startupAnimation = xaml.IndexOf("x:Name=\"StartupAnimationToggle\"", StringComparison.Ordinal);
        Assert.True(functionKeys < gamingMode);
        Assert.True(gamingMode < startupAnimation);
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
        Assert.Contains("ViewModel.ShutdownOpenBladeAsync", window, StringComparison.Ordinal);
        Assert.Contains("SessionAgentLauncher.TryStopSibling()", window, StringComparison.Ordinal);
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

        int handler = window.IndexOf(
            "private void OnTestedPlatformWarningClick",
            StringComparison.Ordinal);
        Assert.True(handler >= 0);
        string handlerSource = window[handler..];
        Assert.Contains(
            "MainNavigation.SelectedItem = AboutNavigationItem",
            handlerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AboutTestedPlatformWarning.StartBringIntoView",
            handlerSource,
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
    public void SettingsExposeReorderableTelemetryAndDestructiveShutdown()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml");

        Assert.Contains("ViewModel.DashboardTelemetryItems", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TrayHoverLabelItems", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsControl Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanDragItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CanReorderItems=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ReorderMode=\"Enabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DestructiveButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemFillColorCriticalBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("#47E10C", app, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x:Name=\"SettingsPageHeader\"", xaml, StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(xaml, "Margin=\"0,0,0,20\""));
        Assert.Contains(
            "IsOn=\"{x:Bind ViewModel.AlwaysOverrideExternalLightingChanges, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"OnExternalLightingOverridePolicyToggled\"",
            xaml,
            StringComparison.Ordinal);

        int dashboardSelector = xaml.IndexOf(
            "Text=\"Shown on Dashboard\"",
            StringComparison.Ordinal);
        int previousSubcard = xaml.LastIndexOf(
            "Style=\"{StaticResource SettingsSubcardStyle}\"",
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
        Assert.Contains("'--property:SatelliteResourceLanguages=en-US'", publish, StringComparison.Ordinal);
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
            "<Button x:Name=\"TestedPlatformWarningBadge\"",
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
        Assert.Contains("<TextBlock Text=\"Battery\" Style=\"{StaticResource CardTitleStyle}\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Power mode\"", xaml, StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(xaml, "Style=\"{StaticResource SettingsSubcardStyle}\""));
        Assert.Equal(4, CountOccurrences(xaml, "Style=\"{StaticResource SettingsLastSubcardStyle}\""));
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
        Assert.Contains(
            "!TryGetInputDeviceKind(header.Device",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "nint buffer = Marshal.AllocHGlobal(checked((int)size))",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("!TryGetInputDeviceKind(header.Device", StringComparison.Ordinal)
                < source.IndexOf("EnsureRawInputBuffer(size)", StringComparison.Ordinal),
            "Non-target Raw Input packets must be rejected before the full packet buffer is acquired.");
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
        Assert.Contains("menu.Opening +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("view.Enabled = model.IsEnabled", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.DoubleClick +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("viewModel.TrayHoverLabelText", trayHost, StringComparison.Ordinal);
        Assert.Contains("viewModel.PropertyChanged +=", trayHost, StringComparison.Ordinal);
        Assert.Contains("viewModel.PropertyChanged -=", trayHost, StringComparison.Ordinal);
        Assert.Contains("nameof(MainViewModel.TrayIconState)", trayHost, StringComparison.Ordinal);
        Assert.Contains("OpenBladeIconAssets.LoadTrayIcon", trayHost, StringComparison.Ordinal);
        Assert.Contains("open.Invoke()", trayHost, StringComparison.Ordinal);
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
        Assert.Contains("\"OpenBlade.png\"", notificationPresenter, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("OpenBladeIconAssets.ApplicationIconPath is { } applicationIconPath", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon(applicationIconPath)", window, StringComparison.Ordinal);
        Assert.Contains(
            "args.AddedItems.FirstOrDefault() is not FanModeOption selected",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "selector.SelectedItem is not FanModeOption selected",
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
    public void WinUiAppRoutesEveryExitSourceThroughLifecycleAndGuardsActivation()
    {
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains("exitLifecycle.ExitAsync()", app, StringComparison.Ordinal);
        Assert.Contains("exitLifecycle.TryRunWhileActive", app, StringComparison.Ordinal);
        Assert.Contains("Trace.TraceError", app, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(app, "ExitTrayAsync()") >= 3,
            "The setup event, tray command, and shared method must all reference ExitTrayAsync().");
    }

    [Fact]
    public void RuntimeThemeUsesRootRequestedThemeWithoutLegacyThemeWatchers()
    {
        string themeManager = ReadRepositoryFile("src", "OpenBlade.Tray", "ThemeManager.cs");
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

        string appXaml = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml");
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
            if (!file.EndsWith(
                    "AccentColorManager.cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain("UISettings", source, StringComparison.Ordinal);
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
        Assert.Contains("<TitleBar x:Name=\"AppTitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"OpenBlade\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<NavigationView x:Name=\"MainNavigation\"", xaml, StringComparison.Ordinal);
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
    public void MainWindowLoadsThemeBeforeWatchingAndOwnsPreferenceSaveLifetime()
    {
        string source = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData", source, StringComparison.Ordinal);
        Assert.Contains("\"OpenBlade\"", source, StringComparison.Ordinal);
        Assert.Contains("\"preferences.json\"", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged += OnViewModelPropertyChanged", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged -= OnViewModelPropertyChanged", source, StringComparison.Ordinal);
        Assert.Contains("preferenceSaveQueue.Enqueue", source, StringComparison.Ordinal);
        Assert.Contains("StartMinimized = preferences.StartMinimized", source, StringComparison.Ordinal);
        Assert.Contains("StartMinimizedToggle.IsOn = StartMinimized", source, StringComparison.Ordinal);
        Assert.Contains("\"preference save queue\"", source, StringComparison.Ordinal);
        Assert.Contains("preferenceSaveQueue.DisposeAsync", source, StringComparison.Ordinal);

        int load = source.IndexOf("await preferenceStore.LoadAsync", StringComparison.Ordinal);
        int apply = source.IndexOf("ApplyTheme(ViewModel.Theme)", StringComparison.Ordinal);
        int subscribe = source.IndexOf(
            "ViewModel.PropertyChanged += OnViewModelPropertyChanged",
            StringComparison.Ordinal);
        int watch = source.IndexOf("await ViewModel.StartAsync()", StringComparison.Ordinal);
        Assert.True(load >= 0 && load < apply && apply < subscribe && subscribe < watch);

        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");
        Assert.Contains(
            "windowInitializationTask = mainWindow.InitializeAsync()",
            app,
            StringComparison.Ordinal);
        Assert.Contains("await windowInitializationTask", app, StringComparison.Ordinal);
        Assert.True(
            app.IndexOf("await windowInitializationTask", StringComparison.Ordinal)
            < app.LastIndexOf("exitLifecycle.TryRunWhileActive", StringComparison.Ordinal));
    }

    [Fact]
    public void VisibleObservationFollowsEveryShowAndStopsBeforeHideAndViewModelDisposal()
    {
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        string app = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml.cs");

        Assert.Contains("new VisibleObservationLoop(ViewModel.ObserveStateAsync", window, StringComparison.Ordinal);
        Assert.Contains("new VisibleObservationLoop(ViewModel.ObserveTrayStateAsync", window, StringComparison.Ordinal);
        Assert.DoesNotContain("AppWindow.Show(true)", app, StringComparison.Ordinal);
        int showMethod = window.IndexOf("public void ShowAndActivate()", StringComparison.Ordinal);
        int start = window.IndexOf("visibleObservationLoop.StartOrWake()", showMethod, StringComparison.Ordinal);
        int show = window.IndexOf("AppWindow.Show(true)", showMethod, StringComparison.Ordinal);
        Assert.True(showMethod >= 0 && start > showMethod && show > start);

        int closing = window.IndexOf("private void OnClosing", StringComparison.Ordinal);
        int stop = window.IndexOf("StartHiddenObservation()", closing, StringComparison.Ordinal);
        int hide = window.IndexOf("AppWindow.Hide()", closing, StringComparison.Ordinal);
        Assert.True(closing >= 0 && stop > closing && hide > stop);

        Assert.Contains("window.StartHiddenObservation()", app, StringComparison.Ordinal);
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
        Assert.Contains("DisposeComponentSafelyAsync", window, StringComparison.Ordinal);
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
        Assert.Contains("chargeLimitCommits.Schedule(value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingChargeLimitPercent", source, StringComparison.Ordinal);
        Assert.Contains("command.ExecuteAsync(value)", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeBatteryProtectionToggle(toggle)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LaptopPageHostsPersistentSpecialKeyBindings()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

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
            "AutomationProperties.Name=\"{x:Bind SlotLabel}\"",
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
            "AutomationProperties.GetName(comboBox)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "binding.SelectedAction = selectedAction;",
            window,
            StringComparison.Ordinal);
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
            "FileTypeChoices.Add(\"ZIP archives\", [\".zip\"])",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ShowOverwritePrompt = true", source, StringComparison.Ordinal);
        Assert.Contains(
            "Title = \"Export OpenBlade diagnostics\"",
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
        Assert.DoesNotContain("ZipArchive", source, StringComparison.Ordinal);
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
            "string uiVersion = OpenBladeProductVersion.Read(typeof(MainWindow).Assembly);",
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
}
