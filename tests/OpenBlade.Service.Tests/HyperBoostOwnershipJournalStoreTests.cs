using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class HyperBoostOwnershipJournalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.HyperBoostOwnershipJournalStoreTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData((int)HyperBoostOwnershipJournalState.EnablePending)]
    [InlineData((int)HyperBoostOwnershipJournalState.OwnedConfirmed)]
    [InlineData((int)HyperBoostOwnershipJournalState.RestorePending)]
    [InlineData((int)HyperBoostOwnershipJournalState.Relinquished)]
    public async Task WriteAndLoad_RoundTripsExactOwnershipState(
        int stateValue)
    {
        var state = (HyperBoostOwnershipJournalState)stateValue;
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        var expected = new HyperBoostOwnershipJournal(
            state,
            PerformancePreset.Performance,
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        await store.WriteAsync(expected, CancellationToken.None);
        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.Equal(expected, loaded.Journal);
    }

    [Fact]
    public async Task Load_MalformedJournalFailsClosedWithoutDeletingEvidence()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllTextAsync(path, "{ not-json", CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.Invalid);
        Assert.Null(loaded.Journal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Load_SchemaOneDefaultsToPadRequired()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "state": 1,
              "baseMode": 1,
              "updatedAtUtc": "2026-08-11T12:00:00Z"
            }
            """,
            CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.True(Assert.IsType<HyperBoostOwnershipJournal>(loaded.Journal)
            .RequiresCoolingPad);
    }

    [Theory]
    [InlineData(0, 1, "2026-08-11T12:00:00Z")]
    [InlineData(1, 99, "2026-08-11T12:00:00Z")]
    [InlineData(1, 1, "0001-01-01T00:00:00Z")]
    public async Task Load_InvalidSemanticDocumentFailsClosed(
        int schemaVersion,
        int state,
        string updatedAtUtc)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        string json = $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "state": {{state}},
              "baseMode": 1,
              "requiresCoolingPad": true,
              "updatedAtUtc": "{{updatedAtUtc}}"
            }
            """;
        await File.WriteAllTextAsync(path, json, CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.Invalid);
        Assert.Null(loaded.Journal);
    }

    [Fact]
    public async Task Clear_RemovesJournalAndReturnsEmptyState()
    {
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        await store.WriteAsync(
            new HyperBoostOwnershipJournal(
                HyperBoostOwnershipJournalState.OwnedConfirmed,
                PerformancePreset.Balanced,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);
        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.Null(loaded.Journal);
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
