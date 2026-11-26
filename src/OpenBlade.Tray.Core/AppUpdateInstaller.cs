using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum AppUpdateInstallStage
{
    Downloading,
    Verifying,
    Launching,
}

public sealed record AppUpdateInstallProgress(
    AppUpdateInstallStage Stage,
    long BytesReceived,
    long TotalBytes);

public enum AppUpdateInstallFailure
{
    Unavailable,
    VerificationFailed,
    LaunchFailed,
}

public sealed class AppUpdateInstallException : Exception
{
    public AppUpdateInstallException(
        AppUpdateInstallFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public AppUpdateInstallFailure Failure { get; }
}

public interface IAppUpdateInstaller : IDisposable
{
    /// <summary>
    /// Downloads the exact release's installer, verifies its published size and
    /// SHA-256, and opens it. Completion means the installer process started.
    /// </summary>
    Task InstallAsync(
        ReleaseUpdateInfo release,
        IProgress<AppUpdateInstallProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IAppUpdateLauncher
{
    void Start(string installerPath);
}

public sealed class ShellAppUpdateLauncher : IAppUpdateLauncher
{
    public void Start(string installerPath)
    {
        // The tray offers in-app installs only at normal integrity (see MainWindow),
        // so the bundle starts unelevated and requests elevation itself. Its success
        // page then relaunches the tray and Session Agent at normal integrity, as an
        // ordinary double-click would.
        using Process? process = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        });
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the OpenBlade installer.");
        }
    }
}

/// <summary>
/// Installs a published OpenBlade release from its GitHub release assets.
/// Release installers are not Authenticode-signed, so the trust anchor is the
/// release's published SHA-256 over HTTPS: the <c>.sha256</c> asset and, when
/// GitHub reports one, the asset digest must both match the downloaded bytes.
/// This is the same evidence a manual download from the release page offers.
/// </summary>
public sealed class GitHubAppUpdateInstaller : IAppUpdateInstaller
{
    public const string InstallerAssetName = "OpenBlade-Setup.exe";
    public const string ChecksumAssetName = InstallerAssetName + ".sha256";
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 256 * 1024;
    private const int MaximumChecksumBytes = 1024;
    private const string ReleaseTagEndpoint =
        GitHubReleaseUpdateClient.RepositoryApi + "/releases/tags/";
    private const string ReleaseDownloadPathPrefix =
        "/OpenBladeProject/openblade-core/releases/download/";
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    // A connection that delivers nothing for this long has stalled.
    internal static readonly TimeSpan DefaultDownloadStallTimeout = TimeSpan.FromSeconds(60);
    // Accepts the same forms as the release gate: sha256sum text or binary mode.
    private static readonly Regex ChecksumLine = new(
        @"^(?<hash>[0-9A-Fa-f]{64})\s+\*?OpenBlade-Setup\.exe$",
        RegexOptions.CultureInvariant);
    private static readonly SearchValues<char> HexDigits =
        SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private readonly HttpClient httpClient;
    private readonly IAppUpdateLauncher launcher;
    private readonly string downloadRoot;
    private readonly TimeSpan stallTimeout;
    private readonly object sync = new();
    private FileStream? heldInstaller;
    private bool disposed;

    public GitHubAppUpdateInstaller()
        : this(SharedHttpClient, new ShellAppUpdateLauncher(), DefaultDownloadRoot())
    {
    }

    public GitHubAppUpdateInstaller(
        HttpClient httpClient,
        IAppUpdateLauncher launcher,
        string downloadRoot)
        : this(httpClient, launcher, downloadRoot, DefaultDownloadStallTimeout)
    {
    }

