using System.Buffers.Binary;
using System.Numerics;

namespace OpenBlade.Protocol;

internal sealed record CpuQueryIoctlReply(bool Success, byte[] Bytes,
    CpuQueryFailureCode? Failure = null, int? NativeErrorCode = null);

// Bounded attended-probe diagnostics only. Never include exception messages,
// paths, hardware identifiers or raw request/reply payloads in this record.
internal enum CpuQueryStage
{
    Approval, InitialUsbIdentity, MutexCreate, MutexWait, BackendFilePins,
    HostIdentity, ProcessorIdentity, FreshUsbIdentity, ControllerPresence,
    InstalledBackend, DeviceOpen, PciCount, PciRecords, PciIdentity,
    CoreMask, FuseMask, PmVersion, CoreOffset, Cleanup,
}
internal enum CpuQueryFailureCode
{
    AdmissionDenied, Timeout, Win32Error, AccessDenied, IoError, InvalidOperation,
    SecurityError, CryptographicError, JsonError, NativeIoctlFailed,
    WireRequestRejected, WireReplyRejected, ReplyLengthMismatch, InvalidData,
    UnsupportedVersion,
}
internal enum CpuQueryOperation { PciEnumeration, PciRead, SmnWrite }
internal sealed record CpuQueryDiagnostic(CpuQueryStage Stage, CpuQueryFailureCode Failure,
    int? NativeErrorCode = null, CpuQueryOperation? Operation = null)
{
    internal static CpuQueryDiagnostic FromException(CpuQueryStage stage, Exception exception,
        CpuQueryOperation? operation = null) => new(stage, exception switch
        {
            System.ComponentModel.Win32Exception => CpuQueryFailureCode.Win32Error,
            UnauthorizedAccessException => CpuQueryFailureCode.AccessDenied,
            IOException => CpuQueryFailureCode.IoError,
            System.Security.SecurityException => CpuQueryFailureCode.SecurityError,
            System.Security.Cryptography.CryptographicException => CpuQueryFailureCode.CryptographicError,
            System.Text.Json.JsonException => CpuQueryFailureCode.JsonError,
            _ => CpuQueryFailureCode.InvalidOperation,
        }, exception is System.ComponentModel.Win32Exception { NativeErrorCode: > 0 } native
            ? native.NativeErrorCode : null, operation);
}

// A separately attended Windows adapter implements this boundary. Production
// native invocation remains blocked; the engine never provisions a driver.
internal interface IRz090528CpuQueryIoctl
{
    CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength);
}

// Optional command-boundary admission. Transport guards remain on every IOCTL;
// a completed twelve-core vector never substitutes for its thirteen commands.
internal interface ICpuMailboxCommandAdmission
{
    bool CommandBoundaryCurrent();
}

internal interface IRz090528CpuQuerySynchronization
{
    // The adapter must acquire Global\AMDRyzenMasterMutex, not a process-local lock.
    // Query and lease disposal stay synchronous on one worker thread because
    // Windows mutex ownership is thread-affine. Delays must observe cancellation.
    IDisposable? Acquire(TimeSpan timeout, CancellationToken cancellationToken);
    void Delay(TimeSpan duration, CancellationToken cancellationToken);
}

internal enum CpuLiveQueryStatus { Observed, UnsupportedIdentity, Unavailable, InvalidData, Busy, UnsupportedVersion }
internal sealed record CpuLiveCoreOffset(int PhysicalCoreId, short Offset);
internal sealed record Rz090528CpuLiveObservation(uint EnabledCoreMask, uint PmVersion,
    int PmTableLength, IReadOnlyList<CpuLiveCoreOffset> CoreOffsets);
internal sealed record Rz090528CpuLiveQueryResult(CpuLiveQueryStatus Status,
    Rz090528CpuLiveObservation? Observation, CpuQueryDiagnostic? Diagnostic = null);

// The exact SDK selects the 16F8 node record but uses its bus for D0F0 SMN
// transport. On the admitted host, that root is 1507 and the fixed D18F1
// orientation source is 16F9, all on bus zero. These are distinct functions.
internal static class CpuQueryPciTopology
{
    internal const uint RootId = 0x15071022, NodeId = 0x16F81022, OrientationId = 0x16F91022;

