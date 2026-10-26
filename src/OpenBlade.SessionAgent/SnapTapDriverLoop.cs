using System.Runtime.InteropServices;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class SnapTapDriverLoop
{
    internal static int Run(int parentId, Func<bool> shouldStop)
    {
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
            stage = "HeartbeatOpen";
            using EventWaitHandle heartbeat = EventWaitHandle.OpenExisting(
                SnapTapDriverSupervisor.HeartbeatName(parentId));
            stage = "TransportOpen";
            transport = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint),
                () => !shouldStop() && AllReleased(), CanOutput);
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
            while (!AllReleased() && !shouldStop())
            {
                heartbeat.Set();
                Thread.Sleep(10);
            }
            if (shouldStop())
            {
                return 0;
            }
            stage = "HookSetup";
            session = new RazerSnapTapHookSession(transport);
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
            while (!shouldStop() && !processor.Faulted && !publisher.Failed)
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
                        // Close promptly rather than consume input on a desktop
                        // where current-user output ownership is unavailable.
                        loopFailure = SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Worker, "Run", stage, "ForegroundUnavailable");
                        break;
                    }
                }
                if (!processor.Enabled && foreground != 0 && layout != 0 && processor.ArePhysicallyReleased && Released())
                {
                    processor.Enable();
                }
                if (transport.TryRead(out SnapTapKeyStroke stroke))
                {
                    if (!processor.Handle(stroke))
                    {
                        loopFailure = processor.Failure ?? SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Worker, "Run", stage, "InputProcessingFailed");
                        break;
                    }
                    confirmedInput = true;
                }
                processor.RepeatRestoredWinner();
                publisher.Publish(processor.Enabled ? (confirmedInput ? SnapTapRuntimeState.Active : SnapTapRuntimeState.Armed)
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
            // On ambiguous output, close the subscription without more writes.
            // The guardian reconciles only owned hook configuration after exit.
            stage = "Cleanup";
            bool stopped = !processor.Faulted && session.Stop();
            DiagnosticFailure? terminalFailure = session.Failure ?? processor.Failure ?? loopFailure;
            bool failed = !stopped || publisher.Failed;
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
                || processor?.Failure?.MayHaveSubmitted == true;
            DiagnosticFailure failure = session?.Failure ?? processor?.Failure ?? SnapTapDiagnosticFailure.FromException(
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
        }

        bool CanOutput() => targetWindow != 0 && Native.GetForegroundWindow() == targetWindow
            && KeyboardLayout(targetWindow) == targetLayout && (stopping || !shouldStop());

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
