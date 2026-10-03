using System.Diagnostics;
using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public enum BrokerRecordedInstallerProcessStatus
{
    NotRunning,
    RunningExactInstaller,
    Indeterminate,
}

public interface IBrokerRecordedInstallerProcessInspector
{
    Task<BrokerRecordedInstallerProcessStatus> InspectAsync(
        BrokerInFlightDeployment state,
        CancellationToken cancellationToken);
}

public sealed class WindowsBrokerRecordedInstallerProcessInspector(BrokerPaths paths)
    : IBrokerRecordedInstallerProcessInspector
{
    public async Task<BrokerRecordedInstallerProcessStatus> InspectAsync(
        BrokerInFlightDeployment state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.InstallerProcessId.HasValue
            || state.InstallerSha256 is null)
        {
            return BrokerRecordedInstallerProcessStatus.Indeterminate;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(state.InstallerProcessId.Value);
        }
        catch (ArgumentException)
        {
            return BrokerRecordedInstallerProcessStatus.NotRunning;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return BrokerRecordedInstallerProcessStatus.NotRunning;
                }

                DateTimeOffset startedAt = process.StartTime.ToUniversalTime();
                if (startedAt < state.CreatedAt.AddSeconds(-5)
                    || startedAt > state.UpdatedAt.AddSeconds(5))
                {
                    return BrokerRecordedInstallerProcessStatus.Indeterminate;
                }

                string expectedDirectory = Path.Combine(
                    paths.StagingDirectory,
                    state.RequestId.ToString("N"));
                string expectedInstaller = Path.Combine(
                    expectedDirectory,
                    OpenBladeReleaseManifestCodec.InstallerFileName);
                string actualImage = process.MainModule?.FileName ?? string.Empty;
                if (!string.Equals(
                    Path.GetFullPath(actualImage),
                    expectedInstaller,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return BrokerRecordedInstallerProcessStatus.Indeterminate;
                }

                ProtectedBrokerStorage.RejectReparsePoint(paths.StagingDirectory);
                ProtectedBrokerStorage.RejectReparsePoint(expectedDirectory);
                ProtectedBrokerStorage.RejectReparsePoint(expectedInstaller);
                paths.VerifyDirectory(paths.StagingDirectory);
                paths.VerifyDirectory(expectedDirectory);
                FileInfo installer = new(expectedInstaller);
                if (!installer.Exists
                    || installer.Length <= 0
                    || installer.Length > OpenBladeReleaseManifestCodec.MaximumInstallerBytes)
                {
                    return BrokerRecordedInstallerProcessStatus.Indeterminate;
                }

                await using FileStream stream = new(
                    expectedInstaller,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                return string.Equals(
                    Convert.ToHexString(hash).ToLowerInvariant(),
                    state.InstallerSha256,
                    StringComparison.Ordinal)
                    ? BrokerRecordedInstallerProcessStatus.RunningExactInstaller
                    : BrokerRecordedInstallerProcessStatus.Indeterminate;
            }
            catch (Exception exception) when (exception is
                System.ComponentModel.Win32Exception or InvalidOperationException
                or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return BrokerRecordedInstallerProcessStatus.Indeterminate;
            }
        }
    }
}

public interface IBrokerStartupReconciler
{
    Task<bool> TryReconcileAsync(
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken);
}

