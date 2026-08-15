using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Session;
using WinRT.Interop;

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
    private const string ResponsiveChoiceSelectorSmokeArgument =
        "--responsive-choice-selector-smoke";
    private const string PerformanceNotificationReplacementTag = "performance";
    private static readonly TimeSpan RazerServiceStopCancellationTimeout =
        TimeSpan.FromSeconds(65);
    private static readonly TimeSpan RazerServiceStopHardExitTimeout =
        TimeSpan.FromSeconds(70);

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
    private Task windowInitializationTask = Task.CompletedTask;
    private DispatcherQueue uiDispatcherQueue = null!;
    private WindowsProcessorEfficiencyPolicy processorPolicy = null!;
    private int fatalCrashReported;

    public bool ExitRequested => exitLifecycle?.ExitRequested == true;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        string[] commandLineArguments = Environment.GetCommandLineArgs();
        if (commandLineArguments is
            [_, ResponsiveChoiceSelectorSmokeArgument, string selectorSmokeReportPath])
        {
            _ = RunResponsiveChoiceSelectorSmokeAsync(selectorSmokeReportPath);
            return;
        }
        if (commandLineArguments is [_, XamlStartupSmokeArgument, string smokeReportPath])
        {
            try
            {
                RunXamlStartupSmoke(smokeReportPath);
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
        var startupRegistration = new WindowsStartupRegistration();
        if (!startupRegistration.ShouldStartForLaunchArguments(
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

        mainWindow = CreateMainWindow(startupRegistration);
        trayDashboardWindow = new TrayDashboardWindow(
            mainWindow.ViewModel,
            WindowNative.GetWindowHandle(mainWindow),
            visible => mainWindow.SetTrayDashboardVisible(visible));
        trayDashboardWindow.ApplyPreferences(mainWindow.TrayDashboardPreference);
        mainWindow.TrayDashboardPreferenceChanged += OnTrayDashboardPreferenceChanged;
        mainWindow.TrayDashboardEditSessionChanged += OnTrayDashboardEditSessionChanged;
        SessionAgentEventEndpoint sessionEventEndpoint =
            SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(userSid),
                sessionId);
        brightnessOsdHost = WindowsTrayBrightnessOsdHost.CreateBestEffort(
            mainWindow,
            sessionEventEndpoint,
            exception => Trace.TraceWarning(
                "OpenBlade brightness OSD host failed: {0}",
                exception.Message));
        performanceNotificationHost =
            WindowsTrayPerformanceNotificationHost.CreateBestEffort(
                mainWindow,
                sessionEventEndpoint,
                signal => Enqueue(() => ShowPerformanceNotification(signal)),
                exception => Trace.TraceWarning(
                    "OpenBlade performance notification host failed: {0}",
                    exception.Message));
        windowInitializationTask = mainWindow.InitializeAsync();

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

    private void RunXamlStartupSmoke(string smokeReportPath)
    {
        File.WriteAllText(smokeReportPath, "enable visual styles");
        System.Windows.Forms.Application.EnableVisualStyles();
        File.WriteAllText(smokeReportPath, "construct main window");
        var smokeMainWindow = new MainWindow();
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
                MaxExpandedItems = 3,
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
                ReferenceEquals(compact.SelectedItem, replacementOptions[1]),
                "The compact selector did not reuse the equivalent replacement option.");
            RequireResponsiveSelector(
                ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[1]),
                "The expanded selector did not reuse the equivalent replacement option.");
            RequireResponsiveSelector(
                publishedSelections == 0,
                "An options refresh published a user selection.");

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

            selector.MaxExpandedItems = 0;
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Visible
                    && expanded.Visibility == Visibility.Collapsed
                    && ReferenceEquals(compact.SelectedItem, replacementOptions[2]),
                "The compact presentation did not retain selection.");
            selector.MaxExpandedItems = 3;
            await DrainDispatcherAsync(selector.DispatcherQueue);
            RequireResponsiveSelector(
                compact.Visibility == Visibility.Collapsed
                    && expanded.Visibility == Visibility.Visible
                    && ReferenceEquals(expanded.SelectedItem?.Tag, replacementOptions[2]),
                "The expanded presentation did not retain selection.");

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
                MaxExpandedItems = 4,
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
            layoutHost.Width = expandedWidth
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

    private MainWindow CreateMainWindow(IStartupRegistration startupRegistration)
    {
        return new MainWindow(
            processorPolicy,
            startupRegistration,
            TryShowTrayNotification);
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
        trayIconHost = new WinFormsTrayIconHost(
            point => Enqueue(() => _ = ToggleTrayDashboardWhenReadyAsync(point)),
            () => Enqueue(OpenMainWindowFromTrayIcon),
            () => Enqueue(() => _ = RequestTrayExitAsync()),
            mainWindow.ViewModel,
            trayIconThemeSource,
            () => Enqueue(mainWindow.RequestTrayTelemetryRefresh),
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
                "Performance mode could not be changed",
                "OpenBlade could not apply the selected mode.",
                TrayNotificationSeverity.Error,
                ReplacementTag: PerformanceNotificationReplacementTag,
                Destination: TrayNotificationDestination.Troubleshooting);
        }
        else
        {
            string body = signal.Preset is { } preset
                ? $"Current mode: {PerformancePresetPresentation.Label(preset)}"
                : "The performance mode changed.";
            string? logoAssetRelativePath = signal.Preset is { } selectedPreset
                ? PerformanceNotificationIconAsset.Resolve(
                    mainWindow?.ViewModel.ActivePowerSource
                        ?? PowerSource.ExternalUnknown,
                    selectedPreset)
                : null;
            notification = new TrayNotification(
                "Performance mode changed",
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
                    Heading = "OpenBlade could not shut down",
                    Text = exception.Message,
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
        var keepRunning = new TaskDialogButton("Keep background features running");
        var shutDown = new TaskDialogButton("Shut down completely");
        var page = new TaskDialogPage
        {
            Caption = "OpenBlade",
            Heading = "Exit OpenBlade?",
            Text = "Choose whether keyboard lighting, hotkeys, profiles, and automation "
                + "should keep running in the background.",
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
