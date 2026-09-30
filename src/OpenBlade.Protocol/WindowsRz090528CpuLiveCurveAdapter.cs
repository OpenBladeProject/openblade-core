using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenBlade.Protocol;

// Opaque, typed access to one retained platform. This object is correlation and
// resource ownership only: callers must independently supply each query approval
// or exact-request write proof. It exposes no device, IOCTL or register access.
internal sealed partial class WindowsRz090528CpuNativeSession
{
    private static readonly ConditionalWeakTable<ICpuQueryWindowsPlatform, WindowsRz090528CpuNativeSession> Sessions = new();
    private readonly ICpuQueryWindowsPlatform platform;

    private WindowsRz090528CpuNativeSession(ICpuQueryWindowsPlatform platform) => this.platform = platform;

    // The injected-platform seam shares identity as well as the native latch.
    // Service worker ownership must survive rewrapping the same platform too.
    internal static WindowsRz090528CpuNativeSession ForPlatform(ICpuQueryWindowsPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return Sessions.GetValue(platform, static value => new(value));
    }

    internal Rz090528CpuLiveQueryResult Query(CpuQueryAttendedApproval? approval,
        Func<DeviceIdentity?> readFreshUsbIdentity, CancellationToken token = default) =>
        WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(approval, readFreshUsbIdentity, platform, token);

    internal CpuLiveCurveNativeResult ExecuteCurve(CpuLiveCurveNativeRequest request,
        CpuLiveCurveWriteProof? proof, Func<DeviceIdentity?> readFreshUsbIdentity, CancellationToken token = default) =>
        WindowsRz090528CpuQueryAdapter.ExecuteCurveWithPlatform(request, proof, readFreshUsbIdentity, platform, token);
}

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    private static readonly ConditionalWeakTable<ICpuQueryWindowsPlatform, NativeOperationLatch> NativeOperations = new();

    // The native session factory retains the private platform; this typed gate
    // still requires an independent trusted write proof. No concrete proof,
    // Capture command or production composition creates write authority today.
    // All execution is synchronous; callers must retain their actual worker task
    // through termination even if they stop awaiting it. The latch outlives that wait.
    internal static CpuLiveCurveNativeResult ExecuteCurveWithPlatform(CpuLiveCurveNativeRequest request,
        CpuLiveCurveWriteProof? proof, Func<DeviceIdentity?> readUsb, ICpuQueryWindowsPlatform platform,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(readUsb);
        var latch = NativeOperations.GetValue(platform, static _ => new());
        if (Interlocked.CompareExchange(ref latch.Active, 1, 0) != 0)
        { return new(CpuLiveCurveNativeDisposition.NotSubmitted, true, true, false, null, CpuLiveCurveNativeFailure.AdmissionDenied); }
        var wire = new CpuCurveNativeWire(request);
        IDisposable? lease = null;
        WindowsRz090528CpuQueryAdapter? adapter = null;
        CpuLiveCurveSnapshot? after = null;
        bool verified = false, cleanup = true;
        var failure = CpuLiveCurveNativeFailure.AdmissionDenied;
        try
        {
            token.ThrowIfCancellationRequested();
            if (proof is null || !proof.Consume(request) || !ExactUsb(readUsb())) { throw new CurveAdmissionException(); }
            adapter = new WindowsRz090528CpuQueryAdapter(platform, readUsb, wire);
            lease = adapter.Acquire(TimeSpan.FromSeconds(20), token);
            if (lease is null || !proof.Current(request)) { throw new CurveAdmissionException(); }
            var session = new Rz090528CpuMailboxSession(adapter, adapter);
            var before = CpuLiveCurveSnapshot.TryCreate(session.Observe(token));
            if (!CpuLiveCurveNativeRequest.Equal(before, request.Before)) { throw new CurveAdmissionException(); }
            uint capability = session.ReadCurveCapability(token);
            if ((capability & 4) == 0 || proof.ReadFreshRange(request)?.AllowsOperation(request) != true
                || !proof.Current(request)) { throw new CurveAdmissionException(); }
            wire.Arm(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!adapter.FreshAdmission() || !proof.Current(request)
                    || proof.ReadFreshRange(request)?.AllowsOperation(request) != true || !proof.Current(request))
                { throw new CurveAdmissionException(); }
                token.ThrowIfCancellationRequested();
            });
            failure = CpuLiveCurveNativeFailure.Transport;
            session.SubmitCurve(request.Command, token);
            after = CpuLiveCurveSnapshot.TryCreate(session.Observe(token));
            token.ThrowIfCancellationRequested();
            verified = CpuLiveCurveNativeRequest.Equal(after, request.After) && wire.Complete && proof.Current(request);
            failure = verified ? CpuLiveCurveNativeFailure.None : CpuLiveCurveNativeFailure.ReadbackDrift;
        }
        catch (CurveAdmissionException) { failure = CpuLiveCurveNativeFailure.AdmissionDenied; }
        catch (OperationCanceledException) { failure = CpuLiveCurveNativeFailure.Cancelled; }
        catch (Exception)
        {
            cleanup = adapter?.cleanupFailed != true;
            failure = cleanup ? CpuLiveCurveNativeFailure.Transport : CpuLiveCurveNativeFailure.Cleanup;
        }
        finally
        {
            wire.Abort();
            try { lease?.Dispose(); }
            catch (Exception) { cleanup = false; verified = false; failure = CpuLiveCurveNativeFailure.Cleanup; }
            finally
            {
                // Acquire can throw cancellation from its own cleanup before it
                // returns a lease. Cleanup truth is sticky and independent of
                // whichever exception (including cancellation) escaped last.
                if (adapter?.cleanupFailed == true)
                { cleanup = false; verified = false; failure = CpuLiveCurveNativeFailure.Cleanup; }
                // A cleanup failure parks this platform scope. A new request or
                // awaiter timeout cannot authorize reuse of uncertain resources.
                if (cleanup) { Volatile.Write(ref latch.Active, 0); }
            }
        }
        return Result();

        // Returned only on this executing worker; no background work is queued.
        CpuLiveCurveNativeResult Result() => new(wire.Disposition, true, cleanup, verified, after, failure);
    }

    private sealed class CurveAdmissionException : Exception;

    private sealed class NativeOperationLatch { internal int Active; }
}

