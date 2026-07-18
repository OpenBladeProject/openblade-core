using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
    [Fact]
    public void ProfilesPageUsesReadOnlySummariesAndOneCurrentDraftReview()
    {
        string xaml = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml");

        Assert.Contains("ViewModel.PluggedInProfileThermalSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.BatteryProfileLightingSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UsbCProfileDisplaySummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SavedActiveProfileSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PendingActiveProfileSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.DiscardProfileDraftCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Save changes\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.ToolTip=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Unsaved changes\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "x:Name=\"LightingEffectPicker\""));
        Assert.DoesNotContain("PluggedInLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BatteryLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UsbCLightingEditor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedManualChangeDurationOption", xaml, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("DisplayViewModel.UserSettingsChanged +=", window, StringComparison.Ordinal);
        Assert.Contains(
            "args.PowerSource,",
            window,
            StringComparison.Ordinal);
        Assert.Contains("ViewModel.SavedDisplayProfileRestoreRequested +=", window, StringComparison.Ordinal);
        Assert.Contains("DisplayViewModel.ApplyProfileAsync(settings, token)", window, StringComparison.Ordinal);
        Assert.Contains("nameof(MainViewModel.ActivePowerSource)", window, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ActiveSavedDisplayProfile", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactiveRawInputKeepsFailedNativeSubclassAliveUntilWindowTeardown()
    {
        string source = ReadRepositoryFile(
            "src",
            "OpenBlade.Tray",
            "WindowsRawKeyboardSource.cs");
        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");

        Assert.Contains(
            "ConcurrentDictionary<SubclassKey, WindowsRawKeyboardSource>",
            source,
            StringComparison.Ordinal);
        Assert.Contains("FailedRemovalKeepAlive[key] = this", source, StringComparison.Ordinal);
        Assert.Contains(
            "RetainIfSubclassRemovalFails(\"registration rollback\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetainIfSubclassRemovalFails(\"disposal\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains("WindowMessageNonClientDestroy", source, StringComparison.Ordinal);
        Assert.Contains("FailedRemovalKeepAlive.TryRemove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rawInput.Keyboard.VirtualKey", source, StringComparison.Ordinal);
        Assert.Contains("reactiveKeyPulsePump.ClearPending()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfContainedPublishRequiresAndCopiesApplicationResourceIndex()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");

        Assert.Contains("Name=\"PublishApplicationResourceIndex\"", project, StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"Publish\"", project, StringComparison.Ordinal);
        Assert.Contains("$(TargetDir)$(AssemblyName).pri", project, StringComparison.Ordinal);
        Assert.Contains("$(PublishDir)$(AssemblyName).pri", project, StringComparison.Ordinal);
        Assert.Contains("<Error", project, StringComparison.Ordinal);
        Assert.Contains("!Exists('$(TargetDir)$(AssemblyName).pri')", project, StringComparison.Ordinal);
        Assert.Contains("<Copy", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayExecutableIsPinnedUnpackagedSelfContainedWinUiAndContainsNoWpf()
    {
        string project = ReadRepositoryFile("src", "OpenBlade.Tray", "OpenBlade.Tray.csproj");
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
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("Include=\"Microsoft.WindowsAppSDK\" Version=\"2.2.0\"", project, StringComparison.Ordinal);
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
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
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
        Assert.Contains("menu.Dispose()", trayHost, StringComparison.Ordinal);

        string window = ReadRepositoryFile("src", "OpenBlade.Tray", "MainWindow.xaml.cs");
        Assert.Contains("OpenBladeIconAssets.ApplicationIconPath is { } applicationIconPath", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon(applicationIconPath)", window, StringComparison.Ordinal);
        Assert.Contains("Root.ActualThemeChanged +=", window, StringComparison.Ordinal);
        Assert.Contains("Root.ActualThemeChanged -=", window, StringComparison.Ordinal);
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

        string appXaml = ReadRepositoryFile("src", "OpenBlade.Tray", "App.xaml");
        Assert.DoesNotContain("RequestedTheme", appXaml, StringComparison.Ordinal);

        string trayRoot = Path.GetDirectoryName(ProjectPath)!;
        foreach (string file in Directory.EnumerateFiles(trayRoot, "*", SearchOption.AllDirectories)
                     .Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml"))
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("Microsoft.Win32", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SystemEvents", source, StringComparison.Ordinal);
            Assert.DoesNotContain("UISettings", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ColorValuesChanged", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Application.RequestedTheme", source, StringComparison.Ordinal);
        }
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
        Assert.Contains("await preferenceSaveQueue.DisposeAsync()", source, StringComparison.Ordinal);

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
        Assert.DoesNotContain("AppWindow.Show(true)", app, StringComparison.Ordinal);
        int showMethod = window.IndexOf("public void ShowAndActivate()", StringComparison.Ordinal);
        int start = window.IndexOf("visibleObservationLoop.StartOrWake()", showMethod, StringComparison.Ordinal);
        int show = window.IndexOf("AppWindow.Show(true)", showMethod, StringComparison.Ordinal);
        Assert.True(showMethod >= 0 && start > showMethod && show > start);

        int closing = window.IndexOf("private void OnClosing", StringComparison.Ordinal);
        int stop = window.IndexOf("visibleObservationLoop.Stop()", closing, StringComparison.Ordinal);
        int hide = window.IndexOf("AppWindow.Hide()", closing, StringComparison.Ordinal);
        Assert.True(closing >= 0 && stop > closing && hide > stop);

        int disposeLoop = window.IndexOf("await visibleObservationLoop.DisposeAsync()", StringComparison.Ordinal);
        int disposeViewModel = window.IndexOf("await ViewModel.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(disposeLoop >= 0 && disposeViewModel > disposeLoop);
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
        int initialShow = app.LastIndexOf("ShowMainWindow();", StringComparison.Ordinal);
        Assert.True(exitLifecycle >= 0 && exitLifecycle < initializationAwait);
        Assert.True(setupShutdown >= 0 && setupShutdown < initializationAwait);
        Assert.True(trayIcon >= 0 && trayIcon < initializationAwait);
        Assert.True(exitLifecycle < initialShow);
        Assert.True(setupShutdown < initialShow);
        Assert.True(trayIcon < initialShow);
        Assert.Contains("exitLifecycle.TryRunWhileActive", app, StringComparison.Ordinal);
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

        Assert.Contains("ProfilesStackThresholdDip = 720", source, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged += OnRootSizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("Root.SizeChanged -= OnRootSizeChanged", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"ProfilesLayout\")", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"ProfilesSecondColumn\")", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"ProfilesThirdColumn\")", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"BatteryProfileCard\")", source, StringComparison.Ordinal);
        Assert.Contains("Root.FindName(\"UsbCProfileCard\")", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(batteryProfileCard, stack ? 1 : 0)", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(batteryProfileCard, stack ? 0 : 1)", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(usbCProfileCard, stack ? 2 : 0)", source, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(usbCProfileCard, stack ? 0 : 2)", source, StringComparison.Ordinal);

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
        Assert.Contains("pendingChargeLimitPercent = value", source, StringComparison.Ordinal);
        Assert.Contains("command.ExecuteAsync(value)", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeBatteryProtectionToggle(toggle)", source, StringComparison.Ordinal);
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
            "IServiceClient serviceClient = new ServiceClient();\n        ViewModel = new MainViewModel(\n            serviceClient,\n            new WinUiDispatcher(dispatcherQueue),",
            window.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "uiVersion: OpenBladeProductVersion.Read(typeof(MainWindow).Assembly)",
            window,
            StringComparison.Ordinal);
    }

    private static string ProjectPath => GetProjectPath();

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ProjectPath)!,
            "..",
            "..",
            Path.Combine(segments))));

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
