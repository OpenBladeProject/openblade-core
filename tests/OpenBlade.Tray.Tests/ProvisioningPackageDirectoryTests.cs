namespace OpenBlade.Tray.Tests;

public sealed class ProvisioningPackageDirectoryTests
{
    [Fact]
    public void UnsubmittedAttemptDeletesOnlyItsNestedPackageAndPreservesJournalAndOtherAttempts()
    {
        WithDirectory(parent =>
        {
            string journal = Path.Combine(parent, "installation.jsonl");
            string previous = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            File.WriteAllText(journal, "diagnostic history");
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "retained.bin"), "previous attempt");
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            using (var lifetime = Lifetime(parent, attempt))
            {
                lifetime.BeginAcquisition();
                string nested = Path.Combine(attempt, "base-audio", "package");
                Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "partial.cab"), "partial download");
            }

            Assert.False(Directory.Exists(attempt));
            Assert.Equal("diagnostic history", File.ReadAllText(journal));
            Assert.Equal("previous attempt", File.ReadAllText(Path.Combine(previous, "retained.bin")));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedAttemptAndUnstartedLifetimeNeverDelete(bool begin)
    {
        WithDirectory(parent =>
        {
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            using (var lifetime = Lifetime(parent, attempt))
            {
                if (begin)
                {
                    lifetime.BeginAcquisition();
                    lifetime.Retain();
                }
                else
                {
                    Directory.CreateDirectory(attempt);
                }
                File.WriteAllText(Path.Combine(attempt, "keep.bin"), "recovery input");
            }
            Assert.Equal("recovery input", File.ReadAllText(Path.Combine(attempt, "keep.bin")));
        });
    }

    [Fact]
    public void ExistingCollisionAndFailedCreationNeverEstablishCleanupOwnership()
    {
        WithDirectory(parent =>
        {
            string collision = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(collision);
            using (var lifetime = Lifetime(parent, collision))
            {
                Assert.Throws<IOException>(lifetime.BeginAcquisition);
            }
            Assert.True(Directory.Exists(collision));

            string failed = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            using (var lifetime = new ProvisioningPackageDirectory(parent, failed, path =>
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "unconfirmed.bin"), "unknown creation");
                throw new IOException("creation failed");
            }, ValidateDirectory))
            {
                Assert.Throws<IOException>(lifetime.BeginAcquisition);
            }
            Assert.True(File.Exists(Path.Combine(failed, "unconfirmed.bin")));
        });
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("nested/00112233445566778899aabbccddeeff")]
    [InlineData("../00112233445566778899aabbccddeeff")]
    public void DestinationMustBeOneDirectGuidChild(string relative)
    {
        WithDirectory(parent =>
        {
            string attempt = Path.GetFullPath(Path.Combine(parent, relative));
            using var lifetime = Lifetime(parent, attempt);
            Assert.Throws<InvalidDataException>(lifetime.BeginAcquisition);
            Assert.False(Directory.Exists(attempt));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReparseEntryOrAncestorRefusesCleanupBeforeAnyDeletion(bool ancestor)
    {
        WithDirectory(parent =>
        {
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            string link = Path.Combine(attempt, "link");
            bool changed = false;
            using (var lifetime = new ProvisioningPackageDirectory(parent, attempt,
                path => Directory.CreateDirectory(path), ValidateDirectory,
                path => changed && string.Equals(path, ancestor ? parent : link, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : File.GetAttributes(path)))
            {
                lifetime.BeginAcquisition();
                Directory.CreateDirectory(link);
                File.WriteAllText(Path.Combine(attempt, "keep.bin"), "untouched");
                changed = true;
            }
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(attempt, "keep.bin")));
            Assert.True(Directory.Exists(link));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcessiveInventoryOrDepthRefusesCleanupBeforeAnyDeletion(bool depth)
    {
        WithDirectory(parent =>
        {
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            using (var lifetime = Lifetime(parent, attempt))
            {
                lifetime.BeginAcquisition();
                if (depth)
                {
                    string nested = attempt;
                    for (int index = 0; index <= ProvisioningPackageDirectory.MaximumDepth; index++)
                    {
                        nested = Path.Combine(nested, "d");
                        Directory.CreateDirectory(nested);
                    }
                }
                else
                {
                    for (int index = 0; index <= ProvisioningPackageDirectory.MaximumEntries; index++)
                    {
                        File.WriteAllText(Path.Combine(attempt, $"{index}.bin"), string.Empty);
                    }
                }
                File.WriteAllText(Path.Combine(attempt, "keep.bin"), "untouched");
            }
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(attempt, "keep.bin")));
        });
    }

    [Fact]
    public void CleanupFailurePreservesThePrimaryException()
    {
        WithDirectory(parent =>
        {
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            bool failCleanup = false;
            var primary = new HttpRequestException("download failed");
            HttpRequestException observed = Assert.Throws<HttpRequestException>((Action)(() =>
            {
                using var lifetime = new ProvisioningPackageDirectory(parent, attempt,
                    path => Directory.CreateDirectory(path), path =>
                    {
                        if (failCleanup) { throw new InvalidDataException("untrusted cleanup inventory"); }
                        ValidateDirectory(path);
                    });
                lifetime.BeginAcquisition();
                failCleanup = true;
                throw primary;
            }));
            Assert.Same(primary, observed);
            Assert.True(Directory.Exists(attempt));
        });
    }

    [Fact]
    public void OwnedReadonlyExtractionFileDoesNotPreventCleanup()
    {
        WithDirectory(parent =>
        {
            string attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            string file = Path.Combine(attempt, "extracted.inf");
            using (var lifetime = Lifetime(parent, attempt))
            {
                lifetime.BeginAcquisition();
                File.WriteAllText(file, "partial extraction");
                File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
            }
            bool retained = File.Exists(file);
            if (retained)
            {
                // Restore only this isolated fixture so a failing assertion does
                // not leave the test's temporary directory behind.
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Assert.False(retained);
            Assert.False(Directory.Exists(attempt));
        });
    }

    private static ProvisioningPackageDirectory Lifetime(string parent, string attempt) =>
        new(parent, attempt, path => Directory.CreateDirectory(path), ValidateDirectory);

    private static void ValidateDirectory(string path)
    {
        if (!Directory.Exists(path)) { throw new IOException("Missing isolated test directory."); }
    }

    private static void WithDirectory(Action<string> action)
    {
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-PackageCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try { action(parent); }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
