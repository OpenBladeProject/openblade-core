using OpenSynapse.Protocol;
using System.Runtime.ExceptionServices;

namespace OpenSynapse.Capture;

public readonly record struct BatteryQueryResult(BatteryValidationSetting? Setting)
{
    public static BatteryQueryResult Unknown { get; } = new(null);
}

public sealed record ValidationConflictCheck(
    IReadOnlyList<string> Conflicts,
    string? DetectionFailure)
{
    public bool Safe => Conflicts.Count == 0 && DetectionFailure is null;
}

public interface IValidationConflictGuard
{
    ValidationConflictCheck Check();
}

public interface IBatteryValidationPort
{
    Task<BatteryQueryResult> QueryAsync(CancellationToken cancellationToken);

    Task ApplyAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken);
}

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
    IBatteryValidationPort port,
    IValidationConflictGuard conflictGuard,
    IInteractiveApproval approval,
    TimeSpan cleanupTimeout)
{
    private readonly TimeSpan _cleanupTimeout = cleanupTimeout > TimeSpan.Zero
        && cleanupTimeout != Timeout.InfiniteTimeSpan
            ? cleanupTimeout
            : throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

    public async Task<BatteryValidationResult> RunAsync(
        string? model,
        HidInterfaceInfo? interfaceInfo,
        BatteryValidationSetting candidate,
        CancellationToken cancellationToken)
    {
        if (!TargetBladeDevice.IsExactInteractiveValidationTarget(model, interfaceInfo))
        {
            return Abort(BatteryValidationPrimaryFailure.WrongTarget);
        }

        ValidationConflictCheck initialConflict = conflictGuard.Check();
        if (!initialConflict.Safe)
        {
            return Abort(initialConflict.DetectionFailure is null
                ? BatteryValidationPrimaryFailure.Conflict
                : BatteryValidationPrimaryFailure.ConflictDetectionFailed);
        }

        if (!Enum.IsDefined(candidate))
        {
            return Abort(BatteryValidationPrimaryFailure.UndefinedCandidate);
        }

        BatteryQueryResult priorResult = await port.QueryAsync(cancellationToken);
        if (priorResult.Setting is null)
        {
            return Abort(BatteryValidationPrimaryFailure.UnknownPrior);
        }

        BatteryValidationSetting prior = priorResult.Setting.Value;
        if (!Enum.IsDefined(prior))
        {
            return Abort(BatteryValidationPrimaryFailure.UndefinedPrior);
        }

        if (candidate == prior)
        {
            return Abort(BatteryValidationPrimaryFailure.SameCandidate);
        }

        if (!await approval.ConfirmAsync(prior, candidate, cancellationToken))
        {
            return Abort(BatteryValidationPrimaryFailure.ApprovalDeclined);
        }

        ValidationConflictCheck preApplyConflict = conflictGuard.Check();
        if (!preApplyConflict.Safe)
        {
            return Abort(preApplyConflict.DetectionFailure is null
                ? BatteryValidationPrimaryFailure.Conflict
                : BatteryValidationPrimaryFailure.ConflictDetectionFailed);
        }

        bool applied = false;
        bool readBack = false;
        bool restored = false;
        BatteryValidationPrimaryFailure primaryFailure = BatteryValidationPrimaryFailure.None;
        BatteryValidationRestorationFailure restorationFailure =
            BatteryValidationRestorationFailure.None;
        OperationCanceledException? callerCancellation = null;

        try
        {
            await port.ApplyAsync(candidate, cancellationToken);
            applied = true;

            BatteryQueryResult candidateResult = await port.QueryAsync(cancellationToken);
            readBack = candidateResult.Setting == candidate;
            if (!readBack)
            {
                primaryFailure = BatteryValidationPrimaryFailure.CandidateMismatch;
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
            using CancellationTokenSource cleanup = new(_cleanupTimeout);
            bool restoreApplied = false;
            try
            {
                await port.ApplyAsync(prior, cleanup.Token);
                restoreApplied = true;
            }
            catch (Exception)
            {
                restorationFailure = BatteryValidationRestorationFailure.RestoreApplyFailed;
            }

            if (restoreApplied)
            {
                try
                {
                    BatteryQueryResult restoredResult = await port.QueryAsync(cleanup.Token);
                    restored = restoredResult.Setting == prior;
                    if (!restored)
                    {
                        restorationFailure = BatteryValidationRestorationFailure.RestoreMismatch;
                    }
                }
                catch (Exception)
                {
                    restorationFailure = BatteryValidationRestorationFailure.RestoreQueryFailed;
                }
            }
        }

        if (callerCancellation is not null)
        {
            ExceptionDispatchInfo.Capture(callerCancellation).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new BatteryValidationResult(
            ApplyAttempted: true,
            Applied: applied,
            ReadBack: readBack,
            Restored: restored,
            PrimaryFailure: primaryFailure,
            RestorationFailure: restorationFailure);
    }

    private static BatteryValidationResult Abort(BatteryValidationPrimaryFailure failure) => new(
        ApplyAttempted: false,
        Applied: false,
        ReadBack: false,
        Restored: false,
        PrimaryFailure: failure,
        RestorationFailure: BatteryValidationRestorationFailure.None);
}
