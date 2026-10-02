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
    internal CpuPmObservationProfile Profile { get; }
    private CpuPmAttendedApproval(CpuPmRunOwnedDriverLifetime lifetime, CpuPmObservationProfile profile)
    { Lifetime = lifetime; Profile = profile; }
    internal static CpuPmAttendedApproval Acknowledge(bool pciTransportAndKernelCopyApproved,
        bool freshRunOwnedDriverLifetimeApproved, CpuPmRunOwnedDriverLifetime lifetime,
        CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples)
    {
        if (!Enum.IsDefined(profile) || !pciTransportAndKernelCopyApproved || !freshRunOwnedDriverLifetimeApproved
            || lifetime is not { ConfirmedFreshRunOwned: true } || lifetime.RunId == Guid.Empty || lifetime.DriverLifetimeId == Guid.Empty)
        { throw new ArgumentException("Both scope-specific PM approvals and a run-owned lifetime are required."); }
        return new(lifetime, profile);
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
        Func<CpuPmRunOwnedDriverLifetime?> readFreshDriverLifetime, CancellationToken token = default,
        IRz090528CpuLivePowerRangeMetadataReader? metadataReader = null) =>
        WindowsRz090528CpuQueryAdapter.ObservePmNative(approval, readFreshDriverLifetime, token, metadataReader);
    internal static CpuPmObservationBatch ObserveForAttendedValidation(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> readFreshDriverLifetime, ICpuPmWindowsPlatform platform,
        ICpuPmObservationClock clock, CancellationToken token = default,
        IRz090528CpuLivePowerRangeMetadataReader? metadataReader = null) =>
        WindowsRz090528CpuQueryAdapter.ObservePm(approval, readFreshDriverLifetime, platform, clock, token, metadataReader);
    internal static CpuPmObservationBatch Denied(CpuPmObservationStage stage = CpuPmObservationStage.Admission,
        CpuPmObservationFailure failure = CpuPmObservationFailure.AdmissionDenied,
        CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples) => new(false, [], [], 0, 0, new(stage, failure), profile: profile);
}

