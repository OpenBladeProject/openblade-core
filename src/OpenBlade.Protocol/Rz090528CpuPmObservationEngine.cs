using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal enum CpuPmSessionContinuity { Unknown, ConfirmedFreshRunOwnedLifetime, Interrupted }
internal sealed record CpuPmSessionEvidence(DeviceIdentity UsbIdentity, HostIdentityReadResult Host,
    CpuQueryProcessorIdentity Processor, uint MaximumExtendedCpuidLeaf, int Package,
    InstalledCpuBackendSnapshot Backend, bool ControllersAbsent, CpuPmSessionContinuity Continuity,
    Guid SessionId, Guid DriverLifetimeId);

// This is fresh evidence, not an approval flag. The attended adapter must own
// approval, pins, lifecycle/suspend observations and the existing-device handle.
// File metadata comes from immutable held pins; fresh checks use cheap session,
// power, controller and service evidence, not repeated hashing or PnP scans.
internal interface ICpuPmObservationSessionAdmission
{
    CpuPmSessionEvidence ReadFresh();
    bool ValidateSnapshotBoundary();
}
internal interface ICpuPmObservationClock
{
    TimeSpan MonotonicNow { get; }
    long MonotonicTimestamp { get; }
    DateTimeOffset UtcNow { get; }
    void DelayOneSecond(CancellationToken cancellationToken);
}
internal enum CpuPmObservationStage { Admission, Mutex, PciCount, PciRecords, PciIdentity, Mask, Version, Refresh, Copy, PostVersion, Interval, Cleanup, SnapshotAdmission, FusedCap }
internal enum CpuPmObservationFailure { AdmissionDenied, Busy, Cancelled, BudgetExpired, InvalidData, UnsupportedVersion, RequestRejected, ReplyRejected, NativeFailure, CleanupFailed }
internal sealed record CpuPmObservationDiagnostic(CpuPmObservationStage Stage, CpuPmObservationFailure Failure);
internal sealed record CpuPmCompletedSample(CpuPmRawSnapshot Snapshot, uint RefreshReturnedWord);

// Returned transport evidence only: a candidate is never fresh write authority.
// Retain a shaped reply even when its contents or post-read admission fail.
internal sealed record CpuPmCapRegisterRead(uint Address, uint RawValue, DateTimeOffset CapturedUtc,
    long MonotonicTimestamp, bool TransportSucceeded, bool PostReadAdmissionPassed);
internal sealed record CpuPmFusedCapObservation(CpuPmCapRegisterRead? A0F, CpuPmCapRegisterRead? A0B)
{
    internal CpuLivePowerFusedCap? Candidate => A0F is { Address: CpuLivePowerFusedCap.RegisterA0F, TransportSucceeded: true, PostReadAdmissionPassed: true } first
        && A0B is { Address: CpuLivePowerFusedCap.RegisterA0B, TransportSucceeded: true, PostReadAdmissionPassed: true } second
        ? CpuLivePowerFusedCap.TryDecode(first.RawValue, second.RawValue) : null;
}

internal sealed class CpuPmObservationBatch
{
    private readonly CpuPmCompletedSample[] samples;
    private readonly byte[] records;
    internal CpuPmObservationBatch(bool complete, IEnumerable<CpuPmCompletedSample> samples,
        byte[] records, uint orientationMask, uint fuseMask, CpuPmObservationDiagnostic? diagnostic,
        CpuPmFusedCapObservation? fusedCap = null)
    {
        Complete = complete;
        this.samples = samples.ToArray();
        this.records = (byte[])records.Clone();
        OrientationMask = orientationMask;
        FuseMask = fuseMask;
        Diagnostic = diagnostic;
        FusedCap = fusedCap;
    }
    internal bool Complete { get; }
    internal uint OrientationMask { get; }
    internal uint FuseMask { get; }
    internal CpuPmObservationDiagnostic? Diagnostic { get; }
    internal CpuPmFusedCapObservation? FusedCap { get; }
    internal CpuPmCompletedSample[] CopySamples() => (CpuPmCompletedSample[])samples.Clone();
    internal byte[] CopyPciRecords() => (byte[])records.Clone();
}

