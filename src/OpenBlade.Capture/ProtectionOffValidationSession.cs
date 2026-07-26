using System.Runtime.ExceptionServices;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public enum ProtectionOffValidationPrimaryFailure
{
    None,
    WrongTarget,
    Conflict,
    ConflictDetectionFailed,
    OpenFailed,
    BaselineProtectionQueryFailed,
    BaselineTemporaryQueryFailed,
    BaselineNotLimit80,
    BaselineTemporaryActive,
    ApprovalDeclined,
    ApplyFailed,
    CandidateProtectionQueryFailed,
    CandidateTemporaryQueryFailed,
    CandidateProtectionMismatch,
    CandidateTemporaryActive,
}

public enum ProtectionOffValidationRestorationFailure
{
    None,
    RestoreApplyFailed,
    RestoreProtectionQueryFailed,
    RestoreTemporaryQueryFailed,
    RestoreProtectionMismatch,
    RestoreTemporaryActive,
}

public sealed record ProtectionOffValidationResult(
    BatteryValidationSetting? BaselineProtection,
    bool? BaselineTemporaryFullCharge,
    bool ApplyAttempted,
    bool Applied,
    BatteryValidationSetting? CandidateProtection,
    bool? CandidateTemporaryFullCharge,
    bool ReadBack,
    BatteryValidationSetting? RestorationProtection,
    bool? RestorationTemporaryFullCharge,
    ProtectionOffValidationPrimaryFailure PrimaryFailure,
    ProtectionOffValidationRestorationFailure RestorationFailure)
{
    public bool Restored =>
        RestorationProtection == BatteryValidationSetting.Limit80
        && RestorationTemporaryFullCharge == false
        && RestorationFailure == ProtectionOffValidationRestorationFailure.None;

    public bool Success => ApplyAttempted
        && Applied
        && ReadBack
        && Restored
        && PrimaryFailure == ProtectionOffValidationPrimaryFailure.None;
}

