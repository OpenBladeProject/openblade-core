using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// A single attended query, separately authorized from passive installed-backend
// opt-in. No production setting, IPC request or UI action creates this approval.
internal sealed class CpuQueryAttendedApproval
{
    private int consumed;
    private readonly long created = Stopwatch.GetTimestamp();
    private CpuQueryAttendedApproval() { }
    internal static CpuQueryAttendedApproval AcknowledgePciTransportWrites(bool approved) =>
        approved ? new() : throw new ArgumentException("Attended PCI transport writes require approval.", nameof(approved));
    internal bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0
        && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2);
}

internal readonly record struct CpuQueryProcessorIdentity(string Vendor, int Family, int Model, int Stepping);
internal enum CpuQueryMutexWait { Acquired, Abandoned, Timeout }
internal interface ICpuQueryMutex : IDisposable
{
    CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken);
    void Release();
}
internal interface ICpuQueryDevice : IDisposable
{
    CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength);
}
internal interface ICpuQueryWindowsPlatform
{
    HostIdentityReadResult ReadHost();
    CpuQueryProcessorIdentity ReadProcessor();
    bool ControllersAbsent();
    IDisposable PinBackendFiles();
    InstalledCpuBackendSnapshot ReadBackend();
    ICpuQueryDevice OpenExistingDevice();
    ICpuQueryMutex CreateSharedMutex();
}

/// <summary>
/// Separately attended validation only. This uses PCI transport writes even for
/// getters. It never starts/provisions a driver or loads an AMD SDK DLL. Production
/// read admission remains denied until the native path has been validated.
/// </summary>
internal sealed partial class WindowsRz090528CpuQueryAdapter : IRz090528CpuQueryIoctl, IRz090528CpuQuerySynchronization
{
    private readonly ICpuQueryWindowsPlatform platform;
    private readonly Func<DeviceIdentity?> readFreshUsbIdentity;
    private readonly CpuQueryWireAdmission wire = new();
    private ICpuQueryDevice? device;
    private int ownerThread;
    private CpuQueryStage stage = CpuQueryStage.InitialUsbIdentity;

    private WindowsRz090528CpuQueryAdapter(ICpuQueryWindowsPlatform platform, Func<DeviceIdentity?> readFreshUsbIdentity)
    {
        this.platform = platform;
        this.readFreshUsbIdentity = readFreshUsbIdentity;
    }

    // An installed-backend checkbox is passive inspection permission only.
    internal static Rz090528CpuLiveQueryResult QueryProduction(bool useInstalledBackend) =>
        new(CpuLiveQueryStatus.Unavailable, null);

    internal static Rz090528CpuLiveQueryResult QueryForAttendedValidation(
        CpuQueryAttendedApproval? approval, Func<DeviceIdentity?> readFreshUsbIdentity,
        CancellationToken cancellationToken = default) =>
        QueryForAttendedValidation(approval, readFreshUsbIdentity, new CpuQueryWindowsPlatform(), cancellationToken);

