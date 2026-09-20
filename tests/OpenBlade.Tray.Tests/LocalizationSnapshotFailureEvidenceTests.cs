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
            string directory = Path.Combine(root, "tray-main-home");
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
    public void SeparateCaptureRootsPreserveEarlierFailureEvidence()
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            string firstRoot = Path.Combine(parent, "first-capture");
            string secondRoot = Path.Combine(parent, "second-capture");
            byte[] first = [1, 1, 1, 1];
            byte[] second = [2, 2, 2, 2];

            Assert.True(LocalizationSnapshotFailureEvidence.TryWrite(
                firstRoot,
                "tray",
                "main-home",
                1,
                1,
                first,
                1,
                1,
                first));
            Assert.True(LocalizationSnapshotFailureEvidence.TryWrite(
                secondRoot,
                "tray",
                "main-home",
                1,
                1,
                second,
                1,
                1,
                second));

            Assert.Equal(
                first,
                File.ReadAllBytes(Path.Combine(firstRoot, "tray-main-home", "previous.bgra")));
            Assert.Equal(
                second,
                File.ReadAllBytes(Path.Combine(secondRoot, "tray-main-home", "previous.bgra")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
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
}
