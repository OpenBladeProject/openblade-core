using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class SnapTapDriverLoop
{
    // A clean pause after a focus loss or a deferred subscription. The supervisor
    // rearms only with this code and the matching fresh unambiguous Paused status.
    internal const int CleanPauseExitCode = 8;

    internal static int Run(int parentId, Func<bool> shouldStop,
        KeyboardDriverFeatures features = KeyboardDriverFeatures.SnapTap)
    {
        KeyboardDriverStatusStore.ConfigureScope(features);
        var admission = new RazerSnapTapDriverAdmission();
        using var publisher = new SnapTapStatusPublisher();
        RazerSnapTapDriverEndpoint? endpoint = null;
        nint targetWindow = 0;
        nint targetLayout = 0;
        bool stopping = false;
        DriverKeyboardOutput? output = null;
        bool confirmedInput = false;
        string stage = "Admission";
        RazerSnapTapDriverTransport? transport = null;
        RazerSnapTapHookSession? session = null;
        SnapTapDriverInputProcessor? processor = null;
        FunctionRowDriverContext? functionContext = null;
        FunctionRowDriverInputProcessor? functionProcessor = null;
        SnapTapDeliveryMonitor? delivery = null;
        var deliveryTracker = new SnapTapDeliveryTracker();
        var deliveryClock = Stopwatch.StartNew();
        bool deliveryObservable = false;
        TimeSpan lastDeliveryPass = TimeSpan.Zero;
        try
        {
            if (!admission.TryGetEndpointWithFailure(out endpoint, out _, out DiagnosticFailure? admissionFailure))
            {
                publisher.Publish(SnapTapRuntimeState.Unavailable, admissionFailure
                    ?? SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Worker, "Admission", stage, "AdmissionRejected"));
                return 3;
            }
            publisher.SetProfile(endpoint.Profile);
            features = SnapTapDriverSupervisor.AdmitFeatures(features, endpoint.Profile);
            if (features == KeyboardDriverFeatures.None) { return 3; }
            KeyboardDriverStatusStore.ConfigureScope(features);
            RazerKeyboardHookPurpose purpose = RazerKeyboardHookScope.Purpose(features);
            bool snapEnabled = features.HasFlag(KeyboardDriverFeatures.SnapTap);
            if (features.HasFlag(KeyboardDriverFeatures.FunctionRow))
            {
                stage = "FunctionRowMetadata";
                functionContext = new(endpoint.Profile);
            }
            stage = "HeartbeatOpen";
            using EventWaitHandle heartbeat = EventWaitHandle.OpenExisting(
                SnapTapDriverSupervisor.HeartbeatName(parentId));
            using EventWaitHandle admitting = EventWaitHandle.OpenExisting(
                SnapTapDriverSupervisor.AdmissionName(parentId));
            stage = "TransportOpen";
            transport = new RazerSnapTapDriverTransport(endpoint!.Path,
                () => AdmitWithProgress(() => admission.IsCurrent(endpoint), () => heartbeat.Set(), admitting),
                () => !shouldStop() && AllReleased() && (functionContext is null || functionContext.Ready), CanOutput, purpose);
            output = new DriverKeyboardOutput(DecideOutput, SendDriverOutput);
            if (functionContext is not null)
            {
                functionProcessor = new(output.SendFunctionRow, functionContext.Publish, functionContext.Reset);
            }
            stage = "InputConfiguration";
            if (!Native.SystemParametersInfo(0x000A, 0, out uint speed, 0) || speed > 31)
            {
                publisher.Publish(SnapTapRuntimeState.Failed, SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker, "Run", stage, "KeyboardSpeedUnavailable"));
                return 1;
            }
            processor = new SnapTapDriverInputProcessor(output.SendBatch,
                repeatInterval: TimeSpan.FromSeconds(1 / (2.5 + 27.5 * speed / 31)));
            // Before any hook exists, so a helper that cannot confirm delivery never consumes keys.
            stage = "DeliveryMonitor";
            delivery = new SnapTapDeliveryMonitor(functionContext?.DeliveryFeed);
            publisher.Publish(SnapTapRuntimeState.WaitingForRelease);
            stage = "WaitingForRelease";
            // Do not hook an in-flight hold whose initial down we did not see.
            while (!shouldStop())
            {
                nint foreground = Native.GetForegroundWindow();
                bool hasForeground = foreground != 0 && KeyboardLayout(foreground) != 0;
                if (functionContext?.Failed == true)
                {
                    throw new InvalidOperationException("Function-row metadata observation failed.");
                }
                if (hasForeground && AllReleased() && (functionContext is null || functionContext.Ready))
                {
                    break;
                }
                publisher.Publish(hasForeground ? SnapTapRuntimeState.WaitingForRelease : SnapTapRuntimeState.Paused);
                delivery.Pump(deliveryTracker);
                heartbeat.Set();
                Thread.Sleep(10);
            }
            if (shouldStop())
            {
                // No hook or output exists yet; report the clean stop for this worker.
                publisher.Publish(SnapTapRuntimeState.Off);
                return 0;
            }
            stage = "HookSetup";
            session = new RazerSnapTapHookSession(transport, purpose,
                owned =>
                {
                    RazerKeyboardOwnedHookEvidence.Write(parentId, owned);
                    heartbeat.Set();
                });
            if (!session.Start())
            {
                if (session.SubscriptionDeferred)
                {
                    // A key, mouse button or metadata change during hook setup refused the
                    // idle-only enable before submission. Exit like a focus pause: the
                    // guardian clears and reads back the owned hooks, then rearms.
                    publisher.Publish(SnapTapRuntimeState.Paused, SubscriptionDeferredFailure());
                    return CleanPauseExitCode;
                }
                publisher.Publish(SnapTapRuntimeState.Failed, session.Failure
                    ?? SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Worker, "HookSession", stage, "HookSetupFailed",
                        session.FailureMayHaveSubmitted));
                return 1;
            }
            stage = "InputLoop";
            DiagnosticFailure? loopFailure = null;
            bool foregroundPause = false;
            while (!shouldStop() && !processor.Faulted && !publisher.Failed
                && functionProcessor?.Faulted != true && functionContext?.Failed != true)
            {
                nint foreground = Native.GetForegroundWindow();
                nint layout = KeyboardLayout(foreground);
                if (processor.Winner != 0 && (foreground != targetWindow || layout != targetLayout))
                {
                    processor.Poison();
                    loopFailure = SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Worker, "Run", stage, "ForegroundChanged", true);
                    break;
                }
                if (processor.Winner == 0)
                {
                    targetWindow = foreground;
                    targetLayout = layout;
                    if (foreground == 0 || layout == 0)
                    {
                        // GetForegroundWindow may briefly return null during activation.
                        // Close without reading queued input; only the guardian may rearm
                        // a fresh worker after verified cleanup of this subscription.
                        foregroundPause = CanPauseForForegroundLoss(processor) && functionProcessor?.HasTrackedKeys != true;
                        loopFailure = ForegroundUnavailableFailure(foreground != 0, layout != 0, processor.ArePhysicallyReleased);
                        break;
                    }
                }
                if (snapEnabled && !processor.Enabled && foreground != 0 && layout != 0 && processor.ArePhysicallyReleased && Released())
                {
                    processor.Enable();
                }
                deliveryObservable = delivery.CanObserve(targetWindow);
                if (!deliveryObservable)
                {
                    deliveryTracker.Clear();
                }
                if (transport.TryRead(out SnapTapKeyStroke stroke))
                {
                    if (!Handle(stroke))
                    {
                        loopFailure = functionProcessor?.Failure ?? processor.Failure ?? SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Worker, "Run", stage, "InputProcessingFailed");
                        break;
                    }
                    confirmedInput = true;
                }
                processor.RepeatRestoredWinner();
                TimeSpan deliveryPass = deliveryClock.Elapsed;
                if (deliveryPass - lastDeliveryPass > SnapTapDeliveryTracker.FreezeGap)
                {
                    deliveryTracker.Rebase(deliveryPass);
                }
                lastDeliveryPass = deliveryPass;
                delivery.Pump(deliveryTracker);
                if (deliveryTracker.Check(deliveryPass) is { } lost)
                {
                    // The driver accepted a key that Windows never received. Stop all
                    // output and let the guardian clear the hooks so Windows gets the
                    // keys directly; the lost keys may still arrive late.
                    processor.Poison();
                    loopFailure = lost;
                    break;
                }
                KeyboardDriverStatusStore.SetFunctionKeyKnown(functionContext?.FunctionKeyKnown ?? true);
                publisher.Publish((processor.Enabled || !snapEnabled) ? (confirmedInput ? SnapTapRuntimeState.Active : SnapTapRuntimeState.Armed)
                    : foreground == 0 ? SnapTapRuntimeState.Paused : SnapTapRuntimeState.WaitingForRelease);
                // Only the actual input loop can report progress. A blocked
                // driver request cannot be masked by a heartbeat timer thread.
                heartbeat.Set();
            }
            if (processor.Faulted && loopFailure is null)
            {
                loopFailure = processor.Failure ?? SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker, "Run", stage, "ProcessorFaulted");
            }
            else if (publisher.Failed && loopFailure is null)
            {
                loopFailure = SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker, "PublishStatus", stage, "StatusWriteFailed");
            }
            stopping = true;
            if (!processor.Faulted && Native.GetForegroundWindow() == targetWindow
                && KeyboardLayout(targetWindow) == targetLayout)
            {
                processor.Disable();
            }
            else if (processor.Winner != 0)
            {
                processor.Poison();
            }
            bool functionDrained = functionProcessor?.HasTrackedKeys != true;
            if (!functionDrained && !processor.Faulted && functionProcessor?.Faulted != true
                && functionContext?.Failed != true)
            {
                functionDrained = FunctionRowDriverLoop.Pump(functionProcessor!, () =>
                {
                    heartbeat.Set();
                    if (!transport.TryRead(out var stroke)) { return null; }
                    if (stroke.ScanCode is 0x1E or 0x20)
                    {
                        if (!processor.Handle(stroke)) { throw new IOException("Keyboard output became uncertain during drain."); }
                        return null;
                    }
                    return stroke;
                }, () => true, () => functionContext!.FunctionKeyPressed,
                    () => functionContext!.PrimaryMode, () => functionContext!.DriverMode,
                    () => functionContext!.DeviceKind, functionContext!.DeviceKind);
            }
            // On ambiguous output, close the subscription without more writes.
            // The guardian reconciles only owned hook configuration after exit.
            stage = "Cleanup";
            var stopExit = new StopRefusalExit(output!.SnapStopRefused, output.FunctionStopRefused, publisher.Failed,
                session.Failure, processor.Faulted, processor.Failure, functionProcessor?.Faulted == true,
                functionProcessor?.Failure, loopFailure,
                functionProcessor?.TracksOnly(code => output.IsDelivered(code) || code == output.FunctionRefusedDown) ?? true);
            // Release every key this worker delivered and Windows still holds down: after
            // a stop refused output before submission with nothing else failed, or after
            // an ordinary stop whose drain passed later A/D keys through. Never after
            // uncertain output. Release before media cleanup, which can throw.
            // A function-row fault without uncertain output, such as a rejected media edge,
            // still releases keys the drain delivered.
            bool ordinaryClean = !processor.Faulted && functionDrained && functionProcessor?.OutputUncertain != true
                && session.Failure is null && !publisher.Failed;
            bool releaseAttempted = stopExit.MayRelease || ordinaryClean;
            bool keysReleased = releaseAttempted && output.TryReleaseDelivered();
            functionProcessor?.ReleaseMedia();
            bool closed = session.Stop();
            bool functionStopped = functionContext?.Stop() ?? true;
            if (stopExit.IsClean(keysReleased, closed, functionStopped))
            {
                publisher.Publish(SnapTapRuntimeState.Off);
                return publisher.Failed ? 1 : 0;
            }
            bool stopped = !processor.Faulted && closed && functionDrained && functionStopped
                && functionProcessor?.OutputUncertain != true && (!releaseAttempted || keysReleased);
            // Counts only: diagnostics never record which keys were held.
            DiagnosticFailure? releaseFailure = output.ReleaseUncertain
                ? SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Worker, "Run", stage, "StopReleaseFailed", true)
                : releaseAttempted && !keysReleased
                    ? SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Worker, "Run", stage, "KeysNotReleased") with
                    {
                        Context = $"Held={output.DeliveredCount}",
                    }
                    : null;
            DiagnosticFailure? terminalFailure = session.Failure ?? releaseFailure ?? functionProcessor?.Failure ?? processor.Failure ?? loopFailure;
            bool failed = !stopped || publisher.Failed;
            if (foregroundPause && !failed && session.Failure is null && processor.Failure is null)
            {
                publisher.Publish(SnapTapRuntimeState.Paused, loopFailure);
                return CleanPauseExitCode;
            }
            if (failed || terminalFailure is not null)
            {
                terminalFailure ??= SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker, "Run", stage,
                    publisher.Failed ? "StatusWriteFailed" : "CleanupFailed",
                    session.FailureMayHaveSubmitted || processor.Failure?.MayHaveSubmitted == true);
                publisher.Publish(SnapTapRuntimeState.Failed, terminalFailure);
            }
            else
            {
                publisher.Publish(SnapTapRuntimeState.Off);
            }
            return failed ? 1 : 0;
        }
        catch (Exception exception)
        {
            bool mayHaveSubmitted = session?.FailureMayHaveSubmitted == true
                || processor?.Failure?.MayHaveSubmitted == true || functionProcessor?.OutputUncertain == true
                || functionProcessor?.HasTrackedKeys == true;
            DiagnosticFailure failure = session?.Failure ?? functionProcessor?.Failure ?? processor?.Failure ?? SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Worker,
                "Run",
                stage,
                "UnexpectedFailure",
                exception,
                mayHaveSubmitted);
            processor?.Poison();
            publisher.Publish(SnapTapRuntimeState.Failed, failure);
            return 1;
        }
        finally
        {
            transport?.Dispose();
            functionProcessor?.ReleaseMedia();
            functionContext?.Dispose();
            delivery?.Dispose();
        }

        void SendDriverOutput(SnapTapKeyStroke stroke)
        {
            transport!.Send(stroke);
            // Start the deadline once the driver has accepted the key: each send first
            // repeats full admission, which can take over a second under load. Only
            // this thread's Pump hands observed keys to the tracker, so a key that
            // arrives during the send is still matched.
            if (deliveryObservable)
            {
                deliveryTracker.Expect(stroke, deliveryClock.Elapsed);
            }
        }

        DriverOutputDecision DecideOutput() => OutputDecision(targetWindow, targetLayout,
            Native.GetForegroundWindow(), KeyboardLayout(targetWindow), stopping, shouldStop());

        bool CanOutput() => DecideOutput() == DriverOutputDecision.Allowed;

        bool Handle(SnapTapKeyStroke stroke)
        {
            if (stroke.ScanCode is 0x1E or 0x20) { return processor!.Handle(stroke); }
            if (functionProcessor is null || functionContext is null) { return false; }
            // Known/pressed are one metadata observation. Reading them separately
            // could combine an earlier false value with a newly known held Fn.
            bool? functionKey = functionContext.FunctionKeyState;
            return functionProcessor.Handle(stroke, functionContext.DeviceKind,
                functionKey == true, functionContext.PrimaryMode,
                functionContext.Ready && functionKey.HasValue);
        }
    }

    internal static DriverOutputDecision OutputDecision(nint targetWindow, nint targetLayout,
        nint currentWindow, nint currentLayout, bool stopping, bool stopRequested) =>
        !IsOutputTargetCurrent(targetWindow, targetLayout, currentWindow, currentLayout, true)
            ? DriverOutputDecision.TargetChanged
            : !stopping && stopRequested ? DriverOutputDecision.StopRequested : DriverOutputDecision.Allowed;

    // Brackets only the user-mode admission that precedes each driver mutation, never
    // the driver request itself. Progress is signalled before the window flag is
    // cleared so the supervisor cannot see a closed window with a stale heartbeat.
    internal static bool AdmitWithProgress(Func<bool> admit, Action signalProgress, EventWaitHandle window)
    {
        signalProgress();
        window.Set();
        try
        {
            return admit();
        }
        finally
        {
            signalProgress();
            window.Reset();
        }
    }

    internal static DiagnosticFailure ForegroundUnavailableFailure(
        bool foregroundAvailable, bool layoutAvailable, bool physicallyReleased) =>
        SnapTapDiagnosticFailure.Create(
            SnapTapDiagnosticFailure.Worker, "Run", "InputLoop", "ForegroundUnavailable") with
        {
            Context = $"ForegroundAvailable={foregroundAvailable}; KeyboardLayoutAvailable={layoutAvailable}; PhysicallyReleased={physicallyReleased}",
        };

    internal static DiagnosticFailure SubscriptionDeferredFailure() =>
        SnapTapDiagnosticFailure.Create(
            SnapTapDiagnosticFailure.Worker, "Run", "HookSetup", "SubscriptionDeferred");

    internal static bool CanPauseForForegroundLoss(SnapTapDriverInputProcessor processor) =>
        !processor.Faulted && processor.Winner == 0 && processor.ArePhysicallyReleased;

    internal static bool IsOutputTargetCurrent(nint targetWindow, nint targetLayout,
        nint currentWindow, nint currentLayout, bool outputAllowed) =>
        targetWindow != 0 && targetLayout != 0 && currentWindow == targetWindow
            && currentLayout == targetLayout && outputAllowed;

    private static nint KeyboardLayout(nint window)
    {
        uint thread = Native.GetWindowThreadProcessId(window, out _);
        return thread == 0 ? 0 : Native.GetKeyboardLayout(thread);
    }

    private static bool Released()
    {
        nint layout = KeyboardLayout(Native.GetForegroundWindow());
        return layout != 0 && SnapTapKeyReleaseAdmission.AreReleased(
            scan => Native.MapVirtualKeyEx(scan, 3, layout), Native.GetAsyncKeyState);
    }

    internal static bool AllReleased()
    {
        // First aggregate enable can run RzCommon's pressed-key handoff. Admit
        // that transition only at an idle keyboard, including modifier keys and mouse buttons.
        for (int key = 1; key <= 255; key++)
        {
            if ((Native.GetAsyncKeyState(key) & 0x8000) != 0)
            {
                return false;
            }
        }
        return Released();
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")]
        internal static extern nint GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", ExactSpelling = true)]
        internal static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);
    }
}
