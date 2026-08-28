using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerInFlightStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.DeploymentBroker.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_CreateBindRestartAndClearAreRequestBound()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerInFlightStore store = new(paths);
        Guid requestId = Guid.NewGuid();
        DateTimeOffset created = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

        BrokerInFlightDeployment initial = await store.CreateOnceAsync(requestId, created);
        Assert.Null(initial.InstallerProcessId);
        Assert.Null(initial.InstallerSha256);
        store.BindPreviousState(
            requestId,
            new BrokerInstalledState("1.0.0", "1.0.0+" + new string('b', 40), new string('c', 64)),
            created.AddMilliseconds(500));
        store.BindRelease(
            requestId,
            new string('a', 64),
            "65535.1.2.3",
            new string('d', 40),
            new string('e', 64),
            new string('f', 64),
            1,
            created.AddSeconds(1));
        store.BindInstallerProcess(requestId, 4242, created.AddSeconds(2));

        BrokerInFlightDeployment restarted = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(paths).LoadAsync());
        Assert.Equal(requestId, restarted.RequestId);
        Assert.Equal(4242, restarted.InstallerProcessId);
        Assert.Null(restarted.InstallerExitCode);
        Assert.Equal(new string('a', 64), restarted.InstallerSha256);
        Assert.Equal("65535.1.2.3", restarted.TargetVersion);
        Assert.Equal(new string('c', 64), restarted.PreviousPayloadSha256);
        Assert.Equal(new string('d', 40), restarted.TargetSourceRevision);
        Assert.Throws<UnauthorizedAccessException>(() => store.ClearResolved(Guid.NewGuid()));
        Assert.True(File.Exists(paths.InFlightPath));

        store.ClearResolved(requestId);
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Store_ObservedInstallerResultIsDurableAndExactlyRequestBound()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerInFlightStore store = new(paths);
        Guid requestId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        await store.CreateOnceAsync(requestId, now);
        store.BindPreviousState(
            requestId,
            new BrokerInstalledState("1.0.0", "1.0.0+" + new string('b', 40), new string('c', 64)),
            now.AddSeconds(1));
        store.BindRelease(
            requestId,
            new string('a', 64),
            "65535.1.2.3",
            new string('d', 40),
            new string('e', 64),
            new string('f', 64),
            1,
            now.AddSeconds(2));
        store.BindInstallerProcess(requestId, 4242, now.AddSeconds(3));

        Assert.Throws<UnauthorizedAccessException>(() =>
            store.BindInstallerResult(Guid.NewGuid(), 0, now.AddSeconds(4)));
        store.BindInstallerResult(requestId, 0, now.AddSeconds(4));
        Assert.Throws<InvalidDataException>(() =>
            store.BindInstallerResult(requestId, 1, now.AddSeconds(5)));

        BrokerInFlightDeployment restarted = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(paths).LoadAsync());
        Assert.Equal(0, restarted.InstallerExitCode);
        Assert.Equal(BrokerInFlightDeploymentCodec.CurrentSchemaVersion, restarted.SchemaVersion);
    }

    [Fact]
    public async Task Store_CollisionPreservesOriginalAndRejectsSecondRequest()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        BrokerInFlightStore store = new(paths);
        Guid originalRequest = Guid.NewGuid();
        DateTimeOffset now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        await store.CreateOnceAsync(originalRequest, now);
        byte[] originalBytes = await File.ReadAllBytesAsync(paths.InFlightPath);

        await Assert.ThrowsAsync<IOException>(() =>
            store.CreateOnceAsync(Guid.NewGuid(), now.AddSeconds(1)));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(paths.InFlightPath));
        Assert.Equal(originalRequest, (await store.LoadAsync())!.RequestId);
        Assert.Empty(Directory.GetFiles(root, "unresolved-deployment.*.tmp"));
    }

    [Fact]
    public async Task Store_PrelaunchCrashStateRemainsDurablyAmbiguousAcrossRestart()
    {
        BrokerPaths paths = BrokerPaths.CreateForTests(root);
        Guid requestId = Guid.NewGuid();
        await new BrokerInFlightStore(paths).CreateOnceAsync(
            requestId,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

        BrokerInFlightDeployment recovered = Assert.IsType<BrokerInFlightDeployment>(
            await new BrokerInFlightStore(paths).LoadAsync());

        Assert.Equal(requestId, recovered.RequestId);
        Assert.Null(recovered.InstallerProcessId);
        Assert.Null(recovered.TargetVersion);
        Assert.True(File.Exists(paths.InFlightPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
