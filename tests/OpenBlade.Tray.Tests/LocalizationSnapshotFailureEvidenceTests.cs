using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Localization;

namespace OpenBlade.Tray.Tests;

public sealed class LocalizationSnapshotFailureEvidenceTests
{
    [Fact]
    public void TryWriteRetainsBothCanonicalFramesWithIndependentDimensions()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            byte[] previous = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] candidate = [9, 10, 11, 12, 13, 14, 15, 16];

            bool retained = LocalizationSnapshotFailureEvidence.TryWrite(
                root,
                "tray",
                "main-home",
                2,
                1,
                previous,
                1,
                2,
                candidate);

            Assert.True(retained);
            string directory = GetSingleEvidenceDirectory(root, "tray-main-home");
            Assert.Equal(previous, File.ReadAllBytes(Path.Combine(directory, "previous.bgra")));
            Assert.Equal(candidate, File.ReadAllBytes(Path.Combine(directory, "candidate.bgra")));
            using JsonDocument metadata = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "metadata.json")));
            Assert.Equal("tray", metadata.RootElement.GetProperty("surface").GetString());
            Assert.Equal("main-home", metadata.RootElement.GetProperty("stage").GetString());
            Assert.Equal(2, metadata.RootElement.GetProperty("previous").GetProperty("width").GetInt32());
            Assert.Equal(1, metadata.RootElement.GetProperty("previous").GetProperty("height").GetInt32());
            Assert.Equal(1, metadata.RootElement.GetProperty("candidate").GetProperty("width").GetInt32());
            Assert.Equal(2, metadata.RootElement.GetProperty("candidate").GetProperty("height").GetInt32());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(previous)).ToLowerInvariant(),
                metadata.RootElement.GetProperty("previous").GetProperty("sha256").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(candidate)).ToLowerInvariant(),
                metadata.RootElement.GetProperty("candidate").GetProperty("sha256").GetString());
            Assert.DoesNotContain(root, metadata.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SameConfiguredRootPreservesConcurrentFailureEvidence()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            byte[] first = [1, 1, 1, 1];
            byte[] second = [2, 2, 2, 2];
            bool firstRetained = false;
            bool secondRetained = false;

            Parallel.Invoke(
                () => firstRetained = LocalizationSnapshotFailureEvidence.TryWrite(
                    root,
                    "tray",
                    "main-home",
                    1,
                    1,
                    first,
                    1,
                    1,
                    first),
                () => secondRetained = LocalizationSnapshotFailureEvidence.TryWrite(
                    root,
                    "tray",
                    "main-home",
                    1,
                    1,
                    second,
                    1,
                    1,
                    second));

            Assert.True(firstRetained);
            Assert.True(secondRetained);
            string stageDirectory = Path.Combine(root, "tray-main-home");
            string[] failures = Directory.GetDirectories(stageDirectory);
            Assert.Equal(2, failures.Length);
            byte[][] retainedFrames = failures
                .Select(directory =>
                {
                    byte[] previous = File.ReadAllBytes(
                        Path.Combine(directory, "previous.bgra"));
                    byte[] candidate = File.ReadAllBytes(
                        Path.Combine(directory, "candidate.bgra"));
                    Assert.Equal(previous, candidate);
                    using JsonDocument metadata = JsonDocument.Parse(
                        File.ReadAllText(Path.Combine(directory, "metadata.json")));
                    Assert.Equal(
                        Convert.ToHexString(SHA256.HashData(previous)).ToLowerInvariant(),
                        metadata.RootElement.GetProperty("previous").GetProperty("sha256").GetString());
                    Assert.Equal(
                        Convert.ToHexString(SHA256.HashData(candidate)).ToLowerInvariant(),
                        metadata.RootElement.GetProperty("candidate").GetProperty("sha256").GetString());
                    return previous;
                })
                .OrderBy(bytes => bytes[0])
                .ToArray();
            Assert.Equal(first, retainedFrames[0]);
            Assert.Equal(second, retainedFrames[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryWriteRejectsUnsafeOrOversizedEvidenceWithoutCreatingArtifacts()
    {
        string parent = CreateTemporaryDirectory();
        string root = Path.Combine(parent, "evidence");
        try
        {
            Assert.False(LocalizationSnapshotFailureEvidence.TryWrite(
                root,
                "tray",
                "../escape",
                1,
                1,
                new byte[4],
                1,
                1,
                new byte[4]));
            Assert.False(LocalizationSnapshotFailureEvidence.TryWrite(
                root,
                "tray",
                "main-home",
                4096,
                2049,
                new byte[4],
                1,
                1,
                new byte[4]));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void TryWriteReportsRetentionFailureWithoutReplacingTheCallerFailure()
    {
        string parent = CreateTemporaryDirectory();
        string blockedRoot = Path.Combine(parent, "not-a-directory");
        File.WriteAllText(blockedRoot, "keep");
        try
        {
            var original = new InvalidOperationException("stabilization failed");

            Action failAfterRetention = () =>
            {
                bool retained = LocalizationSnapshotFailureEvidence.TryWrite(
                    blockedRoot,
                    "installer",
                    "installer-progress",
                    1,
                    1,
                    new byte[4],
                    1,
                    1,
                    new byte[4]);
                Assert.False(retained);
                throw original;
            };
            Exception? thrown = Record.Exception(failAfterRetention);

            Assert.Same(original, thrown);
            Assert.Equal("keep", File.ReadAllText(blockedRoot));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void TryWriteWithoutConfiguredRootCreatesNoArtifacts()
    {
        Assert.False(LocalizationSnapshotFailureEvidence.TryWrite(
            null,
            "tray",
            "main-home",
            1,
            1,
            new byte[4],
            1,
            1,
            new byte[4]));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "OpenBlade.SnapshotEvidenceTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSingleEvidenceDirectory(string root, string stage) =>
        Assert.Single(Directory.GetDirectories(Path.Combine(root, stage)));
}