    internal static bool TryFindBus(byte[] records, out uint bus)
    {
        bus = 0;
        if (records.Length < 28 || (records.Length - 28) % 52 != 0
            || records.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0
            || Word(records, 24) is 0 or > 65536
            || Word(records, 24) != (records.Length - 28) / 52) { return false; }
        bool root = false, node = false, orientation = false;
        var seen = new HashSet<uint>();
        for (int offset = 28; offset < records.Length; offset += 52)
        {
            uint b = Word(records, offset), d = Word(records, offset + 4), f = Word(records, offset + 8);
            if (b > 255 || d > 31 || f > 7 || !seen.Add((b << 8) | (d << 3) | f)) { return false; }
            uint id = Word(records, offset + 12);
            if (id == RootId)
            {
                if (root || b != 0 || d != 0 || f != 0) { return false; }
                root = true;
            }
            else if (id == NodeId)
            {
                if (node || b != 0 || d != 0x18 || f != 0) { return false; }
                node = true;
            }
            else if (id == OrientationId)
            {
                if (orientation || b != 0 || d != 0x18 || f != 1) { return false; }
                orientation = true;
            }
        }
        return root && node && orientation;
    }

    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}

/// <summary>
/// Query-only Strix wire engine. Production admission, CPU family/build/signature
/// checks, running-driver checks and competing-controller checks belong to the
/// service. Invoke on one worker thread; this bounded synchronous engine is
/// connected to native access only through a separately attended validation path.
/// Mailbox queries require PCI transport writes; they are not passive inspection.
/// Saved configuration and PPT limits are deliberately absent from the result.
/// </summary>
internal sealed class Rz090528CpuLiveQuery(
    IRz090528CpuQueryIoctl ioctl, IRz090528CpuQuerySynchronization synchronization, CpuQueryProgress? progress = null)
{
    internal Rz090528CpuLiveQueryResult Query(DeviceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (identity.Model != "RZ09-0528" || identity.VendorId != 0x1532 || identity.ProductId != 0x02C6)
        {
            return new(CpuLiveQueryStatus.UnsupportedIdentity, null,
                new(CpuQueryStage.InitialUsbIdentity, CpuQueryFailureCode.AdmissionDenied));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = synchronization.Acquire(TimeSpan.FromSeconds(20), cancellationToken);
        if (lease is null)
        {
            return new(CpuLiveQueryStatus.Busy, null,
                new(CpuQueryStage.MutexWait, CpuQueryFailureCode.Timeout));
        }

        var session = new Rz090528CpuMailboxSession(ioctl, synchronization, progress, allowPriorTerminalResponse: true);
        try { return new(CpuLiveQueryStatus.Observed, session.Observe(cancellationToken)); }
        catch (Rz090528CpuMailboxSession.QueryFailure failure)
        {
            return new(failure.Status, null, new(session.Stage, failure.Failure, failure.NativeErrorCode, failure.Operation));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            return new(CpuLiveQueryStatus.Unavailable, null, CpuQueryDiagnostic.FromException(session.Stage, exception));
        }
    }

}

// One synchronous, lease-owned session shared by getter and exact per-core CO operations.
// The raw PCI/SMN/mailbox helpers remain private.
internal sealed class Rz090528CpuMailboxSession(
    IRz090528CpuQueryIoctl ioctl, IRz090528CpuQuerySynchronization synchronization, CpuQueryProgress? progress = null,
    bool allowPriorTerminalResponse = false)
{
    private const uint EnumeratePci = 0x81112F18;
    private const uint ReadPci = 0x81112F24;
    private const uint WriteSmnRegister = 0x81112FF8;
    private const uint ResponseAddress = 0x03B10A80;
    private const uint ArgumentAddress = 0x03B10A88;
    private const uint MessageAddress = 0x03B10A20;
    private const int MaximumPciRecords = 256 * 32 * 8;
    private const int MaximumPolls = 1000;
    private CpuQueryStage stage;
    private uint? bus;
    internal CpuQueryStage Stage => stage;

    internal Rz090528CpuLiveObservation Observe(CancellationToken cancellationToken)
    {
        bus ??= DiscoverBus(cancellationToken);
        stage = CpuQueryStage.PciIdentity;
        if (ReadPciWord(bus.Value, 0, 0, 0, cancellationToken) != CpuQueryPciTopology.RootId
            || ReadPciWord(bus.Value, 0x18, 0, 0, cancellationToken) != CpuQueryPciTopology.NodeId)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }
        // The exact SDK obtains orientation from this BDF. Confirm its identity
        // independently, then compare the orientation to the separate fuse mask.
        if (ReadPciWord(0, 0x18, 1, 0, cancellationToken) != CpuQueryPciTopology.OrientationId)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        stage = CpuQueryStage.CoreMask;
        var mask = ReadPciWord(0, 0x18, 1, 0x140, cancellationToken);
        stage = CpuQueryStage.FuseMask;
        var fusedMask = ~(ReadSmn(bus.Value, 0x03820AB0, cancellationToken) >> 5) & 0xFFFF;
        if ((mask & 0xFFFF0000) != 0 || BitOperations.PopCount(mask) != 12 || mask != fusedMask)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        stage = CpuQueryStage.PmVersion;
        var version = QueryMailbox(bus.Value, QueryCommand.PmVersion, 0, cancellationToken);
        var length = version switch
        {
            0x005D0008 => 0xD54,
            0x005D0009 => 0xD58,
            0x005D000A => 0xD60,
            0x005D000B => 0xD50,
            0x005D000C => 0xD68,
            _ => throw new QueryFailure(CpuLiveQueryStatus.UnsupportedVersion),
        };
        stage = CpuQueryStage.CoreOffset;
        var offsets = new List<CpuLiveCoreOffset>(12);
        for (var physicalId = 0; physicalId < 16; physicalId++)
        {
            if ((mask & (1u << physicalId)) == 0)
            {
                continue;
            }

            var argument = (uint)(((physicalId / 8) << 8) | (physicalId % 8)) << 20;
            var value = QueryMailbox(bus.Value, QueryCommand.CoreOffset, argument, cancellationToken);
            offsets.Add(new(physicalId, unchecked((short)(value & 0xFFFF))));
        }
        return new(mask, version, length, offsets.AsReadOnly());
    }

    private uint DiscoverBus(CancellationToken cancellationToken)
    {
        stage = CpuQueryStage.PciCount;
        var first = Exchange(EnumeratePci, new byte[28], 28, cancellationToken);
        var count = Word(first, 24);
        if (count == 0 || count > MaximumPciRecords || !HeaderIsZero(first))
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        var request = new byte[checked(28 + (int)count * 52)];
        Put(request, 24, count);
        stage = CpuQueryStage.PciRecords;
        var records = Exchange(EnumeratePci, request, request.Length, cancellationToken);
        if (Word(records, 24) != count || !HeaderIsZero(records))
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        return CpuQueryPciTopology.TryFindBus(records, out uint bus)
            ? bus : throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
    }

    private static bool HeaderIsZero(byte[] bytes) => bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) < 0;

    private uint ReadPciWord(uint bus, uint device, uint function, uint offset, CancellationToken cancellationToken)
    {
        var request = Words(bus, device, function, offset, 4);
        var response = Exchange(ReadPci, request, 24, cancellationToken);
        if (!response.AsSpan(0, 20).SequenceEqual(request))
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        return Word(response, 20);
    }

    private void WriteRegister(uint bus, uint index, uint value, CancellationToken cancellationToken)
        => Exchange(WriteSmnRegister, Words(index, value, bus), 0, cancellationToken);

    private uint ReadSmn(uint bus, uint address, CancellationToken cancellationToken)
    {
        WriteRegister(bus, 0, address, cancellationToken);
        return ReadPciWord(bus, 0, 0, 0xC8, cancellationToken);
    }

    private void WriteSmn(uint bus, uint address, uint value, CancellationToken cancellationToken)
    {
        WriteRegister(bus, 0, address, cancellationToken);
        WriteRegister(bus, 1, value, cancellationToken);
    }

    // Typed entry points only. The distinct wire gate is required for capability/setter.
    internal uint ReadCurveCapability(CancellationToken token) =>
        QueryMailbox(bus ?? throw new InvalidOperationException("Discovery required."), QueryCommand.Capability, 0, token);

    internal void SubmitCurve(CpuLiveCurveCommand command, CancellationToken token) =>
        QueryMailbox(bus ?? throw new InvalidOperationException("Discovery required."), QueryCommand.SetCoreOffset, command.EncodeArgument(), token);
    private enum QueryCommand : uint { PmVersion = 0x06, CoreOffset = 0x5E, Capability = 0x82, SetCoreOffset = 0x53 }

    private uint QueryMailbox(uint bus, QueryCommand command, uint argument,
        CancellationToken cancellationToken) => command == QueryCommand.CoreOffset
        ? CpuObservationCosts.Measure(CpuObservationCost.GetCore0x5E,
            () => QueryMailboxCore(bus, command, argument, cancellationToken))
        : QueryMailboxCore(bus, command, argument, cancellationToken);

    private uint QueryMailboxCore(uint bus, QueryCommand command, uint argument,
        CancellationToken cancellationToken)
    {
        CheckCommandBoundary(cancellationToken);
        progress?.MailboxPhase(CpuQueryMailboxPhase.ReadinessWait);
        WriteRegister(bus, 0, ResponseAddress, cancellationToken);
        uint previousResponse = 0;
        for (var poll = 0; poll < MaximumPolls; poll++)
        {
            previousResponse = ReadPciWord(bus, 0, 0, 0xC8, cancellationToken);
            if (previousResponse != 0) { break; }
            progress?.MailboxZero(afterSubmission: false);
            using (progress?.Enter(CpuQueryBucket.MailboxPolling))
            { synchronization.Delay(TimeSpan.FromMilliseconds(1), cancellationToken); }
        }
        if (previousResponse == 0)
        {
            throw new QueryFailure(CpuLiveQueryStatus.Busy);
        }

        // Exact vendor getter readiness accepts any completed response; this is not
        // acknowledgement of the command about to be submitted. Setter sessions stay strict.
        if (previousResponse != 1 && !allowPriorTerminalResponse)
        {
            progress?.RejectMailboxResponse(previousResponse);
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        progress?.MailboxPhase(CpuQueryMailboxPhase.Preparation);
        WriteSmn(bus, ResponseAddress, 0, cancellationToken);
        progress?.MailboxTransportCompleted(submission: false);
        // Vendor capability82 has no input argument; preserve that exact shape.
        if (command != QueryCommand.Capability) { WriteSmn(bus, ArgumentAddress, argument, cancellationToken); }
        progress?.MailboxPhase(CpuQueryMailboxPhase.Submission);
        WriteSmn(bus, MessageAddress, (uint)command, cancellationToken);
        progress?.MailboxTransportCompleted(submission: true);
        progress?.MailboxPhase(CpuQueryMailboxPhase.ResponseWait);
        // Select once; all subsequent data reads are protected by the same lease.
        WriteRegister(bus, 0, ResponseAddress, cancellationToken);
        for (var poll = 0; poll < MaximumPolls; poll++)
        {
            var response = ReadPciWord(bus, 0, 0, 0xC8, cancellationToken);
            if (response == 1)
            {
                if (command != QueryCommand.SetCoreOffset) { progress?.MailboxPhase(CpuQueryMailboxPhase.ArgumentRead); }
                uint result = command == QueryCommand.SetCoreOffset ? 0 : ReadSmn(bus, ArgumentAddress, cancellationToken);
                CheckCommandBoundary(cancellationToken);
                return result;
            }

            if (response != 0)
            {
                progress?.RejectMailboxResponse(response);
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            progress?.MailboxZero(afterSubmission: true);
            using (progress?.Enter(CpuQueryBucket.MailboxPolling))
            { synchronization.Delay(TimeSpan.FromMilliseconds(1), cancellationToken); }
        }
        throw new QueryFailure(CpuLiveQueryStatus.Busy);
    }

    private void CheckCommandBoundary(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ioctl is ICpuMailboxCommandAdmission admission && !admission.CommandBoundaryCurrent())
        { throw new QueryFailure(CpuLiveQueryStatus.Unavailable, CpuQueryFailureCode.AdmissionDenied); }
        token.ThrowIfCancellationRequested();
    }

    private byte[] Exchange(uint code, byte[] input, int outputLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Boundary(stage, Operation(code));
        CpuQueryIoctlReply reply;
        try { reply = ioctl.Invoke(code, input, outputLength); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            var diagnostic = CpuQueryDiagnostic.FromException(stage, exception, Operation(code));
            throw new QueryFailure(CpuLiveQueryStatus.Unavailable,
                diagnostic.Failure, diagnostic.NativeErrorCode, diagnostic.Operation);
        }
        if (!reply.Success)
        {
            throw new QueryFailure(CpuLiveQueryStatus.Unavailable,
                reply.Failure ?? CpuQueryFailureCode.NativeIoctlFailed, reply.NativeErrorCode, Operation(code));
        }

        if (reply.Bytes.Length != outputLength)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData,
                CpuQueryFailureCode.ReplyLengthMismatch, operation: Operation(code));
        }

        return reply.Bytes;
    }

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var index = 0; index < words.Length; index++)
        {
            Put(bytes, index * 4, words[index]);
        }

        return bytes;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static CpuQueryOperation Operation(uint code) => code switch
    {
        EnumeratePci => CpuQueryOperation.PciEnumeration,
        ReadPci => CpuQueryOperation.PciRead,
        WriteSmnRegister => CpuQueryOperation.SmnWrite,
        _ => throw new InvalidOperationException("Unknown CPU query operation."),
    };

    internal sealed class QueryFailure(CpuLiveQueryStatus status, CpuQueryFailureCode? failure = null,
        int? nativeErrorCode = null, CpuQueryOperation? operation = null) : Exception
    {
        internal CpuLiveQueryStatus Status { get; } = status;
        internal CpuQueryFailureCode Failure { get; } = failure ?? status switch
        {
            CpuLiveQueryStatus.Busy => CpuQueryFailureCode.Timeout,
            CpuLiveQueryStatus.UnsupportedVersion => CpuQueryFailureCode.UnsupportedVersion,
            _ => CpuQueryFailureCode.InvalidData,
        };
        internal int? NativeErrorCode { get; } = nativeErrorCode;
        internal CpuQueryOperation? Operation { get; } = operation;
    }
}
