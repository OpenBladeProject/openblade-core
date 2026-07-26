using System.Runtime.ExceptionServices;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _ = CrashReportWriter.CreateDefault(
                    "OpenBlade.SessionAgent",
                    OpenBladeProductVersion.Read(typeof(Program).Assembly))
                .TryWrite(
                    "Fatal process failure",
                    exception,
                    processTerminating: true);
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        SessionAgentPipeEndpoint endpoint = SessionAgentPipeEndpoint.Create(userSid);
        SessionAgentEventEndpoint eventEndpoint =
            SessionAgentEventEndpoint.Create(userSid);
        if (!TryAcquireStartupOwnership(
                endpoint,
                userSid,
                () => PersistentTraceLog.RegisterDefault(
                    "OpenBlade.SessionAgent",
                    OpenBladeProductVersion.Read(typeof(Program).Assembly)),
                out SessionAgentPipeOwner? owner,
                out IDisposable? traceLogRegistration))
        {
            return 0;
        }

        await using SessionAgentPipeOwner acquiredOwner = owner!;
        using IDisposable acquiredTraceLogRegistration = traceLogRegistration!;
        using (var lifetime = new CancellationTokenSource())
        using (EventWaitHandle shutdownEvent = CreateShutdownEvent())
        using (var controller = new WindowsDisplaySettingsController())
        {
            RegisteredWaitHandle shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
                shutdownEvent,
                static (state, _) =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                lifetime,
                Timeout.Infinite,
                executeOnlyOnce: true);
            try
            {
                var powerContext = new DisplayPowerContextState();
                using var applier = new DisplayProfileApplier(controller, powerContext);
                var checkpointStore = new DisplayAutomationCheckpointStore(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OpenBlade",
                        "display-automation.json"));
                await using var coordinator = new DisplayAutomationCoordinator(
                    powerContext,
                    applier,
                    checkpointStore.Load(),
                    source =>
                    {
                        if (!checkpointStore.Save(source))
                        {
                            System.Diagnostics.Trace.TraceWarning(
                                "OpenBlade could not save the display automation checkpoint.");
                        }
                    });
                var dispatcher = new SessionDisplayRequestDispatcher(applier);
                await using var server = new SessionDisplayPipeServer(acquiredOwner, dispatcher);
                string preferencesPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenBlade",
                    "preferences.json");
                var specialKeyBindings = new SpecialKeyBindingSource(
                    preferencesPath,
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade shortcut preference refresh failed: {0}",
                        exception.Message));
                var specialKeyInput = new WindowsSpecialKeyInputSender();
                await using var specialKeyServiceClient = new ServiceClient();
                var specialKeyExecutor = new SpecialKeyActionExecutor(
                    specialKeyBindings,
                    specialKeyServiceClient,
                    specialKeyInput);
                using IDisplayBrightnessOsd displayBrightnessOsd =
                    WindowsTrayBrightnessOsdNotifier.CreateBestEffort(
                        eventEndpoint,
                        exception => System.Diagnostics.Trace.TraceWarning(
                            "OpenBlade brightness OSD notification failed: {0}",
                            exception.Message));
                await using var functionRowServiceClient = new ServiceClient();
                var functionRowActions = new FunctionRowActionExecutor(
                    functionRowServiceClient,
                    specialKeyInput,
                    new WmiDisplayBrightnessController(),
                    displayBrightnessOsd);
                await using var statusServiceClient = new ServiceClient();
                await using var statusFeed = new SharedServiceStatusFeed(
                    statusServiceClient);
                var automation = new ServiceDisplayAutomationLoop(
                    statusFeed,
                    coordinator);
                using IPerformanceShortcutNotificationSink shortcutNotifications =
                    WindowsTrayPerformanceShortcutNotificationSink.CreateBestEffort(
                        eventEndpoint,
                        exception => System.Diagnostics.Trace.TraceWarning(
                            "OpenBlade notification failed: {0}",
                            exception.Message));
                Action<Exception> inputErrorHandler = exception =>
                    System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade input observation failed: {0}",
                        exception.Message);
                await using var reactiveServiceClient = new ServiceClient();
                await using var reactive = new ServiceReactiveLightingLoop(
                    reactiveServiceClient,
                    WindowsReactiveKeyboardSourceFactory.Instance,
                    inputErrorHandler,
                    shortcutNotifications,
                    statusFeed,
                    specialKeyBindings.ShouldHandleInDriverMode,
                    specialKeyExecutor.ExecuteAsync,
                    specialKeyExecutor.ExecuteDefaultAsync,
                    functionRowActions.ExecuteAsync);
                await using var ambientServiceClient = new ServiceClient();
                await using var ambient = new ServiceAmbientLightingLoop(
                    ambientServiceClient,
                    WindowsAmbientScreenSamplerFactory.Instance,
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade Ambient Awareness failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed);
                await using var lightingIdleServiceClient = new ServiceClient();
                var lightingIdlePolicy = new ServiceLightingIdlePolicyLoop(
                    lightingIdleServiceClient,
                    new WindowsUserIdleTimeSource(),
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade lighting power policy failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed,
                    displayStateSource: new WindowsSessionDisplayStateSource());
                await using var playbackMuteServiceClient = new ServiceClient();
                var playbackMuteIndicator = new ServicePlaybackMuteIndicatorLoop(
                    playbackMuteServiceClient,
                    new WindowsPlaybackMuteStateSource(),
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade playback mute indication failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed);

                Task statusFeedTask = statusFeed.RunAsync(lifetime.Token);
                Task specialKeyBindingsTask = specialKeyBindings.RunAsync(lifetime.Token);
                Task serverTask = server.RunAsync(lifetime.Token);
                Task automationTask = automation.RunAsync(lifetime.Token);
                Task reactiveTask = reactive.RunAsync(lifetime.Token);
                Task ambientTask = ambient.RunAsync(lifetime.Token);
                Task lightingIdleTask = lightingIdlePolicy.RunAsync(lifetime.Token);
                Task playbackMuteTask = playbackMuteIndicator.RunAsync(lifetime.Token);
                Task completed = await Task.WhenAny(
                    serverTask,
                    statusFeedTask,
                    specialKeyBindingsTask,
                    automationTask,
                    reactiveTask,
                    ambientTask,
                    lightingIdleTask,
                    playbackMuteTask).ConfigureAwait(false);
                Exception? terminalFailure = completed.IsFaulted
                    ? completed.Exception?.GetBaseException()
                    : null;
                if (!lifetime.IsCancellationRequested)
                {
                    lifetime.Cancel();
                }

                try
                {
                    await Task.WhenAll(
                            serverTask,
                            statusFeedTask,
                            specialKeyBindingsTask,
                            automationTask,
                            reactiveTask,
                            ambientTask,
                            lightingIdleTask,
                            playbackMuteTask)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                if (terminalFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(terminalFailure).Throw();
                }
                return 0;
            }
            finally
            {
                using var callbacksCompleted = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset);
                if (shutdownRegistration.Unregister(callbacksCompleted))
                {
                    _ = callbacksCompleted.WaitOne(TimeSpan.FromSeconds(2));
                }
            }
        }
    }

    internal static bool TryAcquireStartupOwnership(
        SessionAgentPipeEndpoint endpoint,
        SecurityIdentifier userSid,
        Func<IDisposable> registerTraceLog,
        out SessionAgentPipeOwner? owner,
        out IDisposable? traceLogRegistration)
    {
        ArgumentNullException.ThrowIfNull(registerTraceLog);
        if (!SessionAgentPipeOwner.TryAcquire(endpoint, userSid, out owner))
        {
            traceLogRegistration = null;
            return false;
        }

        try
        {
            traceLogRegistration = registerTraceLog()
                ?? throw new InvalidOperationException(
                    "The Session Agent trace registration was not created.");
            return true;
        }
        catch
        {
            owner!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            owner = null;
            traceLogRegistration = null;
            throw;
        }
    }

    private static EventWaitHandle CreateShutdownEvent()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The session-agent executable path is unavailable.");
        return new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            SessionAgentShutdownEndpoint.ForExecutablePath(executablePath));
    }
}
