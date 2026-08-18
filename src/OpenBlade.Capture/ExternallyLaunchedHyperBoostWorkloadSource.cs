using System.Diagnostics;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

/// <summary>
/// Observes one operator-launched process. It never starts, stops, or selects a
/// workload; the reviewed plan supplies the only accepted file name and hash.
/// </summary>
internal sealed class ExternallyLaunchedHyperBoostWorkloadSource(int processId)
    : IHyperBoostExternalWorkloadSource
{
    private readonly int processId = processId > 0
        ? processId
        : throw new ArgumentOutOfRangeException(nameof(processId));

    public ValueTask<HyperBoostExternalWorkloadObservation> ObserveAsync(
        ReviewedHyperBoostThermalPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.WorkloadKind != HyperBoostValidationWorkloadKind.ExternalReviewedLoad
            || string.IsNullOrWhiteSpace(plan.WorkloadName)
            || string.IsNullOrWhiteSpace(plan.WorkloadSha256))
        {
            return ValueTask.FromResult(
                HyperBoostExternalWorkloadObservation.IdentityMismatch);
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return ValueTask.FromResult(
                    HyperBoostExternalWorkloadObservation.NotRunning);
            }

            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)
                || !Path.GetFileName(path).Equals(
                    plan.WorkloadName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(
                    HyperBoostExternalWorkloadObservation.IdentityMismatch);
            }

            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return ValueTask.FromResult(actual.Equals(
                plan.WorkloadSha256,
                StringComparison.Ordinal)
                    ? HyperBoostExternalWorkloadObservation.RunningExact
                    : HyperBoostExternalWorkloadObservation.IdentityMismatch);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(
                HyperBoostExternalWorkloadObservation.NotRunning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ValueTask.FromResult(
                HyperBoostExternalWorkloadObservation.ObservationFailed);
        }
    }
}
