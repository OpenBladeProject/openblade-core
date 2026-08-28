using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class FixedLocalDevInboxSourceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.FixedLocalDevInboxSource.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StageAsync_CopiesOnlyExactExclusivelyOpenedSignedSet()
    {
        using InboxFixture fixture = CreateFixture();
        string staging = fixture.Paths.CreateRequestStagingDirectory(Guid.NewGuid());

        StagedVerifiedLocalDevDeployment staged = await fixture.Source.StageAsync(
            staging,
            fixture.Trust);

        Assert.Equal(fixture.Manifest.InstallationId, staged.VerifiedRelease.Manifest.InstallationId);
        Assert.Equal(fixture.Manifest.Sequence, staged.VerifiedRelease.Manifest.Sequence);
        Assert.Equal(fixture.Installer, await File.ReadAllBytesAsync(staged.InstallerPath));
        Assert.Equal(
            Path.Combine(staging, FixedLocalDevInboxSource.InstallerFileName),
            staged.InstallerPath);
    }

    [Theory]
    [InlineData("extra.txt")]
    [InlineData("openblade-local-dev-deployment.json")]
    public async Task StageAsync_RejectsExtraOrWrongCaseNames(string unauthorizedName)
    {
        using InboxFixture fixture = CreateFixture();
        string unauthorized = Path.Combine(fixture.Paths.InboxDirectory, unauthorizedName);
        if (string.Equals(unauthorizedName, "extra.txt", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(unauthorized, "x");
        }
        else
        {
            string canonical = Path.Combine(
                fixture.Paths.InboxDirectory,
                FixedLocalDevInboxSource.ManifestFileName);
            string temporary = Path.Combine(fixture.Paths.InboxDirectory, "rename.tmp");
            File.Move(canonical, temporary);
            File.Move(temporary, unauthorized);
        }
        string staging = fixture.Paths.CreateRequestStagingDirectory(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Source.StageAsync(
            staging,
            fixture.Trust));
    }

    [Fact]
    public async Task StageAsync_RejectsHardLinkedInput()
    {
        using InboxFixture fixture = CreateFixture();
        string installer = Path.Combine(
            fixture.Paths.InboxDirectory,
            FixedLocalDevInboxSource.InstallerFileName);
        string outsideLink = Path.Combine(root, "outside-hardlink.exe");
        Assert.True(CreateHardLink(outsideLink, installer, IntPtr.Zero));
        string staging = fixture.Paths.CreateRequestStagingDirectory(Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Source.StageAsync(
            staging,
            fixture.Trust));
    }

    [Fact]
    public async Task StageAsync_FailsClosedWhenInputCannotBeExclusivelyOpened()
    {
        using InboxFixture fixture = CreateFixture();
        string installer = Path.Combine(
            fixture.Paths.InboxDirectory,
            FixedLocalDevInboxSource.InstallerFileName);
        await using FileStream competing = new(
            installer,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        string staging = fixture.Paths.CreateRequestStagingDirectory(Guid.NewGuid());

        await Assert.ThrowsAsync<IOException>(() => fixture.Source.StageAsync(
            staging,
            fixture.Trust));
    }

    private InboxFixture CreateFixture()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Directory.CreateDirectory(paths.InboxDirectory);
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        string keyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(spki);
        byte[] installer = "fixed local development installer"u8.ToArray();
        OpenBladeReleaseManifest manifest = DeploymentPolicyTests.CreateManifest("65535.1.2.3") with
        {
            SigningKeyId = keyId,
            InstallationId = Guid.NewGuid(),
            Sequence = 42,
            InstallerSize = installer.Length,
            InstallerSha256 = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(),
        };
        byte[] manifestBytes = OpenBladeReleaseManifestCodec.SerializeCanonical(manifest);
        byte[] signature = key.SignHash(
            OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifestBytes),
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllBytes(
            Path.Combine(paths.InboxDirectory, FixedLocalDevInboxSource.ManifestFileName),
            manifestBytes);
        File.WriteAllBytes(
            Path.Combine(paths.InboxDirectory, FixedLocalDevInboxSource.SignatureFileName),
            signature);
        File.WriteAllBytes(
            Path.Combine(paths.InboxDirectory, FixedLocalDevInboxSource.InstallerFileName),
            installer);
        return new InboxFixture(
            key,
            paths,
            new FixedLocalDevInboxSource(paths),
            new OpenBladeReleaseTrustPolicy(
                true,
                [new OpenBladeReleaseTrustKey(keyId, Convert.ToBase64String(spki))]),
            manifest,
            installer);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record InboxFixture(
        ECDsa Key,
        BrokerPaths Paths,
        FixedLocalDevInboxSource Source,
        OpenBladeReleaseTrustPolicy Trust,
        OpenBladeReleaseManifest Manifest,
        byte[] Installer) : IDisposable
    {
        public void Dispose() => Key.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
