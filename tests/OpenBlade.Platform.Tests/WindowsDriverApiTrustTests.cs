using System.Security.Cryptography;

namespace OpenBlade.Platform.Tests;

public sealed class WindowsDriverApiTrustTests
{
    private const string MicrosoftWindowsPublisher =
        "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void RejectsRelativeMissingAndUnsignedFiles()
    {
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            "unsigned.dll", new[] { MicrosoftWindowsPublisher }));

        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");
        Assert.ThrowsAny<IOException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            missing, new[] { MicrosoftWindowsPublisher }));

        using var temporary = new TemporaryFile("not a signed executable"u8.ToArray());
        Assert.Throws<CryptographicException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            temporary.Path, new[] { MicrosoftWindowsPublisher }));
    }

    [Fact]
    public void UsesThePublisherFromSuccessfulTrustStateWithoutThumbprintPinning()
    {
        string systemFile = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        string identity = WindowsDriverApiTrust.VerifyAuthenticodeFile(
            systemFile, new[] { MicrosoftWindowsPublisher });
        Assert.StartsWith("SHA256:", identity, StringComparison.Ordinal);
        Assert.Equal(71, identity.Length);

        Assert.Throws<CryptographicException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            systemFile, new[] { WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher }));
        Assert.True(WindowsDriverApiTrust.MatchesExpectedPublisher(
            MicrosoftWindowsPublisher, new[] { MicrosoftWindowsPublisher }));
        Assert.False(WindowsDriverApiTrust.MatchesExpectedPublisher(
            MicrosoftWindowsPublisher, new[] { "CN=Rotated certificate with a different publisher" }));
    }

    [Fact]
    public void ReturnsAContentObservationRatherThanAReleaseAllowlistIdentity()
    {
        string kernelIdentity = WindowsDriverApiTrust.VerifyAuthenticodeFile(
            Path.Combine(Environment.SystemDirectory, "kernel32.dll"), new[] { MicrosoftWindowsPublisher });
        string ntdllIdentity = WindowsDriverApiTrust.VerifyAuthenticodeFile(
            Path.Combine(Environment.SystemDirectory, "ntdll.dll"), new[] { MicrosoftWindowsPublisher });

        Assert.NotEqual(kernelIdentity, ntdllIdentity);
    }

    [Fact]
    public void CancellationStopsBeforePathOrTrustAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            "not-even-an-absolute-path", new[] { MicrosoftWindowsPublisher }, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => WindowsDriverApiTrust.VerifyCatalogMembers(
            "not-even-an-absolute-path", Array.Empty<string>(), new[] { MicrosoftWindowsPublisher }, cancellation.Token));
    }

    [Fact]
    public void ReadLockBlocksWritesAndDeletionUntilReleased()
    {
        using var temporary = new TemporaryFile("locked"u8.ToArray());
        using (var handle = WindowsDriverApiTrust.OpenLockedFileForTest(temporary.Path))
        {
            Assert.Throws<IOException>(() => File.Open(temporary.Path, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
            Assert.Throws<IOException>(() => File.Delete(temporary.Path));
        }

        File.WriteAllText(temporary.Path, "released");
        File.Delete(temporary.Path);
        Assert.False(File.Exists(temporary.Path));
    }

    [Fact]
    public void BoundsCatalogMembersAndPathsBeforeOpeningFiles()
    {
        string catalog = Path.Combine(Path.GetTempPath(), "missing.cat");
        string[] tooManyMembers = Enumerable.Repeat(catalog, WindowsDriverApiTrust.MaximumMemberCount + 1).ToArray();
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.VerifyCatalogMembers(
            catalog, tooManyMembers, new[] { MicrosoftWindowsPublisher }));

        string tooLong = "C:\\" + new string('a', WindowsDriverApiTrust.MaximumPathLength) + ".dll";
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            tooLong, new[] { MicrosoftWindowsPublisher }));
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            catalog, Array.Empty<string>()));

        using var oversized = new TemporaryFile(new byte[] { 1 });
        using (var file = File.Open(oversized.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.SetLength(WindowsDriverApiTrust.MaximumFileLength + 1);
        }
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.VerifyAuthenticodeFile(
            oversized.Path, new[] { MicrosoftWindowsPublisher }));
    }

    [Fact]
    public void RejectsAReparsePointInAnyPathComponent()
    {
        string path = @"C:\safe\linked\driver.sys";
        Assert.Throws<InvalidDataException>(() => WindowsDriverApiTrust.EnsureNoReparsePoints(path,
            current => string.Equals(current, @"C:\safe\linked", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory));
    }

    [Fact]
    public void RejectsAnUnsignedCatalogBeforeTreatingAFileAsItsMember()
    {
        using var catalog = new TemporaryFile("not a catalog"u8.ToArray(), ".cat");
        using var member = new TemporaryFile("not a member"u8.ToArray(), ".sys");

        Assert.Throws<CryptographicException>(() => WindowsDriverApiTrust.VerifyCatalogMembers(
            catalog.Path, new[] { member.Path },
            new[] { WindowsDriverApiTrust.MicrosoftHardwareCompatibilityPublisher }));
    }

    [Fact]
    public void CatalogMembershipPrefersSha256AndDoesNotProbeSha1AfterSuccess()
    {
        var algorithms = new List<string>();

        WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
        {
            algorithms.Add(algorithm);
            return 0;
        });

        Assert.Equal(["SHA256"], algorithms);
    }

    [Fact]
    public void CatalogMembershipRetriesSha1OnlyForExactNoSignatureStatus()
    {
        var algorithms = new List<string>();

        WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
        {
            algorithms.Add(algorithm);
            return algorithm == "SHA256" ? unchecked((int)0x800B0100) : 0;
        });

        Assert.Equal(["SHA256", "SHA1"], algorithms);
    }

    [Fact]
    public void CatalogMembershipCancellationAfterSha256MissPreventsSha1Fallback()
    {
        var algorithms = new List<string>();
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
            {
                algorithms.Add(algorithm);
                cancellation.Cancel();
                return unchecked((int)0x800B0100);
            }, cancellation.Token));

        Assert.Equal(["SHA256"], algorithms);
    }

    [Theory]
    [InlineData(unchecked((int)0x80096010))]
    [InlineData(unchecked((int)0x800B010C))]
    public void CatalogMembershipDoesNotDowngradeOtherTrustFailures(int status)
    {
        var algorithms = new List<string>();

        CryptographicException error = Assert.Throws<CryptographicException>(() =>
            WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
            {
                algorithms.Add(algorithm);
                return status;
            }));

        Assert.Equal(["SHA256"], algorithms);
        Assert.Contains($"0x{status:X8}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogMembershipPreservesIoFailureWithoutTryingSha1()
    {
        var algorithms = new List<string>();
        var expected = new IOException("locked member read failed");

        IOException actual = Assert.Throws<IOException>(() =>
            WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
            {
                algorithms.Add(algorithm);
                throw expected;
            }));

        Assert.Same(expected, actual);
        Assert.Equal(["SHA256"], algorithms);
    }

    [Fact]
    public void CatalogMembershipRejectsFailedSha1Fallback()
    {
        var algorithms = new List<string>();

        CryptographicException error = Assert.Throws<CryptographicException>(() =>
            WindowsDriverApiTrust.VerifyCatalogMemberAlgorithms(algorithm =>
            {
                algorithms.Add(algorithm);
                return algorithm == "SHA256" ? unchecked((int)0x800B0100) : unchecked((int)0x80096010);
            }));

        Assert.Equal(["SHA256", "SHA1"], algorithms);
        Assert.Contains("0x80096010", error.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryFile : IDisposable
    {
        internal TemporaryFile(byte[] content, string extension = ".bin")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(Path, content);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
