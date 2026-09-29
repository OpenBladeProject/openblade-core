using System.Buffers.Binary;
using System.Numerics;

namespace OpenBlade.Protocol;

internal sealed record CpuQueryIoctlReply(bool Success, byte[] Bytes);

// Only an admitted service-owned adapter may implement this boundary. No Windows
// adapter or automatic driver provisioning is supplied by the query engine.
internal interface IRz090528CpuQueryIoctl
{
    CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength);
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
    Rz090528CpuLiveObservation? Observation);

/// <summary>
/// Query-only Strix wire engine. Production admission, CPU family/build/signature
/// checks, running-driver checks and competing-controller checks belong to the
/// service. Invoke on one worker thread; this bounded synchronous engine is not
/// wired to native access and has no default adapter.
/// Mailbox queries require PCI transport writes; they are not passive inspection.
/// Saved configuration and PPT limits are deliberately absent from the result.
/// </summary>
internal sealed class Rz090528CpuLiveQuery(
    IRz090528CpuQueryIoctl ioctl, IRz090528CpuQuerySynchronization synchronization)
{
    private const uint EnumeratePci = 0x81112F18;
    private const uint ReadPci = 0x81112F24;
    private const uint WriteSmnRegister = 0x81112FF8;
    private const uint ResponseAddress = 0x03B10A80;
    private const uint ArgumentAddress = 0x03B10A88;
    private const uint MessageAddress = 0x03B10A20;
    private const int MaximumPciRecords = 256 * 32 * 8;
    private const int MaximumPolls = 1000;

    internal Rz090528CpuLiveQueryResult Query(DeviceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (identity.Model != "RZ09-0528" || identity.VendorId != 0x1532 || identity.ProductId != 0x02C6)
        {
            return new(CpuLiveQueryStatus.UnsupportedIdentity, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var lease = synchronization.Acquire(TimeSpan.FromSeconds(20), cancellationToken);
        if (lease is null)
        {
            return new(CpuLiveQueryStatus.Busy, null);
        }

        try
        {
            var bus = DiscoverBus(cancellationToken);
            if (ReadPciWord(bus, 0, 0, 0, cancellationToken) != 0x16F81022)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }
            // The exact SDK obtains orientation from this BDF. Confirm its vendor
            // independently, then compare the orientation to the separate fuse mask.
            if ((ReadPciWord(0, 0x18, 1, 0, cancellationToken) & 0xFFFF) != 0x1022)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            var mask = ReadPciWord(0, 0x18, 1, 0x140, cancellationToken);
            var fusedMask = ~(ReadSmn(bus, 0x03820AB0, cancellationToken) >> 5) & 0xFFFF;
            if ((mask & 0xFFFF0000) != 0 || BitOperations.PopCount(mask) != 12 || mask != fusedMask)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            var version = QueryMailbox(bus, QueryCommand.PmVersion, 0, cancellationToken);
            var length = version switch
            {
                0x005D0008 => 0xD54,
                0x005D0009 => 0xD58,
                0x005D000A => 0xD60,
                0x005D000B => 0xD50,
                0x005D000C => 0xD68,
                _ => throw new QueryFailure(CpuLiveQueryStatus.UnsupportedVersion),
            };
            var offsets = new List<CpuLiveCoreOffset>(12);
            for (var physicalId = 0; physicalId < 16; physicalId++)
            {
                if ((mask & (1u << physicalId)) == 0)
                {
                    continue;
                }

                var argument = (uint)(((physicalId / 8) << 8) | (physicalId % 8)) << 20;
                var value = QueryMailbox(bus, QueryCommand.CoreOffset, argument, cancellationToken);
                offsets.Add(new(physicalId, unchecked((short)(value & 0xFFFF))));
            }
            return new(CpuLiveQueryStatus.Observed, new(mask, version, length, offsets.AsReadOnly()));
        }
        catch (QueryFailure failure) { return new(failure.Status, null); }
    }

    private uint DiscoverBus(CancellationToken cancellationToken)
    {
        var first = Exchange(EnumeratePci, new byte[28], 28, cancellationToken);
        var count = Word(first, 24);
        if (count == 0 || count > MaximumPciRecords || !HeaderIsZero(first))
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        var request = new byte[checked(28 + (int)count * 52)];
        Put(request, 24, count);
        var records = Exchange(EnumeratePci, request, request.Length, cancellationToken);
        if (Word(records, 24) != count || !HeaderIsZero(records))
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        uint? selected = null;
        for (var index = 0; index < count; index++)
        {
            var offset = 28 + (int)index * 52;
            var bus = Word(records, offset);
            var device = Word(records, offset + 4);
            var function = Word(records, offset + 8);
            if (bus > 255 || device > 31 || function > 7)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            if (Word(records, offset + 12) != 0x16F81022)
            {
                continue;
            }

            if (selected.HasValue || device != 0 || function != 0)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            selected = bus;
        }
        return selected ?? throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
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

    // No caller-supplied raw message API: only the two statically verified getters.
    private enum QueryCommand : uint { PmVersion = 0x06, CoreOffset = 0x5E }

    private uint QueryMailbox(uint bus, QueryCommand command, uint argument,
        CancellationToken cancellationToken)
    {
        WriteRegister(bus, 0, ResponseAddress, cancellationToken);
        uint previousResponse = 0;
        for (var poll = 0; poll < MaximumPolls; poll++)
        {
            previousResponse = ReadPciWord(bus, 0, 0, 0xC8, cancellationToken);
            if (previousResponse != 0) { break; }
            synchronization.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
        }
        if (previousResponse == 0)
        {
            throw new QueryFailure(CpuLiveQueryStatus.Busy);
        }

        if (previousResponse != 1)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
        }

        WriteSmn(bus, ResponseAddress, 0, cancellationToken);
        WriteSmn(bus, ArgumentAddress, argument, cancellationToken);
        WriteSmn(bus, MessageAddress, (uint)command, cancellationToken);
        // Select once; all subsequent data reads are protected by the same lease.
        WriteRegister(bus, 0, ResponseAddress, cancellationToken);
        for (var poll = 0; poll < MaximumPolls; poll++)
        {
            var response = ReadPciWord(bus, 0, 0, 0xC8, cancellationToken);
            if (response == 1)
            {
                return ReadSmn(bus, ArgumentAddress, cancellationToken);
            }

            if (response != 0)
            {
                throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
            }

            synchronization.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
        }
        throw new QueryFailure(CpuLiveQueryStatus.Busy);
    }

    private byte[] Exchange(uint code, byte[] input, int outputLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = ioctl.Invoke(code, input, outputLength);
        if (!reply.Success)
        {
            throw new QueryFailure(CpuLiveQueryStatus.Unavailable);
        }

        if (reply.Bytes.Length != outputLength)
        {
            throw new QueryFailure(CpuLiveQueryStatus.InvalidData);
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
    private sealed class QueryFailure(CpuLiveQueryStatus status) : Exception { internal CpuLiveQueryStatus Status { get; } = status; }
}
