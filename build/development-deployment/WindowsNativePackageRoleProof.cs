using System.Security.Cryptography;

namespace OpenBlade.Deployment;

// A successful read authenticates an offline package under the caller's fixture policy.
// This proof carries no authority to install, launch, or claim an SCM generation.
internal sealed class WindowsNativePackageRoleProof
{
    private WindowsNativePackageRoleProof(WindowsNativePayloadIdentity identity, RoleImage service, RoleImage sessionAgent)
    {
        Identity = identity;
        Service = service;
        SessionAgent = sessionAgent;
    }

    internal WindowsNativePayloadIdentity Identity { get; }
    internal RoleImage Service { get; }
    internal RoleImage SessionAgent { get; }

    internal static Task<WindowsNativePackageRoleProof> ReadFixtureAsync(
        string installerPath, ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> signature,
        OpenBladeReleaseTrustPolicy fixturePolicy, WindowsNativePayloadIdentity selected,
        WindowsNativeBurnProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var release = OpenBladeSignedReleaseVerifier.Verify(manifest.Span, signature.Span, fixturePolicy);
        return WindowsNativeRoleInventory.WithVerifiedSnapshotAsync(installerPath, release, selected,
            (snapshot, token) => ReadRoles(snapshot, selected, profile, release.Manifest.Version, token),
            cancellationToken);
    }

#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
    internal static async Task<WindowsNativePackageRoleProof> ReadHeldCandidateAsync(
        FileStream snapshot, OpenBladeVerifiedRelease release, WindowsNativePayloadIdentity selected,
        CancellationToken cancellationToken)
    {
        var manifest = release.Manifest;
        var identity = new WindowsNativePayloadIdentity(manifest.InstallerSha256,
            new(manifest.SourceRevision, Version.Parse(manifest.Version)));
        if (manifest.SchemaVersion != OpenBladeReleaseManifestCodec.CurrentSchemaVersion || identity != selected)
        {
            throw new InvalidDataException("The signed release differs from the admitted Rust candidate.");
        }

        await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(snapshot, release, cancellationToken)
            .ConfigureAwait(false);
        snapshot.Position = 0;
        return ReadRoles(snapshot, identity, WindowsNativeBurnProfile.AuthoredOpenBlade, manifest.Version,
            cancellationToken);
    }
#endif

#if OPENBLADE_PACKAGE_PROOF_FIXTURE
    // The signed 9p fixture has a product-shaped version outside the reserved LocalDev range.
    // Authenticate the exact fixture by its independently pinned whole-file identity.
    internal static async Task<WindowsNativePackageRoleProof> ReadPinnedSigned9pAsync(
        string installerPath, CancellationToken cancellationToken = default)
    {
        const long fixtureLength = 1052024;
        var identity = new WindowsNativePayloadIdentity(
            "cc01de2afa4b92cf4756d0565eb2baddd1a50344b900f483405badbfa8f5b5b6",
            new("af61a8c842429bc0564390dee39b466ee0da9023", new(0, 58, 4, 0)));

        cancellationToken.ThrowIfCancellationRequested();
        await using var snapshot = new FileStream(
            installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (snapshot.Length != fixtureLength || !CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(snapshot, cancellationToken).ConfigureAwait(false),
            Convert.FromHexString(identity.PackageSha256)))
        {
            throw new InvalidDataException("The held Burn snapshot differs from the pinned signed 9p fixture.");
        }

        snapshot.Position = 0;
        return ReadRoles(snapshot, identity, WindowsNativeBurnProfile.SinglePackageFixture,
            identity.Build.ProductVersion.ToString(4), cancellationToken);
    }
#endif

    private static WindowsNativePackageRoleProof ReadRoles(
        Stream snapshot, WindowsNativePayloadIdentity identity, WindowsNativeBurnProfile profile,
        string expectedVersion, CancellationToken cancellationToken)
    {
        var roles = WindowsNativeInventoryBurnExtractor.ExtractRoles(snapshot, profile, expectedVersion, cancellationToken);
        var inventory = WindowsNativeRoleInventory.DecodeForBuild(identity.Build, roles.Resource.AsSpan());
        cancellationToken.ThrowIfCancellationRequested();
        return new(identity,
            Bind(inventory, WindowsNativeRole.Service, roles.Service),
            Bind(inventory, WindowsNativeRole.SessionAgent, roles.SessionAgent));
    }

    private static RoleImage Bind(WindowsNativeRoleInventory inventory, WindowsNativeRole role,
        WindowsNativeInventoryBurnExtractor.RoleMember member)
    {
        string sha256 = inventory.GetExpectedSha256(role);
        if (!string.Equals(member.Sha256, sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {role} cabinet bytes differ from the authenticated role inventory.");
        }

        return new(role, member.PayloadId, member.PayloadPath, member.CabinetMember,
            WindowsNativeRoleInventory.InstalledFileName(role), member.Length, sha256);
    }

    internal sealed record RoleImage(WindowsNativeRole Role, string PayloadId, string PayloadPath, string CabinetMember,
        string InstalledName, int Length, string Sha256);
}