// Separate from CpuQueryWireAdmission: the old approval/profile remains06/5E.
// Exact operation order and full pre-vector are independently checked here at
// the last managed boundary, including root/node/orientation, mask and PM layout.
internal sealed class CpuCurveNativeWire(CpuLiveCurveNativeRequest request) : ICpuNativeWireAdmission
{
    private const uint Enumerate = 0x81112F18, Read = 0x81112F24, Write = 0x81112FF8;
    private const uint Fuse = 0x03820AB0, Response = 0x03B10A80, Argument = 0x03B10A88, Message = 0x03B10A20;
    private uint count, selected;
    private uint? bus;
    private int commandIndex;
    private bool failed, root, node, orientation, mask, fuse, responseReady, cleared, argumentWritten, pending, acknowledged;
    private Action? finalAdmission;
    internal CpuLiveCurveNativeDisposition Disposition { get; private set; }
    internal bool Complete => commandIndex == 28 && !pending && !failed;
    internal void Arm(Action check)
    {
        if (failed || commandIndex != 14 || pending || finalAdmission is not null)
        { throw new InvalidOperationException("Incomplete curve admission."); }
        finalAdmission = check;
    }
    public void Abort() => failed = true;

    public bool Allows(uint code, byte[] input, int outputLength)
    {
        bool allowed = !failed && AllowsCore(code, input, outputLength);
        if (!allowed) { failed = true; }
        return allowed;
    }

    private bool AllowsCore(uint code, byte[] input, int outputLength)
    {
        if (code == Enumerate)
        {
            return !bus.HasValue && input.Length >= 28 && outputLength == input.Length
                && input.AsSpan(0, 24).IndexOfAnyExcept((byte)0) < 0
                && (count == 0 ? input.Length == 28 && Word(input, 24) == 0
                    : Word(input, 24) == count && input.Length == 28 + count * 52 && input.AsSpan(28).IndexOfAnyExcept((byte)0) < 0);
        }
        if (!bus.HasValue) { return false; }
        if (code == Read)
        {
            if (input.Length != 20 || outputLength != 24 || Word(input, 16) != 4) { return false; }
            uint b = Word(input, 0), d = Word(input, 4), f = Word(input, 8), offset = Word(input, 12);
            return b == 0 && d == 0x18 && f == 1 && offset is 0 or 0x140
                || b == bus && d == 0x18 && f == 0 && offset == 0
                || b == bus && d == 0 && f == 0 && (offset == 0 || offset == 0xC8
                    && (selected == Fuse || selected == Response || selected == Argument && pending && acknowledged && commandIndex != 14));
        }
        if (code != Write || input.Length != 12 || outputLength != 0 || Word(input, 8) != bus
            || !root || !node || !orientation) { return false; }
        uint index = Word(input, 0), value = Word(input, 4);
        if (index == 0) { return value is Fuse or Response or Argument or Message; }
        if (index != 1 || !mask || !fuse || commandIndex >= 28) { return false; }
        return selected switch
        {
            Response => value == 0 && responseReady && !pending,
            Argument => cleared && !pending && commandIndex != 13 && value == ExpectedArgument(),
            Message => cleared && !pending && value == ExpectedMessage()
                && (commandIndex == 13 ? !argumentWritten : argumentWritten)
                && (commandIndex != 14 || finalAdmission is not null && Disposition == CpuLiveCurveNativeDisposition.NotSubmitted),
            _ => false,
        };
    }

