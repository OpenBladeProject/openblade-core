#if OPENBLADE_DEVELOPER_HARNESS
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public partial class App
{
    // An explicit developer-only, disconnected workload. No normal product
    // initialization, installed IPC, input observation or hardware APIs run here.
    private static async Task RunResourcePerformanceAuditAsync(string reportPath)
    {
        MainWindow? window = null;
        Exception? failure = null;
        object? report = null;
        string? path = null;
        int renderingCallbacks = 0;
        EventHandler<object> renderingObserver = (_, _) => renderingCallbacks++;
        bool observingRendering = false;
        try
        {
            path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? secondsText = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_PHASE_SECONDS");
            int seconds = secondsText is null ? 2 : int.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture);
            string windowKind = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_WINDOW_KIND") ?? "dashboard";
            bool observeRendering = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_OBSERVE_RENDERING") == "1";
            bool offscreenWindows = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_OFFSCREEN") == "1";
            bool requireInputDesktop = Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_INPUT_DESKTOP") == "1";
            if (requireInputDesktop && !offscreenWindows)
            {
                throw new InvalidOperationException("Input-desktop validation requires offscreen nonactivating presentation.");
            }
            int windowDwellMilliseconds = int.Parse(Environment.GetEnvironmentVariable("OPENBLADE_RESOURCE_WINDOW_DWELL_MS") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            if (windowDwellMilliseconds is < 0 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(windowDwellMilliseconds));
            }
            if (windowKind is not ("dashboard" or "dashboard-empty" or "empty" or "empty-toolwindow" or "empty-toolwindow-titlebar"
                or "empty-toolwindow-restore" or "empty-toolwindow-reset"))
            {
                throw new ArgumentException("Unknown window control kind.");
            }
            if (seconds is < 0 or > 600)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), "Resource phases must be between zero and 600 seconds.");
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            IServiceClient client = DispatchProxy.Create<IServiceClient, GpuStartupServiceClientProxy>();
            var recorder = (GpuStartupServiceClientProxy)client;
            long constructorAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
            long constructorStart = Stopwatch.GetTimestamp();
            window = MainWindow.CreateLocalizationSnapshot(client, new UiPreferenceStore(path + ".preferences.json"));
            double constructionMilliseconds = Stopwatch.GetElapsedTime(constructorStart).TotalMilliseconds;
            long constructionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - constructorAllocatedStart;
            ApplyGpuStartupStatus(window.ViewModel, CreateGpuStartupStatus(delayedRanges: false));
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
            FrameworkElement root = (FrameworkElement)window.Content;
            if (offscreenWindows) { ShowOffscreenResourceWindow(window, requireInputDesktop); }
            else { ShowNoninteractiveSnapshotWindow(window); }
            await WaitForResourceLayoutAsync(root);
            double constructionToLoadedLayoutMilliseconds = Stopwatch.GetElapsedTime(constructorStart).TotalMilliseconds;
            if (offscreenWindows) { VerifyOffscreenResourceWindow(window, requireInputDesktop); }
            double? rasterizationScale = root.XamlRoot?.RasterizationScale;
            window.VerifyResourceIsolation(recorder);
            // The parent observes this marker using its own monotonic launch
            // clock. Readiness is loaded layout, not a delayed snapshot or paint fence.
            File.WriteAllText(path + ".ready", "loaded-layout");
            // Subscribing can itself request continuous frames. Keep this a
            // separate diagnostic, never part of the default CPU/idle workload.
            if (observeRendering)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += renderingObserver;
                observingRendering = true;
            }

            var phases = new List<ResourcePhase>();
            var dashboardReferences = new List<ResourceDashboardReference>(256);
            long? retainedAfterWarmup = null;
            long? retainedAfterActivity = null;
            long? retainedAfterDispose = null;
            int warmupOperations = 0;
            int dashboardCycles = 0;
            int activityOperations = 0;
            int activityChartRebuilds = 0;
            int activitySampleChartRebuilds = 0;
            int idleRenderingCallbacks = 0;
            int activityRenderingCallbacks = 0;
            int? boundedHistorySamples = null;
            ResourceDashboardReference? retiredMainWindow = null;
            ResourceCheckpoint? afterActivityCollection = null;
            Dictionary<string, int>? subscribersAfterWarmup = null;
            Dictionary<string, int>? subscribersAfterActivity = null;
            ResourceHandleSnapshot? nativeHandlesAfterWarmup = null;
            ResourceHandleSnapshot? nativeHandlesAfterActivity = null;
            ResourceGuiSnapshot? guiResourcesAfterWarmup = null;
            ResourceGuiSnapshot? guiResourcesAfterActivity = null;
            ResourceHandleTypeNames? nativeHandleTypeNames = null;
            using var process = Process.GetCurrentProcess();
            if (seconds != 0)
            {
                File.WriteAllText(path + ".phase", "warmup");
                retiredMainWindow = await ExerciseResourceMainWindowLifetimeAsync(path + ".retired.preferences.json", offscreenWindows, requireInputDesktop);
                await window.PreparePerformanceTelemetryAsync();
                using MainWindow.ResourceSampleFixture fixture = window.CreateResourceSampleFixture();
                for (int index = 0; index < 32; index++)
                {
                    window.RunResourcePresentationStep(fixture, index);
                    warmupOperations++;
                    await Task.Delay(50);
                }
                for (int index = 0; index < 4; index++)
                {
                    await CycleResourceDashboardAsync(window, dashboardReferences, windowKind, windowDwellMilliseconds, offscreenWindows, requireInputDesktop,
                        verifyTeardown: index == 0 && windowKind == "dashboard");
                    dashboardCycles++;
                }
                window.SelectLocalizationSnapshotPage("home");
                await Task.Delay(500);
                retainedAfterWarmup = CollectResourceRetainedBytes();
                subscribersAfterWarmup = window.SnapshotResourceSubscribers();
                nativeHandlesAfterWarmup = CaptureResourceHandleTypes();
                guiResourcesAfterWarmup = CaptureResourceGuiCounts();
                nativeHandleTypeNames = CaptureResourceHandleTypeNames();

                File.WriteAllText(path + ".phase", "idle");
                ResourceCheckpoint idleStart = CaptureResourceCheckpoint(process);
                int renderingBeforeIdle = renderingCallbacks;
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                phases.Add(FinishResourcePhase("disconnected-idle", idleStart, process, 0));
                idleRenderingCallbacks = renderingCallbacks - renderingBeforeIdle;

                File.WriteAllText(path + ".phase", "activity");
                int rebuildsBeforeActivity = fixture.ChartRebuilds;
                int sampleRebuildsBeforeActivity = fixture.SampleChartRebuilds;
                ResourceCheckpoint activityStart = CaptureResourceCheckpoint(process);
                int renderingBeforeActivity = renderingCallbacks;
                long activityStartTicks = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(activityStartTicks) < TimeSpan.FromSeconds(seconds))
                {
                    window.RunResourcePresentationStep(fixture, warmupOperations + activityOperations);
                    activityOperations++;
                    if (activityOperations == 1 || activityOperations % 10 == 0)
                    {
                        await CycleResourceDashboardAsync(window, dashboardReferences, windowKind, windowDwellMilliseconds, offscreenWindows, requireInputDesktop);
                        dashboardCycles++;
                    }
                    await Task.Delay(250);
                }
                phases.Add(FinishResourcePhase("paced-presentation-and-dashboard-lifetime", activityStart, process, activityOperations));
                activityRenderingCallbacks = renderingCallbacks - renderingBeforeActivity;
                activityChartRebuilds = fixture.ChartRebuilds - rebuildsBeforeActivity;
                activitySampleChartRebuilds = fixture.SampleChartRebuilds - sampleRebuildsBeforeActivity;
                TelemetryHistorySnapshot history = window.ViewModel.GetTelemetrySnapshot();
                boundedHistorySamples = history.RawSamples.Count;
                if (boundedHistorySamples > TelemetryHistory.DefaultCapacity
                    || history.RawSamples[^1].ServiceRevision != warmupOperations + activityOperations - 1 + 10_000
                    || activitySampleChartRebuilds != activityOperations)
                {
                    throw new InvalidOperationException("The bounded presentation workload did not retain and render its accepted samples.");
                }
                window.SelectLocalizationSnapshotPage("home");
                await Task.Delay(500);
                retainedAfterActivity = CollectResourceRetainedBytes();
                await Task.Delay(250);
                afterActivityCollection = CaptureResourceCheckpoint(process);
                subscribersAfterActivity = window.SnapshotResourceSubscribers();
                nativeHandlesAfterActivity = CaptureResourceHandleTypes();
                guiResourcesAfterActivity = CaptureResourceGuiCounts();
                window.VerifyResourceIsolation(recorder);
                if (offscreenWindows) { VerifyOffscreenResourceWindow(window, requireInputDesktop); }
            }
            File.WriteAllText(path + ".phase", "dispose");
            await window.DisposeAsync();
            if (seconds != 0)
            {
                retainedAfterDispose = CollectResourceRetainedBytes();
            }
            report = new
            {
                schemaVersion = 1,
                productVersion = OpenBladeProductVersion.Read(typeof(App).Assembly),
                framework = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processId = Environment.ProcessId,
                disconnected = true,
                serviceCalls = recorder.Calls,
                phaseSeconds = seconds,
                windowKind,
                windowDwellMilliseconds,
                observeRendering,
                offscreenWindows,
                requireInputDesktop,
                windowPresentation = offscreenWindows ? SnapshotResourceWindowPresentation() : null,
                constructionMilliseconds,
                constructionAllocatedBytes,
                constructionToLoadedLayoutMilliseconds,
                rasterizationScale,
                phases,
                warmupOperations,
                activityOperations,
                activityChartRebuilds,
                activitySampleChartRebuilds,
                idleRenderingCallbacks,
                activityRenderingCallbacks,
                boundedHistorySamples,
                dashboardCycles,
                dashboardWrappersAliveAfterCollection = seconds == 0 ? (int?)null : dashboardReferences.Count(reference => reference.Window.IsAlive),
                dashboardContentsAliveAfterCollection = seconds == 0 ? (int?)null : dashboardReferences.Count(reference => reference.Content.IsAlive),
                dashboardDetachedContentsAliveAfterCollection = seconds == 0 ? (int?)null : dashboardReferences.Count(reference => reference.DetachedContent?.IsAlive == true),
                dashboardNativeWindowsSurvivedClose = dashboardReferences.Count(reference => !reference.NativeWindowDestroyed),
                dashboardRevealHosts = typeof(TrayDashboardWindow).GetField("RevealHosts", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is System.Collections.IDictionary hosts ? hosts.Count : -1,
                retiredMainWindowAliveAfterCollection = retiredMainWindow?.Window.IsAlive,
                retiredMainContentAliveAfterCollection = retiredMainWindow?.Content.IsAlive,
                retiredMainNativeWindowSurvivedClose = retiredMainWindow is not null && !retiredMainWindow.NativeWindowDestroyed,
                bindingCleanupFailureChecksPassed = seconds != 0,
                nativeTitleBarCleanupChecksPassed = seconds != 0 && windowKind == "dashboard",
                privateBytesAfterActivityCollection = afterActivityCollection?.PrivateBytes,
                handlesAfterActivityCollection = afterActivityCollection?.Handles,
                subscribersAfterWarmup,
                subscribersAfterActivity,
                nativeHandlesAfterWarmup,
                nativeHandlesAfterActivity,
                guiResourcesAfterWarmup,
                guiResourcesAfterActivity,
                nativeHandleTypeNames,
                retainedAfterWarmup,
                retainedAfterActivity,
                retainedAfterDispose,
                notes = "Disconnected native snapshot mode with isolated preferences and rejecting service client. Loaded-layout readiness is not first paint or installed-app startup. Activity is synthetic, paced at a minimum 250 ms plus operation time; timers/IPC are not the production background observation pipeline. GC checkpoints are outside timed phases. Retained memory includes fixture/history, the still-rooted disposed main window and the growing weak-reference tracking list. Private bytes, managed retention and native handles are separate observations, not proof of absence of leaks or an energy measurement.",
            };
            if (recorder.Calls != 0)
            {
                throw new InvalidOperationException("Resource validation attempted an installed service operation.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (observingRendering)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= renderingObserver;
            }
            if (window is not null)
            {
                try { await window.DisposeAsync(); }
                catch (Exception exception) { failure ??= exception; }
            }
        }
        try
        {
            if (path is null)
            {
                Console.Error.WriteLine(failure);
                Environment.Exit(1);
                return;
            }
            if (failure is null)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            }
            else
            {
                File.WriteAllText(path + ".error.txt", failure.ToString());
            }
        }
        catch
        {
            Environment.Exit(1);
            return;
        }
        // No await after closing the last XAML window.
        window?.Close();
        Environment.Exit(failure is null ? 0 : 1);
    }

    private static async Task WaitForResourceLayoutAsync(FrameworkElement root)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Inspect()
        {
            if (root.IsLoaded && root.ActualWidth > 0 && root.ActualHeight > 0)
            {
                ready.TrySetResult();
            }
        }
        void Loaded(object sender, RoutedEventArgs args) => Inspect();
        void LayoutUpdated(object? sender, object args) => Inspect();
        root.Loaded += Loaded;
        root.LayoutUpdated += LayoutUpdated;
        try
        {
            root.UpdateLayout();
            Inspect();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            root.Loaded -= Loaded;
            root.LayoutUpdated -= LayoutUpdated;
        }
    }

    private static async Task CycleResourceDashboardAsync(MainWindow window, List<ResourceDashboardReference> references, string windowKind, int dwellMilliseconds,
        bool offscreenWindows, bool requireInputDesktop, bool verifyTeardown = false)
    {
        Window dashboard = windowKind.StartsWith("empty", StringComparison.Ordinal)
            ? new Window { Content = new Microsoft.UI.Xaml.Controls.Grid() }
            : new TrayDashboardWindow(window.ViewModel,
                WinRT.Interop.WindowNative.GetWindowHandle(window), _ => { });
        Microsoft.UI.Windowing.AppWindow nativeWindow = dashboard.AppWindow;
        bool destroyed = false;
        bool titleBarResetBeforeClose = false;
        Exception? titleBarReadFailure = null;
        void OnDestroying(Microsoft.UI.Windowing.AppWindow sender, object args)
        {
            destroyed = true;
            if (verifyTeardown)
            {
                try { titleBarResetBeforeClose = sender.TitleBar.ButtonForegroundColor is null; }
                catch (Exception exception) { titleBarReadFailure = exception; }
            }
        }
        nativeWindow.Destroying += OnDestroying;
        ResourceDashboardReference? reference = null;
        List<Exception>? failures = null;
        try
        {
            if (verifyTeardown)
            {
                nativeWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 17, 34, 51);
            }
            if (windowKind.StartsWith("empty-toolwindow", StringComparison.Ordinal))
            {
                // Match the production dashboard's presenter recipe without
                // its XAML content, binding connector, timers or native subclass.
                var presenter = Microsoft.UI.Windowing.OverlappedPresenter.CreateForToolWindow();
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = false;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: windowKind == "empty-toolwindow-titlebar");
                dashboard.AppWindow.SetPresenter(presenter);
            }
            if (dashboard is TrayDashboardWindow quickControls)
            {
                quickControls.ApplyPreferences(TrayDashboardPreferenceDefaults.Preference);
            }
            WeakReference? detachedContent = null;
            if (windowKind == "dashboard-empty")
            {
                // Keep the actual presenter, native subclass and timers while
                // isolating activation/layout of the dashboard's visual tree.
                detachedContent = new WeakReference(dashboard.Content);
                dashboard.Content = new Microsoft.UI.Xaml.Controls.Grid();
            }
            dashboard.AppWindow.Resize(new Windows.Graphics.SizeInt32(430, 760));
            if (offscreenWindows) { ShowOffscreenResourceWindow(dashboard, requireInputDesktop); }
            else { ShowNoninteractiveSnapshotWindow(dashboard); }
            await WaitForResourceLayoutAsync((FrameworkElement)dashboard.Content);
            if (offscreenWindows) { VerifyOffscreenResourceWindow(dashboard, requireInputDesktop); }
            if (dwellMilliseconds != 0) { await Task.Delay(dwellMilliseconds); }
            reference = new ResourceDashboardReference(new WeakReference(dashboard), new WeakReference(dashboard.Content), detachedContent);
            references.Add(reference);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            try
            {
                if (windowKind is "empty-toolwindow-restore" or "empty-toolwindow-reset")
                {
                    dashboard.AppWindow.Hide();
                    if (windowKind == "empty-toolwindow-restore")
                    {
                        ((Microsoft.UI.Windowing.OverlappedPresenter)dashboard.AppWindow.Presenter)
                            .SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
                    }
                    else
                    {
                        dashboard.AppWindow.TitleBar.ResetToDefault();
                    }
                }
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try
            {
                if (dashboard is TrayDashboardWindow quickControls) { quickControls.Dispose(); }
                else { dashboard.Close(); }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try { nativeWindow.Destroying -= OnDestroying; }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            if (verifyTeardown)
            {
                if (!titleBarResetBeforeClose)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        "Dashboard native caption customization was not reset before native destruction.", titleBarReadFailure));
                }
            }
            if (reference is not null) { reference.NativeWindowDestroyed = destroyed; }
            try { if (offscreenWindows) { VerifyOffscreenResourceWindow(window, requireInputDesktop); } }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 })
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is not null)
        {
            throw new AggregateException("Dashboard resource validation and cleanup failed.", failures);
        }
    }

    private static async Task<ResourceDashboardReference> ExerciseResourceMainWindowLifetimeAsync(string preferencePath,
        bool offscreenWindows, bool requireInputDesktop)
    {
        IServiceClient client = DispatchProxy.Create<IServiceClient, GpuStartupServiceClientProxy>();
        MainWindow retired = MainWindow.CreateLocalizationSnapshot(client, new UiPreferenceStore(preferencePath));
        Microsoft.UI.Windowing.AppWindow nativeWindow = retired.AppWindow;
        bool destroyed = false;
        void OnDestroying(Microsoft.UI.Windowing.AppWindow sender, object args) => destroyed = true;
        nativeWindow.Destroying += OnDestroying;
        ResourceDashboardReference? reference = null;
        try
        {
            retired.AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
            if (offscreenWindows) { ShowOffscreenResourceWindow(retired, requireInputDesktop); }
            else { ShowNoninteractiveSnapshotWindow(retired); }
            await WaitForResourceLayoutAsync((FrameworkElement)retired.Content);
            if (offscreenWindows) { VerifyOffscreenResourceWindow(retired, requireInputDesktop); }
            retired.VerifyResourceIsolation((GpuStartupServiceClientProxy)client);
            VerifyResourceBindingCleanupFailures(retired);
            await VerifyResourceMainWindowPendingDisposalAsync(retired);
            reference = new ResourceDashboardReference(new WeakReference(retired), new WeakReference(retired.Content));
            return reference;
        }
        finally
        {
            try { await retired.DisposeAsync(); }
            finally
            {
                try { retired.Close(); }
                finally
                {
                    nativeWindow.Destroying -= OnDestroying;
                    if (reference is not null) { reference.NativeWindowDestroyed = destroyed; }
                }
            }
        }
    }

    private static async Task VerifyResourceMainWindowPendingDisposalAsync(MainWindow window)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellationFailure = window.SeedResourceDisposalFailures(pending.Task);
        window.AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 17, 34, 51);
        Task disposing = window.DisposeAsync().AsTask();
        bool hiddenBeforeAwait;
        bool waitedForInitialization;
        try
        {
            hiddenBeforeAwait = !window.AppWindow.IsVisible && !window.IsWindowVisible;
            waitedForInitialization = !disposing.IsCompleted;
        }
        finally
        {
            pending.SetException(new InvalidOperationException("Expected resource-fixture initialization failure."));
        }
        await disposing;
        if (!hiddenBeforeAwait || !waitedForInitialization)
        {
            throw new InvalidOperationException("Permanent disposal did not hide the main window before awaiting pending initialization.");
        }
        if (window.ExtendsContentIntoTitleBar || window.AppWindow.TitleBar.ButtonForegroundColor is not null)
        {
            throw new InvalidOperationException("Failed initialization skipped native main-window title-bar cleanup.");
        }
    }

    private static long CollectResourceRetainedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    private static void VerifyResourceBindingCleanupFailures(Window window)
    {
        bool trackingStopped = false;
        WindowXamlBindingLifetime.Release(window, new ResourceInvalidBindings(), () => trackingStopped = true);
        if (!trackingStopped)
        {
            throw new InvalidOperationException("A binding delegate failure skipped tracking cleanup.");
        }
        // Neither a changed compiler contract nor failed listener cleanup may
        // prevent the caller's remaining permanent teardown.
        WindowXamlBindingLifetime.Release(window, new object(),
            () => throw new InvalidOperationException("Expected resource-fixture tracking failure."));
    }

    private sealed class ResourceInvalidBindings
    {
        public int Activated(object sender, WindowActivatedEventArgs args) => 0;
    }

    private static ResourceCheckpoint CaptureResourceCheckpoint(Process process)
    {
        process.Refresh();
        return new ResourceCheckpoint(Stopwatch.GetTimestamp(), process.TotalProcessorTime,
            process.PrivateMemorySize64, process.HandleCount,
            GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static ResourcePhase FinishResourcePhase(string name, ResourceCheckpoint start, Process process, int operations)
    {
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - start.TotalAllocatedBytes;
        int gen0 = GC.CollectionCount(0) - start.Gen0;
        int gen1 = GC.CollectionCount(1) - start.Gen1;
        int gen2 = GC.CollectionCount(2) - start.Gen2;
        process.Refresh();
        return new ResourcePhase(name, Stopwatch.GetElapsedTime(start.Timestamp).TotalSeconds,
            (process.TotalProcessorTime - start.Cpu).TotalMilliseconds, allocatedBytes,
            gen0, gen1, gen2, start.PrivateBytes, process.PrivateMemorySize64,
            start.Handles, process.HandleCount, operations);
    }

    private readonly record struct ResourceCheckpoint(long Timestamp, TimeSpan Cpu, long PrivateBytes, int Handles,
        long TotalAllocatedBytes, int Gen0, int Gen1, int Gen2);
    private sealed record ResourcePhase(string Name, double ElapsedSeconds, double CpuMilliseconds, long AllocatedBytes,
        int Gen0Collections, int Gen1Collections, int Gen2Collections, long StartPrivateBytes, long EndPrivateBytes,
        int StartHandles, int EndHandles, int Operations);
    private sealed record ResourceDashboardReference(WeakReference Window, WeakReference Content, WeakReference? DetachedContent = null)
    {
        internal bool NativeWindowDestroyed { get; set; }
    }
}

public sealed partial class MainWindow
{
    internal CancellationTokenRegistration SeedResourceDisposalFailures(Task pending)
    {
        lock (initializationSync) { initializationTask = pending; }
        Volatile.Write(ref windowVisible, true);
        return featureLifetime.Token.Register(static () =>
            throw new InvalidOperationException("Expected resource-fixture cancellation callback failure."));
    }

    internal Dictionary<string, int> SnapshotResourceSubscribers()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var handlers = (Delegate?)typeof(MainViewModel).GetField(nameof(MainViewModel.PropertyChanged), flags)?.GetValue(ViewModel)
            ?? throw new InvalidOperationException("The view-model event diagnostic was unavailable.");
        return handlers.GetInvocationList()
            .GroupBy(handler => $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    internal sealed class ResourceSampleFixture : IDisposable
    {
        private readonly MainWindow window;
        internal ResourceSampleFixture(MainWindow window, TelemetryHistory history, FieldInfo latestField)
        {
            this.window = window;
            History = history;
            LatestField = latestField;
            window.TelemetryPlot.VisibleRangeChanged += OnChartRebuilt;
        }
        internal TelemetryHistory History { get; }
        internal FieldInfo LatestField { get; }
        internal int ChartRebuilds { get; private set; }
        internal int SampleChartRebuilds { get; set; }
        private void OnChartRebuilt(object? sender, EventArgs args) => ChartRebuilds++;
        public void Dispose() => window.TelemetryPlot.VisibleRangeChanged -= OnChartRebuilt;
    }

    internal ResourceSampleFixture CreateResourceSampleFixture()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var history = (TelemetryHistory)(typeof(MainViewModel).GetField("telemetryHistory", flags)?.GetValue(ViewModel)
            ?? throw new InvalidOperationException("Telemetry history fixture hook was not found."));
        FieldInfo latest = typeof(MainViewModel).GetField("latestLiveTelemetrySample", flags)
            ?? throw new InvalidOperationException("Latest telemetry fixture hook was not found.");
        return new ResourceSampleFixture(this, history, latest);
    }

    internal void RunResourcePresentationStep(ResourceSampleFixture fixture, int index)
    {
        var sample = new TelemetrySample(DateTimeOffset.UtcNow, index + 10_000,
            TelemetrySource.LiveService, PowerSource.PluggedIn, "Resource fixture",
            -12_000 - index % 5_000, 2_000 + index % 800, 1_900 + index % 700,
            index % 97 == 0 ? null : 55 + index % 20, 50 + index % 15);
        fixture.History.AddSample(sample);
        fixture.LatestField.SetValue(ViewModel, sample);
        int before = fixture.ChartRebuilds;
        OnViewModelPropertyChanged(ViewModel, new PropertyChangedEventArgs(nameof(MainViewModel.LatestTelemetryFreshness)));
        OnViewModelPropertyChanged(ViewModel, new PropertyChangedEventArgs(nameof(MainViewModel.TelemetryHistorySnapshot)));
        int sampleRebuilds = fixture.ChartRebuilds - before;
        if (sampleRebuilds != 1)
        {
            throw new InvalidOperationException($"Each accepted sample must rebuild once; observed {sampleRebuilds}.");
        }
        fixture.SampleChartRebuilds += sampleRebuilds;
        if (index % 4 == 0)
        {
            string[] pages = ["home", "laptop", "profiles", "lighting", "display", "settings", "about", "telemetry"];
            SelectLocalizationSnapshotPage(pages[index / 4 % pages.Length]);
        }
    }

    internal void VerifyResourceIsolation(App.GpuStartupServiceClientProxy recorder)
    {
        if (!localizationSnapshotMode || initializationTask is not null || recorder.Calls != 0
            || gpuClockOffsetCommits.TryPeekPending(out _) || gpuClockOffsetCommitTimer.IsRunning)
        {
            throw new InvalidOperationException("Resource fixture must remain disconnected with no scheduled writes.");
        }
    }
}
#endif
