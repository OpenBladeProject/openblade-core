using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerDriverPackageTests
{
    [Theory]
    [InlineData("Blade 16 - RZ09-0581", "02E0")]
    [InlineData("Blade 16 - RZ09-0528", "02C6")]
    [InlineData("Blade 16 - RZ09-0483", "029F")]
    [InlineData("Blade 14 - RZ09-0482", "029D")]
    [InlineData("Blade 14 - RZ09-0530", "02C5")]
    [InlineData("Blade 15 Base Model (Early 2020) - RZ09-0328", "0255")]
    [InlineData("Blade 18 - RZ09-0484", "02A0")]
    public void RecipeRequiresBothExactHostIdentities(string productName, string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.FindHost("Razer", productName, "Razer", productName);
        Assert.NotNull(recipe);
        Assert.Equal(productId, recipe.ProductId);
        Assert.Same(recipe, RazerKeyboardDriverRecipes.FindById(recipe.Id));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Other", productName, "Razer", productName));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", productName, "Other", productName));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", productName, "Razer", productName + " variant"));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", productName + " variant", "Razer", productName));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", productName, "Razer", null));
    }

    [Fact]
    public void RecipesDoNotAdmitUnknownProfilesOrMixedHosts()
    {
        Assert.Equal(7, RazerKeyboardDriverRecipes.All.Count);
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", "Blade 14 - RZ09-0000", "Razer", "Blade 14 - RZ09-0000"));
        Assert.Null(RazerKeyboardDriverRecipes.FindHost("Razer", "Blade 16 - RZ09-0528", "Razer", "Blade 16 - RZ09-0581"));
        Assert.Null(RazerKeyboardDriverRecipes.FindById(null));
        Assert.Null(RazerKeyboardDriverRecipes.FindById("02C6"));
    }

    [Fact]
    public void RecipesKeepModelPackagesAndSignersSeparate()
    {
        var original = RazerKeyboardDriverRecipes.Pid02E0;
        var added = RazerKeyboardDriverRecipes.Pid02C6;
        Assert.Equal(RazerKeyboardDriverPackage.DownloadUri, original.DownloadUri);
        Assert.Equal(RazerKeyboardDriverPackage.Files, original.Files);
        Assert.Equal(RazerKeyboardDriverPackage.StageOrder, original.StageOrder);
        Assert.Equal(RazerKeyboardDriverPackage.InstallOrder, original.InstallOrder);
        Assert.Equal(227351, added.ArchiveLength);
        Assert.Equal("533F8464A6A3ED328ABDADAC2DF2619859A6151FC17E775F46FCE310A682F8C5", added.ArchiveSha256);
        Assert.Equal("1F4DB7FDCCB0E6292AA2E70436EF68466E8674EA", added.DeviceCatalogSignerThumbprint);
        Assert.NotEqual(original.DeviceCatalogSignerThumbprint, added.DeviceCatalogSignerThumbprint);
        Assert.Equal(original.CommonCatalogSignerThumbprint, added.CommonCatalogSignerThumbprint);
        foreach (var recipe in RazerKeyboardDriverRecipes.All)
        {
            Assert.Equal(recipe.CabinetFiles is not null ? 15 : recipe.IsZip ? 19 : 16, recipe.Files.Count);
            if (recipe.CabinetFiles is null)
            {
                Assert.Contains(recipe.Files, file => file.RelativePath == "THIRD-PARTY-NOTICE.txt");
            }
            Assert.Equal(5, recipe.StageOrder.Count);
            Assert.Equal(5, recipe.InstallOrder.Count);
            Assert.Equal(recipe.StageOrder.Order(), recipe.InstallOrder.Order());
            foreach (string inf in recipe.StageOrder)
            {
                Assert.Contains(recipe.Files, file => file.RelativePath == inf);
                Assert.True(inf == recipe.InfFor("common") || inf.Contains(recipe.ProductId.ToLowerInvariant(), StringComparison.Ordinal));
            }
            Assert.All(recipe.Files.Where(file => file.RelativePath.EndsWith(".sys", StringComparison.Ordinal)), file =>
                Assert.Equal(file.RelativePath.EndsWith("RzCommon.sys", StringComparison.Ordinal) ? recipe.CommonHash : recipe.DeviceHash, file.Sha256));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)recipe.StageOrder)[0] = "other.inf");
        }
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task RecipeRejectsOtherModelsDownloadBeforeSecondRequest(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var other = RazerKeyboardDriverRecipes.All.First(value => value.ProductId != productId);
        RazerKeyboardDriverPackage.ValidateDownloadUri(recipe.DownloadUri, recipe);
        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverPackage.ValidateDownloadUri(other.DownloadUri, recipe));
        using var handler = new FakeHandler(request =>
        {
            Assert.Equal(recipe.DownloadUri, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = other.DownloadUri } };
        });
        using var client = new HttpClient(handler);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.DownloadAsync(client, output, recipe, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task RecipeAcquisitionRejectsChangedArchiveBeforeTrust(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        using var directory = new TemporaryDirectory();
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[recipe.ArchiveLength]),
        });
        using var client = new HttpClient(handler);
        int trustCalls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.AcquireVerifiedAsync(
            directory.Path, recipe, client, (_, _) => trustCalls++, CancellationToken.None));
        Assert.Equal(0, trustCalls);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path));
        Assert.Equal(recipe.ArchiveName, System.IO.Path.GetFileName(Assert.Single(Directory.EnumerateFiles(directory.Path))));
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public async Task RecipeExtractionUsesOnlySelectedArchiveRoot(string productId)
    {
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var other = RazerKeyboardDriverRecipes.All.First(value => value.ProductId != productId);
        using var directory = new TemporaryDirectory();
        using var wrong = MakeArchive((other.PackageDirectory + "/notice.txt", "notice", 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverPackage.ExtractVerifiedAsync(
            wrong, directory.Path, recipe, [Descriptor("notice.txt", "notice")], CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        using var correct = MakeArchive((recipe.PackageDirectory + "/notice.txt", "notice", 0));
        await RazerKeyboardDriverPackage.ExtractVerifiedAsync(correct, directory.Path, recipe,
            [Descriptor("notice.txt", "notice")], CancellationToken.None);
        Assert.Equal("notice", File.ReadAllText(System.IO.Path.Combine(directory.Path, recipe.PackageDirectory, "notice.txt")));
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void CatalogPublisherRejectsOtherModelsSignerAndWrongPublisher(string productId)
    {
        const string publisher = "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var recipe = RazerKeyboardDriverRecipes.All.Single(value => value.ProductId == productId);
        var other = RazerKeyboardDriverRecipes.All.First(value =>
            value.DeviceCatalogSignerThumbprint != recipe.DeviceCatalogSignerThumbprint);
        string catalog = $"rzdev_{productId.ToLowerInvariant()}_kbd.cat";
        RazerDriverPackageTrust.VerifyCatalogPublisher(recipe, catalog, publisher, recipe.DeviceCatalogSignerThumbprint);
        RazerDriverPackageTrust.VerifyCatalogPublisher(recipe, "rzcommon.cat", publisher, recipe.CommonCatalogSignerThumbprint);
        Assert.Throws<CryptographicException>(() => RazerDriverPackageTrust.VerifyCatalogPublisher(recipe,
            catalog, publisher, other.DeviceCatalogSignerThumbprint));
        Assert.Throws<CryptographicException>(() => RazerDriverPackageTrust.VerifyCatalogPublisher(recipe,
            catalog, "Other publisher", recipe.DeviceCatalogSignerThumbprint));
        Assert.Throws<CryptographicException>(() => RazerDriverPackageTrust.VerifyCatalogPublisher(recipe,
            "rzcommon.cat", publisher, new string('0', 40)));
    }

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
