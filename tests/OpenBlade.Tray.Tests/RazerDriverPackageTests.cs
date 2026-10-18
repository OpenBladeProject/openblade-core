using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerDriverPackageTests
{
    [Theory]
    [InlineData("http://github.com/package.zip")]
    [InlineData("https://github.com/other/package.zip")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/package.zip")]
    [InlineData("https://user@release-assets.githubusercontent.com/package.zip")]
    [InlineData("https://release-assets.githubusercontent.com:444/package.zip")]
    [InlineData("file:///C:/package.zip")]
    public void DownloadRejectsUnapprovedAddresses(string address)
    {
        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverPackage.ValidateDownloadUri(new Uri(address)));
    }

    [Fact]
    public async Task RedirectToUnapprovedHostIsRejectedBeforeSecondRequest()
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/package.zip") },
        });
        using var client = new HttpClient(handler);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.DownloadAsync(client, output, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task RedirectLoopIsBounded()
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = RazerKeyboardDriverPackage.DownloadUri },
        });
        using var client = new HttpClient(handler);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.DownloadAsync(client, output, CancellationToken.None));
        Assert.Equal(4, handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task DownloadRejectsWrongLengthWithoutContentLength(int delta)
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[RazerKeyboardDriverPackage.ArchiveLength + delta])),
        });
        using var client = new HttpClient(handler);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.DownloadAsync(client, output, CancellationToken.None));
        Assert.True(output.Length <= RazerKeyboardDriverPackage.ArchiveLength);
    }

    [Fact]
    public async Task AcquisitionRejectsArchiveHashBeforeExtractionOrTrust()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[RazerKeyboardDriverPackage.ArchiveLength]),
        });
        using var client = new HttpClient(handler);
        var trustCalls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.AcquireVerifiedAsync(
            directory.Path, client, (_, _) => trustCalls++, CancellationToken.None));
        Assert.Equal(0, trustCalls);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
    }

    [Fact]
    public async Task AcquisitionPreservesExistingContentAndMakesNoRequest()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, RazerKeyboardDriverPackage.ArchiveName);
        File.WriteAllText(path, "keep");
        using var handler = new FakeHandler(_ => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => RazerKeyboardDriverPackage.AcquireVerifiedAsync(
            directory.Path, client, (_, _) => { }, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CancellationBeforeAcquisitionMakesNoRequestOrFile()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new FakeHandler(_ => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RazerKeyboardDriverPackage.AcquireVerifiedAsync(
            directory.Path, client, (_, _) => { }, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("package/unknown.exe")]
    [InlineData("Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76/../escape.txt")]
    [InlineData("Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76/notice.txt:payload")]
    public async Task ExtractionRejectsUnapprovedNamesBeforeWriting(string name)
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((name, "notice", 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            archive, directory.Path, [Descriptor("notice.txt", "notice")], CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(0xA000 << 16)]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData(0x4000 << 16)]
    public async Task ExtractionRejectsLinksAndDirectories(int attributes)
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((EntryName("notice.txt"), "notice", attributes));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            archive, directory.Path, [Descriptor("notice.txt", "notice")], CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExtractionRejectsDuplicateEntries()
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((EntryName("notice.txt"), "notice", 0), (EntryName("notice.txt"), "notice", 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            archive, directory.Path, [Descriptor("notice.txt", "notice"), Descriptor("second.txt", "notice")], CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExtractionRejectsOversizedEntryBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((EntryName("notice.txt"), "notice too long", 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            archive, directory.Path, [Descriptor("notice.txt", "notice")], CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExtractionRejectsChangedBytesOfSameLength()
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((EntryName("notice.txt"), "NOTICE", 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            archive, directory.Path, [Descriptor("notice.txt", "notice")], CancellationToken.None));
    }

    [Fact]
    public async Task ExtractionPreservesNoticeAndNestedPackageBytes()
    {
        using var directory = new TemporaryDirectory();
        using var archive = MakeArchive((EntryName("THIRD-PARTY-NOTICE.txt"), "notice", 0), (EntryName("driver/file.inf"), "driver", 0));
        await RazerKeyboardDriverPackage.ExtractVerifiedAsync(archive, directory.Path,
            [Descriptor("THIRD-PARTY-NOTICE.txt", "notice"), Descriptor("driver/file.inf", "driver")], CancellationToken.None);
        Assert.Equal("notice", File.ReadAllText(System.IO.Path.Combine(directory.Path, EntryName("THIRD-PARTY-NOTICE.txt"))));
        Assert.Equal("driver", File.ReadAllText(System.IO.Path.Combine(directory.Path, EntryName("driver/file.inf"))));
    }

    private static string EntryName(string relative) => RazerKeyboardDriverPackage.PackageDirectory + "/" + relative;

    private static RazerKeyboardDriverPackage.PackageFile Descriptor(string path, string content) =>
        new(path, Encoding.UTF8.GetByteCount(content), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private static MemoryStream MakeArchive(params (string Name, string Content, int Attributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                entry.ExternalAttributes = item.Attributes;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenBlade-package-test-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
