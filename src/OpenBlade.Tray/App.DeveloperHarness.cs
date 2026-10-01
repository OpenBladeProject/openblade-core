using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Globalization;
using OpenBlade.Contracts;
#if OPENBLADE_DEVELOPER_HARNESS
using OpenBlade.Localization;
#endif
using OpenBlade.Platform;
using OpenBlade.Session;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using XamlBorder = Microsoft.UI.Xaml.Controls.Border;
using XamlButton = Microsoft.UI.Xaml.Controls.Button;
using XamlColumnDefinition = Microsoft.UI.Xaml.Controls.ColumnDefinition;
using XamlExpander = Microsoft.UI.Xaml.Controls.Expander;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;
using XamlOrientation = Microsoft.UI.Xaml.Controls.Orientation;
using XamlRowDefinition = Microsoft.UI.Xaml.Controls.RowDefinition;
using XamlStackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using XamlTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;

namespace OpenBlade.Tray;

/// <summary>
/// The one hook the product launch path accepts from the live-localization
/// smoke. It is compiled into every build so <c>RefreshLanguageAsync</c> keeps
/// one signature; only harness builds ever create an instance.
/// </summary>
public partial class App
{
    private sealed class LiveLocalizationSmokeContext(
        string reportPath,
        UiPreferenceStore preferenceStore)
    {
        public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

        public UiPreferenceStore PreferenceStore { get; } = preferenceStore;

        public void Report(string phase) => File.AppendAllText(
            reportPath,
            $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}");

        public async Task RunPhaseAsync(
            string phase,
            Func<Task> operation,
            TimeSpan timeout)
        {
            Report(phase);
            try
            {
                await operation().WaitAsync(timeout);
            }
            catch (TimeoutException exception)
            {
                throw new LiveLocalizationSmokePhaseTimeoutException(
                    $"Live localization smoke phase '{phase}' exceeded "
                    + $"{timeout.TotalSeconds:0} seconds.",
                    exception);
            }
        }
    }

    private sealed class LiveLocalizationSmokePhaseTimeoutException(
        string message,
        Exception innerException)
        : TimeoutException(message, innerException);
}

#if OPENBLADE_DEVELOPER_HARNESS
/// <summary>
/// Developer-only smoke and localization-snapshot harnesses. Build checks
/// and the translator workflow start the Tray with one of the arguments or
/// environment variables below; an ordinary launch never enters this file.
/// </summary>
public partial class App
{
    private const string XamlStartupSmokeArgument = "--xaml-startup-smoke";
    private const string ResponsivePageHeaderSmokeArgument =
        "--responsive-page-header-smoke";
    private const string ResponsiveChoiceSelectorSmokeArgument =
        "--responsive-choice-selector-smoke";
    private const string PerformanceRowLayoutSmokeArgument =
        "--performance-row-layout-smoke";
    private const string LiveLocalizationSmokeArgument =
        "--live-localization-smoke";
    private const string LiveLocalizationSmokePreferencePathVariable =
        "OPENBLADE_LIVE_LOCALIZATION_SMOKE_PREFERENCE_PATH";
    private const string LocalizationSnapshotDirectoryVariable =
        "OPENBLADE_LOCALIZATION_SNAPSHOT_DIR";
    private const string LocalizationSnapshotPageVariable =
        "OPENBLADE_LOCALIZATION_SNAPSHOT_PAGE";

