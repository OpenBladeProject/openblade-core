using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// No Program caller or native driver proof provider exists. This listener only
// supplies the transport for a future separately approved supervisor.
internal static class WindowsCpuPmAuthorityListener
{
    internal const uint OpenMode = 0x3 | 0x40000000 | 0x00080000;
    internal const uint PipeMode = 0x8;
    internal const string ProtectedAcl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }
    internal static CpuPmAuthorityListenerSession Create(Guid run, Guid lifetime, ICpuPmAuthorityClock clock)
    {
        string name = @"\\.\pipe\" + CpuPmAuthorityProtocol.PipeName(run);
        if (lifetime == Guid.Empty) { throw new ArgumentException("Lifetime required."); }
        ArgumentNullException.ThrowIfNull(clock);
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        IntPtr descriptor = IntPtr.Zero;
        SafePipeHandle? handle = null;
        NamedPipeServerStream? stream = null;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ProtectedAcl, 1, out descriptor, out _))
            { throw new IOException("PM pipe ACL unavailable."); }
            var attributes = new SecurityAttributes { Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()), Descriptor = descriptor, InheritHandle = 0 };
            handle = CreateNamedPipe(name, OpenMode, PipeMode, 1, 4100, 4100, 2000, ref attributes);
            if (handle.IsInvalid) { throw new IOException("PM pipe listener unavailable."); }
            stream = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            handle = null; // the stream now owns the SafePipeHandle
            var transport = new Transport(stream);
            var session = new CpuPmAuthorityListenerSession(run, lifetime, transport, clock);
            stream = null; // ownership transfers to the single-use session
            return session;
        }
        catch { stream?.Dispose(); handle?.Dispose(); throw; }
        finally { if (descriptor != IntPtr.Zero) { _ = LocalFree(descriptor); } }
    }
    private sealed class Transport(NamedPipeServerStream stream) : ICpuPmAuthorityListenerTransport
    {
        private bool transferred;
        private int disposed;
        public Stream Stream => stream;
        public Task WaitForConnectionAsync(CancellationToken token) => stream.WaitForConnectionAsync(token);
        public ICpuPmAuthorityChannel BindRetainedChild(CpuPmAuthorityPeer expectedChild)
        {
            if (transferred || Volatile.Read(ref disposed) != 0) { throw new InvalidOperationException("Pipe already bound."); }
            var channel = WindowsCpuPmAuthorityChannel.BindConnectedParent(stream, expectedChild);
            transferred = true; // channel owns stream and retained executable/process pins
            return channel;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && !transferred) { stream.Dispose(); }
        }
    }

    // BOOL/DWORD/HANDLE ABI and security layout verified against Microsoft:
    // https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew
    // https://learn.microsoft.com/previous-versions/windows/desktop/legacy/aa379560(v=vs.85)
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances,
        uint outputBuffer, uint inputBuffer, uint defaultTimeout, ref SecurityAttributes security);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr allocation);
}
