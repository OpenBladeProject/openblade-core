using System.Runtime.InteropServices;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class SnapTapDriverLoop
{
    internal static int Run(int parentId, Func<bool> shouldStop)
    {
        var admission = new RazerSnapTapDriverAdmission();
        if (!admission.TryGetEndpoint(out var endpoint))
        {
            return 3;
        }
        using EventWaitHandle heartbeat = EventWaitHandle.OpenExisting(SnapTapDriverSupervisor.HeartbeatName(parentId));
        using var publisher = new SnapTapStatusPublisher();
        nint targetWindow = 0;
        nint targetLayout = 0;
        bool stopping = false;
        bool confirmedInput = false;
        using var transport = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint),
            () => !shouldStop() && AllReleased(), CanOutput);
        var session = new RazerSnapTapHookSession(transport);
        if (!Native.SystemParametersInfo(0x000A, 0, out uint speed, 0) || speed > 31)
        {
            return 1;
        }
        var processor = new SnapTapDriverInputProcessor(Send,
            repeatInterval: TimeSpan.FromSeconds(1 / (2.5 + 27.5 * speed / 31)));
        try
        {
            publisher.Publish(SnapTapRuntimeState.WaitingForRelease);
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
            if (!session.Start())
            {
                return 1;
            }
            while (!shouldStop() && !processor.Faulted && !publisher.Failed)
            {
                nint foreground = Native.GetForegroundWindow();
                nint layout = KeyboardLayout(foreground);
                if (processor.Winner != 0 && (foreground != targetWindow || layout != targetLayout))
                {
                    processor.Poison();
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
            bool stopped = !processor.Faulted && session.Stop();
            bool failed = !stopped || publisher.Failed;
            publisher.Publish(failed ? SnapTapRuntimeState.Failed : SnapTapRuntimeState.Off);
            return failed ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            processor.Poison();
            publisher.Publish(SnapTapRuntimeState.Failed);
            return 1;
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
                transport.Send(index == 0 ? batch.First : batch.Second);
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
