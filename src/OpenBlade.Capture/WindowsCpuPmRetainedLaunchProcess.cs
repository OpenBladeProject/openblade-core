using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal sealed record CpuPmProcessObservation(int Pid, long Created, CpuPmChildState State, string? Image);
internal interface ICpuPmRetainedProcessProbe : IDisposable { CpuPmProcessObservation Read(bool includeLiveImage); }

// Shared reducer for retained process identity. The terminal path validates the
// same retained kernel object/creation time and held executable pins, using the
// path proven while alive (or the voltage caller's pinned actual launch). It does
// not require a new dead-process image query or use exit code 259 as liveness.
internal sealed class CpuPmRetainedLaunchProcess : ICpuPmRetainedChild
{
    private readonly ICpuPmRetainedProcessProbe probe;
    private readonly ICpuPmLaunchFiles files;
    private bool released;
    public CpuPmAuthorityPeer Identity { get; }
    private CpuPmRetainedLaunchProcess(CpuPmAuthorityPeer identity, ICpuPmRetainedProcessProbe probe, ICpuPmLaunchFiles files)
    { Identity = identity; this.probe = probe; this.files = files; }
    internal static CpuPmRetainedLaunchProcess Retain(ICpuPmRetainedProcessProbe probe, ICpuPmLaunchFiles files, string expectedImage, string expectedHash)
        => RetainCore(probe, files, expectedImage, expectedHash, null);
    // Voltage completion only: the caller pins the reviewed fixed image BEFORE
    // Process.Start and supplies a probe duplicated from that returned SafeHandle.
    // A terminal first observation has launch provenance, never PM live authority.
    internal static CpuPmRetainedLaunchProcess RetainForCompletion(ICpuPmRetainedProcessProbe actuallyLaunchedProbe,
        ICpuPmLaunchFiles files, string expectedImage, string expectedHash, ICpuPmLaunchFilePin prelaunchImage)
        => RetainCore(actuallyLaunchedProbe, files, expectedImage, expectedHash, prelaunchImage ?? throw new ArgumentNullException(nameof(prelaunchImage)));
    private static CpuPmRetainedLaunchProcess RetainCore(ICpuPmRetainedProcessProbe probe, ICpuPmLaunchFiles files,
        string expectedImage, string expectedHash, ICpuPmLaunchFilePin? prelaunchImage)
    {
        try
        {
            files.AssertAllPins(); var observed = probe.Read(true);
            var identity = new CpuPmAuthorityPeer(observed.Pid, observed.Created, expectedImage, expectedHash);
            if (!identity.IsValid || observed.State is not (CpuPmChildState.Running or CpuPmChildState.Terminal)
                || observed.State == CpuPmChildState.Terminal && prelaunchImage is null
                || observed.State == CpuPmChildState.Running && observed.Image != expectedImage
                || observed.Image is not null && observed.Image != expectedImage) { throw new IOException("Launched process binding denied."); }
            var image = files.PinFile(expectedImage, CpuPmLaunchSchema.MaximumFileBytes, true);
            if (image.Sha256 != expectedHash || prelaunchImage is not null && (prelaunchImage.Sha256 != expectedHash || prelaunchImage.Length != image.Length))
            { throw new IOException("Launched image pin denied."); }
            var value = new CpuPmRetainedLaunchProcess(identity, probe, files);
            if (value.ReadFreshIdentity() != identity) { throw new IOException("Launched process changed."); }
            return value;
        }
        catch { probe.Dispose(); throw; }
    }
    public CpuPmAuthorityPeer? ReadFreshIdentity()
    {
        try
        {
            if (released) { return null; }
            files.AssertAllPins();
            var observed = probe.Read(true);
            if (observed.Pid != Identity.ProcessId || observed.Created != Identity.CreationFileTimeUtc
                || observed.State is not (CpuPmChildState.Running or CpuPmChildState.Terminal)
                || observed.State == CpuPmChildState.Running && observed.Image != Identity.ExecutablePath
                || observed.Image is not null && observed.Image != Identity.ExecutablePath) { return null; }
            files.AssertAllPins(); return Identity;
        }
        catch (Exception) { return null; }
    }
    public CpuPmChildState ReadState()
    {
        if (ReadFreshIdentity() is null) { return CpuPmChildState.Unknown; }
        try
        {
            var value = probe.Read(false);
            return value.Pid == Identity.ProcessId && value.Created == Identity.CreationFileTimeUtc ? value.State : CpuPmChildState.Unknown;
        }
        catch (Exception) { return CpuPmChildState.Unknown; }
    }
    internal void ReleaseAfterTerminal()
    {
        if (released) { return; }
        if (ReadState() != CpuPmChildState.Terminal) { throw new InvalidOperationException("Retained child is not terminal."); }
        released = true; probe.Dispose();
        // Shared payload/record pins belong to the enclosing supervisor lifetime.
    }
}