// Fixed attended idle profile only. All execution stays synchronous on one
// thread; no SDK, driver provisioning, setters, workload or native adapter wiring.
internal sealed class Rz090528CpuPmObservationEngine(IRz090528CpuQueryIoctl ioctl,
    IRz090528CpuQuerySynchronization synchronization, ICpuPmObservationSessionAdmission admission,
    ICpuPmObservationClock clock)
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private readonly CpuPmObservationWireAdmission wire = new();
    private readonly List<CpuPmCompletedSample> samples = [];
    private byte[] records = [];
    private uint orientationMask, fuseMask;
    private TimeSpan started, previousClock;
    private Guid sessionId, lifetimeId;
    private int ownerThread;
    private bool used, readingVersionArgument;
    private CpuPmObservationStage stage;
    private CpuPmCapRegisterRead? capA0F, capA0B;
    private uint readingCapAddress;

    internal CpuPmObservationBatch Observe(CancellationToken cancellationToken = default)
    {
        if (used) { return Batch(new(CpuPmObservationStage.Admission, CpuPmObservationFailure.AdmissionDenied)); }
        used = true;
        ownerThread = Environment.CurrentManagedThreadId;
        started = previousClock = clock.MonotonicNow;
        IDisposable? lease = null;
        CpuPmObservationDiagnostic? diagnostic = null;
        try
        {
            stage = CpuPmObservationStage.Admission;
            CheckBoundary(cancellationToken);
            stage = CpuPmObservationStage.Mutex;
            lease = synchronization.Acquire(TimeSpan.FromSeconds(20), cancellationToken);
            if (lease is null) { throw new Failure(CpuPmObservationFailure.Busy); }
            CheckBoundary(cancellationToken);
            uint bus = Discover(cancellationToken);
            stage = CpuPmObservationStage.PciIdentity;
            ReadPci(bus, 0, 0, 0, cancellationToken);
            ReadPci(bus, 0x18, 0, 0, cancellationToken);
            ReadPci(0, 0x18, 1, 0, cancellationToken);
            stage = CpuPmObservationStage.Mask;
            orientationMask = ReadPci(0, 0x18, 1, 0x140, cancellationToken);
            fuseMask = ~(ReadSmn(bus, 0x03820AB0, cancellationToken) >> 5) & 0xFFFF;
            if (orientationMask != 0xFF55 || fuseMask != orientationMask) { throw new Failure(CpuPmObservationFailure.InvalidData); }
            stage = CpuPmObservationStage.FusedCap;
            ReadCap(bus, CpuLivePowerFusedCap.RegisterA0F, cancellationToken);
            ReadCap(bus, CpuLivePowerFusedCap.RegisterA0B, cancellationToken);
            if (new CpuPmFusedCapObservation(capA0F, capA0B).Candidate is null)
            { throw new Failure(CpuPmObservationFailure.InvalidData); }
            TimeSpan? lastCapture = null;
            for (int index = 0; index < 3; index++)
            {
                if (lastCapture.HasValue)
                {
                    stage = CpuPmObservationStage.Interval;
                    CheckBoundary(cancellationToken);
                    clock.DelayOneSecond(cancellationToken);
                    CheckBoundary(cancellationToken);
                    if (clock.MonotonicNow - lastCapture.Value < TimeSpan.FromSeconds(1))
                    { throw new Failure(CpuPmObservationFailure.InvalidData); }
                }
                CheckSnapshotBoundary(cancellationToken);
                stage = CpuPmObservationStage.Version;
                Mailbox(bus, 6, 0, cancellationToken);
                stage = CpuPmObservationStage.Refresh;
                uint refresh = Mailbox(bus, 0x65, 3, cancellationToken);
                stage = CpuPmObservationStage.Copy;
                byte[] reply = Exchange(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, cancellationToken);
                DateTimeOffset utc = clock.UtcNow;
                long timestamp = clock.MonotonicTimestamp;
                lastCapture = clock.MonotonicNow;
                stage = CpuPmObservationStage.PostVersion;
                Mailbox(bus, 6, 0, cancellationToken); // explicitly reselect after hidden C4/C8 writes
                CheckBoundary(cancellationToken);
                if (!CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(true, reply), utc, timestamp, out var snapshot))
                { throw new Failure(CpuPmObservationFailure.InvalidData); }
                // Invalid candidate telemetry is retained privately, never claimed as a valid batch.
                samples.Add(new(snapshot!, refresh));
                if (snapshot!.Validity != CpuPmRawSnapshotValidity.ValidCandidateObservation)
                { throw new Failure(CpuPmObservationFailure.InvalidData); }
                CheckSnapshotBoundary(cancellationToken);
            }
        }
        catch (Failure failure) { wire.Abort(); diagnostic = new(stage, failure.Code); }
        catch (OperationCanceledException) { wire.Abort(); diagnostic = new(stage, CpuPmObservationFailure.Cancelled); }
        catch (Exception exception) when (IsBoundedFailure(exception))
        { wire.Abort(); diagnostic = new(stage, CpuPmObservationFailure.NativeFailure); }
        finally
        {
            try { lease?.Dispose(); }
            catch (Exception exception) when (IsBoundedFailure(exception))
            { wire.Abort(); diagnostic = new(CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed); }
        }
        return Batch(diagnostic);
    }

    private CpuPmObservationBatch Batch(CpuPmObservationDiagnostic? diagnostic) =>
        new(diagnostic is null && samples.Count == 3, samples, records, orientationMask, fuseMask, diagnostic,
            capA0F is null && capA0B is null ? null : new(capA0F, capA0B));

    private void CheckBoundary(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId != ownerThread) { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        TimeSpan now = clock.MonotonicNow;
        if (now < previousClock || now - started >= TimeSpan.FromSeconds(90))
        { throw new Failure(CpuPmObservationFailure.BudgetExpired); }
        previousClock = now;
        CpuPmSessionEvidence evidence = admission.ReadFresh();
        if (evidence.UsbIdentity is not { Model: "RZ09-0528", VendorId: 0x1532, ProductId: 0x02C6 }
            || evidence.Host.Failure is not null || evidence.Host.Evidence is not
            { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4", BiosVersion: "2.02" }
                || evidence.Processor is not { Vendor: "AuthenticAMD", Family: 0x1A, Model: 0x24, Stepping: 0 }
            || evidence.MaximumExtendedCpuidLeaf < 0x80000001 || evidence.Package != 1
            || !evidence.ControllersAbsent || evidence.Continuity != CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime
            || evidence.SessionId == Guid.Empty || evidence.DriverLifetimeId == Guid.Empty
            || Rz090528InstalledCpuBackendInspector.Evaluate(evidence.Backend).Availability != CpuTuningBackendAvailability.Available)
        { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        if (sessionId == Guid.Empty) { sessionId = evidence.SessionId; lifetimeId = evidence.DriverLifetimeId; }
        else if (sessionId != evidence.SessionId || lifetimeId != evidence.DriverLifetimeId)
        { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        // Include the fresh evidence provider's time in the cooperative budget.
        now = clock.MonotonicNow;
        if (now < previousClock || now - started >= TimeSpan.FromSeconds(90))
        { throw new Failure(CpuPmObservationFailure.BudgetExpired); }
        previousClock = now;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void CheckSnapshotBoundary(CancellationToken cancellationToken)
    {
        stage = CpuPmObservationStage.SnapshotAdmission;
        CheckBoundary(cancellationToken);
        if (!admission.ValidateSnapshotBoundary()) { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        CheckBoundary(cancellationToken);
    }

    private uint Discover(CancellationToken token)
    {
        stage = CpuPmObservationStage.PciCount;
        uint count = Word(Exchange(Enumerate, new byte[28], 28, token), 24);
        byte[] request = new byte[checked(28 + (int)count * 52)];
        Put(request, 24, count);
        stage = CpuPmObservationStage.PciRecords;
        records = Exchange(Enumerate, request, request.Length, token);
        return CpuQueryPciTopology.TryFindBus(records, out uint bus)
            ? bus : throw new Failure(CpuPmObservationFailure.InvalidData);
    }
    private uint ReadPci(uint bus, uint device, uint function, uint offset, CancellationToken token) =>
        Word(Exchange(Read, Words(bus, device, function, offset, 4), 24, token), 20);
    private void WriteRegister(uint bus, uint index, uint value, CancellationToken token) =>
        Exchange(Write, Words(index, value, bus), 0, token);
    private uint ReadSmn(uint bus, uint address, CancellationToken token)
    { WriteRegister(bus, 0, address, token); return ReadPci(bus, 0, 0, 0xC8, token); }
    private void ReadCap(uint bus, uint address, CancellationToken token)
    {
        WriteRegister(bus, 0, address, token);
        readingCapAddress = address;
        try { ReadPci(bus, 0, 0, 0xC8, token); }
        finally { readingCapAddress = 0; }
    }
    private void WriteSmn(uint bus, uint address, uint value, CancellationToken token)
    { WriteRegister(bus, 0, address, token); WriteRegister(bus, 1, value, token); }
    private uint Mailbox(uint bus, uint command, uint argument, CancellationToken token)
    {
        WriteRegister(bus, 0, Response, token);
        Poll(bus, token);
        WriteSmn(bus, Response, 0, token);
        WriteSmn(bus, Argument, argument, token);
        WriteSmn(bus, Message, command, token);
        WriteRegister(bus, 0, Response, token);
        Poll(bus, token);
        readingVersionArgument = command == 6;
        try { return ReadSmn(bus, Argument, token); }
        finally { readingVersionArgument = false; }
    }
    private void Poll(uint bus, CancellationToken token)
    {
        for (int index = 0; index < 1000; index++)
        {
            uint response = ReadPci(bus, 0, 0, 0xC8, token);
            if (response == 1) { return; }
            if (response != 0) { throw new Failure(CpuPmObservationFailure.InvalidData); }
            CheckBoundary(token);
            synchronization.Delay(TimeSpan.FromMilliseconds(1), token);
        }
        throw new Failure(CpuPmObservationFailure.Busy);
    }
    private byte[] Exchange(uint code, byte[] input, int outputLength, CancellationToken token)
    {
        CheckBoundary(token);
        if (!wire.Allows(code, input, outputLength)) { throw new Failure(CpuPmObservationFailure.RequestRejected); }
        CpuQueryIoctlReply reply;
        try { reply = ioctl.Invoke(code, input, outputLength); }
        catch { wire.Abort(); throw; }
        CpuPmCapRegisterRead? capRead = null;
        if (readingCapAddress != 0 && code == Read && reply.Bytes.Length == 24
            && reply.Bytes.AsSpan(0, 20).SequenceEqual(input))
        {
            capRead = new(readingCapAddress, Word(reply.Bytes, 20), clock.UtcNow,
                clock.MonotonicTimestamp, reply.Success, false);
            RetainCap(capRead);
        }
        if (!wire.AcceptReply(code, input, outputLength, reply))
        {
            throw new Failure(!reply.Success ? CpuPmObservationFailure.NativeFailure
                : readingVersionArgument && code == Read && reply.Bytes.Length == 24 && Word(input, 12) == 0xC8
                    && Word(reply.Bytes, 20) != CpuPmObservationWireAdmission.Version
                    ? CpuPmObservationFailure.UnsupportedVersion : CpuPmObservationFailure.ReplyRejected);
        }
        CheckBoundary(token);
        if (capRead is not null) { RetainCap(capRead with { PostReadAdmissionPassed = true }); }
        return (byte[])reply.Bytes.Clone();
    }
    private void RetainCap(CpuPmCapRegisterRead read)
    {
        if (read.Address == CpuLivePowerFusedCap.RegisterA0F) { capA0F = read; }
        else { capA0B = read; }
    }
    private sealed class Failure(CpuPmObservationFailure code) : Exception { internal CpuPmObservationFailure Code { get; } = code; }
    private static bool IsBoundedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException
        or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException;
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static byte[] Words(params uint[] values)
    { byte[] bytes = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) { Put(bytes, i * 4, values[i]); } return bytes; }
}
