using System.Buffers.Binary;
using System.Diagnostics;

namespace OpenBlade.Protocol;

internal sealed class CpuPowerAdmissionException : Exception;

// An exact instruction sequence, with polling as its only variable repetition.
// This is separate from both the 06/5E query and attended PM guards. Every request
// is checked again at the actual native boundary, including echoed PCI replies,
// topology/masks, two version-bracketed PM copies and one exact CO setter.
internal sealed class CpuPowerNativeWire : ICpuNativeWireAdmission
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8, Copy = 0x81112F08;
    private const uint Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private const uint ExpectedFirmwareVersion = 0x0B5D1600;
    private readonly CpuLiveCurveNativeRequest curveRequest;
    private readonly Action checkpoint, snapshotAdmission, finalAdmission;
    private readonly Action? afterSetter;
    private readonly Queue<Step> steps = new();
    private Step? pending;
    private bool failed, scriptCreated, waiting;
    private int operations;
    private uint bus, capFirst;
    private CpuLivePowerSnapshot? copied;
    internal CpuLiveCurveNativeDisposition Disposition { get; private set; }
    internal CpuLivePowerFusedCap? Cap { get; private set; }
    internal uint? Capability { get; private set; }
    internal CpuLivePowerSnapshot? After { get; private set; }
    internal CpuLiveCurveSnapshot? CurveAfter { get; private set; }
    internal bool Complete => scriptCreated && !failed && pending is null && steps.Count == 0;

    internal CpuPowerNativeWire(CpuLiveCurveNativeRequest curveRequest, Action checkpoint, Action snapshotAdmission,
        Action finalAdmission, Action afterSetter)
    {
        this.curveRequest = curveRequest;
        this.checkpoint = checkpoint; this.snapshotAdmission = snapshotAdmission; this.finalAdmission = finalAdmission;
        this.afterSetter = afterSetter;
        steps.Enqueue(new(Enumerate, new byte[28], 28, bytes =>
        {
            uint count = Word(bytes, 24);
            if (!Header(bytes) || count is 0 or > 65536) { return false; }
            var input = new byte[checked(28 + (int)count * 52)]; Put(input, 24, count);
            steps.Enqueue(new(Enumerate, input, input.Length, records =>
            {
                if (!Header(records) || Word(records, 24) != count || !CpuQueryPciTopology.TryFindBus(records, out bus)) { return false; }
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
        if (step.Setter) { afterSetter?.Invoke(); }
        return true;
    }

    public void Abort() { failed = true; pending = null; }

    private void CreateScript()
    {
        scriptCreated = true;
        Pci(bus, 0, 0, 0, value => value == CpuQueryPciTopology.RootId);
        Pci(bus, 0x18, 0, 0, value => value == CpuQueryPciTopology.NodeId);
        Pci(0, 0x18, 1, 0, value => value == CpuQueryPciTopology.OrientationId);
        Pci(0, 0x18, 1, 0x140, value => value == 0xFF55);
        Smn(0x03820AB0, value => (~(value >> 5) & 0xFFFF) == 0xFF55);
        // Bind the reviewed PPT handler semantics inside this retained lease.
        // Firmware version is separate from the PM layout checked by Snapshot.
        Mailbox(0x02, null, value => value == ExpectedFirmwareVersion);
        ReadCaps();
        if (curveRequest is { PowerPeer: not null } curve)
        {
            CurveSnapshot(curve.Before, after: false);
            // CO metadata is read before the peer snapshot, under this lease.
            Mailbox(0x82, null, value => { if ((value & 4) == 0) { return false; } snapshotAdmission(); return true; });
            Snapshot(before: true);
            ReadCaps();
            Mailbox(CpuLiveCurveCommand.Message, curve.Command.EncodeArgument(), null, setter: true);
            CurveSnapshot(curve.After, after: true);
            Snapshot(before: false); // Prove the complete PPT peer remained unchanged.
        }
        else { throw new CpuPowerAdmissionException(); }
    }
    private void CurveSnapshot(CpuLiveCurveSnapshot expected, bool after)
    {
        var values = new List<CpuLiveCoreOffset>();
        Mailbox(6, 0, value => value == CpuLiveCurveSnapshot.SupportedPmVersion);
        foreach (var core in expected.CoreOffsets)
        {
            uint argument = ((uint)(core.PhysicalCoreId / 8) << 28) | ((uint)(core.PhysicalCoreId % 8) << 20);
            Mailbox(0x5E, argument, value =>
            {
                short offset = unchecked((short)value);
                if (offset != core.Offset) { return false; }
                values.Add(new(core.PhysicalCoreId, offset));
                if (after && values.Count == expected.CoreOffsets.Count)
                {
                    CurveAfter = CpuLiveCurveSnapshot.TryCreate(new(CpuLiveCurveSnapshot.SupportedMask,
                        CpuLiveCurveSnapshot.SupportedPmVersion, CpuLiveCurveSnapshot.SupportedPmLength, values));
                }
                return true;
            });
        }
    }

    private void ReadCaps()
    {
        Smn(CpuLivePowerFusedCap.RegisterA0F, value => { capFirst = value; return value != uint.MaxValue; });
        Smn(CpuLivePowerFusedCap.RegisterA0B, value => (Cap = CpuLivePowerFusedCap.TryDecode(capFirst, value)) is not null);
        // Vendor capability getter82 has zero input arguments, unlike getters06/65.
        Mailbox(0x82, null, value => { Capability = value; return value != uint.MaxValue; });
    }
    private void Snapshot(bool before)
    {
        Mailbox(6, 0, value => value == CpuLivePowerSnapshot.SupportedVersion);
        Mailbox(0x65, 3, _ => true);
        steps.Enqueue(new(Copy, Words(CpuLivePowerSnapshot.PayloadLength), CpuLivePowerSnapshot.PayloadLength + 4, bytes =>
        {
            if (!CpuPmRawSnapshot.TryCreate(CpuLivePowerSnapshot.SupportedVersion, new(true, bytes),
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), out var raw)) { return false; }
            copied = CpuLivePowerSnapshot.TryCreate(raw);
            return copied is not null && (!before || copied.Pair == curveRequest.PowerPeer!.Expected);
        }));
        // F08 can alter hidden mailbox/index state; always explicitly reselect.
        Mailbox(6, 0, value =>
        {
            if (value != CpuLivePowerSnapshot.SupportedVersion || copied is null) { return false; }
            if (!before) { After = copied; }
            copied = null; return true;
        });
    }
    private void Mailbox(uint command, uint? argument, Func<uint, bool>? accept, bool setter = false)
    {
        Select(Response); Poll(); Data(0);
        if (argument is uint value) { Select(Argument); Data(value); }
        Select(Message); Data(command, setter);
        Select(Response);
        Poll(setter ? _ => { Disposition = CpuLiveCurveNativeDisposition.Acknowledged; return true; } : null);
        if (accept is not null) { Select(Argument); Pci(bus, 0, 0, 0xC8, accept); }
    }
    private void Smn(uint address, Func<uint, bool> accept) { Select(address); Pci(bus, 0, 0, 0xC8, accept); }
    private void Select(uint address) => steps.Enqueue(new(Write, Words(0, address, bus), 0));
    private void Data(uint value, bool setter = false) => steps.Enqueue(new(Write, Words(1, value, bus), 0, Setter: setter));
    private void Pci(uint b, uint d, uint f, uint offset, Func<uint, bool> accept) =>
        steps.Enqueue(new(Read, Words(b, d, f, offset, 4), 24, bytes => accept(Word(bytes, 20))));
    private void Poll(Func<byte[], bool>? accept = null) =>
        steps.Enqueue(new(Read, Words(bus, 0, 0, 0xC8, 4), 24, accept, Poll: true));
    private static bool Header(byte[] bytes) => bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) < 0;
    private static byte[] Words(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) { Put(bytes, i * 4, values[i]); }
        return bytes;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private sealed record Step(uint Code, byte[] Input, int Output, Func<byte[], bool>? Accept = null, bool Poll = false, bool Setter = false)
    { internal int Polls; }
}
