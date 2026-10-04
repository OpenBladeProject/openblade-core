using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OpenBlade.Protocol;

// Separate admission capability: never attach this guard to the 06/5E query path.
// Its caller must establish the exact attended identity and a single mutex lease.
internal sealed class CpuPmObservationWireAdmission(CpuPmObservationProfile profile = CpuPmObservationProfile.IdleThreeSamples)
{
    private int CapOperations => profile == CpuPmObservationProfile.RangeSingleSample ? 8 : 4;
    private int MaximumCopies => profile == CpuPmObservationProfile.RangeSingleSample ? 1 : 3;
    private bool capabilityConfirmed, firmwareConfirmed;
    internal const uint ExpectedFirmwareVersion = 0x0B5D1600;
    internal const uint Copy = 0x81112F08, Version = 0x005D000B;
    internal const int PayloadLength = 0xD50, ReplyLength = PayloadLength + 4;
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Fuse = 0x03820AB0, Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private uint count, selected, argument, command;
    private uint? bus;
    private bool enumerated, failed, root, node, orientation, ready, cleared, argumentWritten;
    private bool versionConfirmed, copyPermit, postVersionRequired;
    private bool orientationMaskConfirmed, fuseMaskConfirmed;
    private int capProgress;
    private uint capA0F;
    private int copies, polls, operations;
    private (uint Code, byte[] Input, int Output)? pending;

    internal bool IsPoisoned => failed;
    internal int CopiesInvoked => copies;
    internal void Abort() { failed = true; pending = null; copyPermit = false; }

    internal bool Allows(uint code, byte[] input, int outputLength)
    {
        if (failed || pending.HasValue || ++operations > 20000 || !AllowsCore(code, input, outputLength))
        { Abort(); return false; }
        pending = (code, (byte[])input.Clone(), outputLength);
        if (code == Copy)
        {
            // Consume before entering the driver, including a failed/partial cache fill.
            copies++;
            copyPermit = false;
            versionConfirmed = false;
            postVersionRequired = true;
            InvalidateMailbox();
        }
        return true;
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
        // Once the exact masks are confirmed, no other operation can interrupt
        // the fixed A0F/A0B sequence (then A13/A90 for the range profile).
        if (fuseMaskConfirmed && capProgress < CapOperations)
        {
            if (capProgress % 2 == 0)
            {
                return code == Write && input.Length == 12 && outputLength == 0
                    && Word(input, 0) == 0 && Word(input, 8) == bus
                    && Word(input, 4) == (capProgress switch { 0 => CpuLivePowerFusedCap.RegisterA0F, 2 => CpuLivePowerFusedCap.RegisterA0B, 4 => CpuLivePowerRangeObservation.RegisterA13, _ => CpuLivePowerRangeObservation.RegisterA90 });
            }
            return code == Read && input.Length == 20 && outputLength == 24
                && Word(input, 0) == bus && Word(input, 4) == 0 && Word(input, 8) == 0
                && Word(input, 12) == 0xC8 && Word(input, 16) == 4;
        }
        if (code == Copy)
        {
            return root && node && orientation && versionConfirmed && copyPermit && copies < MaximumCopies
                && input.Length == 4 && Word(input, 0) == PayloadLength && outputLength == ReplyLength;
        }
        if (code == Read)
        {
            if (input.Length != 20 || outputLength != 24 || Word(input, 16) != 4) { return false; }
            uint b = Word(input, 0), d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
            if (offset == 0)
            {
                return b == bus && d == 0 && f == 0 || b == bus && d == 0x18 && f == 0
                    || b == 0 && d == 0x18 && f == 1;
            }
            if (!root || !node || !orientation) { return false; }
            if (b == 0 && d == 0x18 && f == 1 && offset == 0x140) { return command == 0 && !orientationMaskConfirmed; }
            return b == bus && d == 0 && f == 0 && offset == 0xC8
                && (selected == Fuse && command == 0 && orientationMaskConfirmed && !fuseMaskConfirmed || selected == Response && capProgress == CapOperations && ++polls <= 1000
                    || selected == Argument && command != 0 && ready);
        }
        if (code != Write || input.Length != 12 || outputLength != 0 || Word(input, 8) != bus
            || !root || !node || !orientation) { return false; }
        uint index = Word(input, 0), value = Word(input, 4);
        if (index == 0) { return value == Fuse ? orientationMaskConfirmed && !fuseMaskConfirmed : capProgress == CapOperations && value is Response or Argument or Message; }
        if (index != 1) { return false; }
        return selected switch
        {
            Response => value == 0 && ready && command == 0 && !copyPermit,
            Argument => cleared && !argumentWritten && value is 0 or 3,
            Message => cleared && argumentWritten && command == 0
                && (value == 2 && argument == 0 && profile == CpuPmObservationProfile.RangeSingleSample && !firmwareConfirmed && copies == 0 && !capabilityConfirmed
                    || value == 0x82 && argument == 0 && profile == CpuPmObservationProfile.RangeSingleSample && firmwareConfirmed && !capabilityConfirmed && copies == 0 && !versionConfirmed
                    || value == 6 && argument == 0 && !copyPermit && (profile == CpuPmObservationProfile.IdleThreeSamples || capabilityConfirmed && !versionConfirmed && (copies == 0 || postVersionRequired))
                    || value == 0x65 && argument == 3 && versionConfirmed && copies < MaximumCopies && !copyPermit),
            _ => false,
        };
    }

