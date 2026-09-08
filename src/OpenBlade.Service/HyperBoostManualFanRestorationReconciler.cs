using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// Owns bounded read-only reconciliation; this policy has no hardware write authority.
internal static class HyperBoostManualFanRestorationReconciler
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);

    internal static async Task<ScopedApplyOutcome>
        ReconcileAsync(
            Func<CancellationToken, Task<ConfirmedDeviceState>> readState,
            FanControlSettings previous,
            FanControlSettings requested,
            ScopedApplyOutcome outcome,
            Func<TimeSpan, CancellationToken, Task> retryDelay,
            CancellationToken cancellationToken)
    {
        if (previous.Mode != FanMode.Automatic
            || !DeviceSubsystemOrder.IsManualFanMode(requested.Mode)
            || outcome.Result.Status != ControlResultStatus.Failed
            || outcome.WriteSubmissionState != WriteSubmissionState.Indeterminate
            || !outcome.WriteAttempted
            || outcome.DeviceAccessFailed)
        {
            return outcome;
        }

        const int readAttemptCount = 2;
        for (int attempt = 0; attempt < readAttemptCount; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    await retryDelay(
                            RetryDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                ConfirmedDeviceState readback = await readState(cancellationToken)
                    .ConfigureAwait(false);
                if (readback.Fans is { } fans
                    && DeviceSubsystemOrder.IsManualFanMode(fans))
                {
                    return outcome with
                    {
                        Result = new ControlResult(
                            ControlResultStatus.Applied,
                            "Fans setting applied and confirmed by bounded fresh readback after an indeterminate HyperBoost restoration submission."),
                        Readback = readback,
                        ReadbackMatched = true,
                        DeviceAccessFailed = false,
                        ConfirmationUnavailable = false,
                        PreWriteState = null,
                    };
                }

                if (readback.Fans == FanMode.Automatic)
                {
                    return outcome with
                    {
                        Result = new ControlResult(
                            ControlResultStatus.Failed,
                            $"{outcome.Result.Message} Fresh readback confirmed that the prior Automatic fan baseline remained active."),
                        Readback = readback,
                        WriteAttempted = false,
                        ReadbackMatched = false,
                        DeviceAccessFailed = false,
                        ConfirmationUnavailable = false,
                        PreWriteState = null,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The exact manual target may already have applied. Preserve the
                // original indeterminate outcome so the caller durably records a
                // pending restore without entering an automatic write retry path.
                return outcome;
            }
            catch
            {
                // A possible submission permits read-only reconciliation only.
                // Use one bounded delayed fresh read, then leave the journal
                // pending because later generic manual observations cannot prove
                // which exact Fixed, Curve, or Maximum intent is active.
            }
        }

        return outcome;
    }
}
