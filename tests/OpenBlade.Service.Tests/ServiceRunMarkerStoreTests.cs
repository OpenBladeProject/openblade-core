using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceRunMarkerStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.ServiceRunMarkerStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnclearedMarkerClassifiesOnlyTheNextRunAsUnexpected()
    {
        string path = Path.Combine(directory, "service-running.json");
        var store = new ServiceRunMarkerStore(path);

        Assert.Equal(
            ServiceStartupKind.Normal,
            await store.BeginRunAsync(CancellationToken.None));
        Assert.Equal(
            ServiceStartupKind.UnexpectedExitRecovery,
            await store.BeginRunAsync(CancellationToken.None));

        await store.CompleteRunAsync(CancellationToken.None);

        Assert.Equal(
            ServiceStartupKind.Normal,
            await store.BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CanceledCompletionRetainsRecoveryEvidence()
    {
        string path = Path.Combine(directory, "service-running.json");
        var store = new ServiceRunMarkerStore(path);
        await store.BeginRunAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CompleteRunAsync(cancellation.Token));

        Assert.Equal(
            ServiceStartupKind.UnexpectedExitRecovery,
            await store.BeginRunAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