    internal bool AcceptReply(uint code, byte[] input, int outputLength, CpuQueryIoctlReply reply)
    {
        if (failed || pending is not { } request || request.Code != code || request.Output != outputLength
            || !request.Input.AsSpan().SequenceEqual(input) || !reply.Success || reply.Bytes.Length != outputLength)
        { Abort(); return false; }
        pending = null;
        if (code == Copy)
        {
            if (Word(reply.Bytes, 0) != PayloadLength) { Abort(); return false; }
        }
        else if (code == Enumerate)
        {
            uint received = Word(reply.Bytes, 24);
            if (reply.Bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0 || received == 0 || received > 65536)
            { Abort(); return false; }
            if (count == 0) { count = received; return true; }
            if (received != count || !CpuQueryPciTopology.TryFindBus(reply.Bytes, out uint found))
            { Abort(); return false; }
            enumerated = true;
            bus = found;
        }
        else if (code == Read)
        {
            if (!reply.Bytes.AsSpan(0, 20).SequenceEqual(input)) { Abort(); return false; }
            uint value = Word(reply.Bytes, 20);
            if (fuseMaskConfirmed && capProgress < CapOperations && capProgress % 2 == 1)
            {
                if (value == uint.MaxValue) { Abort(); return false; }
                if (capProgress == 1) { capA0F = value; }
                else if (capProgress == 3 && CpuLivePowerFusedCap.TryDecode(capA0F, value) is null) { Abort(); return false; }
                capProgress++;
            }
            else if (Word(input, 12) == 0x140)
            {
                if (value != 0xFF55) { Abort(); return false; }
                orientationMaskConfirmed = true;
            }
            else if (Word(input, 12) == 0xC8 && selected == Fuse)
            {
                if ((~(value >> 5) & 0xFFFF) != 0xFF55) { Abort(); return false; }
                fuseMaskConfirmed = true;
            }
            else if (Word(input, 12) == 0)
            {
                uint d = Word(input, 4), f = Word(input, 8);
                if (d == 0) { root = value == CpuQueryPciTopology.RootId; }
                else if (f == 0) { node = value == CpuQueryPciTopology.NodeId; }
                else { orientation = value == CpuQueryPciTopology.OrientationId; }
                if (!(d == 0 ? root : f == 0 ? node : orientation)) { Abort(); return false; }
            }
            else if (Word(input, 12) == 0xC8 && selected == Response)
            {
                if (command != 0 && value > 1) { Abort(); return false; }
                ready = command == 0 ? value != 0 : value == 1;
            }
            else if (Word(input, 12) == 0xC8 && selected == Argument)
            {
                if (command == 2)
                {
                    if (value != ExpectedFirmwareVersion) { Abort(); return false; }
                    firmwareConfirmed = true;
                }
                else if (command == 6)
                {
                    if (value != Version) { Abort(); return false; }
                    versionConfirmed = !postVersionRequired;
                    postVersionRequired = false;
                }
                else if (command == 0x65) { copyPermit = true; }
                else if (command == 0x82) { capabilityConfirmed = true; }
                command = 0;
                cleared = argumentWritten = false;
                polls = 0;
            }
        }
        else if (code == Write)
        {
            uint value = Word(input, 4);
            if (Word(input, 0) == 0)
            {
                selected = value;
                if (fuseMaskConfirmed && capProgress < CapOperations && capProgress % 2 == 0) { capProgress++; }
            }
            else if (selected == Response) { cleared = true; ready = false; argumentWritten = false; polls = 0; }
            else if (selected == Argument) { argument = value; argumentWritten = true; }
            else if (selected == Message) { command = value; cleared = argumentWritten = ready = false; polls = 0; }
        }
        return true;
    }

