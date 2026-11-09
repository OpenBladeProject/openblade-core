using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class ApplicationIconAssetTests
{
    [Fact]
    public void CopiedExportsMatchTheReviewedMonolithAssetSet()
    {
        string root = RepositoryRoot();
        string brand = Path.Combine(root, "src", "OpenBlade.Tray", "Assets", "Brand");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(brand, "app-assets.json")));
        Assert.Equal("Monolith", manifest.RootElement.GetProperty("design").GetString());
        JsonElement assets = manifest.RootElement.GetProperty("assets");
        Assert.Equal(8, assets.GetArrayLength());
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            byte[] data = File.ReadAllBytes(Path.Combine(root, asset.GetProperty("path").GetString()!));
            Assert.Equal(asset.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(data)));
        }
    }

    [Theory]
    [InlineData("OpenBlade.ico")]
    [InlineData("OpenBlade.Installer.ico")]
    public void ApplicationAndInstallerHaveEveryNativeFrame(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "OpenBlade.Tray", "Assets", "Brand", name));
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(data));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)));
        Assert.Equal(sizes.Length, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
        for (int frame = 0; frame < sizes.Length; frame++)
        {
            int entry = 6 + frame * 16;
            Assert.Equal(sizes[frame] == 256 ? 0 : sizes[frame], data[entry]);
            Assert.Equal(data[entry], data[entry + 1]);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8)));
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 12)));
            Assert.InRange(offset, 6 + sizes.Length * 16, data.Length - length);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data[offset..(offset + 8)]);
            Assert.Equal(sizes[frame], BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 16)));
            Assert.Equal(sizes[frame], BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 20)));
            Assert.Equal(6, data[offset + 25]); // PNG color type 6: RGBA, retaining transparency.
        }
    }

    [Theory]
    [InlineData("StoreLogo.png", 50)]
    [InlineData("Square44x44Logo.png", 44)]
    [InlineData("Square150x150Logo.png", 150)]
    public void IdentityAliasesRetainTheirDeclaredDimensions(string name, int size)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "build", "identity", "Assets", name));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
        Assert.Equal(6, data[25]);
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