    internal GitHubAppUpdateInstaller(
        HttpClient httpClient,
        IAppUpdateLauncher launcher,
        string downloadRoot,
        TimeSpan stallTimeout)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stallTimeout, TimeSpan.Zero);
        this.downloadRoot = Path.GetFullPath(downloadRoot);
        this.stallTimeout = stallTimeout;
    }

    public static string DefaultDownloadRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade",
        "Updates");

    public async Task InstallAsync(
        ReleaseUpdateInfo release,
        IProgress<AppUpdateInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        try
        {
            ReleaseInstallerAssets assets = await ReadAssetsAsync(release, cancellationToken)
                .ConfigureAwait(false);
            string expectedHash = await ReadPublishedChecksumAsync(assets, cancellationToken)
                .ConfigureAwait(false);
            if (assets.InstallerDigest is string digest
                && !string.Equals(digest, expectedHash, StringComparison.Ordinal))
            {
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.VerificationFailed,
                    "The release checksum file and GitHub asset digest disagree.");
            }

            string directory = PrepareDirectory(release.Version);
            string installerPath = Path.Combine(directory, InstallerAssetName);
            string partialPath = installerPath + ".partial";
            await DownloadAsync(assets, partialPath, expectedHash, progress, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new(AppUpdateInstallStage.Verifying, assets.InstallerSize, assets.InstallerSize));
            FileStream held = HoldVerifiedInstaller(partialPath, installerPath, assets.InstallerSize, expectedHash);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(AppUpdateInstallStage.Launching, assets.InstallerSize, assets.InstallerSize));
                launcher.Start(installerPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                held.Dispose();
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.LaunchFailed,
                    "The OpenBlade installer could not be started.",
                    exception);
            }
            catch
            {
                held.Dispose();
                throw;
            }

            // Keep the verified file open without write or delete sharing while the
            // bundle runs. With no reparse point on its folder chain, the folders
            // can't be renamed either, so the launch path keeps naming these bytes.
            ReplaceHeldInstaller(held);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppUpdateInstallException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or OperationCanceledException
                or NotSupportedException)
        {
            throw new AppUpdateInstallException(
                AppUpdateInstallFailure.Unavailable,
                "The OpenBlade update could not be downloaded.",
                exception);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            heldInstaller?.Dispose();
            heldInstaller = null;
        }
    }

    private async Task<ReleaseInstallerAssets> ReadAssetsAsync(
        ReleaseUpdateInfo release,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        var endpoint = new Uri(ReleaseTagEndpoint + Uri.EscapeDataString(release.TagName));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("OpenBlade");
        request.Headers.Add("X-GitHub-Api-Version", GitHubReleaseUpdateClient.ApiVersion);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumMetadataBytes, timeout.Token)
            .ConfigureAwait(false);
        await using Stream content = await response.Content.ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: timeout.Token).ConfigureAwait(false);
        return ParseAssets(document.RootElement, release);
    }

    internal static ReleaseInstallerAssets ParseAssets(JsonElement root, ReleaseUpdateInfo release)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out JsonElement tag)
            || tag.ValueKind != JsonValueKind.String
            || !string.Equals(tag.GetString(), release.TagName, StringComparison.Ordinal)
            || !root.TryGetProperty("draft", out JsonElement draft)
            || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub returned release metadata for a different or unpublished release.");
        }

        JsonElement? installer = null;
        JsonElement? checksum = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? assetName = name.GetString();
            if (string.Equals(assetName, InstallerAssetName, StringComparison.Ordinal))
            {
                installer = installer is null ? asset : throw DuplicateAsset(InstallerAssetName);
            }
            else if (string.Equals(assetName, ChecksumAssetName, StringComparison.Ordinal))
            {
                checksum = checksum is null ? asset : throw DuplicateAsset(ChecksumAssetName);
            }
        }

        if (installer is not JsonElement installerAsset || checksum is not JsonElement checksumAsset)
        {
            throw new InvalidDataException("The release does not publish an installer and its checksum.");
        }

        long installerSize = RequiredSize(installerAsset, MaximumInstallerBytes);
        long checksumSize = RequiredSize(checksumAsset, MaximumChecksumBytes);
        return new ReleaseInstallerAssets(
            RequiredDownloadUri(installerAsset),
            installerSize,
            OptionalSha256Digest(installerAsset),
            RequiredDownloadUri(checksumAsset),
            checksumSize);
    }

    private async Task<string> ReadPublishedChecksumAsync(
        ReleaseInstallerAssets assets,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        using HttpResponseMessage response = await SendDownloadAsync(assets.ChecksumUri, timeout.Token)
            .ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(MaximumChecksumBytes, timeout.Token)
            .ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (bytes.LongLength != assets.ChecksumSize)
        {
            throw new AppUpdateInstallException(
                AppUpdateInstallFailure.VerificationFailed,
                "The release checksum file does not match its published size.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimStart('﻿')
                .Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw new AppUpdateInstallException(
                AppUpdateInstallFailure.VerificationFailed,
                "The release checksum file is not UTF-8 text.",
                exception);
        }

        Match match = ChecksumLine.Match(text);
        if (!match.Success)
        {
            throw new AppUpdateInstallException(
                AppUpdateInstallFailure.VerificationFailed,
                "The release checksum file is not in the expected format.");
        }

        return match.Groups["hash"].Value.ToLowerInvariant();
    }

    private async Task DownloadAsync(
        ReleaseInstallerAssets assets,
        string partialPath,
        string expectedHash,
        IProgress<AppUpdateInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        // Restarted before every read, so a connection that stops delivering
        // fails within a minute instead of waiting for the overall limit.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        bool completed = false;
        try
        {
            stall.CancelAfter(stallTimeout);
            using HttpResponseMessage response = await SendDownloadAsync(assets.InstallerUri, stall.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long length && length != assets.InstallerSize)
            {
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.VerificationFailed,
                    "The installer download does not match its published size.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(stall.Token)
                .ConfigureAwait(false);
            await using var target = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long received = 0;
            int lastPercent = -1;
            progress?.Report(new(AppUpdateInstallStage.Downloading, 0, assets.InstallerSize));
            while (true)
            {
                stall.CancelAfter(stallTimeout);
                int read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > assets.InstallerSize)
                {
                    throw new AppUpdateInstallException(
                        AppUpdateInstallFailure.VerificationFailed,
                        "The installer download is larger than its published size.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                int percent = (int)(received * 100 / assets.InstallerSize);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(new(AppUpdateInstallStage.Downloading, received, assets.InstallerSize));
                }
            }

            await target.FlushAsync(timeout.Token).ConfigureAwait(false);
            if (received != assets.InstallerSize
                || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedHash, StringComparison.Ordinal))
            {
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.VerificationFailed,
                    "The installer download does not match its published checksum.");
            }

            completed = true;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && stall.IsCancellationRequested)
        {
            throw new AppUpdateInstallException(
                AppUpdateInstallFailure.Unavailable,
                timeout.IsCancellationRequested
                    ? "The OpenBlade update download timed out."
                    : "The OpenBlade update download stalled.",
                exception);
        }
        finally
        {
            if (!completed)
            {
                TryDelete(partialPath);
            }
        }
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd("OpenBlade");
        HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            // Redirects to GitHub's asset storage are expected; an HTTPS downgrade is not.
            if (response.RequestMessage?.RequestUri is not Uri final
                || !string.Equals(final.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update download did not stay on HTTPS.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private string PrepareDirectory(Version version)
    {
        string directory = Path.Combine(
            downloadRoot,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}.{Math.Max(version.Revision, 0)}"));
        lock (sync)
        {
            // A previously launched installer is replaced by this download.
            heldInstaller?.Dispose();
            heldInstaller = null;
        }

        Directory.CreateDirectory(downloadRoot);
        // Earlier downloads are no longer needed once a newer release is chosen.
        foreach (string stale in Directory.EnumerateDirectories(downloadRoot))
        {
            if (!string.Equals(Path.GetFullPath(stale), directory, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(stale);
            }
        }

        // Start from an empty directory so nothing beside the installer can be
        // loaded from its working directory.
        TryDeleteDirectory(directory);
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("The update download folder could not be cleared.");
        }

        return directory;
    }

    private static FileStream HoldVerifiedInstaller(
        string partialPath,
        string installerPath,
        long expectedSize,
        string expectedHash)
    {
        FileStream held;
        try
        {
            File.Move(partialPath, installerPath, overwrite: true);
            held = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            TryDelete(partialPath);
            TryDelete(installerPath);
            throw;
        }

        try
        {
            // The pin stops the file and its folders being renamed, but a junction
            // can be re-pointed while it is held. A folder chain without reparse
            // points keeps the launch path naming the pinned, verified file.
            RequireNoReparsePointAbove(installerPath);
            // Re-check the file at its final path through the handle that pins it.
            if (held.Length != expectedSize
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(held)), expectedHash, StringComparison.Ordinal))
            {
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.VerificationFailed,
                    "The saved installer does not match its published checksum.");
            }

            return held;
        }
        catch
        {
            held.Dispose();
            TryDelete(installerPath);
            throw;
        }
    }

    private static void RequireNoReparsePointAbove(string path)
    {
        for (DirectoryInfo? directory = new FileInfo(path).Directory;
            directory is not null;
            directory = directory.Parent)
        {
            directory.Refresh();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AppUpdateInstallException(
                    AppUpdateInstallFailure.LaunchFailed,
                    "The update download folder is a link, so the installer was not opened.");
            }
        }
    }

    private void ReplaceHeldInstaller(FileStream held)
    {
        lock (sync)
        {
            if (disposed)
            {
                held.Dispose();
                return;
            }

            heldInstaller?.Dispose();
            heldInstaller = held;
        }
    }

    private static long RequiredSize(JsonElement asset, long maximum)
    {
        if (!asset.TryGetProperty("state", out JsonElement state)
            || state.ValueKind != JsonValueKind.String
            || !string.Equals(state.GetString(), "uploaded", StringComparison.Ordinal)
            || !asset.TryGetProperty("size", out JsonElement size)
            || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out long bytes)
            || bytes <= 0
            || bytes > maximum)
        {
            throw new InvalidDataException("A release asset has an invalid size or state.");
        }

        return bytes;
    }

    private static Uri RequiredDownloadUri(JsonElement asset)
    {
        if (!asset.TryGetProperty("browser_download_url", out JsonElement url)
            || url.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(ReleaseDownloadPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A release asset has an unexpected download location.");
        }

        return uri;
    }

    private static string? OptionalSha256Digest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out JsonElement digest)
            || digest.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string prefix = "sha256:";
        string? value = digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
        if (value is null
            || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value.Length != prefix.Length + 64
            || value.AsSpan(prefix.Length).ContainsAnyExcept(HexDigits))
        {
            throw new InvalidDataException("The installer asset has an invalid digest.");
        }

        return value[prefix.Length..].ToLowerInvariant();
    }

    private static InvalidDataException DuplicateAsset(string name) =>
        new(string.Format(CultureInfo.InvariantCulture, "The release publishes more than one {0}.", name));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.AppUpdateInstaller", "DeleteDownload", exception);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.AppUpdateInstaller", "DeleteDownloadDirectory", exception);
        }
    }

    private static HttpClient CreateSharedHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

internal sealed record ReleaseInstallerAssets(
    Uri InstallerUri,
    long InstallerSize,
    string? InstallerDigest,
    Uri ChecksumUri,
    long ChecksumSize);