    /// <summary>
    /// Runs a developer harness when the launch requests one and returns
    /// whether the harness claimed the launch. The product launch sequence
    /// continues only when this returns <see langword="false"/>.
    /// </summary>
    private bool TryRunDeveloperHarness(string[] commandLineArguments)
    {
        bool pseudoLocalizationRequested = string.Equals(
            Environment.GetEnvironmentVariable("OPENBLADE_PSEUDO_LOCALIZATION"),
            "1",
            StringComparison.Ordinal);
        string? localizationLanguage = Environment.GetEnvironmentVariable(
            LocalizationLanguageVariable);
        string? localizationSnapshotDirectory = Environment.GetEnvironmentVariable(
            LocalizationSnapshotDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(localizationSnapshotDirectory))
        {
            _ = RunLocalizationSnapshotAsync(
                localizationSnapshotDirectory,
                pseudoLocalizationRequested,
                localizationLanguage);
            return true;
        }
        if (commandLineArguments is
            [_, ResponsivePageHeaderSmokeArgument, string headerSmokeReportPath])
        {
            _ = RunResponsivePageHeaderSmokeAsync(headerSmokeReportPath);
            return true;
        }
        if (commandLineArguments is
            [_, ResponsiveChoiceSelectorSmokeArgument, string selectorSmokeReportPath])
        {
            _ = RunResponsiveChoiceSelectorSmokeAsync(selectorSmokeReportPath);
            return true;
        }
        if (commandLineArguments is
            [_, PerformanceRowLayoutSmokeArgument, string performanceRowSmokeReportPath])
        {
            _ = RunPerformanceRowLayoutSmokeAsync(performanceRowSmokeReportPath);
            return true;
        }
        if (commandLineArguments is
            [_, PerformanceRowLayoutSmokeArgument, string performanceRowCapturePath, "--capture"])
        {
            _ = RunPerformanceRowLayoutSmokeAsync(performanceRowCapturePath, captureSnapshots: true);
            return true;
        }
        if (commandLineArguments is
            [_, LiveLocalizationSmokeArgument, string localizationSmokeReportPath])
        {
            _ = RunLiveLocalizationSmokeAsync(
                localizationSmokeReportPath,
                Environment.GetEnvironmentVariable(
                    LiveLocalizationSmokePreferencePathVariable));
            return true;
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
            return true;
        }

        return false;
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


    private async Task RunLiveLocalizationSmokeAsync(
        string smokeReportPath,
        string? preferencePath)
    {
        LiveLocalizationSmokeContext? smokeContext = null;
        try
        {
            smokeContext = new LiveLocalizationSmokeContext(
                smokeReportPath,
                string.IsNullOrWhiteSpace(preferencePath)
                    ? UiPreferenceStore.CreateDefault()
                    : new UiPreferenceStore(preferencePath));
            UnhandledException += (_, eventArgs) => smokeContext.Report(
                $"WinUI unhandled exception: {eventArgs.Exception}");
            smokeContext.Report("initialize smoke infrastructure");
            smokeContext.Report("load expected smoke preferences");
            UiPreferences expectedPreferences = await smokeContext.PreferenceStore
                .LoadAsync()
                .WaitAsync(LiveLocalizationSmokeContext.RefreshTimeout);
            SpecialKeyBindingPreference[] expectedBindings = expectedPreferences
                .EffectiveSpecialKeyBindings
                .ToArray();
            System.Windows.Forms.Application.EnableVisualStyles();
            processorPolicy = WindowsProcessorEfficiencyPolicy.Discover();
            uiDispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException(
                    "The localization smoke test requires a dispatcher thread.");
            startupRegistration = new WindowsStartupRegistration();
            string userSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Current user SID is unavailable.");
            using Process currentProcess = Process.GetCurrentProcess();
            sessionAgentEventEndpoint = SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(userSid),
                currentProcess.SessionId);
            trayIconThemeSource = new WindowsShellThemeSource(uiDispatcherQueue);

            LiveLocalizationSmokeCase[] cases =
            [
                new(
                    UiLanguagePreference.Russian,
                    "Язык приложения",
                    "Используйте язык интерфейса Windows или выберите язык. Изменения применяются сразу.",
                    "Как в Windows",
                    "Открыть",
                    "От аккумулятора",
                    "Сохраняет текущий параметр Windows.",
                    "По умолчанию",
                    "Сочетание клавиш",
                    "Действие клавиши M1"),
                new(
                    UiLanguagePreference.SimplifiedChinese,
                    "应用语言",
                    "使用 Windows 显示语言，或选择一种语言。更改会立即生效。",
                    "跟随 Windows 设置",
                    "打开",
                    "使用电池",
                    "保留当前 Windows 设置。",
                    "默认",
                    "键盘快捷键",
                    "M1 操作"),
                new(
                    UiLanguagePreference.English,
                    "App language",
                    "Use your Windows display language, or choose a language. Changes apply immediately.",
                    "Follow Windows",
                    "Open",
                    "On battery",
                    "Keeps the current Windows setting.",
                    "Default",
                    "Keyboard shortcut",
                    "Action for M1"),
            ];

            ApplyRuntimeLocalizationPreference(cases[0].Preference);
            MainWindow initialWindow = CreateMainWindow(
                startupRegistration,
                cases[0].Preference,
                smokeContext.PreferenceStore,
                UpdateCheckInterval.ManualOnly);
            AttachWindowSurfaces(initialWindow, sessionAgentEventEndpoint);
            await smokeContext.RunPhaseAsync(
                "initialize Russian window",
                () => windowInitializationTask,
                LiveLocalizationSmokeContext.RefreshTimeout);
            initialWindow.ShowAndActivate();
            CreateTrayIconHost();
            AssertLiveLocalizationState(cases[0], previousWindow: null);
            SpecialKeyBindingPreference[] initialBindings = initialWindow
                .SpecialKeyBindings
                .Select(binding => binding.ToPreference())
                .ToArray();
            if (!expectedBindings.SequenceEqual(initialBindings))
            {
                throw new InvalidOperationException(
                    "The localization smoke did not load the isolated M-key preferences.");
            }

            ITrayIconHost initialTrayIcon = trayIconHost
                ?? throw new InvalidOperationException(
                    "The localization smoke test did not create a tray icon.");
            await smokeContext.RunPhaseAsync(
                "refresh Russian to failed Chinese rollback",
                () => RefreshLanguageAsync(
                    UiLanguagePreference.SimplifiedChinese,
                    _ => throw new InvalidOperationException(
                        "Injected replacement tray icon failure."),
                    smokeContext),
                LiveLocalizationSmokeContext.RefreshTimeout);
            if (!ReferenceEquals(mainWindow, initialWindow)
                || !ReferenceEquals(trayIconHost, initialTrayIcon)
                || !initialWindow.IsWindowVisible)
            {
                throw new InvalidOperationException(
                    "A failed language refresh did not preserve the reachable tray shell.");
            }
            AssertLiveLocalizationState(cases[0], previousWindow: null);

            foreach (LiveLocalizationSmokeCase smokeCase in cases[1..])
            {
                MainWindow previousWindow = mainWindow
                    ?? throw new InvalidOperationException(
                        "The localization smoke test lost the main window.");
                SpecialKeyBindingPreference[] previousBindings = previousWindow
                    .SpecialKeyBindings
                    .Select(binding => binding.ToPreference())
                    .ToArray();
                bool expectMaximized = smokeCase.Preference
                    == UiLanguagePreference.SimplifiedChinese;
                if (expectMaximized
                    && previousWindow.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }

                await smokeContext.RunPhaseAsync(
                    $"refresh to {smokeCase.Preference}",
                    () => RefreshLanguageAsync(
                        smokeCase.Preference,
                        liveLocalizationSmokeContext: smokeContext),
                    LiveLocalizationSmokeContext.RefreshTimeout);
                AssertLiveLocalizationState(
                    smokeCase,
                    previousWindow,
                    expectMaximized);
                SpecialKeyBindingPreference[] refreshedBindings = mainWindow!
                    .SpecialKeyBindings
                    .Select(binding => binding.ToPreference())
                    .ToArray();
                if (!previousBindings.SequenceEqual(refreshedBindings))
                {
                    throw new InvalidOperationException(
                        "The language refresh changed invariant M-key bindings.");
                }
            }

            await smokeContext.RunPhaseAsync(
                "dispose live localization smoke surfaces",
                () => DisposeLiveLocalizationSmokeSurfacesAsync(smokeContext),
                LiveLocalizationSmokeContext.CleanupTimeout);
            smokeContext.Report("completed");
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            ReportLiveLocalizationSmokeFailure(
                smokeContext,
                smokeReportPath,
                exception);
            if (exception is LiveLocalizationSmokePhaseTimeoutException)
            {
                Environment.Exit(1);
                return;
            }
            if (smokeContext is not null)
            {
                try
                {
                    await smokeContext.RunPhaseAsync(
                        "dispose live localization smoke surfaces after failure",
                        () => DisposeLiveLocalizationSmokeSurfacesAsync(smokeContext),
                        LiveLocalizationSmokeContext.CleanupTimeout);
                }
                catch (Exception cleanupException)
                {
                    smokeContext.Report($"cleanup failed: {cleanupException}");
                }
            }
            Environment.Exit(1);
        }
    }

    private static void ReportLiveLocalizationSmokeFailure(
        LiveLocalizationSmokeContext? smokeContext,
        string smokeReportPath,
        Exception exception)
    {
        if (smokeContext is not null)
        {
            smokeContext.Report($"failed: {exception}");
            return;
        }

        File.WriteAllText(smokeReportPath, exception.ToString());
    }

