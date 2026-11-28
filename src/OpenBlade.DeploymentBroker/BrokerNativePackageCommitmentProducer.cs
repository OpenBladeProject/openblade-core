using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// No new broker IPC operation or signing root. This adapter consumes an already
// authenticated, sequence-consumed fixed LocalDev transaction. Stable is denied
// until the owner provisions and reviews a distinct product-domain policy.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class BrokerNativePackageCommitmentProducer
{
    internal static async Task<(WindowsNativePackage Package, Guid Request)> OpenCurrentAsync(CancellationToken cancellationToken)
    {
        var paths = BrokerPaths.CreateDefault();
        using var storage = WindowsNativeEpochFiles.OpenProtected(paths.RootDirectory);
        using var held = storage.OpenFile(paths.InFlightPath);
        var active = BrokerInFlightDeploymentCodec.ParseCanonical(WindowsNativeEpochFiles.ReadBounded(held, BrokerInFlightDeploymentCodec.MaximumBytes));
        return (await OpenAsync(active.RequestId, cancellationToken), active.RequestId);
    }

    internal static Task<WindowsNativePackage> OpenAsync(Guid request, CancellationToken cancellationToken) =>
        OpenPackageAsync(request, WindowsNativePackage.OpenLocalDevAsync, cancellationToken);

    internal static async Task<WindowsManagedMailboxPackage?> OpenManagedCurrentAsync(CancellationToken token)
    {
        var paths = BrokerPaths.CreateDefault();
        try { _ = File.GetAttributes(paths.InFlightPath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using var storage = WindowsNativeEpochFiles.OpenProtected(paths.RootDirectory);
        using var held = storage.OpenFile(paths.InFlightPath);
        var active = BrokerInFlightDeploymentCodec.ParseCanonical(WindowsNativeEpochFiles.ReadBounded(held, BrokerInFlightDeploymentCodec.MaximumBytes));
        return await OpenPackageAsync(active.RequestId, WindowsManagedMailboxPackage.OpenLocalDevAsync, token).ConfigureAwait(false);
    }

    private static async Task<T> OpenPackageAsync<T>(Guid request,
        Func<FileStream, IDisposable, byte[], byte[], NativeLocalDevEnrollment, CancellationToken, Task<T>> receive,
        CancellationToken cancellationToken)
    {
        var paths = BrokerPaths.CreateDefault();
        var leases = new NativePackageLeases();
        FileStream? package = null;
        try
        {
            var storage = WindowsNativeEpochFiles.OpenProtected(paths.RootDirectory);
            leases.Add(storage);
            var enrollmentFile = storage.OpenFile(paths.EnrollmentPath);
            leases.Add(enrollmentFile);
            var enrollment = BrokerEnrollmentCodec.ParseCanonical(WindowsNativeEpochFiles.ReadBounded(enrollmentFile, BrokerEnrollmentCodec.MaximumBytes));
            var activeFile = storage.OpenFile(paths.InFlightPath);
            leases.Add(activeFile);
            var active = BrokerInFlightDeploymentCodec.ParseCanonical(WindowsNativeEpochFiles.ReadBounded(activeFile, BrokerInFlightDeploymentCodec.MaximumBytes));
            if (request == Guid.Empty || active.RequestId != request || active.InstallerExitCode.HasValue)
            { throw new InvalidDataException("Native package has no matching active enrolled deployment."); }
            string stage = Path.Combine(paths.StagingDirectory, request.ToString("N"));
            leases.Add(storage.RetainDirectory(stage));
            var manifestFile = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.ManifestFileName));
            leases.Add(manifestFile);
            byte[] manifest = WindowsNativeEpochFiles.ReadBounded(manifestFile, OpenBladeReleaseManifestCodec.MaximumManifestBytes);
            var signatureFile = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.SignatureFileName));
            leases.Add(signatureFile);
            byte[] signature = WindowsNativeEpochFiles.ReadBounded(signatureFile, OpenBladeSignedReleaseVerifier.P1363SignatureBytes);
            var policy = new OpenBladeReleaseTrustPolicy(true,
                [new(enrollment.DevelopmentSigningKeyId, enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64)]);
            var release = OpenBladeSignedReleaseVerifier.Verify(manifest, signature, policy);
            var signed = release.Manifest;
            var sequence = storage.OpenFile(paths.AcceptedSequencePath);
            leases.Add(sequence);
            WindowsNativeEpochFiles.RequireExact(sequence, System.Text.Encoding.ASCII.GetBytes(signed.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (signed.InstallationId != enrollment.InstallationId || active.InstallerSha256 != signed.InstallerSha256
                || active.ManifestSha256 != NativeCanonical.Digest(manifest) || active.SigningKeyId != signed.SigningKeyId
                || active.TargetSequence != signed.Sequence || active.TargetVersion != signed.Version
                || active.TargetSourceRevision != signed.SourceRevision || active.CpuVoltageServiceVersion is not null)
            { throw new InvalidDataException("Native package differs from the accepted enrolled release transaction."); }
            package = storage.OpenFile(Path.Combine(stage, FixedLocalDevInboxSource.InstallerFileName));
            return await receive(package, leases, manifest, signature,
                NativeLocalDevEnrollment.FromHeld(enrollmentFile), cancellationToken).ConfigureAwait(false);
        }
        catch { package?.Dispose(); leases.Dispose(); throw; }
    }

    private sealed class NativePackageLeases : IDisposable
    {
        private readonly List<IDisposable> held = [];
        internal void Add(IDisposable value) => held.Add(value);
        public void Dispose() { for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); } held.Clear(); }
    }
}
