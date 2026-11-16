using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal enum CpuPmSessionContinuity { Unknown, ConfirmedFreshRunOwnedLifetime, Interrupted, ConfirmedRetainedInstalledLifetime }
internal sealed record CpuPmSessionEvidence(DeviceIdentity UsbIdentity, HostIdentityReadResult Host,
    CpuQueryProcessorIdentity Processor, uint MaximumExtendedCpuidLeaf, int Package,
    InstalledCpuBackendSnapshot Backend, bool ControllersAbsent, CpuPmSessionContinuity Continuity,
    Guid SessionId, Guid DriverLifetimeId)
{
    internal CpuInstalledBackendLease? RetainedInstalledLease { get; init; }
}

// Fresh evidence is separate from approval. Attended runs own parent continuity;
// installed observations bind the actual retained lease and initiating request.
// File metadata comes from immutable held pins; fresh checks use cheap session,
// power, controller and service evidence, not repeated hashing or PnP scans.
internal interface ICpuPmObservationSessionAdmission
{
    CpuLiveCurveContract Contract => CpuLiveCurveContract.Rz090528;
    CpuPmSessionEvidence ReadFresh();
    bool ValidateSnapshotBoundary();
    // Null keeps the existing full per-IOCTL checks for unscoped callers.
    bool? TransportCurrent(CpuInstalledBackendLease expected) => null;
}
internal static class CpuPmSessionIdentity
{
    internal static bool HostMatches(CpuLiveCurveContract contract, HostIdentityReadResult host) =>
        host.Failure is null && host.Evidence is { Manufacturer: "Razer" } evidence
        && evidence.ProductName == contract.ProductName
        && (contract != CpuLiveCurveContract.Rz090528 || evidence.Sku == "RZ09-05289EN4");

    internal static bool ProcessorMatches(CpuLiveCurveContract contract, CpuQueryProcessorIdentity processor) =>
        contract == CpuLiveCurveContract.Rz090528 ? processor is { Vendor: "AuthenticAMD", Family: 0x1A, Model: 0x24, Stepping: 0 }
        : contract.UsesPhoenixProtocol && processor is { Vendor: "AuthenticAMD", Family: 0x19 }
            && processor.Model == (contract == CpuLiveCurveContract.Rz090508 ? 0x75 : 0x74);

    internal static bool PackageMatches(CpuLiveCurveContract contract, uint maximumLeaf, int package) =>
        maximumLeaf >= 0x80000001 && (contract != CpuLiveCurveContract.Rz090528 || package == 1);
}

internal interface ICpuPmObservationClock
{
    TimeSpan MonotonicNow { get; }
    long MonotonicTimestamp { get; }
    DateTimeOffset UtcNow { get; }
    void DelayOneSecond(CancellationToken cancellationToken);
}
internal enum CpuPmObservationStage { Admission, Mutex, PciCount, PciRecords, PciIdentity, Mask, Version, Refresh, Copy, PostVersion, Interval, Cleanup, SnapshotAdmission, FusedCap, Capability, MetadataVersion, MetadataDValues, MetadataMinMax, FirmwareVersion }
internal enum CpuPmObservationFailure { AdmissionDenied, Busy, Cancelled, BudgetExpired, InvalidData, UnsupportedVersion, RequestRejected, ReplyRejected, NativeFailure, CleanupFailed }
// Bounded categories and operation codes only; never retain exception messages or payloads.
internal sealed record CpuPmObservationDiagnostic(CpuPmObservationStage Stage, CpuPmObservationFailure Failure,
    CpuQueryFailureCode? NativeFailure = null, int? NativeErrorCode = null, uint? Operation = null,
    CpuPmAdmissionFailure? AdmissionFailure = null, CpuPmWireRejection? WireRejection = null, uint? MailboxStatus = null);
