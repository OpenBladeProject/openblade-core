using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Explicit Windows backend, hosted only in the authenticated isolated input helper.
/// WH_KEYBOARD_LL is global across keyboards; it cannot identify the laptop keyboard.
/// </summary>
internal static class SnapTapWindowsHookLoop
{
    internal static (int HookData, int Message, int Input) NativeStructureSizes =>
        (Marshal.SizeOf<Native.KeyboardHookData>(), Marshal.SizeOf<Native.Message>(), Marshal.SizeOf<Native.Input>());

    internal sealed record AdmissionSnapshot(
        string? BiosManufacturer, string? BiosProduct, string? SystemManufacturer, string? SystemProduct,
        bool ActiveConsole, bool ControllersComplete, bool CompetingController,
        DiagnosticFailure? ObservationFailure = null);

    // Driver packages and keyboard collections are deliberately absent: this is
    // an explicitly selected Windows API backend, not a driver fallback.
    internal static string? AdmissionRejection(AdmissionSnapshot snapshot) =>
        RazerSnapTapDriverProfiles.Find(snapshot.BiosManufacturer, snapshot.BiosProduct,
            snapshot.SystemManufacturer, snapshot.SystemProduct) is null ? "UnsupportedModel"
        : !snapshot.ActiveConsole ? "InactiveConsoleSession"
        : !snapshot.ControllersComplete ? "IncompleteControllerObservation"
        : snapshot.CompetingController ? "CompetingController" : null;

    internal static bool AllReleased(Func<int, short> readKeyState)
    {
        for (int key = 1; key <= 255; key++)
        {
            if ((readKeyState(key) & 0x8000) != 0)
            {
                return false;
            }
        }
        return true;
    }

