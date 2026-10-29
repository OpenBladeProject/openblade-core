using System.Diagnostics;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerInstallerLogRetentionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OpenBlade.DeploymentBroker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Retention_CopiesOnlyLogsIncludingNestedBurnTempAndIsRestartIdempotent()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid request = Guid.NewGuid();
        string staging = paths.CreateRequestStagingDirectory(request);
        string nested = Path.Combine(staging, "{burn-temp}", ".be");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(staging, "bundle.log"), "burn failure");
        await File.WriteAllTextAsync(Path.Combine(nested, "setup.LOG"), "setup cause");
        await File.WriteAllTextAsync(Path.Combine(nested, "private.tmp"), "not retained");
        await File.WriteAllTextAsync(Path.Combine(staging, "OpenBlade-Setup.exe"), "not retained");

        BrokerInstallerLogRetention retention = new(paths);
        Assert.Equal("installer-logs-retained", await retention.RetainAsync(request));
        string archive = Path.Combine(paths.InstallerLogDirectory, request.ToString("N"));
        Assert.Equal(3, Directory.GetFiles(archive).Length);
        string[] logs = await Task.WhenAll(Directory.GetFiles(archive, "*.log").Select(log => File.ReadAllTextAsync(log)));
        Assert.Contains("burn failure", logs);
        Assert.Contains("setup cause", logs);
        Assert.Contains("setup.LOG", await File.ReadAllTextAsync(Path.Combine(archive, "index.json")));
        paths.DeleteRequestStagingDirectory(request);
        Assert.Equal("installer-logs-retained", await retention.RetainAsync(request));
        Assert.Equal(3, Directory.GetFiles(archive).Length);
    }

    [Fact]
    public async Task Retention_KeepsBoundedTailsAndMarksTruncation()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid request = Guid.NewGuid();
        string staging = paths.CreateRequestStagingDirectory(request);
        for (int index = 0; index < 5; index++)
        {
            byte[] bytes = new byte[BrokerInstallerLogRetention.MaximumLogBytes + 12];
            bytes[^1] = 73;
            await File.WriteAllBytesAsync(Path.Combine(staging, $"{index}.log"), bytes);
        }

        BrokerInstallerLogRetention retention = new(paths);
        Assert.Equal("installer-logs-truncated", await retention.RetainAsync(request));
        string archive = Path.Combine(paths.InstallerLogDirectory, request.ToString("N"));
        string[] logs = Directory.GetFiles(archive, "*.log");
        Assert.Equal(BrokerInstallerLogRetention.MaximumRequestBytes, logs.Sum(log => new FileInfo(log).Length));
        foreach (string log in logs)
        {
            byte[] bytes = await File.ReadAllBytesAsync(log);
            Assert.Equal(BrokerInstallerLogRetention.MaximumLogBytes, bytes.Length);
            Assert.Equal(73, bytes[^1]);
        }
        Assert.Equal("installer-logs-truncated", await retention.RetainAsync(request));
    }

    [Fact]
    public async Task Retention_RotatesOnlyAfterNewSnapshotAndRetainsFixedRequestCount()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerInstallerLogRetention retention = new(paths);
        List<Guid> requests = [];
        for (int index = 0; index <= BrokerInstallerLogRetention.MaximumRequests; index++)
        {
            Guid request = Guid.NewGuid();
            requests.Add(request);
            string staging = paths.CreateRequestStagingDirectory(request);
            await File.WriteAllTextAsync(Path.Combine(staging, "bundle.log"), index.ToString());
            Assert.Equal("installer-logs-retained", await retention.RetainAsync(request));
            Directory.SetLastWriteTimeUtc(Path.Combine(paths.InstallerLogDirectory, request.ToString("N")), DateTime.UtcNow.AddDays(index - 20));
        }
        Assert.Equal(BrokerInstallerLogRetention.MaximumRequests, Directory.GetDirectories(paths.InstallerLogDirectory).Length);
        Assert.False(Directory.Exists(Path.Combine(paths.InstallerLogDirectory, requests[0].ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(paths.InstallerLogDirectory, requests[^1].ToString("N"))));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("depth")]
    [InlineData("path")]
    [InlineData("entries")]
    public async Task Retention_RefusesUnboundedDiscoveryWithoutDeletingPriorEvidence(string limit)
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid previous = Guid.NewGuid();
        paths.CreateRequestStagingDirectory(previous);
        BrokerInstallerLogRetention retention = new(paths);
        Assert.Equal("installer-logs-none", await retention.RetainAsync(previous));
        Guid request = Guid.NewGuid();
        string staging = paths.CreateRequestStagingDirectory(request);
        if (limit is "count" or "entries")
        {
            int count = limit == "count" ? BrokerInstallerLogRetention.MaximumLogs : BrokerInstallerLogRetention.MaximumEntries;
            for (int index = 0; index <= count; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(staging, $"{index}." + (limit == "count" ? "log" : "tmp")), "log");
            }
        }
        else
        {
            string nested = staging;
            int depth = limit == "depth" ? BrokerInstallerLogRetention.MaximumDepth + 1 : 6;
            for (int index = 0; index < depth; index++)
            {
                nested = Path.Combine(nested, limit == "path" ? new string('a', 100) : "nested");
                Directory.CreateDirectory(nested);
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => retention.RetainAsync(request));
        Assert.True(Directory.Exists(staging));
        Assert.True(File.Exists(Path.Combine(paths.InstallerLogDirectory, previous.ToString("N"), "index.json")));
    }

    [Fact]
    public async Task Retention_RejectsReparseSourceAndArchiveRoot()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid request = Guid.NewGuid();
        string staging = paths.CreateRequestStagingDirectory(request);
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(staging, "redirect");
        CreateJunction(link, outside);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BrokerInstallerLogRetention(paths).RetainAsync(request));
        }
        finally
        {
            Directory.Delete(link);
        }
        Directory.Delete(paths.InstallerLogDirectory);
        CreateJunction(paths.InstallerLogDirectory, outside);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BrokerInstallerLogRetention(paths).RetainAsync(request));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(paths.InstallerLogDirectory);
        }
    }

    [Fact]
    public async Task Retention_RejectsOversizedExistingArchiveWithoutDeletingSource()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid request = Guid.NewGuid();
        string staging = paths.CreateRequestStagingDirectory(request);
        await File.WriteAllTextAsync(Path.Combine(staging, "setup.log"), "keep source");
        string archive = Path.Combine(paths.InstallerLogDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        string oversized = Path.Combine(archive, "log-00.log");
        await using (FileStream stream = new(oversized, FileMode.CreateNew))
        {
            stream.SetLength(BrokerInstallerLogRetention.MaximumLogBytes + 1);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new BrokerInstallerLogRetention(paths).RetainAsync(request));
        Assert.Equal("keep source", await File.ReadAllTextAsync(Path.Combine(staging, "setup.log")));
        Assert.True(File.Exists(oversized));
    }

    [Fact]
    public async Task Retention_RejectsForeignArchiveFilesAndInvalidRequest()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid request = Guid.NewGuid();
        paths.CreateRequestStagingDirectory(request);
        Directory.CreateDirectory(paths.InstallerLogDirectory);
        string foreign = Path.Combine(paths.InstallerLogDirectory, "not-a-request.log");
        await File.WriteAllTextAsync(foreign, "preserve");
        BrokerInstallerLogRetention retention = new(paths);
        await Assert.ThrowsAsync<InvalidDataException>(() => retention.RetainAsync(request));
        Assert.Equal("preserve", await File.ReadAllTextAsync(foreign));
        await Assert.ThrowsAsync<ArgumentException>(() => retention.RetainAsync(Guid.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Unlike symbolic links, directory junctions need no developer-mode or
        // administrator privilege and still exercise the real reparse guard.
        ProcessStartInfo startInfo = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(10000), "The test directory junction could not be created in time.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
