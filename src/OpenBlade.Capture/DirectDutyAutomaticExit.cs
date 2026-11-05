using OpenBlade.Contracts;

namespace OpenBlade.Capture;

internal sealed record DirectDutyOwner(Guid OperationId, string DeviceIdentity, string FirmwareEvidence, Guid ControllerSession);

internal sealed record DirectDutyAutoDestination(PerformancePreset Performance, CustomPerformanceSettings? Custom = null);

// Produced by a future reviewed entry transaction, never inferred from merely observing FE.
internal sealed record DirectDutyOwnership(
    DirectDutyOwner Owner,
    DirectDutyAutoDestination CapturedProfile,
    byte OriginalOverride,
    byte OwnedOverrideBits,
    byte OriginalDuty1,
    byte OriginalDuty2,
    byte AppliedDuty1,
    byte AppliedDuty2,
    byte AppliedManualFlags,
    byte OriginalBank,
    bool OriginalGateOpen);

internal sealed record DirectDutyEcState(
    bool DirectDuty,
    DirectDutyAutoDestination? NormalProfile,
    byte ManualFlags,
    byte OverrideFlags,
    byte Duty1,
    byte Duty2);

internal sealed record DirectDutyAccessState(byte Bank, bool GateOpen);

internal sealed record DirectDutyProof<T>(
    DirectDutyOwner Owner,
    long Sequence,
    bool Independent,
    bool ExclusiveAndSettled,
    bool Active,
    T State);

internal enum DirectDutyExitStep
{
    AutomaticFirst,
    AutomaticSecond,
    RestoreOverride,
    RestoreDuty1,
    RestoreDuty2,
    RestoreBank,
    RestoreGate,
}

// Specific operations only: no address, opcode, arbitrary payload, or PWM-register write.
internal sealed record DirectDutyExitMutation(DirectDutyExitStep Step, DirectDutyAutoDestination Destination, byte Value = 0);

internal enum DirectDutySubmission { Acknowledged, NotSubmitted, Uncertain }
internal enum DirectDutyExitFailure { None, InvalidOwnership, AdmissionRefused, ObservationRejected, StateChanged, JournalFailed, NotSubmitted, Uncertain, Canceled }

internal sealed record DirectDutyExitResult(
    DirectDutyExitFailure Failure,
    DirectDutyExitStep? Step,
    int SubmissionAttempts,
    bool FanHandbackConfirmed,
    bool CleanupConfirmed)
{
    public bool Success => Failure == DirectDutyExitFailure.None && CleanupConfirmed;
    public bool RequiresReadOnlyReconciliation => !Success;
}

internal interface IDirectDutyAutoExitPort
{
    // Must atomically and durably claim this single-use OperationId, rejecting any prior
    // claim/intent (including unresolved or completed operations) across instances/restarts.
    // Validate the durable entry journal, exact model/firmware command evidence, destination
    // and power/session admission before returning an exclusive session. Absence of a
    // no-write bank/gate confirmation after access restoration must refuse acquisition.
    // No hardware adapter or CLI route is supplied by this offline development slice.
    Task<IDirectDutyAutoExitSession?> AcquireOwnedSessionAsync(
        DirectDutyOwnership ownership, DirectDutyAutoDestination destination, CancellationToken cancellationToken);
}

internal interface IDirectDutyAutoExitSession : IAsyncDisposable
{
    // The adapter must guarantee bounded operations and exclusion of competing firmware work,
    // shared-bank changes and competing field writers while allowing its own queued worker
    // to complete; serializing host calls alone is insufficient. Reads never acquire a bank or gate
    // by writing. Dispose must not perform cleanup writes or retire an unresolved intent.
    Task<DirectDutyProof<DirectDutyEcState>> ReadEcAsync(CancellationToken cancellationToken);
    Task<DirectDutyProof<DirectDutyAccessState>> ReadAccessAsync(CancellationToken cancellationToken);
    Task RecordIntentAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken);
    // Fresh exact identity/controller/power/session/ownership and expected-current admission
    // after journaling, held through submission and independent confirmation.
    Task<bool> AdmitSubmissionAsync(DirectDutyExitMutation mutation,
        DirectDutyProof<DirectDutyEcState> expectedEc,
        DirectDutyProof<DirectDutyAccessState> expectedAccess,
        CancellationToken cancellationToken);
    // Always submits the specified catalog operation, including both Automatic channels.
    // No cached-state shortcuts, retries, alternative profiles or default destinations.
    Task<DirectDutySubmission> SubmitAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken);
    Task RecordConfirmationAsync(DirectDutyExitMutation mutation, long observationSequence, CancellationToken cancellationToken);
}

// Generic Auto exit under the captured or explicitly intended admitted performance profile.
// Deliberately unbound: no production decoder change, hardware factory or executable command.
internal sealed class DirectDutyAutomaticExit(IDirectDutyAutoExitPort port)
{
    private readonly IDirectDutyAutoExitPort port = port ?? throw new ArgumentNullException(nameof(port));
    private int started;