    private void AssertLiveLocalizationState(
        LiveLocalizationSmokeCase smokeCase,
        MainWindow? previousWindow,
        bool expectMaximized = false)
    {
        MainWindow window = mainWindow
            ?? throw new InvalidOperationException(
                "The localization smoke test lost the main window.");
        if (previousWindow is not null && ReferenceEquals(window, previousWindow))
        {
            throw new InvalidOperationException(
                "The localization smoke test did not replace the main window.");
        }
        if (!window.IsWindowVisible
            || trayIconHost is not WinFormsTrayIconHost iconHost
            || trayDashboardWindow is null)
        {
            throw new InvalidOperationException(
                "The language refresh did not leave every interactive tray surface reachable.");
        }
        if (expectMaximized
            && window.AppWindow.Presenter is not OverlappedPresenter
            {
                State: OverlappedPresenterState.Maximized,
            })
        {
            throw new InvalidOperationException(
                "The language refresh did not preserve the maximized window state.");
        }

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
            iconHost.OpenMenuText,
            "tray menu");
        AssertLiveLocalizationValue(
            smokeCase.ExpectedBatteryProfile,
            window.ViewModel.ProfileComparisonOptions.Single(
                option => option.Source == PowerSource.Battery).Label,
            "battery profile option");
        AssertLiveLocalizationValue(
            smokeCase.ExpectedWindowsPowerModeDescription,
            window.ViewModel.AvailableWindowsPowerModeOptions[0].Description,
            "Windows power mode description");
        if (window.SpecialKeyBindings.Count != Enum.GetValues<SpecialKeySlot>().Length)
        {
            throw new InvalidOperationException(
                "The language refresh did not preserve every M-key binding row.");
        }

