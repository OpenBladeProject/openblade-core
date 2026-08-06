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

    [Fact]
    public async Task MarkerStartedBeforeCurrentBootUsesNormalStartup()
    {
        string path = Path.Combine(directory, "service-running.json");
        DateTimeOffset priorBootRun = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var priorRun = new ServiceRunMarkerStore(
            path,
            () => priorBootRun,
            () => TimeSpan.FromHours(2));
        Assert.Equal(
            ServiceStartupKind.Normal,
            await priorRun.BeginRunAsync(CancellationToken.None));
        DateTimeOffset afterRestart = new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
        var restarted = new ServiceRunMarkerStore(
            path,
            () => afterRestart,
            () => TimeSpan.FromMinutes(5));

        Assert.Equal(
            ServiceStartupKind.Normal,
            await restarted.BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MarkerStartedDuringCurrentBootUsesUnexpectedExitRecovery()
    {
        string path = Path.Combine(directory, "service-running.json");
        DateTimeOffset firstStart = new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);
        var priorRun = new ServiceRunMarkerStore(
            path,
            () => firstStart,
            () => TimeSpan.FromHours(1));
        Assert.Equal(
            ServiceStartupKind.Normal,
            await priorRun.BeginRunAsync(CancellationToken.None));
        DateTimeOffset restartedAt = firstStart.AddMinutes(10);
        var restarted = new ServiceRunMarkerStore(
            path,
            () => restartedAt,
            () => TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(10)));

        Assert.Equal(
            ServiceStartupKind.UnexpectedExitRecovery,
            await restarted.BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ForwardClockCorrectionDuringCurrentBootRemainsUnexpected()
    {
        string path = Path.Combine(directory, "service-running.json");
        DateTimeOffset firstStart = new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);
        var priorRun = new ServiceRunMarkerStore(
            path,
            () => firstStart,
            () => TimeSpan.FromSeconds(30));
        Assert.Equal(
            ServiceStartupKind.Normal,
            await priorRun.BeginRunAsync(CancellationToken.None));
        var restarted = new ServiceRunMarkerStore(
            path,
            () => firstStart.AddMinutes(6),
            () => TimeSpan.FromMinutes(1));

        Assert.Equal(
            ServiceStartupKind.UnexpectedExitRecovery,
            await restarted.BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RebootWithAmbiguousWallClockFailsClosed()
    {
        string path = Path.Combine(directory, "service-running.json");
        DateTimeOffset firstStart = new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);
        var priorRun = new ServiceRunMarkerStore(
            path,
            () => firstStart,
            () => TimeSpan.FromHours(2));
        Assert.Equal(
            ServiceStartupKind.Normal,
            await priorRun.BeginRunAsync(CancellationToken.None));
        var restarted = new ServiceRunMarkerStore(
            path,
            () => firstStart.AddMinutes(-30),
            () => TimeSpan.FromMinutes(5));

        Assert.Equal(
            ServiceStartupKind.UnexpectedExitRecovery,
            await restarted.BeginRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VersionOneMarkerFailsClosedDuringSchemaUpgrade()
    {
        string path = Path.Combine(directory, "service-running.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "startedAtUtc": "2026-08-05T12:00:00+00:00"
            }
            """);
        var store = new ServiceRunMarkerStore(
            path,
            () => new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero),
            () => TimeSpan.FromMinutes(5));

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