    public async Task<DirectDutyExitResult> RunAsync(
        DirectDutyOwnership ownership,
        DirectDutyAutoDestination? intendedProfile = null,
        CancellationToken cancellationToken = default)
    {
        var progress = new Progress();
        if (Interlocked.Exchange(ref started, 1) != 0 || !ValidOwnership(ownership))
        {
            return progress.Result(DirectDutyExitFailure.InvalidOwnership);
        }
        DirectDutyAutoDestination destination = intendedProfile ?? ownership.CapturedProfile;
        if (!ValidDestination(destination)) { return progress.Result(DirectDutyExitFailure.InvalidOwnership); }
        if (destination.Custom is { } custom)
        {
            destination = destination with { Custom = custom with { MaximumFans = false } };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using IDirectDutyAutoExitSession? session = await port.AcquireOwnedSessionAsync(ownership, destination, cancellationToken);
            if (session is null) { return progress.Result(DirectDutyExitFailure.AdmissionRefused); }

            DirectDutyEcState expected = new(true, null, ownership.AppliedManualFlags,
                (byte)(ownership.OriginalOverride | ownership.OwnedOverrideBits), ownership.AppliedDuty1, ownership.AppliedDuty2);
            DirectDutyAccessState access = new(0, true);
            DirectDutyProof<DirectDutyEcState>? lastEc = null;
            DirectDutyProof<DirectDutyAccessState>? lastAccess = null;
            // FE is admitted only here, under the separately validated owned-entry record.
            await ReadAccessAsync(access);
            await ReadEcAsync(expected);
            if ((expected.ManualFlags & 0xFE) != 0)
            {
                // Normal captured fan states admit only 0/1. In particular, bit 7 can
                // make the reviewed EC receiver stage rather than apply the Auto flag.
                return progress.Result(DirectDutyExitFailure.StateChanged);
            }

            DirectDutyEcState automatic = expected with { DirectDuty = false, NormalProfile = destination, ManualFlags = 0 };
            await MutateEcAsync(DirectDutyExitStep.AutomaticFirst, 0, automatic);
            // Intentionally send the second admitted channel even if the first already made
            // actual state match. A cache or matching value cannot suppress the handback pair.
            await MutateEcAsync(DirectDutyExitStep.AutomaticSecond, 0, automatic);
            progress.FanHandback = true;

            if (expected.OverrideFlags != ownership.OriginalOverride)
            {
                await MutateEcAsync(DirectDutyExitStep.RestoreOverride, ownership.OriginalOverride,
                    expected with { OverrideFlags = ownership.OriginalOverride });
            }
            if (expected.Duty1 != ownership.OriginalDuty1)
            {
                await MutateEcAsync(DirectDutyExitStep.RestoreDuty1, ownership.OriginalDuty1,
                    expected with { Duty1 = ownership.OriginalDuty1 });
            }
            if (expected.Duty2 != ownership.OriginalDuty2)
            {
                await MutateEcAsync(DirectDutyExitStep.RestoreDuty2, ownership.OriginalDuty2,
                    expected with { Duty2 = ownership.OriginalDuty2 });
            }

            // Independently confirm all EC cleanup before restoring access. Do not try to
            // reread EC through a closed gate or secretly select bank 0 after restoring it.
            await ReadEcAsync(expected);
            if (ownership.OriginalBank != 0)
            {
                await MutateAccessAsync(DirectDutyExitStep.RestoreBank, ownership.OriginalBank,
                    access with { Bank = ownership.OriginalBank });
            }
            if (!ownership.OriginalGateOpen)
            {
                await MutateAccessAsync(DirectDutyExitStep.RestoreGate, 0, access with { GateOpen = false });
            }
            await ReadAccessAsync(access);
            progress.Cleanup = true;
            return progress.Result(DirectDutyExitFailure.None);

            async Task ReadEcAsync(DirectDutyEcState required)
            {
                progress.Phase = Phase.Observe;
                DirectDutyProof<DirectDutyEcState> proof = await session.ReadEcAsync(cancellationToken);
                progress.Accept(proof, ownership.Owner);
                if (proof.State != required) { throw new ExitStopped(DirectDutyExitFailure.StateChanged); }
                lastEc = proof;
            }

            async Task ReadAccessAsync(DirectDutyAccessState required)
            {
                progress.Phase = Phase.Observe;
                DirectDutyProof<DirectDutyAccessState> proof = await session.ReadAccessAsync(cancellationToken);
                progress.Accept(proof, ownership.Owner);
                if (proof.State != required) { throw new ExitStopped(DirectDutyExitFailure.StateChanged); }
                lastAccess = proof;
            }

            async Task SubmitAsync(DirectDutyExitMutation mutation)
            {
                progress.Phase = Phase.Journal;
                await session.RecordIntentAsync(mutation, cancellationToken);
                progress.Phase = Phase.Admit;
                if (!await session.AdmitSubmissionAsync(mutation, lastEc!, lastAccess!, cancellationToken))
                {
                    throw new ExitStopped(DirectDutyExitFailure.AdmissionRefused);
                }
                cancellationToken.ThrowIfCancellationRequested();
                progress.Phase = Phase.Submit;
                progress.Submissions++;
                DirectDutySubmission result = await session.SubmitAsync(mutation, cancellationToken);
                if (result != DirectDutySubmission.Acknowledged)
                {
                    throw new ExitStopped(result == DirectDutySubmission.NotSubmitted
                        ? DirectDutyExitFailure.NotSubmitted : DirectDutyExitFailure.Uncertain);
                }
                progress.Unconfirmed = true;
            }

            async Task ConfirmAsync(DirectDutyExitMutation mutation)
            {
                progress.Phase = Phase.Journal;
                await session.RecordConfirmationAsync(mutation, progress.Sequence, cancellationToken);
                progress.Unconfirmed = false;
            }

            async Task MutateEcAsync(DirectDutyExitStep step, byte value, DirectDutyEcState next)
            {
                progress.Step = step;
                await ReadAccessAsync(access);
                await ReadEcAsync(expected);
                var mutation = new DirectDutyExitMutation(step, destination, value);
                await SubmitAsync(mutation);
                await ReadEcAsync(next);
                expected = next;
                if (step == DirectDutyExitStep.AutomaticSecond) { progress.FanHandback = true; }
                await ConfirmAsync(mutation);
            }

            async Task MutateAccessAsync(DirectDutyExitStep step, byte value, DirectDutyAccessState next)
            {
                progress.Step = step;
                await ReadAccessAsync(access);
                var mutation = new DirectDutyExitMutation(step, destination, value);
                await SubmitAsync(mutation);
                await ReadAccessAsync(next);
                access = next;
                await ConfirmAsync(mutation);
            }
        }
        catch (ExitStopped stopped) { return progress.Result(stopped.Failure); }
        catch (OperationCanceledException)
        {
            return progress.Result(progress.Phase == Phase.Submit || progress.Unconfirmed
                ? DirectDutyExitFailure.Uncertain : DirectDutyExitFailure.Canceled);
        }
        catch (Exception)
        {
            return progress.Result(progress.Phase == Phase.Submit || progress.Unconfirmed
                ? DirectDutyExitFailure.Uncertain
                : progress.Phase == Phase.Journal ? DirectDutyExitFailure.JournalFailed
                : progress.Phase == Phase.Admit ? DirectDutyExitFailure.AdmissionRefused
                : DirectDutyExitFailure.ObservationRejected);
        }
    }

