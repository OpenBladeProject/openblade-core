#if OPENBLADE_DEVELOPER_HARNESS
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using OpenBlade.Session;
using WinRT.Interop;

namespace OpenBlade.Tray;

public partial class App
{
    // Exercise the production language transaction with isolated preferences,
    // rejecting IPC and no tray icon, OSD, native input or update integrations.
    private async Task RunResourceLocalizationAuditAsync(string reportPath)
    {
        string path = Path.GetFullPath(reportPath);
        Exception? failure = null;
        object? report = null;
        var retired = new List<WeakReference>();
        var checkpoints = new List<ResourceRetirementCheckpoint>();
        IServiceClient client = DispatchProxy.Create<IServiceClient, GpuStartupServiceClientProxy>();
        var recorder = (GpuStartupServiceClientProxy)client;
        var store = new UiPreferenceStore(path + ".preferences.json");
        var context = new LiveLocalizationSmokeContext(path + ".phases.txt", store);
        using var process = Process.GetCurrentProcess();
        long started = Stopwatch.GetTimestamp();
        int cycles = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int seconds = int.Parse(Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_PHASE_SECONDS") ?? "2",
                System.Globalization.CultureInfo.InvariantCulture);
            if (seconds is < 0 or > 600
                || Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_OFFSCREEN") != "1"
                || Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_INPUT_DESKTOP") == "1"
                || Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_OBSERVE_RETIREMENT") == "1")
            {
                throw new InvalidOperationException("Localized-content validation requires the private offscreen desktop and its own bounded retirement checkpoints.");
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            startupRegistration = new WindowsStartupRegistration();
            sessionAgentEventEndpoint = SessionAgentEventEndpoint.Create(
                new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), process.SessionId);
            ApplyRuntimeLocalizationPreference(UiLanguagePreference.English);
            CreateInitialResourceLocalizationSurfaces(client, store);
            trayIconHost = new ResourceTrayIconHost();
            mainWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
            trayDashboardWindow!.AppWindow.Resize(new Windows.Graphics.SizeInt32(430, 760));
            Window mainHost = mainWindow.HostWindow;
            Window dashboardHost = trayDashboardWindow.HostWindow;
            nint mainHandle = WindowNative.GetWindowHandle(mainHost);
            nint dashboardHandle = WindowNative.GetWindowHandle(dashboardHost);
            await PresentResourceLocalizationAsync();
            File.WriteAllText(path + ".ready", "loaded-layout");

            if (seconds != 0)
            {
                // Warm all three language resources before comparing counts.
                for (int index = 0; index < 3; index++)
                {
                    await SwapResourceLocalizationAsync(index, client, store, context, retired);
                }
                await RejectResourceLocalizationAttachmentAsync(client, store, context, retired);
                await DrainResourceLocalizedContentAsync();
                checkpoints.Add(CaptureResourceRetirementCheckpoint("after-warmup", 0, started, process));
                File.WriteAllText(path + ".phase", "localized-content");
                long activityStarted = Stopwatch.GetTimestamp();
                do
                {
                    await SwapResourceLocalizationAsync(cycles, client, store, context, retired);
                    cycles++;
                    if (!ReferenceEquals(mainWindow!.HostWindow, mainHost)
                        || !ReferenceEquals(trayDashboardWindow!.HostWindow, dashboardHost)
                        || WindowNative.GetWindowHandle(mainHost) != mainHandle
                        || WindowNative.GetWindowHandle(dashboardHost) != dashboardHandle)
                    {
                        throw new InvalidOperationException("A language change replaced a native window shell.");
                    }
                    if (cycles % 12 == 0 && Stopwatch.GetElapsedTime(activityStarted) < TimeSpan.FromSeconds(seconds))
                    {
                        await DrainResourceLocalizedContentAsync();
                        checkpoints.Add(CaptureResourceRetirementCheckpoint($"language-batch-{cycles}", cycles, started, process));
                    }
                }
                while (cycles < 12 || Stopwatch.GetElapsedTime(activityStarted) < TimeSpan.FromSeconds(seconds));
                await DrainResourceLocalizedContentAsync();
                checkpoints.Add(CaptureResourceRetirementCheckpoint("after-language-changes", cycles, started, process));
                await Task.Delay(TimeSpan.FromSeconds(3));
                await DrainResourceLocalizedContentAsync();
                checkpoints.Add(CaptureResourceRetirementCheckpoint("after-idle", cycles, started, process));
                if (retired.Any(reference => reference.IsAlive))
                {
                    File.WriteAllText(path + ".retention.json", JsonSerializer.Serialize(new
                    {
                        checkpoints,
                        nativeHandleTypeNames = CaptureResourceHandleTypeNames(),
                        retained = retired.Select((reference, index) => new { index, type = reference.Target?.GetType().FullName })
                            .Where(value => value.type is not null).ToArray(),
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    throw new InvalidOperationException($"{retired.Count(reference => reference.IsAlive)} retired localized controls or roots remain alive.");
                }
            }
            mainWindow!.VerifyResourceIsolation(recorder);
            report = new
            {
                schemaVersion = 1,
                productVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                processId = Environment.ProcessId,
                disconnected = true,
                serviceCalls = recorder.Calls,
                windowKind = "localized-content",
                offscreenWindows = true,
                requireInputDesktop = false,
                observeRetirement = false,
                windowPresentation = SnapshotResourceWindowPresentation(),
                languageChanges = cycles,
                stableNativeShells = true,
                attachmentRollbackPassed = seconds != 0,
                shellMessageHostsPreserved = true,
                retiredReferences = retired.Count,
                retiredReferencesAlive = retired.Count(reference => reference.IsAlive),
                checkpoints,
                nativeHandleTypeNames = CaptureResourceHandleTypeNames(),
                notes = "Production language transaction with disconnected factories. Three-language warmup and failed-attachment rollback precede measurement. Two native shells stay live. GC and dispatcher acknowledgements are diagnostic only; private bytes include managed heap, JIT, caches and native allocations. Offscreen layout is not visible rendering or installed-product behavior.",
            };
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try { brightnessOsdHost?.Dispose(); }
            catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
            try { performanceNotificationHost?.Dispose(); }
            catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
            try { trayDashboardWindow?.Dispose(); }
            catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
            try { if (mainWindow is not null) { DetachWindowEvents(mainWindow); await mainWindow.DisposeAsync(); } }
            catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
        }
        try
        {
            File.WriteAllText(failure is null ? path : path + ".error.txt", failure?.ToString()
                ?? JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch { Environment.Exit(1); return; }
        // Closing the last native shell may end the dispatcher; do not await it.
        mainWindow?.Close();
        Environment.Exit(failure is null ? 0 : 1);
    }

    private void CreateInitialResourceLocalizationSurfaces(IServiceClient client, UiPreferenceStore store)
    {
        MainWindow view = MainWindow.CreateLocalizationSnapshot(client, store);
        ApplyGpuStartupStatus(view.ViewModel, CreateGpuStartupStatus(delayedRanges: false));
        view.ViewModel.LiveReadingsEnabled = false;
        WindowSurfaceSet surfaces = CreateWindowSurfaces(view, sessionAgentEventEndpoint!);
        AttachWindowSurfaces(view, surfaces, Task.CompletedTask);
        VerifyResourceShellMessageHosts();
    }

    private async Task SwapResourceLocalizationAsync(int index, IServiceClient client, UiPreferenceStore store,
        LiveLocalizationSmokeContext context, List<WeakReference> retired)
    {
        UiLanguagePreference preference = (index % 3) switch
        {
            0 => UiLanguagePreference.Russian,
            1 => UiLanguagePreference.SimplifiedChinese,
            _ => UiLanguagePreference.English,
        };
        string heading = preference switch
        {
            UiLanguagePreference.Russian => "Язык приложения",
            UiLanguagePreference.SimplifiedChinese => "应用语言",
            _ => "App language",
        };
        RecordResourceLocalizedContent(retired);
        WeakReference previous = new(mainWindow);
        var mainPosition = mainWindow!.AppWindow.Position;
        var mainSize = mainWindow.AppWindow.Size;
        var dashboardPosition = trayDashboardWindow!.AppWindow.Position;
        var dashboardSize = trayDashboardWindow.AppWindow.Size;
        var previousBrightness = brightnessOsdHost;
        var previousNotification = performanceNotificationHost;
        await RefreshLanguageAsync(preference, _ => new ResourceTrayIconHost(), context,
            host => CreateResourceLocalizationView(client, store, host, preference),
            replacementSurfaceFactory: null, replacementInitializationFactory: _ => Task.CompletedTask);
        if (ReferenceEquals(previous.Target, mainWindow))
        {
            throw new InvalidOperationException("The language transaction rolled back unexpectedly.");
        }
        AssertLiveLocalizationValue(heading, mainWindow!.LocalizedAppLanguageHeading, "compiled language heading");
        AssertLocalizedXamlApplied(mainWindow);
        VerifyResourceShellMessageHosts(previousBrightness, previousNotification);
        AssertLiveLocalizationValue(LocalizedResources.Get("TrayDashboardWindow_WindowTitle", "OpenBlade quick controls"),
            trayDashboardWindow!.HostWindow.Title, "dashboard window title");
        if (!mainWindow.AppWindow.Position.Equals(mainPosition) || !mainWindow.AppWindow.Size.Equals(mainSize)
            || !trayDashboardWindow.AppWindow.Position.Equals(dashboardPosition) || !trayDashboardWindow.AppWindow.Size.Equals(dashboardSize))
        {
            throw new InvalidOperationException("A language change altered native shell geometry.");
        }
        ApplyGpuStartupStatus(mainWindow.ViewModel, CreateGpuStartupStatus(delayedRanges: false));
        mainWindow.ViewModel.LiveReadingsEnabled = false;
        await PresentResourceLocalizationAsync();
    }

    private static MainWindow CreateResourceLocalizationView(IServiceClient client, UiPreferenceStore store,
        Window host, UiLanguagePreference? preference = null)
    {
        MainWindow view = MainWindow.CreateLocalizationSnapshot(client, store, host, preference);
        view.ViewModel.LiveReadingsEnabled = false;
        return view;
    }

    private async Task RejectResourceLocalizationAttachmentAsync(IServiceClient client, UiPreferenceStore store,
        LiveLocalizationSmokeContext context, List<WeakReference> retired)
    {
        MainWindow previous = mainWindow!;
        TrayDashboardWindow previousDashboard = trayDashboardWindow!;
        bool stagedBothCandidates = false;
        bool attachmentReached = false;
        var previousBrightness = brightnessOsdHost;
        var previousNotification = performanceNotificationHost;
        await RefreshLanguageAsync(UiLanguagePreference.Russian, _ =>
            {
                stagedBothCandidates = true;
                return new ResourceTrayIconHost();
            }, context,
            host =>
            {
                MainWindow candidate = CreateResourceLocalizationView(client, store, host);
                // The developer-only partial hook runs after native Content is
                // assigned, so rollback must undo a partially attached view.
                candidate.ResourceHostAttachmentObserver = () =>
                {
                    attachmentReached = true;
                    throw new InvalidOperationException("Expected resource-fixture attachment failure.");
                };
                retired.Add(new WeakReference(candidate));
                return candidate;
            }, replacementSurfaceFactory: null, replacementInitializationFactory: _ => Task.CompletedTask);
        if (!stagedBothCandidates || !attachmentReached
            || !ReferenceEquals(mainWindow, previous) || !ReferenceEquals(trayDashboardWindow, previousDashboard)
            || !ReferenceEquals(previous.ContentHost.ActiveView, previous)
            || !ReferenceEquals(previousDashboard.ContentHost.ActiveView, previousDashboard))
        {
            throw new InvalidOperationException("Failed localization attachment did not restore both previous views.");
        }
        VerifyResourceShellMessageHosts(previousBrightness, previousNotification);
        await PresentResourceLocalizationAsync();
    }

    private void VerifyResourceShellMessageHosts(WindowsTrayBrightnessOsdHost? expectedBrightness = null,
        WindowsTrayPerformanceNotificationHost? expectedNotification = null)
    {
        nint handle = WindowNative.GetWindowHandle(mainWindow!.HostWindow);
        foreach (var pair in new (Type Type, object? Actual, object? Expected)[]
        {
            (typeof(WindowsTrayBrightnessOsdHost), brightnessOsdHost, expectedBrightness),
            (typeof(WindowsTrayPerformanceNotificationHost), performanceNotificationHost, expectedNotification),
        })
        {
            var hosts = (System.Collections.IDictionary?)pair.Type.GetField("Hosts", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (pair.Actual is null || pair.Expected is not null && !ReferenceEquals(pair.Actual, pair.Expected)
                || hosts is null || !ReferenceEquals(hosts[handle], pair.Actual))
            {
                throw new InvalidOperationException($"The stable shell lost its {pair.Type.Name} registration.");
            }
        }
    }

    private void RecordResourceLocalizedContent(List<WeakReference> retired)
    {
        retired.Add(new WeakReference(mainWindow));
        retired.Add(new WeakReference(mainWindow!.Content));
        retired.Add(new WeakReference(trayDashboardWindow));
        retired.Add(new WeakReference(trayDashboardWindow!.Content));
    }

    private async Task PresentResourceLocalizationAsync()
    {
        // The offscreen presenter deliberately bypasses the product's visible
        // state and reveal animation. Reset that test-only presentation first.
        mainWindow!.AppWindow.Hide();
        trayDashboardWindow!.AppWindow.Hide();
        ShowOffscreenResourceWindow(mainWindow!, requireInputDesktop: false);
        ShowOffscreenResourceWindow(trayDashboardWindow!.HostWindow, requireInputDesktop: false);
        await WaitForResourceLayoutAsync((FrameworkElement)mainWindow!.Content);
        var dashboardRoot = (FrameworkElement)trayDashboardWindow.Content;
        try { await WaitForResourceLayoutAsync(dashboardRoot); }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException($"Dashboard layout: view loaded={trayDashboardWindow.IsLoaded} size={trayDashboardWindow.ActualWidth}x{trayDashboardWindow.ActualHeight}; root loaded={dashboardRoot.IsLoaded} size={dashboardRoot.ActualWidth}x{dashboardRoot.ActualHeight} visibility={dashboardRoot.Visibility}; native visible={trayDashboardWindow.AppWindow.IsVisible} size={trayDashboardWindow.AppWindow.Size.Width}x{trayDashboardWindow.AppWindow.Size.Height}.", exception);
        }
        VerifyOffscreenResourceWindow(mainWindow, requireInputDesktop: false);
        VerifyOffscreenResourceWindow(trayDashboardWindow.HostWindow, requireInputDesktop: false);
    }

    private static async Task DrainResourceLocalizedContentAsync()
    {
        await WaitForResourceDispatcherTurnAsync();
        CollectResourceRetainedBytes();
        await Task.Delay(250);
        await WaitForResourceDispatcherTurnAsync();
        CollectResourceRetainedBytes();
    }

    private sealed class ResourceTrayIconHost : ITrayIconHost
    {
        public void Dispose() { }
        public void ShowNotification(TrayNotification notification) =>
            throw new InvalidOperationException("The disconnected localization workload attempted a notification.");
    }
}

public sealed partial class MainWindow
{
    internal Action? ResourceHostAttachmentObserver { get; set; }

    partial void OnHostContentAttached() => ResourceHostAttachmentObserver?.Invoke();
}
#endif
