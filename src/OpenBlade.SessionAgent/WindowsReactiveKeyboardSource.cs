using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class WindowsReactiveKeyboardSourceFactory : IReactiveKeyboardSourceFactory
{
    internal static WindowsReactiveKeyboardSourceFactory Instance { get; } = new();

    private WindowsReactiveKeyboardSourceFactory()
    {
    }

    public IReactiveKeyboardSource Create(Action<ReactiveKeyPulse> publish) =>
        new WindowsReactiveKeyboardSource(publish, _ => { });

    public IReactiveKeyboardSource Create(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut) =>
        new WindowsReactiveKeyboardSource(publish, publishPerformanceShortcut);
}

/// <summary>
/// Session-local, message-only Raw Input window shared by Reactive lighting and
/// the two captured performance shortcuts. The callbacks emit only anonymous
/// matrix slots or a typed shortcut source, never translated text, virtual-key
/// values, window titles, or foreground-process information.
/// </summary>
internal sealed class WindowsReactiveKeyboardSource : IReactiveKeyboardSource
{
    private const uint WindowMessageInput = 0x00FF;
    private const uint WindowMessageClose = 0x0010;
    private const uint WindowMessageDestroy = 0x0002;
    private const uint RawInputCommand = 0x10000003;
    private const uint RawInputTypeKeyboard = 1;
    private const uint RawInputTypeHid = 2;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const uint RawInputDevicePageOnly = 0x00000020;
    private const uint RawInputDeviceRemove = 0x00000001;
    private const uint RawInputDeviceName = 0x20000007;
    private const int MaximumRawInputBytes = 64 * 1024;
    private const ushort GenericDesktopUsagePage = 0x0001;
    private const ushort PageOnlyUsage = 0x0000;
    private static readonly nint MessageOnlyWindow = new(-3);
    private static readonly ConcurrentDictionary<nint, WindowsReactiveKeyboardSource>
        Sources = new();
    private static readonly WindowProcedure WindowProcedureCallback = ProcessWindowMessage;

    private readonly Action<ReactiveKeyPulse> publish;
    private readonly Action<PerformanceShortcutSource> publishPerformanceShortcut;
    private readonly string windowClassName = $"OpenBlade.Reactive.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource initialization =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread messageThread;
    private readonly ConcurrentDictionary<nint, bool> targetDevices = new();
    private nint windowHandle;
    private uint messageThreadId;
    private bool functionKeyPressed;
    private bool performanceFunctionKeyPressed;
    private bool dedicatedPerformanceButtonPressed;
    private bool functionPPressed;
    private int disposed;