    private void InvalidateMailbox()
    {
        selected = argument = command = 0;
        ready = cleared = argumentWritten = false;
        polls = 0;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}

internal enum CpuPmRawSnapshotValidity { ValidCandidateObservation, AllZeroCandidates, AllOnesCandidates, NonfiniteCandidates }
internal readonly record struct CpuPmCandidateWord(int Offset, uint RawBits, float Value);

// Private evidence only. Candidate offsets are upstream labels, never confirmed
// PSMU 32/33 targets, freshness, saved configuration, or a PPT restoration contract.
internal sealed class CpuPmRawSnapshot
{
    private readonly byte[] reply;
    private readonly byte[] payload;
    private readonly CpuPmCandidateWord[] candidates;
    private CpuPmRawSnapshot(byte[] bytes, DateTimeOffset capturedUtc, long monotonicTimestamp)
    {
        reply = (byte[])bytes.Clone();
        payload = reply.AsSpan(4).ToArray();
        CapturedUtc = capturedUtc;
        MonotonicTimestamp = monotonicTimestamp;
        ReplySha256 = Convert.ToHexString(SHA256.HashData(reply));
        PayloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
        candidates = Enumerable.Range(0, 6).Select(index =>
        {
            int offset = index * 4;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
            return new CpuPmCandidateWord(offset, bits, BitConverter.UInt32BitsToSingle(bits));
        }).ToArray();
        Validity = candidates.All(word => word.RawBits == 0) ? CpuPmRawSnapshotValidity.AllZeroCandidates
            : candidates.All(word => word.RawBits == uint.MaxValue) ? CpuPmRawSnapshotValidity.AllOnesCandidates
            : candidates.Any(word => !float.IsFinite(word.Value)) ? CpuPmRawSnapshotValidity.NonfiniteCandidates
            : CpuPmRawSnapshotValidity.ValidCandidateObservation;
    }
    internal DateTimeOffset CapturedUtc { get; }
    internal long MonotonicTimestamp { get; }
    internal string ReplySha256 { get; }
    internal string PayloadSha256 { get; }
    internal CpuPmRawSnapshotValidity Validity { get; }
    internal byte[] CopyReply() => (byte[])reply.Clone();
    internal byte[] CopyPayload() => (byte[])payload.Clone();
    internal CpuPmCandidateWord[] CopyCandidates() => (CpuPmCandidateWord[])candidates.Clone();
    internal static bool TryCreate(uint version, CpuQueryIoctlReply reply, DateTimeOffset capturedUtc,
        long monotonicTimestamp, out CpuPmRawSnapshot? snapshot)
    {
        snapshot = null;
        if (version != CpuPmObservationWireAdmission.Version || !reply.Success
            || reply.Bytes.Length != CpuPmObservationWireAdmission.ReplyLength
            || BinaryPrimitives.ReadUInt32LittleEndian(reply.Bytes) != CpuPmObservationWireAdmission.PayloadLength)
        { return false; }
        snapshot = new CpuPmRawSnapshot(reply.Bytes, capturedUtc, monotonicTimestamp);
        return true;
    }
}