    private static bool ValidOwnership(DirectDutyOwnership? ownership) => ownership?.Owner is { } owner
        && owner.OperationId != Guid.Empty && owner.ControllerSession != Guid.Empty
        && !string.IsNullOrWhiteSpace(owner.DeviceIdentity) && !string.IsNullOrWhiteSpace(owner.FirmwareEvidence)
        // Current entry evidence covers the eight-bank SO6120 map and permission bit 6.
        // Generic performance selection is not permission to broaden that hardware contract.
        && ownership.OriginalBank <= 7 && ownership.OwnedOverrideBits is 0 or 0x40
        && ((ownership.OriginalOverride | ownership.OwnedOverrideBits) & 0x40) != 0
        && (ownership.OriginalOverride & ownership.OwnedOverrideBits) == 0
        && ValidDestination(ownership.CapturedProfile);

    private static bool ValidDestination(DirectDutyAutoDestination? destination) => destination is not null
        && Enum.IsDefined(destination.Performance)
        && (destination.Performance == PerformancePreset.Custom
            ? CustomPerformanceSettings.IsWellFormed(destination.Custom) : destination.Custom is null);

    private enum Phase { Observe, Journal, Admit, Submit }
    private sealed class ExitStopped(DirectDutyExitFailure failure) : Exception
    {
        public DirectDutyExitFailure Failure { get; } = failure;
    }

    private sealed class Progress
    {
        public DirectDutyExitStep? Step { get; set; }
        public Phase Phase { get; set; }
        public long Sequence { get; private set; }
        public int Submissions { get; set; }
        public bool Unconfirmed { get; set; }
        public bool FanHandback { get; set; }
        public bool Cleanup { get; set; }

        public void Accept<T>(DirectDutyProof<T>? proof, DirectDutyOwner owner)
        {
            if (proof is null || proof.Owner != owner || proof.Sequence <= Sequence
                || !proof.Independent || !proof.ExclusiveAndSettled || !proof.Active || proof.State is null)
            {
                throw new ExitStopped(DirectDutyExitFailure.ObservationRejected);
            }
            Sequence = proof.Sequence;
        }

        public DirectDutyExitResult Result(DirectDutyExitFailure failure) =>
            new(failure, Step, Submissions, FanHandback, failure == DirectDutyExitFailure.None && Cleanup);
    }
}
