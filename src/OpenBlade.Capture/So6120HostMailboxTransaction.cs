namespace OpenBlade.Capture;

// Exact SO6120 EC 1.04 bank-zero mapping. This enum is not a raw-address API.
internal enum So6120MailboxRegister : byte
{
    ActiveState = 0x06, Tachometer1 = 0x08, Tachometer2 = 0x09, PolicyCommand = 0x0A, PolicyStatus = 0x0B,
    Selector = 0x1A, Manual = 0x36, ActualDuty1 = 0xB0, ActualDuty2 = 0xB1,
    Cycle = 0xB2, StagedDuty1 = 0xB8, StagedDuty2 = 0xB9, Override = 0xF0,
}

internal enum So6120PortEffect { NotPerformed, MayHavePerformed, Performed }
internal enum So6120PortOperation { SelectIndex, ReadData, WriteData }

// Preserve the native Boolean, returned count, error, and execution uncertainty.
// A future adapter must use 9C402004 (2-byte LE input, 1-byte output) for reads,
// requiring Boolean success AND one returned byte. 9C402008 writes use 3-byte LE
// input and no output: success is the Boolean, NOT returned-count == 0 (the
// reviewed driver sets Information=10). This file implements no native adapter.
internal sealed record So6120PortResult(
    bool Succeeded, uint BytesReturned, byte Value, int NativeError, So6120PortEffect Effect);

// Fixed 300/301 boundary only. Implementations must return promptly: the host
// deadline cannot interrupt a blocked native call or cancel an EC command.
internal interface ISo6120MailboxBytePort
{
    So6120PortResult SelectIndex(byte index);
    So6120PortResult ReadData();
    So6120PortResult WriteData(byte value);
}

internal sealed record So6120MailboxRequest(Guid Id, So6120MailboxRegister Register, byte? WriteValue)
{
    public byte Bank => 0;
    public byte Count => 1;
    public byte Command => WriteValue.HasValue ? (byte)0xA1 : (byte)0xA0;
}

// Required OUTER OWNER boundary, not implemented exclusion or a durable journal.
// The caller must hold its reviewed transaction/field ownership and lifecycle
// admission across the complete operation, including fresh A0 confirmation.
// The outer owner also admits bit-preserving, policy-safe values: mapped writes
// store whole bytes, including every side effect of the F0 override byte.
internal interface ISo6120MailboxPrerequisites
{
    // Durably records this immutable request's intent and checks exact-device,
    // controller, power, session, ownership and applicable readback admission.
    bool Prepare(So6120MailboxRequest request);
    // Fresh per-publication admission after staging; records publication intent
    // durably before returning true. False must mean no command was submitted.
    bool AdmitPublication(So6120MailboxRequest request);
}

internal enum So6120MailboxFailure
{
    None, AlreadyUsed, InvalidRequest, AdmissionRefused, BoundaryFailure,
    PortFailure, Busy, DescriptorChanged, StatusRejected, CommandChanged,
    PollLimit, Deadline, Cancelled, ConfirmationMismatch,
}

internal sealed record So6120MailboxPortObservation(
    Guid RequestId, string Phase, So6120PortOperation Operation, byte? Argument,
    So6120PortResult? Result, string? ExceptionType);

internal sealed record So6120MailboxResult(
    So6120MailboxFailure Failure, byte? Value, int CommandsPossiblySubmitted,
    int CommandsCompleted, bool WriteHandlerCompleted,
    IReadOnlyList<So6120MailboxPortObservation> Operations)
{
    public bool Success => Failure == So6120MailboxFailure.None;
    // Handler-return plus a separate byte match only. A value already matching
    // does not prove a transition, ownership, or a fan effect.
    public bool WriteConfirmed => Success && WriteHandlerCompleted;
    public bool RequiresReadOnlyReconciliation => !Success && Operations.Count != 0;
}

// Developer-only protocol core. See docs/so6120-host-mailbox-contract.md and
// docs/so6120-host-mailbox-alternatives.md. No CLI, driver handle, MBOX0F, MCU
// gate, cleanup write or retry path exists here. Descriptor equality and 94=0
// freshness cannot distinguish all resets/replacements or establish exclusion.
internal sealed class So6120HostMailboxTransaction
{
    private readonly ISo6120MailboxBytePort port;
    private readonly ISo6120MailboxPrerequisites prerequisites;
    private readonly TimeProvider time;
    private readonly TimeSpan timeout;
    private readonly int maxPolls;
    private int started;

