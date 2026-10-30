using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class SnapTapDriverLoop
{
    internal const int ForegroundPauseExitCode = 8;

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
        bool confirmedInput = false;
        string stage = "Admission";
        RazerSnapTapDriverTransport? transport = null;
        RazerSnapTapHookSession? session = null;
        SnapTapDriverInputProcessor? processor = null;
        FunctionRowDriverContext? functionContext = null;
        FunctionRowDriverInputProcessor? functionProcessor = null;
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
            stage = "TransportOpen";
            transport = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint),
                () => !shouldStop() && AllReleased() && (functionContext is null || functionContext.Ready), CanOutput, purpose);
            if (functionContext is not null)
            {
                functionProcessor = new(transport.Send, functionContext.Publish, functionContext.Reset);
            }
            stage = "InputConfiguration";
            if (!Native.SystemParametersInfo(0x000A, 0, out uint speed, 0) || speed > 31)
            {
                publisher.Publish(SnapTapRuntimeState.Failed, SnapTapDiagnosticFailure.Create(
                    SnapTapDiagnosticFailure.Worker, "Run", stage, "KeyboardSpeedUnavailable"));
                return 1;
            }
            processor = new SnapTapDriverInputProcessor(Send,
                repeatInterval: TimeSpan.FromSeconds(1 / (2.5 + 27.5 * speed / 31)));
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
                heartbeat.Set();
                Thread.Sleep(10);
            }
            if (shouldStop())
            {
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
                        loopFailure = SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Worker, "Run", stage, "ForegroundUnavailable") with
                        {
                            Context = $"ForegroundAvailable={foreground != 0}; KeyboardLayoutAvailable={layout != 0}; PhysicallyReleased={processor.ArePhysicallyReleased}",
                        };
                        break;
                    }
                }
                if (snapEnabled && !processor.Enabled && foreground != 0 && layout != 0 && processor.ArePhysicallyReleased && Released())
                {
                    processor.Enable();
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
            functionProcessor?.ReleaseMedia();
            // On ambiguous output, close the subscription without more writes.
            // The guardian reconciles only owned hook configuration after exit.
            stage = "Cleanup";
            bool closed = session.Stop();
            bool functionStopped = functionContext?.Stop() ?? true;
            bool stopped = !processor.Faulted && closed && functionDrained && functionStopped
                && functionProcessor?.OutputUncertain != true;
            DiagnosticFailure? terminalFailure = session.Failure ?? functionProcessor?.Failure ?? processor.Failure ?? loopFailure;
            bool failed = !stopped || publisher.Failed;
            if (foregroundPause && !failed && session.Failure is null && processor.Failure is null)
            {
                publisher.Publish(SnapTapRuntimeState.Paused, loopFailure);
                return ForegroundPauseExitCode;
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
        }

        bool CanOutput() => IsOutputTargetCurrent(targetWindow, targetLayout,
            Native.GetForegroundWindow(), KeyboardLayout(targetWindow), stopping || !shouldStop());

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

        int Send(SnapTapOutputBatch batch)
        {
            if (!CanOutput())
            {
                return 0;
            }
            int sent = 0;
            for (int index = 0; index < batch.Count; index++)
            {
                transport!.Send(index == 0 ? batch.First : batch.Second);
                sent++;
            }
            return sent;
        }
    }

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

    private static bool AllReleased()
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
