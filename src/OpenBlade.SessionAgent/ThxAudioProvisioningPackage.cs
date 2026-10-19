using System.Net;
using System.Security.Cryptography;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record ThxAudioProvisioningArtifact(
    ThxAudioProvisioningStep Step,
    string FileName,
    Uri Source,
    long Length,
    string Sha256,
    IReadOnlyList<string> Arguments);

internal static class ThxAudioProvisioningPackage
{
    internal const string SignerSubject = "CN=THX LTD., O=THX LTD., L=Novato, S=California, C=US, OID.1.3.6.1.4.1.311.60.2.1.2=DELAWARE, OID.1.3.6.1.4.1.311.60.2.1.3=US, SERIALNUMBER=6128649, OID.2.5.4.15=Private Organization";
    internal const string SignerThumbprint = "B23FAAE4D5ADEDA77BCCD9F12F32C92D3FF755D5";

    internal static IReadOnlyList<ThxAudioProvisioningArtifact> Artifacts { get; } =
    [
        new(ThxAudioProvisioningStep.InstallDriverStack,
            "THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe"),
            51585680, "0154A94D60D68882E92BF03DC1265A8C82FE8432B64DD348438862194E89E32B",
            ["/s", "/norestart"]),
        new(ThxAudioProvisioningStep.InstallProperties,
            "thx_rznb11_properties_updater_4.2.37.1845.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb11_properties_updater_4.2.37.1845.exe"),
            4397200, "8333667C11EFFC71ED09BBECB439F29C0642385F70C835287BE00F04C02BD6C8", []),
        new(ThxAudioProvisioningStep.InstallSurroundConfiguration,
            "thx_rznb11_714_patcher_config_updater_4.2.37.1845.exe",
            new("https://app-assets.razer.com/files/synapse/devices/common/THX/thx_rznb11_714_patcher_config_updater_4.2.37.1845.exe"),
            590992, "04F4D4A6A3F69391347BF08512FA5DC2C918E7443B5EA33024DFE1486DEB12AE", []),
    ];

    internal static async Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken)
    {
        ThxAudioProvisioningJournal.EnsureProtectedDirectory(destination);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        foreach (ThxAudioProvisioningArtifact artifact in Artifacts)
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
            return;
        }
        throw new InvalidDataException("THX provisioning download redirected too many times.");
    }

    internal static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !uri.Host.Equals("app-assets.razer.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("THX provisioning download used an unapproved address.");
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
