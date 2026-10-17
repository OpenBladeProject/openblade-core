using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.SessionAgent;

/// <summary>One outstanding request, owned exclusively by the supervised input worker.</summary>
internal sealed class RazerSnapTapNativeIo : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly Func<bool> isCurrent;

    internal RazerSnapTapNativeIo(string path, Func<bool> isCurrent)
    {
        this.isCurrent = isCurrent;
        if (!isCurrent())
        {
            throw new InvalidOperationException("Keyboard driver admission changed.");
        }
        handle = Native.CreateFile(path, 0xC0000000, 3, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!isCurrent())
        {
            handle.Dispose();
            throw new InvalidOperationException("Keyboard driver admission changed.");
        }
    }

    // Only the typed adapter supplies control codes and record lengths. Timeout of
    // a mutation is ambiguous: callers must latch failure and never replay it.
    internal byte[]? Request(uint code, byte[] input, int outputLength, int timeoutMilliseconds, bool readOnly, Func<bool>? canSubmit = null)
    {
        // Reads stay bound to the already admitted file object. Re-enumerating
        // the entire PnP tree on every notification would stall the input loop.
        // Every mutation repeats full exact-device/controller admission.
        if (!readOnly && !isCurrent())
        {
            throw new InvalidOperationException("Keyboard driver admission changed.");
        }
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        nint inBuffer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        nint outBuffer = Marshal.AllocHGlobal(Math.Max(outputLength, 1));
        nint overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.RequestOverlap>());
        try
        {
            Marshal.Copy(input, 0, inBuffer, input.Length);
            Marshal.StructureToPtr(new Native.RequestOverlap
            {
                Event = completed.SafeWaitHandle.DangerousGetHandle(),
            }, overlapped, false);
            if (canSubmit is not null && !canSubmit())
            {
                throw new InvalidOperationException("Keyboard input ownership changed before submission.");
            }
            bool immediate = Native.DeviceIoControl(handle, code, inBuffer, input.Length,
                outBuffer, outputLength, out _, overlapped);
            if (!immediate && Marshal.GetLastWin32Error() != 997)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            bool timedOut = !immediate && !completed.WaitOne(timeoutMilliseconds);
            if (timedOut)
            {
                Native.CancelIoEx(handle, overlapped);
                if (!completed.WaitOne(1000))
                {
                    // Neither unmanaged storage nor the file may be freed while
                    // the kernel retains this request. The supervisor observes
                    // process death; it never shares this subscribed file object.
                    Environment.FailFast("The keyboard driver request did not cancel.");
                }
            }
            if (!Native.GetOverlappedResult(handle, overlapped, out uint count, false))
            {
                int error = Marshal.GetLastWin32Error();
                if (timedOut && readOnly && error == 995)
                {
                    return null;
                }
                throw new Win32Exception(error);
            }
            if (timedOut && !readOnly)
            {
                throw new IOException("Keyboard driver submission timed out.");
            }
            if (count != outputLength)
            {
                throw new InvalidDataException("Unexpected keyboard driver response length.");
            }
            byte[] result = new byte[outputLength];
            Marshal.Copy(outBuffer, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(outBuffer);
            Marshal.FreeHGlobal(inBuffer);
        }
    }

    public void Dispose() => handle.Dispose();

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RequestOverlap
        {
            internal nuint Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal nint Event;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input,
            int inputLength, nint output, int outputLength, out uint returned, nint overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetOverlappedResult(SafeFileHandle device, nint overlapped,
            out uint returned, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(SafeFileHandle device, nint overlapped);
    }
}
