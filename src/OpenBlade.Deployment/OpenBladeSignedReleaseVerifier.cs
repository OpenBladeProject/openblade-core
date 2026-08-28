using System.Security.Cryptography;

namespace OpenBlade.Deployment;

public sealed record OpenBladeReleaseTrustKey(string KeyId, string SubjectPublicKeyInfoBase64);

public sealed record OpenBladeReleaseTrustPolicy(
    bool Enabled,
    IReadOnlyList<OpenBladeReleaseTrustKey> TrustedKeys);

public sealed record OpenBladeVerifiedRelease(
    OpenBladeReleaseManifest Manifest,
    string SigningKeyId);

public enum OpenBladeReleaseVerificationFailure
{
    PolicyDisabled,
    PolicyInvalid,
    ManifestSizeInvalid,
    ManifestInvalid,
    ManifestNotCanonical,
    SignatureLengthInvalid,
    UnknownSigningKey,
    PublicKeyInvalid,
    SignatureInvalid,
    InstallerSizeMismatch,
    InstallerHashMismatch,
}

public sealed class OpenBladeReleaseVerificationException : Exception
{
    public OpenBladeReleaseVerificationException(
        OpenBladeReleaseVerificationFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public OpenBladeReleaseVerificationFailure Failure { get; }
}

public static class OpenBladeSignedReleaseVerifier
{
    public const int P1363SignatureBytes = 64;
    public const int MaximumSubjectPublicKeyInfoBytes = 256;

    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static readonly byte[] SigningDomain =
        "OpenBlade-Local-Dev-Deployment-Manifest-v1\0"u8.ToArray();

    public static byte[] ComputeSigningDigest(ReadOnlySpan<byte> canonicalManifestBytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SigningDomain);
        hash.AppendData(canonicalManifestBytes);
        return hash.GetHashAndReset();
    }

