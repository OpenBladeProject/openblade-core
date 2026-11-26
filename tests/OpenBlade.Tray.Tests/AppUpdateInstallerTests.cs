using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class AppUpdateInstallerTests : IDisposable
{
    private const string Tag = "v1.1.0";
    private const string ReleaseApi =
        "https://api.github.com/repos/OpenBladeProject/openblade-core/releases/tags/v1.1.0";
    private const string InstallerUrl =
        "https://github.com/OpenBladeProject/openblade-core/releases/download/v1.1.0/OpenBlade-Setup.exe";
    private const string ChecksumUrl =
        "https://github.com/OpenBladeProject/openblade-core/releases/download/v1.1.0/OpenBlade-Setup.exe.sha256";
    private static readonly byte[] InstallerBytes =
        Encoding.ASCII.GetBytes("MZ fake OpenBlade bundle payload for update tests");
    private static readonly ReleaseUpdateInfo Release = new(
        new Version(1, 1, 0, 0),
        Tag,
        new Uri("https://github.com/OpenBladeProject/openblade-core/releases/tag/v1.1.0"));
    private readonly string scratch = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.AppUpdateInstallerTests",
        Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(scratch, "Updates");

    public void Dispose()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task VerifiedDownloadIsOpenedAndHeldUntilDisposed()
    {
        var handler = new RoutingHandler(Routes());
        var launcher = new RecordingLauncher();
        var progress = new List<AppUpdateInstallStage>();
        using var installer = Create(handler, launcher);

        await installer.InstallAsync(
            Release,
            new InlineProgress(report => progress.Add(report.Stage)),
            CancellationToken.None);

        string expectedPath = Path.Combine(Root, "1.1.0.0", "OpenBlade-Setup.exe");
        Assert.Equal(expectedPath, Assert.Single(launcher.Paths));
        Assert.Equal(InstallerBytes, File.ReadAllBytes(expectedPath));
        Assert.False(File.Exists(expectedPath + ".partial"));
        Assert.Equal([ReleaseApi, ChecksumUrl, InstallerUrl], handler.Requests);
        Assert.Contains(AppUpdateInstallStage.Downloading, progress);
        Assert.Equal(AppUpdateInstallStage.Launching, progress[^1]);
        // The verified bytes stay pinned while the installer runs.
        Assert.Throws<IOException>(() => new FileStream(expectedPath, FileMode.Open, FileAccess.Write).Dispose());
        Assert.Throws<IOException>(() => File.Delete(expectedPath));

        installer.Dispose();
        File.Delete(expectedPath);
    }

    [Theory]
    [InlineData("{0} *OpenBlade-Setup.exe\r\n")]
    [InlineData("{0}  OpenBlade-Setup.exe\n")]
    [InlineData("﻿{0} *OpenBlade-Setup.exe\r\n")]
    public async Task ChecksumFilesInEveryGateAcceptedFormAreRead(string format)
    {
        byte[] checksum = Encoding.UTF8.GetBytes(string.Format(format, Sha256(InstallerBytes)));
        var launcher = new RecordingLauncher();
        using var installer = Create(new RoutingHandler(Routes(checksumFile: checksum)), launcher);

        await installer.InstallAsync(Release, null, CancellationToken.None);

        Assert.Single(launcher.Paths);
    }

    [Fact]
    public async Task ChecksumMismatchStopsBeforeLaunchAndRemovesTheDownload()
    {
        byte[] wrongChecksum = Encoding.ASCII.GetBytes(new string('a', 64) + " *OpenBlade-Setup.exe\r\n");
        var handler = new RoutingHandler(Routes(checksumFile: wrongChecksum, digest: null));
        var launcher = new RecordingLauncher();
        using var installer = Create(handler, launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.VerificationFailed, failure.Failure);
        // The installer was downloaded, so the hash comparison (not a size check) refused it.
        Assert.Contains(InstallerUrl, handler.Requests);
        Assert.Empty(launcher.Paths);
        Assert.Empty(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NonUtf8ChecksumFileIsAVerificationFailure()
    {
        // The UTF-16 byte-order mark (FF FE) is not valid UTF-8.
        byte[] utf16 =
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Sha256(InstallerBytes) + " *OpenBlade-Setup.exe")];
        var launcher = new RecordingLauncher();
        using var installer = Create(new RoutingHandler(Routes(checksumFile: utf16)), launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.VerificationFailed, failure.Failure);
        Assert.Empty(launcher.Paths);
    }

    [Fact]
    public async Task DisagreeingGitHubDigestStopsBeforeDownloadingTheInstaller()
    {
        var handler = new RoutingHandler(Routes(digest: "sha256:" + new string('b', 64)));
        var launcher = new RecordingLauncher();
        using var installer = Create(handler, launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.VerificationFailed, failure.Failure);
        Assert.DoesNotContain(InstallerUrl, handler.Requests);
        Assert.Empty(launcher.Paths);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task DownloadOfTheWrongLengthIsRejectedWhileStreaming(int extraBytes)
    {
        // Without Content-Length, only the streaming and final length checks apply.
        byte[] body = extraBytes > 0
            ? [.. InstallerBytes, 0x00]
            : InstallerBytes[..^1];
        var launcher = new RecordingLauncher();
        using var installer = Create(new RoutingHandler(Routes(installerBody: () => new NonSeekableStream(body))), launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.VerificationFailed, failure.Failure);
        Assert.Empty(launcher.Paths);
        Assert.Empty(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StalledDownloadFailsAndRemovesThePartialFile()
    {
        var launcher = new RecordingLauncher();
        using var installer = new GitHubAppUpdateInstaller(
            new HttpClient(new RoutingHandler(Routes(installerBody: () => new StalledStream()))),
            launcher,
            Root,
            TimeSpan.FromMilliseconds(200));

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AppUpdateInstallFailure.Unavailable, failure.Failure);
        Assert.Empty(launcher.Paths);
        Assert.Empty(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CallerCancellationIsRethrownAndRemovesThePartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        var launcher = new RecordingLauncher();
        using var installer = Create(
            new RoutingHandler(Routes(installerBody: () => new StalledStream(() => cancellation.Cancel()))),
            launcher);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(Release, null, cancellation.Token));

        Assert.Empty(launcher.Paths);
        Assert.Empty(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("missing-checksum")]
    [InlineData("http-download")]
    [InlineData("foreign-host")]
    [InlineData("other-repository")]
    [InlineData("former-owner")]
    [InlineData("other-tag")]
    [InlineData("draft")]
    [InlineData("string-size")]
    public async Task UnexpectedReleaseMetadataIsRejected(string variant)
    {
        string metadata = variant switch
        {
            "missing-checksum" => Metadata(includeChecksum: false),
            "http-download" => Metadata(installerUrl: InstallerUrl.Replace("https://", "http://", StringComparison.Ordinal)),
            "foreign-host" => Metadata(installerUrl: InstallerUrl.Replace("github.com", "example.com", StringComparison.Ordinal)),
            "other-repository" => Metadata(installerUrl: InstallerUrl.Replace("OpenBladeProject/openblade-core", "someone/openblade-core", StringComparison.Ordinal)),
            "former-owner" => Metadata(installerUrl: InstallerUrl.Replace("OpenBladeProject", "OSSBlade", StringComparison.Ordinal)),
            "other-tag" => Metadata(tag: "v9.9.9"),
            "draft" => Metadata(draft: true),
            "string-size" => Metadata(installerSize: "\"big\""),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        Dictionary<string, Func<HttpResponseMessage>> routes = Routes();
        routes[ReleaseApi] = () => Json(metadata);
        var handler = new RoutingHandler(routes);
        var launcher = new RecordingLauncher();
        using var installer = Create(handler, launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.Unavailable, failure.Failure);
        Assert.Equal([ReleaseApi], handler.Requests);
        Assert.Empty(launcher.Paths);
    }

    [Fact]
    public async Task LaunchFailureReleasesTheVerifiedFile()
    {
        var launcher = new RecordingLauncher { Failure = new InvalidOperationException("blocked") };
        using var installer = Create(new RoutingHandler(Routes()), launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.LaunchFailed, failure.Failure);
        File.Delete(Path.Combine(Root, "1.1.0.0", "OpenBlade-Setup.exe"));
    }

    [Fact]
    public async Task JunctionedDownloadFolderIsNotLaunched()
    {
        string target = Path.Combine(scratch, "elsewhere");
        Directory.CreateDirectory(target);
        CreateJunction(Root, target);
        var launcher = new RecordingLauncher();
        using var installer = Create(new RoutingHandler(Routes()), launcher);

        AppUpdateInstallException failure = await Assert.ThrowsAsync<AppUpdateInstallException>(
            () => installer.InstallAsync(Release, null, CancellationToken.None));

        Assert.Equal(AppUpdateInstallFailure.LaunchFailed, failure.Failure);
        Assert.Empty(launcher.Paths);
        Assert.Empty(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
        Directory.Delete(Root);
    }

    [Fact]
    public async Task NewDownloadReplacesEarlierReleaseFolders()
    {
        string stale = Path.Combine(Root, "1.0.0.0");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "OpenBlade-Setup.exe"), "old");
        using var installer = Create(new RoutingHandler(Routes()), new RecordingLauncher());

        await installer.InstallAsync(Release, null, CancellationToken.None);

        Assert.False(Directory.Exists(stale));
    }

    private GitHubAppUpdateInstaller Create(RoutingHandler handler, IAppUpdateLauncher launcher) =>
        new(new HttpClient(handler), launcher, Root);

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Dictionary<string, Func<HttpResponseMessage>> Routes(
        byte[]? checksumFile = null,
        string? digest = "",
        Func<Stream>? installerBody = null)
    {
        // Published checksum files are CRLF, 87 bytes for a 19-character file name.
        byte[] checksum = checksumFile
            ?? Encoding.ASCII.GetBytes(Sha256(InstallerBytes) + " *OpenBlade-Setup.exe\r\n");
        return new(StringComparer.Ordinal)
        {
            [ReleaseApi] = () => Json(Metadata(checksumSize: checksum.Length, digest: digest)),
            [ChecksumUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(checksum),
            },
            [InstallerUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(installerBody?.Invoke() ?? new MemoryStream(InstallerBytes)),
            },
        };
    }

    private static string Metadata(
        string tag = Tag,
        bool draft = false,
        bool includeChecksum = true,
        string installerUrl = InstallerUrl,
        string? digest = "",
        int checksumSize = 87,
        string? installerSize = null)
    {
        var assets = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "OpenBlade-Setup.exe",
                ["state"] = "uploaded",
                ["size"] = installerSize is null
                    ? InstallerBytes.Length
                    : JsonDocument.Parse(installerSize).RootElement,
                ["digest"] = digest == "" ? "sha256:" + Sha256(InstallerBytes) : digest,
                ["browser_download_url"] = installerUrl,
            },
        };
        if (includeChecksum)
        {
            assets.Add(new Dictionary<string, object?>
            {
                ["name"] = "OpenBlade-Setup.exe.sha256",
                ["state"] = "uploaded",
                ["size"] = checksumSize,
                ["browser_download_url"] = ChecksumUrl,
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = false,
            ["assets"] = assets,
        });
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RoutingHandler(Dictionary<string, Func<HttpResponseMessage>> routes)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            HttpResponseMessage response = routes.TryGetValue(uri, out Func<HttpResponseMessage>? route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingLauncher : IAppUpdateLauncher
    {
        public List<string> Paths { get; } = [];

        public Exception? Failure { get; init; }

        public void Start(string installerPath)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Paths.Add(installerPath);
        }
    }

    private sealed class InlineProgress(Action<AppUpdateInstallProgress> report)
        : IProgress<AppUpdateInstallProgress>
    {
        public void Report(AppUpdateInstallProgress value) => report(value);
    }

    /// <summary>Hides the length, so the response carries no Content-Length.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Delivers one byte, then never delivers more until canceled.</summary>
    private sealed class StalledStream(Action? onStall = null) : Stream
    {
        private bool sentFirstByte;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sentFirstByte)
            {
                sentFirstByte = true;
                buffer.Span[0] = InstallerBytes[0];
                return 1;
            }

            onStall?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