internal sealed class WindowsCpuPmLaunchProcesses : ICpuPmLaunchProcesses
{
    private static CpuPmAuthenticatedChildRun? currentProcessRoot;
    private readonly CpuPmReviewedLaunchBootstrap trusted;
    private readonly ICpuPmLaunchFiles files;
    private WindowsCpuPmProcessProbe? current;
    private WindowsCpuPmPinnedPeer? parent;
    private bool published, disposed;
    internal WindowsCpuPmLaunchProcesses(CpuPmReviewedLaunchBootstrap trusted, ICpuPmLaunchFiles files)
    { this.trusted = trusted; this.files = files; }
    public CpuPmAuthorityPeer ReadCurrentChild()
    {
        if (disposed) { throw new IOException("Current child context closed."); }
        current ??= WindowsCpuPmProcessProbe.Current();
        var observed = current.Read(true); string expected = Path.Combine(trusted.PayloadPath, "OpenBlade.Capture.exe");
        if (observed.Pid != Environment.ProcessId || observed.State != CpuPmChildState.Running || observed.Image != expected) { throw new IOException("Current child identity denied."); }
        var pin = files.PinFile(expected, CpuPmLaunchSchema.MaximumFileBytes, true);
        var identity = new CpuPmAuthorityPeer(observed.Pid, observed.Created, expected, pin.Sha256);
        if (!identity.IsValid) { throw new IOException("Current child identity invalid."); }
        return identity;
    }
    public int ReadCurrentParentProcessId() => WindowsCpuPmProcessProbe.CurrentParentId();
    public void RetainLiveParent(CpuPmAuthorityPeer expected)
    {
        if (disposed || parent is not null) { throw new IOException("Parent binding already consumed."); }
        parent = WindowsCpuPmPinnedPeer.Open(expected);
    }
    public bool IsSameLiveParent(CpuPmAuthorityPeer expected) => !disposed && parent?.Identity == expected && parent.IsCurrent();
    public void RetainToCurrentProcessExit(CpuPmAuthenticatedChildRun run)
    {
        if (disposed || published || Interlocked.CompareExchange(ref currentProcessRoot, run, null) is not null)
        { throw new IOException("Authenticated child lifetime already consumed."); }
        published = true;
    }
    public void Dispose()
    {
        if (published) { throw new InvalidOperationException("Authenticated child pins remain until process exit."); }
        if (disposed) { return; }
        disposed = true; parent?.Dispose(); current?.Dispose();
    }
    // Parent-side binding duplicates the actual handle returned by Process.Start;
    // it never reopens a supplied PID or adopts owner/child JSON as launch proof.
    // Caller must retain its original Process even if this binding fails, because
    // BeginChildLaunch already made child existence possible.
    internal static CpuPmRetainedLaunchProcess RetainActuallyLaunchedChild(Process launched, CpuPmReviewedLaunchBootstrap trusted,
        string reviewedChildHash, ICpuPmLaunchFiles verifiedPayloadPins)
    {
        var probe = WindowsCpuPmProcessProbe.FromLaunchedHandle(launched.SafeHandle);
        return CpuPmRetainedLaunchProcess.Retain(probe, verifiedPayloadPins, Path.Combine(trusted.PayloadPath, "OpenBlade.Capture.exe"), reviewedChildHash);
    }
}