    public static string ComputeSigningKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        return Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();
    }

    public static OpenBladeVerifiedRelease Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        OpenBladeReleaseTrustPolicy trustPolicy)
    {
        ArgumentNullException.ThrowIfNull(trustPolicy);

        if (!trustPolicy.Enabled)
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.PolicyDisabled,
                "Local development deployment trust is disabled.");
        }

        IReadOnlyDictionary<string, ECDsa> trustedKeys = LoadTrustedKeys(trustPolicy);
        try
        {
            OpenBladeReleaseManifest manifest = OpenBladeReleaseManifestCodec.ParseCanonical(manifestBytes);

            if (signatureBytes.Length != P1363SignatureBytes)
            {
                throw Failure(
                    OpenBladeReleaseVerificationFailure.SignatureLengthInvalid,
                    $"The local development deployment signature must contain exactly {P1363SignatureBytes} bytes.");
            }

            if (!trustedKeys.TryGetValue(manifest.SigningKeyId, out ECDsa? key))
            {
                throw Failure(
                    OpenBladeReleaseVerificationFailure.UnknownSigningKey,
                    "The local development deployment signing key is not trusted.");
            }

            byte[] signingDigest = ComputeSigningDigest(manifestBytes);
            if (!key.VerifyHash(
                    signingDigest,
                    signatureBytes,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw Failure(
                    OpenBladeReleaseVerificationFailure.SignatureInvalid,
                    "The detached local development deployment signature is invalid.");
            }

            return new OpenBladeVerifiedRelease(manifest, manifest.SigningKeyId);
        }
        finally
        {
            foreach (ECDsa key in trustedKeys.Values)
            {
                key.Dispose();
            }
        }
    }

    public static async Task VerifyInstallerAsync(
        Stream installerStream,
        OpenBladeVerifiedRelease verifiedRelease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installerStream);
        ArgumentNullException.ThrowIfNull(verifiedRelease);

        if (!installerStream.CanRead)
        {
            throw new ArgumentException("The installer stream must be readable.", nameof(installerStream));
        }

        long expectedSize = verifiedRelease.Manifest.InstallerSize;
        byte[] buffer = new byte[81920];
        long actualSize = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            int bytesRead = await installerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            actualSize = checked(actualSize + bytesRead);
            if (actualSize > expectedSize)
            {
                throw Failure(
                    OpenBladeReleaseVerificationFailure.InstallerSizeMismatch,
                    "The installer contains more bytes than the signed local development manifest permits.");
            }

            hash.AppendData(buffer, 0, bytesRead);
        }

        if (actualSize != expectedSize)
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.InstallerSizeMismatch,
                "The installer size does not match the signed local development manifest.");
        }

        byte[] actualHash = hash.GetHashAndReset();
        byte[] expectedHash = Convert.FromHexString(verifiedRelease.Manifest.InstallerSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.InstallerHashMismatch,
                "The installer SHA-256 does not match the signed local development manifest.");
        }
    }

    private static IReadOnlyDictionary<string, ECDsa> LoadTrustedKeys(OpenBladeReleaseTrustPolicy trustPolicy)
    {
        if (trustPolicy.TrustedKeys is null || trustPolicy.TrustedKeys.Count == 0)
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.PolicyInvalid,
                "An enabled local development trust policy must pin at least one public key.");
        }

        Dictionary<string, ECDsa> trustedKeys = new(StringComparer.Ordinal);
        try
        {
            foreach (OpenBladeReleaseTrustKey trustedKey in trustPolicy.TrustedKeys)
            {
                if (trustedKey is null
                    || string.IsNullOrWhiteSpace(trustedKey.KeyId)
                    || string.IsNullOrWhiteSpace(trustedKey.SubjectPublicKeyInfoBase64)
                    || trustedKeys.ContainsKey(trustedKey.KeyId))
                {
                    throw Failure(
                        OpenBladeReleaseVerificationFailure.PolicyInvalid,
                        "The local development trust policy contains an invalid or duplicate key identifier.");
                }

                trustedKeys.Add(trustedKey.KeyId, LoadTrustedKey(trustedKey));
            }

            return trustedKeys;
        }
        catch
        {
            foreach (ECDsa key in trustedKeys.Values)
            {
                key.Dispose();
            }

            throw;
        }
    }

    private static ECDsa LoadTrustedKey(OpenBladeReleaseTrustKey trustedKey)
    {
        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(trustedKey.SubjectPublicKeyInfoBase64);
        }
        catch (FormatException exception)
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
                "A pinned local development public key is not valid base64.",
                exception);
        }

        if (subjectPublicKeyInfo.Length == 0
            || subjectPublicKeyInfo.Length > MaximumSubjectPublicKeyInfoBytes
            || !string.Equals(
                Convert.ToBase64String(subjectPublicKeyInfo),
                trustedKey.SubjectPublicKeyInfoBase64,
                StringComparison.Ordinal))
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
                "A pinned local development public key is not canonical bounded SPKI DER base64.");
        }


        if (!string.Equals(
                trustedKey.KeyId,
                ComputeSigningKeyId(subjectPublicKeyInfo),
                StringComparison.Ordinal))
        {
            throw Failure(
                OpenBladeReleaseVerificationFailure.PolicyInvalid,
                "A pinned local development key identifier must equal the lowercase SHA-256 of its SPKI DER bytes.");
        }

        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
            ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
            if (bytesRead != subjectPublicKeyInfo.Length
                || key.KeySize != 256
                || !string.Equals(parameters.Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal)
                || parameters.Q.X?.Length != 32
                || parameters.Q.Y?.Length != 32)
            {
                throw Failure(
                    OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
                    "A pinned local development public key must be an ECDSA NIST P-256 SPKI public key.");
            }

            return key;
        }
        catch (OpenBladeReleaseVerificationException)
        {
            key.Dispose();
            throw;
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw Failure(
                OpenBladeReleaseVerificationFailure.PublicKeyInvalid,
                "A pinned local development public key is not a valid ECDSA NIST P-256 SPKI public key.",
                exception);
        }
    }

    private static OpenBladeReleaseVerificationException Failure(
        OpenBladeReleaseVerificationFailure failure,
        string message,
        Exception? innerException = null)
    {
        return new OpenBladeReleaseVerificationException(failure, message, innerException);
    }
}
