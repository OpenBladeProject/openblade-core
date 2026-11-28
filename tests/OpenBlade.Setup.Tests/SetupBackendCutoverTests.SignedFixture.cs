using System.IO.Compression;
using System.Security.Cryptography;
using OpenBlade.Deployment;
using static OpenBlade.Deployment.OpenBladeReleaseVerificationFailure;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;
using ManifestCodec = OpenBlade.Deployment.OpenBladeReleaseManifestCodec;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    [LocalLiveRustFixtureTheory]
    [InlineData(SignedFixtureFault.WrongKey, UnknownSigningKey)]
    [InlineData(SignedFixtureFault.InvalidSignature, SignatureInvalid)]
    [InlineData(SignedFixtureFault.NonCanonicalManifest, ManifestNotCanonical)]
    [InlineData(SignedFixtureFault.ChangedOuterByte, InstallerHashMismatch)]
    [InlineData(SignedFixtureFault.TruncatedOuterBytes, InstallerSizeMismatch)]
    public async Task SignedRustFixture_RejectsBeforeWritingPayload(
        SignedFixtureFault fault, OpenBladeReleaseVerificationFailure expectedFailure)
    {
        using var tree = new TestTree();
        var fixture = CreateSignedFixture(fault);

        var failure = await Assert.ThrowsAsync<OpenBladeReleaseVerificationException>(
            () => StageSignedFixtureAsync(fixture, tree.Payload));

        Assert.Equal(expectedFailure, failure.Failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Payload));
    }

    [LocalLiveRustFixtureTheory]
    [InlineData(SignedFixtureFault.SwappedRoles, "Unexpected SHA-256 for " + ServiceFile + ".")]
    [InlineData(SignedFixtureFault.MissingAgent, "Missing backend role " + AgentFile + ".")]
    public async Task SignedRustFixture_RequiresIndependentRolePins(
        SignedFixtureFault fault, string expectedMessage)
    {
        using var tree = new TestTree();
        var fixture = CreateSignedFixture(fault);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => StageSignedFixtureAsync(fixture, tree.Payload));

        Assert.Equal(expectedMessage, failure.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Payload));
    }

    public enum SignedFixtureFault
    {
        None,
        WrongKey,
        InvalidSignature,
        NonCanonicalManifest,
        ChangedOuterByte,
        TruncatedOuterBytes,
        SwappedRoles,
        MissingAgent,
    }

    private sealed record SignedRustFixture(
        byte[] Container, byte[] Manifest, byte[] Signature,
        OpenBladeReleaseTrustPolicy Policy, IReadOnlySet<string> ExpectedPaths);

    private static SignedRustFixture CreateSignedFixture(SignedFixtureFault fault,
        string? scratchRoles = null)
    {
        using var source = new TestTree();
        source.CreateCompletePayload();
        string rustSource = scratchRoles ?? Environment.GetEnvironmentVariable(LiveFixtureDirectoryVariable)!;
        Assert.True(Path.IsPathFullyQualified(rustSource));

        // Stable ZIP entry order, timestamps and storage method keep the outer
        // bytes deterministic. The ZIP only exists in memory and is never executed.
        string[] paths = ReadManifest(source.Payload).Keys
            .Select(path => path.Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in paths)
            {
                if (fault == SignedFixtureFault.MissingAgent && path == AgentFile)
                {
                    continue;
                }

                string inputPath = (fault, path) switch
                {
                    (SignedFixtureFault.SwappedRoles, ServiceFile) => AgentFile,
                    (SignedFixtureFault.SwappedRoles, AgentFile) => ServiceFile,
                    _ => path,
                };
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream output = entry.Open();
                string inputFile = inputPath switch
                {
                    ServiceFile => Path.Combine(rustSource, scratchRoles is null ? LiveServiceFixture : ServiceFile),
                    AgentFile => Path.Combine(rustSource, scratchRoles is null ? LiveAgentFixture : AgentFile),
                    _ => Path.Combine(source.Payload, inputPath),
                };
                using Stream input = File.OpenRead(inputFile);
                input.CopyTo(output);
            }
        }

        byte[] container = buffer.ToArray();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        OpenBladeReleaseTrustKey signer = ExportTrustKey(key);
        byte[] manifest = ManifestCodec.SerializeCanonical(new OpenBladeReleaseManifest
        {
            SchemaVersion = ManifestCodec.CurrentSchemaVersion,
            SignatureAlgorithm = ManifestCodec.SignatureAlgorithm,
            SigningKeyCurve = ManifestCodec.SigningKeyCurve,
            SigningKeyId = signer.KeyId,
            InstallationId = new Guid("11111111-1111-1111-1111-111111111111"),
            Sequence = 1,
            Channel = ManifestCodec.LocalDevChannel,
            Product = ManifestCodec.Product,
            Version = "65535.1.2.3",
            SourceRevision = LiveFixtureRevision,
            Architecture = ManifestCodec.Architecture,
            MinimumWindowsBuild = ManifestCodec.MinimumWindowsBuild,
            InstallerFileName = ManifestCodec.InstallerFileName,
            BundleId = ManifestCodec.BundleId,
            ProviderKey = ManifestCodec.ProviderKey,
            UpgradeCode = ManifestCodec.UpgradeCode,
            InstallerSize = container.Length,
            InstallerSha256 = Convert.ToHexStringLower(SHA256.HashData(container)),
        });
        if (fault == SignedFixtureFault.NonCanonicalManifest)
        {
            manifest = [.. manifest, (byte)'\n'];
        }

        byte[] signature = key.SignHash(
            OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifest),
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        switch (fault)
        {
            case SignedFixtureFault.WrongKey:
                using (ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    signer = ExportTrustKey(other);
                }
                break;
            case SignedFixtureFault.InvalidSignature:
                Array.Clear(signature);
                break;
            case SignedFixtureFault.ChangedOuterByte:
                container[^1] ^= 1;
                break;
            case SignedFixtureFault.TruncatedOuterBytes:
                container = container[..^1];
                break;
        }

        return new(container, manifest, signature, new(true, [signer]), paths.ToHashSet(StringComparer.Ordinal));
    }

    private static OpenBladeReleaseTrustKey ExportTrustKey(ECDsa key)
    {
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        return new(OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(publicKey), Convert.ToBase64String(publicKey));
    }

    private static async Task<SortedDictionary<string, string>> StageSignedFixtureAsync(
        SignedRustFixture fixture, string destination, string serviceHash = LiveServiceHash,
        string agentHash = LiveAgentHash)
    {
        OpenBladeVerifiedRelease release = OpenBladeSignedReleaseVerifier.Verify(
            fixture.Manifest, fixture.Signature, fixture.Policy);
        // Decode the same private stream only after the production verifier
        // accepts every byte. Setup receives only files from the verified ZIP.
        using var stream = new MemoryStream(fixture.Container, writable: false);
        await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(stream, release);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var inventory = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!fixture.ExpectedPaths.Contains(entry.FullName) || files.ContainsKey(entry.FullName))
            {
                throw new InvalidDataException("Unexpected or duplicate fixture entry.");
            }

            using Stream input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            byte[] content = bytes.ToArray();
            files.Add(entry.FullName, content);
            string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            inventory.Add(relativePath, Convert.ToHexString(SHA256.HashData(content)));
        }

        RequireRustRoleHash(inventory, ServiceFile, serviceHash);
        RequireRustRoleHash(inventory, AgentFile, agentHash);
        if (files.Count != fixture.ExpectedPaths.Count)
        {
            throw new InvalidDataException("Incomplete fixture inventory.");
        }

        foreach ((string path, byte[] bytes) in files)
        {
            WriteFile(Path.Combine(destination, path), bytes);
        }

        return inventory;
    }
}
