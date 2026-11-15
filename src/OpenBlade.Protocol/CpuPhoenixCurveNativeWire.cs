using System.Buffers.Binary;
using System.Diagnostics;

namespace OpenBlade.Protocol;

// An exact instruction sequence, with polling as its only variable repetition.
// This Phoenix contract is separate from the Strix PPT-setter admission. Every request
// is checked again at the actual native boundary, including echoed PCI replies,
// topology/masks, two version-bracketed PM copies and one exact CO setter.
internal sealed class CpuPhoenixCurveNativeWire : ICpuNativeWireAdmission
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8, Copy = 0x81112F08;
    private const uint Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private readonly CpuLiveCurveContract contract;
    private readonly CpuCurveNativeProfile profile;
    private readonly CpuLiveCurveNativeRequest curveRequest;
    private readonly Action checkpoint, snapshotAdmission, finalAdmission, commandCheckpoint;
    private readonly Action? afterSetter;
    private readonly Queue<Step> steps = new();
    private Step? pending;
    private bool failed, scriptCreated, waiting;
    private int operations;
    private uint bus;
    private CpuLivePowerSnapshot? copied;
    internal CpuLiveCurveNativeDisposition Disposition { get; private set; }
    internal uint? Capability { get; private set; }
    internal CpuLivePowerSnapshot? After { get; private set; }
    internal CpuLiveCurveSnapshot? CurveAfter { get; private set; }
    internal bool Complete => scriptCreated && !failed && pending is null && steps.Count == 0;

    internal CpuPhoenixCurveNativeWire(CpuLiveCurveNativeRequest curveRequest, Action checkpoint, Action snapshotAdmission,
        Action finalAdmission, Action afterSetter, Action? commandCheckpoint = null)
    {
        contract = curveRequest.Before.Contract;
        profile = CpuCurveNativeProfile.ForContract(contract);
        if (!contract.UsesPhoenixProtocol || curveRequest.Command.Contract != contract || curveRequest.PowerPeer is null)
        { throw new CpuPowerAdmissionException(); }
        this.curveRequest = curveRequest;
        this.checkpoint = checkpoint; this.snapshotAdmission = snapshotAdmission; this.finalAdmission = finalAdmission;
        this.afterSetter = afterSetter;
        this.commandCheckpoint = commandCheckpoint ?? checkpoint;
        steps.Enqueue(new(Enumerate, new byte[28], 28, bytes =>
        {
            uint count = Word(bytes, 24);
            if (!Header(bytes) || count is 0 or > 65536) { return false; }
            var input = new byte[checked(28 + (int)count * 52)]; Put(input, 24, count);
            steps.Enqueue(new(Enumerate, input, input.Length, records =>
            {
                if (!Header(records) || Word(records, 24) != count || !CpuQueryPciTopology.TryFindBus(records, profile, out bus)) { return false; }
                CreateScript(); return true;
            }));
            return true;
        }));
    }

    internal void Run(WindowsRz090528CpuQueryAdapter adapter, CancellationToken token)
    {
        while (steps.TryPeek(out var step))
        {
            token.ThrowIfCancellationRequested();
            if (!adapter.Invoke(step.Code, (byte[])step.Input.Clone(), step.Output).Success)
            { throw new IOException("Power transport did not complete."); }
            if (waiting) { adapter.Delay(TimeSpan.FromMilliseconds(1), token); }
        }
        if (!Complete) { throw new IOException("Power sequence incomplete."); }
    }

    public bool Allows(uint code, byte[] input, int outputLength)
    {
        if (failed || pending is not null || ++operations > 20000 || !steps.TryPeek(out var step)
            || code != step.Code || outputLength != step.Output || !input.AsSpan().SequenceEqual(step.Input)
            || step.Poll && ++step.Polls > 1000) { Abort(); return false; }
        pending = step; return true;
    }

    internal void BeforeNativeInvoke(uint code, byte[] input)
    {
        if (failed || pending is not { } step || step.Code != code || !input.AsSpan().SequenceEqual(step.Input))
        { throw new CpuPowerAdmissionException(); }
        checkpoint();
        if (step.CommandStart) { commandCheckpoint(); }
        if (!step.Setter) { return; }
        if (Disposition != CpuLiveCurveNativeDisposition.NotSubmitted) { throw new CpuPowerAdmissionException(); }
        finalAdmission();
        // Preparation and selection are not submission. This marker is written
        // immediately before the only actual device.Invoke of the CO setter.
        Disposition = CpuLiveCurveNativeDisposition.PossibleSubmission;
    }

    public bool AcceptReply(uint code, byte[] input, int outputLength, CpuQueryIoctlReply reply)
    {
        checkpoint();
        if (failed || pending is not { } step || step.Code != code || step.Output != outputLength
            || !input.AsSpan().SequenceEqual(step.Input) || !reply.Success || reply.Bytes.Length != outputLength
            || code == Read && !reply.Bytes.AsSpan(0, 20).SequenceEqual(input)) { Abort(); return false; }
        pending = null; waiting = false;
        if (step.Poll)
        {
            uint value = Word(reply.Bytes, 20);
            if (value > 1) { Abort(); return false; }
            if (value == 0) { waiting = true; return true; }
        }
        steps.Dequeue();
        if (step.Accept?.Invoke(reply.Bytes) == false) { Abort(); return false; }
        if (step.CommandEnd) { commandCheckpoint(); }
        if (step.Setter) { afterSetter?.Invoke(); }
        return true;
    }

    public void Abort() { failed = true; pending = null; }

    private void CreateScript()
    {
        scriptCreated = true;
        Pci(bus, 0, 0, 0, value => value == profile.RootId);
        Pci(bus, 0x18, 0, 0, value => value == profile.NodeId);
        Pci(0, 0x18, 1, 0, value => value == profile.OrientationId);
        Pci(0, 0x18, 1, 0x140, value => profile.DecodeOrientation(value) == contract.EnabledCoreMask);
        Smn(profile.FuseAddress, value => profile.DecodeFuse(value) == contract.EnabledCoreMask);
        CurveSnapshot(curveRequest.Before, after: false);
        Snapshot(before: true);
        Mailbox(profile.SetterMessage, profile.SetterArgument(curveRequest.Command), null, setter: true);
        CurveSnapshot(curveRequest.After, after: true);
        Snapshot(before: false);
    }
    private void CurveSnapshot(CpuLiveCurveSnapshot expected, bool after)
    {
        var values = new List<CpuLiveCoreOffset>();
        Mailbox(6, 0, value => contract.AcceptsGeometry(contract.EnabledCoreMask, value, profile.PmLength(value)));
        // The native getter checks fresh capability82 bit2 before every E1 vector.
        // No PPT setter fuse reads are part of this CO contract.
        Mailbox(0x82, null, value =>
        {
            if (value == uint.MaxValue || (value & 4) == 0) { return false; }
            Capability = value;
            if (!after) { snapshotAdmission(); }
            return true;
        });
        foreach (var core in expected.CoreOffsets)
        {
            uint argument = profile.GetterArgument(core.PhysicalCoreId);
            Mailbox(profile.GetterMessage, argument, value =>
            {
                short offset = unchecked((short)value);
                if (offset != core.Offset) { return false; }
                values.Add(new(core.PhysicalCoreId, offset));
                if (after && values.Count == expected.CoreOffsets.Count)
                {
                    CurveAfter = CpuLiveCurveSnapshot.TryCreate(contract, new(contract.EnabledCoreMask, 0x004C0008, 0xAF0, values));
                }
                return true;
            });
        }
    }

    private void Snapshot(bool before)
    {
        Mailbox(6, 0, value => value == 0x004C0008u);
        Mailbox(0x65, 3, _ => true);
        steps.Enqueue(new(Copy, Words(0xAF0), 0xAF0 + 4, bytes =>
        {
            if (!CpuPmRawSnapshot.TryCreate(contract, 0x004C0008u, new(true, bytes),
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), out var raw)) { return false; }
            copied = CpuLivePowerSnapshot.TryCreate(contract, raw);
            return copied is not null && (!before || copied.Pair == curveRequest.PowerPeer!.Expected);
        }, CommandStart: true, CommandEnd: true));
        // F08 can alter hidden mailbox/index state; always explicitly reselect.
        Mailbox(6, 0, value =>
        {
            if (value != 0x004C0008u || copied is null) { return false; }
            if (!before) { After = copied; }
            copied = null; return true;
        });
    }
    private void Mailbox(uint command, uint? argument, Func<uint, bool>? accept, bool setter = false)
    {
        Select(Response, commandStart: true); Poll(); Data(0);
        if (argument is uint value) { Select(Argument); Data(value); }
        Select(Message); Data(command, setter);
        Select(Response);
        Poll(setter ? _ => { Disposition = CpuLiveCurveNativeDisposition.Acknowledged; return true; } : null, commandEnd: accept is null);
        if (accept is not null) { Select(Argument); Pci(bus, 0, 0, 0xC8, accept, commandEnd: true); }
    }
    private void Smn(uint address, Func<uint, bool> accept)
    { Select(address, commandStart: true); Pci(bus, 0, 0, 0xC8, accept, commandEnd: true); }
    private void Select(uint address, bool commandStart = false) =>
        steps.Enqueue(new(Write, Words(0, address, bus), 0, CommandStart: commandStart));
    private void Data(uint value, bool setter = false) => steps.Enqueue(new(Write, Words(1, value, bus), 0, Setter: setter));
    private void Pci(uint b, uint d, uint f, uint offset, Func<uint, bool> accept, bool commandEnd = false) =>
        steps.Enqueue(new(Read, Words(b, d, f, offset, 4), 24, bytes => accept(Word(bytes, 20)), CommandEnd: commandEnd));
    private void Poll(Func<byte[], bool>? accept = null, bool commandEnd = false) =>
        steps.Enqueue(new(Read, Words(bus, 0, 0, 0xC8, 4), 24, accept, Poll: true, CommandEnd: commandEnd));
    private static bool Header(byte[] bytes) => bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) < 0;
    private static byte[] Words(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) { Put(bytes, i * 4, values[i]); }
        return bytes;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private sealed record Step(uint Code, byte[] Input, int Output, Func<byte[], bool>? Accept = null, bool Poll = false,
        bool Setter = false, bool CommandStart = false, bool CommandEnd = false)
    { internal int Polls; }
}