public sealed class BrokerStartupReconciler(
    BrokerInFlightStore inFlightStore,
    IBrokerRecordedInstallerProcessInspector processInspector,
    IBrokerInstalledStateVerifier installedStateVerifier,
    BrokerDurableRecordStore durableRecords,
    BrokerPaths paths,
    TimeProvider timeProvider,
    ILogger<BrokerStartupReconciler> logger,
    IBrokerCpuVoltageDeployment? cpuVoltage = null) : IBrokerStartupReconciler
{
    public async Task<bool> TryReconcileAsync(
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        BrokerInFlightDeployment? state = await inFlightStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            return cpuVoltage?.TryReconcile() ?? true;
        }

        BrokerRecordedInstallerProcessStatus processStatus = await processInspector
            .InspectAsync(state, cancellationToken).ConfigureAwait(false);
        if (processStatus is not BrokerRecordedInstallerProcessStatus.NotRunning
            || state.InstallerProcessId is null
            || state.InstallerExitCode is null
            || state.PreviousVersion is null
            || state.PreviousProductVersion is null
            || state.PreviousPayloadSha256 is null
            || state.InstallerSha256 is null
            || state.TargetVersion is null
            || state.TargetSourceRevision is null
            || state.ManifestSha256 is null
            || state.SigningKeyId is null
            || !state.TargetSequence.HasValue)
        {
            return false;
        }

        BrokerInstalledState before = new(
            state.PreviousVersion,
            state.PreviousProductVersion,
            state.PreviousPayloadSha256);
        BrokerPostInstallVerification verification;
        IDisposable? cpuLease = null;
        using var reconciliationLifetime = new CpuReconciliationLifetime(() => cpuLease?.Dispose());
        try
        {
            if (state.CpuVoltageServiceVersion is not null)
            {
                if (cpuVoltage is null) { return false; }
                OpenBladeVerifiedRelease release = ReadCpuRelease(state, enrollment);
                cpuLease = cpuVoltage.AcquireClosedReconciliation(state, release.Manifest);
                // Bound/Resumed is reachable only after the original installer health
                // gate passed. A closed validation host may intentionally have stopped
                // Service. The lease freshly proves exact installed Service bytes and
                // resolved host ownership; this is prior installation health, not a
                // claim that Service is currently running, and does not query or start it.
                verification = new(true, true, false);
            }
            else
            {
                if (cpuVoltage is not null && !cpuVoltage.TryReconcile()) { return false; }
                verification = await installedStateVerifier.VerifyCurrentStateAsync(
                    before, state.TargetVersion, state.TargetSourceRevision, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or OperationCanceledException or OpenBladeReleaseVerificationException)
        {
            logger.LogError(exception, "Startup deployment reconciliation was unavailable.");
            return false;
        }

        BrokerDeploymentDisposition disposition = BrokerDeploymentClassifier.Classify(
            installerResultKnown: true,
            state.InstallerExitCode,
            verification.ExactTargetInstalled,
            verification.ServiceHealthy,
            verification.ExactPreviousStateRestored);
        if (disposition == BrokerDeploymentDisposition.Indeterminate)
        {
            return false;
        }

        try
        {
            if (state.CpuVoltageServiceVersion is null) { paths.DeleteRequestStagingDirectory(state.RequestId); }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "Resolved startup staging could not be safely removed.");
            return false;
        }

        BrokerTerminalReport report = new(
            SchemaVersion: 1,
            RequestId: state.RequestId,
            InstallationId: enrollment.InstallationId,
            Sequence: state.TargetSequence.Value,
            DeploymentPolicyId: enrollment.DeploymentPolicyId,
            Disposition: disposition,
            ManifestSha256: state.ManifestSha256,
            SigningKeyId: state.SigningKeyId,
            PackageSha256: state.InstallerSha256,
            PreviousVersion: state.PreviousVersion,
            TargetVersion: state.TargetVersion,
            InstallerExitCode: state.InstallerExitCode,
            ServiceHealthy: verification.ServiceHealthy,
            CompletedAt: timeProvider.GetUtcNow());
        try
        {
            await durableRecords.CreateTerminalReportOnceAsync(report, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            if (!await durableRecords.MatchesExistingTerminalReportAsync(
                report,
                cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        await durableRecords.AppendTransitionAsync(
            new BrokerTransition(
                1,
                state.RequestId,
                disposition == BrokerDeploymentDisposition.Succeeded
                    ? BrokerTransitionKind.HealthVerified
                    : BrokerTransitionKind.RollbackVerified,
                timeProvider.GetUtcNow(),
                disposition == BrokerDeploymentDisposition.Succeeded
                    ? state.CpuVoltageServiceVersion is not null
                        ? "startup-cpu-closed-prior-install-health-reconciled"
                        : "startup-target-reconciled"
                    : "startup-rollback-reconciled"),
            cancellationToken).ConfigureAwait(false);
        inFlightStore.ClearResolved(state.RequestId);
        if (state.CpuVoltageServiceVersion is not null) { cpuVoltage!.CompleteClosedReconciliation(state.RequestId); }
        return true;
    }

    private OpenBladeVerifiedRelease ReadCpuRelease(BrokerInFlightDeployment state, BrokerEnrollment enrollment)
    {
        string staging = Path.Combine(paths.StagingDirectory, state.RequestId.ToString("N"));
        string manifestPath = Path.Combine(staging, FixedLocalDevInboxSource.ManifestFileName);
        string signaturePath = Path.Combine(staging, FixedLocalDevInboxSource.SignatureFileName);
        paths.VerifyDirectory(staging); paths.VerifyFile(manifestPath); paths.VerifyFile(signaturePath);
        byte[] manifest = ReadBounded(manifestPath, OpenBladeReleaseManifestCodec.MaximumManifestBytes);
        byte[] signature = ReadBounded(signaturePath, OpenBladeSignedReleaseVerifier.P1363SignatureBytes);
        var release = OpenBladeSignedReleaseVerifier.Verify(manifest, signature, new(true,
            [new(enrollment.DevelopmentSigningKeyId, enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64)]));
        if (release.Manifest.SchemaVersion != 2 || release.Manifest.InstallationId != enrollment.InstallationId
            || release.Manifest.CpuVoltageServiceVersion != state.CpuVoltageServiceVersion
            || release.Manifest.Version != state.TargetVersion || release.Manifest.SourceRevision != state.TargetSourceRevision
            || release.Manifest.Sequence != state.TargetSequence || release.Manifest.InstallerSha256 != state.InstallerSha256
            || BrokerDurableRecordStore.ComputeSha256(manifest) != state.ManifestSha256 || release.SigningKeyId != state.SigningKeyId)
        { throw new InvalidDataException("The signed CPU release differs from the durable installer binding."); }
        return release;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        ProtectedBrokerStorage.RejectReparsePoint(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum) { throw new InvalidDataException("Invalid retained CPU release size."); }
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private sealed class CpuReconciliationLifetime(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
