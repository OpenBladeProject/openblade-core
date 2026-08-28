using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenBlade.LocalDevDeploymentTool;

internal interface ILocalDevDeploymentSigner : IDisposable
{
    byte[] ExportSubjectPublicKeyInfo();

    byte[] SignHash(ReadOnlySpan<byte> digest);
}

internal interface ILocalDevDeploymentSignerFactory
{
    ILocalDevDeploymentSigner Open(string keyName);

    ILocalDevDeploymentSigner CreateOnce(string keyName);
}

internal sealed class WindowsPlatformCngSignerFactory : ILocalDevDeploymentSignerFactory
{
    private const int KeyAlreadyExistsHResult = unchecked((int)0x8009000F);
    internal static CngAlgorithm CreateAlgorithm => CngAlgorithm.ECDsaP256;

    public ILocalDevDeploymentSigner Open(string keyName) => OpenValidatedKey(CngKey.Open(
        keyName,
        CngProvider.MicrosoftPlatformCryptoProvider,
        CngKeyOpenOptions.None), deleteOnFailure: false);

    public ILocalDevDeploymentSigner CreateOnce(string keyName)
    {
        CngKey key;
        try
        {
            key = CngKey.Create(CreateAlgorithm, keyName, CreateKeyParameters());
        }
        catch (CryptographicException exception)
            when (exception.HResult == KeyAlreadyExistsHResult)
        {
            throw new LocalDevDeploymentToolException(
                "The TPM development key already exists and was not replaced.",
                exception);
        }

        return OpenValidatedKey(key, deleteOnFailure: true);
    }

    internal static CngKeyCreationParameters CreateKeyParameters() => new()
    {
        Provider = CngProvider.MicrosoftPlatformCryptoProvider,
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing,
        KeyCreationOptions = CngKeyCreationOptions.None,
        UIPolicy = new CngUIPolicy(CngUIProtectionLevels.None),
    };

    private static ILocalDevDeploymentSigner OpenValidatedKey(
        CngKey key,
        bool deleteOnFailure)
    {
        try
        {
            PlatformCngKeyPolicy.Validate(
                key.Provider?.Provider ?? string.Empty,
                WindowsPlatformCngProvider.GetImplementationType(),
                key.Algorithm,
                key.ExportPolicy,
                key.KeyUsage,
                key.IsEphemeral);
            return new WindowsPlatformCngSigner(key);
        }
        catch (Exception validationException)
        {
            Exception? cleanupException = null;
            if (deleteOnFailure)
            {
                try
                {
                    key.Delete();
                }
                catch (Exception exception) when (exception is
                    CryptographicException or InvalidOperationException)
                {
                    cleanupException = exception;
                }
            }

            key.Dispose();
            if (cleanupException is not null)
            {
                throw new LocalDevDeploymentToolException(
                    "The invalid TPM development key could not be removed after validation failed.",
                    new AggregateException(validationException, cleanupException));
            }

            throw;
        }
    }
}

internal static class WindowsPlatformCngProvider
{
    private const string ImplementationTypeProperty = "Impl Type";

    internal static int GetImplementationType()
    {
        nint provider = 0;
        int result = NCryptOpenStorageProvider(
            out provider,
            CngProvider.MicrosoftPlatformCryptoProvider.Provider,
            0);
        if (result != 0)
        {
            throw new CryptographicException(result);
        }

        try
        {
            byte[] value = new byte[sizeof(int)];
            result = NCryptGetProperty(
                provider,
                ImplementationTypeProperty,
                value,
                value.Length,
                out int resultSize,
                0);
            if (result != 0)
            {
                throw new CryptographicException(result);
            }
            if (resultSize != value.Length)
            {
                throw new LocalDevDeploymentToolException(
                    "The TPM provider implementation property is malformed.");
            }

            return BitConverter.ToInt32(value);
        }
        finally
        {
            _ = NCryptFreeObject(provider);
        }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(
        out nint provider,
        string providerName,
        uint flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptGetProperty(
        nint provider,
        string propertyName,
        [Out] byte[] output,
        int outputSize,
        out int resultSize,
        uint flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(nint provider);
}

internal static class PlatformCngKeyPolicy
{
    internal const int HardwareImplementationFlag = 0x00000001;

    internal static void Validate(
        string providerName,
        int implementationType,
        CngAlgorithm? algorithm,
        CngExportPolicies exportPolicy,
        CngKeyUsages keyUsage,
        bool isEphemeral)
    {
        if (!string.Equals(
                providerName,
                CngProvider.MicrosoftPlatformCryptoProvider.Provider,
                StringComparison.Ordinal))
        {
            throw new LocalDevDeploymentToolException(
                "The development signing key must use the Microsoft Platform Crypto Provider.");
        }

        if ((implementationType & HardwareImplementationFlag) == 0)
        {
            throw new LocalDevDeploymentToolException(
                "The development signing key must report hardware-backed implementation.");
        }

        if (algorithm != CngAlgorithm.ECDsaP256
            && algorithm != CngAlgorithm.ECDsa)
        {
            throw new LocalDevDeploymentToolException(
                "The development signing key must use ECDSA.");
        }

        if (exportPolicy != CngExportPolicies.None)
        {
            throw new LocalDevDeploymentToolException(
                "The development signing private key must be non-exportable.");
        }

        if (keyUsage != CngKeyUsages.Signing)
        {
            throw new LocalDevDeploymentToolException(
                "The development signing key must permit signing only.");
        }

        if (isEphemeral)
        {
            throw new LocalDevDeploymentToolException(
                "The development signing key must be persisted in the TPM provider.");
        }
    }
}

internal sealed class WindowsPlatformCngSigner : ILocalDevDeploymentSigner
{
    private readonly CngKey key;
    private readonly ECDsaCng signer;

    internal WindowsPlatformCngSigner(CngKey key)
    {
        this.key = key;
        signer = new ECDsaCng(key);
        ECParameters parameters = signer.ExportParameters(includePrivateParameters: false);
        if (signer.KeySize != 256
            || !string.Equals(
                parameters.Curve.Oid.Value,
                "1.2.840.10045.3.1.7",
                StringComparison.Ordinal)
            || parameters.Q.X?.Length != 32
            || parameters.Q.Y?.Length != 32)
        {
            signer.Dispose();
            throw new LocalDevDeploymentToolException(
                "The development signing key must use ECDSA NIST P-256.");
        }
    }

    public byte[] ExportSubjectPublicKeyInfo() => signer.ExportSubjectPublicKeyInfo();

    public byte[] SignHash(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != SHA256.HashSizeInBytes)
        {
            throw new LocalDevDeploymentToolException(
                "The signing digest must contain exactly 32 bytes.");
        }

        return signer.SignHash(
            digest,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        signer.Dispose();
        key.Dispose();
    }
}
