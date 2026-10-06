using System.Security.Cryptography;
using System.Text;
using OpenBlade.Deployment;
using OpenBlade.LocalDevDeploymentTool;

namespace OpenBlade.LocalDevDeploymentTool.Tests;

public sealed class LocalDevDeploymentToolTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.LocalDevDeployment.Tool.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Stage_WritesOnlyFixedEnrollmentBoundSignedInboxFiles()
    {
        Directory.CreateDirectory(root);
        string inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(inbox);
        string installer = Path.Combine(root, LocalDevDeploymentToolApplication.InstallerFileName);
        await File.WriteAllBytesAsync(installer, [1, 2, 3, 4]);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        Guid installationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string sid = "S-1-5-21-1-2-3-1001";
        string receipt = Path.Combine(root, "receipt.json");
        await File.WriteAllBytesAsync(
            receipt,
            LocalDevDeploymentReceiptCodec.SerializeCanonical(new(
                1,
                installationId,
                sid,
                LocalDevDeploymentReceiptCodec.PolicyId,
                OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(spki),
                Convert.ToBase64String(spki),
                "1.0.0",
                DateTimeOffset.UnixEpoch)));
        StringWriter output = new();
        StringWriter error = new();

        int result = await LocalDevDeploymentToolApplication.RunAsync(
            [
                "stage",
                "--installer",
                installer,
                "--version",
                "65535.1.2.3",
                "--source-revision",
                new string('a', 40),
                "--key-name",
                "test-key",
            ],
            output,
            error,
            new FakeSignerFactory(key),
            new FakeLayout(receipt, inbox, sid),
            new FixedSequenceSource(42),
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(
            new[]
            {
                LocalDevDeploymentToolApplication.ManifestFileName,
                LocalDevDeploymentToolApplication.SignatureFileName,
                LocalDevDeploymentToolApplication.InstallerFileName,
            }.Order(StringComparer.Ordinal),
            Directory.GetFiles(inbox)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal));
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(
            inbox,
            LocalDevDeploymentToolApplication.ManifestFileName));
        byte[] signature = await File.ReadAllBytesAsync(Path.Combine(
            inbox,
            LocalDevDeploymentToolApplication.SignatureFileName));
        OpenBladeVerifiedRelease verified = OpenBladeSignedReleaseVerifier.Verify(
            manifest,
            signature,
            new OpenBladeReleaseTrustPolicy(
                true,
                [
                    new OpenBladeReleaseTrustKey(
                        OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(spki),
                        Convert.ToBase64String(spki)),
                ]));
        Assert.Equal(installationId, verified.Manifest.InstallationId);
        Assert.Equal(42, verified.Manifest.Sequence);
        Assert.Equal(OpenBladeReleaseManifestCodec.LocalDevChannel, verified.Manifest.Channel);
        Assert.Equal(new string('a', 40), verified.Manifest.SourceRevision);
    }

    [Fact]
    public async Task Stage_RejectsKeyDifferentFromEnrollmentWithoutWritingInbox()
    {
        Directory.CreateDirectory(root);
        string inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(inbox);
        string installer = Path.Combine(root, LocalDevDeploymentToolApplication.InstallerFileName);
        await File.WriteAllBytesAsync(installer, [1]);
        using ECDsa enrolledKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] enrolledSpki = enrolledKey.ExportSubjectPublicKeyInfo();
        string receipt = Path.Combine(root, "receipt.json");
        await File.WriteAllBytesAsync(
            receipt,
            LocalDevDeploymentReceiptCodec.SerializeCanonical(new(
                1,
                Guid.NewGuid(),
                "S-1-5-21-1-2-3-1001",
                LocalDevDeploymentReceiptCodec.PolicyId,
                OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(enrolledSpki),
                Convert.ToBase64String(enrolledSpki),
                "1.0.0",
                DateTimeOffset.UnixEpoch)));

        int result = await LocalDevDeploymentToolApplication.RunAsync(
            [
                "stage",
                "--installer",
                installer,
                "--version",
                "65535.1.2.3",
                "--source-revision",
                new string('a', 40),
                "--key-name",
                "different-key",
            ],
            TextWriter.Null,
            TextWriter.Null,
            new FakeSignerFactory(differentKey),
            new FakeLayout(receipt, inbox, "S-1-5-21-1-2-3-1001"),
            new FixedSequenceSource(1),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(Directory.GetFileSystemEntries(inbox));
    }

    [Theory]
    [InlineData()]
    [InlineData("stage")]
    [InlineData("inspect-key", "--key-name", "key", "--extra", "value")]
    [InlineData("create-key", "--key-name", "key", "--extra", "value")]
    public void Parser_RejectsAnythingOutsideFixedCommands(params string[] args)
    {
        Assert.Throws<LocalDevDeploymentToolException>(
            () => LocalDevDeploymentToolCommandParser.Parse(args));
    }

    [Fact]
    public async Task CreateKey_UsesCreateOnceFactoryAndWritesPublicIdentity()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        FakeSignerFactory factory = new(key);
        StringWriter output = new();
        StringWriter error = new();

        int result = await LocalDevDeploymentToolApplication.RunAsync(
            ["create-key", "--key-name", "OpenBlade Local Dev Test"],
            output,
            error,
            factory,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal("OpenBlade Local Dev Test", factory.CreatedKeyName);
        Assert.Null(factory.OpenedKeyName);
        Assert.Contains(
            OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(
                key.ExportSubjectPublicKeyInfo()),
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateKey_WhenNameExistsFailsWithoutWritingIdentity()
    {
        StringWriter output = new();
        StringWriter error = new();

        int result = await LocalDevDeploymentToolApplication.RunAsync(
            ["create-key", "--key-name", "existing-key"],
            output,
            error,
            new ExistingKeySignerFactory(),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("not replaced", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_RejectsUnknownProperty()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"unknown\":true}");
        Assert.Throws<LocalDevDeploymentToolException>(
            () => LocalDevDeploymentReceiptCodec.ParseCanonical(bytes));
    }

    [Fact]
    public void PlatformKeyPolicy_RequiresExactHardwareTpmSigningKey()
    {
        PlatformCngKeyPolicy.Validate(
            CngProvider.MicrosoftPlatformCryptoProvider.Provider,
            PlatformCngKeyPolicy.HardwareImplementationFlag,
            CngAlgorithm.ECDsaP256,
            CngExportPolicies.None,
            CngKeyUsages.Signing,
            isEphemeral: false);
        Assert.Throws<LocalDevDeploymentToolException>(() => PlatformCngKeyPolicy.Validate(
            CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider,
            PlatformCngKeyPolicy.HardwareImplementationFlag,
            CngAlgorithm.ECDsaP256,
            CngExportPolicies.None,
            CngKeyUsages.Signing,
            isEphemeral: false));
        Assert.Throws<LocalDevDeploymentToolException>(() => PlatformCngKeyPolicy.Validate(
            CngProvider.MicrosoftPlatformCryptoProvider.Provider,
            implementationType: 0,
            CngAlgorithm.ECDsaP256,
            CngExportPolicies.None,
            CngKeyUsages.Signing,
            isEphemeral: false));
        PlatformCngKeyPolicy.Validate(
            CngProvider.MicrosoftPlatformCryptoProvider.Provider,
            PlatformCngKeyPolicy.HardwareImplementationFlag,
            CngAlgorithm.ECDsa,
            CngExportPolicies.None,
            CngKeyUsages.Signing,
            isEphemeral: false);
        Assert.Throws<LocalDevDeploymentToolException>(() => PlatformCngKeyPolicy.Validate(
            CngProvider.MicrosoftPlatformCryptoProvider.Provider,
            PlatformCngKeyPolicy.HardwareImplementationFlag,
            CngAlgorithm.Rsa,
            CngExportPolicies.None,
            CngKeyUsages.Signing,
            isEphemeral: false));
    }

    [Fact]
    public void PlatformKeyCreation_IsCreateOnceSigningOnlyNonExportableP256()
    {
        CngKeyCreationParameters parameters =
            WindowsPlatformCngSignerFactory.CreateKeyParameters();

        Assert.Equal(CngProvider.MicrosoftPlatformCryptoProvider, parameters.Provider);
        Assert.Equal(CngAlgorithm.ECDsaP256, WindowsPlatformCngSignerFactory.CreateAlgorithm);
        Assert.Equal(CngExportPolicies.None, parameters.ExportPolicy);
        Assert.Equal(CngKeyUsages.Signing, parameters.KeyUsage);
        Assert.Equal(CngKeyCreationOptions.None, parameters.KeyCreationOptions);
        CngUIPolicy uiPolicy = Assert.IsType<CngUIPolicy>(parameters.UIPolicy);
        Assert.Equal(CngUIProtectionLevels.None, uiPolicy.ProtectionLevel);
        Assert.Equal(
            CngKeyCreationOptions.None,
            parameters.KeyCreationOptions & (
                CngKeyCreationOptions.MachineKey
                | CngKeyCreationOptions.OverwriteExistingKey));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeSignerFactory(ECDsa key) : ILocalDevDeploymentSignerFactory
    {
        internal string? OpenedKeyName { get; private set; }

        internal string? CreatedKeyName { get; private set; }

        public ILocalDevDeploymentSigner Open(string keyName)
        {
            OpenedKeyName = keyName;
            return new FakeSigner(key);
        }

        public ILocalDevDeploymentSigner CreateOnce(string keyName)
        {
            CreatedKeyName = keyName;
            return new FakeSigner(key);
        }
    }

    private sealed class FakeSigner(ECDsa key) : ILocalDevDeploymentSigner
    {
        public byte[] ExportSubjectPublicKeyInfo() => key.ExportSubjectPublicKeyInfo();

        public byte[] SignHash(ReadOnlySpan<byte> digest) => key.SignHash(
            digest,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose()
        {
        }
    }

    private sealed class ExistingKeySignerFactory : ILocalDevDeploymentSignerFactory
    {
        public ILocalDevDeploymentSigner Open(string keyName) =>
            throw new InvalidOperationException();

        public ILocalDevDeploymentSigner CreateOnce(string keyName) =>
            throw new LocalDevDeploymentToolException(
                "The TPM development key already exists and was not replaced.");
    }

    private sealed record FakeLayout(
        string ReceiptPath,
        string InboxDirectory,
        string CurrentUserSid) : ILocalDevDeploymentLayout;

    private sealed class FixedSequenceSource(long sequence) : ILocalDevSequenceSource
    {
        public long Next() => sequence;
    }
}
