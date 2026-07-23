using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Capture;

/// <summary>
/// Read-only, time-bounded Raw Input probe for the RZ09-0581 keyboard collections.
/// It records scan codes and raw HID reports, but never translates keys to text or
/// inspects the foreground application.
/// </summary>
public sealed class SpecialKeyInputProbe
{
    private const uint WindowMessageInput = 0x00FF;
    private const uint WindowMessageClose = 0x0010;
    private const uint WindowMessageDestroy = 0x0002;
    private const uint RawInputCommand = 0x10000003;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const uint RawInputDeviceNotify = 0x00002000;
    private const uint RawInputDevicePageOnly = 0x00000020;
    private const uint RawInputDeviceName = 0x20000007;
    private const uint RawInputTypeKeyboard = 1;
    private const uint RawInputTypeHid = 2;
    private const int LowLevelKeyboardHook = 13;
    private const uint WindowMessageKeyDown = 0x0100;
    private const uint WindowMessageKeyUp = 0x0101;
    private const uint WindowMessageSystemKeyDown = 0x0104;
    private const uint WindowMessageSystemKeyUp = 0x0105;
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort ConsumerUsagePage = 0x000C;
    private const int MaximumRawInputBytes = 64 * 1024;
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly TextWriter output;
    private readonly string windowClassName = $"OpenBlade.SpecialKeyProbe.{Guid.NewGuid():N}";
    private readonly Dictionary<nint, string?> targetDevicePaths = new();
    private readonly WindowProcedure windowProcedure;
    private readonly HookProcedure hookProcedure;
    private nint windowHandle;
    private nint keyboardHook;
    private int rawInputMessages;
    private int targetInputMessages;
    private int deviceIdentityFailures;

    public SpecialKeyInputProbe(TextWriter output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        windowProcedure = ProcessWindowMessage;
        hookProcedure = ProcessKeyboardHook;
    }

