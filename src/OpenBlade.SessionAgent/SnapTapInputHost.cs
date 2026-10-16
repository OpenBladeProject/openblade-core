using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Runs without the Session Agent's Raw Input source in a separate process.</summary>
internal static class SnapTapInputHost
{
    internal const string ModeArgument = "--snap-tap-input";

    internal static (int HookData, int Message, int Input) NativeStructureSizes =>
        (Marshal.SizeOf<Native.KeyboardHookData>(), Marshal.SizeOf<Native.Message>(), Marshal.SizeOf<Native.Input>());

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == ModeArgument;

    internal static int Run(string[] args)
    {
        if (args.Length != 2
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId)
            || parentId <= 0 || parentId == Environment.ProcessId)
        {
            return 2;
        }
        try
        {
            string preferencesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade", "preferences.json");
            if (!new UiPreferenceStore(preferencesPath).LoadAsync().GetAwaiter().GetResult().SnapTapEnabled)
            {
                return 0;
            }
            using SafeProcessHandle parent = Native.OpenProcess(0x00101000, false, parentId);
            if (parent.IsInvalid || !ValidateParent(parent, parentId))
            {
                return 2;
            }
            using EventWaitHandle stop = EventWaitHandle.OpenExisting(
                SnapTapInputProcess.StopEventName(parentId));
            using var shutdown = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                SessionAgentShutdownEndpoint.ForExecutablePath(Environment.ProcessPath!));
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            string userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
            using Process current = Process.GetCurrentProcess();
            using var mutex = new Mutex(false, $"Local\\OpenBlade.SnapTap.{current.SessionId}.{userKey}");
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // An abandoned helper might have submitted input. Never rearm automatically.
                return 1;
            }
            if (!acquired)
            {
                return 1;
            }
            try
            {
                return new InputLoop(parent, stop, shutdown).Run();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or WaitHandleCannotBeOpenedException)
        {
            return 1;
        }
    }

    private static bool ValidateParent(SafeProcessHandle parent, int parentId)
    {
        if (Native.WaitForSingleObject(parent, 0) != 258
            || !Native.ProcessIdToSessionId((uint)parentId, out uint parentSession)
            || !Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSession)
            || parentSession != currentSession)
        {
            return false;
        }
        var image = new StringBuilder(32768);
        uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(parent, 0, image, ref length)
            || !string.Equals(Path.GetFullPath(image.ToString()),
                Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)
            || !Native.OpenProcessToken(parent, 8, out SafeAccessTokenHandle token))
        {
            return false;
        }
        using (token)
        using (var parentIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            return parentIdentity.User is not null && parentIdentity.User == identity.User;
        }
    }

    private sealed class InputLoop
    {
        private const uint StopMessage = 0x8001;
        private readonly SafeProcessHandle parent;
        private readonly EventWaitHandle stop;
        private readonly EventWaitHandle shutdown;
        private readonly Native.HookProc callback;
        private readonly Native.Input[] inputs = new Native.Input[2];
        private readonly SnapTapPriorityCore core;
        private readonly nuint tag = (nuint)(0x4F42535400000000UL | (uint)Environment.ProcessId);
        private SnapTapStatusPublisher? publisher;
        private nint hook;
        private nint targetWindow;
        private nint targetKeyboardLayout;
        private nuint timer;
        private uint threadId;
        private bool stopRequested;
        private bool failed;
        private bool stopping;
        private bool confirmedInput;

        internal InputLoop(SafeProcessHandle parent, EventWaitHandle stop, EventWaitHandle shutdown)
        {
            this.parent = parent;
            this.stop = stop;
            this.shutdown = shutdown;
            callback = OnKeyboard;
            core = new SnapTapPriorityCore(Send);
        }

        internal int Run()
        {
            using var statusPublisher = new SnapTapStatusPublisher();
            publisher = statusPublisher;
            publisher.Publish(SnapTapRuntimeState.WaitingForRelease);
            threadId = Native.GetCurrentThreadId();
            try
            {
                // Create the thread queue before any callback can request a stop.
                Native.PeekMessage(out _, 0, 0, 0, 0);
                hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
                if (hook == 0)
                {
                    failed = true;
                    return 1;
                }
                timer = Native.SetTimer(0, 0, 50, 0);
                if (timer == 0)
                {
                    failed = true;
                    return 1;
                }
                int received;
                while ((received = Native.GetMessage(out Native.Message message, 0, 0, 0)) > 0)
                {
                    if (message.Id == StopMessage || stopRequested || ShouldStop())
                    {
                        break;
                    }
                    ReconcileIdle();
                    if (stopRequested)
                    {
                        break;
                    }
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
                if (received < 0)
                {
                    failed = true;
                    core.Poison();
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException or ObjectDisposedException)
            {
                failed = true;
                core.Poison();
            }
            finally
            {
                stopping = true;
                // Only a confirmed owned key, in the original foreground, can be released.
                if (!core.Faulted && (core.Winner == 0 || Native.GetForegroundWindow() == targetWindow))
                {
                    core.Disable();
                }
                else
                {
                    core.Poison();
                }
                if (hook != 0 && !Native.UnhookWindowsHookEx(hook))
                {
                    failed = true;
                }
                if (timer != 0 && !Native.KillTimer(0, timer))
                {
                    failed = true;
                }
                publisher.Publish(failed || core.Faulted
                    ? SnapTapRuntimeState.Failed : SnapTapRuntimeState.Off);
                GC.KeepAlive(callback);
            }
            return failed || core.Faulted || publisher.Failed ? 1 : 0;
        }

        private bool ShouldStop() => stop.WaitOne(0) || shutdown.WaitOne(0)
            || Native.WaitForSingleObject(parent, 0) != 258;

        private void ReconcileIdle()
        {
            if (publisher!.Failed)
            {
                RequestFailure();
                return;
            }
            nint foreground = Native.GetForegroundWindow();
            if (core.Winner != 0 && (foreground != targetWindow || KeyboardLayout(foreground) != targetKeyboardLayout))
            {
                core.Poison();
                RequestFailure();
                return;
            }
            if (foreground == 0)
            {
                core.Disable();
                confirmedInput = false;
                publisher.Publish(SnapTapRuntimeState.Paused);
                return;
            }
            if (core.Winner == 0 && foreground != targetWindow)
            {
                confirmedInput = false;
                targetWindow = foreground;
                targetKeyboardLayout = KeyboardLayout(foreground);
            }
            if (!core.Enabled && foreground != 0 && core.ArePhysicallyReleased && Idle())
            {
                core.Enable();
            }
            publisher.Publish(core.Enabled ? (confirmedInput ? SnapTapRuntimeState.Active : SnapTapRuntimeState.Armed)
                : foreground == 0 ? SnapTapRuntimeState.Paused : SnapTapRuntimeState.WaitingForRelease);
        }

        private static nint KeyboardLayout(nint foreground)
        {
            uint thread = Native.GetWindowThreadProcessId(foreground, out _);
            return thread == 0 ? 0 : Native.GetKeyboardLayout(thread);
        }

        private static bool Idle()
        {
            nint layout = KeyboardLayout(Native.GetForegroundWindow());
            return layout != 0 && SnapTapKeyReleaseAdmission.AreReleased(
                scan => Native.MapVirtualKeyEx(scan, 3, layout), Native.GetAsyncKeyState);
        }

        private void RequestFailure()
        {
            failed = true;
            stopRequested = true;
            Native.PostThreadMessage(threadId, StopMessage, 0, 0);
        }

        private int Send(SnapTapOutputBatch batch)
        {
            // Parent loss does not authorize another press; final known release is allowed.
            if (targetWindow == 0 || Native.GetForegroundWindow() != targetWindow || KeyboardLayout(targetWindow) != targetKeyboardLayout
                || (!stopping && ShouldStop()))
            {
                return 0;
            }
            for (int i = 0; i < batch.Count; i++)
            {
                SnapTapKeyStroke key = i == 0 ? batch.First : batch.Second;
                inputs[i] = new Native.Input
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
                if ((key.Flags & 0x10) != 0 && key.Extra == tag)
                {
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if (key.Scan is not (0x1E or 0x20) || (key.Flags & 1) != 0
                    || message is not (0x100 or 0x101 or 0x104 or 0x105))
                {
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if (stopRequested || ShouldStop())
                {
                    stopRequested = true;
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if ((key.Flags & 0x10) != 0)
                {
                    core.Poison();
                    RequestFailure();
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                nint foreground = Native.GetForegroundWindow();
                if (core.Winner != 0 && (foreground != targetWindow || KeyboardLayout(foreground) != targetKeyboardLayout))
                {
                    core.Poison();
                    RequestFailure();
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if (foreground == 0)
                {
                    core.Disable();
                    return Native.CallNextHookEx(hook, code, message, data);
                }
                if (core.Winner == 0)
                {
                    targetWindow = foreground;
                    targetKeyboardLayout = KeyboardLayout(foreground);
                }
                bool consumed = core.Handle(new SnapTapKeyStroke(
                    (ushort)key.Scan, message is 0x100 or 0x104));
                if (consumed)
                {
                    confirmedInput = true;
                    publisher!.Publish(SnapTapRuntimeState.Active);
                }
                if (core.Faulted)
                {
                    RequestFailure();
                }
                return consumed ? 1 : Native.CallNextHookEx(hook, code, message, data);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                core.Poison();
                RequestFailure();
                return Native.CallNextHookEx(hook, code, message, data);
            }
        }
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
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint id, out uint session);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
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
        internal static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
        internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")]
        internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool KillTimer(nint window, nuint id);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    }
}
