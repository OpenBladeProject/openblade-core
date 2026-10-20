using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioEmbeddedInstaller(
    string FileName,
    long CompressedOffset,
    int CompressedLength,
    long Length,
    string Sha256);

internal sealed record ThxAudioProvisioningArtifact(
    ThxAudioProvisioningStep Step,
    string FileName,
    Uri Source,
    long Length,
    string Sha256,
    IReadOnlyList<string> Arguments,
    ThxAudioEmbeddedInstaller? EmbeddedInstaller = null)
{
    internal string ExecutableFileName => EmbeddedInstaller?.FileName ?? FileName;
    internal long ExecutableLength => EmbeddedInstaller?.Length ?? Length;
    internal string ExecutableSha256 => EmbeddedInstaller?.Sha256 ?? Sha256;
}

internal static class ThxAudioProvisioningPackage
{
    internal const string SignerSubject = "CN=THX LTD., O=THX LTD., L=Novato, S=California, C=US, OID.1.3.6.1.4.1.311.60.2.1.2=DELAWARE, OID.1.3.6.1.4.1.311.60.2.1.3=US, SERIALNUMBER=6128649, OID.2.5.4.15=Private Organization";
    internal const string SignerThumbprint = "B23FAAE4D5ADEDA77BCCD9F12F32C92D3FF755D5";
    private const int MaximumCompressedInstallerLength = 64 * 1024 * 1024;
    private const string Rznb12BaseInstallerUrl = "https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/thx-rznb12-4.2.37.1621/THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe";

