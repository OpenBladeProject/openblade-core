using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Session-local physical keyboard source for Reactive lighting. Construct this
/// only while Reactive is active and dispose it as soon as the effect changes.
/// The data-minimized callback publishes matrix slots only. It does not retain,
/// log, or expose translated text, virtual-key values, or app/window identity.
/// </summary>
internal sealed class WindowsRawKeyboardSource : IDisposable
{
    private const uint WindowMessageInput = 0x00FF;
    private const uint WindowMessageNonClientDestroy = 0x0082;
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

    // If native subclass removal fails, retaining the source keeps its delegate
    // callable until WM_NCDESTROY proves that Windows has torn down the HWND.
    // Include the subclass id so a later source on the same HWND cannot replace
    // the delegate that still needs to remain rooted.
    private static readonly ConcurrentDictionary<SubclassKey, WindowsRawKeyboardSource>
        FailedRemovalKeepAlive = new();
    private static long nextSubclassId;

    private readonly nint windowHandle;
    private readonly Action<ReactiveKeyPulse> publish;
    private readonly SubclassProcedure subclassProcedure;
    private readonly nuint subclassId;
    private readonly ConcurrentDictionary<nint, bool> targetDevices = new();
    private bool functionKeyPressed;
    private int disposed;

    internal WindowsRawKeyboardSource(
        nint windowHandle,
        Action<ReactiveKeyPulse> publish)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle));
        }

        this.windowHandle = windowHandle;
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        subclassProcedure = ProcessWindowMessage;
        subclassId = checked((nuint)Interlocked.Increment(ref nextSubclassId));

        if (!SetWindowSubclass(windowHandle, subclassProcedure, subclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to observe keyboard input for Reactive lighting.");
        }

        try
        {
            Register(windowHandle, RawInputDeviceInputSink);
        }
        catch
        {
            Interlocked.Exchange(ref disposed, 1);
            RetainIfSubclassRemovalFails("registration rollback");
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        RetainIfSubclassRemovalFails("disposal");

        // A removal registration must use a null target. Failure during normal
        // window teardown is harmless because Windows also removes the target.
        try
        {
            Register(0, RawInputDeviceRemove);
        }
        catch (Win32Exception exception)
        {
            Trace.TraceWarning(
                "Reactive keyboard observation cleanup failed: {0}",
                exception.NativeErrorCode);
        }
    }

    private nint ProcessWindowMessage(
        nint hwnd,
        uint message,
        nuint windowParameter,
        nint messageParameter,
        nuint id,
        nuint referenceData)
    {
        try
        {
            if (message == WindowMessageInput && Volatile.Read(ref disposed) == 0)
            {
                ProcessRawInput(messageParameter);
            }
            else if (message == WindowMessageNonClientDestroy)
            {
                Interlocked.Exchange(ref disposed, 1);
                RemoveWindowSubclass(hwnd, subclassProcedure, id);
                FailedRemovalKeepAlive.TryRemove(
                    new SubclassKey(hwnd, id),
                    out _);
            }
        }
        catch (Exception exception)
        {
            // Do not include the packet or key identity in diagnostics.
            Trace.TraceError(
                "Reactive keyboard observation failed: {0}",
                exception.GetType().Name);
        }

        return DefSubclassProc(hwnd, message, windowParameter, messageParameter);
    }

    private void RetainIfSubclassRemovalFails(string operation)
    {
        SubclassKey key = new(windowHandle, subclassId);
        if (RemoveWindowSubclass(windowHandle, subclassProcedure, subclassId))
        {
            FailedRemovalKeepAlive.TryRemove(key, out _);
            return;
        }

        int nativeError = Marshal.GetLastWin32Error();
        FailedRemovalKeepAlive[key] = this;
        Trace.TraceWarning(
            "Reactive keyboard subclass {0} could not detach; retained until window teardown: {1}",
            operation,
            nativeError);
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

    private static void Register(nint target, uint flags)
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
            throw new Win32Exception(Marshal.GetLastWin32Error(),
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

    private readonly record struct SubclassKey(
        nint WindowHandle,
        nuint SubclassId);

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint windowParameter,
        nint messageParameter,
        nuint subclassId,
        nuint referenceData);

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

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint windowParameter,
        nint messageParameter);
}
