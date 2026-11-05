using OpenBlade.Contracts;

namespace OpenBlade.Capture;

internal sealed record DirectDutyOwner(Guid OperationId, string DeviceIdentity, string FirmwareEvidence, Guid ControllerSession);

internal sealed record DirectDutyAutoDestination(PerformancePreset Performance, CustomPerformanceSettings? Custom = null);

// Firmware-observable fields only; legacy profile GPU offsets are host intent.
internal sealed record DirectDutyCustomState(CustomPerformanceLevel Cpu, CustomPerformanceLevel Gpu, bool MaximumFans);

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
    PerformancePreset? NormalProfile,
    byte ManualFlags,
    byte OverrideFlags,
    byte Duty1,
    byte Duty2,
    DirectDutyCustomState? RetainedCustom = null);

// Bank is observed only through an open gate. Null never means a cached bank.
internal sealed record DirectDutyAccessState(byte? Bank, bool GateOpen);

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
internal enum DirectDutyExitFailure { None, InvalidOwnership, AdmissionRefused, AcquisitionFailed, UnsupportedDestination, ObservationRejected, StateChanged, JournalFailed, NotSubmitted, Uncertain, Canceled }
// Acquisition provenance, retained after session release; this is not a live lock state.
internal enum DirectDutyClaimState { NotAcquired, Unknown, Acquired }

internal sealed record DirectDutyConfirmation(long ObservationSequence, long? PreservedBankSequence = null);

internal sealed record DirectDutyExitResult(
    DirectDutyExitFailure Failure,
    DirectDutyExitStep? Step,
    int SubmissionAttempts,
    bool FanHandbackConfirmed,
    bool CleanupConfirmed)
{
    public DirectDutyExitFailure Cause { get; init; }
    public DirectDutyClaimState ClaimState { get; init; }
    public bool SessionReleaseFailed { get; init; }
    public bool Success => Failure == DirectDutyExitFailure.None && CleanupConfirmed && !SessionReleaseFailed;
    public bool RequiresReadOnlyReconciliation => !Success;
}

