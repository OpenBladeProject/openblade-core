using System.ComponentModel;
using System.Text.Json;
using OpenBlade.Service;
using OpenBlade.WriterFence.Scratch;

namespace OpenBlade.Service.Tests;

public sealed class ServiceRunMarkerWriterLeaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Uptime = TimeSpan.FromHours(1);
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"openblade-run-marker-{Guid.NewGuid():N}");
    private readonly string rootId;
    private readonly string lockId;

    public ServiceRunMarkerWriterLeaseTests()
    {
        Directory.CreateDirectory(root);
        using var pinned = PinnedRoot.OpenPath(root);
        rootId = pinned.Info.Identity;
        using var file = Native.OpenRelative(pinned.Handle, WriterLease.Name, directory: false, create: true);
        lockId = Native.Inspect(file, directory: false).Identity;
    }

    private string MarkerPath => Path.Combine(root, "service-running.json");

    [Fact]
    public async Task BothOperationsRequireTheNativeLeaseAndReleaseItAfterSuccess()
    {
        var store = CreateStore(now: () =>
        {
            var error = Assert.Throws<Win32Exception>(() => Acquire());
            Assert.Equal(32, error.NativeErrorCode);
            return Now;
        });

        using (Acquire())
        {
            await AssertContendedAsync(() => store.BeginRunAsync(CancellationToken.None));
            Assert.Equal([WriterLease.Name], Directory.GetFiles(root).Select(Path.GetFileName));
        }

        Assert.Equal(ServiceStartupKind.Normal, await store.BeginRunAsync(CancellationToken.None));
        byte[] marker = await File.ReadAllBytesAsync(MarkerPath);
        using (Acquire())
        {
            await AssertContendedAsync(() => store.CompleteRunAsync(CancellationToken.None));
            Assert.Equal(marker, await File.ReadAllBytesAsync(MarkerPath));
            Assert.False(File.Exists(MarkerPath + ".tmp"));
        }

        await store.CompleteRunAsync(CancellationToken.None);
        using (Acquire())
        {
            Assert.Equal([WriterLease.Name], Directory.GetFiles(root).Select(Path.GetFileName));
        }
    }

    [Fact]
    public async Task StaleOrUnstartedStoreCannotRemoveASuccessorMarkerEvenWithIdenticalClocks()
    {
        var first = CreateStore();
        var second = CreateStore();
        await first.BeginRunAsync(CancellationToken.None);
        byte[] firstMarker = await File.ReadAllBytesAsync(MarkerPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.BeginRunAsync(CancellationToken.None));
        Assert.Equal(firstMarker, await File.ReadAllBytesAsync(MarkerPath));
        Assert.Equal(ServiceStartupKind.UnexpectedExitRecovery,
            await second.BeginRunAsync(CancellationToken.None));
        byte[] secondMarker = await File.ReadAllBytesAsync(MarkerPath);
        Assert.False(firstMarker.AsSpan().SequenceEqual(secondMarker));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.CompleteRunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore().CompleteRunAsync(CancellationToken.None));
        Assert.Equal(secondMarker, await File.ReadAllBytesAsync(MarkerPath));

        await second.CompleteRunAsync(CancellationToken.None);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task CanceledOrDeniedDeletionPreservesTheMarkerAndReleasesTheLease()
    {
        var store = CreateStore();
        await store.BeginRunAsync(CancellationToken.None);
        byte[] marker = await File.ReadAllBytesAsync(MarkerPath);
        using var cancellation = new CancellationTokenSource();
        var canceled = CreateStore(acquire: (path, token) =>
        {
            IDisposable lease = Acquire(path, token);
            cancellation.Cancel();
            return lease;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.CompleteRunAsync(cancellation.Token));

        // Read is allowed, but the Windows sharing mode denies deletion.
        using (File.Open(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.CompleteRunAsync(CancellationToken.None));
        }

        using (Acquire())
        {
            Assert.Equal(marker, await File.ReadAllBytesAsync(MarkerPath));
        }
        Assert.Equal(ServiceStartupKind.UnexpectedExitRecovery,
            await CreateStore().BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedLeaseReleaseLeavesTheWrittenMarkerWithoutCleanupAuthority()
    {
        bool failRelease = true;
        var store = CreateStore(acquire: (path, token) =>
        {
            IDisposable lease = Acquire(path, token);
            return failRelease ? new FailedRelease(lease) : lease;
        });

        await Assert.ThrowsAsync<IOException>(() => store.BeginRunAsync(CancellationToken.None));
        byte[] marker = await File.ReadAllBytesAsync(MarkerPath);
        failRelease = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteRunAsync(CancellationToken.None));
        Assert.Equal(marker, await File.ReadAllBytesAsync(MarkerPath));
    }

    [Fact]
    public async Task FailedReplacementAndUnclearedTempEvidencePreserveThePreviousMarker()
    {
        var previous = CreateStore();
        await previous.BeginRunAsync(CancellationToken.None);
        byte[] marker = await File.ReadAllBytesAsync(MarkerPath);
        // The target can be read but cannot be replaced while this handle is open.
        using (File.Open(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? error = await Record.ExceptionAsync(() => CreateStore().BeginRunAsync(CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        string temp = MarkerPath + ".tmp";
        byte[] unfinished = await File.ReadAllBytesAsync(temp);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore().BeginRunAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => previous.CompleteRunAsync(CancellationToken.None));
        using (Acquire())
        {
            Assert.Equal(marker, await File.ReadAllBytesAsync(MarkerPath));
            Assert.Equal(unfinished, await File.ReadAllBytesAsync(temp));
        }
    }

    [Fact]
    public async Task BackendTransitionRequiresIndependentRetirementAndClearMarkerEvidence()
    {
        Guid outgoingGeneration = Guid.NewGuid();
        Guid? receipt = Guid.NewGuid();
        var incoming = CreateStore(requireRetiredBackend: () =>
        {
            var error = Assert.Throws<Win32Exception>(() => Acquire());
            Assert.Equal(32, error.NativeErrorCode);
            if (receipt != outgoingGeneration)
            {
                throw new InvalidOperationException("Outgoing generation retirement is unconfirmed.");
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => incoming.BeginRunAsync(CancellationToken.None));
        Assert.False(File.Exists(MarkerPath));
        receipt = outgoingGeneration;

        var outgoing = CreateStore();
        await outgoing.BeginRunAsync(CancellationToken.None);
        byte[] marker = await File.ReadAllBytesAsync(MarkerPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => incoming.BeginRunAsync(CancellationToken.None));
        Assert.Equal(marker, await File.ReadAllBytesAsync(MarkerPath));
        await outgoing.CompleteRunAsync(CancellationToken.None);

        string temp = MarkerPath + ".tmp";
        await File.WriteAllTextAsync(temp, "unfinished outgoing marker");
        await Assert.ThrowsAsync<InvalidOperationException>(() => incoming.BeginRunAsync(CancellationToken.None));
        Assert.Equal("unfinished outgoing marker", await File.ReadAllTextAsync(temp));
        File.Delete(temp);

        Assert.Equal(ServiceStartupKind.Normal, await incoming.BeginRunAsync(CancellationToken.None));
        await incoming.CompleteRunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnboundFormatRemainsVersionTwoAndBothFormsSupportUnexpectedExitRecovery()
    {
        var legacy = new ServiceRunMarkerStore(MarkerPath, () => Now, () => Uptime);
        await legacy.BeginRunAsync(CancellationToken.None);
        using (JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(MarkerPath)))
        {
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(["schemaVersion", "startedAtUtc", "uptimeAtStart"],
                json.RootElement.EnumerateObject().Select(property => property.Name));
        }

        Assert.Equal(ServiceStartupKind.UnexpectedExitRecovery,
            await CreateStore().BeginRunAsync(CancellationToken.None));
        Assert.Equal(ServiceStartupKind.UnexpectedExitRecovery,
            await legacy.BeginRunAsync(CancellationToken.None));
    }

    private ServiceRunMarkerStore CreateStore(
        Func<DateTimeOffset>? now = null,
        Func<string, CancellationToken, IDisposable>? acquire = null,
        Action? requireRetiredBackend = null) =>
        new(MarkerPath, now ?? (() => Now), () => Uptime, acquire ?? Acquire, requireRetiredBackend);

    private WriterLease Acquire() => WriterLease.AcquireExactRoot(root, rootId, lockId);

    private IDisposable Acquire(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(root, path);
        return Acquire();
    }

    private static async Task AssertContendedAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<Win32Exception>(action);
        Assert.Equal(32, error.NativeErrorCode);
    }

    private sealed class FailedRelease(IDisposable lease) : IDisposable
    {
        public void Dispose()
        {
            lease.Dispose();
            throw new IOException("Lease release was not confirmed.");
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
