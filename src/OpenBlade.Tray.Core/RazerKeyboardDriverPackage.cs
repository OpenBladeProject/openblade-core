using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace OpenBlade.Tray;

/// <summary>Verified package acquisition. Public compatibility members retain the original 02E0 recipe.</summary>
public static class RazerKeyboardDriverPackage
{
    public const string ReleaseTag = "rz09-0581-1532-02e0-driver-stack-1.0.0.78-1.0.0.76";
    public const string ArchiveName = "Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76.zip";
    public const string PackageDirectory = "Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76";
    public const long ArchiveLength = 227111;
    public const string ArchiveSha256 = "715045E146971D3AD851E84433A76282E7FF7D407A0C22C0507427AF22ACDBE0";
    public static Uri DownloadUri { get; } = new($"https://github.com/OpenBladeProject/blade-driver-catalog/releases/download/{ReleaseTag}/{ArchiveName}");

    public sealed record PackageFile(string RelativePath, long Length, string Sha256);

    // Pins were reviewed against the release-tag manifest and the exact archive.
    public static IReadOnlyList<PackageFile> Files { get; } = Array.AsReadOnly(new PackageFile[]
    {
        new("manifest.json", 8739, "95D07F0E23B3BD5CABABF97829BD95CAA253BD751DF6A89D44A3FA657552215B"),
        new("README.txt", 577, "DB36C813D9F4A0FBC41622C887FEF1AA8F6273657DA5C65839EE4F7D614A600D"),
        new("SHA256SUMS.txt", 1719, "9EE42D7439E1A8C42E1A1DB9FDDA7121DB489862F14F2A9206C10BCF9163204C"),
        new("THIRD-PARTY-NOTICE.txt", 579, "79006950E651B977490669C3152FA34BCB21D29AC1F500560C42B7C4B7846BAA"),
        new("rzcommonu/rzcommonu.inf", 1646, "DD3C8D65C7C3CCF2093FE70FB21FB8DC724E837880566BAE629B3104A5B1B37B"),
        new("rzcommonu/rzcommon.cat", 13844, "1DFBA6428F727EA414F0FB5E938EE76063B1193A6CC12895396C9B89486C6F6E"),
        new("rzcommonu/RzCommon.sys", 64880, "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59"),
        new("rzdevu_02e0_dkm/rzdevu_02e0_dkm.inf", 1835, "C9831A19FE081F7AFA403E70116AB35762A3D4E339C4A7724CC6E64A3B035173"),
        new("rzdevu_02e0_dkm/rzdev_02e0_dkm.cat", 11771, "2FB8F3A324D63590DBECE9C42D71E0B6498DA9BDECCB10045824056ED1EE4FFF"),
        new("rzdevu_02e0_dkm/RzDev_02e0.sys", 65872, "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787"),
        new("rzdevu_02e0_vcon/rzdevu_02e0_vcon.inf", 1880, "BC3E0BCA22643930B25D4CF649144E89CFED0825493AA385931FF9F542FF3613"),
        new("rzdevu_02e0_vcon/rzdev_02e0_vcon.cat", 11778, "0C3841B570DBC80819B44EB6027D3611FC2B39F063A612910F58F60EC99F0E21"),
        new("rzdevu_02e0_vcon/RzDev_02e0.sys", 65872, "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787"),
        new("rzdevu_02e0_kbd/rzdevu_02e0_kbd.inf", 1980, "F3222D8A260DA3BFC87C19080C080D675E90D1F1EB7D1793A6148CBE3E90F5F3"),
        new("rzdevu_02e0_kbd/rzdev_02e0_kbd.cat", 11774, "75F200B0643A3F79E29646C2DD98A6FDA419F5D2608F691AE8C6AB587DD6A108"),
        new("rzdevu_02e0_kbd/RzDev_02e0.sys", 65872, "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787"),
        new("rzdevu_02e0_mou/rzdevu_02e0_mou.inf", 2032, "05AC3D6E2BC6B84B6F255CBEAD573156FC6F1C0CF7B44924D873572F944B649B"),
        new("rzdevu_02e0_mou/rzdev_02e0_mou.cat", 11774, "8C3A7AD5C68B9ACAFB36B35DD8E20A18B4B188E9594FFC7626A406FC75E92FC1"),
        new("rzdevu_02e0_mou/RzDev_02e0.sys", 65872, "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787"),
    });

    // Preserve the reviewed package guide's dependency staging and activation order.
    public static IReadOnlyList<string> StageOrder { get; } = Array.AsReadOnly(new[]
    {
        "rzdevu_02e0_vcon/rzdevu_02e0_vcon.inf", "rzcommonu/rzcommonu.inf",
        "rzdevu_02e0_dkm/rzdevu_02e0_dkm.inf", "rzdevu_02e0_kbd/rzdevu_02e0_kbd.inf",
        "rzdevu_02e0_mou/rzdevu_02e0_mou.inf",
    });

    public static IReadOnlyList<string> InstallOrder { get; } = Array.AsReadOnly(new[]
    {
        "rzdevu_02e0_dkm/rzdevu_02e0_dkm.inf", "rzdevu_02e0_vcon/rzdevu_02e0_vcon.inf",
        "rzdevu_02e0_kbd/rzdevu_02e0_kbd.inf", "rzdevu_02e0_mou/rzdevu_02e0_mou.inf",
        "rzcommonu/rzcommonu.inf",
    });

