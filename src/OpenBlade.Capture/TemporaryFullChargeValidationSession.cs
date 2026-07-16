using OpenBlade.Protocol;
using System.Runtime.ExceptionServices;

namespace OpenBlade.Capture;

public interface ITemporaryFullChargeApproval
{
    Task<bool> ConfirmAsync(
        BatteryValidationSetting baselineProtection,
        bool baselineTemporaryFullCharge,
        CancellationToken cancellationToken);
}

public enum TemporaryFullChargeValidationPrimaryFailure
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
    EnableFailed,
    CandidateProtectionQueryFailed,
    CandidateTemporaryQueryFailed,
    CandidateProtectionMismatch,
    CandidateTemporaryInactive,
}

public enum TemporaryFullChargeValidationRestorationFailure
{
    None,
    CancelFailed,
    RestorationProtectionQueryFailed,
    RestorationTemporaryQueryFailed,
    RestorationProtectionMismatch,
    RestorationTemporaryActive,
}

public sealed record TemporaryFullChargeValidationResult(
    BatteryValidationSetting? BaselineProtection,
    bool? BaselineTemporaryFullCharge,
    bool EnableAttempted,
    bool EnableAcknowledged,
    BatteryValidationSetting? CandidateProtection,
    bool? CandidateTemporaryFullCharge,
    bool ReadBack,
    bool CancelAttempted,
    bool CancelAcknowledged,
    BatteryValidationSetting? RestorationProtection,
    bool? RestorationTemporaryFullCharge,
    TemporaryFullChargeValidationPrimaryFailure PrimaryFailure,
    TemporaryFullChargeValidationRestorationFailure RestorationFailure)
{
    public bool Restored =>
        CancelAcknowledged
        && RestorationProtection == BatteryValidationSetting.Limit80
        && RestorationTemporaryFullCharge == false
        && RestorationFailure == TemporaryFullChargeValidationRestorationFailure.None;

    public bool Success => EnableAttempted
        && EnableAcknowledged
        && ReadBack
        && Restored
        && PrimaryFailure == TemporaryFullChargeValidationPrimaryFailure.None;
}

