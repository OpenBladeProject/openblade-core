using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Globalization;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Session;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using XamlButton = Microsoft.UI.Xaml.Controls.Button;
using XamlColumnDefinition = Microsoft.UI.Xaml.Controls.ColumnDefinition;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;
using XamlOrientation = Microsoft.UI.Xaml.Controls.Orientation;
using XamlRowDefinition = Microsoft.UI.Xaml.Controls.RowDefinition;
using XamlStackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using XamlTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;

namespace OpenBlade.Tray;

internal enum StartOpenBladeResult
{
    Started,
    AlreadyRunning,
    Canceled,
}

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string XamlStartupSmokeArgument = "--xaml-startup-smoke";
    private const string ResponsivePageHeaderSmokeArgument =
        "--responsive-page-header-smoke";
    private const string ResponsiveChoiceSelectorSmokeArgument =
        "--responsive-choice-selector-smoke";
    private const string LiveLocalizationSmokeArgument =
        "--live-localization-smoke";
    private const string LocalizationSnapshotDirectoryVariable =
        "OPENBLADE_LOCALIZATION_SNAPSHOT_DIR";
    private const string LocalizationSnapshotPageVariable =
        "OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE";
    private const string LocalizationLanguageVariable =
        "OPENBLADE_LOCALIZATION_LANGUAGE";
    private const string PerformanceNotificationReplacementTag = "performance";
    private static readonly TimeSpan RazerServiceStopCancellationTimeout =
        TimeSpan.FromSeconds(65);
    private static readonly TimeSpan RazerServiceStopHardExitTimeout =
        TimeSpan.FromSeconds(70);
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    private CrashReportWriter? crashReportWriter;
    private IDisposable? traceLogRegistration;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private TrayInstanceCoordinator? instanceCoordinator;
    private MainWindow? mainWindow;
    private TrayDashboardWindow? trayDashboardWindow;
    private WindowsTrayBrightnessOsdHost? brightnessOsdHost;
    private WindowsTrayPerformanceNotificationHost? performanceNotificationHost;
    private WindowsShellThemeSource? trayIconThemeSource;
    private ITrayIconHost? trayIconHost;
    private TrayExitLifecycle? exitLifecycle;
    private TrayExitController? trayExitController;
    private IStartupRegistration? startupRegistration;
    private SessionAgentEventEndpoint? sessionAgentEventEndpoint;
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private WindowsProcessorEfficiencyPolicy processorPolicy = null!;
    private int fatalCrashReported;
    private int languageRefreshInProgress;

    static App()
    {
        ApplyLocalizationPreference();
    }

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLineArguments = Environment.GetCommandLineArgs();
        bool pseudoLocalizationRequested = string.Equals(
                Environment.GetEnvironmentVariable("OPENBLADE_PSEUDO_LOCALIZATION"),
                "1",
                StringComparison.Ordinal);
        string? localizationLanguage = Environment.GetEnvironmentVariable(
            LocalizationLanguageVariable);
        base.OnLaunched(args);
        string? localizationSnapshotDirectory = Environment.GetEnvironmentVariable(
            LocalizationSnapshotDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(localizationSnapshotDirectory))
        {
            _ = RunLocalizationSnapshotAsync(
                localizationSnapshotDirectory,
                pseudoLocalizationRequested,
                localizationLanguage);
            return;
        }
        if (commandLineArguments is
            [_, ResponsivePageHeaderSmokeArgument, string headerSmokeReportPath])
        {
            _ = RunResponsivePageHeaderSmokeAsync(headerSmokeReportPath);
            return;
        }
        if (commandLineArguments is
            [_, ResponsiveChoiceSelectorSmokeArgument, string selectorSmokeReportPath])
        {
            _ = RunResponsiveChoiceSelectorSmokeAsync(selectorSmokeReportPath);
            return;
        }
        if (commandLineArguments is
            [_, LiveLocalizationSmokeArgument, string localizationSmokeReportPath])
        {
            _ = RunLiveLocalizationSmokeAsync(localizationSmokeReportPath);
            return;
        }
        if (commandLineArguments is [_, XamlStartupSmokeArgument, string smokeReportPath])
        {
            try
            {
                RunXamlStartupSmoke(
                    smokeReportPath,
                    pseudoLocalizationRequested,
                    localizationLanguage);
            }
            catch (Exception exception)
            {
                File.WriteAllText(smokeReportPath, exception.ToString());
                Environment.Exit(1);
            }
            return;
        }
        ElevatedServiceHelperParseResult helperParseResult =
            ElevatedServiceControlLauncher.ParseHelperAction(
            commandLineArguments,
            out ElevatedServiceControlAction helperAction);
        bool elevatedServiceHelper = helperParseResult == ElevatedServiceHelperParseResult.Valid;
        string componentName = elevatedServiceHelper
            ? helperAction switch
            {
                ElevatedServiceControlAction.Start => "OpenBlade.Tray.StartHelper",
                ElevatedServiceControlAction.Shutdown => "OpenBlade.Tray.ShutdownHelper",
                ElevatedServiceControlAction.StopRazerServices =>
                    "OpenBlade.Tray.StopRazerServicesHelper",
                _ => throw new ArgumentOutOfRangeException(nameof(helperAction)),
            }
            : "OpenBlade.Tray";
        RegisterCrashReporting(componentName);
        traceLogRegistration ??= PersistentTraceLog.RegisterDefault(
            componentName,
            OpenBladeProductVersion.Read(typeof(App).Assembly));
        if (helperParseResult == ElevatedServiceHelperParseResult.Invalid)
        {
            Trace.TraceWarning("OpenBlade rejected malformed elevated service helper arguments.");
            traceLogRegistration.Dispose();
            traceLogRegistration = null;
            Exit();
            return;
        }
        if (elevatedServiceHelper)
        {
            _ = RunElevatedServiceControlHelperAsync(helperAction);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        var registration = new WindowsStartupRegistration();
        startupRegistration = registration;
        if (!registration.ShouldStartForLaunchArguments(
                commandLineArguments))
        {
            Trace.TraceInformation(
                "OpenBlade skipped the current user's disabled tray startup registration.");
            traceLogRegistration.Dispose();
            traceLogRegistration = null;
            Exit();
            return;
        }

        processorPolicy = WindowsProcessorEfficiencyPolicy.Discover();
        Trace.TraceInformation(
            "OpenBlade processor efficiency policy: {0}",
            processorPolicy.Status.Message);
        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Current user SID is unavailable.");
        using Process currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;
        TrayInstanceEndpoint instanceEndpoint = TrayInstanceEndpoint.Create(userSid, sessionId);
        if (!TrayInstanceCoordinator.TryStart(
            instanceEndpoint,
            () => Enqueue(ShowMainWindow),
            out instanceCoordinator))
        {
            Exit();
            return;
        }

        sessionAgentEventEndpoint =
            SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(userSid),
                sessionId);
        AttachWindowSurfaces(
            CreateMainWindow(registration),
            sessionAgentEventEndpoint);

        exitLifecycle = new TrayExitLifecycle(
            new TrayExitSteps(
                UnregisterShutdown: () =>
                {
                    shutdownRegistration?.Unregister(null);
                    shutdownRegistration = null;
                },
                DisposeShutdownEvent: () =>
                {
                    shutdownEvent?.Dispose();
                    shutdownEvent = null;
                },
                DisposeTrayIcon: () =>
                {
                    trayIconHost?.Dispose();
                    trayIconHost = null;
                    trayIconThemeSource?.Dispose();
                    trayIconThemeSource = null;
                },
                DisposeViewModelAsync: async () =>
                {
                    performanceNotificationHost?.Dispose();
                    performanceNotificationHost = null;
                    brightnessOsdHost?.Dispose();
                    brightnessOsdHost = null;
                    if (mainWindow is not null)
                    {
                        mainWindow.TrayDashboardPreferenceChanged -=
                            OnTrayDashboardPreferenceChanged;
                        mainWindow.TrayDashboardEditSessionChanged -=
                            OnTrayDashboardEditSessionChanged;
                        mainWindow.LanguagePreferenceChanged -=
                            OnLanguagePreferenceChanged;
                    }
                    trayDashboardWindow?.Dispose();
                    trayDashboardWindow = null;
                    if (mainWindow is not null)
                    {
                        await mainWindow.DisposeAsync();
                    }
                },
                CloseWindow: () =>
                {
                    mainWindow?.Close();
                    mainWindow = null;
                },
                DisposeInstanceCoordinatorAsync: async () =>
                {
                    if (instanceCoordinator is not null)
                    {
                        await instanceCoordinator.DisposeAsync();
                        instanceCoordinator = null;
                    }
                },
                ExitApplication: () =>
                {
                    traceLogRegistration?.Dispose();
                    traceLogRegistration = null;
                    Exit();
                }),
            exception => Trace.TraceError(
                "OpenBlade tray shutdown cleanup failed: {0}",
                exception));

        shutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            TrayShutdownEndpoint.ForExecutablePath(executablePath));
        shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            shutdownEvent,
            (_, _) => Enqueue(() => _ = ExitTrayAsync()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        trayIconThemeSource = new WindowsShellThemeSource(uiDispatcherQueue);
        CreateTrayIconHost();
        StartSessionAgent();
        ShowMainWindow(initialLaunch: true);
    }

    private void RunXamlStartupSmoke(
        string smokeReportPath,
        bool pseudoLocalizationRequested,
        string? localizationLanguage)
    {
        if (pseudoLocalizationRequested)
        {
            AssertPseudoResourcesLoaded();
        }
        else if (!string.IsNullOrWhiteSpace(localizationLanguage))
        {
            AssertRequestedResourcesLoaded(localizationLanguage);
        }
        File.WriteAllText(smokeReportPath, "enable visual styles");
        System.Windows.Forms.Application.EnableVisualStyles();
        File.WriteAllText(smokeReportPath, "construct main window");
        var smokeMainWindow = new MainWindow();
        if (pseudoLocalizationRequested ||
            !string.IsNullOrWhiteSpace(localizationLanguage))
        {
            AssertLocalizedXamlApplied(smokeMainWindow);
        }
        File.WriteAllText(smokeReportPath, "construct dashboard window");
        var smokeDashboardWindow = new TrayDashboardWindow(
            smokeMainWindow.ViewModel,
            WindowNative.GetWindowHandle(smokeMainWindow),
            _ => { });
        File.WriteAllText(smokeReportPath, "apply all-visible preferences");
        TrayDashboardPreference defaults = TrayDashboardPreferenceDefaults.Preference;
        smokeDashboardWindow.ApplyPreferences(defaults with
        {
            Controls = defaults.Controls!
                .Select(control => control with { IsVisible = true })
                .ToArray(),
            StatusItems = defaults.StatusItems!
                .Select(status => status with { IsVisible = true })
                .ToArray(),
            Sliders = defaults.Sliders!
                .Select(slider => slider with { IsVisible = true })
                .ToArray(),
        });
        File.WriteAllText(smokeReportPath, "dispose dashboard window");
        smokeDashboardWindow.Dispose();
        File.WriteAllText(smokeReportPath, "close main window");
        smokeMainWindow.Close();
        File.WriteAllText(smokeReportPath, "completed");
        Environment.Exit(0);
    }

    private static void ApplyLocalizationPreference()
    {
        string? language = RequestedLocalizationLanguage();
        if (string.IsNullOrWhiteSpace(language))
        {
            UiLanguagePreference preference =
                UiPreferenceStore.CreateDefault().LoadLanguagePreference();
            language = UiLanguagePreferencePolicy.ResolveCultureName(preference);
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        ApplyLocalizationLanguage(language);
    }

    private static void ApplyRuntimeLocalizationPreference(
        UiLanguagePreference preference)
    {
        string? language = UiLanguagePreferencePolicy.ResolveCultureName(preference);
        if (string.IsNullOrWhiteSpace(language))
        {
            ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            CultureInfo.CurrentUICulture = SystemUiCulture;
            LocalizedResources.SetLanguageOverride(SystemUiCulture.Name);
            return;
        }

        ApplyLocalizationLanguage(language);
    }

    private static void ApplyLocalizationLanguage(string language)
    {
        CultureInfo managedCulture = CultureInfo.GetCultureInfo(language);
        string resourceLanguage = managedCulture.Name;
        ApplicationLanguages.PrimaryLanguageOverride = resourceLanguage;
        CultureInfo.CurrentUICulture = managedCulture;
        LocalizedResources.SetLanguageOverride(resourceLanguage);
    }

    private static string? RequestedLocalizationLanguage() => string.Equals(
                Environment.GetEnvironmentVariable("OPENBLADE_PSEUDO_LOCALIZATION"),
                "1",
                StringComparison.Ordinal)
            ? "qps-ploc"
            : Environment.GetEnvironmentVariable(LocalizationLanguageVariable);

    private static async Task RunLiveLocalizationSmokeAsync(string smokeReportPath)
    {
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            LiveLocalizationSmokeCase[] cases =
            [
                new(
                    UiLanguagePreference.English,
                    "App language",
                    "Use your Windows display language, or choose a language. Changes apply immediately.",
                    "Follow Windows",
                    "Open"),
                new(
                    UiLanguagePreference.SimplifiedChinese,
                    "应用语言",
                    "使用 Windows 显示语言，或选择一种语言。更改会立即生效。",
                    "跟随 Windows",
                    "打开"),
                new(
                    UiLanguagePreference.Russian,
                    "Язык приложения",
                    "Используйте язык интерфейса Windows или выберите язык. Изменения применяются сразу.",
                    "Следовать параметрам Windows",
                    "Открыть"),
            ];

            foreach (LiveLocalizationSmokeCase smokeCase in cases)
            {
                ApplyRuntimeLocalizationPreference(smokeCase.Preference);
                var window = MainWindow.CreateLocalizationSnapshot();
                try
                {
                    AssertLiveLocalizationValue(
                        smokeCase.ExpectedHeading,
                        window.LocalizedAppLanguageHeading,
                        "settings heading");
                    AssertLiveLocalizationValue(
                        smokeCase.ExpectedDescription,
                        window.LocalizedAppLanguageDescription,
                        "settings description");
                    AssertLiveLocalizationValue(
                        smokeCase.ExpectedSystemChoice,
                        window.ViewModel.LanguageSelectionOptions[0].Label,
                        "system language choice");
                    AssertLiveLocalizationValue(
                        smokeCase.ExpectedOpenMenuItem,
                        LocalizedResources.Get("TrayIconMenu_Open", "Open"),
                        "tray menu");
                }
                finally
                {
                    await window.DisposeAsync();
                    window.Close();
                }
            }

            File.WriteAllText(smokeReportPath, "completed");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(smokeReportPath, exception.ToString());
            Environment.Exit(1);
        }
    }

    private static void AssertLiveLocalizationValue(
        string expected,
        string actual,
        string surface)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Live localization did not update the {surface}: "
                + $"expected '{expected}', actual '{actual}'.");
        }
    }

    private async Task RunLocalizationSnapshotAsync(
        string outputDirectory,
        bool pseudoLocalizationRequested,
        string? localizationLanguage)
    {
        string? reportPath = null;
        MainWindow? snapshotMainWindow = null;
        TrayDashboardWindow? snapshotDashboardWindow = null;
        try
        {
            string resolvedDirectory = Path.GetFullPath(outputDirectory);
            reportPath = Path.Combine(resolvedDirectory, "snapshot-report.txt");
            Directory.CreateDirectory(resolvedDirectory);
            if (pseudoLocalizationRequested)
            {
                AssertPseudoResourcesLoaded();
            }
            else if (!string.IsNullOrWhiteSpace(localizationLanguage))
            {
                AssertRequestedResourcesLoaded(localizationLanguage);
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            snapshotMainWindow = MainWindow.CreateLocalizationSnapshot();
            if (pseudoLocalizationRequested ||
                !string.IsNullOrWhiteSpace(localizationLanguage))
            {
                AssertLocalizedXamlApplied(snapshotMainWindow);
            }
            snapshotMainWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
            snapshotMainWindow.Activate();
            await WaitForSnapshotLayoutAsync();
            string[] mainDestinations =
            {
                "home",
                "profiles",
                "laptop",
                "lighting",
                "display",
                "settings",
                "settings-appearance",
                "about",
            };
            string? requestedPage = Environment.GetEnvironmentVariable(
                LocalizationSnapshotPageVariable);
            if (!string.IsNullOrWhiteSpace(requestedPage)
                && !mainDestinations.Contains(requestedPage, StringComparer.Ordinal)
                && !string.Equals(
                    requestedPage,
                    "tray-dashboard",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unknown localization snapshot page '{requestedPage}'.");
            }
            IEnumerable<string> destinations = string.IsNullOrWhiteSpace(requestedPage)
                ? mainDestinations
                : mainDestinations.Where(destination => string.Equals(
                    destination,
                    requestedPage,
                    StringComparison.Ordinal));
            foreach (string destination in destinations)
            {
                Windows.Graphics.SizeInt32 snapshotSize = string.Equals(
                    destination,
                    "settings-appearance",
                    StringComparison.Ordinal)
                    ? new Windows.Graphics.SizeInt32(1920, 1000)
                    : new Windows.Graphics.SizeInt32(1120, 820);
                snapshotMainWindow.AppWindow.Resize(snapshotSize);
                await WaitForSnapshotLayoutAsync();
                string navigationDestination = string.Equals(
                    destination,
                    "settings-appearance",
                    StringComparison.Ordinal)
                    ? "settings"
                    : destination;
                snapshotMainWindow.SelectLocalizationSnapshotPage(navigationDestination);
                await WaitForSnapshotLayoutAsync();
                if (string.Equals(
                    destination,
                    "settings-appearance",
                    StringComparison.Ordinal))
                {
                    snapshotMainWindow.ShowLocalizationSnapshotSettingsAppearance();
                    await WaitForSnapshotLayoutAsync();
                }
                await SaveSnapshotAsync(
                    snapshotMainWindow.Content,
                    Path.Combine(resolvedDirectory, $"main-{destination}.png"));
            }

            if (string.IsNullOrWhiteSpace(requestedPage)
                || string.Equals(
                    requestedPage,
                    "tray-dashboard",
                    StringComparison.Ordinal))
            {
                snapshotDashboardWindow = new TrayDashboardWindow(
                    snapshotMainWindow.ViewModel,
                    WindowNative.GetWindowHandle(snapshotMainWindow),
                    _ => { });
                TrayDashboardPreference defaults = TrayDashboardPreferenceDefaults.Preference;
                snapshotDashboardWindow.ApplyPreferences(defaults with
                {
                    Controls = defaults.Controls!
                        .Select(control => control with { IsVisible = true })
                        .ToArray(),
                    StatusItems = defaults.StatusItems!
                        .Select(status => status with { IsVisible = true })
                        .ToArray(),
                    Sliders = defaults.Sliders!
                        .Select(slider => slider with { IsVisible = true })
                        .ToArray(),
                });
                snapshotDashboardWindow.AppWindow.Resize(
                    new Windows.Graphics.SizeInt32(430, 760));
                snapshotDashboardWindow.Activate();
                await WaitForSnapshotLayoutAsync();
                await SaveSnapshotAsync(
                    snapshotDashboardWindow.Content,
                    Path.Combine(resolvedDirectory, "tray-dashboard.png"));
            }

            await File.WriteAllTextAsync(
                reportPath,
                pseudoLocalizationRequested
                    ? "completed qps-ploc"
                    : $"completed {localizationLanguage ?? "en-US"}");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            if (reportPath is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(reportPath, exception.ToString());
                }
                catch (Exception reportException)
                {
                    Trace.TraceError(
                        "OpenBlade localization snapshot report failed: {0}",
                        reportException);
                }
            }
            else
            {
                Trace.TraceError(
                    "OpenBlade localization snapshot setup failed: {0}",
                    exception);
            }
            Environment.Exit(1);
        }
        finally
        {
            snapshotDashboardWindow?.Dispose();
            snapshotMainWindow?.Close();
        }
    }

    private static async Task WaitForSnapshotLayoutAsync()
    {
        // Let the built-in navigation transition finish before capturing geometry.
        await Task.Delay(1500);
        await Task.Yield();
    }

    private static void AssertPseudoResourcesLoaded()
    {
        const string visibleResourceKey = "MainWindow_ButtonCheckNow.Content";
        string visibleText = LocalizedResources.Get(
            visibleResourceKey,
            "Check now");
        string accessibleName = LocalizedResources.Get(
            "MainWindow_ButtonCheckNow.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
            "Check GitHub for OpenBlade updates");
        string? resolvedLanguage = LocalizedResources.GetResolvedLanguage(
            visibleResourceKey);
        if (!visibleText.StartsWith("[!!", StringComparison.Ordinal)
            || !accessibleName.StartsWith("[!!", StringComparison.Ordinal)
            || !string.Equals(
                resolvedLanguage,
                "qps-ploc",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The qps-ploc resource context did not load known visible and accessibility resources. "
                    + $"Visible='{visibleText}', accessible='{accessibleName}', "
                    + $"override='{ApplicationLanguages.PrimaryLanguageOverride}', "
                    + $"candidate='{resolvedLanguage ?? "<none>"}'.");
        }
    }

    private static void AssertRequestedResourcesLoaded(string language)
    {
        const string visibleResourceKey = "MainWindow_ButtonCheckNow.Content";
        string visibleText = LocalizedResources.Get(visibleResourceKey, "Check now");
        string accessibleName = LocalizedResources.Get(
            "MainWindow_ButtonCheckNow.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
            "Check GitHub for OpenBlade updates");
        string? resolvedLanguage = LocalizedResources.GetResolvedLanguage(
            visibleResourceKey);
        bool supported = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "ru-RU", StringComparison.OrdinalIgnoreCase);
        if (supported)
        {
            if (string.Equals(visibleText, "Check now", StringComparison.Ordinal)
                || string.Equals(
                    accessibleName,
                    "Check GitHub for OpenBlade updates",
                    StringComparison.Ordinal)
                || !string.Equals(
                    resolvedLanguage,
                    language,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The supported '{language}' resource context did not load known visible "
                        + "and accessibility resources. "
                        + $"Visible='{visibleText}', accessible='{accessibleName}', "
                        + $"override='{ApplicationLanguages.PrimaryLanguageOverride}', "
                        + $"candidate='{resolvedLanguage ?? "<none>"}'.");
            }
        }
        else if (!string.Equals(visibleText, "Check now", StringComparison.Ordinal)
            || !string.Equals(
                accessibleName,
                "Check GitHub for OpenBlade updates",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported localization language '{language}' did not fall back to en-US.");
        }
    }

    private static void AssertLocalizedXamlApplied(MainWindow mainWindow)
    {
        string expectedContent = LocalizedResources.Get(
            "MainWindow_ButtonCheckNow.Content",
            "Check now");
        string expectedAccessibleName = LocalizedResources.Get(
            "MainWindow_ButtonCheckNow.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
            "Check GitHub for OpenBlade updates");
        if (!mainWindow.HasLocalizedCheckNowButton(expectedContent, expectedAccessibleName))
        {
            throw new InvalidOperationException(
                "Compiled x:Uid resources did not apply the resolved visible and accessibility text. "
                    + $"Expected content='{expectedContent}', accessible='{expectedAccessibleName}'. "
                    + "The named Settings-page button retained a different value.");
        }
    }

    private static async Task SaveSnapshotAsync(object? content, string path)
    {
        if (content is not UIElement element)
        {
            throw new InvalidOperationException(
                "The localization snapshot window has no renderable content.");
        }

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException(
                "The localization snapshot window produced an empty image.");
        }

        Windows.Storage.Streams.IBuffer pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            stream);
        using var pixelReader = DataReader.FromBuffer(pixels);
        byte[] pixelBytes = new byte[checked((int)pixels.Length)];
        pixelReader.ReadBytes(pixelBytes);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            checked((uint)bitmap.PixelWidth),
            checked((uint)bitmap.PixelHeight),
            96,
            96,
            pixelBytes);
        await encoder.FlushAsync();

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)stream.Size));
        byte[] encoded = new byte[checked((int)stream.Size)];
        reader.ReadBytes(encoded);
        await File.WriteAllBytesAsync(path, encoded);
    }

    private static async Task RunResponsivePageHeaderSmokeAsync(string smokeReportPath)
    {
        try
        {
            File.WriteAllText(smokeReportPath, "construct responsive page header");
            var smokeWindow = new MainWindow();
            FrameworkElement root = smokeWindow.Content as FrameworkElement
                ?? throw new InvalidOperationException("The main window root was not created.");
            Style titleStyle = (Style)root.Resources["PageHeaderTitleStyle"];
            Style saveStyle = (Style)root.Resources["ProfileSaveButtonStyle"];
            Style secondaryStyle = (Style)root.Resources["PageHeaderSecondaryButtonStyle"];

            var header = new ResponsivePageHeader
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Width = 420,
            };
            header.RowDefinitions.Add(new XamlRowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new XamlRowDefinition { Height = GridLength.Auto });
            header.ColumnDefinitions.Add(new XamlColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            header.ColumnDefinitions.Add(new XamlColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new XamlColumnDefinition { Width = GridLength.Auto });

            var title = new XamlTextBlock
            {
                Style = titleStyle,
                Text = "Display",
            };
            ResponsivePageHeader.SetRole(title, ResponsivePageHeaderElementRole.Title);
            header.Children.Add(title);

            var save = new XamlButton
            {
                Content = "Save these current settings to an extraordinarily long USB-C power profile with additional localized wording",
                Style = saveStyle,
                Visibility = Visibility.Visible,
                Width = 300,
            };
            var restore = new XamlButton
            {
                Content = "Restore this extraordinarily long localized profile configuration and discard the current settings",
                Style = secondaryStyle,
                Visibility = Visibility.Collapsed,
                Width = 300,
            };
            var actions = new XamlStackPanel
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Orientation = XamlOrientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Visibility = Visibility.Visible,
            };
            actions.Children.Add(save);
            actions.Children.Add(restore);
            XamlGrid.SetColumn(actions, 1);
            ResponsivePageHeader.SetRole(
                actions,
                ResponsivePageHeaderElementRole.ProfileActions);
            header.Children.Add(actions);

            var customize = new XamlButton
            {
                Content = "Customize",
                Style = secondaryStyle,
            };
            XamlGrid.SetColumn(customize, 2);
            ResponsivePageHeader.SetRole(
                customize,
                ResponsivePageHeaderElementRole.Customize);
            header.Children.Add(customize);

            var host = new XamlGrid
            {
                Padding = new Thickness(24),
            };
            host.Children.Add(header);
            smokeWindow.Content = host;
            smokeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 600));
            var loaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            header.Loaded += (_, _) => loaded.TrySetResult(true);
            smokeWindow.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

            File.WriteAllText(smokeReportPath, "verify save-only horizontal layout");
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(actions),
                "The Save-only action counted its collapsed Restore sibling.");
            RequireResponsivePageHeader(
                save.ActualHeight > 32,
                "The long Save-only action did not wrap at its 300-DIP test width.");
            RequireResponsivePageHeader(
                actions.ActualWidth <= header.ActualWidth + 0.5,
                "The Save-only lower row exceeded the header width.");
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(actions),
                "The long Save-only action changed placement after a repeated layout pass.");

            File.WriteAllText(smokeReportPath, "verify wide and narrow round trip");
            header.Width = 1300;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsTitleRow(actions),
                "The Save-only action did not return to the title row when it fit.");
            save.Width = double.NaN;
            header.Width = 360;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(actions, header.ActualWidth),
                "The Save-only action did not return to the vertical lower row.");

            File.WriteAllText(smokeReportPath, "verify long restore-only layout");
            save.Visibility = Visibility.Collapsed;
            restore.Visibility = Visibility.Visible;
            restore.Width = double.NaN;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(actions, header.ActualWidth),
                "A long Restore-only action did not use the vertical lower row.");
            RequireResponsivePageHeader(
                actions.ActualWidth <= header.ActualWidth + 0.5
                    && restore.ActualWidth <= header.ActualWidth + 0.5
                    && restore.ActualHeight > 32,
                "The long Restore-only action did not wrap within the header width.");
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(actions, header.ActualWidth),
                "The long Restore-only action changed placement after a repeated layout pass.");

            File.WriteAllText(smokeReportPath, "verify empty transition layout");
            restore.Visibility = Visibility.Collapsed;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHiddenProfileActionSlot(actions),
                "An action group with no visible Save or Restore action reserved a lower row.");

            File.WriteAllText(smokeReportPath, "verify restore-only sibling exclusion");
            header.Width = 420;
            restore.Width = 300;
            restore.Visibility = Visibility.Visible;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(actions),
                "The Restore-only action counted its collapsed Save sibling.");

            File.WriteAllText(smokeReportPath, "verify both actions move together");
            save.Width = 300;
            save.Visibility = Visibility.Visible;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(actions, header.ActualWidth),
                "Visible Save and Restore actions did not move together to the vertical lower row.");

            File.WriteAllText(smokeReportPath, "verify complete state round trip");
            restore.Visibility = Visibility.Collapsed;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(actions),
                "The header did not restore its original Save-only horizontal layout.");
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(actions),
                "The restored Save-only layout changed after a repeated layout pass.");

            smokeWindow.Close();
            File.WriteAllText(smokeReportPath, "completed");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(smokeReportPath, exception.ToString());
            Environment.Exit(1);
        }
    }

    private static async Task DrainResponsiveHeaderLayoutAsync(
        ResponsivePageHeader header)
    {
        header.UpdateLayout();
        await DrainDispatcherAsync(header.DispatcherQueue);
        header.UpdateLayout();
        await DrainDispatcherAsync(header.DispatcherQueue);
    }

    private static bool IsTitleRow(XamlStackPanel actions) =>
        XamlGrid.GetRow(actions) == 0
        && XamlGrid.GetColumn(actions) == 1
        && XamlGrid.GetColumnSpan(actions) == 1
        && actions.Orientation == XamlOrientation.Horizontal
        && actions.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Right
        && actions.Margin.Top == 0
        && actions.Margin.Right == PageHeaderLayoutPolicy.InterActionGroupGapDip
        && double.IsPositiveInfinity(actions.MaxWidth);

    private static bool IsHorizontalLowerRow(XamlStackPanel actions) =>
        XamlGrid.GetRow(actions) == 1
        && XamlGrid.GetColumn(actions) == 0
        && XamlGrid.GetColumnSpan(actions) == 3
        && actions.Orientation == XamlOrientation.Horizontal
        && actions.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Stretch
        && actions.Margin.Top == 12
        && actions.Margin.Right == 0
        && double.IsPositiveInfinity(actions.MaxWidth);

    private static bool IsVerticalLowerRow(
        XamlStackPanel actions,
        double availableWidth) =>
        XamlGrid.GetRow(actions) == 1
        && XamlGrid.GetColumn(actions) == 0
        && XamlGrid.GetColumnSpan(actions) == 3
        && actions.Orientation == XamlOrientation.Vertical
        && actions.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Stretch
        && actions.Margin.Top == 12
        && actions.Margin.Right == 0
        && Math.Abs(actions.MaxWidth - availableWidth) < 0.5;

    private static bool IsHiddenProfileActionSlot(XamlStackPanel actions) =>
        XamlGrid.GetRow(actions) == 0
        && XamlGrid.GetColumn(actions) == 1
        && XamlGrid.GetColumnSpan(actions) == 1
        && actions.Orientation == XamlOrientation.Horizontal
        && actions.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Right
        && actions.Margin == new Thickness(0)
        && double.IsPositiveInfinity(actions.MaxWidth);

    private static void RequireResponsivePageHeader(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task RunResponsiveChoiceSelectorSmokeAsync(string smokeReportPath)
    {
        try
        {
            File.WriteAllText(smokeReportPath, "construct responsive selector");
            ResponsiveChoiceSelectorSmokeOption[] originalOptions =
            [
                new("a", "A"),
                new("b", "B"),
                new("c", "C"),
            ];
            var selector = new ResponsiveChoiceSelector
            {
                DisplayMemberPath = nameof(ResponsiveChoiceSelectorSmokeOption.Label),
                ItemsSource = originalOptions,
                SelectedItem = originalOptions[1],
                PlaceholderText = "Checking",
                ExpandedMinWindowWidth = 0,
                ExpandedWidthAllowance = 0,
            };
            int publishedSelections = 0;
            selector.SelectionChanged += (_, _) => publishedSelections++;

            var loaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            selector.Loaded += (_, _) => loaded.TrySetResult(true);
            var smokeWindow = new Microsoft.UI.Xaml.Window
            {
                Content = selector,
            };
            smokeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 600));
            smokeWindow.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);

            var compact = selector.FindName("CompactSelector")
                as Microsoft.UI.Xaml.Controls.ComboBox
                ?? throw new InvalidOperationException("Compact selector was not created.");
            var expanded = selector.FindName("ExpandedSelector")
                as Microsoft.UI.Xaml.Controls.SelectorBar
                ?? throw new InvalidOperationException("Expanded selector was not created.");
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, originalOptions[1]),
                "The initial authoritative selection was not retained.");
            RequireResponsiveSelector(
                ReferenceEquals(compact.SelectedItem, originalOptions[1]),
                "The compact selector did not receive the initial selection.");
            RequireResponsiveSelector(
                ReferenceEquals(
                    StableComboBoxSelection.GetAnchor(compact),
                    originalOptions[1]),
                "The compact selector anchor did not receive the initial selection.");
            RequireResponsiveSelector(
                ReferenceEquals(expanded.SelectedItem?.Tag, originalOptions[1]),
                "The expanded selector did not receive the initial selection.");
            RequireResponsiveSelector(
                publishedSelections == 0,
                "Initialization published a user selection.");
            RequireResponsiveSelector(
                compact.PlaceholderText == "Checking",
                "The compact selector did not receive its placeholder.");

            File.WriteAllText(smokeReportPath, "replace responsive selector options");
            ResponsiveChoiceSelectorSmokeOption[] replacementOptions =
            [
                new("a", "A"),
                new("b", "B"),
                new("c", "C"),
            ];
            selector.ItemsSource = replacementOptions;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, originalOptions[1]),
                "An options refresh replaced the authoritative selection.");
            RequireResponsiveSelector(
                ReferenceEquals(
                    StableComboBoxSelection.GetAnchor(compact),
                    originalOptions[1]),
                "An options refresh replaced the authoritative anchor.");
            RequireResponsiveSelector(
                ReferenceEquals(compact.SelectedItem, replacementOptions[1])
                    && ReferenceEquals(compact.SelectionBoxItem, replacementOptions[1]),
                "The compact selector did not render the equivalent replacement option.");
            RequireResponsiveSelector(
                ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[1]),
                "The expanded selector did not reuse the equivalent replacement option.");
            RequireResponsiveSelector(
                publishedSelections == 0,
                "An options refresh published a user selection.");

            File.WriteAllText(smokeReportPath, "remove authoritative selection from options");
            selector.ItemsSource = new[]
            {
                replacementOptions[0],
                replacementOptions[2],
            };
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, originalOptions[1])
                    && compact.Visibility == Visibility.Visible
                    && expanded.Visibility == Visibility.Collapsed
                    && compact.SelectedItem is null
                    && expanded.SelectedItem is null
                    && publishedSelections == 0,
                "A removed authoritative selection did not use the compact placeholder.");
            selector.ItemsSource = replacementOptions;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Collapsed
                    && expanded.Visibility == Visibility.Visible
                    && ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[1])
                    && publishedSelections == 0,
                "Restoring an equivalent option did not restore the expanded selection.");

            File.WriteAllText(smokeReportPath, "defer compact null selection");
            compact.SelectedItem = null;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, originalOptions[1]),
                "A deferred compact null cleared the authoritative selection.");
            RequireResponsiveSelector(
                ReferenceEquals(
                    StableComboBoxSelection.GetAnchor(compact),
                    originalOptions[1]),
                "A deferred compact null cleared the authoritative anchor.");
            RequireResponsiveSelector(
                ReferenceEquals(compact.SelectedItem, replacementOptions[1]),
                "The compact selection was not restored after a deferred null.");
            RequireResponsiveSelector(
                ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[1]),
                "A deferred compact null cleared the expanded selection.");
            RequireResponsiveSelector(
                publishedSelections == 0,
                "A deferred compact null escaped as a user selection.");

            File.WriteAllText(smokeReportPath, "switch responsive selector power source");
            ResponsiveChoiceSelectorSmokeOption[] batteryOptions =
            [
                new("battery-saver", "Battery Saver"),
                new("battery-balanced", "Battery Balanced"),
            ];
            selector.ExpandedMinWindowWidth = 2000;
            selector.SelectedItem = null;
            selector.ItemsSource = batteryOptions;
            selector.SelectedItem = batteryOptions[0];
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, batteryOptions[0])
                    && ReferenceEquals(
                        StableComboBoxSelection.GetAnchor(compact),
                        batteryOptions[0])
                    && ReferenceEquals(compact.SelectedItem, batteryOptions[0])
                    && ReferenceEquals(compact.SelectionBoxItem, batteryOptions[0])
                    && ReferenceEquals(expanded.SelectedItem?.Tag, batteryOptions[0])
                    && compact.Visibility == Visibility.Visible
                    && expanded.Visibility == Visibility.Collapsed
                    && publishedSelections == 0,
                "A power-source option swap did not restore the compact selection anchor.");
            selector.ExpandedMinWindowWidth = 0;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Collapsed
                    && expanded.Visibility == Visibility.Visible
                    && ReferenceEquals(expanded.SelectedItem?.Tag, batteryOptions[0])
                    && publishedSelections == 0,
                "A power-source option swap did not restore the expanded presentation.");
            selector.ItemsSource = replacementOptions;
            selector.SelectedItem = replacementOptions[1];
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);

            File.WriteAllText(smokeReportPath, "clear authoritative selection");
            selector.SelectedItem = null;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.SelectedItem is null
                    && expanded.SelectedItem is null
                    && StableComboBoxSelection.GetAnchor(compact) is null,
                "An authoritative null did not clear both presentations.");
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Visible
                    && expanded.Visibility == Visibility.Collapsed,
                "An authoritative null did not use the compact presentation.");

            File.WriteAllText(smokeReportPath, "synchronize responsive presentations");
            selector.SelectedItem = replacementOptions[1];
            compact.SelectedItem = replacementOptions[0];
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, replacementOptions[0])
                    && ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[0])
                    && publishedSelections == 1,
                "A compact user selection did not synchronize once.");
            Microsoft.UI.Xaml.Controls.SelectorBarItem expandedOption =
                expanded.Items
                    .OfType<Microsoft.UI.Xaml.Controls.SelectorBarItem>()
                    .Single(item => ReferenceEquals(item.Tag, replacementOptions[2]));
            expanded.SelectedItem = expandedOption;
            RequireResponsiveSelector(
                ReferenceEquals(selector.SelectedItem, replacementOptions[2])
                    && ReferenceEquals(compact.SelectedItem, replacementOptions[2])
                    && publishedSelections == 2,
                "An expanded user selection did not synchronize once.");

            selector.ExpandedMinWindowWidth = 2000;
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Visible
                    && expanded.Visibility == Visibility.Collapsed
                    && ReferenceEquals(compact.SelectedItem, replacementOptions[2]),
                "The compact presentation did not retain selection.");
            selector.ExpandedMinWindowWidth = 0;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Collapsed
                    && expanded.Visibility == Visibility.Visible
                    && ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[2]),
                "The expanded presentation did not retain selection.");

            File.WriteAllText(smokeReportPath, "switch bound responsive selector power source");
            ResponsiveChoiceSelectorSmokeOption[] acOptions =
            [
                new("balanced", "Balanced"),
                new("performance", "Performance"),
            ];
            ResponsiveChoiceSelectorSmokeOption[] boundBatteryOptions =
            [
                new("battery-saver", "Battery Saver"),
                new("battery-balanced", "Battery Balanced"),
            ];
            var boundModel = new ResponsiveChoiceSelectorSmokeModel(
                acOptions,
                acOptions[0]);
            var boundSelector = new ResponsiveChoiceSelector
            {
                DisplayMemberPath = nameof(ResponsiveChoiceSelectorSmokeOption.Label),
                ExpandedMinWindowWidth = 2000,
                ExpandedWidthAllowance = 0,
            };
            boundSelector.SetBinding(
                ResponsiveChoiceSelector.ItemsSourceProperty,
                new Microsoft.UI.Xaml.Data.Binding
                {
                    Source = boundModel,
                    Path = new PropertyPath(nameof(ResponsiveChoiceSelectorSmokeModel.Options)),
                    Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
                });
            boundSelector.SetBinding(
                ResponsiveChoiceSelector.SelectedItemProperty,
                new Microsoft.UI.Xaml.Data.Binding
                {
                    Source = boundModel,
                    Path = new PropertyPath(nameof(ResponsiveChoiceSelectorSmokeModel.Selected)),
                    Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay,
                });
            int boundPublishedSelections = 0;
            ResponsiveChoiceSelectorSmokeOption? boundSelectionDuringPublish = null;
            var boundOwnerCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            boundSelector.SelectionChanged += async (_, args) =>
            {
                boundPublishedSelections++;
                boundSelectionDuringPublish = boundSelector.SelectedItem
                    as ResponsiveChoiceSelectorSmokeOption;
                await Task.Yield();
                boundModel.Select(
                    args.SelectedItem as ResponsiveChoiceSelectorSmokeOption);
                boundSelector.RestoreAuthoritativeSelection();
                boundOwnerCompleted.TrySetResult(true);
            };
            var boundLoaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            boundSelector.Loaded += (_, _) => boundLoaded.TrySetResult(true);
            var boundWindow = new Microsoft.UI.Xaml.Window
            {
                Content = boundSelector,
            };
            boundWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 600));
            boundWindow.Activate();
            await boundLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            var boundCompact = boundSelector.FindName("CompactSelector")
                as Microsoft.UI.Xaml.Controls.ComboBox
                ?? throw new InvalidOperationException("Bound compact selector was not created.");
            var boundExpanded = boundSelector.FindName("ExpandedSelector")
                as Microsoft.UI.Xaml.Controls.SelectorBar
                ?? throw new InvalidOperationException("Bound expanded selector was not created.");
            RequireResponsiveSelector(
                boundSelector.GetBindingExpression(
                    ResponsiveChoiceSelector.SelectedItemProperty) is not null,
                "The bound selector did not establish its runtime selection binding.");
            boundCompact.SelectedItem = acOptions[1];
            await boundOwnerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(boundModel.Selected, acOptions[1])
                    && ReferenceEquals(boundSelectionDuringPublish, acOptions[0])
                    && boundSelector.GetBindingExpression(
                        ResponsiveChoiceSelector.SelectedItemProperty) is not null
                    && boundPublishedSelections == 1,
                "A bound compact user selection did not preserve its owner contract.");
            boundModel.SwitchOptions(boundBatteryOptions, boundBatteryOptions[0]);
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            RequireResponsiveSelector(
                ReferenceEquals(boundSelector.SelectedItem, boundBatteryOptions[0])
                    && ReferenceEquals(
                        StableComboBoxSelection.GetAnchor(boundCompact),
                        boundBatteryOptions[0])
                    && ReferenceEquals(boundCompact.SelectedItem, boundBatteryOptions[0])
                    && ReferenceEquals(boundCompact.SelectionBoxItem, boundBatteryOptions[0])
                    && ReferenceEquals(boundExpanded.SelectedItem?.Tag, boundBatteryOptions[0])
                    && boundPublishedSelections == 1,
                "A bound power-source option swap did not restore the compact selection: "
                    + $"model={boundModel.Selected?.Key}, "
                    + $"control={(boundSelector.SelectedItem as ResponsiveChoiceSelectorSmokeOption)?.Key}, "
                    + $"anchor={(StableComboBoxSelection.GetAnchor(boundCompact) as ResponsiveChoiceSelectorSmokeOption)?.Key}, "
                    + $"compact={(boundCompact.SelectedItem as ResponsiveChoiceSelectorSmokeOption)?.Key}, "
                    + $"selectionBox={(boundCompact.SelectionBoxItem as ResponsiveChoiceSelectorSmokeOption)?.Key}, "
                    + $"expanded={(boundExpanded.SelectedItem?.Tag as ResponsiveChoiceSelectorSmokeOption)?.Key}, "
                    + $"published={boundPublishedSelections}.");
            boundSelector.ExpandedMinWindowWidth = 0;
            await DrainDispatcherAsync(boundSelector.DispatcherQueue);
            RequireResponsiveSelector(
                boundCompact.Visibility == Visibility.Collapsed
                    && boundExpanded.Visibility == Visibility.Visible
                    && ReferenceEquals(boundExpanded.SelectedItem?.Tag, boundBatteryOptions[0]),
                "A bound power-source option swap did not restore the expanded presentation.");
            boundWindow.Close();

            File.WriteAllText(smokeReportPath, "validate width-only presentation");
            selector.ItemsSource = new[] { replacementOptions[2] };
            await DrainDispatcherAsync(selector.DispatcherQueue);
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Collapsed
                    && expanded.Visibility == Visibility.Visible
                    && ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[2]),
                "A fitting option list was compacted because of its item count.");

            File.WriteAllText(smokeReportPath, "validate constrained responsive host");
            ResponsiveChoiceSelectorSmokeOption[] powerModeOptions =
            [
                new("unchanged", "Leave unchanged"),
                new("efficiency", "Best power efficiency"),
                new("balanced", "Balanced"),
                new("performance", "Best performance"),
            ];
            var layoutSelector = new ResponsiveChoiceSelector
            {
                DisplayMemberPath = nameof(ResponsiveChoiceSelectorSmokeOption.Label),
                ItemsSource = powerModeOptions,
                SelectedItem = powerModeOptions[0],
                CompactMinWidth = 210,
                ExpandedMinWindowWidth = 800,
                ExpandedWidthAllowance = 260,
            };
            var supportingContent = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            supportingContent.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Windows power mode",
            });
            supportingContent.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = "Keeps the current Windows setting.",
                TextWrapping = TextWrapping.Wrap,
            });
            var layoutHost = new Microsoft.UI.Xaml.Controls.Grid
            {
                Width = 1400,
                ColumnSpacing = 20,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            };
            layoutHost.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            layoutHost.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition
            {
                Width = GridLength.Auto,
            });
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(layoutSelector, 1);
            layoutHost.Children.Add(supportingContent);
            layoutHost.Children.Add(layoutSelector);

            var layoutLoaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            layoutSelector.Loaded += (_, _) => layoutLoaded.TrySetResult(true);
            var layoutWindow = new Microsoft.UI.Xaml.Window
            {
                Content = layoutHost,
            };
            layoutWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1800, 600));
            layoutWindow.Activate();
            await layoutLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            double rasterizationScale = layoutSelector.XamlRoot?.RasterizationScale ?? 1;
            layoutWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(1400 * rasterizationScale),
                (int)Math.Ceiling(600 * rasterizationScale)));
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);

            var layoutCompact = layoutSelector.FindName("CompactSelector")
                as Microsoft.UI.Xaml.Controls.ComboBox
                ?? throw new InvalidOperationException("Layout compact selector was not created.");
            var layoutExpanded = layoutSelector.FindName("ExpandedSelector")
                as Microsoft.UI.Xaml.Controls.SelectorBar
                ?? throw new InvalidOperationException("Layout expanded selector was not created.");
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Collapsed
                    && layoutExpanded.Visibility == Visibility.Visible,
                "A wide settings row did not use the expanded presentation.");
            double expandedWidth = layoutExpanded.ActualWidth;
            RequireResponsiveSelector(
                double.IsFinite(expandedWidth) && expandedWidth > 0,
                "The expanded presentation did not report a usable width.");

            const double minimumSupportingWidth = 240;
            double constrainedHostWidth = expandedWidth
                + layoutHost.ColumnSpacing
                + minimumSupportingWidth
                - 1;
            layoutHost.Width = constrainedHostWidth;
            layoutHost.UpdateLayout();
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Visible
                    && layoutExpanded.Visibility == Visibility.Collapsed,
                $"A constrained settings row did not use the compact presentation: "
                + $"host={layoutHost.ActualWidth}, supporting={supportingContent.ActualWidth}, "
                + $"expanded={layoutExpanded.ActualWidth}, measured={expandedWidth}.");
            RequireResponsiveSelector(
                supportingContent.ActualWidth >= minimumSupportingWidth,
                "The compact presentation did not preserve supporting content width.");

            File.WriteAllText(smokeReportPath, "restore expanded responsive host");
            double latestExpandedWidth = Math.Max(
                expandedWidth,
                layoutExpanded.ActualWidth);
            layoutHost.Width = latestExpandedWidth
                + layoutHost.ColumnSpacing
                + minimumSupportingWidth
                + 2;
            layoutHost.UpdateLayout();
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Collapsed
                    && layoutExpanded.Visibility == Visibility.Visible
                    && supportingContent.ActualWidth >= minimumSupportingWidth,
                $"A widened settings row did not safely restore the expanded presentation: "
                + $"host={layoutHost.ActualWidth}, supporting={supportingContent.ActualWidth}, "
                + $"expanded={layoutExpanded.ActualWidth}, measured={expandedWidth}.");

            layoutHost.Width = constrainedHostWidth;
            layoutHost.UpdateLayout();
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            await DrainDispatcherAsync(layoutSelector.DispatcherQueue);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Visible
                    && layoutExpanded.Visibility == Visibility.Collapsed
                    && ReferenceEquals(layoutCompact.SelectedItem, powerModeOptions[0]),
                "A second constrained layout did not preserve compact selection.");

            layoutWindow.Close();
            smokeWindow.Close();
            File.WriteAllText(smokeReportPath, "completed");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(smokeReportPath, exception.ToString());
            Environment.Exit(1);
        }
    }

    private static Task DrainDispatcherAsync(DispatcherQueue dispatcherQueue)
    {
        var drained = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => drained.TrySetResult(true)))
        {
            throw new InvalidOperationException("The responsive selector dispatcher is unavailable.");
        }
        return drained.Task;
    }

    private static void RequireResponsiveSelector(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ResponsiveChoiceSelectorSmokeOption(string Key, string Label);

    private sealed record LiveLocalizationSmokeCase(
        UiLanguagePreference Preference,
        string ExpectedHeading,
        string ExpectedDescription,
        string ExpectedSystemChoice,
        string ExpectedOpenMenuItem);

    private sealed class ResponsiveChoiceSelectorSmokeModel(
        IReadOnlyList<ResponsiveChoiceSelectorSmokeOption> options,
        ResponsiveChoiceSelectorSmokeOption? selected)
        : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public IReadOnlyList<ResponsiveChoiceSelectorSmokeOption> Options { get; private set; } =
            options;

        public ResponsiveChoiceSelectorSmokeOption? Selected { get; private set; } = selected;

        public void Select(ResponsiveChoiceSelectorSmokeOption? option)
        {
            Selected = option;
            PropertyChanged?.Invoke(
                this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
        }

        public void SwitchOptions(
            IReadOnlyList<ResponsiveChoiceSelectorSmokeOption> replacement,
            ResponsiveChoiceSelectorSmokeOption selectedOption)
        {
            Select(null);
            Options = replacement;
            PropertyChanged?.Invoke(
                this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(Options)));
            Select(selectedOption);
        }
    }

    private MainWindow CreateMainWindow(
        IStartupRegistration startupRegistration,
        UiLanguagePreference? initialLanguageOverride = null)
    {
        return new MainWindow(
            processorPolicy,
            startupRegistration,
            TryShowTrayNotification,
            initialLanguageOverride: initialLanguageOverride);
    }

    private void AttachWindowSurfaces(
        MainWindow window,
        SessionAgentEventEndpoint eventEndpoint)
    {
        mainWindow = window;
        window.LanguagePreferenceChanged += OnLanguagePreferenceChanged;
        window.TrayDashboardPreferenceChanged += OnTrayDashboardPreferenceChanged;
        window.TrayDashboardEditSessionChanged += OnTrayDashboardEditSessionChanged;
        trayDashboardWindow = new TrayDashboardWindow(
            window.ViewModel,
            WindowNative.GetWindowHandle(window),
            visible => window.SetTrayDashboardVisible(visible));
        trayDashboardWindow.ApplyPreferences(window.TrayDashboardPreference);
        brightnessOsdHost = WindowsTrayBrightnessOsdHost.CreateBestEffort(
            window,
            eventEndpoint,
            exception => Trace.TraceWarning(
                "OpenBlade brightness OSD host failed: {0}",
                exception.Message));
        performanceNotificationHost =
            WindowsTrayPerformanceNotificationHost.CreateBestEffort(
                window,
                eventEndpoint,
                signal => Enqueue(() => ShowPerformanceNotification(signal)),
                exception => Trace.TraceWarning(
                    "OpenBlade performance notification host failed: {0}",
                    exception.Message));
        windowInitializationTask = window.InitializeAsync();
    }

    private void OnLanguagePreferenceChanged(UiLanguagePreference preference)
    {
        if (Interlocked.CompareExchange(ref languageRefreshInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = RefreshLanguageAsync(preference);
    }

    private async Task RefreshLanguageAsync(UiLanguagePreference preference)
    {
        MainWindow? previousWindow = mainWindow;
        IStartupRegistration? registration = startupRegistration;
        SessionAgentEventEndpoint? eventEndpoint = sessionAgentEventEndpoint;
        if (previousWindow is null || registration is null || eventEndpoint is null)
        {
            Interlocked.Exchange(ref languageRefreshInProgress, 0);
            return;
        }

        bool wasVisible = previousWindow.IsWindowVisible;
        PointInt32 position = previousWindow.AppWindow.Position;
        SizeInt32 size = previousWindow.AppWindow.Size;
        string previousLanguageOverride = ApplicationLanguages.PrimaryLanguageOverride;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        MainWindow? replacement = null;

        try
        {
            previousWindow.StartHiddenObservation();
            previousWindow.AppWindow.Hide();
            ApplyRuntimeLocalizationPreference(preference);

            replacement = CreateMainWindow(registration, preference);
            await replacement.InitializeAsync();

            trayIconHost?.Dispose();
            trayIconHost = null;
            performanceNotificationHost?.Dispose();
            performanceNotificationHost = null;
            brightnessOsdHost?.Dispose();
            brightnessOsdHost = null;
            previousWindow.LanguagePreferenceChanged -= OnLanguagePreferenceChanged;
            previousWindow.TrayDashboardPreferenceChanged -=
                OnTrayDashboardPreferenceChanged;
            previousWindow.TrayDashboardEditSessionChanged -=
                OnTrayDashboardEditSessionChanged;
            trayDashboardWindow?.Dispose();
            trayDashboardWindow = null;
            await previousWindow.DisposeAsync();

            if (exitLifecycle?.ExitRequested == true)
            {
                await replacement.DisposeAsync();
                replacement.Close();
                return;
            }

            AttachWindowSurfaces(replacement, eventEndpoint);
            replacement.AppWindow.MoveAndResize(new RectInt32(
                position.X,
                position.Y,
                size.Width,
                size.Height));
            previousWindow.Close();
            CreateTrayIconHost();

            if (wasVisible)
            {
                replacement.ShowAndActivate();
                replacement.ShowLanguageSettings();
                await replacement.StartUpdateCheckingAsync();
            }
            else
            {
                replacement.StartHiddenObservation();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade could not apply the selected language: {0}",
                exception);
            if (replacement is not null && !ReferenceEquals(replacement, mainWindow))
            {
                await replacement.DisposeAsync();
                replacement.Close();
            }
            ApplicationLanguages.PrimaryLanguageOverride = previousLanguageOverride;
            CultureInfo.CurrentUICulture = previousUiCulture;
            LocalizedResources.SetLanguageOverride(previousUiCulture.Name);
            if (ReferenceEquals(mainWindow, previousWindow))
            {
                previousWindow.ShowAndActivate();
            }
        }
        finally
        {
            Interlocked.Exchange(ref languageRefreshInProgress, 0);
        }
    }

    private bool TryShowTrayNotification(TrayNotification notification)
    {
        ITrayIconHost? host = trayIconHost;
        if (host is null)
        {
            return false;
        }

        host.ShowNotification(notification);
        return true;
    }

    private void CreateTrayIconHost()
    {
        if (mainWindow is null || trayIconThemeSource is null)
        {
            return;
        }

        trayExitController = new TrayExitController(
            ShowTrayExitDialog,
            ExitTrayAsync,
            ShutdownOpenBladeAsync);
        MainWindow window = mainWindow;
        trayIconHost = new WinFormsTrayIconHost(
            point => Enqueue(() => _ = ToggleTrayDashboardWhenReadyAsync(point)),
            () => Enqueue(OpenMainWindowFromTrayIcon),
            () => Enqueue(() => _ = RequestTrayExitAsync()),
            window.ViewModel,
            trayIconThemeSource,
            () => Enqueue(window.RequestTrayTelemetryRefresh),
            destination => Enqueue(
                () => _ = ShowNotificationDestinationAsync(destination)));
    }

    private static void StartSessionAgent()
    {
        try
        {
            if (!SessionAgentLauncher.TryStartSibling())
            {
                Trace.TraceWarning(
                    "OpenBlade session agent is not present beside the tray executable.");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade session agent could not be started: {0}",
                exception);
        }
    }

    private void ShowPerformanceNotification(
        PerformanceShortcutNotificationSignal signal)
    {
        if (trayIconHost is null)
        {
            return;
        }

        NotificationCategory category = signal.Kind
            == PerformanceShortcutNotificationKind.Failed
                ? NotificationCategory.OperationFailures
                : NotificationCategory.PerformanceChanges;
        if (mainWindow?.IsNotificationEnabled(category) != true)
        {
            return;
        }

        TrayNotification notification;
        if (signal.Kind == PerformanceShortcutNotificationKind.Failed)
        {
            notification = new TrayNotification(
                LocalizedResources.Get(
                    "App_PerformanceNotification_FailedTitle",
                    "Performance mode could not be changed"),
                LocalizedResources.Get(
                    "App_PerformanceNotification_FailedBody",
                    "OpenBlade could not apply the selected mode."),
                TrayNotificationSeverity.Error,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Troubleshooting);
        }
        else
        {
            string body = signal.Preset is { } preset
                ? LocalizedResources.Format(
                    "App_PerformanceNotification_CurrentMode",
                    "Current mode: {0}",
                    PerformancePresetPresentation.Label(preset))
                : LocalizedResources.Get(
                    "App_PerformanceNotification_ChangedBody",
                    "The performance mode changed.");
            string? logoAssetRelativePath = signal.Preset is { } selectedPreset
                ? PerformanceNotificationIconAsset.Resolve(
                    mainWindow?.ViewModel.ActivePowerSource
                        ?? PowerSource.ExternalUnknown,
                    selectedPreset)
                : null;
            notification = new TrayNotification(
                LocalizedResources.Get(
                    "App_PerformanceNotification_ChangedTitle",
                    "Performance mode changed"),
                body,
                TrayNotificationSeverity.Information,
                LogoAssetRelativePath: logoAssetRelativePath,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Laptop);
        }

        trayIconHost.ShowNotification(notification);
    }

    public Task ExitTrayAsync()
    {
        if (exitLifecycle is null)
        {
            return Task.CompletedTask;
        }

        return exitLifecycle.ExitAsync();
    }

    internal async Task<StartOpenBladeResult> StartOpenBladeAsync()
    {
        WindowsServiceStartPreflightResult preflight =
            await WindowsServiceStartOperation.PreflightAsync(CancellationToken.None);
        if (preflight == WindowsServiceStartPreflightResult.AlreadyRunning)
        {
            return StartOpenBladeResult.AlreadyRunning;
        }
        if (preflight == WindowsServiceStartPreflightResult.Unavailable)
        {
            throw new InvalidOperationException(
                "OpenBlade service is missing, inaccessible, or cannot be started from its current state.");
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var launcher = new ElevatedServiceControlLauncher();
        var coordinator = new TrayServiceStartCoordinator(
            token => launcher.RequestAsync(
                executablePath,
                ElevatedServiceControlAction.Start,
                token),
            WindowsServiceStateConfirmation.WaitForRunningAsync);
        TrayServiceStartResult result = await coordinator.StartAsync(CancellationToken.None);
        if (result == TrayServiceStartResult.Canceled)
        {
            Trace.TraceInformation("OpenBlade service start elevation was canceled.");
            return StartOpenBladeResult.Canceled;
        }
        if (result == TrayServiceStartResult.Unconfirmed)
        {
            throw new InvalidOperationException(
                "The elevated service start could not be confirmed. OpenBlade did not retry the request.");
        }

        return StartOpenBladeResult.Started;
    }

    public async Task ShutdownOpenBladeAsync()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var launcher = new ElevatedServiceControlLauncher();
        var coordinator = new TrayCompleteShutdownCoordinator(
            token => launcher.RequestAsync(
                executablePath,
                ElevatedServiceControlAction.Shutdown,
                token),
            WindowsServiceStateConfirmation.WaitForStoppedAsync,
            () => _ = SessionAgentLauncher.TryStopSibling(),
            ExitTrayAsync);
        TrayCompleteShutdownResult result = await coordinator.ShutdownAsync(
            CancellationToken.None);
        if (result == TrayCompleteShutdownResult.Canceled)
        {
            Trace.TraceInformation("OpenBlade service shutdown elevation was canceled.");
            return;
        }
        if (result == TrayCompleteShutdownResult.Unconfirmed)
        {
            throw new InvalidOperationException(
                "The elevated shutdown result could not be confirmed. OpenBlade did not retry the request.");
        }
    }

    internal async Task<ElevatedServiceControlOutcome> StopRazerServicesAsync()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Tray executable path is unavailable.");
        var launcher = new ElevatedServiceControlLauncher();
        // The helper owns the post-consent deadline. A parent deadline would
        // start before UAC and could publish readback while an approved child
        // was still running.
        return await launcher.RequestAsync(
            executablePath,
            ElevatedServiceControlAction.StopRazerServices,
            CancellationToken.None);
    }

    private async Task RunElevatedServiceControlHelperAsync(
        ElevatedServiceControlAction action)
    {
        int exitCode = 1;
        try
        {
            if (action == ElevatedServiceControlAction.Start)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                await Task.Run(
                    () => WindowsServiceStartOperation.StartAsync(timeout.Token),
                    timeout.Token);
            }
            else if (action == ElevatedServiceControlAction.Shutdown)
            {
                await using var serviceClient = new ServiceClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ControlOperationResult result = await serviceClient.ShutdownOpenBladeAsync(
                    timeout.Token);
                if (result.Status != ControlOperationStatus.Stored)
                {
                    throw new InvalidOperationException(result.Message);
                }
            }
            else if (action == ElevatedServiceControlAction.StopRazerServices)
            {
                using var hardExit = new System.Threading.Timer(
                    _ => Environment.Exit(1),
                    null,
                    RazerServiceStopHardExitTimeout,
                    Timeout.InfiniteTimeSpan);
                using var timeout = new CancellationTokenSource(
                    RazerServiceStopCancellationTimeout);
                RazerServiceStopExecutionResult result = await Task.Run(
                    () => RazerServiceStopOperation.StopAllAsync(timeout.Token),
                    timeout.Token);
                if (!result.Succeeded)
                {
                    string failure = result.InventoryError
                        ?? string.Join(
                            "; ",
                            result.Failures.Select(entry =>
                                $"{entry.DisplayName}: {entry.Message}"));
                    throw new InvalidOperationException(failure);
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(action));
            }

            exitCode = 0;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade elevated service {0} failed: {1}",
                action.ToString().ToLowerInvariant(),
                exception);
        }
        finally
        {
            Environment.ExitCode = exitCode;
            traceLogRegistration?.Dispose();
            traceLogRegistration = null;
            Exit();
        }
    }

    private async Task RequestTrayExitAsync()
    {
        try
        {
            if (trayExitController is null)
            {
                Trace.TraceWarning(
                    "OpenBlade ignored a tray exit request before exit controls were initialized.");
                return;
            }

            await trayExitController.RequestExitAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError("OpenBlade shutdown failed: {0}", exception);
            try
            {
                ShowTrayTaskDialog(new TaskDialogPage
                {
                    Caption = "OpenBlade",
                    Heading = LocalizedResources.Get(
                        "App_ExitError_Heading",
                        "OpenBlade could not shut down"),
                    Text = LocalizedResources.Format(
                        "App_ExitError_Text",
                        "OpenBlade could not shut down: {0}",
                        exception.Message),
                    Buttons = { TaskDialogButton.Close },
                    DefaultButton = TaskDialogButton.Close,
                });
            }
            catch (Exception dialogException)
            {
                Trace.TraceError(
                    "OpenBlade could not show the shutdown error dialog: {0}",
                    dialogException);
            }
        }
    }

    private TrayExitChoice ShowTrayExitDialog()
    {
        var keepRunning = new TaskDialogButton(LocalizedResources.Get(
            "App_ExitDialog_KeepRunning",
            "Keep background features running"));
        var shutDown = new TaskDialogButton(LocalizedResources.Get(
            "App_ExitDialog_ShutDown",
            "Shut down completely"));
        var page = new TaskDialogPage
        {
            Caption = "OpenBlade",
            Heading = LocalizedResources.Get(
                "App_ExitDialog_Heading",
                "Exit OpenBlade?"),
            Text = LocalizedResources.Get(
                "App_ExitDialog_Text",
                "Choose whether keyboard lighting, hotkeys, profiles, and automation should keep running in the background."),
            AllowCancel = true,
            DefaultButton = TaskDialogButton.Cancel,
            Buttons =
            {
                keepRunning,
                shutDown,
                TaskDialogButton.Cancel,
            },
        };

        TaskDialogButton result = ShowTrayTaskDialog(page);
        if (result == keepRunning)
        {
            return TrayExitChoice.KeepBackgroundRunning;
        }

        if (result == shutDown)
        {
            return TrayExitChoice.ShutDownCompletely;
        }

        return TrayExitChoice.Cancel;
    }

    private TaskDialogButton ShowTrayTaskDialog(TaskDialogPage page)
    {
        MainWindow? window = mainWindow;
        if (window?.IsWindowVisible == true)
        {
            return TaskDialog.ShowDialog(
                WindowNative.GetWindowHandle(window),
                page,
                TaskDialogStartupLocation.CenterOwner);
        }

        return TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
    }

    private void ShowMainWindow() => _ = ShowMainWindowWhenReadyAsync(initialLaunch: false);

    private void OpenMainWindowFromTrayIcon()
    {
        trayDashboardWindow?.DismissForMainWindow();
        ShowMainWindow();
    }

    private async Task ToggleTrayDashboardWhenReadyAsync(TrayDashboardPoint point)
    {
        try
        {
            await windowInitializationTask;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade tray dashboard initialization failed: {0}",
                exception);
        }

        if (exitLifecycle is null)
        {
            return;
        }

        exitLifecycle.TryRunWhileActive(() => trayDashboardWindow?.ToggleAt(point));
    }

    private void OnTrayDashboardPreferenceChanged(TrayDashboardPreference preference) =>
        trayDashboardWindow?.ApplyPreferences(preference);

    private void OnTrayDashboardEditSessionChanged(bool editing)
    {
        if (editing)
        {
            trayDashboardWindow?.BeginEditSession();
        }
        else
        {
            trayDashboardWindow?.EndEditSession();
        }
    }

    private async Task ShowNotificationDestinationAsync(
        TrayNotificationDestination destination)
    {
        await ShowMainWindowWhenReadyAsync(initialLaunch: false);
        mainWindow?.NavigateToNotificationDestination(destination);
    }

    private void ShowMainWindow(bool initialLaunch) =>
        _ = ShowMainWindowWhenReadyAsync(initialLaunch);

    private async Task ShowMainWindowWhenReadyAsync(bool initialLaunch)
    {
        try
        {
            await windowInitializationTask;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "OpenBlade window initialization failed; continuing with the usable shell: {0}",
                exception);
        }

        if (exitLifecycle is null)
        {
            return;
        }

        MainWindow? window = mainWindow;
        if (window is null)
        {
            return;
        }

        if (initialLaunch
            && window.StartMinimized
            && !window.UpdateViewModel.NeedsConsentPrompt)
        {
            window.StartHiddenObservation();
            await window.StartUpdateCheckingAsync();
            return;
        }

        bool shown = false;
        exitLifecycle.TryRunWhileActive(() =>
        {
            window.ShowAndActivate();
            shown = true;
        });
        if (shown)
        {
            await window.StartUpdateCheckingAsync();
        }
    }

    private void Enqueue(Action action)
    {
        if (!uiDispatcherQueue.TryEnqueue(() => action()))
        {
            // Shutdown won the race; no UI work remains valid.
        }
    }

    private void OnXamlUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteFatalCrashReport("WinUI unhandled exception", args.Exception);
        // Intentionally leave Handled false so WinUI terminates normally.
    }

    private void RegisterCrashReporting(string componentName)
    {
        if (crashReportWriter is not null)
        {
            return;
        }

        crashReportWriter = CrashReportWriter.CreateDefault(
            componentName,
            OpenBladeProductVersion.Read(typeof(App).Assembly));
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnAppDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        Exception exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException(
                $"Unhandled non-Exception object of type "
                    + (args.ExceptionObject?.GetType().FullName ?? "<null>"));
        WriteFatalCrashReport("AppDomain unhandled exception", exception);
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        crashReportWriter?.TryWrite(
            "Unobserved task exception",
            args.Exception,
            processTerminating: false);
        // Do not call SetObserved; reporting must not change failure semantics.
    }

    private void WriteFatalCrashReport(string source, Exception exception)
    {
        if (Interlocked.CompareExchange(ref fatalCrashReported, 1, 0) != 0)
        {
            return;
        }

        if (crashReportWriter?.TryWrite(source, exception, processTerminating: true) is null)
        {
            Interlocked.Exchange(ref fatalCrashReported, 0);
        }
    }
}