internal interface IDirectDutyAutoExitPort
{
    // Must atomically and durably claim this single-use OperationId, rejecting any prior
    // claim/intent across instances/restarts, except a new attempt explicitly authorized by
    // DirectDutyUnsubmittedReconciliation. No observation or caller result grants reuse.
    // Validate the durable entry journal, exact model/firmware command evidence, destination
    // and power/session admission before returning an exclusive session. Null guarantees no
    // claim was acquired; an exception leaves claim status unknown. Failed acquisition must
    // release native exclusion, retain durable evidence, and never submit a hardware write.
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
    // A true value requires exact-model evidence that the gate-close operation preserves
    // the bank, a dedicated gate-only read, and exclusion spanning both observations.
    bool SupportsBankPreservingGateClose { get; }
    // Models with Custom/maximum-fan state must expose its actual retained values even in
    // FE or other presets. If they cannot be independently read, refuse acquisition; null
    // is reserved for model contracts where these controls are demonstrably inapplicable.
    Task<DirectDutyProof<DirectDutyEcState>> ReadEcAsync(CancellationToken cancellationToken);
    // Open gate: freshly read bank and gate. Closed gate: freshly read only the dedicated
    // gate field, with Bank=null. Never reinterpret a generic-read error as gate evidence.
    Task<DirectDutyProof<DirectDutyAccessState>> ReadAccessAsync(CancellationToken cancellationToken);
    Task RecordIntentAsync(DirectDutyExitMutation mutation,
        DirectDutyProof<DirectDutyEcState> expectedEc,
        DirectDutyProof<DirectDutyAccessState> expectedAccess,
        CancellationToken cancellationToken);
    // Fresh exact identity/controller/power/session/ownership and expected-current admission
    // after journaling, held through submission and independent confirmation.
    Task<bool> AdmitSubmissionAsync(DirectDutyExitMutation mutation,
        DirectDutyProof<DirectDutyEcState> expectedEc,
        DirectDutyProof<DirectDutyAccessState> expectedAccess,
        CancellationToken cancellationToken);
    // Always submits exactly one specified catalog operation. Auto changes selector/manual
    // flag only: it must not hide CPU/GPU level or maximum-fan writes inside an Auto step.
    // No cached-state shortcuts, retries, alternative profiles or default destinations.
    // Under the same exclusion, recheck this attempt's current fence and durably mark the
    // submission boundary BEFORE any native write. Cancellation or admission rejection
    // must durably resolve an intent as never submitted before it can qualify for reuse.
    // A NotSubmitted outcome must also retain durable transport proof for reconciliation;
    // inability to establish/persist that proof is Uncertain, not permission to retry.
    Task<DirectDutySubmission> SubmitAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken);
    // Gate closure binds the fresh gate observation to the bank observation stored in its
    // intent, preserved by the reviewed close command under continuous exclusion.
    Task RecordConfirmationAsync(DirectDutyExitMutation mutation, DirectDutyConfirmation confirmation, CancellationToken cancellationToken);
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
            destination = destination with { Custom = custom with { MaximumFans = false, GpuClockOffsets = null } };
        }

        IDirectDutyAutoExitSession? session = null;
        DirectDutyExitFailure failure = DirectDutyExitFailure.None;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Phase = Phase.Acquire;
            progress.ClaimState = DirectDutyClaimState.Unknown;
            session = await port.AcquireOwnedSessionAsync(ownership, destination, cancellationToken);
            if (session is null)
            {
                progress.ClaimState = DirectDutyClaimState.NotAcquired;
                throw new ExitStopped(DirectDutyExitFailure.AdmissionRefused);
            }
            progress.ClaimState = DirectDutyClaimState.Acquired;
            if (!ownership.OriginalGateOpen && !session.SupportsBankPreservingGateClose)
            {
                throw new ExitStopped(DirectDutyExitFailure.AdmissionRefused);
            }

            DirectDutyEcState expected = new(true, null, ownership.AppliedManualFlags,
                (byte)(ownership.OriginalOverride | ownership.OwnedOverrideBits), ownership.AppliedDuty1, ownership.AppliedDuty2);
            DirectDutyAccessState access = new(0, true);
            DirectDutyProof<DirectDutyEcState>? lastEc = null;
            DirectDutyProof<DirectDutyAccessState>? lastAccess = null;
            // FE is admitted only here, under the separately validated owned-entry record.
            await ReadAccessAsync(access);
            await ReadEcAsync(expected, captureRetainedCustom: true);
            expected = lastEc!.State;
            // Auto does not set CPU/GPU levels or the separate maximum-fan flag. Use
            // independent retained state, never captured preferences or the mode cache.
            if (expected.RetainedCustom is { MaximumFans: true }
                || destination.Custom is { } requestedCustom
                    && expected.RetainedCustom != new DirectDutyCustomState(requestedCustom.Cpu, requestedCustom.Gpu, false))
            {
                throw new ExitStopped(DirectDutyExitFailure.UnsupportedDestination);
            }

            DirectDutyEcState automatic = expected with { DirectDuty = false, NormalProfile = destination.Performance, ManualFlags = 0 };
            await MutateEcAsync(DirectDutyExitStep.AutomaticFirst, 0, automatic);
            // Intentionally send the second admitted channel even if the first already made
            // actual state match. A cache or matching value cannot suppress the handback pair.
            await MutateEcAsync(DirectDutyExitStep.AutomaticSecond, 0, automatic);

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
                await MutateAccessAsync(DirectDutyExitStep.RestoreGate, 0, new(null, false));
            }
            await ReadAccessAsync(access);
            progress.Cleanup = true;

            async Task ReadEcAsync(DirectDutyEcState required, bool captureRetainedCustom = false)
            {
                progress.Phase = Phase.Observe;
                DirectDutyProof<DirectDutyEcState> proof = await session.ReadEcAsync(cancellationToken);
                progress.Accept(proof, ownership.Owner);
                if (proof.State.RetainedCustom is { } retained)
                {
                    // The shared constructor validates firmware-representable CPU/GPU levels.
                    _ = new CustomPerformanceSettings(retained.Cpu, retained.Gpu);
                }
                if (captureRetainedCustom) { required = required with { RetainedCustom = proof.State.RetainedCustom }; }
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
                await session.RecordIntentAsync(mutation, lastEc!, lastAccess!, cancellationToken);
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

            async Task ConfirmAsync(DirectDutyExitMutation mutation, long? preservedBankSequence = null)
            {
                progress.Phase = Phase.Journal;
                await session.RecordConfirmationAsync(mutation, new(progress.Sequence, preservedBankSequence), cancellationToken);
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
                long? preservedBankSequence = step == DirectDutyExitStep.RestoreGate ? lastAccess!.Sequence : null;
                var mutation = new DirectDutyExitMutation(step, destination, value);
                await SubmitAsync(mutation);
                await ReadAccessAsync(next);
                access = next;
                await ConfirmAsync(mutation, preservedBankSequence);
            }
        }
        catch (ExitStopped stopped)
        {
            progress.Cause = stopped.Failure;
            failure = progress.Unconfirmed ? DirectDutyExitFailure.Uncertain : stopped.Failure;
        }
        catch (OperationCanceledException)
        {
            progress.Cause = DirectDutyExitFailure.Canceled;
            failure = progress.Phase == Phase.Submit || progress.Unconfirmed
                ? DirectDutyExitFailure.Uncertain : DirectDutyExitFailure.Canceled;
        }
        catch (Exception)
        {
            progress.Cause = progress.Phase == Phase.Acquire ? DirectDutyExitFailure.AcquisitionFailed
                : progress.Phase == Phase.Submit ? DirectDutyExitFailure.Uncertain
                : progress.Phase == Phase.Journal ? DirectDutyExitFailure.JournalFailed
                : progress.Phase == Phase.Admit ? DirectDutyExitFailure.AdmissionRefused
                : DirectDutyExitFailure.ObservationRejected;
            failure = progress.Phase == Phase.Submit || progress.Unconfirmed ? DirectDutyExitFailure.Uncertain : progress.Cause;
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception) { progress.SessionReleaseFailed = true; }
            }
        }
        return progress.Result(failure);
    }

    internal static bool ValidOwnership(DirectDutyOwnership? ownership) => ownership?.Owner is { } owner
        && owner.OperationId != Guid.Empty && owner.ControllerSession != Guid.Empty
        && !string.IsNullOrWhiteSpace(owner.DeviceIdentity) && !string.IsNullOrWhiteSpace(owner.FirmwareEvidence)
        // Current entry evidence covers the eight-bank SO6120 map and permission bit 6.
        // Generic performance selection is not permission to broaden that hardware contract.
        && ownership.OriginalBank <= 7 && ownership.OwnedOverrideBits is 0 or 0x40
        && ((ownership.OriginalOverride | ownership.OwnedOverrideBits) & 0x40) != 0
        && (ownership.OriginalOverride & ownership.OwnedOverrideBits) == 0
        // These are captured input constraints; reject before consuming any claim.
        && ownership.AppliedManualFlags is 0 or 1
        && ValidDestination(ownership.CapturedProfile);

    private static bool ValidDestination(DirectDutyAutoDestination? destination) => destination is not null
        && Enum.IsDefined(destination.Performance)
        && (destination.Performance == PerformancePreset.Custom
            ? CustomPerformanceSettings.IsWellFormed(destination.Custom) : destination.Custom is null);

    private enum Phase { Acquire, Observe, Journal, Admit, Submit }
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
        public DirectDutyExitFailure Cause { get; set; }
        public DirectDutyClaimState ClaimState { get; set; }
        public bool SessionReleaseFailed { get; set; }

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
            new(failure, Step, Submissions, FanHandback, Cleanup)
            {
                Cause = Cause == DirectDutyExitFailure.None ? failure : Cause,
                ClaimState = ClaimState,
                SessionReleaseFailed = SessionReleaseFailed,
            };
    }
}
