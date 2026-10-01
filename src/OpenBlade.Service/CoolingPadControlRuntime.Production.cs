using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed partial class CoolingPadControlRuntime
{
    private readonly bool productionLifecycle;
    private bool parked;
    private bool planningOnly;
    private bool knownUnsubmitted;
    private string? restorationOwner;
    private long restorationStartedAt;
    private static readonly TimeSpan RestorationWait = TimeSpan.FromMinutes(10);

    internal async Task<CoolingPadControlStatus> ParkForHyperBoostEntryAsync(CancellationToken token)
    {
        RequestHyperBoostEntryPark();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (await TryParkSafeRevocationAsync().ConfigureAwait(false)) { return status; }
            await CloseAsync().ConfigureAwait(false);
            return Pause(CoolingPadControlPauseReason.HyperBoostActive);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> TryParkAsync(CoolingPadControlPauseReason reason)
    {
        if (!productionLifecycle || port is null || restorationOwner is not null || status.SubmissionStateUnknown
            || !(parked || status.State == CoolingPadControlState.Running)
            || armedEvidence?.Evidence?.Phase is not (CoolingPadMutationPhase.Acknowledged or CoolingPadMutationPhase.Parked)) { return false; }
        try { await CheckpointAsync(CoolingPadMutationPhase.Parked, null, CancellationToken.None).ConfigureAwait(false); }
        catch { return false; }
        parked = true;
        status = status with
        {
            State = CoolingPadControlState.Paused,
            PauseReason = reason,
            ResumeRequired = true,
            RetainedSessionResumable = true,
            QuietRestorationAvailable = true
        };
        return true;
    }

    private async Task CheckpointAsync(CoolingPadMutationPhase phase, int? demandRpm, CancellationToken token)
    {
        if (!productionLifecycle) { return; }
        if (armedEvidence is null) { throw new InvalidDataException("Missing owned mutation generation."); }
        var expected = armedEvidence;
        armedEvidence = await EvidenceAsync(t => evidenceStore.CheckpointAsync(expected, phase, demandRpm, t), token).ConfigureAwait(false);
        if (armedEvidence.Invalid || armedEvidence.Evidence?.Phase != phase || !armedEvidence.BlocksAdmission)
        { throw new InvalidDataException("Pad checkpoint was not confirmed."); }
        status = status with { RetainedEffect = true };
    }

    internal async Task RefreshProductionStateAsync(CancellationToken token)
    {
        if (!productionLifecycle) { return; }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (restorationOwner is not null && clock.GetElapsedTime(restorationStartedAt) >= RestorationWait)
            { await FailAsync(new TimeoutException("Physical confirmation expired.")).ConfigureAwait(false); }
            if (port is not null) { return; }
            var loaded = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
            if (loaded.BlocksAdmission)
            {
                bool unknown = loaded.Invalid || loaded.Evidence?.Phase is null or CoolingPadMutationPhase.Pending or CoolingPadMutationPhase.RestorationPending;
                status = status with
                {
                    RetainedEffect = true,
                    ResumeRequired = true,
                    SubmissionStateUnknown = unknown,
                    PauseReason = unknown ? CoolingPadControlPauseReason.SubmissionUnknown : CoolingPadControlPauseReason.ExplicitResumeRequired,
                    LastAcknowledgedDemand = loaded.Evidence?.LastAcknowledgedDemandRpm is { } rpm ? new(rpm) : null
                };
            }
        }
        catch
        {
            status = status with
            {
                RetainedEffect = true,
                SubmissionStateUnknown = true,
                ResumeRequired = true,
                PauseReason = CoolingPadControlPauseReason.SubmissionUnknown
            };
        }
        finally { gate.Release(); }
    }

    // Each IPC request submits at most one stage. The fresh prompt is bound to
    // its user/session, operation nonce, stage, monotonic deadline and owned ledger.
    internal async Task<ControlOperationResult> RestoreQuietAsync(string owner, Guid operationId,
        CoolingPadRestorationStage stage, bool physicallyConfirmed, Func<bool> sessionAdmission, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!productionLifecycle || string.IsNullOrWhiteSpace(owner) || !Enum.IsDefined(stage))
            { return new(ControlOperationStatus.Blocked, "Cooling-pad restoration is unavailable."); }
            if (stage == CoolingPadRestorationStage.Begin)
            {
                if (operationId != Guid.Empty || restorationOwner is not null || !physicallyConfirmed
                    || port is not ICoolingPadDeveloperRestorationPort || status.SubmissionStateUnknown
                    || !(parked || status.State == CoolingPadControlState.Running))
                { return new(ControlOperationStatus.Blocked, "Restoration requires a healthy retained session and a fresh normal-pad confirmation."); }
                long generation = RevokeForRetainedEpoch(out bool retainedEpochSafe);
                if (!retainedEpochSafe)
                {
                    await CloseAsync().ConfigureAwait(false);
                    Pause(CoolingPadControlPauseReason.SessionChanged);
                    return new(ControlOperationStatus.Blocked, "The retained cooling-pad epoch was invalidated.");
                }
                runningGeneration = generation;
                epochAdmission = sessionAdmission;
                revoked = false;
                if (!Allowed(token)) { throw new OperationCanceledException(token); }
                restorationOwner = owner;
                operationId = Guid.NewGuid();
                parked = false;
                status = status with { State = CoolingPadControlState.Paused, RetainedSessionResumable = false, QuietRestorationAvailable = false };
            }
            else
            {
                if (restorationOwner != owner || status.Restoration is not { } prompt
                    || prompt.OperationId != operationId || prompt.Stage != stage)
                { return new(ControlOperationStatus.Blocked, "The restoration prompt is stale or belongs to another session."); }
                if (!physicallyConfirmed || clock.GetElapsedTime(restorationStartedAt) >= RestorationWait
                    || !sessionAdmission() || !Allowed(token))
                { throw new OperationCanceledException("Restoration confirmation was rejected, expired or revoked."); }
            }
            var current = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
            if (current != armedEvidence) { throw new InvalidDataException("The pad evidence generation changed."); }
            var restoration = (ICoolingPadDeveloperRestorationPort)port!;
            switch (stage)
            {
                case CoolingPadRestorationStage.Begin:
                    RequireWrite(await PortAsync(() => restoration.ReassertMediumAsync(() => Allowed(token), token), token).ConfigureAwait(false));
                    Prompt(CoolingPadRestorationStage.Medium);
                    break;
                case CoolingPadRestorationStage.Medium:
                    RequireWrite(await PortAsync(() => restoration.ReassertQuietAsync(() => Allowed(token), token), token).ConfigureAwait(false));
                    Prompt(CoolingPadRestorationStage.Quiet);
                    break;
                case CoolingPadRestorationStage.Quiet:
                    RequireWrite(await PortAsync(() => restoration.RestoreRawAsync(() => Allowed(token), token), token).ConfigureAwait(false));
                    Prompt(CoolingPadRestorationStage.Final);
                    break;
                case CoolingPadRestorationStage.Final:
                    var resolved = await EvidenceAsync(t => evidenceStore.ResolveAsync(armedEvidence!, t), token).ConfigureAwait(false);
                    if (resolved.Invalid || resolved.BlocksAdmission) { throw new InvalidDataException("Resolution was not confirmed."); }
                    armedEvidence = null;
                    Volatile.Write(ref retainedMutationEvidence, 0);
                    Revoke();
                    await CloseAsync().ConfigureAwait(false);
                    status = status with
                    {
                        Requested = CoolingPadControlSettings.Stopped,
                        State = CoolingPadControlState.Stopped,
                        PauseReason = CoolingPadControlPauseReason.None,
                        PlannedDemand = null,
                        ResumeRequired = false,
                        RetainedEffect = false,
                        SubmissionStateUnknown = false
                    };
                    return new(ControlOperationStatus.Applied, "Preferred quiet restoration and your physical checks completed. Firmware ownership and fan RPM are not measured.");
            }
            return new(ControlOperationStatus.Applied, "Restoration stage acknowledged. Check the pad before confirming the next stage.");

            void Prompt(CoolingPadRestorationStage next)
            {
                restorationStartedAt = clock.GetTimestamp();
                status = status with { Restoration = new(operationId, next, clock.GetUtcNow() + RestorationWait) };
            }
        }
        catch (Exception exception)
        {
            await FailAsync(exception).ConfigureAwait(false);
            return new(ControlOperationStatus.Blocked, "Restoration stopped. The pad may retain its last demand; no automatic recovery will run.");
        }
        finally { gate.Release(); }
    }
}