internal enum CpuPmAdmissionFailure { InstalledRequestAdmission, InstalledBackendContinuity, OwnedLifetimeContinuity }
internal sealed class CpuPmAdmissionException(CpuPmAdmissionFailure failure) : IOException
{
    internal CpuPmAdmissionFailure Failure { get; } = failure;
}
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
        CpuPmFusedCapObservation? fusedCap = null, CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples,
        CpuLivePowerRangeObservation? rangeObservation = null)
    {
        Complete = complete;
        this.samples = samples.ToArray();
        this.records = (byte[])records.Clone();
        OrientationMask = orientationMask;
        FuseMask = fuseMask;
        Diagnostic = diagnostic;
        FusedCap = fusedCap; Profile = profile; RangeObservation = rangeObservation;
    }
    internal uint? FirmwareVersion { get; init; }
    internal CpuPmObservationProfile Profile { get; }
    internal CpuLivePowerRangeObservation? RangeObservation { get; }
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
    ICpuPmObservationClock clock, CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples,
    IRz090528CpuLivePowerRangeMetadataReader? metadataReader = null)
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private readonly CpuLiveCurveContract contract = admission.Contract;
    private readonly CpuCurveNativeProfile native = CpuCurveNativeProfile.ForContract(admission.Contract);
    private readonly CpuPmObservationWireAdmission wire = new(profile, admission.Contract);
    private readonly List<CpuPmCompletedSample> samples = [];
    private byte[] records = [];
    private uint orientationMask, fuseMask;
    private TimeSpan started, previousClock;
    private Guid sessionId, lifetimeId;
    private CpuInstalledBackendLease? installedLease;
    private int ownerThread;
    private bool used, readingVersionArgument, readingFirmwareArgument;
    private uint? firmwareVersion;
    private CpuPmObservationStage stage;
    private CpuPmCapRegisterRead? capA0F, capA0B, capA13, capA90;
    private CpuPmCapabilityObservation? capability;
    private CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion>? metadataVersion;
    private CpuLivePowerRangeMetadataApplicability metadataApplicability;
    private CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax>? metadataMinMax;
    private bool readingCapabilityArgument;
    private CpuPmObservationFailure? metadataCheckpointFailure;
    private int SampleCount => profile == CpuPmObservationProfile.RangeSingleSample ? 1 : 3;
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
            if (!Enum.IsDefined(profile) || native.IsPhoenix && profile != CpuPmObservationProfile.RangeSingleSample
                || !native.IsPhoenix && profile == CpuPmObservationProfile.RangeSingleSample && metadataReader is null)
            { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
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
            orientationMask = native.DecodeOrientation(ReadPci(0, 0x18, 1, 0x140, cancellationToken));
            fuseMask = native.DecodeFuse(ReadSmn(bus, native.FuseAddress, cancellationToken));
            if (orientationMask != contract.EnabledCoreMask || fuseMask != orientationMask) { throw new Failure(CpuPmObservationFailure.InvalidData); }
            if (!native.IsPhoenix)
            {
                stage = CpuPmObservationStage.FusedCap;
                ReadCap(bus, CpuLivePowerFusedCap.RegisterA0F, cancellationToken);
                ReadCap(bus, CpuLivePowerFusedCap.RegisterA0B, cancellationToken);
                if (new CpuPmFusedCapObservation(capA0F, capA0B).Candidate is null)
                { throw new Failure(CpuPmObservationFailure.InvalidData); }
                if (profile == CpuPmObservationProfile.RangeSingleSample)
                {
                    metadataApplicability = CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001;
                    ReadCap(bus, CpuLivePowerRangeObservation.RegisterA13, cancellationToken);
                    ReadCap(bus, CpuLivePowerRangeObservation.RegisterA90, cancellationToken);
                    // This firmware identity binds the separately admitted Strix PPT handlers.
                    stage = CpuPmObservationStage.FirmwareVersion;
                    firmwareVersion = Mailbox(bus, 2, 0, cancellationToken);
                }
            }
            if (profile == CpuPmObservationProfile.RangeSingleSample)
            {
                stage = CpuPmObservationStage.Capability;
                capability = new(0x82, 0, false, false, null, null, null, false);
                Mailbox(bus, 0x82, 0, cancellationToken);
            }
            TimeSpan? lastCapture = null;
            for (int index = 0; index < SampleCount; index++)
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
                uint version = Mailbox(bus, 6, 0, cancellationToken);
                int length = native.PmLength(version);
                (uint refresh, byte[] reply) = CpuObservationCosts.Measure(CpuObservationCost.PMCopy, () =>
                {
                    stage = CpuPmObservationStage.Refresh;
                    uint refreshed = Mailbox(bus, 0x65, 3, cancellationToken);
                    stage = CpuPmObservationStage.Copy;
                    CheckBoundary(cancellationToken);
                    byte[] copied = Exchange(CpuPmObservationWireAdmission.Copy, Words((uint)length), length + sizeof(uint), cancellationToken);
                    CheckBoundary(cancellationToken);
                    return (refreshed, copied);
                });
                DateTimeOffset utc = clock.UtcNow;
                long timestamp = clock.MonotonicTimestamp;
                lastCapture = clock.MonotonicNow;
                stage = CpuPmObservationStage.PostVersion;
                Mailbox(bus, 6, 0, cancellationToken); // explicitly reselect after hidden C4/C8 writes
                CheckBoundary(cancellationToken);
                if (!CpuPmRawSnapshot.TryCreate(contract, version, new(true, reply), utc, timestamp, out var snapshot))
                { throw new Failure(CpuPmObservationFailure.InvalidData); }
                // Invalid candidate telemetry is retained privately, never claimed as a valid batch.
                samples.Add(new(snapshot!, refresh));
                if (snapshot!.Validity != CpuPmRawSnapshotValidity.ValidCandidateObservation)
                { throw new Failure(CpuPmObservationFailure.InvalidData); }
                CheckSnapshotBoundary(cancellationToken);
            }
            if (!native.IsPhoenix && profile == CpuPmObservationProfile.RangeSingleSample) { ReadMetadata(cancellationToken); }
        }
        catch (Failure failure)
        { wire.Abort(); diagnostic = new(stage, failure.Code, failure.NativeFailure, failure.NativeErrorCode, failure.Operation, failure.AdmissionFailure); }
        catch (CpuPmAdmissionException exception)
        { wire.Abort(); diagnostic = new(stage, CpuPmObservationFailure.AdmissionDenied, CpuQueryFailureCode.AdmissionDenied, AdmissionFailure: exception.Failure); }
        catch (OperationCanceledException) { wire.Abort(); diagnostic = new(stage, CpuPmObservationFailure.Cancelled); }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            wire.Abort();
            var detail = CpuQueryDiagnostic.FromException(CpuQueryStage.Approval, exception);
            diagnostic = new(stage, CpuPmObservationFailure.NativeFailure, detail.Failure, detail.NativeErrorCode);
        }
        finally
        {
            try
            {
                lease?.Dispose();
                if (diagnostic is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.MonotonicNow < previousClock || clock.MonotonicNow - started >= TimeSpan.FromSeconds(90))
                    { diagnostic = new(CpuPmObservationStage.Cleanup, CpuPmObservationFailure.BudgetExpired); }
                }
            }
            catch (OperationCanceledException)
            { wire.Abort(); diagnostic ??= new(CpuPmObservationStage.Cleanup, CpuPmObservationFailure.Cancelled); }
            catch (Exception exception) when (IsBoundedFailure(exception))
            {
                wire.Abort();
                var detail = CpuQueryDiagnostic.FromException(CpuQueryStage.Cleanup, exception);
                diagnostic ??= new(CpuPmObservationStage.Cleanup, CpuPmObservationFailure.CleanupFailed, detail.Failure, detail.NativeErrorCode);
            }
        }
        return Batch(diagnostic);
    }

    private CpuPmObservationBatch Batch(CpuPmObservationDiagnostic? diagnostic)
    {
        if (diagnostic is not null && wire.FirstRejection is { } rejection)
        { diagnostic = diagnostic with { WireRejection = rejection, MailboxStatus = wire.RejectedMailboxStatus }; }
        CpuLivePowerRangeObservation? range = profile == CpuPmObservationProfile.RangeSingleSample
            ? new(capA0F, capA0B, capA13, capA90, capability, metadataVersion, null, metadataMinMax, metadataApplicability) : null;
        bool metadataComplete = native.IsPhoenix
            ? capability is { ResponseAcknowledged: true, InvocationEntered: true, PostReadAdmissionPassed: true, ReturnedWordTransportSucceeded: true, RawValue: { } raw } && raw != uint.MaxValue && (raw & 4) != 0
            : range is null || range.MetadataComplete;
        return new(diagnostic is null && samples.Count == SampleCount && metadataComplete,
            samples, records, orientationMask, fuseMask, diagnostic,
            capA0F is null && capA0B is null ? null : new(capA0F, capA0B), profile, range)
        {
            FirmwareVersion = firmwareVersion
        };
    }

    private void ReadMetadata(CancellationToken token)
    {
        CheckSnapshotBoundary(token);
        stage = CpuPmObservationStage.MetadataVersion;
        metadataVersion = metadataReader!.QueryVersion(() => MetadataCheckpoint(token));
        CheckMetadata(metadataVersion, token);
        if (metadataVersion.Value is not { Version: 7 }) { throw new Failure(CpuPmObservationFailure.UnsupportedVersion); }
        metadataVersion = metadataVersion with { PostAdmissionPassed = true };
        // This exact firmware's PPT entry is OBIT type One. Getdvalues only
        // matches type Zero and is deliberately not part of this fixed profile.
        stage = CpuPmObservationStage.MetadataMinMax;
        metadataMinMax = metadataReader.GetMinMaxSteppingValues(() => MetadataCheckpoint(token));
        CheckMetadata(metadataMinMax, token);
        if (metadataMinMax.Value is not { Value: 0x50001 }) { throw new Failure(CpuPmObservationFailure.InvalidData); }
        metadataMinMax = metadataMinMax with { PostAdmissionPassed = true };
    }
    private void MetadataCheckpoint(CancellationToken token)
    {
        if (metadataCheckpointFailure.HasValue) { throw new IOException("Range metadata admission stopped."); }
        try { CheckBoundary(token); }
        catch (Failure failure)
        {
            metadataCheckpointFailure = failure.Code;
            throw new IOException("Range metadata admission stopped.");
        }
    }
    private void CheckMetadata<T>(CpuLivePowerRangeMetadataCall<T> call, CancellationToken token) where T : class
    {
        if (metadataCheckpointFailure is { } failure) { throw new Failure(failure); }
        CheckBoundary(token);
        const CpuLivePowerRangeMetadataStages complete = CpuLivePowerRangeMetadataStages.InvocationEntered
            | CpuLivePowerRangeMetadataStages.InvocationReturned | CpuLivePowerRangeMetadataStages.OutputEnumerated
            | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed;
        if (call.Status != CpuLivePowerRangeMetadataStatus.Complete || call.Value is null || call.Stages != complete)
        { throw new Failure(CpuPmObservationFailure.InvalidData); }
        CpuPmObservationStage current = stage; CheckSnapshotBoundary(token); stage = current;
    }

    private void CheckBoundary(CancellationToken cancellationToken)
    {
        CheckLifetime(cancellationToken);
        CpuPmSessionEvidence evidence = admission.ReadFresh();
        if (CpuLiveCurveContract.ForIdentity(evidence.UsbIdentity) != contract
            || !CpuPmSessionIdentity.HostMatches(contract, evidence.Host)
            || !CpuPmSessionIdentity.ProcessorMatches(contract, evidence.Processor)
            || !CpuPmSessionIdentity.PackageMatches(contract, evidence.MaximumExtendedCpuidLeaf, evidence.Package)
            || native.IsPhoenix && evidence.Continuity != CpuPmSessionContinuity.ConfirmedRetainedInstalledLifetime
            || !evidence.ControllersAbsent
            || Rz090528InstalledCpuBackendInspector.Evaluate(evidence.Backend, CpuInstalledBackendProfile.ForContract(contract)).Availability != CpuTuningBackendAvailability.Available)
        { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        if (evidence.Continuity == CpuPmSessionContinuity.ConfirmedRetainedInstalledLifetime)
        {
            if (profile != CpuPmObservationProfile.RangeSingleSample || sessionId != Guid.Empty
                || evidence.SessionId != Guid.Empty || evidence.DriverLifetimeId != Guid.Empty
                || evidence.RetainedInstalledLease is not { LifetimeCurrent: true } current
                || !current.RegistrationMatches(evidence.Backend)
                || installedLease is not null && !ReferenceEquals(installedLease, current))
            { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
            installedLease = current;
        }
        else
        {
            if (installedLease is not null || evidence.RetainedInstalledLease is not null
                || evidence.Continuity != CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime
                || evidence.SessionId == Guid.Empty || evidence.DriverLifetimeId == Guid.Empty)
            { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
            if (sessionId == Guid.Empty) { sessionId = evidence.SessionId; lifetimeId = evidence.DriverLifetimeId; }
            else if (sessionId != evidence.SessionId || lifetimeId != evidence.DriverLifetimeId)
            { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        }
        // Include the fresh evidence provider's time in the cooperative budget.
        CheckLifetime(cancellationToken);
    }

    private void CheckLifetime(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId != ownerThread) { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        TimeSpan now = clock.MonotonicNow;
        if (now < previousClock || now - started >= TimeSpan.FromSeconds(90))
        { throw new Failure(CpuPmObservationFailure.BudgetExpired); }
        previousClock = now;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void CheckTransportBoundary(CancellationToken token)
    {
        CheckLifetime(token);
        if (installedLease is null || admission.TransportCurrent(installedLease) is not bool current)
        { CheckBoundary(token); return; }
        if (!current || !installedLease.LifetimeCurrent) { throw new Failure(CpuPmObservationFailure.AdmissionDenied); }
        CheckLifetime(token);
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
        return CpuQueryPciTopology.TryFindBus(records, native, out uint bus)
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
        CheckBoundary(token);
        WriteRegister(bus, 0, address, token);
        readingCapAddress = address;
        try { ReadPci(bus, 0, 0, 0xC8, token); }
        finally { readingCapAddress = 0; }
        CheckBoundary(token);
        var completed = address switch
        {
            CpuLivePowerFusedCap.RegisterA0F => capA0F,
            CpuLivePowerFusedCap.RegisterA0B => capA0B,
            CpuLivePowerRangeObservation.RegisterA13 => capA13,
            CpuLivePowerRangeObservation.RegisterA90 => capA90,
            _ => null,
        };
        if (completed is not null) { RetainCap(completed with { PostReadAdmissionPassed = true }); }
    }
    private void WriteSmn(uint bus, uint address, uint value, CancellationToken token)
    { WriteRegister(bus, 0, address, token); WriteRegister(bus, 1, value, token); }
    private uint Mailbox(uint bus, uint command, uint argument, CancellationToken token)
    {
        CheckBoundary(token);
        WriteRegister(bus, 0, Response, token);
        Poll(bus, requireSuccess: false, token);
        WriteSmn(bus, Response, 0, token);
        WriteSmn(bus, Argument, argument, token);
        WriteSmn(bus, Message, command, token);
        WriteRegister(bus, 0, Response, token);
        Poll(bus, requireSuccess: true, token);
        readingVersionArgument = command is 2 or 6;
        readingFirmwareArgument = command == 2;
        readingCapabilityArgument = command == 0x82;
        if (readingCapabilityArgument && capability is not null) { capability = capability with { ResponseAcknowledged = true }; }
        try
        {
            uint result = ReadSmn(bus, Argument, token);
            CheckBoundary(token);
            if (readingCapabilityArgument && capability is not null) { capability = capability with { PostReadAdmissionPassed = true }; }
            return result;
        }
        finally { readingVersionArgument = readingFirmwareArgument = readingCapabilityArgument = false; }
    }
    private void Poll(uint bus, bool requireSuccess, CancellationToken token)
    {
        for (int index = 0; index < 1000; index++)
        {
            uint response = ReadPci(bus, 0, 0, 0xC8, token);
            if (response == 1 || !requireSuccess && response != 0) { return; }
            if (response != 0) { throw new Failure(CpuPmObservationFailure.InvalidData); }
            CheckTransportBoundary(token);
            synchronization.Delay(TimeSpan.FromMilliseconds(1), token);
        }
        throw new Failure(CpuPmObservationFailure.Busy);
    }
    private byte[] Exchange(uint code, byte[] input, int outputLength, CancellationToken token)
    {
        CheckTransportBoundary(token);
        if (!wire.Allows(code, input, outputLength)) { throw new Failure(CpuPmObservationFailure.RequestRejected); }
        CpuQueryIoctlReply reply;
        try
        {
            if (stage == CpuPmObservationStage.Capability && code == Write && Word(input, 0) == 1 && Word(input, 4) == 0x82 && capability is not null)
            { capability = capability with { InvocationEntered = true }; }
            reply = ioctl.Invoke(code, input, outputLength);
        }
        catch (CpuPmAdmissionException exception)
        {
            wire.Abort();
            throw new Failure(CpuPmObservationFailure.AdmissionDenied, CpuQueryFailureCode.AdmissionDenied,
                operation: code, admissionFailure: exception.Failure);
        }
        catch (Exception exception) when (IsBoundedFailure(exception))
        {
            wire.Abort();
            var detail = CpuQueryDiagnostic.FromException(CpuQueryStage.Approval, exception);
            throw new Failure(CpuPmObservationFailure.NativeFailure, detail.Failure, detail.NativeErrorCode, code);
        }
        catch { wire.Abort(); throw; }
        if (readingCapAddress != 0 && code == Read && reply.Bytes.Length == 24
            && reply.Bytes.AsSpan(0, 20).SequenceEqual(input))
        {
            RetainCap(new(readingCapAddress, Word(reply.Bytes, 20), clock.UtcNow,
                clock.MonotonicTimestamp, reply.Success, false));
        }
        if (readingCapabilityArgument && reply.Bytes.Length == 24 && reply.Bytes.AsSpan(0, 20).SequenceEqual(input) && capability is not null)
        { capability = capability with { RawValue = Word(reply.Bytes, 20), CapturedUtc = clock.UtcNow, MonotonicTimestamp = clock.MonotonicTimestamp, ReturnedWordTransportSucceeded = reply.Success }; }
        if (!wire.AcceptReply(code, input, outputLength, reply))
        {
            throw new Failure(!reply.Success ? reply.Failure == CpuQueryFailureCode.AdmissionDenied
                    ? CpuPmObservationFailure.AdmissionDenied : CpuPmObservationFailure.NativeFailure
                : readingVersionArgument && code == Read && reply.Bytes.Length == 24 && Word(input, 12) == 0xC8
                    && (readingFirmwareArgument
                        ? Word(reply.Bytes, 20) != CpuPmObservationWireAdmission.ExpectedFirmwareVersion
                        : !contract.AcceptsGeometry(contract.EnabledCoreMask, Word(reply.Bytes, 20), native.PmLength(Word(reply.Bytes, 20))))
                    ? CpuPmObservationFailure.UnsupportedVersion : CpuPmObservationFailure.ReplyRejected,
                reply.Failure, reply.NativeErrorCode, code);
        }
        CheckTransportBoundary(token);
        return (byte[])reply.Bytes.Clone();
    }
    private void RetainCap(CpuPmCapRegisterRead read)
    {
        if (read.Address == CpuLivePowerFusedCap.RegisterA0F) { capA0F = read; }
        else if (read.Address == CpuLivePowerFusedCap.RegisterA0B) { capA0B = read; }
        else if (read.Address == CpuLivePowerRangeObservation.RegisterA13) { capA13 = read; }
        else if (read.Address == CpuLivePowerRangeObservation.RegisterA90) { capA90 = read; }
    }
    private sealed class Failure(CpuPmObservationFailure code, CpuQueryFailureCode? nativeFailure = null,
        int? nativeErrorCode = null, uint? operation = null, CpuPmAdmissionFailure? admissionFailure = null) : Exception
    {
        internal CpuPmObservationFailure Code { get; } = code;
        internal CpuQueryFailureCode? NativeFailure { get; } = nativeFailure;
        internal int? NativeErrorCode { get; } = nativeErrorCode;
        internal uint? Operation { get; } = operation;
        internal CpuPmAdmissionFailure? AdmissionFailure { get; } = admissionFailure;
    }
    private static bool IsBoundedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException
        or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException;
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static byte[] Words(params uint[] values)
    { byte[] bytes = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) { Put(bytes, i * 4, values[i]); } return bytes; }
}
