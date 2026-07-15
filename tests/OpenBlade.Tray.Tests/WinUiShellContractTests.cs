using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class WinUiShellContractTests
{
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
        Assert.Contains("open.Invoke()", trayHost, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref disposed, 1)", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Visible = false", trayHost, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Dispose()", trayHost, StringComparison.Ordinal);
        Assert.Contains("menu.Dispose()", trayHost, StringComparison.Ordinal);
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
        int apply = source.IndexOf("ThemeManager.Apply", StringComparison.Ordinal);
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
            "new MainViewModel(\n            new ServiceClient(),\n            new WinUiDispatcher(dispatcherQueue),",
            window.Replace("\r\n", "\n", StringComparison.Ordinal),
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
