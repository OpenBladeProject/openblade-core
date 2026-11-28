#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        byte[] enrollmentBytes = WindowsCandidateReceiptStorage.ReadBounded(enrollmentFile, BrokerEnrollmentCodec.MaximumBytes);
        var enrollment = BrokerEnrollmentCodec.ParseCanonical(enrollmentBytes);
        var trust = CreateTrust(enrollment);

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
                || bound.RequestId != requestId || bound.InstallerProcessId.HasValue || bound.InstallerExitCode.HasValue)
            {
                throw new InvalidDataException("The candidate in-flight binding differs from the verified release.");
            }
            RequireReleaseBinding(bound, enrollment, release, staged.ManifestBytes);
            await new BrokerAcceptedSequenceStore(paths).ConsumeAsync(manifest.Sequence, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            return WindowsCandidatePackageReceipt.Publish(storage, proof, release, staged.ManifestBytes, enrollmentBytes,
                BrokerInFlightDeploymentCodec.SerializeCanonical(bound), requestId, bound.UpdatedAt);
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

    internal async Task WithCurrentProcessReceiptAsync(byte[] secret,
        Func<WindowsCandidatePackageReceipt, byte[], Action, Task> receive, CancellationToken cancellationToken)
    {
        using var root = storage.PinDirectory(paths.RootDirectory);
        using var binding = storage.OpenFile(paths.InFlightPath);
        byte[] active = WindowsCandidateReceiptStorage.ReadBounded(binding, BrokerInFlightDeploymentCodec.MaximumBytes);
        var current = BrokerInFlightDeploymentCodec.ParseCanonical(active);
        using var receiver = Process.GetCurrentProcess();
        if (current.InstallerProcessId != receiver.Id || current.BrokerProcessId == receiver.Id
            || current.InstallerExitCode.HasValue)
        {
            throw new InvalidDataException("The candidate has no active launch binding for this receiver.");
        }

        string stage = Path.Combine(paths.StagingDirectory, current.RequestId.ToString("N"));
        using var directory = storage.PinDirectory(stage);
        WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(stage, WindowsCandidatePackageReceipt.ClaimedName));
        byte[] pending;
        using (var file = storage.OpenFile(Path.Combine(stage, WindowsCandidatePackageReceipt.PendingName)))
        {
            pending = WindowsCandidateReceiptStorage.ReadBounded(file);
        }
        var issuedAt = WindowsCandidatePackageReceipt.ReadIssuedAt(pending);
        DateTimeOffset startedAt = receiver.StartTime.ToUniversalTime();
        if (issuedAt < current.CreatedAt || startedAt < issuedAt || current.UpdatedAt < startedAt)
        {
            throw new InvalidDataException("The candidate receipt and receiver launch generations differ.");
        }
        // BindInstallerProcess changes only PID and UpdatedAt. Reconstruct the
        // exact prelaunch generation, keeping every other canonical field.
        byte[] issued = BrokerInFlightDeploymentCodec.SerializeCanonical(current with
        {
            InstallerProcessId = null,
            UpdatedAt = issuedAt,
        });

        using var broker = ScratchCandidateLaunch.OpenBroker(current.BrokerProcessId);
        string image = ScratchCandidateLaunch.ReceiverPath(storage);
        using var imageDirectory = storage.PinDirectory(Path.GetDirectoryName(image)!);
        using var heldClosure = ScratchReceiverClosure.Retain(storage);
        ScratchCandidateLaunch.RequireLiveImage(receiver, image);
        ScratchNativeReceiverLaunch.RequirePolicies(receiver.SafeHandle);
        using var launch = storage.OpenFile(Path.Combine(stage, ScratchCandidateLaunch.EvidenceName));
        byte[] launchEvidence = ScratchCandidateLaunch.Encode(active, broker, receiver, heldClosure, secret);
        WindowsCandidateReceiptStorage.RequireExact(launch, launchEvidence);

        void RequireLiveLaunch()
        {
            cancellationToken.ThrowIfCancellationRequested();
            heldClosure.RequireUnchanged();
            ScratchNativeReceiverLaunch.RequirePolicies(receiver.SafeHandle);
            if (broker.HasExited)
            {
                throw new InvalidDataException("The scratch broker exited before Setup completed its handoff.");
            }
        }

        using var enrollmentFile = storage.OpenFile(paths.EnrollmentPath);
        byte[] enrollmentBytes = WindowsCandidateReceiptStorage.ReadBounded(enrollmentFile, BrokerEnrollmentCodec.MaximumBytes);
        var enrollment = BrokerEnrollmentCodec.ParseCanonical(enrollmentBytes);
        using var manifestFile = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.ManifestFileName));
        byte[] manifest = WindowsCandidateReceiptStorage.ReadBounded(manifestFile, OpenBladeReleaseManifestCodec.MaximumManifestBytes);
        using var signatureFile = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.SignatureFileName));
        byte[] signature = WindowsCandidateReceiptStorage.ReadBounded(signatureFile, FixedLocalDevInboxSource.SignatureBytes);
        var release = OpenBladeSignedReleaseVerifier.Verify(manifest, signature, CreateTrust(enrollment));
        RequireReleaseBinding(current, enrollment, release, manifest);
        using var sequence = storage.OpenFile(paths.AcceptedSequencePath);
        WindowsCandidateReceiptStorage.RequireExact(sequence,
            Encoding.ASCII.GetBytes(release.Manifest.Sequence.ToString(CultureInfo.InvariantCulture)));
        using var package = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.InstallerFileName));
        var proof = await WindowsNativePackageRoleProof.ReadHeldCandidateAsync(package, release, Selected,
            cancellationToken).ConfigureAwait(false);
        var receipt = WindowsCandidatePackageReceipt.Reopen(storage, proof, release, manifest, enrollmentBytes,
            issued, active, current.RequestId, issuedAt, pending);
        RequireLiveLaunch();
        await receive(receipt, ScratchCandidateLaunch.ReceiverIdentity(launchEvidence), RequireLiveLaunch)
            .ConfigureAwait(false);
    }

    private static OpenBladeReleaseTrustPolicy CreateTrust(BrokerEnrollment enrollment)
    {
        var trust = new OpenBladeReleaseTrustPolicy(true,
            [new(enrollment.DevelopmentSigningKeyId, enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64)]);
        BrokerTrustPolicyValidator.Validate(new(enrollment.DeploymentPolicyId, trust));
        return trust;
    }

    private static void RequireReleaseBinding(BrokerInFlightDeployment state, BrokerEnrollment enrollment,
        OpenBladeVerifiedRelease release, byte[] manifestBytes)
    {
        var manifest = release.Manifest;
        if (manifest.InstallationId != enrollment.InstallationId || state.InstallerSha256 != manifest.InstallerSha256
            || state.TargetVersion != manifest.Version || state.TargetSourceRevision != manifest.SourceRevision
            || state.ManifestSha256 != BrokerDurableRecordStore.ComputeSha256(manifestBytes)
            || state.SigningKeyId != release.SigningKeyId || state.TargetSequence != manifest.Sequence
            || state.CpuVoltageServiceVersion is not null)
        {
            throw new InvalidDataException("The candidate in-flight binding differs from the verified release.");
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