    // Called by the shared adapter only after Allows and immediately before the
    // actual device.Invoke. Preparation/selection never sets the sticky marker.
    internal void BeforeNativeInvoke(uint code, byte[] input)
    {
        if (code != Write || Word(input, 0) != 1 || selected != Message || Word(input, 4) != 0x53) { return; }
        if (failed || commandIndex != 14 || finalAdmission is null
            || Disposition != CpuLiveCurveNativeDisposition.NotSubmitted)
        { throw new InvalidOperationException("Curve submission is not armed."); }
        finalAdmission();
        Disposition = CpuLiveCurveNativeDisposition.PossibleSubmission;
    }

    public bool AcceptReply(uint code, byte[] input, int outputLength, CpuQueryIoctlReply reply)
    {
        if (failed || !reply.Success || reply.Bytes.Length != outputLength) { return Reject(); }
        if (code == Enumerate)
        {
            uint received = Word(reply.Bytes, 24);
            if (reply.Bytes.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0 || received is 0 or > 65536) { return Reject(); }
            if (count == 0) { count = received; return true; }
            if (received != count || !CpuQueryPciTopology.TryFindBus(reply.Bytes, out uint found)) { return Reject(); }
            bus = found;
        }
        else if (code == Read)
        {
            if (!reply.Bytes.AsSpan(0, 20).SequenceEqual(input)) { return Reject(); }
            uint value = Word(reply.Bytes, 20), offset = Word(input, 12);
            if (offset == 0)
            {
                if (Word(input, 4) == 0) { if (!(root = value == CpuQueryPciTopology.RootId)) { return Reject(); } }
                else if (Word(input, 8) == 0) { if (!(node = value == CpuQueryPciTopology.NodeId)) { return Reject(); } }
                else if (!(orientation = value == CpuQueryPciTopology.OrientationId)) { return Reject(); }
            }
            else if (offset == 0x140) { if (!(mask = value == CpuLiveCurveSnapshot.SupportedMask)) { return Reject(); } }
            else if (selected == Fuse) { if (!(fuse = (~(value >> 5) & 0xFFFF) == CpuLiveCurveSnapshot.SupportedMask)) { return Reject(); } }
            else if (selected == Response)
            {
                responseReady = value == 1;
                if (pending && value != 0)
                {
                    if (value != 1) { return Reject(); }
                    acknowledged = true;
                    if (commandIndex == 14)
                    {
                        Disposition = CpuLiveCurveNativeDisposition.Acknowledged;
                        FinishCommand();
                    }
                }
            }
            else if (selected == Argument)
            {
                if (commandIndex is 0 or 15 && value != CpuLiveCurveSnapshot.SupportedPmVersion
                    || commandIndex is > 0 and < 13 && unchecked((short)value) != request.Before.CoreOffsets[commandIndex - 1].Offset
                    || commandIndex == 13 && (value & 4) == 0) { return Reject(); }
                FinishCommand();
            }
        }
        else if (code == Write)
        {
            uint value = Word(input, 4);
            if (Word(input, 0) == 0) { selected = value; }
            else if (selected == Response) { cleared = true; responseReady = false; argumentWritten = false; }
            else if (selected == Argument) { argumentWritten = true; }
            else if (selected == Message) { pending = true; acknowledged = false; cleared = false; argumentWritten = false; }
        }
        return true;
    }
    private void FinishCommand() { pending = false; acknowledged = false; commandIndex++; }
    private uint ExpectedMessage() => commandIndex switch { 0 or 15 => 6, 13 => 0x82, 14 => 0x53, _ => 0x5E };
    private uint ExpectedArgument()
    {
        if (commandIndex is 0 or 15) { return 0; }
        if (commandIndex == 14) { return request.Command.EncodeArgument(); }
        int index = commandIndex < 13 ? commandIndex - 1 : commandIndex - 16;
        int id = request.Before.CoreOffsets[index].PhysicalCoreId;
        return ((uint)(id / 8) << 28) | ((uint)(id % 8) << 20);
    }
    private bool Reject() { failed = true; return false; }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