    public So6120HostMailboxTransaction(ISo6120MailboxBytePort port,
        ISo6120MailboxPrerequisites prerequisites, TimeProvider? time = null,
        TimeSpan? timeout = null, int maxPolls = 128)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        this.prerequisites = prerequisites ?? throw new ArgumentNullException(nameof(prerequisites));
        this.time = time ?? TimeProvider.System;
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(200);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(5))
        { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        if (maxPolls is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(maxPolls)); }
        this.maxPolls = maxPolls;
    }

    public So6120MailboxResult Read(So6120MailboxRegister register, CancellationToken cancellationToken = default) =>
        Run(register, null, cancellationToken);

    public So6120MailboxResult WriteAndConfirm(So6120MailboxRegister register, byte value,
        CancellationToken cancellationToken = default) => Run(register, value, cancellationToken);

    private So6120MailboxResult Run(So6120MailboxRegister register, byte? value, CancellationToken cancellationToken)
    {
        var observations = new List<So6120MailboxPortObservation>();
        int possiblySubmitted = 0, completed = 0;
        bool writeCompleted = false;
        byte? observed = null;
        var request = new So6120MailboxRequest(Guid.NewGuid(), register, value);
        long start = time.GetTimestamp();
        string phase = "prepare";
        if (Interlocked.Exchange(ref started, 1) != 0) { return Result(So6120MailboxFailure.AlreadyUsed); }
        if (!Readable(register) || value.HasValue && !Writable(register)) { return Result(So6120MailboxFailure.InvalidRequest); }
        try
        {
            observed = Transfer(request);
            if (value.HasValue)
            {
                writeCompleted = true;
                // A distinct request, descriptor, status reset and command. Never
                // accept the staged payload or a cached value as confirmation.
                request = new(Guid.NewGuid(), register, null);
                observed = Transfer(request);
                if (observed != value) { throw new Stopped(So6120MailboxFailure.ConfirmationMismatch); }
            }
            Check();
            return Result(So6120MailboxFailure.None);
        }
        catch (Stopped stop) { return Result(stop.Failure); }
        catch (OperationCanceledException) { return Result(So6120MailboxFailure.Cancelled); }
        catch (Exception) { return Result(So6120MailboxFailure.BoundaryFailure); }

        So6120MailboxResult Result(So6120MailboxFailure failure) =>
            new(failure, failure == So6120MailboxFailure.None ? observed : null,
                possiblySubmitted, completed, writeCompleted, observations.AsReadOnly());

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (time.GetElapsedTime(start) >= timeout) { throw new Stopped(So6120MailboxFailure.Deadline); }
        }

        So6120PortResult Operation(So6120PortOperation operation, byte? argument, bool publishes = false)
        {
            Check();
            So6120PortResult result;
            try
            {
                result = operation switch
                {
                    So6120PortOperation.SelectIndex => port.SelectIndex(argument!.Value),
                    So6120PortOperation.ReadData => port.ReadData(),
                    So6120PortOperation.WriteData => port.WriteData(argument!.Value),
                    _ => throw new InvalidOperationException(),
                };
                if (result is null) { throw new InvalidOperationException("Missing port result."); }
            }
            catch (Exception exception)
            {
                // Once the boundary was entered, a thrown exception cannot prove
                // no execution, including cancellation thrown by an adapter.
                if (publishes) { possiblySubmitted++; }
                observations.Add(new(request.Id, phase, operation, argument, null, exception.GetType().FullName));
                if (exception is OperationCanceledException) { throw new Stopped(So6120MailboxFailure.Cancelled); }
                throw new Stopped(So6120MailboxFailure.PortFailure);
            }
            observations.Add(new(request.Id, phase, operation, argument, result, null));
            if (publishes && (result.Succeeded || result.Effect != So6120PortEffect.NotPerformed)) { possiblySubmitted++; }
            Check();
            if (!result.Succeeded || result.Effect != So6120PortEffect.Performed
                || operation == So6120PortOperation.ReadData && result.BytesReturned != 1)
            { throw new Stopped(So6120MailboxFailure.PortFailure); }
            return result;
        }

        byte ReadMailbox(byte index)
        {
            Operation(So6120PortOperation.SelectIndex, index);
            return Operation(So6120PortOperation.ReadData, null).Value;
        }

        void WriteMailbox(byte index, byte data, bool publishes = false)
        {
            Operation(So6120PortOperation.SelectIndex, index);
            Operation(So6120PortOperation.WriteData, data, publishes);
        }

        void Descriptor()
        {
            if (ReadMailbox(0x90) != request.Bank || ReadMailbox(0x91) != (byte)request.Register
                || ReadMailbox(0x92) != request.Count)
            { throw new Stopped(So6120MailboxFailure.DescriptorChanged); }
        }

        byte? Transfer(So6120MailboxRequest current)
        {
            request = current;
            phase = "prepare";
            Check();
            if (!prerequisites.Prepare(request)) { throw new Stopped(So6120MailboxFailure.AdmissionRefused); }
            Check();
            phase = "idle";
            if (ReadMailbox(0x93) != 0) { throw new Stopped(So6120MailboxFailure.Busy); }
            phase = "stage";
            // Exact BIOS4.01 ECRR also clears this shared result field before
            // publication. This is freshness, not an ownership token.
            WriteMailbox(0x94, 0);
            WriteMailbox(0x90, request.Bank);
            WriteMailbox(0x91, (byte)request.Register);
            WriteMailbox(0x92, request.Count);
            if (request.WriteValue is byte data) { WriteMailbox(0xA0, data); }
            phase = "pre-publication";
            Descriptor();
            if (ReadMailbox(0x94) != 0) { throw new Stopped(So6120MailboxFailure.StatusRejected); }
            if (ReadMailbox(0x93) != 0) { throw new Stopped(So6120MailboxFailure.Busy); }
            Check();
            if (!prerequisites.AdmitPublication(request)) { throw new Stopped(So6120MailboxFailure.AdmissionRefused); }
            Check();
            // Admission can perform durable I/O and host checks. Revalidate the
            // live fields afterwards, including the payload that A1 will sample.
            phase = "post-admission";
            Descriptor();
            if (ReadMailbox(0x94) != 0) { throw new Stopped(So6120MailboxFailure.StatusRejected); }
            if (ReadMailbox(0x93) != 0) { throw new Stopped(So6120MailboxFailure.Busy); }
            if (request.WriteValue is byte expected && ReadMailbox(0xA0) != expected)
            { throw new Stopped(So6120MailboxFailure.DescriptorChanged); }
            phase = "publish";
            WriteMailbox(0x93, request.Command, publishes: true);
            phase = "poll";
            bool finished = false;
            for (int poll = 0; poll < maxPolls; poll++)
            {
                byte command = ReadMailbox(0x93);
                if (command == 0) { finished = true; break; }
                if (command != request.Command) { throw new Stopped(So6120MailboxFailure.CommandChanged); }
            }
            if (!finished) { throw new Stopped(So6120MailboxFailure.PollLimit); }
            phase = "completion";
            if (ReadMailbox(0x94) != 0x80) { throw new Stopped(So6120MailboxFailure.StatusRejected); }
            Descriptor();
            byte? response = request.WriteValue.HasValue ? null : ReadMailbox(0xA0);
            completed++;
            return response;
        }
    }

    private static bool Readable(So6120MailboxRegister register) => register is
        So6120MailboxRegister.ActiveState or So6120MailboxRegister.Tachometer1 or So6120MailboxRegister.Tachometer2 or
        So6120MailboxRegister.PolicyCommand or So6120MailboxRegister.PolicyStatus or
        So6120MailboxRegister.Selector or So6120MailboxRegister.Manual or
        So6120MailboxRegister.ActualDuty1 or So6120MailboxRegister.ActualDuty2 or
        So6120MailboxRegister.Cycle or So6120MailboxRegister.StagedDuty1 or
        So6120MailboxRegister.StagedDuty2 or So6120MailboxRegister.Override;

    private static bool Writable(So6120MailboxRegister register) => register is
        So6120MailboxRegister.Selector or So6120MailboxRegister.Manual or
        So6120MailboxRegister.Override or So6120MailboxRegister.StagedDuty1 or So6120MailboxRegister.StagedDuty2;

    private sealed class Stopped(So6120MailboxFailure failure) : Exception
    {
        public So6120MailboxFailure Failure { get; } = failure;
    }
}
