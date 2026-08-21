using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Platform;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class Program
{
    private const string IdentityRegistrationRelaunchArgument =
        "--identity-registration-relaunch";

    [STAThread]
    private static int Main(string[] args)
    {
        if (MicrophoneMuteStateProbeCommand.IsRequested(args))
        {
            try
            {
                return new MicrophoneMuteStateProbeCommand(
                    WindowsMicrophoneMuteStateQuery.Query,
                    Console.Out).Run();
            }
            catch
            {
                return 2;
            }
        }

        if (DynamicLightingIdentityProbeCommand.IsRequested(args))
        {
            try
            {
                return new DynamicLightingIdentityProbeCommand(
                        new WindowsDynamicLightingPackageIdentitySource(),
                        new WindowsLampArrayObservationSource())
                    .RunAsync(args, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // The explicit probe never writes a crash report because
                // exception details may contain package or device identifiers.
                return 2;
            }
        }

        try
        {
            return RunAsync(args.Contains(
                IdentityRegistrationRelaunchArgument,
                StringComparer.Ordinal)).GetAwaiter().GetResult();
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

    private static async Task<int> RunAsync(bool identityRegistrationRelaunch)
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;
        SessionAgentPipeEndpoint endpoint = SessionAgentPipeEndpoint.Create(userSid, sessionId);
        SessionAgentPipeEndpoint gpuEndpoint = SessionAgentPipeEndpoint.CreateGpu(
            userSid,
            sessionId);
        SessionAgentEventEndpoint eventEndpoint =
            SessionAgentEventEndpoint.Create(userSid, sessionId);
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
        if (!SessionAgentPipeOwner.TryAcquire(
                gpuEndpoint,
                userSid,
                out SessionAgentPipeOwner? gpuOwner))
        {
            Trace.TraceInformation(
                "The current user's GPU session-agent pipe is already owned.");
            return 0;
        }
        await using SessionAgentPipeOwner acquiredGpuOwner = gpuOwner!;
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
                using var gpuAppManager = new WindowsGpuAppManager();
                var gpuDispatcher = new SessionGpuRequestDispatcher(gpuAppManager);
                await using var gpuServer = new SessionGpuPipeServer(
                    acquiredGpuOwner,
                    gpuDispatcher);
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
                await using var powerModeCoordinator =
                    new WindowsPowerModeAutomationCoordinator(
                        new WindowsPowerModeController(),
                        resultObserver: result =>
                        {
                            if (!result.Result.Succeeded)
                            {
                                System.Diagnostics.Trace.TraceWarning(
                                    "OpenBlade Windows power mode failed: {0}",
                                    result.Result.Error);
                            }
                        });
                var powerModeAutomation = new ServicePowerModeAutomationLoop(
                    statusFeed,
                    powerModeCoordinator);
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
                    new ServiceReactiveLightingLoopOptions(
                        inputErrorHandler,
                        shortcutNotifications,
                        statusFeed,
                        specialKeyBindings.ShouldHandleInDriverMode,
                        specialKeyExecutor.ExecuteAsync,
                        specialKeyExecutor.ExecuteDefaultAsync,
                        ExecuteFunctionRowInputAsync: functionRowActions.ExecuteAsync,
                        ResetFunctionRowInputAsync: functionRowActions.ResetAsync));
                // Ambient Awareness and Audio Meter are mutually exclusive
                // renderers for the same service request type. Share their
                // persistent frame pipe so switching modes cannot strand a
                // connection owned by the inactive renderer.
                await using var hostRenderedLightingServiceClient = new ServiceClient();
                await using var ambient = new ServiceAmbientLightingLoop(
                    hostRenderedLightingServiceClient,
                    WindowsAmbientScreenSamplerFactory.Instance,
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade Ambient Awareness failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed);
                await using var audioMeter = new ServiceAudioMeterLightingLoop(
                    hostRenderedLightingServiceClient,
                    WindowsAudioMeterLightingSamplerFactory.Instance,
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade Audio Meter failed: {0}",
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
                await using var microphoneMuteServiceClient = new ServiceClient();
                var microphoneMuteIndicator = new ServiceMicrophoneMuteIndicatorLoop(
                    microphoneMuteServiceClient,
                    new WindowsMicrophoneMuteStateSource(),
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade microphone mute indication failed: {0}",
                        exception.Message),
                    statusWatcher: statusFeed);
                var lampArrayObservation = new ServiceLampArrayObservationLoop(
                    statusFeed,
                    new WindowsLampArrayObservationSource(),
                    observation => System.Diagnostics.Trace.TraceInformation(
                        "OpenBlade LampArray observation: {0}",
                        observation.ToSanitizedSummary()),
                    exception => System.Diagnostics.Trace.TraceWarning(
                        "OpenBlade LampArray observation failed: {0}",
                        exception.Message));

                Task statusFeedTask = statusFeed.RunAsync(lifetime.Token);
                Task specialKeyBindingsTask = specialKeyBindings.RunAsync(lifetime.Token);
                Task serverTask = server.RunAsync(lifetime.Token);
                Task gpuServerTask = gpuServer.RunAsync(lifetime.Token);
                Task automationTask = automation.RunAsync(lifetime.Token);
                Task powerModeAutomationTask = powerModeAutomation.RunAsync(lifetime.Token);
                Task reactiveTask = reactive.RunAsync(lifetime.Token);
                Task ambientTask = ambient.RunAsync(lifetime.Token);
                Task audioMeterTask = audioMeter.RunAsync(lifetime.Token);
                Task lightingIdleTask = lightingIdlePolicy.RunAsync(lifetime.Token);
                Task playbackMuteTask = playbackMuteIndicator.RunAsync(lifetime.Token);
                Task microphoneMuteTask = microphoneMuteIndicator.RunAsync(lifetime.Token);
                Task lampArrayObservationTask =
                    lampArrayObservation.RunAsync(lifetime.Token);
                Task<bool> identityRegistrationTask =
                    MonitorIdentityRegistrationAsync(
                        enabled: !identityRegistrationRelaunch,
                        lifetime.Token);
                Task[] supervisedTasks =
                [
                    serverTask,
                    gpuServerTask,
                    statusFeedTask,
                    specialKeyBindingsTask,
                    automationTask,
                    powerModeAutomationTask,
                    reactiveTask,
                    ambientTask,
                    audioMeterTask,
                    lightingIdleTask,
                    playbackMuteTask,
                    microphoneMuteTask,
                    lampArrayObservationTask,
                    identityRegistrationTask,
                ];
                Task completed = await Task.WhenAny(supervisedTasks).ConfigureAwait(false);
                bool relaunchForIdentity = ShouldRelaunchForIdentity(
                    completed,
                    identityRegistrationTask);
                Exception? terminalFailure = completed.IsFaulted
                    ? completed.Exception?.GetBaseException()
                    : null;
                if (!lifetime.IsCancellationRequested)
                {
                    lifetime.Cancel();
                }

                try
                {
                    await Task.WhenAll(supervisedTasks)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (terminalFailure is not null)
                {
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                if (terminalFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(terminalFailure).Throw();
                }
                if (relaunchForIdentity)
                {
                    await acquiredOwner.DisposeAsync();
                    return RelaunchAfterIdentityRegistration();
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

    internal static bool ShouldRelaunchForIdentity(
        Task completed,
        Task<bool> identityRegistrationTask) =>
        ReferenceEquals(completed, identityRegistrationTask)
        && identityRegistrationTask.IsCompletedSuccessfully
        && identityRegistrationTask.GetAwaiter().GetResult();

    internal static async Task<bool> MonitorIdentityRegistrationAsync(
        bool enabled,
        CancellationToken cancellationToken,
        PerUserDynamicLightingRegistration? registration = null)
    {
        if (enabled)
        {
            try
            {
                if (await (registration ?? new PerUserDynamicLightingRegistration())
                        .EnsureRegisteredAsync(cancellationToken))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "OpenBlade per-user Dynamic Lighting registration failed: {0}",
                    exception.Message);
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return false;
    }

    private static int RelaunchAfterIdentityRegistration()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The Session Agent executable path is unavailable.");
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = IdentityRegistrationRelaunchArgument,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return process is null ? 1 : 0;
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