    internal WindowsReactiveKeyboardSource(
        Action<ReactiveKeyPulse> publish,
        Action<PerformanceShortcutSource> publishPerformanceShortcut)
    {
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.publishPerformanceShortcut = publishPerformanceShortcut
            ?? throw new ArgumentNullException(nameof(publishPerformanceShortcut));
        messageThread = new Thread(RunMessageWindow)
        {
            IsBackground = true,
            Name = "OpenBlade Reactive input",
        };
        messageThread.Start();
        try
        {
            initialization.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        nint handle = Volatile.Read(ref windowHandle);
        bool closePosted = handle != 0 && PostMessage(handle, WindowMessageClose, 0, 0);
        if (!closePosted && Volatile.Read(ref messageThreadId) is uint threadId and not 0)
        {
            _ = PostThreadMessage(threadId, 0x0012, 0, 0); // WM_QUIT
        }

        if (GetCurrentThreadId() != Volatile.Read(ref messageThreadId)
            && !messageThread.Join(TimeSpan.FromSeconds(5)))
        {
            Trace.TraceWarning("Reactive input message window did not stop promptly.");
        }
    }

    private void RunMessageWindow()
    {
        nint module = 0;
        bool classRegistered = false;
        bool inputRegistered = false;
        try
        {
            messageThreadId = GetCurrentThreadId();
            module = GetModuleHandle(null);
            if (module == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var windowClass = new WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                Instance = module,
                Procedure = WindowProcedureCallback,
                ClassName = windowClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to register the Reactive input message window.");
            }
            classRegistered = true;

            nint created = CreateWindowEx(
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
            if (created == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to create the Reactive input message window.");
            }

            windowHandle = created;
            Sources[created] = this;
            RegisterRawInput(created, RawInputDeviceInputSink);
            inputRegistered = true;
            initialization.TrySetResult();

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
        }
        catch (Exception exception)
        {
            initialization.TrySetException(exception);
            if (Volatile.Read(ref disposed) == 0)
            {
                Trace.TraceError(
                    "Reactive keyboard observation failed: {0}",
                    exception.GetType().Name);
            }
        }
        finally
        {
            if (inputRegistered)
            {
                try
                {
                    RegisterRawInput(0, RawInputDeviceRemove);
                }
                catch (Win32Exception exception)
                {
                    Trace.TraceWarning(
                        "Reactive input registration cleanup failed: {0}",
                        exception.NativeErrorCode);
                }
            }

            nint handle = Interlocked.Exchange(ref windowHandle, 0);
            if (handle != 0)
            {
                Sources.TryRemove(handle, out _);
                _ = DestroyWindow(handle);
            }
            if (classRegistered)
            {
                _ = UnregisterClass(windowClassName, module);
            }

            initialization.TrySetException(
                new InvalidOperationException("Reactive input stopped before initialization."));
        }
    }

    private static nint ProcessWindowMessage(
        nint window,
        uint message,
        nuint windowParameter,
        nint messageParameter)
    {
        if (message == WindowMessageInput
            && Sources.TryGetValue(window, out WindowsReactiveKeyboardSource? source)
            && Volatile.Read(ref source.disposed) == 0)
        {
            try
            {
                source.ProcessRawInput(messageParameter);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Reactive keyboard packet processing failed: {0}",
                    exception.GetType().Name);
            }
        }
        else if (message == WindowMessageClose)
        {
            _ = DestroyWindow(window);
            return 0;
        }
        else if (message == WindowMessageDestroy)
        {
            Sources.TryRemove(window, out _);
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, windowParameter, messageParameter);
    }

    private void ProcessRawInput(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = checked((uint)Marshal.SizeOf<RawInputHeader>());
        uint sizingResult = GetRawInputData(
            rawInputHandle,
            RawInputCommand,
            0,
            ref size,
            headerSize);
        if (sizingResult == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (size < headerSize || size > MaximumRawInputBytes)
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
            if (result == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (result < headerSize || receivedSize < headerSize)
            {
                return;
            }

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Size < headerSize
                || header.Size > receivedSize
                || !targetDevices.GetOrAdd(header.Device, IsTargetInputDevice))
            {
                return;
            }

            if (header.Type == RawInputTypeKeyboard)
            {
                ProcessKeyboard(buffer, headerSize, receivedSize);
            }
            else if (header.Type == RawInputTypeHid)
            {
                ProcessHid(buffer, headerSize, receivedSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessKeyboard(nint buffer, uint headerSize, uint receivedSize)
    {
        int keyboardSize = Marshal.SizeOf<RawKeyboard>();
        if (receivedSize < headerSize + keyboardSize)
        {
            return;
        }

        RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(
            nint.Add(buffer, checked((int)headerSize)));
        if (!ReactiveRawKeyboardInput.TryCreatePulses(
            keyboard.MakeCode,
            keyboard.Flags,
            out ReactiveKeyPulse primary,
            out ReactiveKeyPulse? secondary))
        {
            return;
        }

        publish(primary);
        if (secondary is { } secondPulse)
        {
            publish(secondPulse);
        }
    }

    private void ProcessHid(nint buffer, uint headerSize, uint receivedSize)
    {
        const int rawHidHeaderSize = sizeof(uint) * 2;
        if (receivedSize < headerSize + rawHidHeaderSize)
        {
            return;
        }

        nint hid = nint.Add(buffer, checked((int)headerSize));
        uint reportSize = unchecked((uint)Marshal.ReadInt32(hid));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, sizeof(uint)));
        if (reportSize == 0 || reportCount == 0)
        {
            return;
        }

        ulong rawBytes = (ulong)reportSize * reportCount;
        ulong requiredBytes = (ulong)headerSize + rawHidHeaderSize + rawBytes;
        if (rawBytes > MaximumRawInputBytes || requiredBytes > receivedSize)
        {
            return;
        }

        nint reports = nint.Add(hid, rawHidHeaderSize);
        byte[] report = new byte[checked((int)reportSize)];
        for (uint index = 0; index < reportCount; index++)
        {
            Marshal.Copy(
                nint.Add(reports, checked((int)(index * reportSize))),
                report,
                0,
                report.Length);
            if (Rz09_0581PerformanceShortcutRawHidInput.TryCreateTrigger(
                report,
                ref performanceFunctionKeyPressed,
                ref dedicatedPerformanceButtonPressed,
                ref functionPPressed,
                out PerformanceShortcutSource shortcut))
            {
                publishPerformanceShortcut(shortcut);
            }
            if (Rz09_0581ReactiveRawHidInput.TryCreatePulse(
                report,
                ref functionKeyPressed,
                out ReactiveKeyPulse pulse))
            {
                publish(pulse);
            }
        }
    }

    private static bool IsTargetInputDevice(nint deviceHandle)
    {
        if (deviceHandle == 0)
        {
            return false;
        }

        uint characterCount = 0;
        uint sizingResult = GetRawInputDeviceInfo(
            deviceHandle,
            RawInputDeviceName,
            null,
            ref characterCount);
        if (sizingResult == uint.MaxValue || characterCount == 0 || characterCount > 4096)
        {
            return false;
        }

        var name = new StringBuilder(checked((int)characterCount));
        uint result = GetRawInputDeviceInfo(
            deviceHandle,
            RawInputDeviceName,
            name,
            ref characterCount);
        return result != uint.MaxValue
            && Rz09_0581ReactiveInputDevice.IsTargetPath(name.ToString());
    }

    private static void RegisterRawInput(nint target, uint flags)
    {
        RawInputDevice[] devices =
        [
            new()
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = PageOnlyUsage,
                Flags = flags | RawInputDevicePageOnly,
                Target = target,
            },
        ];
        if (!RegisterRawInputDevices(
            devices,
            checked((uint)devices.Length),
            checked((uint)Marshal.SizeOf<RawInputDevice>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to register keyboard input for Reactive lighting.");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint windowParameter,
        nint messageParameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out WindowMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

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
