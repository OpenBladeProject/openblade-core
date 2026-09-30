using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using System.ServiceProcess;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Distinct from the original 06/5E query approval. Both permissions and the
// lifecycle owner's exact run/lifetime are required; nothing provisions a driver.
internal sealed class CpuPmAttendedApproval
{
    private readonly long created = Stopwatch.GetTimestamp();
    private int consumed;
    internal CpuPmRunOwnedDriverLifetime Lifetime { get; }
    private CpuPmAttendedApproval(CpuPmRunOwnedDriverLifetime lifetime) => Lifetime = lifetime;
    internal static CpuPmAttendedApproval Acknowledge(bool pciTransportAndKernelCopyApproved,
        bool freshRunOwnedDriverLifetimeApproved, CpuPmRunOwnedDriverLifetime lifetime)
    {
        if (!pciTransportAndKernelCopyApproved || !freshRunOwnedDriverLifetimeApproved
            || lifetime is not { ConfirmedFreshRunOwned: true } || lifetime.RunId == Guid.Empty || lifetime.DriverLifetimeId == Guid.Empty)
        { throw new ArgumentException("Both scope-specific PM approvals and a run-owned lifetime are required."); }
        return new(lifetime);
    }
    internal bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0 && Stopwatch.GetElapsedTime(created) < TimeSpan.FromMinutes(2);
}

internal readonly record struct CpuPmProcessorPackage(uint MaximumExtendedLeaf, int Package);
internal sealed record CpuPmServiceSnapshot(bool Exists, string? ImagePath, int? Type, int? Start, bool Running);
internal interface ICpuPmContinuityScope : IDisposable
{
    CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? lifetime);
}
internal interface ICpuPmWindowsPlatform : ICpuQueryWindowsPlatform
{
    DeviceIdentity? ReadUsb();
    CpuPmProcessorPackage ReadPackage();
    CpuPmServiceSnapshot ReadService();
    ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime);
}

internal static class WindowsRz090528CpuPmObservationAdapter
{
    internal static CpuPmObservationBatch ObserveProduction(bool useInstalledBackend) => Denied();
    internal static CpuPmObservationBatch ObserveForAttendedValidation(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshDriverLifetime, CancellationToken token = default) =>
        WindowsRz090528CpuQueryAdapter.ObservePmNative(approval, readFreshDriverLifetime, token);
    internal static CpuPmObservationBatch ObserveForAttendedValidation(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshDriverLifetime, ICpuPmWindowsPlatform platform,
        ICpuPmObservationClock clock, CancellationToken token = default) =>
        WindowsRz090528CpuQueryAdapter.ObservePm(approval, readFreshDriverLifetime, platform, clock, token);
    internal static CpuPmObservationBatch Denied(CpuPmObservationStage stage = CpuPmObservationStage.Admission,
        CpuPmObservationFailure failure = CpuPmObservationFailure.AdmissionDenied) => new(false, [], [], 0, 0, new(stage, failure));
}

