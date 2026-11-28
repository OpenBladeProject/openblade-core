using System.ComponentModel;
using OpenBlade.Service;
using OpenBlade.WriterFence.Scratch;

namespace OpenBlade.Service.Tests;

public sealed class MachineProfileBootstrapperTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Service.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeAsync_PersistsSafeDefaultsWhenProfileFileIsMissing()
    {
        Paths paths = CreatePaths();
        MachineProfileBootstrapper bootstrapper = new(CreateStore(paths));

        BatteryConfiguration result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.All(paths.All, path => Assert.True(File.Exists(path)));
        Assert.Equal(result, await CreateStore(paths).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_PreservesCorruptProfileInQuarantine()
    {
        Directory.CreateDirectory(directory);
        Paths paths = CreatePaths();
        await File.WriteAllTextAsync(paths.Profiles, "{bad json");
        MachineProfileBootstrapper bootstrapper = new(CreateStore(paths));

        BatteryConfiguration result = await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.Equal(ConfigurationLoadDisposition.Quarantined, result.Disposition);
        Assert.Equal("{bad json", await File.ReadAllTextAsync(paths.Profiles));
        Assert.False(File.Exists(paths.Preferences));
        Assert.False(File.Exists(paths.Marker));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InitializeAsync_HoldsExactLeaseThroughVerificationAndPublishesReadableState(
        bool legacy, bool cancelAfterCommit)
    {
        var writer = new ScratchConfigurationWriter(directory);
        Paths paths = CreatePaths();
        if (legacy)
        {
            await File.WriteAllTextAsync(paths.Profiles, BatteryConfigurationStoreTests.LegacyJson.Replace(
                "80", "75", StringComparison.Ordinal));
        }
        using var cancellation = new CancellationTokenSource();
        bool verified = false;
        var store = CreateStore(paths, (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.AfterVerification)
            {
                Win32Exception error = Assert.Throws<Win32Exception>(() => writer.Acquire());
                Assert.Equal(32, error.NativeErrorCode);
                verified = true;
                if (cancelAfterCommit)
                {
                    cancellation.Cancel();
                }
            }
            return Task.CompletedTask;
        });

        BatteryConfiguration result = await new MachineProfileBootstrapper(store, directory, writer.Acquire)
            .InitializeAsync(cancellation.Token);

        Assert.True(verified);
        Assert.True(result.IsAuthoritative);
        Assert.False(result.Recovered);
        Assert.Equal(legacy ? 75 : 80, result.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(paths.All.Append(Path.Combine(directory, WriterLease.Name)).Order(),
            Directory.GetFileSystemEntries(directory).Order());
        using IDisposable successor = writer.Acquire();
        Assert.Equal(result, (await CreateStore(paths).InspectAsync(CancellationToken.None)).Configuration);
    }

    [Fact]
    public async Task InitializeAsync_ContendedLeaseDoesNotMaterializeDefaults()
    {
        var writer = new ScratchConfigurationWriter(directory);
        var bootstrapper = new MachineProfileBootstrapper(CreateStore(CreatePaths()), directory, writer.Acquire);
        using IDisposable current = writer.Acquire();

        await UserConfigurationWriterLeaseTests.AssertContendedAsync(
            () => bootstrapper.InitializeAsync(CancellationToken.None));

        Assert.Equal([WriterLease.Name], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task InitializeAsync_UnresolvedRecoveryPreservesEveryByteAndReleasesLease()
    {
        var writer = new ScratchConfigurationWriter(directory);
        Paths paths = CreatePaths();
        await CreateStore(paths).LoadAsync(CancellationToken.None);
        await File.WriteAllTextAsync(paths.Marker + ".recovery-required", "unresolved");
        await File.WriteAllBytesAsync(paths.Profiles + ".transaction-existing.backup", [1, 2, 3]);
        var prior = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
        var bootstrapper = new MachineProfileBootstrapper(CreateStore(paths), directory, writer.Acquire);

        ConfigurationQuarantinedException error = await Assert.ThrowsAsync<ConfigurationQuarantinedException>(
            () => bootstrapper.InitializeAsync(CancellationToken.None));

        Assert.Contains(paths.Marker + ".recovery-required", error.Message, StringComparison.Ordinal);
        Assert.Equal(prior.Keys.Order(), Directory.GetFiles(directory).Order());
        foreach ((string file, byte[] bytes) in prior)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        }
        using IDisposable successor = writer.Acquire();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitializeAsync_RollsBackAfterReplacementAndReleasesLease(bool cancel, bool legacy)
    {
        var writer = new ScratchConfigurationWriter(directory);
        Paths paths = CreatePaths();
        byte[]? legacyBytes = null;
        if (legacy)
        {
            await File.WriteAllTextAsync(paths.Profiles, BatteryConfigurationStoreTests.LegacyJson);
            legacyBytes = await File.ReadAllBytesAsync(paths.Profiles);
        }
        using var cancellation = new CancellationTokenSource();
        var store = CreateStore(paths, (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.AfterProfileReplacement)
            {
                if (!cancel)
                {
                    throw new IOException("Injected transaction failure.");
                }
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        });
        var bootstrapper = new MachineProfileBootstrapper(store, directory, writer.Acquire);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => bootstrapper.InitializeAsync(cancellation.Token));
        }
        else
        {
            IOException error = await Assert.ThrowsAsync<IOException>(
                () => bootstrapper.InitializeAsync(cancellation.Token));
            Assert.Equal("Injected transaction failure.", error.Message);
        }

        using IDisposable successor = writer.Acquire();
        string lockPath = Path.Combine(directory, WriterLease.Name);
        if (legacyBytes is not null)
        {
            Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(paths.Profiles));
            Assert.Equal(new[] { lockPath, paths.Profiles }.Order(), Directory.GetFileSystemEntries(directory).Order());
        }
        else
        {
            Assert.Equal([lockPath], Directory.GetFileSystemEntries(directory));
        }
    }

    [Fact]
    public async Task InitializeAsync_CanceledDuringAcquisitionReleasesLeaseWithoutWriting()
    {
        var writer = new ScratchConfigurationWriter(directory);
        using var cancellation = new CancellationTokenSource();
        var bootstrapper = new MachineProfileBootstrapper(
            CreateStore(CreatePaths()),
            directory,
            (path, token) =>
            {
                IDisposable lease = writer.Acquire(path, token);
                cancellation.Cancel();
                return lease;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bootstrapper.InitializeAsync(cancellation.Token));

        using IDisposable successor = writer.Acquire();
        Assert.Equal([WriterLease.Name], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task InitializeAsync_RejectsCrossRootTargetBeforeAcquisition()
    {
        Paths paths = CreatePaths();
        string otherRoot = Path.Combine(directory, "other");
        var store = CreateStore(paths with { Preferences = Path.Combine(otherRoot, "battery-preferences.json") });
        var bootstrapper = new MachineProfileBootstrapper(
            store,
            directory,
            (_, _) => throw new Xunit.Sdk.XunitException("Cross-root tuple reached lease acquisition."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.InitializeAsync(CancellationToken.None));

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task InitializeAsync_RejectsNonFileTargetBeforeCreatingArtifacts()
    {
        var writer = new ScratchConfigurationWriter(directory);
        Paths paths = CreatePaths();
        Directory.CreateDirectory(paths.Preferences);
        var bootstrapper = new MachineProfileBootstrapper(CreateStore(paths), directory, writer.Acquire);

        await Assert.ThrowsAsync<IOException>(() => bootstrapper.InitializeAsync(CancellationToken.None));

        using IDisposable successor = writer.Acquire();
        Assert.Equal([WriterLease.Name], Directory.GetFiles(directory).Select(Path.GetFileName));
        Assert.Equal([paths.Preferences], Directory.GetDirectories(directory));
    }

    private Paths CreatePaths() => new(
        Path.Combine(directory, "profiles.json"),
        Path.Combine(directory, "battery-preferences.json"),
        Path.Combine(directory, "battery-configuration.commit.json"));

    private static BatteryConfigurationStore CreateStore(
        Paths paths,
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>? transactionBoundary = null) =>
        new(paths.Profiles, paths.Preferences, paths.Marker, validator: null, postCommitBoundary: null,
            transactionBoundary);

    private sealed record Paths(string Profiles, string Preferences, string Marker)
    {
        public string[] All => [Profiles, Preferences, Marker];
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
