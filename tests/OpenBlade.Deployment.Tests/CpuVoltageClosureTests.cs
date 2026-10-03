using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

public sealed class CpuVoltageClosureTests
{
    [Fact]
    public void ClosureRequiresEveryRoleAndBindsExactManifestBytes()
    {
        string source = new('a', 40);
        byte[] bytes = Manifest(source, CpuVoltageDeploymentClosure.RequiredFiles);
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(12, CpuVoltageDeploymentClosure.Parse(bytes, source, digest).Count);
        Assert.Throws<InvalidDataException>(() => CpuVoltageDeploymentClosure.Parse(bytes, new string('b', 40), digest));
        Assert.Throws<InvalidDataException>(() => CpuVoltageDeploymentClosure.Parse(bytes, source, new string('0', 64)));
        byte[] incomplete = Manifest(source, CpuVoltageDeploymentClosure.RequiredFiles.Skip(1));
        Assert.Throws<InvalidDataException>(() => CpuVoltageDeploymentClosure.Parse(incomplete, source,
            Convert.ToHexString(SHA256.HashData(incomplete)).ToLowerInvariant()));
    }

    [Theory]
    [InlineData("a/b/c/d.dll")]
    [InlineData("service/CON.dll")]
    [InlineData("service/lpt0.txt")]
    [InlineData("service")]
    [InlineData("OpenBlade.Capture.dll")]
    public void ClosureRejectsUnsafeNamesCollisionsAndAliases(string extra)
    {
        string source = new('a', 40);
        byte[] bytes = Manifest(source, CpuVoltageDeploymentClosure.RequiredFiles.Append(extra));
        Assert.Throws<InvalidDataException>(() => CpuVoltageDeploymentClosure.Parse(bytes, source,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    }

    [Fact]
    public void PinnedFileVerificationRejectsSameLengthChangedBytes()
    {
        byte[] expected = [1, 2, 3];
        var file = new CpuVoltageDeploymentFile("a.dll", 3, Convert.ToHexString(SHA256.HashData(expected)));
        using MemoryStream valid = new(expected);
        CpuVoltageDeploymentClosure.VerifyFile(valid, file);
        Assert.Equal(0, valid.Position);
        using MemoryStream changed = new([1, 2, 4]);
        Assert.Throws<InvalidDataException>(() => CpuVoltageDeploymentClosure.VerifyFile(changed, file));
    }

    private static byte[] Manifest(string source, IEnumerable<string> names) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        source,
        files = names.Select(name => new { name, length = 1, sha256 = new string('A', 64) }).ToArray(),
    });
}
