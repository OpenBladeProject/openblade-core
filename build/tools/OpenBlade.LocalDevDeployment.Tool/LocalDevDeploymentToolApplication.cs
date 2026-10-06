using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBlade.Deployment;

namespace OpenBlade.LocalDevDeploymentTool;

internal sealed partial class LocalDevDeploymentToolApplication
{
    internal const string ManifestFileName = "OpenBlade-Local-Dev-Deployment.json";
    internal const string SignatureFileName = "OpenBlade-Local-Dev-Deployment.sig";
    internal const string InstallerFileName = "OpenBlade-Setup.exe";

    private static readonly string[] ExactInboxFiles =
    [
        ManifestFileName,
        SignatureFileName,
        InstallerFileName,
    ];

    private readonly ILocalDevDeploymentSignerFactory signerFactory;
    private readonly ILocalDevDeploymentLayout layout;
    private readonly ILocalDevSequenceSource sequenceSource;
    private readonly TextWriter output;

    private LocalDevDeploymentToolApplication(
        ILocalDevDeploymentSignerFactory signerFactory,
        ILocalDevDeploymentLayout layout,
        ILocalDevSequenceSource sequenceSource,
        TextWriter output)
    {
        this.signerFactory = signerFactory;
        this.layout = layout;
        this.sequenceSource = sequenceSource;
        this.output = output;
    }

