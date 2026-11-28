using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeOriginCorrectionsTests
{
    [Fact]
    public void SharedDescriptorVectorsAdmitMetadataOnlyAtProgramData()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-origin-corrections-v1.json")));
        foreach (var item in data.RootElement.GetProperty("descriptors").EnumerateArray())
        {
            var descriptor = new RawSecurityDescriptor(Convert.FromBase64String(item.GetProperty("descriptor").GetString()!), 0);
            if (item.GetProperty("programData").GetBoolean()) { WindowsNativeEpochFiles.RequireSharedAncestor(descriptor, true); }
            else { Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.RequireSharedAncestor(descriptor, true)); }
            Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.RequireSharedAncestor(descriptor, false));
            Assert.False(WindowsNativeEpochFiles.AcceptsPublicRoot(descriptor));
        }
    }

    [Fact]
    public void SharedOwnerVectorsRoundtripExactUtf16AndKeepLegacyAsciiLiteral()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-origin-corrections-v1.json")));
        foreach (var item in data.RootElement.GetProperty("owners").EnumerateArray())
        {
            string path = new(item.GetProperty("units").EnumerateArray().Select(v => (char)v.GetInt32()).ToArray());
            byte[] record = Convert.FromBase64String(item.GetProperty("record").GetString()!);
            var owner = NativeStartupOwner.Parse(record);
            Assert.Equal(path, owner.Path);
            Assert.Equal(record, owner.Encode());
        }
        foreach (var item in data.RootElement.GetProperty("malformedOwners").EnumerateArray())
        { Assert.Throws<InvalidDataException>(() => NativeStartupOwner.Parse(Convert.FromBase64String(item.GetString()!))); }
        byte[] legacy = NativeCanonical.Lines("OpenBlade.NativeStartupOwner.v1", new string('a', 32), new string('b', 32), "17", "1", "0", @"C:\ASCII\Setup.exe");
        Assert.Equal(@"C:\ASCII\Setup.exe", NativeStartupOwner.Parse(legacy).Path);
        string loneUnit = "C:\\" + (char)0xd800 + "\\Setup.exe";
        var lone = new NativeStartupOwner(new string('a', 32), new string('b', 32), 17, 1, 0, loneUnit);
        Assert.Equal(loneUnit, NativeStartupOwner.Parse(lone.Encode()).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("QQ==")]
    [InlineData("QQB=")]
    [InlineData("QQA= ")]
    [InlineData("AA A=")]
    [InlineData("AAAA")]
    [InlineData("!!!!")]
    public void MalformedOwnerEncodingIsRejected(string encoded)
    {
        byte[] record = NativeCanonical.Lines("OpenBlade.NativeStartupOwner.v2", new string('a', 32), new string('b', 32), "17", "1", "0", encoded);
        Assert.Throws<InvalidDataException>(() => NativeStartupOwner.Parse(record));
    }

    [Fact]
    public void ExcessiveAndNulOwnerPathsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => NativeStartupOwner.EncodePath(new string('x', 4097)));
        Assert.Throws<InvalidDataException>(() => NativeStartupOwner.EncodePath("C:\\a\0b"));
        byte[] large = Encoding.ASCII.GetBytes("OpenBlade.NativeStartupOwner.v2\n" + new string('a', 32) + "\n" + new string('b', 32) + "\n17\n1\n0\n" + Convert.ToBase64String(new byte[8194]) + "\n");
        Assert.Throws<InvalidDataException>(() => NativeStartupOwner.Parse(large));
    }

    [Fact]
    public void LegacyGuardPinsPresentParentAndRefusesAllPreservedHistory()
    {
        var calls = new List<string>();
        WindowsNativeEpochFiles.RequireLegacyStoreAbsent(() => true, () => { calls.Add("pin"); return new Lease(calls); }, () => { calls.Add("child"); return false; });
        Assert.Equal(new[] { "pin", "child", "dispose" }, calls);
        Assert.Throws<IOException>(() => WindowsNativeEpochFiles.RequireLegacyStoreAbsent(() => true, () => new Lease(calls), () => true));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsNativeEpochFiles.RequireLegacyStoreAbsent(() => throw new UnauthorizedAccessException(), () => new Lease(calls), () => false));
        Assert.Throws<IOException>(() => WindowsNativeEpochFiles.RequireLegacyStoreAbsent(() => true, () => throw new IOException("unreadable/reparse parent"), () => false));
        Assert.Throws<IOException>(() => WindowsNativeEpochFiles.RequireLegacyStoreAbsent(() => false, () => new Lease(calls), () => throw new IOException("unknown child")));
    }
    private sealed class Lease(List<string> calls) : IDisposable { public void Dispose() => calls.Add("dispose"); }
}
