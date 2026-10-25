using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardDriverVendorPackageTests
{
    // Independently encoded with Python's raw LZMA1 implementation, not this decoder.
    private const string Small = "XQAAgAAAJ5wIpw9Hj0ls0ogoKCVzr/EsQCcfM/SG1PvBMIVIm4gw9II///uVwAA=";
    private const string Oversized = "XQAAgAAAIO/7v/6jsV7l+D+yqiZV+GhwQXAVD439HkwbikK3GfRpGHGuZiOKik0vow3Zf6bjjCMRU+BZGMV1iuJ3+LaUfwxqwN50SWTi6VxTsgTY90QMq18NbUbp5cN2iLeWV6y2TeFpHW/7S4gQbELLiD9cAI/QTq8mKJRxHz2PJOFwnqcjX+woy4XRlZiKfiqR8id19xnABphNmP3Yr9WQD8QlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS9Ll+qDz5LZkKQEw7/EJP4cXhZ+AvN/5UoRg+p/Hze+5owLlbAj4Xzg4HAZcQlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS8ZCfdylj//5oIAAA=";
    private const string Content = "OpenBlade reviewed driver block\n";

    [Fact]
    public void FixedBlockDecodesIndependentKnownAnswer()
    {
        using MemoryStream input = Block(Small);
        using Stream decoded = RazerKeyboardDriverVendorPackage.DecodeBlock(input, new("test.inf", 4, (int)input.Length - 4, true));
        Assert.Equal(Content, new StreamReader(decoded).ReadToEnd());
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(4, 5)]
    [InlineData(4, 2097153)]
    [InlineData(1000, 30)]
    public void InvalidSpansAreRejectedBeforeDecoding(int offset, int length)
    {
        using MemoryStream input = Block(Small);
        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverVendorPackage.DecodeBlock(input, new("test.inf", offset, length, true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void ChangedHeaderOrDictionaryIsRejected(int changedByte)
    {
        using MemoryStream input = Block(Small);
        byte[] data = input.ToArray();
        data[changedByte] ^= 1;
        using var changed = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverVendorPackage.DecodeBlock(changed, new("test.inf", 4, data.Length - 4, true)));
    }

    [Fact]
    public void ExpansionIsBoundedAndCancellationIsObserved()
    {
        using MemoryStream input = Block(Oversized);
        Assert.Throws<InvalidDataException>(() => RazerKeyboardDriverVendorPackage.DecodeBlock(input, new("test.inf", 4, (int)input.Length - 4, true)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using MemoryStream small = Block(Small);
        Assert.Throws<OperationCanceledException>(() => RazerKeyboardDriverVendorPackage.DecodeBlock(small,
            new("test.inf", 4, (int)small.Length - 4, true), cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractedBytesMustMatchTheSelectedFilePin(bool changeHash)
    {
        using var directory = new TemporaryDirectory();
        using MemoryStream archive = Archive();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content)));
        var recipe = Recipe("drivers/test.inf", changeHash ? new string('0', 64) : hash, (int)archive.Length - 32);
        if (changeHash)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverVendorPackage.ExtractVerifiedAsync(
                archive, directory.Path, recipe, default));
        }
        else
        {
            await RazerKeyboardDriverVendorPackage.ExtractVerifiedAsync(archive, directory.Path, recipe, default);
            Assert.Equal(Content, File.ReadAllText(Path.Combine(directory.Path, recipe.PackageDirectory, "drivers/test.inf")));
            Assert.Single(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
        }
    }

    [Theory]
    [InlineData("../outside.inf")]
    [InlineData("/outside.inf")]
    [InlineData("drivers/../../outside.inf")]
    [InlineData("drivers/test.inf:stream")]
    [InlineData("drivers\\test.inf")]
    public async Task OutputNamesCannotEscapeTheProtectedPackage(string path)
    {
        using var directory = new TemporaryDirectory();
        using MemoryStream archive = Archive();
        await Assert.ThrowsAsync<InvalidDataException>(() => RazerKeyboardDriverVendorPackage.ExtractVerifiedAsync(
            archive, directory.Path, Recipe(path, new string('0', 64), (int)archive.Length - 32), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task CommonSelectionDoesNotExtractAnotherModelsFilters()
    {
        using var directory = new TemporaryDirectory();
        var source = RazerKeyboardDriverRecipes.Pid02C6;
        byte[] bytes = Encoding.UTF8.GetBytes("signed member fixture");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string[] paths = ["rzcommonu/rzcommonu.inf", "rzcommonu/rzcommon.cat", "rzcommonu/RzCommon.sys", "other-model/filter.sys"];
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in paths)
            {
                using Stream output = zip.CreateEntry(source.PackageDirectory + "/" + path).Open();
                output.Write(bytes);
            }
        }
        archive.Position = 0;
        await RazerKeyboardDriverPackage.ExtractVerifiedAsync(archive, directory.Path, source,
            paths.Select(path => new RazerKeyboardDriverPackage.PackageFile(path, bytes.Length, hash)).ToArray(),
            default, "rzcommonu/", "selected");
        Assert.Equal(3, Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Length);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "selected/other-model")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, source.PackageDirectory)));
    }

    [Fact]
    public void Legacy0255RecipeContainsOnlyCatalogCoveredInfClosure()
    {
        var recipe = RazerKeyboardDriverRecipes.Pid0255;
        Assert.All(recipe.StageOrder.Where(path => path != recipe.InfFor("common")), path =>
            Assert.StartsWith("RzDev_0255_", Path.GetFileName(path), StringComparison.Ordinal));
        Assert.DoesNotContain(recipe.Files, file => Path.GetFileName(file.RelativePath).StartsWith("RzDevU_0255_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, recipe.Files.Count(file => file.RelativePath.EndsWith("WdfCoinstaller01009.dll", StringComparison.Ordinal)));
        Assert.All(recipe.Files.Where(file => file.RelativePath.EndsWith(".dll", StringComparison.Ordinal)), file =>
            Assert.Equal("18487B4FF94EDCCC98ED59D9FCA662D4A1331C5F1E14DF8DB3093256DD9F1C3E", file.Sha256));
        Assert.Equal(16, recipe.VendorBlocks!.Count);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenBlade-vendor-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static RazerKeyboardDriverRecipe Recipe(string path, string hash, int compressedLength) => new(
        "fixture", "fixture", "FFFF", "RzDev_ffff", hash, "fixture.exe", 1, hash, "signer",
        [new(path, Encoding.UTF8.GetByteCount(Content), hash)], [], [],
        new Uri("https://app-assets.razer.com/fixture.exe"), [new(path, 32, compressedLength, true)]);

    private static MemoryStream Block(string encoded)
    {
        byte[] bytes = Convert.FromBase64String(encoded);
        var stream = new MemoryStream();
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x80000000u | (uint)bytes.Length);
        stream.Write(prefix);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Archive()
    {
        using MemoryStream block = Block(Small);
        var stream = new MemoryStream();
        stream.Write(new byte[] { 2, 0, 0, 0, 0xEF, 0xBE, 0xAD, 0xDE, 0x4E, 0x75, 0x6C, 0x6C, 0x73, 0x6F, 0x66, 0x74, 0x49, 0x6E, 0x73, 0x74 });
        stream.Write(BitConverter.GetBytes(1u));
        stream.Write(BitConverter.GetBytes((uint)(28 + block.Length)));
        block.CopyTo(stream);
        stream.Position = 0;
        return stream;
    }
}
