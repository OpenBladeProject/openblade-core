using OpenSynapse.Protocol;
using System.Runtime.ExceptionServices;

namespace OpenSynapse.Capture;

public interface IInteractiveApproval
{
    Task<bool> ConfirmAsync(
        BatteryValidationSetting prior,
        BatteryValidationSetting candidate,
        CancellationToken cancellationToken);
}

public enum BatteryValidationPrimaryFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    BaselineQueryFailed,
    UnknownPrior,
    UndefinedPrior,
    UndefinedCandidate,
    SameCandidate,
    ApprovalDeclined,
    ApplyFailed,
    CandidateQueryFailed,
    CandidateMismatch,
}

public enum BatteryValidationRestorationFailure
{
    None,
    RestoreApplyFailed,
    RestoreQueryFailed,
    RestoreMismatch,
}

public sealed record BatteryValidationResult(
    bool ApplyAttempted,
    bool Applied,
    bool ReadBack,
    bool Restored,
    BatteryValidationPrimaryFailure PrimaryFailure,
    BatteryValidationRestorationFailure RestorationFailure)
{
    public bool Success => ApplyAttempted
        && Applied
        && ReadBack
        && Restored
        && PrimaryFailure == BatteryValidationPrimaryFailure.None
        && RestorationFailure == BatteryValidationRestorationFailure.None;
}

public sealed class BatteryValidationSession(
    IBatteryValidationRunFactory factory,
    IInteractiveApproval approval,
    TimeSpan cleanupTimeout)
{
    private readonly TimeSpan _cleanupTimeout = cleanupTimeout > TimeSpan.Zero
        && cleanupTimeout != Timeout.InfiniteTimeSpan
            ? cleanupTimeout
            : throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

    public async Task<BatteryValidationResult> RunAsync(
        BatteryValidationSetting candidate,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(candidate))
        {
            return Abort(BatteryValidationPrimaryFailure.UndefinedCandidate);
        }

        HidSessionResult<IBatteryValidationPort> opened;
        try
        {
            opened = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Abort(BatteryValidationPrimaryFailure.OpenFailed);
        }

        if (IsCallerCancellation(opened.Failure, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (opened.Failure is not null || opened.Value is not { } port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Abort(MapOpenFailure(opened.Failure));
        }

        await using (port.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            HidSessionResult<BatteryValidationSetting> baseline;
            try
            {
                baseline = await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Abort(BatteryValidationPrimaryFailure.BaselineQueryFailed);
            }

            if (IsCallerCancellation(baseline.Failure, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (baseline.Failure is not null)
            {
                return Abort(BatteryValidationPrimaryFailure.BaselineQueryFailed);
            }

            BatteryValidationSetting prior = baseline.Value;
            if (!Enum.IsDefined(prior))
            {
                return Abort(BatteryValidationPrimaryFailure.UndefinedPrior);
            }

            if (candidate == prior)
            {
                return Abort(BatteryValidationPrimaryFailure.SameCandidate);
            }

            bool approved = await approval
                .ConfirmAsync(prior, candidate, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
            {
                return Abort(BatteryValidationPrimaryFailure.ApprovalDeclined);
            }

            bool applyAttempted = false;
            bool applied = false;
            bool readBack = false;
            bool restored = false;
            BatteryValidationPrimaryFailure primaryFailure = BatteryValidationPrimaryFailure.None;
            BatteryValidationRestorationFailure restorationFailure =
                BatteryValidationRestorationFailure.None;
            OperationCanceledException? callerCancellation = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                applyAttempted = true;
                HidSessionResult<HidUnit> apply =
                    await port.ApplyAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (apply.Failure is not null)
                {
                    primaryFailure = BatteryValidationPrimaryFailure.ApplyFailed;
                    if (IsCallerCancellation(apply.Failure, cancellationToken))
                    {
                        callerCancellation = new OperationCanceledException(cancellationToken);
                    }
                }
                else
                {
                    applied = true;
                    HidSessionResult<BatteryValidationSetting> candidateQuery =
                        await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                    if (candidateQuery.Failure is not null)
                    {
                        primaryFailure = BatteryValidationPrimaryFailure.CandidateQueryFailed;
                        if (IsCallerCancellation(candidateQuery.Failure, cancellationToken))
                        {
                            callerCancellation = new OperationCanceledException(cancellationToken);
                        }
                    }
                    else
                    {
                        readBack = candidateQuery.Value == candidate;
                        if (!readBack)
                        {
                            primaryFailure = BatteryValidationPrimaryFailure.CandidateMismatch;
                        }
                    }
                }
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                callerCancellation = exception;
                primaryFailure = applied
                    ? BatteryValidationPrimaryFailure.CandidateQueryFailed
                    : BatteryValidationPrimaryFailure.ApplyFailed;
            }
            catch (Exception)
            {
                primaryFailure = applied
                    ? BatteryValidationPrimaryFailure.CandidateQueryFailed
                    : BatteryValidationPrimaryFailure.ApplyFailed;
            }
            finally
            {
                if (applyAttempted)
                {
                    using CancellationTokenSource cleanup = new(_cleanupTimeout);
                    bool restoreApplied = false;
                    try
                    {
                        HidSessionResult<HidUnit> restoreApply =
                            await port.ApplyAsync(prior, cleanup.Token).ConfigureAwait(false);
                        restoreApplied = restoreApply.Failure is null;
                        if (!restoreApplied)
                        {
                            restorationFailure =
                                BatteryValidationRestorationFailure.RestoreApplyFailed;
                        }
                    }
                    catch (Exception)
                    {
                        restorationFailure =
                            BatteryValidationRestorationFailure.RestoreApplyFailed;
                    }

                    if (restoreApplied)
                    {
                        try
                        {
                            HidSessionResult<BatteryValidationSetting> restoreQuery =
                                await port.QueryAsync(cleanup.Token).ConfigureAwait(false);
                            if (restoreQuery.Failure is not null)
                            {
                                restorationFailure =
                                    BatteryValidationRestorationFailure.RestoreQueryFailed;
                            }
                            else
                            {
                                restored = restoreQuery.Value == prior;
                                if (!restored)
                                {
                                    restorationFailure =
                                        BatteryValidationRestorationFailure.RestoreMismatch;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            restorationFailure =
                                BatteryValidationRestorationFailure.RestoreQueryFailed;
                        }
                    }
                }
            }

            if (callerCancellation is not null)
            {
                ExceptionDispatchInfo.Capture(callerCancellation).Throw();
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new BatteryValidationResult(
                ApplyAttempted: applyAttempted,
                Applied: applied,
                ReadBack: readBack,
                Restored: restored,
                PrimaryFailure: primaryFailure,
                RestorationFailure: restorationFailure);
        }
    }

    private static bool IsCallerCancellation(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && failure is HidSessionFailureKind.Canceled or HidSessionFailureKind.QueuedCanceled;

    private static BatteryValidationPrimaryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => BatteryValidationPrimaryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => BatteryValidationPrimaryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                BatteryValidationPrimaryFailure.ConflictDetectionFailed,
            _ => BatteryValidationPrimaryFailure.OpenFailed,
        };

    private static BatteryValidationResult Abort(BatteryValidationPrimaryFailure failure) => new(
        ApplyAttempted: false,
        Applied: false,
        ReadBack: false,
        Restored: false,
        PrimaryFailure: failure,
        RestorationFailure: BatteryValidationRestorationFailure.None);
}