public sealed class TemporaryFullChargeValidationSession(
    ITemporaryFullChargeValidationRunFactory factory,
    ITemporaryFullChargeApproval approval,
    TimeSpan cleanupTimeout)
{
    private readonly TimeSpan cleanupTimeout =
        cleanupTimeout > TimeSpan.Zero && cleanupTimeout != Timeout.InfiniteTimeSpan
            ? cleanupTimeout
            : throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

    public async Task<TemporaryFullChargeValidationResult> RunAsync(
        CancellationToken cancellationToken)
    {
        HidSessionResult<ITemporaryFullChargeValidationPort> opened;
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
            return Abort(TemporaryFullChargeValidationPrimaryFailure.OpenFailed);
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

            HidSessionResult<BatteryValidationSetting> baselineProtection;
            try
            {
                baselineProtection = await port
                    .QueryProtectionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Abort(
                    TemporaryFullChargeValidationPrimaryFailure.BaselineProtectionQueryFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (baselineProtection.Failure is not null)
            {
                return Abort(
                    TemporaryFullChargeValidationPrimaryFailure.BaselineProtectionQueryFailed);
            }

            HidSessionResult<bool> baselineTemporary;
            try
            {
                baselineTemporary = await port
                    .QueryTemporaryFullChargeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Abort(
                    TemporaryFullChargeValidationPrimaryFailure.BaselineTemporaryQueryFailed) with
                {
                    BaselineProtection = baselineProtection.Value,
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (baselineTemporary.Failure is not null)
            {
                return Abort(
                    TemporaryFullChargeValidationPrimaryFailure.BaselineTemporaryQueryFailed) with
                {
                    BaselineProtection = baselineProtection.Value,
                };
            }

            TemporaryFullChargeValidationResult baseline = Abort(
                TemporaryFullChargeValidationPrimaryFailure.None) with
            {
                BaselineProtection = baselineProtection.Value,
                BaselineTemporaryFullCharge = baselineTemporary.Value,
            };

            if (baselineProtection.Value != BatteryValidationSetting.Limit80)
            {
                return baseline with
                {
                    PrimaryFailure =
                        TemporaryFullChargeValidationPrimaryFailure.BaselineNotLimit80,
                };
            }

            if (baselineTemporary.Value)
            {
                return baseline with
                {
                    PrimaryFailure =
                        TemporaryFullChargeValidationPrimaryFailure.BaselineTemporaryActive,
                };
            }

            bool approved = await approval.ConfirmAsync(
                baselineProtection.Value,
                baselineTemporary.Value,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
            {
                return baseline with
                {
                    PrimaryFailure =
                        TemporaryFullChargeValidationPrimaryFailure.ApprovalDeclined,
                };
            }

            return await RunApprovedAsync(port, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TemporaryFullChargeValidationResult> RunApprovedAsync(
        ITemporaryFullChargeValidationPort port,
        CancellationToken cancellationToken)
    {
        bool enableAttempted = false;
        bool enableAcknowledged = false;
        BatteryValidationSetting? candidateProtection = null;
        bool? candidateTemporary = null;
        bool readBack = false;
        bool cancelAttempted = false;
        bool cancelAcknowledged = false;
        BatteryValidationSetting? restorationProtection = null;
        bool? restorationTemporary = null;
        bool candidateProtectionCompleted = false;
        TemporaryFullChargeValidationPrimaryFailure primaryFailure =
            TemporaryFullChargeValidationPrimaryFailure.None;
        TemporaryFullChargeValidationRestorationFailure restorationFailure =
            TemporaryFullChargeValidationRestorationFailure.None;
        OperationCanceledException? callerCancellation = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            enableAttempted = true;
            HidSessionResult<HidUnit> enable = await port
                .ApplyTemporaryFullChargeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (enable.Failure is not null)
            {
                primaryFailure = TemporaryFullChargeValidationPrimaryFailure.EnableFailed;
                if (IsCallerCancellation(enable.Failure, cancellationToken))
                {
                    callerCancellation = new OperationCanceledException(cancellationToken);
                }
            }
            else
            {
                enableAcknowledged = true;
                HidSessionResult<BatteryValidationSetting> protection = await port
                    .QueryProtectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                candidateProtectionCompleted = true;
                if (protection.Failure is null)
                {
                    candidateProtection = protection.Value;
                }
                else
                {
                    primaryFailure = TemporaryFullChargeValidationPrimaryFailure
                        .CandidateProtectionQueryFailed;
                    if (IsCallerCancellation(protection.Failure, cancellationToken))
                    {
                        callerCancellation = new OperationCanceledException(cancellationToken);
                    }
                }

                HidSessionResult<bool> temporary = await port
                    .QueryTemporaryFullChargeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (temporary.Failure is null)
                {
                    candidateTemporary = temporary.Value;
                }
                else if (primaryFailure == TemporaryFullChargeValidationPrimaryFailure.None)
                {
                    primaryFailure = TemporaryFullChargeValidationPrimaryFailure
                        .CandidateTemporaryQueryFailed;
                }

                if (IsCallerCancellation(temporary.Failure, cancellationToken))
                {
                    callerCancellation = new OperationCanceledException(cancellationToken);
                }

                if (primaryFailure == TemporaryFullChargeValidationPrimaryFailure.None)
                {
                    if (candidateProtection != BatteryValidationSetting.Limit80)
                    {
                        primaryFailure = TemporaryFullChargeValidationPrimaryFailure
                            .CandidateProtectionMismatch;
                    }
                    else if (candidateTemporary != true)
                    {
                        primaryFailure = TemporaryFullChargeValidationPrimaryFailure
                            .CandidateTemporaryInactive;
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
            if (primaryFailure == TemporaryFullChargeValidationPrimaryFailure.None)
            {
                primaryFailure = enableAcknowledged
                    ? candidateProtectionCompleted
                        ? TemporaryFullChargeValidationPrimaryFailure.CandidateTemporaryQueryFailed
                        : TemporaryFullChargeValidationPrimaryFailure.CandidateProtectionQueryFailed
                    : TemporaryFullChargeValidationPrimaryFailure.EnableFailed;
            }
        }
        catch
        {
            if (primaryFailure == TemporaryFullChargeValidationPrimaryFailure.None)
            {
                primaryFailure = enableAcknowledged
                    ? candidateProtectionCompleted
                        ? TemporaryFullChargeValidationPrimaryFailure.CandidateTemporaryQueryFailed
                        : TemporaryFullChargeValidationPrimaryFailure.CandidateProtectionQueryFailed
                    : TemporaryFullChargeValidationPrimaryFailure.EnableFailed;
            }
        }
        finally
        {
            if (enableAttempted)
            {
                cancelAttempted = true;
                try
                {
                    using CancellationTokenSource cancelCleanup = new(cleanupTimeout);
                    HidSessionResult<HidUnit> cancel = await port
                        .CancelTemporaryFullChargeAsync(cancelCleanup.Token)
                        .ConfigureAwait(false);
                    if (cancel.Failure is not null)
                    {
                        restorationFailure =
                            TemporaryFullChargeValidationRestorationFailure.CancelFailed;
                    }
                    else
                    {
                        cancelAcknowledged = true;
                    }
                }
                catch
                {
                    restorationFailure =
                        TemporaryFullChargeValidationRestorationFailure.CancelFailed;
                }

                if (cancelAcknowledged)
                {
                    try
                    {
                        using CancellationTokenSource protectionCleanup = new(cleanupTimeout);
                        HidSessionResult<BatteryValidationSetting> protection = await port
                            .QueryProtectionAsync(protectionCleanup.Token)
                            .ConfigureAwait(false);
                        if (protection.Failure is null)
                        {
                            restorationProtection = protection.Value;
                        }
                        else
                        {
                            restorationFailure = TemporaryFullChargeValidationRestorationFailure
                                .RestorationProtectionQueryFailed;
                        }
                    }
                    catch
                    {
                        restorationFailure = TemporaryFullChargeValidationRestorationFailure
                            .RestorationProtectionQueryFailed;
                    }

                    try
                    {
                        using CancellationTokenSource temporaryCleanup = new(cleanupTimeout);
                        HidSessionResult<bool> temporary = await port
                            .QueryTemporaryFullChargeAsync(temporaryCleanup.Token)
                            .ConfigureAwait(false);
                        if (temporary.Failure is null)
                        {
                            restorationTemporary = temporary.Value;
                        }
                        else if (restorationFailure ==
                                 TemporaryFullChargeValidationRestorationFailure.None)
                        {
                            restorationFailure = TemporaryFullChargeValidationRestorationFailure
                                .RestorationTemporaryQueryFailed;
                        }
                    }
                    catch
                    {
                        if (restorationFailure ==
                            TemporaryFullChargeValidationRestorationFailure.None)
                        {
                            restorationFailure = TemporaryFullChargeValidationRestorationFailure
                                .RestorationTemporaryQueryFailed;
                        }
                    }

                    if (restorationFailure ==
                        TemporaryFullChargeValidationRestorationFailure.None)
                    {
                        if (restorationProtection != BatteryValidationSetting.Limit80)
                        {
                            restorationFailure = TemporaryFullChargeValidationRestorationFailure
                                .RestorationProtectionMismatch;
                        }
                        else if (restorationTemporary != false)
                        {
                            restorationFailure = TemporaryFullChargeValidationRestorationFailure
                                .RestorationTemporaryActive;
                        }
                    }
                }
            }
        }

        if (callerCancellation is not null)
        {
            ExceptionDispatchInfo.Capture(callerCancellation).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(
            BatteryValidationSetting.Limit80,
            false,
            enableAttempted,
            enableAcknowledged,
            candidateProtection,
            candidateTemporary,
            readBack,
            cancelAttempted,
            cancelAcknowledged,
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

    private static TemporaryFullChargeValidationPrimaryFailure MapOpenFailure(
        HidSessionFailureKind? failure) => failure switch
        {
            HidSessionFailureKind.IdentityBlocked =>
                TemporaryFullChargeValidationPrimaryFailure.WrongTarget,
            HidSessionFailureKind.ConflictBlocked =>
                TemporaryFullChargeValidationPrimaryFailure.Conflict,
            HidSessionFailureKind.ConflictDetectionFailed =>
                TemporaryFullChargeValidationPrimaryFailure.ConflictDetectionFailed,
            _ => TemporaryFullChargeValidationPrimaryFailure.OpenFailed,
        };

    private static TemporaryFullChargeValidationResult Abort(
        TemporaryFullChargeValidationPrimaryFailure failure) => new(
        null, null, false, false, null, null, false, false, false, null, null,
        failure,
        TemporaryFullChargeValidationRestorationFailure.None);
}