    internal static IReadOnlyList<ThxAudioProvisioningArtifact> Artifacts { get; } =
        Array.AsReadOnly<ThxAudioProvisioningArtifact>(
        [
        new(ThxAudioProvisioningStep.InstallDriverStack,
            "THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe"),
            51585680, "0154A94D60D68882E92BF03DC1265A8C82FE8432B64DD348438862194E89E32B",
            ["/quiet", "/norestart"],
            new("thx-rznb11-base.exe", 0x8B152, 50445093, 51037408,
                "819B1504B025105589B5899CC09BB612B7AD83F8DED8DE0D087F81D4085542ED")),
        new(ThxAudioProvisioningStep.InstallProperties,
            "thx_rznb11_properties_updater_4.2.37.1845.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb11_properties_updater_4.2.37.1845.exe"),
            4397200, "8333667C11EFFC71ED09BBECB439F29C0642385F70C835287BE00F04C02BD6C8", []),
        new(ThxAudioProvisioningStep.InstallSurroundConfiguration,
            "thx_rznb11_714_patcher_config_updater_4.2.37.1845.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb11_714_patcher_config_updater_4.2.37.1845.exe"),
            590992, "04F4D4A6A3F69391347BF08512FA5DC2C918E7443B5EA33024DFE1486DEB12AE", []),
        ]);

    internal static IReadOnlyList<ThxAudioProvisioningArtifact> Rznb12Artifacts { get; } =
        Array.AsReadOnly<ThxAudioProvisioningArtifact>(
        [
        new(ThxAudioProvisioningStep.InstallDriverStack,
            "THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe",
            new(Rznb12BaseInstallerUrl),
            53720208, "32392A63FC704DFC4BFBE4F33ED038773BBDE21AC88C61BAEEA013E3F98E8CF2",
            ["/quiet", "/norestart"],
            new("thx-rznb12-base.exe", 0x8CE40, 52553413, 53145160,
                "DE47E110A3E528249EAF59FA7932C1A568183A38F7B52246EE395A0418B14620")),
        new(ThxAudioProvisioningStep.InstallProperties,
            "thx_rznb12_properties_updater_4.2.37.1846.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb12_properties_updater_4.2.37.1846.exe"),
            4375696, "5BBD258C599720B8C81ABB623CA512613081CD913BB2BDA68357F8C7713F845B", []),
        new(ThxAudioProvisioningStep.InstallSurroundConfiguration,
            "thx_rznb12_714_patcher_config_updater_4.2.37.1846.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb12_714_patcher_config_updater_4.2.37.1846.exe"),
            590992, "1F542677BCE9CE0D3235D9A441C6FD9C8182DC364180B5E8632B3A67A591895F", []),
        ]);

    internal static IReadOnlyList<ThxAudioProvisioningArtifact> ForProduct(string productId) =>
        productId switch
        {
            "rznb11" => Artifacts,
            "rznb12" => Rznb12Artifacts,
            _ => throw new ArgumentOutOfRangeException(nameof(productId), productId,
                "Unsupported THX product ID."),
        };

    internal static async Task<string> AcquireVerifiedAsync(string destination, string productId,
        CancellationToken cancellationToken)
    {
        ThxAudioProvisioningJournal.EnsureProtectedDirectory(destination);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        foreach (ThxAudioProvisioningArtifact artifact in ForProduct(productId))
        {
            await DownloadAsync(destination, artifact, client, timeout.Token).ConfigureAwait(false);
        }
        return destination;
    }

    private static async Task DownloadAsync(string root, ThxAudioProvisioningArtifact artifact,
        HttpClient client, CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, artifact.FileName);
        ThxAudioProvisioningJournal.RejectReparse(path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough);
        Uri uri = artifact.Source;
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            ValidateUri(uri);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri location = response.Headers.Location
                    ?? throw new InvalidDataException("THX provisioning redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != artifact.Length)
            {
                throw new InvalidDataException("THX provisioning artifact length does not match its pin.");
            }
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await CopyPinnedLengthAsync(input, output, artifact.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            if (output.Length != artifact.Length || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(artifact.Sha256), SHA256.HashData(output)))
            {
                throw new InvalidDataException("THX provisioning artifact does not match its reviewed pin.");
            }
            ThxNativeComponentTrust.Verify(path, output.SafeFileHandle,
                SignerSubject, SignerThumbprint, cancellationToken);
            if (artifact.EmbeddedInstaller is { } embeddedInstaller)
            {
                string executablePath = Path.Combine(root, embeddedInstaller.FileName);
                ThxAudioProvisioningJournal.RejectReparse(executablePath);
                await using var executable = new FileStream(executablePath, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 16384,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await CopyEmbeddedInstallerAsync(output, executable, embeddedInstaller,
                    cancellationToken).ConfigureAwait(false);
                await executable.FlushAsync(cancellationToken).ConfigureAwait(false);
                executable.Position = 0;
                ThxNativeComponentTrust.Verify(executablePath, executable.SafeFileHandle,
                    SignerSubject, SignerThumbprint, cancellationToken);
            }
            return;
        }
        throw new InvalidDataException("THX provisioning download redirected too many times.");
    }

    internal static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidDataException("THX provisioning download used an unapproved address.");
        }
        if (uri.Host.Equals("app-assets.razer.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsoluteUri.Equals(Rznb12BaseInstallerUrl, StringComparison.Ordinal)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidDataException("THX provisioning download used an unapproved address.");
    }

    internal static async Task CopyEmbeddedInstallerAsync(Stream carrier, Stream output,
        ThxAudioEmbeddedInstaller metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!carrier.CanRead || !carrier.CanSeek)
        {
            throw new ArgumentException("THX provisioning carrier must be readable and seekable.",
                nameof(carrier));
        }
        if (!output.CanRead || !output.CanWrite || !output.CanSeek || output.Position != 0
            || output.Length != 0)
        {
            throw new ArgumentException("THX provisioning output must be an empty readable, writable and seekable stream.",
                nameof(output));
        }
        if (metadata.CompressedOffset < 0 || metadata.CompressedLength < 0 || metadata.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }
        if (metadata.CompressedLength > MaximumCompressedInstallerLength)
        {
            throw new InvalidDataException("THX embedded installer compressed data exceeds its fixed limit.");
        }
        if (metadata.CompressedOffset > carrier.Length
            || metadata.CompressedLength > carrier.Length - metadata.CompressedOffset)
        {
            throw new InvalidDataException("THX embedded installer range is outside its verified carrier.");
        }

        carrier.Position = metadata.CompressedOffset;
        byte[] compressed = GC.AllocateUninitializedArray<byte>(metadata.CompressedLength);
        await carrier.ReadExactlyAsync(compressed, cancellationToken).ConfigureAwait(false);
        await using var compressedStream = new MemoryStream(compressed, writable: false);
        await using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress,
            leaveOpen: false);
        await CopyPinnedLengthAsync(deflate, output, metadata.Length, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Position = 0;
        byte[] actualSha256 = await SHA256.HashDataAsync(output, cancellationToken)
            .ConfigureAwait(false);
        if (output.Length != metadata.Length || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(metadata.Sha256), actualSha256))
        {
            throw new InvalidDataException("THX embedded installer does not match its reviewed pin.");
        }
    }

    internal static async Task CopyPinnedLengthAsync(Stream input, Stream output, long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }
        byte[] buffer = new byte[16384];
        long remaining = expectedLength;
        while (remaining != 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("THX provisioning artifact ended before its pinned length.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("THX provisioning artifact exceeds its pinned length.");
        }
    }
}