    internal static Rz090528CpuLiveQueryResult QueryForAttendedValidation(
        CpuQueryAttendedApproval? approval, Func<DeviceIdentity?> readFreshUsbIdentity,
        ICpuQueryWindowsPlatform platform, CancellationToken cancellationToken = default)
    {
        if (approval?.Consume() != true)
        {
            return new(CpuLiveQueryStatus.Unavailable, null,
                new(CpuQueryStage.Approval, CpuQueryFailureCode.AdmissionDenied));
        }
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(readFreshUsbIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = new WindowsRz090528CpuQueryAdapter(platform, readFreshUsbIdentity);
        try
        {
            var identity = readFreshUsbIdentity();
            if (!ExactUsb(identity))
            {
                return new(CpuLiveQueryStatus.UnsupportedIdentity, null,
                    new(adapter.stage, CpuQueryFailureCode.AdmissionDenied));
            }
            return new Rz090528CpuLiveQuery(adapter, adapter).Query(identity!, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            return new(CpuLiveQueryStatus.Unavailable, null,
                exception is QueryAdmissionDeniedException
                    ? new(adapter.stage, CpuQueryFailureCode.AdmissionDenied)
                    : CpuQueryDiagnostic.FromException(adapter.stage, exception));
        }
    }

    private static bool ExactUsb(DeviceIdentity? identity) => identity is
    { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 };

    private bool FreshAdmission()
    {
        stage = CpuQueryStage.HostIdentity;
        var host = platform.ReadHost();
        if (host.Failure is not null || host.Evidence is not
            { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4" }) { return false; }
        stage = CpuQueryStage.ProcessorIdentity;
        var cpu = platform.ReadProcessor();
        if (cpu is not { Vendor: "AuthenticAMD", Family: 0x1A, Model: 0x24, Stepping: 0 }) { return false; }
        stage = CpuQueryStage.FreshUsbIdentity;
        if (!ExactUsb(readFreshUsbIdentity())) { return false; }
        stage = CpuQueryStage.ControllerPresence;
        if (!platform.ControllersAbsent()) { return false; }
        stage = CpuQueryStage.InstalledBackend;
        return Rz090528InstalledCpuBackendInspector.Evaluate(platform.ReadBackend()).Availability
            == CpuTuningBackendAvailability.Available;
    }

    public IDisposable? Acquire(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout != TimeSpan.FromSeconds(20) || ownerThread != 0)
        { throw new InvalidOperationException("Invalid CPU query lease."); }
        stage = CpuQueryStage.MutexCreate;
        var mutex = platform.CreateSharedMutex();
        bool acquired = false;
        IDisposable? files = null;
        try
        {
            stage = CpuQueryStage.MutexWait;
            var wait = mutex.Wait(timeout, cancellationToken);
            if (wait == CpuQueryMutexWait.Timeout) { return null; }
            acquired = true;
            // Pin directory names and all inspected files for the entire query.
            // Revalidate after waiting, including an abandoned mutex. The engine
            // always rediscovers PCI topology, masks and mailbox state from scratch.
            stage = CpuQueryStage.BackendFilePins;
            files = platform.PinBackendFiles();
            if (!FreshAdmission()) { throw new QueryAdmissionDeniedException(); }
            cancellationToken.ThrowIfCancellationRequested();
            stage = CpuQueryStage.DeviceOpen;
            device = platform.OpenExistingDevice();
            // Detect a service/configuration transition across CreateFile before
            // issuing any IOCTL. A userspace preflight cannot attest the bytes of
            // an already loaded kernel image; that is an attended validation prerequisite.
            if (!FreshAdmission()) { throw new QueryAdmissionDeniedException(); }
            cancellationToken.ThrowIfCancellationRequested();
            ownerThread = Environment.CurrentManagedThreadId;
            var lease = new QueryLease(this, mutex, files, ownerThread);
            files = null;
            acquired = false;
            mutex = null!;
            return lease;
        }
        finally
        {
            try
            {
                if (ownerThread == 0) { try { Cleanup(() => device?.Dispose()); } finally { device = null; } }
            }
            finally
            {
                try { Cleanup(() => files?.Dispose()); }
                finally
                {
                    try { if (acquired) { Cleanup(mutex.Release); } }
                    finally { Cleanup(() => mutex?.Dispose()); }
                }
            }
        }
    }

    private void Cleanup(Action release)
    {
        // Successful cleanup must preserve the admission stage that failed.
        // A cleanup exception has its own stage even before a lease exists.
        try { release(); }
        catch { stage = CpuQueryStage.Cleanup; throw; }
    }

    public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
    {
        CheckThread();
        if (device is null || !wire.Allows(code, input, outputLength))
        { return new(false, [], CpuQueryFailureCode.WireRequestRejected); }
        var reply = device.Invoke(code, input, outputLength);
        if (wire.AcceptReply(code, input, outputLength, reply)) { return reply; }
        return !reply.Success ? reply
            : new(false, [], reply.Bytes.Length != outputLength
                ? CpuQueryFailureCode.ReplyLengthMismatch : CpuQueryFailureCode.WireReplyRejected);
    }

    public void Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        CheckThread();
        if (duration != TimeSpan.FromMilliseconds(1)) { throw new InvalidOperationException("Unbounded CPU query delay."); }
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.WaitHandle.WaitOne(duration)) { cancellationToken.ThrowIfCancellationRequested(); }
    }

    private void CheckThread()
    {
        if (ownerThread == 0 || ownerThread != Environment.CurrentManagedThreadId)
        { throw new InvalidOperationException("CPU query mutex belongs to its synchronous query thread."); }
    }

    private sealed class QueryLease(WindowsRz090528CpuQueryAdapter adapter, ICpuQueryMutex mutex,
        IDisposable files, int thread) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) { return; }
            if (Environment.CurrentManagedThreadId != thread)
            { throw new InvalidOperationException("CPU query lease must be released on its owning thread."); }
            disposed = true;
            adapter.stage = CpuQueryStage.Cleanup;
            try { adapter.device?.Dispose(); }
            finally
            {
                adapter.device = null;
                adapter.ownerThread = 0;
                try { files.Dispose(); }
                finally { try { mutex.Release(); } finally { mutex.Dispose(); } }
            }
        }
    }

    private sealed class QueryAdmissionDeniedException : IOException;
}

