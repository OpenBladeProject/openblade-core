using System.Security.Cryptography;
using System.Text;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

public sealed class OpenBladeSignedReleaseVerifierTests
{
    [Fact]
    public void Verify_AcceptsCanonicalManifestAndP1363Signature()
    {
        using TestRelease release = TestRelease.Create();

        OpenBladeVerifiedRelease verified = OpenBladeSignedReleaseVerifier.Verify(
            release.ManifestBytes,
            release.SignatureBytes,
            release.Policy);

        Assert.Equal(release.SigningKeyId, verified.SigningKeyId);
        Assert.Equal("65535.1.2.3", verified.Manifest.Version);
        Assert.Equal(OpenBladeReleaseManifestCodec.InstallerFileName, verified.Manifest.InstallerFileName);
        Assert.Equal(new Guid("11111111-1111-1111-1111-111111111111"), verified.Manifest.InstallationId);
        Assert.Equal(1, verified.Manifest.Sequence);
    }

    [Fact]
    public void Verify_RejectsManifestByteMutation()
    {
        using TestRelease release = TestRelease.Create();
        byte[] mutated = (byte[])release.ManifestBytes.Clone();
        int revisionIndex = Encoding.UTF8.GetString(mutated).IndexOf(new string('a', 40), StringComparison.Ordinal);
        mutated[revisionIndex] = (byte)'b';

        AssertFailure(
            OpenBladeReleaseVerificationFailure.SignatureInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(mutated, release.SignatureBytes, release.Policy));
    }

    [Fact]
    public void Verify_RejectsSignatureMutationAndWrongLengths()
    {
        using TestRelease release = TestRelease.Create();
        byte[] mutated = (byte[])release.SignatureBytes.Clone();
        mutated[0] ^= 0x01;

        AssertFailure(
            OpenBladeReleaseVerificationFailure.SignatureInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(release.ManifestBytes, mutated, release.Policy));
        AssertFailure(
            OpenBladeReleaseVerificationFailure.SignatureLengthInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(release.ManifestBytes, new byte[63], release.Policy));
        AssertFailure(
            OpenBladeReleaseVerificationFailure.SignatureLengthInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(release.ManifestBytes, new byte[65], release.Policy));
    }

    [Fact]
    public void Verify_RejectsUnknownAndWrongKeys()
    {
        using TestRelease release = TestRelease.Create();
        using ECDsa wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] wrongSpki = wrongKey.ExportSubjectPublicKeyInfo();
        OpenBladeReleaseTrustPolicy unknownKeyPolicy = new(
            true,
            [
                new(
                    OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(wrongSpki),
                    Convert.ToBase64String(wrongSpki)),
            ]);

