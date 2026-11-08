using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.FanStop;

internal sealed record So6120JournalIdentity(DirectDutyOwner Owner, PerformancePreset CapturedProfile,
    byte? RequestedDuty1, byte? RequestedDuty2, string DeviceEvidenceSha256, string FirmwareEvidenceSha256,
    Guid ConsentId, string ConsentSha256, string BootIdentity);

internal sealed record So6120JournalTransfer(Guid Id, So6120MailboxRegister Register, byte? WriteValue);

internal sealed record So6120JournalInspection(bool Complete, bool Corrupt, bool Fenced,
    bool UnresolvedWork, bool PermissionPublicationPossible, bool PermissionRestoredConfirmed,
    long Records, long? CheckpointGeneration, So6120DutyControls? LastConfirmedControls,
    So6120JournalIdentity? Identity)
{
    // Inspection is diagnostic data, never a restart/recovery capability.
    public bool CanResume => false;
    public int FormatVersion { get; init; }
}

internal enum So6120JournalWriteStage { BeforeWrite, BeforeFlush, AfterFlush }

// Developer-only storage for ONE supervised run. No hardware/admission/native
// acquisition is implemented here. Caller-supplied observations/results remain
// caller evidence: a checksum and a journal file do not authenticate hardware.
// FileShare.None is journal ownership, not mailbox/firmware exclusion. The outer
// owner must choose one stable, trusted run root; another root is not excluded.
// Existing files are never reopened for writing, even after clean completion.
internal sealed class So6120DirectDutyJournal : IDisposable
{
    internal const string FileName = "so6120-direct-duty.journal";
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    internal const int MaximumRecordBytes = 256 * 1024;
    internal const int MaximumRecords = 4096;
    private static readonly byte[] Magic = "OBSO6120J1\n"u8.ToArray();
    private static readonly byte[] PlanningMagic = "OBSO6120J2\n"u8.ToArray();
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 32,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };
    private readonly object sync = new();
    private readonly FileStream stream;
    private readonly Action<So6120JournalWriteStage>? fault;
    private State state = new();
    private byte[] digest = new byte[32];
    private bool disposed;
    public bool Faulted { get; private set; }
    public string Path { get; }

    private So6120DirectDutyJournal(string path, FileStream stream, Action<So6120JournalWriteStage>? fault)
    {
        Path = path;
        this.stream = stream;
        this.fault = fault;
    }

    public static So6120DirectDutyJournal Create(string runRoot, So6120JournalIdentity identity) =>
        Create(runRoot, identity, null);

    // Fault injection affects only storage and is never used by production code.
    internal static So6120DirectDutyJournal Create(string runRoot, So6120JournalIdentity identity,
        Action<So6120JournalWriteStage>? fault)
    {
        ValidateIdentity(identity);
        string path = ResolvePath(runRoot, createRoot: true);
        bool planned = !identity.RequestedDuty1.HasValue;
        // The attended v2 observer can read complete frames while this
        // owner retains exclusive write access. Legacy v1 has no live observer.
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            planned ? FileShare.Read : FileShare.None, 4096, FileOptions.WriteThrough);
        var journal = new So6120DirectDutyJournal(path, file, fault);
        try
        {
            // Recheck after opening. This is not protection from a malicious
            // administrator replacing ancestor directories; no such claim is made.
            _ = ResolvePath(runRoot, createRoot: false);
            journal.state.FormatVersion = planned ? 2 : 1;
            file.Write(journal.state.FormatVersion == 1 ? Magic : PlanningMagic);
            file.Flush(flushToDisk: true);
            journal.Append(Kind.Binding, identity);
            return journal;
        }
        catch
        {
            journal.Faulted = true;
            file.Dispose();
            // Preserve a partial/empty file. Its existence consumes this root.
            throw;
        }
    }

    public void ClaimPreparation(DirectDutyOwner owner) => Append(Kind.PreparationClaim, owner);
    public void ApprovePlan(So6120SilentTrialPlan plan, So6120SilentTrialApproval approval) =>
        Append(Kind.ApprovedPlan, new ApprovedPlan(plan, approval));
    public void ClaimEntry(DirectDutyOwner owner, PerformancePreset profile) => Append(Kind.EntryClaim, new Claim(owner, profile));
    public void ClaimExit(So6120DutyCheckpoint checkpoint) => Append(Kind.ExitClaim, checkpoint);
    public void ClaimStop(So6120DutyCheckpoint checkpoint, So6120DutyObservation observation) =>
        Append(Kind.StopClaim, new StopClaim(checkpoint, observation));
    public void RecordObservation(So6120DutyObservation observation) => Append(Kind.Observation, observation);
    public void RecordIntent(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyObservation observation) =>
        Append(Kind.Intent, new Intent(owner, mutation, observation));
    public void BeginTransfer(So6120JournalTransfer transfer) => Append(Kind.TransferBegin, transfer);
    public void PrepareRequest(So6120MailboxRequest request) => Append(Kind.RequestPrepared, request);
    // Must complete durably BEFORE the native command-port operation. It is a
    // publication intent, not evidence that the device received the command.
    public void RecordPublication(Guid requestId) => Append(Kind.Publication, requestId);
    public void RecordTransferResult(So6120MailboxResult result) => Append(Kind.TransferResult, result);
    public void RecordTransferNotSubmitted(So6120MailboxResult evidence) => Append(Kind.TransferNotSubmitted, evidence);
    public void RecordNotSubmitted(DirectDutyOwner owner, So6120DutyMutation mutation, So6120DutyFailure reason) =>
        Append(Kind.IntentNotSubmitted, new Resolution(owner, mutation, reason));
    public void RecordCheckpoint(So6120DutyCheckpoint checkpoint) => Append(Kind.Checkpoint, checkpoint);
    public void RecordFence(DirectDutyOwner owner, So6120DutyFailure reason) => Append(Kind.Fence, new Fence(owner, reason));
    public void Complete(DirectDutyOwner owner, long checkpointGeneration, long observationSequence) =>
        Append(Kind.Complete, new Completion(owner, checkpointGeneration, observationSequence));

    private void Append<T>(Kind kind, T value)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Faulted) { throw new InvalidOperationException("The journal outcome is unresolved; no further records are admitted."); }
            try
            {
                if (state.Sequence >= MaximumRecords) { throw new InvalidDataException("Journal record limit reached."); }
                if (value is So6120MailboxResult result) { ValidateResultBounds(result); }
                var record = new Record(state.Sequence + 1, kind, JsonSerializer.SerializeToElement(value, Options));
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(record, Options);
                if (body.Length > MaximumRecordBytes || stream.Length + body.Length + 36 > MaximumFileBytes)
                { throw new InvalidDataException("Journal size limit reached."); }
                // Use a detached copy: callers cannot mutate a retained result
                // list after validation and change the in-memory durable state.
                record = Decode(body);
                State candidate = Apply(state, record);
                byte[] hash = Hash(digest, body);
                Span<byte> length = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
                fault?.Invoke(So6120JournalWriteStage.BeforeWrite);
                stream.Write(length);
                stream.Write(body);
                stream.Write(hash);
                fault?.Invoke(So6120JournalWriteStage.BeforeFlush);
                stream.Flush(flushToDisk: true);
                fault?.Invoke(So6120JournalWriteStage.AfterFlush);
                state = candidate;
                digest = hash;
            }
            catch
            {
                // Never append a fictitious fence/completion after uncertain
                // storage. The caller's in-process command fence must remain set.
                Faulted = true;
                throw;
            }
        }
    }

    // Post-release inspection only. This reader does not share write access,
    // so it cannot open alongside a live owner. Valid bytes after interruption
    // do not prove the old caller received a flush acknowledgement. No inspection
    // opens a writer or grants authority to resume/reconcile hardware.
    public static So6120JournalInspection Inspect(string runRoot)
    {
        string path = ResolvePath(runRoot, createRoot: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var recovered = new State();
        byte[] previous = new byte[32];
        bool corrupt = false;
        try
        {
            if (file.Length < Magic.Length || file.Length > MaximumFileBytes) { throw new InvalidDataException(); }
            byte[] header = new byte[Magic.Length];
            file.ReadExactly(header);
            Require(header.AsSpan().SequenceEqual(Magic) || header.AsSpan().SequenceEqual(PlanningMagic));
            recovered.FormatVersion = header.AsSpan().SequenceEqual(Magic) ? 1 : 2;
            Span<byte> length = stackalloc byte[4];
            while (file.Position < file.Length)
            {
                Require(recovered.Sequence < MaximumRecords);
                file.ReadExactly(length);
                int count = BinaryPrimitives.ReadInt32LittleEndian(length);
                Require(count > 0 && count <= MaximumRecordBytes && file.Length - file.Position >= count + 32L);
                byte[] body = new byte[count];
                byte[] expected = new byte[32];
                file.ReadExactly(body);
                file.ReadExactly(expected);
                byte[] actual = Hash(previous, body);
                Require(CryptographicOperations.FixedTimeEquals(expected, actual));
                recovered = Apply(recovered, Decode(body));
                previous = actual;
            }
            Require(recovered.Identity is not null && recovered.Sequence != 0);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException
            or EndOfStreamException or ArgumentException or InvalidOperationException or OverflowException)
        {
            corrupt = true;
        }
        return new(recovered.Phase == Phase.Complete && !corrupt, corrupt, recovered.Fenced,
            corrupt || recovered.Phase != Phase.Complete, recovered.PermissionPossible || corrupt,
            recovered.PermissionRestored && !corrupt, recovered.Sequence, recovered.Checkpoint?.Generation,
            recovered.Checkpoint?.Expected, recovered.Identity)
        { FormatVersion = recovered.FormatVersion };
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) { return; }
            disposed = true;
            try { stream.Dispose(); }
            catch { Faulted = true; throw; }
            // Closing does not assert restoration, completion or a durable fence.
        }
    }

    private static State Apply(State original, Record record)
    {
        Require(record.Sequence == original.Sequence + 1 && Enum.IsDefined(record.Kind));
        Require(original.Phase != Phase.Complete && (!original.Fenced || record.Kind == Kind.Fence));
        var next = new State(original) { Sequence = record.Sequence };
        if (record.Kind == Kind.Binding)
        {
            Require(original.Sequence == 0);
            next.Identity = Payload<So6120JournalIdentity>(record);
            ValidateIdentity(next.Identity);
            Require(next.FormatVersion == (next.Identity.RequestedDuty1.HasValue ? 1 : 2));
            next.Phase = Phase.Bound;
            return next;
        }
        Require(next.Identity is not null);
        DirectDutyOwner owner = next.Identity!.Owner;
        switch (record.Kind)
        {
            case Kind.PreparationClaim:
                Require(next.FormatVersion == 2 && next.Phase == Phase.Bound && Payload<DirectDutyOwner>(record) == owner);
                next.Phase = Phase.Preparing;
                break;
            case Kind.ApprovedPlan:
                ApprovedPlan approved = Payload<ApprovedPlan>(record);
                Require(next.FormatVersion == 2 && next.Phase == Phase.Preparing && next.Plan is null && next.Transfer is null
                    && approved.Plan is not null && approved.Approval is not null && approved.Plan.Valid(next.Identity.CapturedProfile)
                    && approved.Plan.Baseline == next.Observation && approved.Plan.Baseline.Owner == owner
                    && approved.Approval.Matches(approved.Plan));
                next.Plan = approved.Plan;
                next.Phase = Phase.PlanApproved;
                break;
            case Kind.EntryClaim:
                Claim claim = Payload<Claim>(record);
                Require(next.Phase == (next.FormatVersion == 1 ? Phase.Bound : Phase.PlanApproved)
                    && claim.Owner == owner && claim.Profile == next.Identity.CapturedProfile);
                next.Phase = Phase.Entry;
                break;
            case Kind.StopClaim:
                StopClaim stop = Payload<StopClaim>(record);
                Require(next.Phase == Phase.Entry && next.Plan is { IncludesStop: true } stopPlan
                    && next.Checkpoint is { } spunUp && stop.Checkpoint == spunUp && spunUp.Owner == owner
                    && next.Intent is null && next.Transfer is null
                    && stop.Observation is { } spinning && spinning == next.Observation
                    && spinning.Sequence > spunUp.Observation.Sequence && spinning.Snapshot.Controls == spunUp.Expected
                    && spunUp.Expected == stopPlan.HalfCycleControls() && (spinning.Snapshot.ActiveState & 1) != 0
                    && stopPlan.AdmitsStopWrite(So6120MailboxRegister.StagedDuty1, spinning.Snapshot));
                next.Phase = Phase.Stop;
                break;
            case Kind.ExitClaim:
                So6120DutyCheckpoint exit = Payload<So6120DutyCheckpoint>(record);
                Require(next.Phase is Phase.Entry or Phase.EntryStopped or Phase.Stop && next.Checkpoint == exit
                    && exit.Owner == owner && next.Intent is null && next.Transfer is null);
                next.Phase = Phase.Exit;
                break;
            case Kind.Observation:
                RequireActive(next);
                Require(next.Transfer is null);
                So6120DutyObservation observation = Payload<So6120DutyObservation>(record);
                ValidateObservation(observation, next, allowSame: false);
                next.Observation = observation;
                break;
            case Kind.Intent:
                Require(next.Phase is Phase.Entry or Phase.Stop or Phase.Exit);
                Intent intent = Payload<Intent>(record);
                Require(next.Intent is null && next.Transfer is null && next.Checkpoint is not null && intent.Owner == owner);
                Require(intent.Mutation == NextMutation(next));
                ValidateObservation(intent.Observation, next, allowSame: true);
                Require(intent.Observation == next.Observation && intent.Observation.Snapshot.Controls == intent.Mutation.Expected);
                Require(intent.Mutation.Expected.PolicyCommand == 0 && (intent.Observation.Snapshot.ActiveState & 1) != 0);
                next.Intent = intent;
                next.WriteAttempted = false;
                next.WriteConfirmed = false;
                break;
            case Kind.TransferBegin:
                RequireActive(next);
                So6120JournalTransfer transfer = Payload<So6120JournalTransfer>(record);
                Require(next.Transfer is null && transfer.Id != Guid.Empty && next.Ids.Add(transfer.Id) && Readable(transfer.Register));
                if (transfer.WriteValue is byte value)
                {
                    Require(next.Phase is Phase.Entry or Phase.Stop or Phase.Exit && next.Intent is not null && !next.WriteAttempted);
                    Require(transfer.Register == next.Intent!.Mutation.Register && value == next.Intent.Mutation.Value);
                    if (next.Phase is Phase.Entry or Phase.Stop && next.Plan is { } plan)
                    {
                        // The session records its fresh pre-write snapshot
                        // after the intent and before beginning the transfer.
                        Require(next.Observation is { } live
                            && live.Snapshot.Controls == next.Intent.Mutation.Expected
                            && (live.Snapshot.ActiveState & 1) != 0
                            && (!plan.RequiresPostIntentObservation || live.Sequence > next.Intent.Observation.Sequence)
                            && (next.Phase == Phase.Stop ? plan.AdmitsStopWrite(transfer.Register, live.Snapshot)
                                : plan.AdmitsLivePwm(live.Snapshot)));
                    }
                    next.WriteAttempted = true;
                }
                next.Transfer = transfer;
                next.PermissionBeforeTransfer = next.PermissionPossible;
                next.Requests.Clear();
                next.Published.Clear();
                break;
            case Kind.RequestPrepared:
                Require(next.Transfer is not null);
                So6120MailboxRequest request = Payload<So6120MailboxRequest>(record);
                int expectedRequests = next.Transfer!.WriteValue.HasValue ? 2 : 1;
                Require(next.Requests.Count < expectedRequests && request.Id != Guid.Empty && next.Ids.Add(request.Id));
                Require(request.Register == next.Transfer.Register
                    && request.WriteValue == (next.Requests.Count == 0 ? next.Transfer.WriteValue : null));
                if (next.Requests.Count != 0) { Require(next.Published.Contains(next.Requests[0].Id)); }
                next.Requests.Add(request);
                break;
            case Kind.Publication:
                Guid id = Payload<Guid>(record);
                Require(next.Transfer is not null && next.Requests.Count != 0 && next.Requests[^1].Id == id && next.Published.Add(id));
                So6120MailboxRequest published = next.Requests[^1];
                if (published.Register == So6120MailboxRegister.Override && published.WriteValue is byte flags && (flags & 0x40) != 0)
                { next.PermissionPossible = true; }
                break;
            case Kind.TransferResult:
                Require(next.Transfer is not null);
                So6120MailboxResult result = Payload<So6120MailboxResult>(record);
                ValidateResultBounds(result);
                ValidateResultIds(result, next);
                int count = next.Transfer!.WriteValue.HasValue ? 2 : 1;
                if (!result.Success)
                {
                    next.Fenced = true;
                    break;
                }
                Require(next.Requests.Count == count && next.Published.Count == count
                    && result.CommandsPossiblySubmitted == count && result.CommandsCompleted == count
                    && result.WriteHandlerCompleted == next.Transfer.WriteValue.HasValue && result.Value.HasValue);
                Require(result.Operations.Count > 0 && result.Operations.All(x => x.ExceptionType is null
                    && x.Result is { Succeeded: true, Effect: So6120PortEffect.Performed }
                    && (x.Operation != So6120PortOperation.ReadData || x.Result.BytesReturned == 1)));
                foreach (So6120MailboxRequest completed in next.Requests)
                {
                    Require(result.Operations.Count(x => x.RequestId == completed.Id && x.Phase == "publish"
                        && x.Operation == So6120PortOperation.WriteData && x.Argument == completed.Command) == 1);
                }
                // Even trusted caller evidence must include the distinct final
                // A0 payload read. A success flag with a truncated/publish-only
                // ledger cannot become a confirmation checkpoint.
                Guid confirmationId = next.Requests[^1].Id;
                Require(result.Operations.Count >= 2);
                So6120MailboxPortObservation payloadIndex = result.Operations[^2];
                So6120MailboxPortObservation payloadRead = result.Operations[^1];
                Require(payloadIndex.RequestId == confirmationId && payloadIndex.Phase == "completion"
                    && payloadIndex.Operation == So6120PortOperation.SelectIndex && payloadIndex.Argument == 0xA0
                    && payloadRead.RequestId == confirmationId && payloadRead.Phase == "completion"
                    && payloadRead.Operation == So6120PortOperation.ReadData && payloadRead.Argument is null
                    && payloadRead.Result!.Value == result.Value);
                if (next.Transfer.WriteValue is byte written)
                {
                    Require(result.Value == written);
                    next.WriteConfirmed = true;
                }
                next.Transfer = null;
                next.Requests.Clear();
                next.Published.Clear();
                break;
            case Kind.TransferNotSubmitted:
                Require(next.Transfer is not null);
                So6120MailboxResult unsubmitted = Payload<So6120MailboxResult>(record);
                ValidateResultBounds(unsubmitted);
                ValidateResultIds(unsubmitted, next);
                Require(!unsubmitted.Success && unsubmitted.CommandsPossiblySubmitted == 0
                    && unsubmitted.CommandsCompleted == 0 && !unsubmitted.WriteHandlerCompleted);
                // A publication intent may precede a known-unperformed native
                // call. Counters alone cannot resolve an entered command call.
                Require(unsubmitted.Operations.Where(x => x.Phase == "publish" && x.Operation == So6120PortOperation.WriteData)
                    .All(x => x.ExceptionType is null && x.Result is { Succeeded: false, Effect: So6120PortEffect.NotPerformed }));
                next.PermissionPossible = next.PermissionBeforeTransfer;
                next.Transfer = null;
                next.Requests.Clear();
                next.Published.Clear();
                break;
            case Kind.IntentNotSubmitted:
                Resolution resolution = Payload<Resolution>(record);
                Require(next.Intent is not null && next.Transfer is null && !next.WriteConfirmed
                    && resolution.Owner == owner && resolution.Mutation == next.Intent.Mutation && Enum.IsDefined(resolution.Reason)
                    && resolution.Reason != So6120DutyFailure.None);
                next.Intent = null;
                next.Phase = next.Phase == Phase.Exit ? Phase.ExitStopped : Phase.EntryStopped;
                break;
            case Kind.Checkpoint:
                RequireActive(next);
                So6120DutyCheckpoint checkpoint = Payload<So6120DutyCheckpoint>(record);
                Require(next.Transfer is null && checkpoint.Owner == owner && checkpoint.Profile == next.Identity.CapturedProfile);
                ValidateObservation(checkpoint.Observation, next, allowSame: true);
                Require(checkpoint.Observation == next.Observation && checkpoint.Expected == checkpoint.Observation.Snapshot.Controls
                    && checkpoint.Expected.PolicyCommand == 0 && checkpoint.Expected.Manual == 0
                    && (checkpoint.Observation.Snapshot.ActiveState & 1) != 0);
                if (next.Checkpoint is null)
                {
                    Require(next.Phase == Phase.Entry && checkpoint.Generation == 1 && next.Intent is null
                        && checkpoint.Baseline == checkpoint.Expected && checkpoint.Owned == So6120DutyOwnedFields.None
                        && So6120DutyWorkflow.TrySelector(checkpoint.Profile, out byte selector)
                        && checkpoint.Baseline.Selector == selector && (checkpoint.Baseline.Override & 0x60) == 0
                        && checkpoint.Baseline.Cycle != 0 && RequestedDuty(next, true) <= checkpoint.Baseline.Cycle
                        && RequestedDuty(next, false) <= checkpoint.Baseline.Cycle
                        && (next.Plan is null || checkpoint.Baseline == next.Plan.Baseline.Snapshot.Controls
                            && next.Plan.AdmitsLivePwm(checkpoint.Observation.Snapshot)));
                }
                else
                {
                    Require(next.Intent is not null && next.WriteConfirmed && checkpoint.Generation == next.Checkpoint.Generation + 1
                        && checkpoint.Baseline == next.Checkpoint.Baseline);
                    So6120DutyMutation mutation = next.Intent!.Mutation;
                    So6120DutyOwnedFields field = Field(mutation.Register);
                    Require(checkpoint.Expected == Set(next.Checkpoint.Expected, mutation.Register, mutation.Value));
                    Require(checkpoint.Owned == (mutation.Value == Get(checkpoint.Baseline, mutation.Register)
                        ? next.Checkpoint.Owned & ~field : next.Checkpoint.Owned | field));
                    if (next.Phase == Phase.Exit && mutation.Register == So6120MailboxRegister.Override)
                    { next.PermissionRestored = true; }
                }
                next.Checkpoint = checkpoint;
                next.Intent = null;
                next.WriteAttempted = false;
                next.WriteConfirmed = false;
                break;
            case Kind.Fence:
                Fence fence = Payload<Fence>(record);
                Require(fence.Owner == owner && Enum.IsDefined(fence.Reason) && fence.Reason != So6120DutyFailure.None);
                next.Fenced = true;
                break;
            case Kind.Complete:
                Completion completion = Payload<Completion>(record);
                Require(next.Phase == Phase.Exit && next.Transfer is null && next.Intent is null && next.Checkpoint is not null
                    && next.Observation is not null && completion.Owner == owner && completion.Generation == next.Checkpoint.Generation
                    && completion.ObservationSequence == next.Observation.Sequence && next.Checkpoint.Owned == So6120DutyOwnedFields.None
                    && next.Checkpoint.Expected == next.Checkpoint.Baseline && next.Observation.Snapshot.Controls == next.Checkpoint.Baseline
                    && (next.Observation.Snapshot.ActiveState & 1) != 0 && next.Observation.Sequence >= next.Checkpoint.Observation.Sequence);
                next.Phase = Phase.Complete;
                break;
            default: throw new InvalidDataException("Unknown journal record.");
        }
        return next;
    }

    private static So6120DutyMutation? NextMutation(State state)
    {
        So6120DutyCheckpoint current = state.Checkpoint!;
        So6120DutyControls target = state.Phase switch
        {
            Phase.Exit => current.Baseline,
            Phase.Stop => current.Expected with { Staged1 = 0, Staged2 = 0 },
            _ => current.Baseline with
            {
                Staged1 = RequestedDuty(state, true),
                Staged2 = RequestedDuty(state, false),
                Override = (byte)(current.Baseline.Override | 0x40),
                Selector = 0xFE,
            },
        };
        So6120MailboxRegister[] order = state.Phase == Phase.Exit
            ? [So6120MailboxRegister.Selector, So6120MailboxRegister.Override, So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2]
            : [So6120MailboxRegister.StagedDuty1, So6120MailboxRegister.StagedDuty2, So6120MailboxRegister.Override, So6120MailboxRegister.Selector];
        foreach (So6120MailboxRegister register in order)
        {
            byte value = Get(target, register);
            if (Get(current.Expected, register) != value)
            {
                Require(state.Phase != Phase.Exit || (current.Owned & Field(register)) != 0);
                return new(register, value, current.Expected);
            }
        }
        return null;
    }

    private static byte RequestedDuty(State state, bool first) => state.Plan is { } plan
        ? first ? plan.RequestedDuty1 : plan.RequestedDuty2
        : (first ? state.Identity!.RequestedDuty1 : state.Identity!.RequestedDuty2)
            ?? throw new InvalidDataException("Unplanned operation has no writable target.");

    private static void ValidateObservation(So6120DutyObservation observation, State state, bool allowSame)
    {
        Require(observation is not null && observation.Owner == state.Identity!.Owner && observation.Snapshot is not null
            && observation.Snapshot.Controls is not null && observation.Sequence > 0 && observation.StartedTimestamp >= 0
            && observation.CompletedTimestamp >= observation.StartedTimestamp);
        if (allowSame && observation == state.Observation) { return; }
        Require(state.Observation is null || observation.Sequence > state.Observation.Sequence
            && observation.StartedTimestamp >= state.Observation.CompletedTimestamp);
    }
    private static void ValidateResultBounds(So6120MailboxResult result)
    {
        Require(result is not null && Enum.IsDefined(result.Failure) && result.CommandsPossiblySubmitted is >= 0 and <= 2
            && result.CommandsCompleted >= 0 && result.CommandsCompleted <= result.CommandsPossiblySubmitted
            && result.Operations is not null && result.Operations.Count <= 4096);
        foreach (So6120MailboxPortObservation item in result.Operations)
        {
            Require(item is not null && item.RequestId != Guid.Empty && Enum.IsDefined(item.Operation)
                && item.Phase is "idle" or "stage" or "pre-publication" or "post-admission" or "publish" or "poll" or "completion"
                && (item.ExceptionType is null || item.ExceptionType.Length <= 256)
                && (item.Result is null || Enum.IsDefined(item.Result.Effect)));
        }
    }
    private static void ValidateResultIds(So6120MailboxResult result, State state) =>
        Require(result.Operations.All(x => state.Requests.Any(request => request.Id == x.RequestId)));
    private static void ValidateIdentity(So6120JournalIdentity identity)
    {
        Require(identity is not null && identity.Owner is not null && So6120DutyWorkflow.ValidOwner(identity.Owner)
            && identity.Owner.DeviceIdentity.Length <= 256 && identity.Owner.FirmwareEvidence.Length <= 256
            && So6120DutyWorkflow.TrySelector(identity.CapturedProfile, out _) && identity.ConsentId != Guid.Empty
            && identity.RequestedDuty1.HasValue == identity.RequestedDuty2.HasValue
            && (identity.RequestedDuty1.HasValue || So6120SilentTrialPlan.SupportsProfile(identity.CapturedProfile))
            && !string.IsNullOrWhiteSpace(identity.BootIdentity) && identity.BootIdentity.Length <= 256
            && IsDigest(identity.DeviceEvidenceSha256) && IsDigest(identity.FirmwareEvidenceSha256) && IsDigest(identity.ConsentSha256));
    }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Readable(So6120MailboxRegister register) => Enum.IsDefined(register);
    private static void RequireActive(State state) => Require(state.Phase is Phase.Entry or Phase.Stop or Phase.Exit
        or Phase.EntryStopped or Phase.ExitStopped or Phase.Preparing or Phase.PlanApproved);
    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) { throw new InvalidDataException("Invalid or out-of-order SO6120 journal evidence."); }
    }
    private static T Payload<T>(Record record) => record.Data.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing record payload.");
    private static Record Decode(byte[] bytes) => JsonSerializer.Deserialize<Record>(bytes, Options) ?? throw new InvalidDataException();
    private static byte[] Hash(byte[] previous, byte[] body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(previous);
        hash.AppendData(body);
        return hash.GetHashAndReset();
    }

    private static string ResolvePath(string runRoot, bool createRoot)
    {
        string full = ValidateRunRoot(runRoot);
        if (createRoot) { Directory.CreateDirectory(full); }
        CheckDirectories(full);
        string path = System.IO.Path.Combine(full, FileName);
        try { Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) when (createRoot) { }
        return path;
    }

    // The CLI calls the same canonical local-root admission before any native
    // acquisition. It checks only paths; creation remains owned by the caller.
    internal static string ValidateRunRoot(string runRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);
        string full = System.IO.Path.GetFullPath(runRoot);
        Require(System.IO.Path.IsPathFullyQualified(runRoot) && !runRoot.StartsWith("\\\\", StringComparison.Ordinal)
            && string.Equals(full, System.IO.Path.TrimEndingDirectorySeparator(runRoot), StringComparison.OrdinalIgnoreCase)
            && full.Length <= 220 && full != System.IO.Path.GetPathRoot(full)
            && !full.AsSpan(2).Contains(':'));
        foreach (string part in full[3..].Split(['\\', '/']))
        { Require(part.Length > 0 && !part.EndsWith('.') && !part.EndsWith(' ')); }
        CheckDirectories(full);
        return full;
    }
    private static void CheckDirectories(string full)
    {
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            try { Require((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) == 0); }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
    }
    private static So6120DutyOwnedFields Field(So6120MailboxRegister register) => register switch
    {
        So6120MailboxRegister.StagedDuty1 => So6120DutyOwnedFields.Staged1,
        So6120MailboxRegister.StagedDuty2 => So6120DutyOwnedFields.Staged2,
        So6120MailboxRegister.Override => So6120DutyOwnedFields.Override,
        So6120MailboxRegister.Selector => So6120DutyOwnedFields.Selector,
        _ => throw new InvalidDataException(),
    };
    private static byte Get(So6120DutyControls value, So6120MailboxRegister register) => register switch
    {
        So6120MailboxRegister.StagedDuty1 => value.Staged1,
        So6120MailboxRegister.StagedDuty2 => value.Staged2,
        So6120MailboxRegister.Override => value.Override,
        So6120MailboxRegister.Selector => value.Selector,
        _ => throw new InvalidDataException(),
    };
    private static So6120DutyControls Set(So6120DutyControls value, So6120MailboxRegister register, byte data) => register switch
    {
        So6120MailboxRegister.StagedDuty1 => value with { Staged1 = data },
        So6120MailboxRegister.StagedDuty2 => value with { Staged2 = data },
        So6120MailboxRegister.Override => value with { Override = data },
        So6120MailboxRegister.Selector => value with { Selector = data },
        _ => throw new InvalidDataException(),
    };

    private enum Kind
    {
        Binding, EntryClaim, ExitClaim, Observation, Intent, TransferBegin, RequestPrepared, Publication,
        TransferResult, TransferNotSubmitted, IntentNotSubmitted, Checkpoint, Fence, Complete, PreparationClaim, ApprovedPlan, StopClaim,
    }
    private enum Phase { Empty, Bound, Entry, EntryStopped, Exit, ExitStopped, Complete, Preparing, PlanApproved, Stop }
    private sealed record Record(long Sequence, Kind Kind, JsonElement Data);
    private sealed record ApprovedPlan(So6120SilentTrialPlan Plan, So6120SilentTrialApproval Approval);
    private sealed record Claim(DirectDutyOwner Owner, PerformancePreset Profile);
    private sealed record StopClaim(So6120DutyCheckpoint Checkpoint, So6120DutyObservation Observation);
    private sealed record Intent(DirectDutyOwner Owner, So6120DutyMutation Mutation, So6120DutyObservation Observation);
    private sealed record Resolution(DirectDutyOwner Owner, So6120DutyMutation Mutation, So6120DutyFailure Reason);
    private sealed record Fence(DirectDutyOwner Owner, So6120DutyFailure Reason);
    private sealed record Completion(DirectDutyOwner Owner, long Generation, long ObservationSequence);
    private sealed class State
    {
        public int FormatVersion;
        public So6120SilentTrialPlan? Plan;
        public long Sequence;
        public Phase Phase;
        public So6120JournalIdentity? Identity;
        public So6120DutyObservation? Observation;
        public So6120DutyCheckpoint? Checkpoint;
        public Intent? Intent;
        public So6120JournalTransfer? Transfer;
        public bool Fenced, WriteAttempted, WriteConfirmed, PermissionPossible, PermissionRestored, PermissionBeforeTransfer;
        public HashSet<Guid> Ids = [];
        public List<So6120MailboxRequest> Requests = [];
        public HashSet<Guid> Published = [];
        public State() { }
        public State(State other)
        {
            FormatVersion = other.FormatVersion; Plan = other.Plan;
            Sequence = other.Sequence; Phase = other.Phase; Identity = other.Identity;
            Observation = other.Observation; Checkpoint = other.Checkpoint; Intent = other.Intent; Transfer = other.Transfer;
            Fenced = other.Fenced; WriteAttempted = other.WriteAttempted; WriteConfirmed = other.WriteConfirmed;
            PermissionPossible = other.PermissionPossible; PermissionRestored = other.PermissionRestored;
            PermissionBeforeTransfer = other.PermissionBeforeTransfer;
            Ids = [.. other.Ids]; Requests = [.. other.Requests]; Published = [.. other.Published];
        }
    }
}
