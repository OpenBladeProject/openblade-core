using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuPmObservationTests
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;

    [Fact]
    public void ThreeCopiesEachRequireCompletedVersionAndRefreshAndPostCopySelection()
    {
        var guard = Discover();
        for (int i = 0; i < 3; i++)
        {
            Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
            Transaction(guard, 0x65, 3, 0xABCDEF); // The returned word has no proven equality-to-3 contract.
            Exchange(guard, CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, CopyReply());
            Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
        }
        Assert.Equal(3, guard.CopiesInvoked);
        Assert.False(guard.Allows(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CopyCannotPrecedeCompletedRefreshArgumentRead(int completedStage)
    {
        var guard = Discover();
        if (completedStage > 0) { Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version); }
        if (completedStage > 1) { BeginTransaction(guard, 0x65, 3); Poll(guard, 1); }
        Assert.False(guard.Allows(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54));
        Assert.True(guard.IsPoisoned);
    }

    [Fact]
    public void CopyInvalidatesSelectedRegisterAndCannotReuseRefresh()
    {
        var guard = Ready();
        Exchange(guard, CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, CopyReply());
        Assert.False(guard.Allows(Read, Pci(0, 0, 0xC8), 24));
        Assert.True(guard.IsPoisoned);
        guard = Ready();
        Exchange(guard, CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, CopyReply());
        Assert.False(guard.Allows(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54));
    }

    [Theory]
    [InlineData(0x005D000A)]
    [InlineData(0x005D000C)]
    [InlineData(0)]
    public void WrongVersionAtEitherBoundaryPermanentlyPoisons(uint version)
    {
        var guard = Discover();
        AssertBadVersion(guard, version);
        guard = Ready();
        Exchange(guard, CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, CopyReply());
        AssertBadVersion(guard, version);
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(0x65, 0)]
    [InlineData(0x66, 0)]
    [InlineData(0x32, 0)]
    [InlineData(0x33, 0)]
    [InlineData(0x5E, 0)]
    public void FixedMailboxCommandArgumentPairsRejectOthers(uint command, uint argument)
    {
        var guard = Discover();
        Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
        PrepareTransaction(guard, argument);
        Select(guard, Message);
        Assert.False(guard.Allows(Write, Words(1, command, 0), 0));
    }

    [Theory]
    [InlineData(false, 0xD54, 0xD50)]
    [InlineData(true, 0xD53, 0xD50)]
    [InlineData(true, 0xD55, 0xD50)]
    [InlineData(true, 0xD54, 0xD54)]
    public void FailedOrMalformedCopyConsumesPermitAndCannotRetryPartialCache(bool success, int length, uint echo)
    {
        var guard = Ready();
        byte[] input = Words(0xD50);
        Assert.True(guard.Allows(CpuPmObservationWireAdmission.Copy, input, 0xD54));
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, echo);
        Assert.False(guard.AcceptReply(CpuPmObservationWireAdmission.Copy, input, 0xD54, new(success, bytes)));
        Assert.Equal(1, guard.CopiesInvoked);
        Assert.False(guard.Allows(CpuPmObservationWireAdmission.Copy, input, 0xD54));
        Assert.False(guard.Allows(Enumerate, new byte[28], 28));
    }

    [Theory]
    [InlineData(0xD54, 4, 0xD50)]
    [InlineData(0xD50, 4, 0xD50)]
    [InlineData(0xD54, 8, 0xD50)]
    [InlineData(0xD54, 4, 0)]
    public void CopyRequestUsesExactVendorLength(int output, int inputLength, uint requested)
    {
        var guard = Ready();
        byte[] input = new byte[inputLength];
        BinaryPrimitives.WriteUInt32LittleEndian(input, requested);
        bool expected = output == 0xD54 && inputLength == 4 && requested == 0xD50;
        Assert.Equal(expected, guard.Allows(CpuPmObservationWireAdmission.Copy, input, output));
    }

    [Fact]
    public void AllThreeIndependentPciIdsAreRequiredBeforeSmnAndWrongIdsPoison()
    {
        var guard = Discover(false);
        ReadWord(guard, Pci(0, 0, 0), CpuQueryPciTopology.RootId);
        ReadWord(guard, Pci(0x18, 0, 0), CpuQueryPciTopology.NodeId);
        Assert.False(guard.Allows(Write, Words(0, Response, 0), 0));
        guard = Discover(false);
        byte[] request = Pci(0, 0, 0);
        Assert.True(guard.Allows(Read, request, 24));
        Assert.False(guard.AcceptReply(Read, request, 24, new(true, ReadReply(request, CpuQueryPciTopology.NodeId))));
    }

    [Fact]
    public void PendingRequestAndChangedReplyInputCannotInterleaveOrBypassValidation()
    {
        var guard = Discover();
        byte[] request = Words(0, Response, 0);
        Assert.True(guard.Allows(Write, request, 0));
        request[4] ^= 1;
        Assert.False(guard.AcceptReply(Write, request, 0, new(true, [])));
        guard = Discover();
        Assert.True(guard.Allows(Write, Words(0, Response, 0), 0));
        Assert.False(guard.Allows(Read, Pci(0, 0, 0), 24));
    }

    [Fact]
    public void NonSuccessRefreshAndPollingExhaustionPermanentlyStop()
    {
        var guard = Discover();
        Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
        BeginTransaction(guard, 0x65, 3);
        Select(guard, Response);
        byte[] request = Pci(0, 0, 0xC8);
        Assert.True(guard.Allows(Read, request, 24));
        Assert.False(guard.AcceptReply(Read, request, 24, new(true, ReadReply(request, 2))));
        Assert.False(guard.Allows(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54));
        guard = Discover();
        BeginTransaction(guard, 6, 0);
        Select(guard, Response);
        for (int i = 0; i < 1000; i++) { ReadWord(guard, request, 0); }
        Assert.False(guard.Allows(Read, request, 24));
    }

    [Fact]
    public void AbortStopsPendingTransactionWithoutRetry()
    {
        var guard = Ready();
        Assert.True(guard.Allows(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54));
        guard.Abort();
        Assert.False(guard.AcceptReply(CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, new(true, CopyReply())));
        Assert.False(guard.Allows(Read, Pci(0, 0, 0), 24));
    }

    [Fact]
    public void RawSnapshotIsDefensivelyCopiedAndPreservesExactFloatBitsWithoutArbitraryLimits()
    {
        byte[] bytes = CopyReply();
        uint[] bits = [0, 0x80000000, 0x7F7FFFFF, 0xFF7FFFFF, 1, 0x3F800000];
        for (int i = 0; i < bits.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + i * 4), bits[i]); }
        Assert.True(CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(true, bytes),
            DateTimeOffset.UnixEpoch, 123, out var snapshot));
        Assert.Equal(CpuPmRawSnapshotValidity.ValidCandidateObservation, snapshot!.Validity);
        Assert.Equal(bits, snapshot.CopyCandidates().Select(word => word.RawBits));
        string hash = snapshot.ReplySha256;
        bytes[4] = 99;
        snapshot.CopyReply()[4] = 99;
        snapshot.CopyPayload()[0] = 99;
        snapshot.CopyCandidates()[0] = default;
        Assert.Equal(0, snapshot.CopyPayload()[0]);
        Assert.Equal(hash, snapshot.ReplySha256);
        Assert.Equal(123, snapshot.MonotonicTimestamp);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.CapturedUtc);
    }

    [Theory]
    [InlineData(0u, (int)CpuPmRawSnapshotValidity.AllZeroCandidates)]
    [InlineData(0xFFFFFFFFu, (int)CpuPmRawSnapshotValidity.AllOnesCandidates)]
    [InlineData(0x7F800000u, (int)CpuPmRawSnapshotValidity.NonfiniteCandidates)]
    [InlineData(0x7FC00001u, (int)CpuPmRawSnapshotValidity.NonfiniteCandidates)]
    public void InvalidOfflineTelemetryRetainsRawEvidence(uint bits, int validity)
    {
        byte[] bytes = CopyReply();
        for (int i = 0; i < 6; i++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + i * 4), bits); }
        Assert.True(CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(true, bytes), default, 0, out var snapshot));
        Assert.Equal((CpuPmRawSnapshotValidity)validity, snapshot!.Validity);
        Assert.Equal(bytes, snapshot.CopyReply());
    }

    [Fact]
    public void CompletedRefreshCannotBeReplacedAndPostVersionCannotStandInForNextPreVersion()
    {
        var guard = Ready();
        Select(guard, Response);
        ReadWord(guard, Pci(0, 0, 0xC8), 1);
        Assert.False(guard.Allows(Write, Words(1, 0, 0), 0));
        guard = Ready();
        Exchange(guard, CpuPmObservationWireAdmission.Copy, Words(0xD50), 0xD54, CopyReply());
        Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
        PrepareTransaction(guard, 3);
        Select(guard, Message);
        Assert.False(guard.Allows(Write, Words(1, 0x65, 0), 0));
    }

    [Fact]
    public void SnapshotRejectsUnsupportedVersionAndMalformedTransportWithoutDecoding()
    {
        Assert.False(CpuPmRawSnapshot.TryCreate(0x005D000C, new(true, CopyReply()), default, 0, out var snapshot));
        Assert.Null(snapshot);
        Assert.False(CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(false, CopyReply()), default, 0, out snapshot));
        Assert.False(CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(true, new byte[0xD53]), default, 0, out snapshot));
        byte[] wrongEcho = CopyReply();
        wrongEcho[0] = 0;
        Assert.False(CpuPmRawSnapshot.TryCreate(CpuPmObservationWireAdmission.Version, new(true, wrongEcho), default, 0, out snapshot));
        Assert.Null(snapshot);
    }

    private static CpuPmObservationWireAdmission Ready()
    {
        var guard = Discover();
        Transaction(guard, 6, 0, CpuPmObservationWireAdmission.Version);
        Transaction(guard, 0x65, 3, 42);
        return guard;
    }
    private static CpuPmObservationWireAdmission Discover(bool confirm = true)
    {
        var guard = new CpuPmObservationWireAdmission();
        byte[] count = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(24), 3);
        Exchange(guard, Enumerate, new byte[28], 28, count);
        byte[] input = new byte[28 + 3 * 52];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(24), 3);
        byte[] records = (byte[])input.Clone();
        uint[][] nodes = [[0, 0, 0, CpuQueryPciTopology.RootId], [0, 0x18, 0, CpuQueryPciTopology.NodeId], [0, 0x18, 1, CpuQueryPciTopology.OrientationId]];
        for (int i = 0; i < nodes.Length; i++) { Words(nodes[i]).CopyTo(records, 28 + i * 52); }
        Exchange(guard, Enumerate, input, input.Length, records);
        if (confirm)
        {
            ReadWord(guard, Pci(0, 0, 0), CpuQueryPciTopology.RootId);
            ReadWord(guard, Pci(0x18, 0, 0), CpuQueryPciTopology.NodeId);
            ReadWord(guard, Pci(0x18, 1, 0), CpuQueryPciTopology.OrientationId);
        }
        return guard;
    }
    private static void AssertBadVersion(CpuPmObservationWireAdmission guard, uint version)
    {
        BeginTransaction(guard, 6, 0);
        Poll(guard, 1);
        Select(guard, Argument);
        byte[] request = Pci(0, 0, 0xC8);
        Assert.True(guard.Allows(Read, request, 24));
        Assert.False(guard.AcceptReply(Read, request, 24, new(true, ReadReply(request, version))));
        Assert.True(guard.IsPoisoned);
    }
    private static void Transaction(CpuPmObservationWireAdmission guard, uint command, uint argument, uint result)
    {
        BeginTransaction(guard, command, argument);
        Poll(guard, 1);
        Select(guard, Argument);
        ReadWord(guard, Pci(0, 0, 0xC8), result);
    }
    private static void BeginTransaction(CpuPmObservationWireAdmission guard, uint command, uint argument)
    {
        PrepareTransaction(guard, argument);
        Select(guard, Message);
        Exchange(guard, Write, Words(1, command, 0), 0, []);
    }
    private static void PrepareTransaction(CpuPmObservationWireAdmission guard, uint argument)
    {
        Poll(guard, 1);
        Exchange(guard, Write, Words(1, 0, 0), 0, []);
        Select(guard, Argument);
        Exchange(guard, Write, Words(1, argument, 0), 0, []);
    }
    private static void Poll(CpuPmObservationWireAdmission guard, uint value)
    { Select(guard, Response); ReadWord(guard, Pci(0, 0, 0xC8), value); }
    private static void Select(CpuPmObservationWireAdmission guard, uint address) => Exchange(guard, Write, Words(0, address, 0), 0, []);
    private static void ReadWord(CpuPmObservationWireAdmission guard, byte[] request, uint value) => Exchange(guard, Read, request, 24, ReadReply(request, value));
    private static void Exchange(CpuPmObservationWireAdmission guard, uint code, byte[] input, int output, byte[] reply)
    { Assert.True(guard.Allows(code, input, output)); Assert.True(guard.AcceptReply(code, input, output, new(true, reply))); }
    private static byte[] ReadReply(byte[] input, uint value)
    { byte[] reply = new byte[24]; input.CopyTo(reply, 0); BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(20), value); return reply; }
    private static byte[] Pci(uint device, uint function, uint offset) => Words(0, device, function, offset, 4);
    private static byte[] Words(params uint[] values)
    { byte[] bytes = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]); } return bytes; }
    private static byte[] CopyReply()
    { byte[] bytes = new byte[0xD54]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xD50); return bytes; }
}
