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
        catch
        {
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        SessionAgentPipeEndpoint endpoint = SessionAgentPipeEndpoint.Create(userSid);
        if (!SessionAgentPipeOwner.TryAcquire(endpoint, userSid, out SessionAgentPipeOwner? owner))
        {
            return 0;
        }

        await using SessionAgentPipeOwner acquiredOwner = owner!;
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
                var specialKeyExecutor = new SpecialKeyActionExecutor(
                    specialKeyBindings,
                    new ServiceClient(),
                    specialKeyInput);
                var functionRowActions = new FunctionRowActionExecutor(
                    new ServiceClient(),
                    specialKeyInput,
                    new WmiDisplayBrightnessController());
                await using var statusFeed = new SharedServiceStatusFeed(
                    new ServiceClient());
                var automation = new ServiceDisplayAutomationLoop(
                    statusFeed,
                    coordinator);
                using IPerformanceShortcutNotificationSink shortcutNotifications =
                    WindowsPerformanceShortcutNotificationSink.CreateBestEffort(
                        exception => System.Diagnostics.Trace.TraceWarning(
                            "OpenBlade notification failed: {0}",
                            exception.Message));
                Action<Exception> inputErrorHandler = exception =>
                    System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade input observation failed: {0}",
                        exception.Message);
                await using var reactive = new ServiceReactiveLightingLoop(
                    new ServiceClient(),
                    WindowsReactiveKeyboardSourceFactory.Instance,
                    inputErrorHandler,
                    shortcutNotifications,
                    statusFeed,
                    specialKeyBindings.OverridesDefaultBehavior,
                    specialKeyExecutor.ExecuteAsync,
                    functionRowActions.ExecuteAsync);
                await using var ambient = new ServiceAmbientLightingLoop(
                    new ServiceClient(),
                    WindowsAmbientScreenSamplerFactory.Instance,
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade Ambient Awareness failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed);
                var lightingIdlePolicy = new ServiceLightingIdlePolicyLoop(
                    new ServiceClient(),
                    new WindowsUserIdleTimeSource(),
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade lighting power policy failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed,
                    displayStateSource: new WindowsSessionDisplayStateSource());

                Task statusFeedTask = statusFeed.RunAsync(lifetime.Token);
                Task specialKeyBindingsTask = specialKeyBindings.RunAsync(lifetime.Token);
                Task serverTask = server.RunAsync(lifetime.Token);
                Task automationTask = automation.RunAsync(lifetime.Token);
                Task reactiveTask = reactive.RunAsync(lifetime.Token);
                Task ambientTask = ambient.RunAsync(lifetime.Token);
                Task lightingIdleTask = lightingIdlePolicy.RunAsync(lifetime.Token);
                Task completed = await Task.WhenAny(
                    serverTask,
                    statusFeedTask,
                    specialKeyBindingsTask,
                    automationTask,
                    reactiveTask,
                    ambientTask,
                    lightingIdleTask).ConfigureAwait(false);
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
                            lightingIdleTask)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                return completed.IsFaulted ? 1 : 0;
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