public sealed class ProtectionOffValidationSession(
    IProtectionOffValidationRunFactory factory,
    IInteractiveApproval approval,
    TimeSpan cleanupTimeout)
{
    private readonly TimeSpan cleanupTimeout =
        cleanupTimeout > TimeSpan.Zero && cleanupTimeout != Timeout.InfiniteTimeSpan
            ? cleanupTimeout
            : throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

    public async Task<ProtectionOffValidationResult> RunAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<IProtectionOffValidationPort> opened;
        try
        {
            opened = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Abort(ProtectionOffValidationPrimaryFailure.OpenFailed);
        }

        if (opened.Value is not { } port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Abort(MapOpenFailure(opened.Failure));
        }

        await using (port.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.Failure is not null)
            {
                return Abort(MapOpenFailure(opened.Failure));
            }

            HidSessionResult<BatteryValidationSetting> baselineProtection =
                await port.QueryProtectionAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (baselineProtection.Failure is not null)
            {
                return Abort(ProtectionOffValidationPrimaryFailure.BaselineProtectionQueryFailed);
            }

            HidSessionResult<bool> baselineTemporary =
                await port.QueryTemporaryFullChargeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (baselineTemporary.Failure is not null)
            {
                return Abort(ProtectionOffValidationPrimaryFailure.BaselineTemporaryQueryFailed) with
                {
                    BaselineProtection = baselineProtection.Value,
                };
            }

            ProtectionOffValidationResult baseline = Abort(
                ProtectionOffValidationPrimaryFailure.None) with
            {
                BaselineProtection = baselineProtection.Value,
                BaselineTemporaryFullCharge = baselineTemporary.Value,
            };
            if (baselineProtection.Value != BatteryValidationSetting.Limit80)
            {
                return baseline with
                {
                    PrimaryFailure = ProtectionOffValidationPrimaryFailure.BaselineNotLimit80,
                };
            }

            if (baselineTemporary.Value)
            {
                return baseline with
                {
                    PrimaryFailure = ProtectionOffValidationPrimaryFailure.BaselineTemporaryActive,
                };
            }

            bool approved = await approval.ConfirmAsync(
                BatteryValidationSetting.Limit80,
                BatteryValidationSetting.ProtectionOff,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
            {
                return baseline with
                {
                    PrimaryFailure = ProtectionOffValidationPrimaryFailure.ApprovalDeclined,
                };
            }

            return await RunApprovedAsync(
                port,
                baselineProtection.Value,
                baselineTemporary.Value,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProtectionOffValidationResult> RunApprovedAsync(
        IProtectionOffValidationPort port,
        BatteryValidationSetting baselineProtection,
        bool baselineTemporary,
        CancellationToken cancellationToken)
    {
        bool applyAttempted = false;
        bool applied = false;
        BatteryValidationSetting? candidateProtection = null;
        bool? candidateTemporary = null;
        bool readBack = false;
        BatteryValidationSetting? restorationProtection = null;
        bool? restorationTemporary = null;
        ProtectionOffValidationPrimaryFailure primaryFailure = ProtectionOffValidationPrimaryFailure.None;
        ProtectionOffValidationRestorationFailure restorationFailure = ProtectionOffValidationRestorationFailure.None;
        OperationCanceledException? callerCancellation = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            applyAttempted = true;
            HidSessionResult<HidUnit> apply =
                await port.ApplyProtectionOffAsync(cancellationToken).ConfigureAwait(false);
            if (apply.Failure is not null)
            {
                primaryFailure = ProtectionOffValidationPrimaryFailure.ApplyFailed;
                if (IsCallerCancellation(apply.Failure, cancellationToken))
                {
                    callerCancellation = new OperationCanceledException(cancellationToken);
                }
            }
            else
            {
                applied = true;
                HidSessionResult<BatteryValidationSetting> protection =
                    await port.QueryProtectionAsync(cancellationToken).ConfigureAwait(false);
                if (protection.Failure is null)
                {
                    candidateProtection = protection.Value;
                }
                else
                {
                    primaryFailure = ProtectionOffValidationPrimaryFailure.CandidateProtectionQueryFailed;
                }

                HidSessionResult<bool> temporary =
                    await port.QueryTemporaryFullChargeAsync(cancellationToken).ConfigureAwait(false);
                if (temporary.Failure is null)
                {
                    candidateTemporary = temporary.Value;
                }
                else if (primaryFailure == ProtectionOffValidationPrimaryFailure.None)
                {
                    primaryFailure = ProtectionOffValidationPrimaryFailure.CandidateTemporaryQueryFailed;
                }

                if (primaryFailure == ProtectionOffValidationPrimaryFailure.None)
                {
                    if (candidateProtection != BatteryValidationSetting.ProtectionOff)
                    {
                        primaryFailure = ProtectionOffValidationPrimaryFailure.CandidateProtectionMismatch;
                    }
                    else if (candidateTemporary != false)
                    {
                        primaryFailure = ProtectionOffValidationPrimaryFailure.CandidateTemporaryActive;
                    }
                    else
                    {
                        readBack = true;
                    }
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            callerCancellation = exception;
            primaryFailure = applied
                ? ProtectionOffValidationPrimaryFailure.CandidateProtectionQueryFailed
                : ProtectionOffValidationPrimaryFailure.ApplyFailed;
        }
        catch
        {
            primaryFailure = applied
                ? ProtectionOffValidationPrimaryFailure.CandidateProtectionQueryFailed
                : ProtectionOffValidationPrimaryFailure.ApplyFailed;
        }
        finally
        {
            if (applyAttempted)
            {
                using CancellationTokenSource restoreApplyCleanup = new(cleanupTimeout);
                try
                {
                    HidSessionResult<HidUnit> restore = await port
                        .RestoreLimit80Async(restoreApplyCleanup.Token).ConfigureAwait(false);
                    if (restore.Failure is not null)
                    {
                        restorationFailure = ProtectionOffValidationRestorationFailure.RestoreApplyFailed;
                    }
                    else
                    {
                        using CancellationTokenSource restoreProtectionCleanup = new(cleanupTimeout);
                        HidSessionResult<BatteryValidationSetting> protection = await port
                            .QueryProtectionAsync(restoreProtectionCleanup.Token).ConfigureAwait(false);
                        if (protection.Failure is null)
                        {
                            restorationProtection = protection.Value;
                        }
                        else
                        {
                            restorationFailure = ProtectionOffValidationRestorationFailure.RestoreProtectionQueryFailed;
                        }

                        using CancellationTokenSource restoreTemporaryCleanup = new(cleanupTimeout);
                        HidSessionResult<bool> temporary = await port
                            .QueryTemporaryFullChargeAsync(restoreTemporaryCleanup.Token).ConfigureAwait(false);
                        if (temporary.Failure is null)
                        {
                            restorationTemporary = temporary.Value;
                        }
                        else if (restorationFailure == ProtectionOffValidationRestorationFailure.None)
                        {
                            restorationFailure = ProtectionOffValidationRestorationFailure.RestoreTemporaryQueryFailed;
                        }

                        if (restorationFailure == ProtectionOffValidationRestorationFailure.None)
                        {
                            if (restorationProtection != BatteryValidationSetting.Limit80)
                            {
                                restorationFailure = ProtectionOffValidationRestorationFailure.RestoreProtectionMismatch;
                            }
                            else if (restorationTemporary != false)
                            {
                                restorationFailure = ProtectionOffValidationRestorationFailure.RestoreTemporaryActive;
                            }
                        }
                    }
                }
                catch
                {
                    restorationFailure = ProtectionOffValidationRestorationFailure.RestoreApplyFailed;
                }
            }
        }

        if (callerCancellation is not null)
        {
            ExceptionDispatchInfo.Capture(callerCancellation).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(
            baselineProtection,
            baselineTemporary,
            applyAttempted,
            applied,
            candidateProtection,
            candidateTemporary,
            readBack,
            restorationProtection,
            restorationTemporary,
            primaryFailure,
            restorationFailure);
    }

    private static bool IsCallerCancellation(
        HidSessionFailureKind? failure,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && failure is HidSessionFailureKind.Canceled or HidSessionFailureKind.QueuedCanceled;

    private static ProtectionOffValidationPrimaryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked => ProtectionOffValidationPrimaryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked => ProtectionOffValidationPrimaryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed => ProtectionOffValidationPrimaryFailure.ConflictDetectionFailed,
            _ => ProtectionOffValidationPrimaryFailure.OpenFailed,
        };

    private static ProtectionOffValidationResult Abort(
        ProtectionOffValidationPrimaryFailure failure) => new(
            null, null, false, false, null, null, false, null, null,
            failure,
            ProtectionOffValidationRestorationFailure.None);
}