// Reuse the private native platform and the actual same-thread query lease.
// This partial does not expose raw device access or alter the original factories.
internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    internal static CpuPmObservationBatch ObservePmNative(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, CancellationToken token) =>
        ObservePm(approval, lifetime, new PmNativePlatform(new CpuQueryWindowsPlatform()), new PmClock(), token);

    internal static CpuPmObservationBatch ObservePm(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, ICpuPmWindowsPlatform platform, ICpuPmObservationClock clock,
        CancellationToken token)
    {
        if (approval?.Consume() != true) { return WindowsRz090528CpuPmObservationAdapter.Denied(); }
        ArgumentNullException.ThrowIfNull(lifetime); ArgumentNullException.ThrowIfNull(platform); ArgumentNullException.ThrowIfNull(clock);
        token.ThrowIfCancellationRequested();
        ICpuPmContinuityScope? continuity = null;
        CpuPmObservationBatch result = WindowsRz090528CpuPmObservationAdapter.Denied();
        try
        {
            if (lifetime() != approval.Lifetime) { return result; }
            // Arm before even constructing/acquiring a native mutex or device.
            continuity = platform.ArmContinuity(approval.Lifetime);
            var adapter = new PmAdapter(platform, continuity, approval.Lifetime, lifetime, clock);
            adapter.FullAdmission();
            token.ThrowIfCancellationRequested();
            result = adapter.PreserveRejectedCap(new Rz090528CpuPmObservationEngine(adapter.Transport, adapter, adapter, clock).Observe(token));
            if (continuity.ReadFresh(lifetime()).Continuity != CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime)
            { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied); }
        }
        catch (OperationCanceledException) { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.Cancelled); }
        catch (Exception e) when (PmBoundedFailure(e))
        { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied); }
        finally
        {
            if (continuity is not null)
            {
                try
                {
                    continuity.Dispose();
                    CpuPmContinuityFailure cleanup = continuity.ReadFresh(approval.Lifetime).Failure;
                    if (cleanup == CpuPmContinuityFailure.UnregisterFailed)
                    { result = Incomplete(result, CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed); }
                    else if (result.Complete && cleanup != CpuPmContinuityFailure.Disposed)
                    { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied); }
                }
                catch (Exception e) when (PmBoundedFailure(e))
                { result = Incomplete(result, CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed); }
            }
        }
        return result;
    }
    private static CpuPmObservationBatch Incomplete(CpuPmObservationBatch result, CpuPmObservationStage stage, CpuPmObservationFailure failure) =>
        new(false, result.CopySamples(), result.CopyPciRecords(), result.OrientationMask, result.FuseMask, new(stage, failure), result.FusedCap);
    private static bool PmBoundedFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or InvalidOperationException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException;

    private sealed class PmAdapter : ICpuQueryWindowsPlatform, IRz090528CpuQuerySynchronization, ICpuPmObservationSessionAdmission
    {
        private readonly ICpuPmWindowsPlatform platform;
        private readonly ICpuPmContinuityScope continuity;
        private readonly CpuPmRunOwnedDriverLifetime lifetime;
        private readonly Func<CpuPmRunOwnedDriverLifetime?> readLifetime;
        private readonly int thread = Environment.CurrentManagedThreadId;
        private HostIdentityReadResult? host;
        private CpuQueryProcessorIdentity processor;
        private CpuPmProcessorPackage package;
        private DeviceIdentity? usb;
        private IReadOnlyList<CpuBackendFileMetadata>? files;
        private bool pinned, leased, abandoned;
        private CpuPmCapRegisterRead? rejectedCap;
        internal WindowsRz090528CpuQueryAdapter Transport { get; }
        internal PmAdapter(ICpuPmWindowsPlatform platform, ICpuPmContinuityScope continuity,
            CpuPmRunOwnedDriverLifetime lifetime, Func<CpuPmRunOwnedDriverLifetime?> readLifetime, ICpuPmObservationClock clock)
        {
            this.platform = platform; this.continuity = continuity; this.lifetime = lifetime; this.readLifetime = readLifetime;
            Transport = new(this, ReadUsb, new PmNativeWire((address, reply) =>
                rejectedCap = new(address, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reply.Bytes.AsSpan(20)),
                    clock.UtcNow, clock.MonotonicTimestamp, reply.Success, false)));
        }
        internal CpuPmObservationBatch PreserveRejectedCap(CpuPmObservationBatch result)
        {
            // The shared query adapter intentionally strips rejected reply bytes.
            // Retain that one cap word as evidence only; its native guard stays
            // poisoned and grants no following request.
            if (rejectedCap is not { } rejected) { return result; }
            var first = result.FusedCap?.A0F; var second = result.FusedCap?.A0B;
            if (rejected.Address == CpuLivePowerFusedCap.RegisterA0F) { first = rejected; } else { second = rejected; }
            return new(false, result.CopySamples(), result.CopyPciRecords(), result.OrientationMask, result.FuseMask,
                result.Diagnostic, new(first, second));
        }
        private void CheckThread()
        { if (Environment.CurrentManagedThreadId != thread) { throw new IOException("PM scope thread changed."); } }
        private CpuPmContinuityEvidence CheckContinuity()
        {
            CheckThread();
            CpuPmRunOwnedDriverLifetime? fresh = readLifetime();
            var evidence = continuity.ReadFresh(fresh);
            if (fresh != lifetime || evidence.Continuity != CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime
                || evidence.SessionId == Guid.Empty || evidence.DriverLifetimeId != lifetime.DriverLifetimeId)
            { throw new IOException("PM lifetime continuity is unavailable."); }
            return evidence;
        }
        internal void FullAdmission()
        {
            _ = ReadHost(); _ = ReadProcessor(); _ = ReadUsb();
            if (!ControllersAbsent()) { throw new IOException("PM controller admission denied."); }
            _ = ReadBackend(); _ = ReadFresh();
        }
        public bool ValidateSnapshotBoundary()
        {
            CheckThread(); if (!leased || !pinned) { return false; }
            FullAdmission(); return true;
        }
        public CpuPmSessionEvidence ReadFresh()
        {
            var current = CheckContinuity();
            if (host is null || usb is null || files is null || leased && !pinned) { throw new IOException("PM provenance unavailable."); }
            CpuPmServiceSnapshot service = platform.ReadService();
            var backend = new InstalledCpuBackendSnapshot(files, service.Exists, service.ImagePath, service.Type, service.Start, service.Running);
            if (service.Start != 3 || Rz090528InstalledCpuBackendInspector.Evaluate(backend).Availability != CpuTuningBackendAvailability.Available)
            { throw new IOException("PM driver state changed."); }
            bool controllers = platform.ControllersAbsent();
            current = CheckContinuity();
            return new(usb, host.Value, processor, package.MaximumExtendedLeaf, package.Package, backend, controllers,
                current.Continuity, current.SessionId, current.DriverLifetimeId);
        }
        public HostIdentityReadResult ReadHost()
        {
            _ = CheckContinuity(); var value = platform.ReadHost();
            if (value.Failure is not null || value.Evidence is not
                { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4", BiosVersion: "2.02" })
            { throw new IOException("PM host admission denied."); }
            host = value;
            return value;
        }
        public CpuQueryProcessorIdentity ReadProcessor()
        {
            processor = platform.ReadProcessor(); package = platform.ReadPackage();
            if (processor is not { Vendor: "AuthenticAMD", Family: 0x1A, Model: 0x24, Stepping: 0 }
                || package.MaximumExtendedLeaf < 0x80000001 || package.Package != 1)
            { throw new IOException("PM package admission denied."); }
            return processor;
        }
        private DeviceIdentity? ReadUsb()
        {
            usb = platform.ReadUsb(); if (!ExactUsb(usb)) { throw new IOException("PM USB admission denied."); }
            return usb;
        }
        public bool ControllersAbsent() => platform.ControllersAbsent();
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            var value = platform.ReadBackend();
            if (value.Start != 3 || Rz090528InstalledCpuBackendInspector.Evaluate(value).Availability != CpuTuningBackendAvailability.Available)
            { throw new IOException("PM backend admission denied."); }
            // Pre-mutex inspection is preliminary only. The actual lease repeats
            // this under deny-write/delete pins before and after device opening.
            files = Array.AsReadOnly(value.Files.ToArray());
            _ = CheckContinuity(); return value;
        }
        public IDisposable PinBackendFiles()
        {
            CheckThread();
            if (abandoned) { throw new IOException("PM mutex ownership was abandoned."); }
            IDisposable held = platform.PinBackendFiles(); pinned = true;
            return new PmRelease(() => { try { held.Dispose(); } finally { pinned = false; } });
        }
        public ICpuQueryDevice OpenExistingDevice() { _ = CheckContinuity(); return platform.OpenExistingDevice(); }
        public ICpuQueryMutex CreateSharedMutex() { _ = CheckContinuity(); return new PmMutex(this, platform.CreateSharedMutex()); }
        public IDisposable? Acquire(TimeSpan timeout, CancellationToken token)
        {
            CheckThread(); IDisposable? lease = Transport.Acquire(timeout, token);
            if (lease is null) { return null; }
            leased = true;
            return new PmRelease(() => { try { lease.Dispose(); } finally { leased = false; } });
        }
        public void Delay(TimeSpan duration, CancellationToken token) => Transport.Delay(duration, token);
        private sealed class PmMutex(PmAdapter adapter, ICpuQueryMutex inner) : ICpuQueryMutex
        {
            public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token)
            { var result = inner.Wait(timeout, token); adapter.abandoned = result == CpuQueryMutexWait.Abandoned; return result; }
            public void Release() => inner.Release();
            public void Dispose() => inner.Dispose();
        }
        private sealed class PmRelease(Action release) : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (disposed) { return; } release(); disposed = true; }
        }
    }
    private sealed class PmNativeWire(Action<uint, CpuQueryIoctlReply> retainRejectedCap) : ICpuNativeWireAdmission
    {
        private readonly CpuPmObservationWireAdmission wire = new();
        private uint selectedCap;
        public bool Allows(uint code, byte[] input, int outputLength)
        {
            bool allowed = wire.Allows(code, input, outputLength);
            if (allowed && code == 0x81112FF8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input) == 0)
            {
                uint address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
                selectedCap = address is CpuLivePowerFusedCap.RegisterA0F or CpuLivePowerFusedCap.RegisterA0B ? address : 0;
            }
            return allowed;
        }
        public bool AcceptReply(uint code, byte[] input, int outputLength, CpuQueryIoctlReply reply)
        {
            bool accepted = wire.AcceptReply(code, input, outputLength, reply);
            if (!accepted && selectedCap != 0 && code == 0x81112F24 && input.Length == 20
                && outputLength == 24 && reply.Bytes.Length == 24 && reply.Bytes.AsSpan(0, 20).SequenceEqual(input))
            { retainRejectedCap(selectedCap, reply); }
            return accepted;
        }
        public void Abort() => wire.Abort();
    }
    private sealed class PmClock : ICpuPmObservationClock
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public TimeSpan MonotonicNow => Stopwatch.GetElapsedTime(start);
        public long MonotonicTimestamp => Stopwatch.GetTimestamp();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public void DelayOneSecond(CancellationToken token) { token.ThrowIfCancellationRequested(); if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) { token.ThrowIfCancellationRequested(); } }
    }
    private sealed class PmNativePlatform(CpuQueryWindowsPlatform shared) : ICpuPmWindowsPlatform
    {
        public HostIdentityReadResult ReadHost() => shared.ReadHost();
        public CpuQueryProcessorIdentity ReadProcessor() => shared.ReadProcessor();
        public DeviceIdentity? ReadUsb() => Rz090528CpuFreshUsbIdentity.Read();
        public CpuPmProcessorPackage ReadPackage()
        {
            if (!X86Base.IsSupported) { return default; }
            uint maximum = unchecked((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax);
            return maximum < 0x80000001 ? new(maximum, -1)
                : new(maximum, (int)(unchecked((uint)X86Base.CpuId(unchecked((int)0x80000001), 0).Ebx) >> 28));
        }
        public bool ControllersAbsent() => shared.ControllersAbsent();
        public IDisposable PinBackendFiles() => shared.PinBackendFiles();
        public InstalledCpuBackendSnapshot ReadBackend() => shared.ReadBackend();
        public ICpuQueryDevice OpenExistingDevice() => shared.OpenExistingDevice();
        public ICpuQueryMutex CreateSharedMutex() => shared.CreateSharedMutex();
        public CpuPmServiceSnapshot ReadService()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Rz090528InstalledCpuBackendInspector.ServiceName, false);
            if (key is null) { return new(false, null, null, null, false); }
            using var service = new ServiceController(Rz090528InstalledCpuBackendInspector.ServiceName);
            return new(true, key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                key.GetValue("Type") as int?, key.GetValue("Start") as int?,
                service.ServiceType == ServiceType.KernelDriver && service.Status == ServiceControllerStatus.Running);
        }
        public ICpuPmContinuityScope ArmContinuity(CpuPmRunOwnedDriverLifetime lifetime) => new PmContinuity(WindowsCpuPmSessionContinuity.Arm(lifetime));
        private sealed class PmContinuity(WindowsCpuPmSessionContinuity scope) : ICpuPmContinuityScope
        {
            public CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? lifetime) => scope.ReadFresh(lifetime);
            public void Dispose() => scope.Dispose();
        }
    }
}