internal sealed class WindowsCpuPmProcessProbe : ICpuPmRetainedProcessProbe
{
    private readonly SafeProcessHandle handle;
    private WindowsCpuPmProcessProbe(SafeProcessHandle handle) => this.handle = handle;
    internal static WindowsCpuPmProcessProbe Current()
    {
        RequireWindowsX64();
        var process = OpenProcess(0x00101000, false, Environment.ProcessId);
        if (process.IsInvalid) { process.Dispose(); throw new IOException("Current process unavailable."); }
        return new(process);
    }
    internal static WindowsCpuPmProcessProbe FromLaunchedHandle(SafeProcessHandle source)
    {
        RequireWindowsX64();
        // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, non-inheritable.
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var copy, 0x00101000, false, 0))
        { copy?.Dispose(); throw new IOException("Launched process handle unavailable."); }
        if (copy.IsInvalid) { copy.Dispose(); throw new IOException("Launched process handle invalid."); }
        return new(copy);
    }
    public CpuPmProcessObservation Read(bool includeLiveImage)
    {
        if (handle.IsClosed || handle.IsInvalid) { throw new IOException("Process pin closed."); }
        uint pid = GetProcessId(handle), wait = WaitForSingleObject(handle, 0);
        if (pid is 0 or > int.MaxValue || !GetProcessTimes(handle, out long created, out _, out _, out _) || created <= 0) { throw new IOException("Process times unavailable."); }
        CpuPmChildState state = wait == 0 ? CpuPmChildState.Terminal : wait == 258 ? CpuPmChildState.Running : CpuPmChildState.Unknown;
        string? image = null;
        if (includeLiveImage && state == CpuPmChildState.Running)
        {
            var path = new StringBuilder(512); uint size = 512;
            bool queried = QueryFullProcessImageNameW(handle, 0, path, ref size) && size is > 0 and < 512;
            uint after = WaitForSingleObject(handle, 0); state = after == 0 ? CpuPmChildState.Terminal : after == 258 ? CpuPmChildState.Running : CpuPmChildState.Unknown;
            if (!queried && state != CpuPmChildState.Terminal) { throw new IOException("Process image unavailable."); }
            if (queried) { image = path.ToString(); }
        }
        return new(checked((int)pid), created, state, image);
    }
    internal static int CurrentParentId()
    {
        RequireWindowsX64();
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) { throw new IOException("Process ancestry unavailable."); }
        var entry = new ProcessEntry { Size = checked((uint)Marshal.SizeOf<ProcessEntry>()) };
        if (!Process32FirstW(snapshot, ref entry)) { throw new IOException("Process ancestry empty."); }
        int count = 0, matches = 0, parent = 0;
        do
        {
            if (++count > 4096) { throw new IOException("Process ancestry bound."); }
            if (entry.ProcessId == Environment.ProcessId)
            { matches++; if (entry.ParentProcessId is 0 or > int.MaxValue) { throw new IOException("Process parent invalid."); } parent = checked((int)entry.ParentProcessId); }
            entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry>());
        } while (Process32NextW(snapshot, ref entry));
        int error = Marshal.GetLastWin32Error();
        if (error != 18 || matches != 1) { throw new IOException("Process ancestry ambiguous."); }
        return parent;
    }
    public void Dispose() => handle.Dispose();
    private static void RequireWindowsX64()
    { if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) { throw new PlatformNotSupportedException(); } }
    // Microsoft PROCESSENTRY32W: DWORDs, ULONG_PTR, LONG, WCHAR[260].
    // https://learn.microsoft.com/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal nuint DefaultHeap;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
        out SafeProcessHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
