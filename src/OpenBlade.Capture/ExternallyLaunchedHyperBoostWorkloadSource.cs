using System.Diagnostics;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

/// <summary>
/// Observes one operator-launched process. It never starts, stops, or selects a
/// workload. The command binds the reviewed plan to the observed file name and
/// hash before hardware access, and this source revalidates that exact identity.
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

        HyperBoostExternalWorkloadObservation observed = ReadIdentity(
            out HyperBoostExternalWorkloadIdentity identity);
        if (observed != HyperBoostExternalWorkloadObservation.RunningExact)
        {
            return ValueTask.FromResult(observed);
        }

        return ValueTask.FromResult(
            identity.ExecutableName.Equals(
                plan.WorkloadName,
                StringComparison.OrdinalIgnoreCase)
            && identity.ExecutableSha256.Equals(
                plan.WorkloadSha256,
                StringComparison.Ordinal)
                ? HyperBoostExternalWorkloadObservation.RunningExact
                : HyperBoostExternalWorkloadObservation.IdentityMismatch);
    }

    internal HyperBoostExternalWorkloadIdentity? CaptureIdentity()
    {
        HyperBoostExternalWorkloadObservation observed = ReadIdentity(
            out HyperBoostExternalWorkloadIdentity identity);
        return observed == HyperBoostExternalWorkloadObservation.RunningExact
            ? identity
            : null;
    }

    private HyperBoostExternalWorkloadObservation ReadIdentity(
        out HyperBoostExternalWorkloadIdentity identity)
    {
        identity = default;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return HyperBoostExternalWorkloadObservation.NotRunning;
            }

            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return HyperBoostExternalWorkloadObservation.ObservationFailed;
            }

            string executableName = Path.GetFileName(path);
            string sha256 = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(path)));
            var observedIdentity = new HyperBoostExternalWorkloadIdentity(
                executableName,
                sha256);
            if (!observedIdentity.IsWellFormed)
            {
                return HyperBoostExternalWorkloadObservation.IdentityMismatch;
            }

            identity = observedIdentity;
            return HyperBoostExternalWorkloadObservation.RunningExact;
        }
        catch (ArgumentException)
        {
            return HyperBoostExternalWorkloadObservation.NotRunning;
        }
        catch
        {
            return HyperBoostExternalWorkloadObservation.ObservationFailed;
        }
    }
}