        AssertFailure(
            OpenBladeReleaseVerificationFailure.UnknownSigningKey,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                unknownKeyPolicy));
    }

    [Fact]
    public void Verify_RejectsDisabledOrEmptyPolicy()
    {
        using TestRelease release = TestRelease.Create();

        AssertFailure(
            OpenBladeReleaseVerificationFailure.PolicyDisabled,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                release.Policy with { Enabled = false }));
        AssertFailure(
            OpenBladeReleaseVerificationFailure.PolicyInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                new OpenBladeReleaseTrustPolicy(true, [])));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,", "{ \"schemaVersion\":1,")]
    [InlineData("\"product\":\"OpenBlade\"", "\"unknown\":1,\"product\":\"OpenBlade\"")]
    [InlineData("\"product\":\"OpenBlade\"", "\"product\":\"OpenBlade\",\"product\":\"OpenBlade\"")]
    [InlineData("\"product\":\"OpenBlade\"", "\"Product\":\"OpenBlade\",\"product\":\"OpenBlade\"")]
    [InlineData("\"product\":\"OpenBlade\",", "")]
    public void Verify_RejectsNoncanonicalUnknownDuplicateAndCaseCollidingJson(
        string oldText,
        string newText)
    {
        using TestRelease release = TestRelease.Create();
        byte[] changed = Replace(release.ManifestBytes, oldText, newText);

        OpenBladeReleaseVerificationException exception = Assert.Throws<OpenBladeReleaseVerificationException>(
            () => OpenBladeSignedReleaseVerifier.Verify(changed, release.SignatureBytes, release.Policy));

        Assert.Contains(
            exception.Failure,
            new[]
            {
                OpenBladeReleaseVerificationFailure.ManifestInvalid,
                OpenBladeReleaseVerificationFailure.ManifestNotCanonical,
            });
    }

    [Theory]
    [InlineData("\"channel\":\"LocalDev\"", "\"channel\":\"Stable\"")]
    [InlineData("\"signatureAlgorithm\":\"ECDSA-P256-SHA256-P1363\"", "\"signatureAlgorithm\":\"RSA-PSS-SHA256\"")]
    [InlineData("\"signingKeyCurve\":\"P-256\"", "\"signingKeyCurve\":\"P-384\"")]
    [InlineData("\"installerFileName\":\"OpenBlade-Setup.exe\"", "\"installerFileName\":\"other.exe\"")]
    [InlineData("\"bundleId\":\"OSSBlade.OpenBlade\"", "\"bundleId\":\"Other.Bundle\"")]
    [InlineData("\"providerKey\":\"OSSBlade.OpenBlade\"", "\"providerKey\":\"Other.Provider\"")]
    [InlineData("FF859CA9-D69F-4995-A2AD-69E540E45724", "00000000-0000-0000-0000-000000000000")]
    [InlineData("\"architecture\":\"x64\"", "\"architecture\":\"arm64\"")]
    [InlineData("\"minimumWindowsBuild\":26100", "\"minimumWindowsBuild\":22621")]
    public void Verify_RejectsWrongFixedConstants(string oldText, string newText)
    {
        using TestRelease release = TestRelease.Create();

        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                Replace(release.ManifestBytes, oldText, newText),
                release.SignatureBytes,
                release.Policy));
    }

    [Theory]
    [InlineData("\"version\":\"65535.1.2.3\"", "\"version\":\"65535.01.2.3\"")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("\"installerSize\":12345678", "\"installerSize\":0")]
    [InlineData("\"sequence\":1", "\"sequence\":0")]
    [InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000")]
    public void Verify_RejectsMalformedReleaseIdentity(string oldText, string newText)
    {
        using TestRelease release = TestRelease.Create();

        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                Replace(release.ManifestBytes, oldText, newText),
                release.SignatureBytes,
                release.Policy));
    }

    [Fact]
    public void Verify_RejectsUppercaseInstallerHash()
    {
        using TestRelease release = TestRelease.Create();

        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                Replace(
                    release.ManifestBytes,
                    release.InstallerSha256,
                    release.InstallerSha256.ToUpperInvariant()),
                release.SignatureBytes,
                release.Policy));
    }

    [Fact]
    public void Verify_RejectsManifestAndPublicKeyBounds()
    {
        using TestRelease release = TestRelease.Create();
        byte[] oversizedManifest = new byte[OpenBladeReleaseManifestCodec.MaximumManifestBytes + 1];
        string oversizedSpki = Convert.ToBase64String(
            new byte[OpenBladeSignedReleaseVerifier.MaximumSubjectPublicKeyInfoBytes + 1]);

        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestSizeInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                oversizedManifest,
                release.SignatureBytes,
                release.Policy));
        AssertFailure(
            OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                new OpenBladeReleaseTrustPolicy(
                    true,
                    [new OpenBladeReleaseTrustKey(new string('a', 64), oversizedSpki)])));
    }

    [Fact]
    public void Verify_RejectsNonP256PublicKey()
    {
        using TestRelease release = TestRelease.Create();
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        OpenBladeReleaseTrustPolicy policy = new(
            true,
            [
                new(
                    OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(p384.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())),
            ]);

        AssertFailure(
            OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                policy));
    }

    [Fact]
    public void Verify_RejectsDeclaredKeyIdThatDoesNotMatchSpkiDigest()
    {
        using TestRelease release = TestRelease.Create();
        OpenBladeReleaseTrustPolicy policy = new(
            true,
            [release.Policy.TrustedKeys[0] with { KeyId = new string('0', 64) }]);

        AssertFailure(
            OpenBladeReleaseVerificationFailure.PolicyInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                release.SignatureBytes,
                policy));
    }

    [Fact]
    public void Verify_RejectsSignatureWithoutStableReleaseDomainSeparation()
    {
        using TestRelease release = TestRelease.Create();
        using ECDsa signingKey = release.CloneSigningKey();
        byte[] undomainedDigest = SHA256.HashData(release.ManifestBytes);
        byte[] undomainedSignature = signingKey.SignHash(
            undomainedDigest,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        AssertFailure(
            OpenBladeReleaseVerificationFailure.SignatureInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                release.ManifestBytes,
                undomainedSignature,
                release.Policy));
    }

    [Fact]
    public void Verify_RejectsBomAndOversizedInstaller()
    {
        using TestRelease release = TestRelease.Create();
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. release.ManifestBytes];

        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(withBom, release.SignatureBytes, release.Policy));
        AssertFailure(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            () => OpenBladeSignedReleaseVerifier.Verify(
                Replace(
                    release.ManifestBytes,
                    "\"installerSize\":12345678",
                    $"\"installerSize\":{OpenBladeReleaseManifestCodec.MaximumInstallerBytes + 1}"),
                release.SignatureBytes,
                release.Policy));
    }

    [Fact]
    public async Task VerifyInstallerAsync_AcceptsOnlySignedSizeAndHash()
    {
        using TestRelease release = TestRelease.CreateWithRealInstallerIdentity();
        OpenBladeVerifiedRelease verified = OpenBladeSignedReleaseVerifier.Verify(
            release.ManifestBytes,
            release.SignatureBytes,
            release.Policy);

        await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
            new MemoryStream(release.InstallerBytes, writable: false),
            verified);

        byte[] mutated = (byte[])release.InstallerBytes.Clone();
        mutated[0] ^= 0x01;
        await AssertFailureAsync(
            OpenBladeReleaseVerificationFailure.InstallerHashMismatch,
            () => OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
                new MemoryStream(mutated, writable: false),
                verified));
        await AssertFailureAsync(
            OpenBladeReleaseVerificationFailure.InstallerSizeMismatch,
            () => OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
                new MemoryStream([.. release.InstallerBytes, 0x00], writable: false),
                verified));
    }

    private static byte[] Replace(byte[] input, string oldText, string newText)
    {
        return Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(input).Replace(oldText, newText, StringComparison.Ordinal));
    }

    private static void AssertFailure(
        OpenBladeReleaseVerificationFailure expected,
        Action action)
    {
        OpenBladeReleaseVerificationException exception =
            Assert.Throws<OpenBladeReleaseVerificationException>(action);
        Assert.Equal(expected, exception.Failure);
    }

    private static async Task AssertFailureAsync(
        OpenBladeReleaseVerificationFailure expected,
        Func<Task> action)
    {
        OpenBladeReleaseVerificationException exception =
            await Assert.ThrowsAsync<OpenBladeReleaseVerificationException>(action);
        Assert.Equal(expected, exception.Failure);
    }

    private sealed class TestRelease : IDisposable
    {
        private readonly ECDsa signingKey;

        private TestRelease(
            ECDsa signingKey,
            byte[] manifestBytes,
            byte[] signatureBytes,
            OpenBladeReleaseTrustPolicy policy,
            byte[] installerBytes,
            string installerSha256)
        {
            this.signingKey = signingKey;
            ManifestBytes = manifestBytes;
            SignatureBytes = signatureBytes;
            Policy = policy;
            InstallerBytes = installerBytes;
            InstallerSha256 = installerSha256;
        }

        public byte[] ManifestBytes { get; }
        public byte[] SignatureBytes { get; }
        public OpenBladeReleaseTrustPolicy Policy { get; }
        public byte[] InstallerBytes { get; }
        public string InstallerSha256 { get; }
        public string SigningKeyId { get; private init; } = string.Empty;

        public ECDsa CloneSigningKey()
        {
            ECDsa clone = ECDsa.Create();
            clone.ImportPkcs8PrivateKey(signingKey.ExportPkcs8PrivateKey(), out _);
            return clone;
        }

        public static TestRelease Create()
        {
            return CreateCore(12345678, new string('b', 64), []);
        }

        public static TestRelease CreateWithRealInstallerIdentity()
        {
            byte[] installerBytes = Encoding.UTF8.GetBytes("synthetic OpenBlade installer fixture");
            string installerSha256 = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
            return CreateCore(installerBytes.Length, installerSha256, installerBytes);
        }

        private static TestRelease CreateCore(
            long installerSize,
            string installerSha256,
            byte[] installerBytes)
        {
            ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] subjectPublicKeyInfo = signingKey.ExportSubjectPublicKeyInfo();
            string signingKeyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(subjectPublicKeyInfo);
            OpenBladeReleaseManifest manifest = new()
            {
                SchemaVersion = OpenBladeReleaseManifestCodec.CurrentSchemaVersion,
                SignatureAlgorithm = OpenBladeReleaseManifestCodec.SignatureAlgorithm,
                SigningKeyCurve = OpenBladeReleaseManifestCodec.SigningKeyCurve,
                SigningKeyId = signingKeyId,
                InstallationId = new Guid("11111111-1111-1111-1111-111111111111"),
                Sequence = 1,
                Channel = OpenBladeReleaseManifestCodec.LocalDevChannel,
                Product = OpenBladeReleaseManifestCodec.Product,
                Version = "65535.1.2.3",
                SourceRevision = new string('a', 40),
                Architecture = OpenBladeReleaseManifestCodec.Architecture,
                MinimumWindowsBuild = OpenBladeReleaseManifestCodec.MinimumWindowsBuild,
                InstallerFileName = OpenBladeReleaseManifestCodec.InstallerFileName,
                BundleId = OpenBladeReleaseManifestCodec.BundleId,
                ProviderKey = OpenBladeReleaseManifestCodec.ProviderKey,
                UpgradeCode = OpenBladeReleaseManifestCodec.UpgradeCode,
                InstallerSize = installerSize,
                InstallerSha256 = installerSha256,
            };
            byte[] manifestBytes = OpenBladeReleaseManifestCodec.SerializeCanonical(manifest);
            byte[] signatureBytes = signingKey.SignHash(
                OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifestBytes),
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            OpenBladeReleaseTrustPolicy policy = new(
                true,
                [
                    new OpenBladeReleaseTrustKey(
                        signingKeyId,
                        Convert.ToBase64String(subjectPublicKeyInfo)),
                ]);

            return new TestRelease(
                signingKey,
                manifestBytes,
                signatureBytes,
                policy,
                installerBytes,
                installerSha256)
            {
                SigningKeyId = signingKeyId,
            };
        }

        public void Dispose()
        {
            signingKey.Dispose();
        }
    }
}