    internal static Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        ILocalDevDeploymentSignerFactory signerFactory,
        CancellationToken cancellationToken) => RunAsync(
            args,
            output,
            error,
            signerFactory,
            new WindowsLocalDevDeploymentLayout(),
            new UserLocalDevSequenceSource(),
            cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        ILocalDevDeploymentSignerFactory signerFactory,
        ILocalDevDeploymentLayout layout,
        ILocalDevSequenceSource sequenceSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(signerFactory);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sequenceSource);

        try
        {
            LocalDevDeploymentToolCommand command = LocalDevDeploymentToolCommandParser.Parse(args);
            LocalDevDeploymentToolApplication application = new(
                signerFactory,
                layout,
                sequenceSource,
                output);
            switch (command)
            {
                case StageDeploymentCommand stage:
                    await application.StageAsync(stage, cancellationToken).ConfigureAwait(false);
                    break;
                case InspectKeyCommand inspect:
                    await application.InspectKeyAsync(inspect).ConfigureAwait(false);
                    break;
                case CreateKeyCommand create:
                    await application.CreateKeyAsync(create).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The parsed local-development command is unsupported.");
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("The local-development operation was cancelled.")
                .ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is
            LocalDevDeploymentToolException or OpenBladeReleaseVerificationException
            or CryptographicException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task StageAsync(
        StageDeploymentCommand command,
        CancellationToken cancellationToken)
    {
        string installerPath = ValidateInstaller(command.InstallerPath);
        ValidateIdentity(command.Version, command.SourceRevision);
        ValidateKeyName(command.KeyName);
        LocalDevDeploymentReceipt receipt = LocalDevDeploymentReceiptCodec.Read(
            layout.ReceiptPath);
        if (!string.Equals(
                receipt.EnrolledAccountSid,
                layout.CurrentUserSid,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The current user is not the enrolled local-development account.");
        }

        using ILocalDevDeploymentSigner signer = signerFactory.Open(command.KeyName);
        byte[] subjectPublicKeyInfo = signer.ExportSubjectPublicKeyInfo();
        string signingKeyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(
            subjectPublicKeyInfo);
        if (!string.Equals(
                signingKeyId,
                receipt.DevelopmentSigningKeyId,
                StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                subjectPublicKeyInfo,
                Convert.FromBase64String(
                    receipt.DevelopmentSigningSubjectPublicKeyInfoBase64)))
        {
            throw new UnauthorizedAccessException(
                "The TPM signing key does not match the attended enrollment receipt.");
        }

        await using FileStream installer = OpenInstaller(installerPath);
        long installerSize = installer.Length;
        byte[] installerHash = await SHA256.HashDataAsync(installer, cancellationToken)
            .ConfigureAwait(false);
        string installerSha256 = Convert.ToHexString(installerHash).ToLowerInvariant();
        long sequence = sequenceSource.Next();
        OpenBladeReleaseManifest manifest = new()
        {
            SchemaVersion = OpenBladeReleaseManifestCodec.CurrentSchemaVersion,
            SignatureAlgorithm = OpenBladeReleaseManifestCodec.SignatureAlgorithm,
            SigningKeyCurve = OpenBladeReleaseManifestCodec.SigningKeyCurve,
            SigningKeyId = signingKeyId,
            InstallationId = receipt.InstallationId,
            Sequence = sequence,
            Channel = OpenBladeReleaseManifestCodec.LocalDevChannel,
            Product = OpenBladeReleaseManifestCodec.Product,
            Version = command.Version,
            SourceRevision = command.SourceRevision,
            Architecture = OpenBladeReleaseManifestCodec.Architecture,
            MinimumWindowsBuild = OpenBladeReleaseManifestCodec.MinimumWindowsBuild,
            InstallerFileName = InstallerFileName,
            BundleId = OpenBladeReleaseManifestCodec.BundleId,
            ProviderKey = OpenBladeReleaseManifestCodec.ProviderKey,
            UpgradeCode = OpenBladeReleaseManifestCodec.UpgradeCode,
            InstallerSize = installerSize,
            InstallerSha256 = installerSha256,
        };
        byte[] manifestBytes = OpenBladeReleaseManifestCodec.SerializeCanonical(manifest);
        byte[] signatureBytes = signer.SignHash(
            OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifestBytes));
        OpenBladeReleaseTrustPolicy trust = new(
            Enabled: true,
            TrustedKeys:
            [
                new OpenBladeReleaseTrustKey(
                    signingKeyId,
                    Convert.ToBase64String(subjectPublicKeyInfo)),
            ]);
        OpenBladeVerifiedRelease verified = OpenBladeSignedReleaseVerifier.Verify(
            manifestBytes,
            signatureBytes,
            trust);
        installer.Position = 0;
        await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
            installer,
            verified,
            cancellationToken).ConfigureAwait(false);
        installer.Position = 0;
        await PublishInboxAsync(
            manifestBytes,
            signatureBytes,
            installer,
            cancellationToken).ConfigureAwait(false);
        await WriteStageResultAsync(
            receipt.InstallationId,
            sequence,
            signingKeyId,
            command.SourceRevision,
            installerSha256).ConfigureAwait(false);
    }

    private async Task InspectKeyAsync(InspectKeyCommand command)
    {
        ValidateKeyName(command.KeyName);
        using ILocalDevDeploymentSigner signer = signerFactory.Open(command.KeyName);
        await WriteKeyIdentityAsync(signer).ConfigureAwait(false);
    }

    private async Task CreateKeyAsync(CreateKeyCommand command)
    {
        ValidateKeyName(command.KeyName);
        using ILocalDevDeploymentSigner signer = signerFactory.CreateOnce(command.KeyName);
        await WriteKeyIdentityAsync(signer).ConfigureAwait(false);
    }

    private async Task WriteKeyIdentityAsync(ILocalDevDeploymentSigner signer)
    {
        byte[] subjectPublicKeyInfo = signer.ExportSubjectPublicKeyInfo();
        using MemoryStream result = new();
        using (Utf8JsonWriter writer = new(result))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "keyId",
                OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(subjectPublicKeyInfo));
            writer.WriteString(
                "subjectPublicKeyInfoBase64",
                Convert.ToBase64String(subjectPublicKeyInfo));
            writer.WriteString(
                "provider",
                CngProvider.MicrosoftPlatformCryptoProvider.Provider);
            writer.WriteBoolean("hardwareBacked", true);
            writer.WriteEndObject();
        }

        await output.WriteLineAsync(Encoding.UTF8.GetString(result.ToArray()))
            .ConfigureAwait(false);
    }

    private async Task PublishInboxAsync(
        byte[] manifestBytes,
        byte[] signatureBytes,
        Stream installer,
        CancellationToken cancellationToken)
    {
        string inbox = Path.GetFullPath(layout.InboxDirectory);
        if (!Directory.Exists(inbox)
            || (File.GetAttributes(inbox) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LocalDevDeploymentToolException(
                "The fixed local-development inbox is unavailable or unsafe.");
        }

        string[] entries = Directory.EnumerateFileSystemEntries(inbox)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] allowedExisting = ExactInboxFiles.Order(StringComparer.Ordinal).ToArray();
        if (entries.Length != 0 && !entries.SequenceEqual(allowedExisting, StringComparer.Ordinal))
        {
            throw new LocalDevDeploymentToolException(
                "The fixed local-development inbox contains an unexpected or incomplete request.");
        }

        foreach (string name in ExactInboxFiles)
        {
            string existing = Path.Combine(inbox, name);
            if (File.Exists(existing))
            {
                File.Delete(existing);
            }
        }

        string suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        string temporaryManifest = Path.Combine(inbox, ManifestFileName + suffix);
        string temporarySignature = Path.Combine(inbox, SignatureFileName + suffix);
        string temporaryInstaller = Path.Combine(inbox, InstallerFileName + suffix);
        List<string> published = [];
        try
        {
            await WriteCreateNewAsync(
                temporaryManifest,
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            await WriteCreateNewAsync(
                temporarySignature,
                signatureBytes,
                cancellationToken).ConfigureAwait(false);
            await using (FileStream outputInstaller = OpenCreateNew(temporaryInstaller))
            {
                await installer.CopyToAsync(outputInstaller, cancellationToken).ConfigureAwait(false);
                await outputInstaller.FlushAsync(cancellationToken).ConfigureAwait(false);
                outputInstaller.Flush(flushToDisk: true);
            }

            MovePublished(temporaryInstaller, Path.Combine(inbox, InstallerFileName), published);
            MovePublished(temporarySignature, Path.Combine(inbox, SignatureFileName), published);
            MovePublished(temporaryManifest, Path.Combine(inbox, ManifestFileName), published);
            string[] finalEntries = Directory.EnumerateFileSystemEntries(inbox)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!finalEntries.SequenceEqual(allowedExisting, StringComparer.Ordinal))
            {
                throw new LocalDevDeploymentToolException(
                    "The fixed local-development inbox does not contain the complete request.");
            }
        }
        catch
        {
            foreach (string path in published)
            {
                File.Delete(path);
            }

            throw;
        }
        finally
        {
            foreach (string path in
                new[] { temporaryManifest, temporarySignature, temporaryInstaller })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private async Task WriteStageResultAsync(
        Guid installationId,
        long sequence,
        string signingKeyId,
        string sourceRevision,
        string installerSha256)
    {
        using MemoryStream result = new();
        using (Utf8JsonWriter writer = new(result))
        {
            writer.WriteStartObject();
            writer.WriteString("installationId", installationId.ToString("D"));
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("signingKeyId", signingKeyId);
            writer.WriteString("sourceRevision", sourceRevision);
            writer.WriteString("installerSha256", installerSha256);
            writer.WriteEndObject();
        }

        await output.WriteLineAsync(Encoding.UTF8.GetString(result.ToArray()))
            .ConfigureAwait(false);
    }

    private static FileStream OpenInstaller(string path)
    {
        FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > OpenBladeReleaseManifestCodec.MaximumInstallerBytes)
        {
            stream.Dispose();
            throw new LocalDevDeploymentToolException(
                "The OpenBlade installer has an invalid size.");
        }

        return stream;
    }

    private static string ValidateInstaller(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), InstallerFileName, StringComparison.Ordinal)
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LocalDevDeploymentToolException(
                $"The installer must be a regular file named exactly '{InstallerFileName}'.");
        }

        return fullPath;
    }

    private static void ValidateIdentity(string version, string sourceRevision)
    {
        if (!OpenBladeReleaseManifestCodec.IsCanonicalLocalDevVersion(version)
            || !SourceRevisionPattern().IsMatch(sourceRevision))
        {
            throw new LocalDevDeploymentToolException(
                "The version or exact lowercase Git source revision is invalid.");
        }
    }

    private static void ValidateKeyName(string value)
    {
        if (value.Length is <= 0 or > 256
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new LocalDevDeploymentToolException("The TPM CNG key name is invalid.");
        }
    }

    private static async Task WriteCreateNewAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenCreateNew(path);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenCreateNew(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static void MovePublished(string source, string destination, List<string> published)
    {
        File.Move(source, destination, overwrite: false);
        published.Add(destination);
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex SourceRevisionPattern();
}
