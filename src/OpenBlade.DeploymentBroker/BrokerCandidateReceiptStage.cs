#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// Explicit offline candidate admission. This has no pipe action or process runner.
internal sealed class BrokerCandidateReceiptStage(WindowsCandidateReceiptStorage storage)
{
    private static readonly WindowsNativePayloadIdentity Selected = new(
        "4079c110978af3008ef929fac309b9637e9e77b0c68a660dd8d956758449359f",
        new("af61a8c842429bc0564390dee39b466ee0da9023", new(65535, 1, 2, 3)));

    private readonly BrokerPaths paths = BrokerPaths.CreateForTests(storage.Root);
    private readonly BrokerSingleFlightGate singleFlight = new();

    internal async Task<WindowsCandidatePackageReceipt> PrepareAsync(
        Guid requestId, BrokerInstalledState previous, CancellationToken cancellationToken)
    {
        using var flight = singleFlight.TryAcquire() ?? throw new IOException("A candidate stage is already active.");
        using var root = storage.PinDirectory(paths.RootDirectory);
        using var enrollmentFile = storage.OpenFile(paths.EnrollmentPath);
        var enrollment = await new BrokerEnrollmentStore(paths).LoadAsync(cancellationToken).ConfigureAwait(false);
        var trust = new OpenBladeReleaseTrustPolicy(true,
            [new(enrollment.DevelopmentSigningKeyId, enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64)]);
        BrokerTrustPolicyValidator.Validate(new(enrollment.DeploymentPolicyId, trust));

        paths.EnsureDirectory(paths.StagingDirectory);
        using var stagingRoot = storage.PinDirectory(paths.StagingDirectory);
        string stage = Path.Combine(paths.StagingDirectory, requestId.ToString("N"));
        WindowsCandidateReceiptStorage.RequireAbsent(stage);
        var state = new BrokerInFlightStore(paths);
        await state.CreateOnceAsync(requestId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        bool created = false;
        bool publishing = false;
        try
        {
            state.BindPreviousState(requestId, previous, DateTimeOffset.UtcNow);
            paths.CreateRequestStagingDirectory(requestId);
            created = true;
            using var directory = storage.PinDirectory(stage);
            using var inbox = storage.PinDirectory(paths.InboxDirectory);
            var staged = await new FixedLocalDevInboxSource(paths).StageAsync(stage, trust, cancellationToken)
                .ConfigureAwait(false);
            var release = OpenBladeSignedReleaseVerifier.Verify(staged.ManifestBytes, staged.SignatureBytes, trust);
            var manifest = release.Manifest;
            if (manifest.InstallationId != enrollment.InstallationId)
            {
                throw new InvalidDataException("The candidate release is bound to another enrollment.");
            }

            string installer = Path.Combine(stage, FixedLocalDevInboxSource.InstallerFileName);
            using FileStream snapshot = storage.OpenFile(installer);
            var proof = await WindowsNativePackageRoleProof.ReadHeldCandidateAsync(snapshot, release, Selected,
                cancellationToken).ConfigureAwait(false);
            string manifestSha256 = BrokerDurableRecordStore.ComputeSha256(staged.ManifestBytes);
            state.BindRelease(requestId, manifest.InstallerSha256, manifest.Version, manifest.SourceRevision,
                manifestSha256, release.SigningKeyId, manifest.Sequence, DateTimeOffset.UtcNow);
            if (await state.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } bound
                || bound.RequestId != requestId || bound.InstallerSha256 != manifest.InstallerSha256
                || bound.TargetVersion != manifest.Version || bound.TargetSourceRevision != manifest.SourceRevision
                || bound.ManifestSha256 != manifestSha256 || bound.SigningKeyId != release.SigningKeyId
                || bound.TargetSequence != manifest.Sequence || bound.InstallerProcessId.HasValue
                || bound.InstallerExitCode.HasValue)
            {
                throw new InvalidDataException("The candidate in-flight binding differs from the verified release.");
            }
            await new BrokerAcceptedSequenceStore(paths).ConsumeAsync(manifest.Sequence, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            return WindowsCandidatePackageReceipt.Publish(storage, proof, release, staged.ManifestBytes,
                BrokerInFlightDeploymentCodec.SerializeCanonical(bound), requestId);
        }
        catch (Exception failure) when (!publishing)
        {
            // A publication failure can leave evidence at either fixed name.
            // Preserve the unresolved binding once publication has been attempted.
            try
            {
                if (created)
                {
                    paths.DeleteRequestStagingDirectory(requestId);
                }
                state.ClearResolved(requestId);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            throw;
        }
    }

    internal async Task ReconcileCommittedAsync(
        WindowsCandidatePackageReceipt receipt, Guid installationId, Guid requestId, CancellationToken cancellationToken)
    {
        using var flight = singleFlight.TryAcquire() ?? throw new IOException("A candidate stage is already active.");
        using var root = storage.PinDirectory(paths.RootDirectory);
        string programFiles = Path.Combine(storage.Root, "program-files");
        receipt.RequireBinding(installationId, requestId, programFiles);
        var state = new BrokerInFlightStore(paths);
        if (await state.LoadAsync(cancellationToken).ConfigureAwait(false) is not { } current
            || current.RequestId != requestId || current.InstallerSha256 != Selected.PackageSha256
            || current.InstallerProcessId.HasValue || current.InstallerExitCode.HasValue)
        {
            throw new InvalidDataException("The scratch candidate transaction is no longer current.");
        }

        // This proves the Setup payload commit only. The scratch route never starts a process.
        using (receipt.OpenActiveBinding())
        {
            receipt.VerifyCommitted(Path.Combine(programFiles, "OpenBlade"), cancellationToken,
                () => paths.DeleteRequestStagingDirectory(requestId));
        }
        state.ClearResolved(requestId);
    }
}
#endif
