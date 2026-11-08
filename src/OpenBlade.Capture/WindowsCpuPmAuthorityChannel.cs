using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

// This file has no callers in Program. Expected peer data must come from the
// future trusted launcher/retained child, never a message sent over this pipe.
internal sealed class WindowsCpuPmAuthorityChannel : ICpuPmAuthorityChannel
{
    private readonly PipeStream pipe;
    private readonly WindowsCpuPmPinnedPeer peer;
    private readonly bool serverEnd;
    private int disposed;
    private WindowsCpuPmAuthorityChannel(PipeStream pipe, WindowsCpuPmPinnedPeer peer, bool serverEnd)
    { this.pipe = pipe; this.peer = peer; this.serverEnd = serverEnd; }

    internal static async Task<WindowsCpuPmAuthorityChannel> ConnectChildAsync(Guid run, CpuPmAuthorityPeer expectedParent, CancellationToken token)
    {
        string name = CpuPmAuthorityProtocol.PipeName(run);
        token.ThrowIfCancellationRequested();
        WindowsCpuPmPinnedPeer? retained = null;
        NamedPipeClientStream? pipe = null;
        try
        {
            retained = WindowsCpuPmPinnedPeer.Open(expectedParent);
            token.ThrowIfCancellationRequested();
            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(2000, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = new WindowsCpuPmAuthorityChannel(pipe, retained, false);
            if (result.ReadFreshPeer() != expectedParent) { throw new IOException("PM authority peer denied."); }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch { pipe?.Dispose(); retained?.Dispose(); throw; }
    }

    // A future supervisor must create a local-only, administrator/SYSTEM ACL,
    // single-instance first-instance pipe and bind its retained launched child.
    // This method neither creates a listener nor establishes driver ownership.
    internal static WindowsCpuPmAuthorityChannel BindConnectedParent(NamedPipeServerStream pipe, CpuPmAuthorityPeer expectedChild)
    {
        var retained = WindowsCpuPmPinnedPeer.Open(expectedChild);
        try
        {
            var result = new WindowsCpuPmAuthorityChannel(pipe, retained, true);
            if (result.ReadFreshPeer() != expectedChild) { throw new IOException("PM authority peer denied."); }
            return result;
        }
        catch { retained.Dispose(); throw; }
    }
    public CpuPmAuthorityPeer? ReadFreshPeer()
    {
        if (Volatile.Read(ref disposed) != 0 || !pipe.IsConnected) { return null; }
        bool found = serverEnd ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        return found && pid == peer.Identity.ProcessId && peer.IsCurrent() ? peer.Identity : null;
    }
    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken token)
    {
        if (serverEnd || ReadFreshPeer() is null) { throw new IOException("PM authority channel denied."); }
        await WriteFrameAsync(pipe, request, token).ConfigureAwait(false);
        byte[] reply = await ReadFrameAsync(pipe, token).ConfigureAwait(false);
        if (ReadFreshPeer() is null) { throw new IOException("PM authority peer changed."); }
        return reply;
    }
    internal static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is 0 or > CpuPmAuthorityProtocol.MaximumFrameBytes) { throw new IOException("PM authority frame bound."); }
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > CpuPmAuthorityProtocol.MaximumFrameBytes) { throw new IOException("PM authority frame bound."); }
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        try { pipe.Dispose(); } finally { peer.Dispose(); }
    }

    // Microsoft Win32 API: BOOL (HANDLE, PULONG); failure never supplies identity.
    // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid
    // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

internal sealed class WindowsCpuPmPinnedPeer : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly List<SafeFileHandle> directories;
    private readonly FileStream executable;
    internal CpuPmAuthorityPeer Identity { get; }
    private WindowsCpuPmPinnedPeer(CpuPmAuthorityPeer identity, SafeProcessHandle process, List<SafeFileHandle> directories, FileStream executable)
    { Identity = identity; this.process = process; this.directories = directories; this.executable = executable; }
    internal static WindowsCpuPmPinnedPeer Open(CpuPmAuthorityPeer expected)
    {
        if (!OperatingSystem.IsWindows() || !expected.IsValid || expected.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(expected.ExecutablePath), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Invalid PM peer pin."); }
        var process = OpenProcess(0x00101000, false, expected.ProcessId); // synchronize + limited query
        var directories = new List<SafeFileHandle>(); FileStream? file = null;
        try
        {
            if (process.IsInvalid) { throw new IOException("PM peer unavailable."); }
            var ancestors = new Stack<string>();
            for (string? path = Path.GetDirectoryName(expected.ExecutablePath); path is not null; path = Path.GetDirectoryName(path)) { ancestors.Push(path); }
            while (ancestors.Count > 0) { directories.Add(Pin(ancestors.Pop(), true)); }
            var handle = Pin(expected.ExecutablePath, false);
            try { file = new FileStream(handle, FileAccess.Read); } catch { handle.Dispose(); throw; }
            if (Convert.ToHexString(SHA256.HashData(file)) != expected.ExecutableSha256) { throw new IOException("PM peer executable changed."); }
            var result = new WindowsCpuPmPinnedPeer(expected, process, directories, file);
            if (!result.IsCurrent()) { throw new IOException("PM peer process changed."); }
            return result;
        }
        catch { file?.Dispose(); foreach (var directory in directories) { directory.Dispose(); } process.Dispose(); throw; }
    }
    internal bool IsCurrent()
    {
        if (process.IsClosed || process.IsInvalid || executable.SafeFileHandle.IsClosed
            || directories.Any(d => d.IsClosed || d.IsInvalid) || WaitForSingleObject(process, 0) != 258
            || !GetProcessTimes(process, out long created, out _, out _, out _) || created != Identity.CreationFileTimeUtc) { return false; }
        var path = new StringBuilder(32768); int size = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size)
            && string.Equals(path.ToString(), Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && FinalPath(executable.SafeFileHandle, Identity.ExecutablePath);
    }
    private static SafeFileHandle Pin(string path, bool directory)
    {
        var handle = CreateFile(path, directory ? 0x80u : 0x80000000u, 1, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0
                || ((info.Attributes & 0x10) != 0) != directory || !FinalPath(handle, path)) { throw new IOException("PM peer path pin denied."); }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    private static bool FinalPath(SafeFileHandle handle, string expected)
    {
        var result = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        return length is > 0 and < 32768 && string.Equals(result.ToString(), @"\\?\" + expected, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { executable.Dispose(); foreach (var directory in directories) { directory.Dispose(); } process.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