// Defense in depth at the native boundary: exact schemas, discovered D0F0 bus,
// fixed index addresses, bounded config reads and only 06/5E mailbox commands.
// No arbitrary PCI write, SMN address, PM-cache F08, refresh, MSR or tuning setter.
internal sealed class CpuQueryWireAdmission
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Fuse = 0x03820AB0, Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private uint count, selected, argument;
    private uint? bus;
    private bool enumerated, failed, responseReady, cleared, argumentWritten;
    private bool rootConfirmed, nodeConfirmed, orientationConfirmed;

    internal bool Allows(uint code, byte[] input, int outputLength)
    {
        bool allowed = !failed && AllowsCore(code, input, outputLength);
        if (!allowed) { failed = true; }
        return allowed;
    }

    private bool AllowsCore(uint code, byte[] input, int outputLength)
    {
        if (code == Enumerate)
        {
            if (enumerated || input.Length < 28 || outputLength != input.Length
                || input.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0) { return false; }
            return count == 0 ? input.Length == 28 && Word(input, 24) == 0
                : Word(input, 24) == count && input.Length == 28 + count * 52
                    && input.AsSpan(28).IndexOfAnyExcept((byte)0) < 0;
        }
        if (!bus.HasValue) { return false; }
        if (code == Read)
        {
            if (input.Length != 20 || outputLength != 24 || Word(input, 16) != 4) { return false; }
            uint b = Word(input, 0), d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
            return b == 0 && d == 0x18 && f == 1 && offset is 0 or 0x140
                || b == bus && d == 0x18 && f == 0 && offset == 0
                || b == bus && d == 0 && f == 0 && (offset == 0
                    || offset == 0xC8 && selected is Fuse or Response or Argument);
        }
        if (code != Write || input.Length != 12 || outputLength != 0 || Word(input, 8) != bus
            || !rootConfirmed || !nodeConfirmed || !orientationConfirmed) { return false; }
        uint index = Word(input, 0), value = Word(input, 4);
        if (index == 0) { return value is Fuse or Response or Argument or Message; }
        if (index != 1) { return false; }
        return selected switch
        {
            Response => value == 0 && responseReady,
            Argument => cleared && IsCoreArgument(value),
            Message => cleared && argumentWritten && (value == 6 && argument == 0 || value == 0x5E),
            _ => false,
        };
    }

    private static bool IsCoreArgument(uint value) =>
        (value & 0xEF8FFFFF) == 0; // bits 28 (CCD 0..1) and 20..22 (core 0..7) only.

    internal bool AcceptReply(uint code, byte[] input, int outputLength, CpuQueryIoctlReply reply)
    {
        if (failed || !reply.Success || reply.Bytes.Length != outputLength) { failed = true; return false; }
        if (code == Enumerate)
        {
            uint received = Word(reply.Bytes, 24);
            if (reply.Bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0 || received == 0 || received > 65536)
            { failed = true; return false; }
            if (count == 0) { count = received; return true; }
            if (received != count) { failed = true; return false; }
            if (!CpuQueryPciTopology.TryFindBus(reply.Bytes, out uint found)) { failed = true; return false; }
            enumerated = true;
            bus = found;
        }
        else if (code == Read)
        {
            if (!reply.Bytes.AsSpan(0, 20).SequenceEqual(input)) { failed = true; return false; }
            if (Word(input, 12) == 0)
            {
                uint d = Word(input, 4), f = Word(input, 8), id = Word(reply.Bytes, 20);
                if (d == 0) { rootConfirmed = id == CpuQueryPciTopology.RootId; }
                else if (f == 0) { nodeConfirmed = id == CpuQueryPciTopology.NodeId; }
                else { orientationConfirmed = id == CpuQueryPciTopology.OrientationId; }
                if (!(d == 0 ? rootConfirmed : f == 0 ? nodeConfirmed : orientationConfirmed))
                { failed = true; return false; }
            }
            if (Word(input, 12) == 0xC8 && selected == Response)
            { responseReady = Word(reply.Bytes, 20) == 1; }
        }
        else if (code == Write)
        {
            uint value = Word(input, 4);
            if (Word(input, 0) == 0) { selected = value; }
            else if (selected == Response) { cleared = true; responseReady = false; argumentWritten = false; }
            else if (selected == Argument) { argument = value; argumentWritten = true; }
            else if (selected == Message) { cleared = false; argumentWritten = false; }
        }
        return true;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    // The actual native implementation is private: other internal callers cannot
    // obtain its raw device interface and bypass attended admission or wire checks.
    private sealed class CpuQueryWindowsPlatform : ICpuQueryWindowsPlatform
    {
        public HostIdentityReadResult ReadHost()
        {
            var registry = new WindowsRegistryValueSource();
            return new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
                WindowsHostIdentitySource.Rz090528Identity).ReadExactIdentity();
        }

        public CpuQueryProcessorIdentity ReadProcessor()
        {
            if (!X86Base.IsSupported) { return default; }
            var vendor = X86Base.CpuId(0, 0);
            if (vendor.Eax < 1 || vendor.Ebx != 0x68747541 || vendor.Edx != 0x69746E65 || vendor.Ecx != 0x444D4163)
            { return default; }
            uint eax = unchecked((uint)X86Base.CpuId(1, 0).Eax);
            int family = (int)((eax >> 8) & 15), model = (int)((eax >> 4) & 15);
            if (family is 6 or 15) { model |= (int)((eax >> 12) & 0xF0); }
            if (family == 15) { family += (int)((eax >> 20) & 255); }
            return new("AuthenticAMD", family, model, (int)(eax & 15));
        }

        public bool ControllersAbsent() => Rz090528CpuControllerPresence.ControllersAbsent();

        public InstalledCpuBackendSnapshot ReadBackend() => new WindowsInstalledCpuBackendSnapshotSource().Read();
        public ICpuQueryDevice OpenExistingDevice() => new NativeDevice();
        public ICpuQueryMutex CreateSharedMutex() => new SharedMutex();
        public IDisposable PinBackendFiles() => new BackendFilePins();

        private sealed class BackendFilePins : IDisposable
        {
            private readonly List<IDisposable> pins = [];
            internal BackendFilePins()
            {
                try
                {
                    var parents = new Stack<string>();
                    for (var directory = new DirectoryInfo(Rz090528InstalledCpuBackendInspector.DirectoryPath);
                        directory is not null; directory = directory.Parent) { parents.Push(directory.FullName); }
                    while (parents.TryPop(out string? path))
                    {
                        // Deny directory renames while resolving/hashing the fixed paths.
                        var handle = CreateFileW(path, 0, 3, 0, 3, 0x02200000, 0);
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            handle.Dispose();
                            throw new Win32Exception(error);
                        }
                        pins.Add(handle);
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        { throw new IOException("CPU backend parent is redirected."); }
                    }
                    foreach (string path in Rz090528InstalledCpuBackendInspector.ExpectedHashes.Keys)
                    {
                        // Open the file itself (never follow a reparse point), deny
                        // writes/deletion, then inspect its now-stable path. This
                        // also closes the check/open race with symlink replacement.
                        var handle = CreateFileW(path, 0x80000000, 1, 0, 3, 0x00200000, 0);
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            handle.Dispose();
                            throw new Win32Exception(error);
                        }
                        pins.Add(handle);
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        { throw new IOException("CPU backend file is redirected."); }
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                for (int i = pins.Count - 1; i >= 0; i--) { pins[i].Dispose(); }
                pins.Clear();
            }
        }

        private sealed class SharedMutex : ICpuQueryMutex
        {
            private readonly Mutex mutex = new(false, @"Global\AMDRyzenMasterMutex");
            public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = timeout - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero) { break; }
                    try
                    {
                        if (mutex.WaitOne(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50)))
                        { return CpuQueryMutexWait.Acquired; }
                    }
                    catch (AbandonedMutexException) { return CpuQueryMutexWait.Abandoned; }
                }
                return CpuQueryMutexWait.Timeout;
            }
            public void Release() => mutex.ReleaseMutex();
            public void Dispose() => mutex.Dispose();
        }

        private sealed class NativeDevice : ICpuQueryDevice
        {
            private readonly SafeFileHandle handle;
            internal NativeDevice()
            {
                handle = CreateFileW(@"\\.\AMDRyzenMasterDriverV31", 0xC0000000, 3, 0, 3, 0, 0);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }
            }
            public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
            {
                var output = new byte[outputLength];
                bool success = DeviceIoControl(handle, code, input, input.Length, output, output.Length, out int returned, 0);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                if (!success) { return new(false, [], CpuQueryFailureCode.NativeIoctlFailed, error > 0 ? error : null); }
                return returned == outputLength ? new(true, output)
                    : new(false, [], CpuQueryFailureCode.ReplyLengthMismatch);
            }
            public void Dispose() => handle.Dispose();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
            uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
            byte[] output, int outputLength, out int returned, nint overlapped);
    }
}