// Reuse the retained private native platform, operation gate and same-thread
// query lease. PM approval and wire admission remain separate from query/CO.
internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    internal static CpuPmObservationBatch ObservePmNative(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, CancellationToken token,
        IRz090528CpuLivePowerRangeMetadataReader? metadataReader = null) =>
        ObservePm(approval, lifetime, InstalledPlatform.Value, new PmClock(), token, metadataReader);

    internal static CpuPmObservationBatch ObservePm(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, ICpuPmWindowsPlatform platform, ICpuPmObservationClock clock,
        CancellationToken token, IRz090528CpuLivePowerRangeMetadataReader? metadataReader = null) =>
        ObservePmScope(approval, lifetime, platform, clock, token, metadataReader).Batch;

    private static CpuLivePowerNativeObservationResult ObservePmScope(CpuPmAttendedApproval? approval,
        Func<CpuPmRunOwnedDriverLifetime?> lifetime, ICpuPmWindowsPlatform platform, ICpuPmObservationClock clock,
        CancellationToken token, IRz090528CpuLivePowerRangeMetadataReader? metadataReader,
        ICpuPmContinuityScope? borrowedContinuity = null)
    {
        if (approval?.Consume() != true || approval.Profile == CpuPmObservationProfile.RangeSingleSample && metadataReader is null)
        { return new(WindowsRz090528CpuPmObservationAdapter.Denied(profile: approval?.Profile ?? CpuPmObservationProfile.IdleThreeSamples), true, true); }
        ArgumentNullException.ThrowIfNull(lifetime); ArgumentNullException.ThrowIfNull(platform); ArgumentNullException.ThrowIfNull(clock);
        token.ThrowIfCancellationRequested();
        // Use the actual persistent platform, never the per-call PmAdapter.
        // The gate covers notification registration and cleanup as well as IOCTLs.
        var operation = NativeOperations.GetValue(platform, static _ => new());
        if (Interlocked.CompareExchange(ref operation.Active, 1, 0) != 0)
        { return new(WindowsRz090528CpuPmObservationAdapter.Denied(CpuPmObservationStage.Mutex, CpuPmObservationFailure.Busy, approval.Profile), true, true); }
        ICpuPmContinuityScope? continuity = null;
        PmAdapter? adapter = null;
        var metadata = metadataReader is null ? null : new PmMetadataCleanup(metadataReader);
        bool cleanup = true, unknownFailure = false;
        CpuPmObservationBatch result = WindowsRz090528CpuPmObservationAdapter.Denied(profile: approval.Profile);
        try
        {
            if (lifetime() != approval.Lifetime) { throw new IOException("PM lifetime admission denied."); }
            // Arm before even constructing/acquiring a native mutex or device.
            // If registration throws before returning its scope, its resources
            // cannot be proven released and this platform must stay parked.
            cleanup = borrowedContinuity is not null;
            continuity = borrowedContinuity ?? platform.ArmContinuity(approval.Lifetime);
            if (continuity is null) { throw new IOException("PM continuity scope unavailable."); }
            cleanup = true;
            adapter = new PmAdapter(platform, continuity, approval.Lifetime, lifetime, clock, approval.Profile);
            adapter.FullAdmission();
            token.ThrowIfCancellationRequested();
            result = adapter.PreserveRejectedCap(new Rz090528CpuPmObservationEngine(adapter.Transport, adapter, adapter, clock, approval.Profile, metadata).Observe(token));
            if (continuity.ReadFresh(lifetime()).Continuity != CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime)
            { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied); }
        }
        catch (OperationCanceledException) { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.Cancelled); }
        catch (Exception e) when (PmBoundedFailure(e))
        { result = Incomplete(result, CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied); }
        catch { unknownFailure = true; throw; }
        finally
        {
            try
            {
                if (continuity is not null && borrowedContinuity is null)
                {
                    // A previous continuity failure can hide a later unregister
                    // failure. Only explicit clean disposal proves release.
                    cleanup = false;
                    try
                    {
                        continuity.Dispose();
                        cleanup = continuity.ReadFresh(approval.Lifetime).Failure == CpuPmContinuityFailure.Disposed;
                    }
                    catch (Exception e) when (PmBoundedFailure(e) || e is OperationCanceledException) { }
                }
            }
            finally
            {
                // Includes Acquire cleanup before a lease was returned, and a
                // cleanup cancellation swallowed by the PM engine's diagnostics.
                cleanup &= !unknownFailure && adapter?.Transport.cleanupFailed != true && metadata?.CleanupComplete != false;
                if (!cleanup)
                { result = Incomplete(result, CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed); }
                else { Volatile.Write(ref operation.Active, 0); }
            }
        }
        return new(result, true, cleanup);
    }
    private static bool MetadataDisposed<T>(CpuLivePowerRangeMetadataCall<T> call) where T : class =>
        call.Status != CpuLivePowerRangeMetadataStatus.DisposalFailed
            && (call.Stages & CpuLivePowerRangeMetadataStages.Disposed) != 0;
    // Track actual invocation/return, including an exception before the engine
    // can retain a result. Missing disposal evidence never releases the latch.
    private sealed class PmMetadataCleanup(IRz090528CpuLivePowerRangeMetadataReader reader) : IRz090528CpuLivePowerRangeMetadataReader
    {
        internal bool CleanupComplete { get; private set; } = true;
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint) => Track(() => reader.QueryVersion(checkpoint));
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint) => Track(() => reader.GetDValues(checkpoint));
        public CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint) => Track(() => reader.GetMinMaxSteppingValues(checkpoint));
        private CpuLivePowerRangeMetadataCall<T> Track<T>(Func<CpuLivePowerRangeMetadataCall<T>> read) where T : class
        {
            bool prior = CleanupComplete; CleanupComplete = false;
            var result = read();
            if (result is null) { throw new IOException("PM metadata result unavailable."); }
            CleanupComplete = prior && MetadataDisposed(result);
            return result;
        }
    }
    private static CpuPmObservationBatch Incomplete(CpuPmObservationBatch result, CpuPmObservationStage stage, CpuPmObservationFailure failure) =>
        new(false, result.CopySamples(), result.CopyPciRecords(), result.OrientationMask, result.FuseMask, new(stage, failure), result.FusedCap, result.Profile, result.RangeObservation)
        {
            FirmwareVersion = result.FirmwareVersion
        };
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
            CpuPmRunOwnedDriverLifetime lifetime, Func<CpuPmRunOwnedDriverLifetime?> readLifetime, ICpuPmObservationClock clock, CpuPmObservationProfile profile)
        {
            this.platform = platform; this.continuity = continuity; this.lifetime = lifetime; this.readLifetime = readLifetime;
            Transport = new(this, ReadUsb, new PmNativeWire(profile, (address, reply) =>
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
            if (rejected.Address == CpuLivePowerFusedCap.RegisterA0F) { first = rejected; }
            else if (rejected.Address == CpuLivePowerFusedCap.RegisterA0B) { second = rejected; }
            var range = result.RangeObservation;
            if (range is not null)
            {
                range = rejected.Address switch
                {
                    CpuLivePowerFusedCap.RegisterA0F => range with { A0F = rejected },
                    CpuLivePowerFusedCap.RegisterA0B => range with { A0B = rejected },
                    CpuLivePowerRangeObservation.RegisterA13 => range with { A13 = rejected },
                    CpuLivePowerRangeObservation.RegisterA90 => range with { A90 = rejected },
                    0x03B10A88 when range.Capability is not null => range with
                    {
                        Capability = range.Capability with
                        {
                            RawValue = rejected.RawValue,
                            CapturedUtc = rejected.CapturedUtc,
                            MonotonicTimestamp = rejected.MonotonicTimestamp,
                            ReturnedWordTransportSucceeded = rejected.TransportSucceeded,
                            PostReadAdmissionPassed = false
                        }
                    },
                    _ => range,
                };
            }
            return new(false, result.CopySamples(), result.CopyPciRecords(), result.OrientationMask, result.FuseMask,
                result.Diagnostic, new(first, second), result.Profile, range)
            {
                FirmwareVersion = result.FirmwareVersion
            };
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
                { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4" })
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
    private sealed class PmNativeWire(CpuPmObservationProfile profile, Action<uint, CpuQueryIoctlReply> retainRejectedCap) : ICpuNativeWireAdmission
    {
        private readonly CpuPmObservationWireAdmission wire = new(profile);
        private uint selectedCap, selectedAddress, currentCommand;
        public bool Allows(uint code, byte[] input, int outputLength)
        {
            bool allowed = wire.Allows(code, input, outputLength);
            if (allowed && code == 0x81112FF8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input) == 1 && selectedAddress == 0x03B10A20)
            { currentCommand = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4)); }
            if (allowed && code == 0x81112FF8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input) == 0)
            {
                uint address = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4));
                selectedAddress = address;
                selectedCap = address == 0x03B10A88 && currentCommand == 0x82 ? address : address is CpuLivePowerFusedCap.RegisterA0F or CpuLivePowerFusedCap.RegisterA0B
                    or CpuLivePowerRangeObservation.RegisterA13 or CpuLivePowerRangeObservation.RegisterA90 ? address : 0;
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
    private sealed partial class CpuQueryWindowsPlatform : ICpuPmWindowsPlatform
    {
        public DeviceIdentity? ReadUsb() => Rz090528CpuFreshUsbIdentity.Read();
        public CpuPmProcessorPackage ReadPackage()
        {
            if (!X86Base.IsSupported) { return default; }
            uint maximum = unchecked((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax);
            return maximum < 0x80000001 ? new(maximum, -1)
                : new(maximum, (int)(unchecked((uint)X86Base.CpuId(unchecked((int)0x80000001), 0).Ebx) >> 28));
        }
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