    public static Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken) =>
        AcquireVerifiedAsync(destination, RazerKeyboardDriverRecipes.Pid02E0, cancellationToken);

    internal static async Task<string> AcquireVerifiedAsync(string destination, RazerKeyboardDriverRecipe recipe,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        return await AcquireVerifiedAsync(destination, recipe, client,
            (root, token) => RazerDriverPackageTrust.Verify(root, recipe, token), timeout.Token).ConfigureAwait(false);
    }

    internal static Task<string> AcquireVerifiedAsync(
        string destination, HttpClient client, Action<string, CancellationToken> verifyTrust,
        CancellationToken cancellationToken) =>
        AcquireVerifiedAsync(destination, RazerKeyboardDriverRecipes.Pid02E0, client, verifyTrust, cancellationToken);

    internal static async Task<string> AcquireVerifiedAsync(
        string destination, RazerKeyboardDriverRecipe recipe, HttpClient client, Action<string, CancellationToken> verifyTrust,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(destination);
        EnsureNoReparsePoints(root);
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new IOException("Driver download requires an empty, protected destination directory.");
        }

        var archivePath = Path.Combine(root, recipe.ArchiveName);
        // CreateNew and FileShare.None prevent an existing archive from being replaced or reused.
        using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 16384, true))
        {
            await DownloadAsync(client, archive, recipe, cancellationToken).ConfigureAwait(false);
            archive.Position = 0;
            await VerifyHashAsync(archive, recipe.ArchiveLength, recipe.ArchiveSha256, cancellationToken).ConfigureAwait(false);
            archive.Position = 0;
            await ExtractVerifiedAsync(archive, root, recipe, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packageRoot = Path.Combine(root, recipe.PackageDirectory);
        verifyTrust(packageRoot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return packageRoot;
    }

    internal static Task DownloadAsync(HttpClient client, Stream destination, CancellationToken cancellationToken) =>
        DownloadAsync(client, destination, RazerKeyboardDriverRecipes.Pid02E0, cancellationToken);

    internal static async Task DownloadAsync(HttpClient client, Stream destination, RazerKeyboardDriverRecipe recipe,
        CancellationToken cancellationToken)
    {
        var uri = recipe.DownloadUri;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            ValidateDownloadUri(uri, recipe);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("OpenBlade/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("The driver download redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != recipe.ArchiveLength)
            {
                throw new InvalidDataException("The driver download size does not match the reviewed package.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await CopyExactAsync(input, destination, recipe.ArchiveLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException("The driver download redirected too many times.");
    }

    internal static void ValidateDownloadUri(Uri uri) => ValidateDownloadUri(uri, RazerKeyboardDriverRecipes.Pid02E0);

    internal static void ValidateDownloadUri(Uri uri, RazerKeyboardDriverRecipe recipe)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Equals(recipe.DownloadUri)))
        {
            throw new InvalidDataException("The driver download used an unapproved address.");
        }
    }

    internal static Task ExtractVerifiedAsync(Stream archiveStream, string destination,
        IReadOnlyList<PackageFile> expectedFiles, CancellationToken cancellationToken) =>
        ExtractVerifiedAsync(archiveStream, destination, RazerKeyboardDriverRecipes.Pid02E0, expectedFiles, cancellationToken);

    internal static Task ExtractVerifiedAsync(Stream archiveStream, string destination,
        RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken) =>
        ExtractVerifiedAsync(archiveStream, destination, recipe, recipe.Files, cancellationToken);

    internal static async Task ExtractVerifiedAsync(Stream archiveStream, string destination,
        RazerKeyboardDriverRecipe recipe, IReadOnlyList<PackageFile> expectedFiles, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(destination);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != expectedFiles.Count)
        {
            throw new InvalidDataException("The driver archive has an unexpected file count.");
        }

        var expected = expectedFiles.ToDictionary(file => recipe.PackageDirectory + "/" + file.RelativePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every name and length before creating any extracted file. Exact names reject
        // traversal, alternate separators, ADS, absolute paths, directories and extra content.
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!expected.TryGetValue(entry.FullName, out var file) || !seen.Add(entry.FullName) ||
                entry.Length != file.Length ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (unixType != 0 && unixType != 0x8000))
            {
                throw new InvalidDataException("The driver archive contains an unapproved file or link.");
            }
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            EnsureNoReparsePoints(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoReparsePoints(path);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 16384, true);
            await using var input = entry.Open();
            var file = expected[entry.FullName];
            await CopyExactAsync(input, output, file.Length, cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            await VerifyHashAsync(output, file.Length, file.Sha256, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task VerifyHashAsync(Stream stream, long expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        if (stream.Length != expectedLength || !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .Equals(expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The driver package failed its integrity check.");
        }
    }

    private static async Task CopyExactAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > expectedLength)
            {
                throw new InvalidDataException("The driver package exceeded its reviewed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total != expectedLength)
        {
            throw new InvalidDataException("The driver package download is incomplete.");
        }
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Driver package paths cannot contain links or reparse points.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