    public int Run(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        nint module = GetModuleHandle(null);
        if (module == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var windowClass = new WindowClass
        {
            Size = checked((uint)Marshal.SizeOf<WindowClass>()),
            Instance = module,
            Procedure = windowProcedure,
            ClassName = windowClassName,
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            windowHandle = CreateWindowEx(
                0,
                windowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindow,
                0,
                module,
                0);
            if (windowHandle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            RegisterRawInput(windowHandle);
            keyboardHook = SetWindowsHookEx(LowLevelKeyboardHook, hookProcedure, module, 0);
            if (keyboardHook == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to install the read-only keyboard scan-code hook.");
            }
            output.WriteLine($"ListeningSeconds={duration.TotalSeconds:0}");
            output.WriteLine("Press M1, M2, M3, M4, and M5 once each, in that order.");

            _ = Task.Delay(duration).ContinueWith(
                _ => PostMessage(windowHandle, WindowMessageClose, 0, 0),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            while (true)
            {
                int result = GetMessage(out WindowMessage message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }
                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }

            output.WriteLine($"RawInputMessages={rawInputMessages}");
            output.WriteLine($"TargetInputMessages={targetInputMessages}");
            output.WriteLine($"DeviceIdentityFailures={deviceIdentityFailures}");
            output.WriteLine("ProbeComplete=True");
            return 0;
        }
        finally
        {
            nint hook = Interlocked.Exchange(ref keyboardHook, 0);
            if (hook != 0)
            {
                _ = UnhookWindowsHookEx(hook);
            }

            nint window = Interlocked.Exchange(ref windowHandle, 0);
            if (window != 0)
            {
                _ = DestroyWindow(window);
            }

            _ = UnregisterClass(windowClassName, module);
        }
    }

    private nint ProcessWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter)
    {
        if (message == WindowMessageInput)
        {
            try
            {
                rawInputMessages++;
                ProcessRawInput(messageParameter);
            }
            catch (Exception exception)
            {
                output.WriteLine($"RawInputError={exception.GetType().Name}:{exception.Message}");
            }
        }
        else if (message == WindowMessageClose)
        {
            _ = DestroyWindow(window);
            return 0;
        }
        else if (message == WindowMessageDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, windowParameter, messageParameter);
    }

    private nint ProcessKeyboardHook(int code, nuint windowParameter, nint messageParameter)
    {
        if (code >= 0
            && windowParameter is WindowMessageKeyDown
                or WindowMessageKeyUp
                or WindowMessageSystemKeyDown
                or WindowMessageSystemKeyUp)
        {
            LowLevelKeyboardInput input = Marshal.PtrToStructure<LowLevelKeyboardInput>(
                messageParameter);
            string state = windowParameter is WindowMessageKeyDown or WindowMessageSystemKeyDown
                ? "Down"
                : "Up";
            output.WriteLine(
                $"LowLevel ScanCode={input.ScanCode:X2};Flags={input.Flags:X2};State={state}");
        }

        return CallNextHookEx(keyboardHook, code, windowParameter, messageParameter);
    }

    private void ProcessRawInput(nint rawInputHandle)
    {
        uint headerSize = checked((uint)Marshal.SizeOf<RawInputHeader>());
        uint size = 0;
        uint sizingResult = GetRawInputData(
            rawInputHandle,
            RawInputCommand,
            0,
            ref size,
            headerSize);
        if (sizingResult == uint.MaxValue || size < headerSize || size > MaximumRawInputBytes)
        {
            return;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            uint receivedSize = size;
            uint result = GetRawInputData(
                rawInputHandle,
                RawInputCommand,
                buffer,
                ref receivedSize,
                headerSize);
            if (result == uint.MaxValue || receivedSize < headerSize)
            {
                return;
            }

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            string? devicePath = ResolveTargetDevicePath(header.Device);
            if (devicePath is null)
            {
                return;
            }

            targetInputMessages++;
            string collection = ExtractCollection(devicePath);
            if (header.Type == RawInputTypeKeyboard)
            {
                ProcessKeyboard(buffer, headerSize, receivedSize, collection);
            }
            else if (header.Type == RawInputTypeHid)
            {
                ProcessHid(buffer, headerSize, receivedSize, collection);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessKeyboard(nint buffer, uint headerSize, uint receivedSize, string collection)
    {
        int keyboardSize = Marshal.SizeOf<RawKeyboard>();
        if (receivedSize < headerSize + keyboardSize)
        {
            return;
        }

        RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(
            nint.Add(buffer, checked((int)headerSize)));
        string state = (keyboard.Flags & 0x0001) == 0 ? "Down" : "Up";
        string prefix = (keyboard.Flags & 0x0002) != 0
            ? "E0"
            : (keyboard.Flags & 0x0004) != 0 ? "E1" : "00";
        output.WriteLine(
            $"Keyboard Collection={collection};ScanCode={prefix}:{keyboard.MakeCode:X2};State={state}");
    }

    private void ProcessHid(nint buffer, uint headerSize, uint receivedSize, string collection)
    {
        const int rawHidHeaderSize = sizeof(uint) * 2;
        if (receivedSize < headerSize + rawHidHeaderSize)
        {
            return;
        }

        nint hid = nint.Add(buffer, checked((int)headerSize));
        uint reportSize = unchecked((uint)Marshal.ReadInt32(hid));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, sizeof(uint)));
        ulong rawBytes = (ulong)reportSize * reportCount;
        if (reportSize == 0
            || reportCount == 0
            || rawBytes > MaximumRawInputBytes
            || (ulong)headerSize + rawHidHeaderSize + rawBytes > receivedSize)
        {
            return;
        }

        nint reports = nint.Add(hid, rawHidHeaderSize);
        for (uint index = 0; index < reportCount; index++)
        {
            int length = checked((int)reportSize);
            var report = new byte[length];
            Marshal.Copy(nint.Add(reports, checked((int)(index * reportSize))), report, 0, length);
            output.WriteLine($"HID Collection={collection};Report={Convert.ToHexString(report)}");
        }
    }

    private string? ResolveTargetDevicePath(nint device)
    {
        if (targetDevicePaths.TryGetValue(device, out string? cached))
        {
            return cached;
        }

        uint characterCount = 0;
        uint sizingResult = GetRawInputDeviceInfo(device, RawInputDeviceName, null, ref characterCount);
        if (sizingResult == uint.MaxValue || characterCount == 0 || characterCount > 4096)
        {
            deviceIdentityFailures++;
            return null;
        }

        var name = new StringBuilder(checked((int)characterCount));
        uint result = GetRawInputDeviceInfo(device, RawInputDeviceName, name, ref characterCount);
        string? path = result != uint.MaxValue
            && name.ToString().Contains("VID_1532&PID_02E0", StringComparison.OrdinalIgnoreCase)
                ? name.ToString()
                : null;
        targetDevicePaths[device] = path;
        return path;
    }

    internal static string ExtractCollection(string devicePath)
    {
        int marker = devicePath.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && marker + 6 <= devicePath.Length
            ? devicePath.Substring(marker + 1, 5).ToUpperInvariant()
            : "Unknown";
    }

    private static void RegisterRawInput(nint target)
    {
        RawInputDevice[] devices =
        [
            new()
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = 0,
                Flags = RawInputDeviceInputSink | RawInputDeviceNotify | RawInputDevicePageOnly,
                Target = target,
            },
            new()
            {
                UsagePage = ConsumerUsagePage,
                Usage = 0,
                Flags = RawInputDeviceInputSink | RawInputDeviceNotify | RawInputDevicePageOnly,
                Target = target,
            },
        ];
        if (!RegisterRawInputDevices(
            devices,
            checked((uint)devices.Length),
            checked((uint)Marshal.SizeOf<RawInputDevice>())))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nuint WindowParameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        internal ushort MakeCode;
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort VirtualKey;
        internal uint Message;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure Procedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WindowParameter;
        internal nint MessageParameter;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    private delegate nint HookProcedure(
        int code,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out WindowMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        HookProcedure hook,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref WindowMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref WindowMessage message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] rawInputDevices,
        uint deviceCount,
        uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint rawInputHandle,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetRawInputDeviceInfoW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);
}
