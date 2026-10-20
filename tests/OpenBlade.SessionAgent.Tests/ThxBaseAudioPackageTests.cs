using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxBaseAudioPackageTests
{
    [Theory]
    [InlineData("RZ09-0581", "IntcAudioBus.inf", 33406870, "BC77F3E1371C05BFE4EE68E70E4BE6A06F352143043FF0195FF92F23B8B6CDF3", 10)]
    [InlineData("RZ09-0528", "HDXACPRazer.inf", 13640113, "6B82AB54C802FECED29BDEAF2248FC861B48DECD94AFD236AB652A19584B3FF9", 1)]
    [InlineData("RZ09-0530", "HDXACPRazer.inf", 13640113, "6B82AB54C802FECED29BDEAF2248FC861B48DECD94AFD236AB652A19584B3FF9", 1)]
    public void RecipesPinOnlyReviewedModelClosures(string model, string root, long length, string hash, int packages)
    {
        ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel(model);
        Assert.Equal(root, recipe.RootInfName);
        Assert.Equal(length, recipe.Length);
        Assert.Equal(hash, recipe.Sha256);
        Assert.Equal(packages, recipe.Packages.Count);
        Assert.Equal(packages, recipe.Catalogs.Count);
        Assert.Equal("https", recipe.Source.Scheme);
        Assert.Equal("catalog.s.download.windowsupdate.com", recipe.Source.Host);
        Assert.Equal(recipe.Files.Count, recipe.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (ThxBaseAudioDriverPackage package in recipe.Packages)
        {
            Assert.Contains(package.Files, file => file.Name.Equals(package.InfName, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(package.Files, file => file.Name.Equals(package.CatalogFile, StringComparison.OrdinalIgnoreCase));
            Assert.All(package.Files, file => Assert.Contains(file, recipe.Files));
        }
        Assert.DoesNotContain(recipe.Files, file => file.Name.Contains("sdcaclass", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recipe.Files, file => file.Name.Equals("IntcAcaCxSensor.inf", StringComparison.OrdinalIgnoreCase)
            || file.Name.Equals("IntcNldCxSensor.inf", StringComparison.OrdinalIgnoreCase)
            || file.Name.Equals("IntcAudioSensorProxyDriver.inf", StringComparison.OrdinalIgnoreCase)
            || file.Name.Equals("LT6911Au.inf", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("RZ09-0482")]
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0484")]
    [InlineData("RZ09-0581-any")]
    [InlineData("")]
    public void UnknownModelsAreRejected(string model) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThxBaseAudioPackage.ForModel(model));

    [Theory]
    [InlineData("RZ09-0581", "B74B42134CF19DD5795C3814D7C55B09A28D32856573E850470210856D05CB19")]
    [InlineData("RZ09-0528", "D63D5F23036643D077F5886E9A78769A0D932A65886B55EE7620F09A25B497ED")]
    public void ReviewedRecipeSerializationIsStableAndCollectionsAreImmutable(string model, string fingerprint)
    {
        ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel(model);
        Assert.Equal(fingerprint, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(recipe)))));
        Assert.Throws<NotSupportedException>(() => ((IList<ThxBaseAudioFile>)recipe.Files).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)recipe.RootHardwareIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)recipe.Catalogs[0].Members).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ThxBaseAudioFile>)recipe.Packages[0].Files).Clear());
        Assert.Same(ThxBaseAudioPackage.ForModel("RZ09-0528"), ThxBaseAudioPackage.ForModel("RZ09-0530"));
    }

    [Fact]
    public void RootMatchScopeIncludesIncidentalModelsAndChipsetsWithoutAdmittingThem()
    {
        IReadOnlyList<string> intel = ThxBaseAudioPackage.ForModel("RZ09-0581").RootHardwareIds;
        Assert.Equal(6, intel.Count);
        Assert.Contains(@"PCI\VEN_8086&DEV_E328&CC_0401", intel);
        Assert.Contains(@"PCI\VEN_8086&DEV_E428&CC_040380", intel);
        Assert.Contains(@"PCI\VEN_8086&DEV_4D28&CC_040380", intel);
        IReadOnlyList<string> realtek = ThxBaseAudioPackage.ForModel("RZ09-0528").RootHardwareIds;
        Assert.Equal(11, realtek.Count);
        Assert.Contains(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A582020", realtek);
        Assert.Contains(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1A582014", realtek);
        Assert.Throws<ArgumentOutOfRangeException>(() => ThxBaseAudioPackage.ForModel("RZ09-0482"));
    }

    [Fact]
    public void IntelPreservesDistinctNestedFilesWithTheSameBasename()
    {
        ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel("RZ09-0581");
        ThxBaseAudioFile root = Assert.Single(recipe.Files, file => file.Name == "MultiChannelWoV.dll");
        ThxBaseAudioFile nested = Assert.Single(recipe.Files, file => file.Name == @"WoV\Service\MultiChannelWoV.dll");
        Assert.NotEqual(root.Sha256, nested.Sha256);
        Assert.Equal(87, recipe.CabinetFileCount);
        Assert.Equal(115854475, recipe.CabinetExpandedLength);
    }

    [Theory]
    [InlineData("..\\escape.inf")]
    [InlineData("C:\\escape.inf")]
    [InlineData("\\absolute.inf")]
    [InlineData("safe.inf:stream")]
    [InlineData("folder//file")]
    [InlineData("safe.\\file")]
    [InlineData("safe \\file")]
    [InlineData("CON.inf")]
    [InlineData("LPT1.txt")]
    [InlineData("*.inf")]
    public void UnsafeCabinetMemberNamesAreRejectedBeforeExtraction(string name)
    {
        using MemoryStream cabinet = Cabinet(name);
        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ReadCabinetTable(cabinet));
    }

    [Fact]
    public void CabinetAllowsSafeNestedPathsAndSignedTrailer()
    {
        using MemoryStream cabinet = Cabinet(@"Fw\one.bin", @"WoV\Service\one.bin");
        cabinet.Position = cabinet.Length;
        cabinet.Write(new byte[64]); // Outside cbCabinet; complete archive hash is checked by acquisition.
        IReadOnlyList<ThxCabinetFile> files = ThxBaseAudioPackage.ReadCabinetTable(cabinet);
        Assert.Equal(new[] { @"Fw\one.bin", @"WoV\Service\one.bin" }, files.Select(file => file.Path));
    }

    [Fact]
    public void CabinetRejectsCaseInsensitiveDuplicateNames()
    {
        using MemoryStream cabinet = Cabinet("same.inf", "SAME.inf");
        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ReadCabinetTable(cabinet));
    }

    [Theory]
    [InlineData(30, 1)] // Previous cabinet.
    [InlineData(30, 2)] // Next cabinet.
    [InlineData(34, 1)] // Nonzero cabinet sequence.
    [InlineData(52, 65535)] // Continued CFFILE.
    [InlineData(28, 513)] // File count bound.
    public void SplitAndUnboundedCabinetTablesAreRejected(int offset, ushort value)
    {
        using MemoryStream cabinet = Cabinet("one.inf");
        cabinet.Position = offset;
        using (var writer = new BinaryWriter(cabinet, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(value);
        }

        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ReadCabinetTable(cabinet));
    }

    [Fact]
    public void CabinetRejectsOversizedMemberAndTruncatedTable()
    {
        using MemoryStream cabinet = Cabinet("one.inf");
        cabinet.Position = 44;
        using (var writer = new BinaryWriter(cabinet, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(uint.MaxValue);
        }

        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ReadCabinetTable(cabinet));
        using MemoryStream truncated = Cabinet("one.inf");
        truncated.SetLength(48);
        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ReadCabinetTable(truncated));
    }

    [Fact]
    public void CabinetInventoryMustContainEveryPinnedClosureMember()
    {
        ThxBaseAudioRecipe recipe = ThxBaseAudioPackage.ForModel("RZ09-0528");
        Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.ValidateCabinetRecipe(
            new[] { new ThxCabinetFile("HDXACPRazer.inf", 1) }, recipe));
    }

    [Fact]
    public void ExecutionLeaseBlocksWritesUntilDisposedAndDetectsChangedBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "openblade-base-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "one.inf");
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(path, bytes);
            ThxBaseAudioFile[] pins = [new("one.inf", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))];
            using (IDisposable lease = ThxBaseAudioPackage.LockVerifiedFiles(root, pins, CancellationToken.None))
            {
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
                Assert.Throws<IOException>(() => File.Delete(path));
            }
            File.WriteAllBytes(path, [3, 2, 1]);
            Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.LockVerifiedFiles(root, pins, CancellationToken.None));
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(Path.Combine(root, "extra.inf"), "extra");
            Assert.Throws<InvalidDataException>(() => ThxBaseAudioPackage.LockVerifiedFiles(root, pins, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MemoryStream Cabinet(params string[] names)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int dataOffset = 44 + names.Sum(name => 17 + Encoding.ASCII.GetByteCount(name));
        writer.Write("MSCF"u8);
        writer.Write(0u);
        writer.Write((uint)(dataOffset + 8 + names.Length));
        writer.Write(0u);
        writer.Write(44u);
        writer.Write(0u);
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)1);
        writer.Write((ushort)names.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((uint)dataOffset);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        for (int index = 0; index < names.Length; index++)
        {
            writer.Write(1u);
            writer.Write((uint)index);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)32);
            writer.Write(Encoding.ASCII.GetBytes(names[index]));
            writer.Write((byte)0);
        }
        writer.Write(0u);
        writer.Write((ushort)names.Length);
        writer.Write((ushort)names.Length);
        writer.Write(new byte[names.Length]);
        stream.Position = 0;
        return stream;
    }
}