        SpecialKeyBindingViewModel m1 = window.SpecialKeyBindings.Single(
            binding => binding.Slot == SpecialKeySlot.M1);
        AssertLiveLocalizationValue(
            smokeCase.ExpectedDefaultAction,
            m1.AvailableActions.Single(
                option => option.Kind == SpecialKeyActionKind.Default).Label,
            "M1 default action");
        AssertLiveLocalizationValue(
            smokeCase.ExpectedKeyboardShortcutAction,
            m1.AvailableActions.Single(
                option => option.Kind == SpecialKeyActionKind.KeyboardShortcut).Label,
            "M1 keyboard shortcut action");
        AssertLiveLocalizationValue(
            smokeCase.ExpectedActionAccessibleName,
            m1.ActionAccessibleName,
            "M1 action accessibility name");
        AssertLiveLocalizationValue(
            m1.AvailableActions.Single(option => option.Kind == m1.SelectedAction.Kind).Label,
            m1.SelectedAction.Label,
            "M1 selected action");
    }

    private async Task DisposeLiveLocalizationSmokeSurfacesAsync(
        LiveLocalizationSmokeContext smokeContext)
    {
        DisposeSurfaceSafely("localization smoke tray icon", trayIconHost);
        trayIconHost = null;
        DisposeSurfaceSafely(
            "localization smoke performance notification host",
            performanceNotificationHost);
        performanceNotificationHost = null;
        DisposeSurfaceSafely(
            "localization smoke brightness OSD host",
            brightnessOsdHost);
        brightnessOsdHost = null;
        DisposeSurfaceSafely(
            "localization smoke dashboard window",
            trayDashboardWindow);
        trayDashboardWindow = null;
        if (mainWindow is not null)
        {
            DetachWindowEvents(mainWindow);
            smokeContext.Report("dispose current localization smoke window");
            await DisposeWindowSafelyAsync(mainWindow);
            await CloseWindowSafelyAsync(mainWindow);
            mainWindow = null;
        }
        DisposeSurfaceSafely(
            "localization smoke tray icon theme source",
            trayIconThemeSource);
        trayIconThemeSource = null;
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
            ShowNoninteractiveSnapshotWindow(snapshotMainWindow);
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
            bool captureNarrowProfiles = string.Equals(
                requestedPage,
                "profiles-narrow",
                StringComparison.Ordinal);
            bool captureWideProfiles = string.Equals(
                requestedPage,
                "profiles-wide",
                StringComparison.Ordinal);
            bool captureProfilesVariant = captureNarrowProfiles || captureWideProfiles;
            bool captureCoolingPad = string.Equals(requestedPage, "cooling-pad", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(requestedPage)
                && !mainDestinations.Contains(requestedPage, StringComparer.Ordinal)
                && !captureProfilesVariant
                && !captureCoolingPad
                && !string.Equals(
                    requestedPage,
                    "tray-dashboard",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unknown localization snapshot page '{requestedPage}'.");
            }
            IEnumerable<string> destinations = captureProfilesVariant || captureCoolingPad
                ? [requestedPage!]
                : string.IsNullOrWhiteSpace(requestedPage)
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
                    : string.Equals(
                        destination,
                        "profiles-narrow",
                        StringComparison.Ordinal)
                        ? new Windows.Graphics.SizeInt32(800, 820)
                        : string.Equals(
                            destination,
                            "profiles-wide",
                            StringComparison.Ordinal)
                            ? new Windows.Graphics.SizeInt32(1600, 900)
                            : new Windows.Graphics.SizeInt32(1120, 820);
                snapshotMainWindow.AppWindow.Resize(snapshotSize);
                await WaitForSnapshotLayoutAsync();
                string navigationDestination = string.Equals(
                    destination,
                    "settings-appearance",
                    StringComparison.Ordinal)
                    ? "settings"
                    : captureProfilesVariant
                        ? "profiles"
                        : captureCoolingPad
                            ? "laptop"
                            : destination;
                snapshotMainWindow.SelectLocalizationSnapshotPage(navigationDestination);
                await WaitForSnapshotLayoutAsync();
                if (captureCoolingPad)
                {
                    await CaptureCoolingPadCardAsync(snapshotMainWindow, resolvedDirectory);
                    continue;
                }
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
                if (snapshotDashboardWindow.Content is FrameworkElement dashboardRoot)
                {
                    dashboardRoot.RequestedTheme = ElementTheme.Light;
                }
                snapshotDashboardWindow.AppWindow.Resize(
                    new Windows.Graphics.SizeInt32(430, 760));
                ShowNoninteractiveSnapshotWindow(snapshotDashboardWindow);
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

    private static void ShowNoninteractiveSnapshotWindow(Window window)
    {
        ((UIElement)window.Content).IsHitTestVisible = false;
        window.AppWindow.IsShownInSwitchers = false;
        LocalizationSnapshotWindowInput.Apply(WindowNative.GetWindowHandle(window));
        // WinUI requires Activate to initialize x:Bind and templates. The native
        // no-activate style is already installed, so this does not take foreground.
        window.Activate();
        if (WindowsPopupPositioner.GetForegroundWindowHandle() == WindowNative.GetWindowHandle(window))
        {
            throw new InvalidOperationException("A snapshot window unexpectedly took foreground.");
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

        LocalizationSnapshotFrame stableFrame =
            await CaptureCanonicalSnapshotFrameAsync(element);
        var stabilization = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(100);
            LocalizationSnapshotFrame candidate =
                await CaptureCanonicalSnapshotFrameAsync(element);
            if (stableFrame.Width == candidate.Width
                && stableFrame.Height == candidate.Height
                && stableFrame.Pixels.AsSpan().SequenceEqual(candidate.Pixels))
            {
                stableFrame = candidate;
                break;
            }

            if (stabilization.Elapsed >= TimeSpan.FromSeconds(10))
            {
                var stabilizationException = new InvalidOperationException(
                    "The localization snapshot did not produce two consecutive "
                        + "canonical frames within 10 seconds.");
                bool retained = LocalizationSnapshotFailureEvidence.TryWrite(
                    Environment.GetEnvironmentVariable(
                        LocalizationSnapshotFailureEvidence.DirectoryEnvironmentVariable),
                    "tray",
                    Path.GetFileNameWithoutExtension(path),
                    stableFrame.Width,
                    stableFrame.Height,
                    stableFrame.Pixels,
                    candidate.Width,
                    candidate.Height,
                    candidate.Pixels);
                if (!retained)
                {
                    Trace.TraceWarning(
                        "OpenBlade localization snapshot failure evidence could not be retained.");
                }
                throw stabilizationException;
            }

            stableFrame = candidate;
        }

        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            checked((uint)stableFrame.Width),
            checked((uint)stableFrame.Height),
            96,
            96,
            stableFrame.Pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)stream.Size));
        byte[] encoded = new byte[checked((int)stream.Size)];
        reader.ReadBytes(encoded);
        await File.WriteAllBytesAsync(path, encoded);
    }

    private static async Task<LocalizationSnapshotFrame>
        CaptureCanonicalSnapshotFrameAsync(UIElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException(
                "The localization snapshot window produced an empty image.");
        }

        Windows.Storage.Streams.IBuffer pixels = await bitmap.GetPixelsAsync();
        using var pixelReader = DataReader.FromBuffer(pixels);
        byte[] pixelBytes = new byte[checked((int)pixels.Length)];
        pixelReader.ReadBytes(pixelBytes);
        LocalizationSnapshotPixelCanonicalizer.Canonicalize(
            pixelBytes,
            bitmap.PixelWidth,
            bitmap.PixelHeight);
        return new LocalizationSnapshotFrame(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            pixelBytes);
    }

    private readonly record struct LocalizationSnapshotFrame(
        int Width,
        int Height,
        byte[] Pixels);

    private static async Task CaptureCoolingPadCardAsync(MainWindow window, string directory)
    {
        // This window uses the snapshot client; no service or device is opened.
        window.SelectLocalizationSnapshotPage("laptop");
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var status = new ServiceStatusSnapshot(1, false, true, [], null, [], "Snapshot",
            DeviceCapabilities.Unavailable(), null, profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false)
        {
            PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadPresent,
            CoolingPadControl = new(new(CoolingPadControlMode.FixedDemand, new(1600)),
                CoolingPadControlState.Unavailable, CoolingPadControlPauseReason.AdmissionClosed,
                PlannedDemand: new(1350), LastAcknowledgedDemand: new(1500))
            { Connection = CoolingPadConnectionState.PresentExact },
        };
        CoolingPadControlViewModel editor = window.ViewModel.CoolingPadControl;
        editor.Update(status, true);
        await WaitForSnapshotLayoutAsync();
        XamlBorder card = FindPerformanceSmokeDescendant<XamlBorder>(window.Content, "CoolingPadControlCard")
            ?? throw new InvalidOperationException("Cooling-pad snapshot card was not rendered.");
        var parent = VisualTreeHelper.GetParent(card) as XamlStackPanel
            ?? throw new InvalidOperationException("Cooling-pad snapshot card host was not found.");
        // Isolate the actual rendered card so the laptop scroll viewport cannot crop it.
        // Keep its real view model and resource-resolved styles; invoke no UI commands.
        parent.Children.Remove(card);
        card.DataContext = editor;
        card.Margin = new Thickness(16);
        card.VerticalAlignment = VerticalAlignment.Top;
        bool darkTheme = string.Equals(Environment.GetEnvironmentVariable(
            "OPENBLADE_COOLING_PAD_SNAPSHOT_THEME"), "Dark", StringComparison.OrdinalIgnoreCase);
        var background = new SolidColorBrush(darkTheme
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Microsoft.UI.Colors.White);
        var host = new XamlGrid
        {
            Background = background,
            RequestedTheme = darkTheme ? ElementTheme.Dark : ElementTheme.Light,
        };
        var frame = new XamlBorder
        {
            Background = background,
            VerticalAlignment = VerticalAlignment.Top,
            Child = card,
        };
        host.Children.Add(frame);
        window.Content = host;
        double scale = host.XamlRoot?.RasterizationScale ?? 1;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(760 * scale), (int)(1120 * scale)));
        var admitted = status.CoolingPadControl! with
        {
            Access = DeviceFeatureAccess.ReadWrite,
            State = CoolingPadControlState.Paused,
            PauseReason = CoolingPadControlPauseReason.ExplicitResumeRequired,
            ResumeRequired = true,
        };
        var stopped = admitted with
        {
            Requested = CoolingPadControlSettings.Stopped,
            State = CoolingPadControlState.Stopped,
            PauseReason = CoolingPadControlPauseReason.None,
            ResumeRequired = false,
            PlannedDemand = null,
            LastAcknowledgedDemand = null,
        };
        var promptId = Guid.NewGuid();
        var running = admitted with
        {
            State = CoolingPadControlState.Running,
            PauseReason = CoolingPadControlPauseReason.None,
            ResumeRequired = false,
            RetainedEffect = true,
            RetainedSessionResumable = false,
            QuietRestorationAvailable = true,
        };
        var restoring = running with { State = CoolingPadControlState.Paused, QuietRestorationAvailable = false };
        var curveSettings = new CoolingPadControlSettings(CoolingPadControlMode.HostCurve, cpuCurve: new([new(40, new(1000)), new(90, new(2200))]), gpuCurve: new([new(30, new(1000)), new(80, new(2200))]));
        (string Name, CoolingPadControlStatus Status, CoolingPadControlMode Mode)[] states =
        [
            ("stopped", stopped, CoolingPadControlMode.Stop),
            ("fixed", admitted, CoolingPadControlMode.FixedDemand),
            ("curve", admitted with { Requested = curveSettings }, CoolingPadControlMode.HostCurve),
            ("running-fixed", running, CoolingPadControlMode.FixedDemand),
            ("running-curve", running with { Requested = curveSettings }, CoolingPadControlMode.HostCurve),
            ("retained", admitted with { Requested = CoolingPadControlSettings.Stopped, RetainedEffect = true }, CoolingPadControlMode.Stop),
            ("restore", running, CoolingPadControlMode.FixedDemand),
            ("restore-medium", restoring with { Restoration = new(promptId, CoolingPadRestorationStage.Medium, DateTimeOffset.UtcNow.AddMinutes(10)) }, CoolingPadControlMode.FixedDemand),
            ("restore-quiet", restoring with { Restoration = new(promptId, CoolingPadRestorationStage.Quiet, DateTimeOffset.UtcNow.AddMinutes(10)) }, CoolingPadControlMode.FixedDemand),
            ("restore-final", restoring with { Restoration = new(promptId, CoolingPadRestorationStage.Final, DateTimeOffset.UtcNow.AddMinutes(10)) }, CoolingPadControlMode.FixedDemand),
            ("unknown", admitted with { SubmissionStateUnknown = true, PauseReason = CoolingPadControlPauseReason.SubmissionUnknown }, CoolingPadControlMode.FixedDemand),
        ];
        foreach (var state in states)
        {
            editor.Update(status with { CoolingPadControl = state.Status }, true);
            RequireResponsiveSelector(editor.SelectedMode.Mode == state.Mode,
                "Cooling-pad selector must reflect authoritative requested intent without manual selection.");
            if (state.Name == "restore") { editor.IsRestorationGuidanceOpen = true; }
            if (state.Name.StartsWith("running-", StringComparison.Ordinal))
            {
                RequireResponsiveSelector(!editor.IsNoticeVisible && !editor.IsRestorationVisible,
                    "Healthy running control must not show retained-effect warnings or unopened restoration guidance.");
            }
            await WaitForSnapshotLayoutAsync();
            RequireResponsiveSelector(card.ActualWidth > 0 && card.ActualHeight > 0
                && card.ActualHeight + card.Margin.Top + card.Margin.Bottom <= host.ActualHeight,
                "Cooling-pad card must fit completely inside its snapshot host.");
            RequireResponsiveSelector(!editor.PhysicalConfirmation && !editor.RestoreQuietCommand.CanExecute(null),
                "Snapshot must not admit a physical restoration without fresh confirmation.");
            await SaveSnapshotAsync(frame, Path.Combine(directory, $"cooling-pad-{state.Name}.png"));
            if (state.Name == "curve")
            {
                var cpu = FindPerformanceSmokeDescendant<XamlExpander>(card, "CoolingPadCpuCurveExpander")!;
                var gpu = FindPerformanceSmokeDescendant<XamlExpander>(card, "CoolingPadGpuCurveExpander")!;
                cpu.IsExpanded = true;
                gpu.IsExpanded = true;
                await WaitForSnapshotLayoutAsync();
                await SaveSnapshotAsync(frame, Path.Combine(directory, "cooling-pad-curve-expanded.png"));
                cpu.IsExpanded = false;
                gpu.IsExpanded = false;
            }
            if (state.Name == "fixed")
            {
                var details = FindPerformanceSmokeDescendant<XamlExpander>(card, "CoolingPadControlDetailsExpander")!;
                details.IsExpanded = true;
                await WaitForSnapshotLayoutAsync();
                await SaveSnapshotAsync(frame, Path.Combine(directory, "cooling-pad-details.png"));
                details.IsExpanded = false;
            }
        }
        editor.Update(status with
        {
            PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            CoolingPadControl = stopped with { Connection = CoolingPadConnectionState.Absent },
        }, true);
        await WaitForSnapshotLayoutAsync();
        RequireResponsiveSelector(!editor.IsVisible && card.Visibility == Visibility.Collapsed,
            "Confirmed absent cooling pad must hide the card, even with retained settings.");
        await SaveSnapshotAsync(host, Path.Combine(directory, "cooling-pad-absent.png"));
    }

    private static async Task RunPerformanceRowLayoutSmokeAsync(
        string smokeReportPath,
        bool captureSnapshots = false)
    {
        try
        {
            File.WriteAllText(smokeReportPath, "construct laptop performance rows");
            MainWindow smokeWindow = MainWindow.CreateLocalizationSnapshot();
            FrameworkElement root = smokeWindow.Content as FrameworkElement
                ?? throw new InvalidOperationException("The main window root was not created.");
            var loaded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            root.Loaded += (_, _) => loaded.TrySetResult(true);
            smokeWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
            if (captureSnapshots)
            {
                ShowNoninteractiveSnapshotWindow(smokeWindow);
            }
            else
            {
                smokeWindow.Activate();
            }
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            smokeWindow.SelectLocalizationSnapshotPage("laptop");
            await WaitForSnapshotLayoutAsync();

            (XamlBorder performanceRow, XamlStackPanel performanceText,
                ResponsiveChoiceSelector performanceSelector) =
                FindPerformanceSmokeRow(root, "DashboardPerformanceSelector", 2);
            (XamlBorder fanRow, XamlStackPanel fanText,
                ResponsiveChoiceSelector fanSelector) =
                FindPerformanceSmokeRow(root, "DashboardFanModeSelector", 1);
            LaptopPerformanceOption[] performanceOptions =
            [
                new(PerformancePreset.Balanced, "Balanced"),
                new(PerformancePreset.Silent, "Silent"),
                new(PerformancePreset.Custom, "Custom"),
            ];
            FanModeOption[] fanOptions =
            [
                new(FanMode.Automatic, "Automatic"),
                new(FanMode.ManualCurve, "Manual curve"),
                new(FanMode.Maximum, "Maximum"),
            ];
            // Programmatic authoritative selection does not raise the user command event.
            performanceSelector.ItemsSource = performanceOptions;
            performanceSelector.SelectedItem = performanceOptions[0];
            fanSelector.ItemsSource = fanOptions;
            fanSelector.SelectedItem = fanOptions[0];
            XamlTextBlock performanceTitle = performanceText.Children
                .OfType<XamlTextBlock>().First();
            XamlTextBlock hyperBoostDescription = performanceText.Children
                .OfType<XamlTextBlock>().Skip(1).First();
            XamlTextBlock fanTitle = fanText.Children.OfType<XamlTextBlock>().Single();
            XamlStackPanel laptopCardsHost = root.FindName("LaptopCardsHost")
                as XamlStackPanel
                ?? throw new InvalidOperationException(
                    "The rendered MainWindow did not contain the Laptop cards host.");
            Thickness compactPadding = (Thickness)root.Resources[
                "DashboardSingleLineRowPadding"];
            Thickness wrappedPadding = (Thickness)root.Resources[
                "DashboardWrappedTitleRowPadding"];
            double borderHeightDifference = Math.Abs(
                performanceRow.BorderThickness.Top + performanceRow.BorderThickness.Bottom
                    - fanRow.BorderThickness.Top - fanRow.BorderThickness.Bottom);
            double rowHeightTolerance = borderHeightDifference
                + 1 / (root.XamlRoot?.RasterizationScale ?? 1);
            string DescribeRows() => FormattableString.Invariant(
                $"host={laptopCardsHost.ActualWidth:F1}, performance={performanceRow.ActualHeight:F1}/{performanceRow.Padding}, fan={fanRow.ActualHeight:F1}/{fanRow.Padding}, titles={performanceTitle.ActualHeight:F1}/{fanTitle.ActualHeight:F1}, description={hyperBoostDescription.Visibility}/{hyperBoostDescription.ActualHeight:F1}");

            if (captureSnapshots)
            {
                var snapshotBackground = new SolidColorBrush(Microsoft.UI.Colors.White);
                performanceRow.Background = snapshotBackground;
                fanRow.Background = snapshotBackground;
            }

            AnimatedVisibility.SetIsVisible(performanceRow, true);
            AnimatedVisibility.SetIsVisible(fanRow, true);
            AnimatedVisibility.SetIsVisible(hyperBoostDescription, false);
            await WaitForSnapshotLayoutAsync();

            File.WriteAllText(smokeReportPath, "verify wide single-line row parity");
            RequireResponsiveSelector(
                performanceRow.Padding.Equals(compactPadding)
                    && fanRow.Padding.Equals(compactPadding)
                    && Math.Abs(performanceRow.ActualHeight - fanRow.ActualHeight) <= rowHeightTolerance,
                "The wide single-line performance row did not match the fan row: "
                    + DescribeRows());
            if (captureSnapshots)
            {
                await SaveSnapshotAsync(performanceRow, smokeReportPath + ".compact.png");
                await SaveSnapshotAsync(fanRow, smokeReportPath + ".fans.png");
            }

            File.WriteAllText(smokeReportPath, "show HyperBoost supporting copy");
            AnimatedVisibility.SetIsVisible(hyperBoostDescription, true);
            await WaitForSnapshotLayoutAsync();
            RequireResponsiveSelector(
                hyperBoostDescription.Visibility == Visibility.Visible
                    && performanceRow.Padding.Equals(wrappedPadding)
                    && performanceRow.ActualHeight > fanRow.ActualHeight + 1,
                "The HyperBoost description did not expand the performance row: "
                    + DescribeRows());
            if (captureSnapshots)
            {
                await SaveSnapshotAsync(performanceRow, smokeReportPath + ".description.png");
            }

            File.WriteAllText(smokeReportPath, "hide HyperBoost supporting copy");
            AnimatedVisibility.SetIsVisible(hyperBoostDescription, false);
            await WaitForSnapshotLayoutAsync();
            RequireResponsiveSelector(
                hyperBoostDescription.Visibility == Visibility.Collapsed
                    && performanceRow.Padding.Equals(compactPadding)
                    && Math.Abs(performanceRow.ActualHeight - fanRow.ActualHeight) <= rowHeightTolerance,
                "The performance row did not return to its compact height: "
                    + DescribeRows());

            File.WriteAllText(smokeReportPath, "constrain laptop cards");
            laptopCardsHost.Width = 310;
            laptopCardsHost.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            await WaitForSnapshotLayoutAsync();
            RequireResponsiveSelector(
                performanceTitle.ActualHeight > fanTitle.ActualHeight + 1
                    && performanceRow.Padding.Equals(wrappedPadding)
                    && fanRow.Padding.Equals(compactPadding),
                "The constrained performance title did not use wrapped-row padding: "
                    + DescribeRows());
            if (captureSnapshots)
            {
                await SaveSnapshotAsync(performanceRow, smokeReportPath + ".wrapped.png");
            }

            File.WriteAllText(smokeReportPath, "verify HyperBoost warning and recovery");
            laptopCardsHost.Width = 450;
            await VerifyHyperBoostWarningLayoutAsync(
                smokeWindow, root, smokeReportPath, captureSnapshots);

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

    private static async Task VerifyHyperBoostWarningLayoutAsync(
        MainWindow window,
        FrameworkElement root,
        string reportPath,
        bool captureSnapshots)
    {
        // Snapshot windows never start their service client. Feed the real
        // presentation path with synthetic status, without any device access.
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        ServiceStatusSnapshot status = new(
            1, true, false,
            ["Razer Chroma SDK Service", "RazerAppEngine", "Razer Gaming Services Updater", "razerwdl"],
            null, [], "Ready",
            DeviceCapabilities.SafeTargetDefaults() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostWithoutCoolingPadAccess = DeviceFeatureAccess.ReadWrite,
            },
            new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, null, null)
            {
                HyperBoostActive = false,
            },
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false)
        {
            PadlessHyperBoostEligibility = PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            CoolingPadControl = new(new(CoolingPadControlMode.FixedDemand, new(1600)),
                CoolingPadControlState.Unavailable, CoolingPadControlPauseReason.AdmissionClosed,
                PlannedDemand: new(1350), LastAcknowledgedDemand: new(1500))
            { Connection = CoolingPadConnectionState.PresentExact },
        };
        System.Reflection.MethodInfo applyStatus = typeof(MainViewModel).GetMethod(
            "ApplyStatus",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Status presentation hook was not found.");
        applyStatus.Invoke(window.ViewModel, [status, false]);
        await WaitForSnapshotLayoutAsync();
        Microsoft.UI.Xaml.Controls.InfoBar warning =
            FindPerformanceSmokeDescendant<Microsoft.UI.Xaml.Controls.InfoBar>(
                root, "HyperBoostUnavailableInfoBar")
            ?? throw new InvalidOperationException("The HyperBoost warning was not rendered.");
        RequireResponsiveSelector(
            warning.IsOpen && warning.ActualHeight > 0
                && warning.Message.Contains("RazerAppEngine", StringComparison.Ordinal)
                && !warning.Message.Contains("Razer Chroma SDK Service", StringComparison.Ordinal)
                && !warning.Message.Contains("Razer Gaming Services Updater", StringComparison.Ordinal)
                && !warning.Message.Contains("razerwdl", StringComparison.Ordinal),
            "The HyperBoost warning did not show the thermal blockers.");
        if (captureSnapshots)
        {
            await SaveSnapshotAsync(root, reportPath + ".hyperboost-main.png");
        }

        var popup = new TrayDashboardWindow(
            window.ViewModel,
            WindowNative.GetWindowHandle(window),
            _ => { });
        double scale = root.XamlRoot?.RasterizationScale ?? 1;
        popup.AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(360 * scale),
            (int)(TrayDashboardLayoutPolicy.CalculatePreferredHeight() * scale)));
        if (captureSnapshots)
        {
            ShowNoninteractiveSnapshotWindow(popup);
        }
        else
        {
            popup.Activate();
        }
        System.Reflection.MethodInfo showDetail = typeof(TrayDashboardWindow).GetMethod(
            "ShowDetail",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Performance detail hook was not found.");
        showDetail.Invoke(popup, [TrayDashboardControlKind.Performance, "Performance"]);
        await WaitForSnapshotLayoutAsync();
        FrameworkElement popupRoot = (FrameworkElement)popup.Content;
        Microsoft.UI.Xaml.Controls.InfoBar popupWarning =
            FindPerformanceSmokeDescendant<Microsoft.UI.Xaml.Controls.InfoBar>(
                popupRoot, "TrayHyperBoostUnavailableInfoBar")
            ?? throw new InvalidOperationException("The tray HyperBoost warning was not rendered.");
        RequireResponsiveSelector(
            popupWarning.IsOpen && popupWarning.ActualHeight > 0
                && popupWarning.ActualWidth > 0
                && popupWarning.ActualWidth <= popupRoot.ActualWidth - 27,
            "The tray HyperBoost warning was missing or wider than its viewport.");
        if (captureSnapshots)
        {
            await SaveSnapshotAsync(popupRoot, reportPath + ".hyperboost-tray.png");
        }

        applyStatus.Invoke(window.ViewModel, [status with
        {
            Revision = 2,
            PotentialControllers = ["Razer Chroma SDK Service"],
        }, false]);
        await WaitForSnapshotLayoutAsync();
        RequireResponsiveSelector(
            window.ViewModel.CanToggleHyperBoost && !warning.IsOpen && !popupWarning.IsOpen,
            "Clearing thermal blockers did not remove both HyperBoost warnings.");
        popup.Close();
    }

    private static (XamlBorder Row, XamlStackPanel Text, ResponsiveChoiceSelector Selector)
        FindPerformanceSmokeRow(
        DependencyObject root,
        string selectorName,
        int expectedTextBlocks)
    {
        ResponsiveChoiceSelector selector =
            FindPerformanceSmokeDescendant<ResponsiveChoiceSelector>(root, selectorName)
            ?? throw new InvalidOperationException($"Missing '{selectorName}'.");
        XamlGrid grid = FindPerformanceSmokeAncestor<XamlGrid>(selector)
            ?? throw new InvalidOperationException($"Missing '{selectorName}' row grid.");
        XamlBorder row = FindPerformanceSmokeAncestor<XamlBorder>(grid)
            ?? throw new InvalidOperationException($"Missing '{selectorName}' row border.");
        XamlStackPanel? text = grid.Children.OfType<XamlStackPanel>()
            .SingleOrDefault(candidate =>
                XamlGrid.GetColumn(candidate) == 0
                && candidate.Children.OfType<XamlTextBlock>().Count() == expectedTextBlocks);
        return (row, text ?? throw new InvalidOperationException(
            $"The rendered '{selectorName}' did not have its expected text stack."), selector);
    }

    private static T? FindPerformanceSmokeDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match
                && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            T? descendant = FindPerformanceSmokeDescendant<T>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindPerformanceSmokeAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        DependencyObject current = child;
        while (VisualTreeHelper.GetParent(current) is DependencyObject parent)
        {
            if (parent is T match)
            {
                return match;
            }

            current = parent;
        }

        return null;
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
            customize.Visibility = Visibility.Collapsed;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsTitleRow(actions, hasCustomizeAction: false),
                "The title-row action gap remained after Customize was hidden.");
            customize.Visibility = Visibility.Visible;
            await DrainResponsiveHeaderLayoutAsync(header);
            RequireResponsivePageHeader(
                IsTitleRow(actions),
                "The title-row action gap was not restored with Customize.");
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

            File.WriteAllText(smokeReportPath, "verify header without customize action");
            var profilesHeader = new ResponsivePageHeader
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Width = 700,
            };
            profilesHeader.RowDefinitions.Add(
                new XamlRowDefinition { Height = GridLength.Auto });
            profilesHeader.RowDefinitions.Add(
                new XamlRowDefinition { Height = GridLength.Auto });
            profilesHeader.ColumnDefinitions.Add(new XamlColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            profilesHeader.ColumnDefinitions.Add(
                new XamlColumnDefinition { Width = GridLength.Auto });
            profilesHeader.ColumnDefinitions.Add(
                new XamlColumnDefinition { Width = GridLength.Auto });

            var profilesTitle = new XamlTextBlock
            {
                Style = titleStyle,
                Text = "Profiles",
            };
            ResponsivePageHeader.SetRole(
                profilesTitle,
                ResponsivePageHeaderElementRole.Title);
            profilesHeader.Children.Add(profilesTitle);

            var importProfiles = new XamlButton
            {
                Content = "Import profiles",
                Style = secondaryStyle,
            };
            var exportProfiles = new XamlButton
            {
                Content = "Export profiles",
                Style = secondaryStyle,
            };
            var profilesActions = new XamlStackPanel
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Orientation = XamlOrientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            };
            profilesActions.Children.Add(importProfiles);
            profilesActions.Children.Add(exportProfiles);
            XamlGrid.SetColumn(profilesActions, 1);
            ResponsivePageHeader.SetRole(
                profilesActions,
                ResponsivePageHeaderElementRole.ProfileActions);
            profilesHeader.Children.Add(profilesActions);

            host.Children.Clear();
            host.Children.Add(profilesHeader);
            await DrainResponsiveHeaderLayoutAsync(profilesHeader);
            RequireResponsivePageHeader(
                IsTitleRow(profilesActions, hasCustomizeAction: false),
                "The Profiles actions did not share the title row when they fit.");

            profilesHeader.Width = 320;
            await DrainResponsiveHeaderLayoutAsync(profilesHeader);
            RequireResponsivePageHeader(
                IsHorizontalLowerRow(profilesActions),
                "The Profiles actions did not move together when the title row was constrained.");

            importProfiles.Content =
                "Import an extraordinarily long localized collection of profiles from JSON";
            exportProfiles.Content =
                "Export an extraordinarily long localized collection of profiles to JSON";
            profilesHeader.Width = 360;
            await DrainResponsiveHeaderLayoutAsync(profilesHeader);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(profilesActions, profilesHeader.ActualWidth),
                "Long localized Profiles actions did not stack within the header width.");
            RequireResponsivePageHeader(
                importProfiles.ActualHeight > 32
                    && exportProfiles.ActualHeight > 32
                    && profilesActions.ActualWidth <= profilesHeader.ActualWidth + 0.5,
                "Long localized Profiles actions did not wrap without clipping.");
            await DrainResponsiveHeaderLayoutAsync(profilesHeader);
            RequireResponsivePageHeader(
                IsVerticalLowerRow(profilesActions, profilesHeader.ActualWidth),
                "The localized Profiles layout changed after a repeated layout pass.");

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

    private static bool IsTitleRow(
        XamlStackPanel actions,
        bool hasCustomizeAction = true) =>
        XamlGrid.GetRow(actions) == 0
        && XamlGrid.GetColumn(actions) == 1
        && XamlGrid.GetColumnSpan(actions) == 1
        && actions.Orientation == XamlOrientation.Horizontal
        && actions.HorizontalAlignment == Microsoft.UI.Xaml.HorizontalAlignment.Right
        && actions.Margin.Top == 0
        && actions.Margin.Right == (hasCustomizeAction
            ? PageHeaderLayoutPolicy.InterActionGroupGapDip
            : 0)
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

            var layoutCompact = layoutSelector.FindName("CompactSelector")
                as Microsoft.UI.Xaml.Controls.ComboBox
                ?? throw new InvalidOperationException("Layout compact selector was not created.");
            var layoutExpanded = layoutSelector.FindName("ExpandedSelector")
                as Microsoft.UI.Xaml.Controls.SelectorBar
                ?? throw new InvalidOperationException("Layout expanded selector was not created.");
            string DescribeLayout() => FormattableString.Invariant(
                $"windowPixels={layoutWindow.AppWindow.Size.Width}x{layoutWindow.AppWindow.Size.Height}, {layoutSelector.CaptureLayoutSnapshot()}, hostActual={layoutHost.ActualWidth:F1}, supporting={supportingContent.ActualWidth:F1}, authoritative={(layoutSelector.SelectedItem as ResponsiveChoiceSelectorSmokeOption)?.Key ?? "<null>"}, compactSelection={(layoutCompact.SelectedItem as ResponsiveChoiceSelectorSmokeOption)?.Key ?? "<null>"}, expandedSelection={(layoutExpanded.SelectedItem?.Tag as ResponsiveChoiceSelectorSmokeOption)?.Key ?? "<null>"}");
            double rasterizationScale = layoutSelector.XamlRoot?.RasterizationScale ?? 1;
            ResponsiveChoiceSelectorLayoutSnapshot wideLayout =
                await WaitForResponsiveSelectorLayoutAsync(
                    layoutHost,
                    layoutSelector,
                    () => layoutWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                        (int)Math.Ceiling(1400 * rasterizationScale),
                        (int)Math.Ceiling(600 * rasterizationScale))),
                    layout => layout.ExpandedVisible && layout.ExpandedWidth > 0,
                    "A wide settings row did not use the expanded presentation",
                    DescribeLayout);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Collapsed
                    && layoutExpanded.Visibility == Visibility.Visible,
                $"A wide settings row did not use the expanded presentation: {DescribeLayout()}.");
            double expandedWidth = wideLayout.ExpandedWidth;
            RequireResponsiveSelector(
                double.IsFinite(expandedWidth) && expandedWidth > 0,
                $"The expanded presentation did not report a usable width: {DescribeLayout()}.");

            const double minimumSupportingWidth = 240;
            double constrainedHostWidth = wideLayout.MinimumHostWidth - 1;
            ResponsiveChoiceSelectorLayoutSnapshot constrainedLayout =
                await WaitForResponsiveSelectorLayoutAsync(
                    layoutHost,
                    layoutSelector,
                    () => layoutHost.Width = constrainedHostWidth,
                    layout => layout.CompactVisible
                        && supportingContent.ActualWidth >= minimumSupportingWidth,
                    "A constrained settings row did not use the compact presentation",
                    DescribeLayout);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Visible
                    && layoutExpanded.Visibility == Visibility.Collapsed,
                $"A constrained settings row did not use the compact presentation: "
                    + $"{DescribeLayout()}.");
            RequireResponsiveSelector(
                supportingContent.ActualWidth >= minimumSupportingWidth,
                $"The compact presentation did not preserve supporting content width: "
                    + $"{DescribeLayout()}.");

            File.WriteAllText(smokeReportPath, "restore expanded responsive host");
            double restoredHostWidth = constrainedLayout.MinimumHostWidth + 2;
            await WaitForResponsiveSelectorLayoutAsync(
                layoutHost,
                layoutSelector,
                () => layoutHost.Width = restoredHostWidth,
                layout => layout.ExpandedVisible
                    && layout.AvailableWidth >= layout.MinimumHostWidth
                    && supportingContent.ActualWidth >= minimumSupportingWidth,
                "A widened settings row did not safely restore the expanded presentation",
                DescribeLayout);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Collapsed
                    && layoutExpanded.Visibility == Visibility.Visible
                    && supportingContent.ActualWidth >= minimumSupportingWidth,
                $"A widened settings row did not safely restore the expanded presentation: "
                    + $"{DescribeLayout()}.");

            await WaitForResponsiveSelectorLayoutAsync(
                layoutHost,
                layoutSelector,
                () => layoutHost.Width = constrainedHostWidth,
                layout => layout.CompactVisible
                    && ReferenceEquals(layoutCompact.SelectedItem, powerModeOptions[0]),
                "A second constrained layout did not preserve compact selection",
                DescribeLayout);
            RequireResponsiveSelector(
                layoutCompact.Visibility == Visibility.Visible
                    && layoutExpanded.Visibility == Visibility.Collapsed
                    && ReferenceEquals(layoutCompact.SelectedItem, powerModeOptions[0]),
                $"A second constrained layout did not preserve compact selection: "
                    + $"{DescribeLayout()}.");

            File.WriteAllText(smokeReportPath, $"completed: {DescribeLayout()}");
            layoutWindow.Close();
            smokeWindow.Close();
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

    private static async Task<ResponsiveChoiceSelectorLayoutSnapshot>
        WaitForResponsiveSelectorLayoutAsync(
            Microsoft.UI.Xaml.FrameworkElement layoutRoot,
            ResponsiveChoiceSelector selector,
            Action changeLayout,
            Func<ResponsiveChoiceSelectorLayoutSnapshot, bool> isComplete,
            string expectation,
            Func<string> describeLayout)
    {
        var completed = new TaskCompletionSource<ResponsiveChoiceSelectorLayoutSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ResponsiveChoiceSelectorLayoutSnapshot latest = selector.CaptureLayoutSnapshot();

        void ObserveLayout()
        {
            latest = selector.CaptureLayoutSnapshot();
            if (isComplete(latest))
            {
                completed.TrySetResult(latest);
            }
        }

        void OnLayoutUpdated(object? sender, object args) => ObserveLayout();

        layoutRoot.LayoutUpdated += OnLayoutUpdated;
        try
        {
            changeLayout();
            layoutRoot.UpdateLayout();
            ObserveLayout();
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"{expectation} after observable layout completion: "
                    + $"{describeLayout()}, latest={latest}.",
                exception);
        }
        finally
        {
            layoutRoot.LayoutUpdated -= OnLayoutUpdated;
        }
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
        string ExpectedOpenMenuItem,
        string ExpectedBatteryProfile,
        string ExpectedWindowsPowerModeDescription,
        string ExpectedDefaultAction,
        string ExpectedKeyboardShortcutAction,
        string ExpectedActionAccessibleName);

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
}
#else
public partial class App
{
    // The shipped Tray has no developer harness; every launch is a product launch.
    private bool TryRunDeveloperHarness(string[] commandLineArguments) => false;
}
#endif
