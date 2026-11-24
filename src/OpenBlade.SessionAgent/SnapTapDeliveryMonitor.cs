using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.SessionAgent;

/// <summary>Reports keyboard events Windows actually received.</summary>
/// <remarks>
/// Create, pump and dispose it on the worker's input-loop thread. It uses its own
/// Raw Input sink, or the shared feed of this process's Fn observer when one
/// exists. Both are passive: unlike a low-level hook they cannot delay or alter input.
/// </remarks>
internal sealed class SnapTapDeliveryMonitor : IDisposable
{
    private const uint WindowMessageInput = 0x00FF;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const uint RawInputDeviceRemove = 0x00000001;
    private const uint RawInputCommand = 0x10000003;
    private const uint RawInputTypeKeyboard = 1;
    private const ushort KeyBreak = 0x0001;
    private const uint PeekRemove = 0x0001;
    private const int MessagesPerPump = 1024;
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly string className = $"OpenBlade.SnapTapDelivery.{Environment.ProcessId}";
    private readonly nint module;
    private readonly int ownIntegrity;
    private const uint BufferSize = 64;
    private readonly nint buffer = Marshal.AllocHGlobal((int)BufferSize);
    private bool bufferFreed;
    private nint window;
    private bool classRegistered;
    private bool inputRegistered;
    private nint cachedWindow;
    private bool cachedObservable;
    private readonly SnapTapKeyboardFeed? feed;

    /// <param name="sharedFeed">
    /// Keyboard packets from this process's existing Raw Input observer. Raw Input
    /// routes each device class to one window per process, so registering a second
    /// keyboard sink would take those packets away from that observer.
    /// </param>
    internal SnapTapDeliveryMonitor(SnapTapKeyboardFeed? sharedFeed = null)
    {
        module = Native.GetModuleHandleW(null);
        try
        {
            ownIntegrity = ProcessIntegrity(Native.GetCurrentProcess())
                ?? throw new InvalidOperationException("The input helper's integrity level could not be read.");
            feed = sharedFeed;
            if (feed is not null)
            {
                return;
            }
            nint defaultProcedure = Native.GetProcAddress(Native.GetModuleHandleW("user32.dll"), "DefWindowProcW");
            if (defaultProcedure == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var windowClass = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = defaultProcedure,
                Instance = module,
                ClassName = className,
            };
            if (Native.RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            classRegistered = true;
            window = Native.CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0,
                MessageOnlyWindow, 0, module, 0);
            if (window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Native.RawInputDevice[] devices =
                [new() { UsagePage = 0x01, Usage = 0x06, Flags = RawInputDeviceInputSink, Target = window }];
            if (!Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.RawInputDevice>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            inputRegistered = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether input sent to this window can be confirmed. Input to a window of
    /// higher integrity, or one whose integrity cannot be read, is not checked.
    /// </summary>
    internal bool CanObserve(nint targetWindow)
    {
        if (targetWindow != cachedWindow)
        {
            cachedWindow = targetWindow;
            cachedObservable = targetWindow != 0 && WindowIntegrity(targetWindow) is { } integrity
                && integrity <= ownIntegrity;
        }
        return cachedObservable;
    }

    /// <summary>Drains queued Raw Input into <paramref name="tracker"/>; bounded per call.</summary>
    internal void Pump(SnapTapDeliveryTracker tracker)
    {
        if (feed is not null)
        {
            feed.Drain(tracker);
            return;
        }
        int headerSize = Marshal.SizeOf<Native.RawInputHeader>();
        int keyboardSize = headerSize + Marshal.SizeOf<Native.RawKeyboard>();
        bool drained = false;
        for (int count = 0; count < MessagesPerPump; count++)
        {
            if (!Native.PeekMessageW(out Native.Message message, 0, 0, 0, PeekRemove))
            {
                drained = true;
                break;
            }
            if (message.Id != WindowMessageInput || message.Window != window)
            {
                Native.DispatchMessageW(ref message);
                continue;
            }
            uint size = BufferSize;
            uint result = Native.GetRawInputData(message.LParam, RawInputCommand, buffer,
                ref size, (uint)headerSize);
            if (result != uint.MaxValue && result >= keyboardSize)
            {
                var header = Marshal.PtrToStructure<Native.RawInputHeader>(buffer);
                var keyboard = Marshal.PtrToStructure<Native.RawKeyboard>(buffer + headerSize);
                // SendInput events, including OpenBlade's own media keys, carry no device.
                if (header.Type == RawInputTypeKeyboard && header.Device != 0)
                {
                    tracker.Observe(keyboard.MakeCode, (keyboard.Flags & KeyBreak) != 0);
                }
            }
            // Raw Input requires default processing to release the input handle.
            Native.DefWindowProcW(message.Window, message.Id, message.WParam, message.LParam);
        }
        // Only an emptied queue proves every delivered key has been seen.
        if (drained)
        {
            tracker.Pumped();
        }
    }

    public void Dispose()
    {
        if (inputRegistered)
        {
            Native.RawInputDevice[] devices =
                [new() { UsagePage = 0x01, Usage = 0x06, Flags = RawInputDeviceRemove, Target = 0 }];
            Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.RawInputDevice>());
            inputRegistered = false;
        }
        if (window != 0)
        {
            Native.DestroyWindow(window);
            window = 0;
        }
        if (classRegistered)
        {
            Native.UnregisterClassW(className, module);
            classRegistered = false;
        }
        if (!bufferFreed)
        {
            Marshal.FreeHGlobal(buffer);
            bufferFreed = true;
        }
    }

    private static int? WindowIntegrity(nint window)
    {
        if (Native.GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0)
        {
            return null;
        }
        nint process = Native.OpenProcess(0x1000, false, processId);
        if (process == 0)
        {
            return null;
        }
        try
        {
            return ProcessIntegrity(process);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static int? ProcessIntegrity(nint process)
    {
        if (!Native.OpenProcessToken(process, 0x0008, out nint token))
        {
            return null;
        }
        try
        {
            Native.GetTokenInformation(token, 25, 0, 0, out uint length);
            if (length == 0 || length > 1024)
            {
                return null;
            }
            nint label = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!Native.GetTokenInformation(token, 25, label, length, out _))
                {
                    return null;
                }
                nint sid = Marshal.ReadIntPtr(label);
                byte count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(label);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal nint Procedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint Background;
            internal string? MenuName;
            internal string ClassName;
            internal nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputDevice
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Flags;
            internal nint Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawInputHeader
        {
            internal uint Type;
            internal uint Size;
            internal nint Device;
            internal nuint WindowParameter;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawKeyboard
        {
            internal ushort MakeCode;
            internal ushort Flags;
            internal ushort Reserved;
            internal ushort VirtualKey;
            internal uint Message;
            internal uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window;
            internal uint Id;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandleW(string? name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        internal static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(nint handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(nint process, uint access, out nint token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(nint token, int informationClass, nint information,
            uint length, out uint returnLength);

        [DllImport("advapi32.dll")]
        internal static extern nint GetSidSubAuthorityCount(nint sid);

        [DllImport("advapi32.dll")]
        internal static extern nint GetSidSubAuthority(nint sid, uint index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClassW(string className, nint instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size,
            uint headerSize);

        [DllImport("user32.dll")]
        internal static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessageW(ref Message message);

        [DllImport("user32.dll")]
        internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