    internal static int Run(Func<bool> shouldStop)
    {
        ArgumentNullException.ThrowIfNull(shouldStop);
        using var publisher = new SnapTapStatusPublisher(backend: KeyboardInputBackend.WindowsHook);
        try
        {
            AdmissionSnapshot snapshot = CaptureAdmission();
            if (AdmissionRejection(snapshot) is { } reason)
            {
                publisher.Publish(reason == "UnsupportedModel" ? SnapTapRuntimeState.UnsupportedModel
                    : SnapTapRuntimeState.Unavailable, Failure("Admission", reason, cause: snapshot.ObservationFailure));
                return 3;
            }
            if (!Native.SystemParametersInfo(0x000A, 0, out uint speed, 0) || speed > 31)
            {
                publisher.Publish(SnapTapRuntimeState.Failed, Failure("InputConfiguration", "KeyboardSpeedUnavailable"));
                return 1;
            }
            if (shouldStop()) { return 0; }
            if (!SnapTapWindowsOwnershipJournal.TryAcquire(SnapTapWindowsOwnershipJournal.DefaultPath,
                    SnapTapDriverSupervisor.ReadBootIdentity, out var ownership, out DiagnosticFailure? ownershipFailure))
            {
                publisher.Publish(SnapTapRuntimeState.RecoveryRequired, ownershipFailure);
                return 4;
            }
            int exitCode = new InputLoop(shouldStop, publisher,
                TimeSpan.FromSeconds(1 / (2.5 + 27.5 * speed / 31))).Run();
            if (exitCode == 0 && !ownership!.CompleteCleanly(out ownershipFailure))
            {
                publisher.Publish(SnapTapRuntimeState.RecoveryRequired, ownershipFailure);
                return 4;
            }
            return exitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            publisher.Publish(SnapTapRuntimeState.Failed, SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Worker, "WindowsHook", "Admission", "WindowsHookFailed", exception));
            return 1;
        }
    }

    private static AdmissionSnapshot CaptureAdmission()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? bios = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
        using RegistryKey? system = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemInformation", writable: false);
        (bool detected, bool complete, DiagnosticFailure? failure) = WindowsRazerSnapTapDriverDiscovery.ObserveControllers();
        return new(Read(bios, "SystemManufacturer"), Read(bios, "SystemProductName"),
            Read(system, "SystemManufacturer"), Read(system, "SystemProductName"),
            WindowsRazerSnapTapDriverDiscovery.HasActiveConsoleSession(), complete, detected, failure);

        static string? Read(RegistryKey? key, string name) =>
            key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private static DiagnosticFailure Failure(string stage, string reason, bool mayHaveSubmitted = false,
        DiagnosticFailure? cause = null) => SnapTapDiagnosticFailure.Create(
            SnapTapDiagnosticFailure.Worker, "WindowsHook", stage, reason, mayHaveSubmitted, cause);

    /// <summary>Pure state seam: no hook registration or native input in offline tests.</summary>
    internal sealed class InputState
    {
        private readonly SnapTapPriorityCore core;
        private nint targetWindow;
        private nint targetLayout;
        private bool confirmedInput;

        internal InputState(Func<SnapTapOutputBatch, int> send, TimeSpan? repeatInterval = null)
        {
            core = new SnapTapPriorityCore(batch =>
            {
                try
                {
                    int sent = send(batch);
                    if (sent != batch.Count)
                    {
                        Failure ??= SnapTapWindowsHookLoop.Failure("SendInput", "OutputCountMismatch", true);
                    }
                    return sent;
                }
                catch (Exception exception)
                {
                    Failure ??= SnapTapDiagnosticFailure.FromException(SnapTapDiagnosticFailure.Worker,
                        "WindowsHook", "SendInput", "OutputFailed", exception, true);
                    throw;
                }
            }, repeatInterval: repeatInterval);
        }

        internal DiagnosticFailure? Failure { get; private set; }
        internal bool Faulted => core.Faulted;
        internal ushort Winner => core.Winner;
        internal SnapTapRuntimeState Status => Faulted ? SnapTapRuntimeState.Failed
            : core.Enabled ? confirmedInput ? SnapTapRuntimeState.Active : SnapTapRuntimeState.Armed
            : targetWindow == 0 || targetLayout == 0 ? SnapTapRuntimeState.Paused : SnapTapRuntimeState.WaitingForRelease;

        internal bool ContextMatches(nint foreground, nint layout) =>
            foreground != 0 && layout != 0 && foreground == targetWindow && layout == targetLayout;

        private bool ObserveContext(nint foreground, nint layout)
        {
            if (Faulted)
            {
                return false;
            }
            if (core.Winner != 0 && !ContextMatches(foreground, layout))
            {
                Fail("ForegroundOrLayoutChanged", true);
                return false;
            }
            if (!ContextMatches(foreground, layout))
            {
                core.Disable();
                targetWindow = foreground;
                targetLayout = layout;
                confirmedInput = false;
            }
            return foreground != 0 && layout != 0;
        }

        internal void Reconcile(nint foreground, nint layout, bool allReleased)
        {
            if (ObserveContext(foreground, layout) && !core.Enabled && core.ArePhysicallyReleased && allReleased)
            {
                core.Enable();
            }
        }

        internal SnapTapInputResult Handle(uint scan, uint flags, nuint message, nint foreground, nint layout)
        {
            // Never consume or feed injected events back into physical held state,
            // including lower-integrity input and this helper's own output.
            if ((flags & 0x12) != 0 || (flags & 1) != 0 || scan is not (0x1E or 0x20)
                || message is not (0x100 or 0x101 or 0x104 or 0x105) || Faulted)
            {
                return default;
            }
            if (!ObserveContext(foreground, layout))
            {
                // While waiting, still observe releases even without foreground;
                // otherwise an out-of-focus release could prevent rearming forever.
                if (!Faulted) { core.Handle(new((ushort)scan, message is 0x100 or 0x104)); }
                return default;
            }
            SnapTapInputResult result = core.Handle(new((ushort)scan, message is 0x100 or 0x104));
            confirmedInput |= result.Handled;
            return result;
        }

        internal void Repeat() => core.RepeatRestoredWinner();

        internal void Stop(nint foreground, nint layout, bool admitted)
        {
            if (!Faulted && core.Winner != 0 && (!admitted || !ContextMatches(foreground, layout)))
            {
                Fail("HeldOutputUncertain", true);
            }
            if (!Faulted)
            {
                core.Disable();
            }
        }

        internal void Fail(string reason, bool mayHaveSubmitted = false, DiagnosticFailure? cause = null)
        {
            Failure ??= SnapTapWindowsHookLoop.Failure("InputLoop", reason, mayHaveSubmitted || core.Winner != 0, cause);
            core.Poison();
        }
    }

    private sealed class InputLoop
    {
        private readonly Func<bool> shouldStop;
        private readonly SnapTapStatusPublisher publisher;
        private readonly Native.HookProc callback;
        private readonly Native.Input[] inputs = new Native.Input[2];
        private readonly InputState state;
        private readonly nuint tag = unchecked((nuint)(0x4F42535400000000UL | (uint)Environment.ProcessId));
        private nint hook;
        private nuint timer;
        private bool stopping;
        private bool admitted = true;
        private long checkedAt;

        internal InputLoop(Func<bool> shouldStop, SnapTapStatusPublisher publisher, TimeSpan repeatInterval)
        {
            this.shouldStop = shouldStop;
            this.publisher = publisher;
            callback = OnKeyboard;
            state = new InputState(Send, repeatInterval);
            checkedAt = Environment.TickCount64;
        }

        internal int Run()
        {
            publisher.Publish(SnapTapRuntimeState.WaitingForRelease);
            string stage = "HookSetup";
            try
            {
                if (shouldStop()) { return 0; }
                Native.PeekMessage(out _, 0, 0, 0, 0);
                hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
                if (hook == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                stage = "TimerSetup";
                timer = Native.SetTimer(0, 0, 10, 0);
                if (timer == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                stage = "InputLoop";
                // The timer makes parent/stop/session checks bounded even when no
                // input arrives. Each message checks stop, so queue traffic cannot starve it.
                while (!shouldStop() && !state.Faulted)
                {
                    int received = Native.GetMessage(out Native.Message message, 0, 0, 0);
                    if (received <= 0)
                    {
                        if (received < 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                        break;
                    }
                    if (shouldStop()) { break; }
                    RefreshAdmission();
                    if (state.Faulted) { break; }
                    nint foreground = Native.GetForegroundWindow();
                    nint layout = KeyboardLayout(foreground);
                    state.Reconcile(foreground, layout, AllReleased(Native.GetAsyncKeyState)
                        && layout != 0 && SnapTapKeyReleaseAdmission.AreReleased(
                            scan => Native.MapVirtualKeyEx(scan, 3, layout), Native.GetAsyncKeyState));
                    if (message.Id == 0x0113 && message.WParam == timer) { state.Repeat(); }
                    if (publisher.Failed) { state.Fail("StatusWriteFailed"); }
                    publisher.Publish(state.Status, state.Failure);
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
                or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                state.Fail("WindowsHookFailed", cause: SnapTapDiagnosticFailure.FromException(
                    SnapTapDiagnosticFailure.Worker, "WindowsHook", stage, "NativeOperationFailed", exception));
            }
            finally
            {
                stopping = true;
                // Only release a confirmed winner in its original focus/layout.
                // An uncertain SendInput result is terminal and never cleaned up by replay.
                nint foreground = Native.GetForegroundWindow();
                state.Stop(foreground, KeyboardLayout(foreground), admitted
                    && WindowsRazerSnapTapDriverDiscovery.HasActiveConsoleSession());
                if (hook != 0 && !Native.UnhookWindowsHookEx(hook))
                {
                    state.Fail("UnhookFailed", cause: SnapTapDiagnosticFailure.FromException(
                        SnapTapDiagnosticFailure.Worker, "WindowsHook", "Unhook", "UnhookFailed",
                        new Win32Exception(Marshal.GetLastWin32Error())));
                }
                if (timer != 0 && !Native.KillTimer(0, timer)) { state.Fail("TimerCleanupFailed"); }
                publisher.Publish(state.Faulted ? SnapTapRuntimeState.Failed : SnapTapRuntimeState.Off, state.Failure);
                GC.KeepAlive(callback);
            }
            return state.Faulted || publisher.Failed ? 1 : 0;
        }

        private void RefreshAdmission()
        {
            // Keep registry/process enumeration outside the low-level callback.
            // These observations expire instead of indefinitely admitting output.
            long now = Environment.TickCount64;
            if (now - checkedAt < 250) { return; }
            checkedAt = now;
            AdmissionSnapshot snapshot = CaptureAdmission();
            if (AdmissionRejection(snapshot) is { } reason)
            {
                admitted = false;
                state.Fail(reason, cause: snapshot.ObservationFailure);
            }
        }

        private bool CanOutput() => admitted && Environment.TickCount64 - checkedAt <= 500
            && WindowsRazerSnapTapDriverDiscovery.HasActiveConsoleSession()
            && (stopping || !shouldStop());

        private int Send(SnapTapOutputBatch batch)
        {
            nint foreground = Native.GetForegroundWindow();
            if (!CanOutput() || !state.ContextMatches(foreground, KeyboardLayout(foreground)))
            {
                state.Fail("OutputAdmissionRejected");
                return 0;
            }
            for (int index = 0; index < batch.Count; index++)
            {
                SnapTapKeyStroke key = index == 0 ? batch.First : batch.Second;
                inputs[index] = new Native.Input
                {
                    Type = 1,
                    Data = new Native.InputUnion
                    {
                        Keyboard = new Native.KeyboardInput
                        {
                            Scan = key.ScanCode,
                            Flags = 8u | (key.IsDown ? 0u : 2u),
                            Extra = tag,
                        },
                    },
                };
            }
            return (int)Native.SendInput((uint)batch.Count, inputs, Marshal.SizeOf<Native.Input>());
        }

        private nint OnKeyboard(int code, nuint message, nint data)
        {
            if (code < 0 || data == 0 || stopping)
            {
                return Native.CallNextHookEx(hook, code, message, data);
            }
            try
            {
                Native.KeyboardHookData key = Marshal.PtrToStructure<Native.KeyboardHookData>(data);
                if ((key.Flags & 0x12) != 0 || shouldStop())
                {
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if (!CanOutput())
                {
                    state.Fail("AdmissionExpired");
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                nint foreground = Native.GetForegroundWindow();
                SnapTapInputResult result = state.Handle(key.Scan, key.Flags, message, foreground, KeyboardLayout(foreground));
                return result.Suppress ? 1 : Native.CallNextHookEx(hook, code, message, data);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ObjectDisposedException)
            {
                state.Fail("HookCallbackFailed", cause: SnapTapDiagnosticFailure.FromException(
                    SnapTapDiagnosticFailure.Worker, "WindowsHook", "Callback", "CallbackFailed", exception));
                return Native.CallNextHookEx(hook, code, message, data);
            }
        }
    }

    private static nint KeyboardLayout(nint window)
    {
        uint thread = Native.GetWindowThreadProcessId(window, out _);
        return thread == 0 ? 0 : Native.GetKeyboardLayout(thread);
    }

    private static class Native
    {
        internal delegate nint HookProc(int code, nuint message, nint data);
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardHookData
        {
            internal uint Vk, Scan, Flags, Time;
            internal nuint Extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window;
            internal uint Id;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal int X, Y;
            internal uint Private;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort Vk, Scan;
            internal uint Flags, Time;
            internal nuint Extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal nuint Extra;
        }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", ExactSpelling = true, SetLastError = true)]
        internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
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
        [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true, SetLastError = true)]
        internal static extern int GetMessage(out Message message, nint window, uint first, uint last);
        [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
        internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    }
}
