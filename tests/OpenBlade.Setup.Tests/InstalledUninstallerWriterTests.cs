using System.Security.Cryptography;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class InstalledUninstallerWriterTests
{
    [Fact]
    public async Task WriteVerified_CopiesExactBytesAndHashToNewDestination()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "source", "OpenBlade.Setup.exe");
        string destination = Path.Combine(temporary.Path, "installed", "OpenBlade.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        byte[] expected = RandomNumberGenerator.GetBytes(512 * 1024 + 37);
        await File.WriteAllBytesAsync(source, expected);

        await new VerifiedInstalledUninstallerWriter().WriteVerifiedAsync(
            new(source, destination),
            CancellationToken.None);

        byte[] actual = await File.ReadAllBytesAsync(destination);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WriteVerified_CreateNewRefusesToOverwriteExistingDestination()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "source", "OpenBlade.Setup.exe");
        string destination = Path.Combine(temporary.Path, "installed", "OpenBlade.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        byte[] existing = [9, 8, 7];
        await File.WriteAllBytesAsync(destination, existing);

        await Assert.ThrowsAsync<IOException>(
            () => new VerifiedInstalledUninstallerWriter().WriteVerifiedAsync(
                new(source, destination),
                CancellationToken.None));

        Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task WriteVerified_RefusesSameSourceAndDestination()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "OpenBlade.Setup.exe");
        await File.WriteAllBytesAsync(executable, [1, 2, 3]);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new VerifiedInstalledUninstallerWriter().WriteVerifiedAsync(
                new(executable, executable),
                CancellationToken.None));

        Assert.Contains("active install directory", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(executable));
    }

    [Fact]
    public async Task WriteVerified_PreCanceledOperationLeavesNoPartialDestination()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "source", "OpenBlade.Setup.exe");
        string destination = Path.Combine(temporary.Path, "installed", "OpenBlade.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1024 * 1024));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new VerifiedInstalledUninstallerWriter().WriteVerifiedAsync(
                new(source, destination),
                cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"OpenBlade.Setup.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
