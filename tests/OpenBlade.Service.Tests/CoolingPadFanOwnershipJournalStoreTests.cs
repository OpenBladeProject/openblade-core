using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadFanOwnershipJournalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.CoolingPadFanOwnershipJournalStoreTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData((int)CoolingPadFanOwnershipJournalState.AcquirePending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.OwnedConfirmed)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.UpdatePending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.HandbackPending)]
    [InlineData((int)CoolingPadFanOwnershipJournalState.Indeterminate)]
    public async Task WriteLoadAndClear_RoundTripsEveryState(int stateValue)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fan-ownership.json");
        var store = new CoolingPadFanOwnershipJournalStore(path);
        var expected = new CoolingPadFanOwnershipJournal(
            (CoolingPadFanOwnershipJournalState)stateValue,
            new CoolingPadFanTarget(2_200),
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        await store.WriteAsync(expected, default);
        CoolingPadFanOwnershipJournalLoadResult loaded =
            await store.LoadAsync(default);

        Assert.False(loaded.Invalid);
        Assert.Equal(expected, loaded.Journal);
        await store.ClearAsync(default);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Load_MalformedDocumentFailsClosed()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fan-ownership.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new CoolingPadFanOwnershipJournalStore(path);

        CoolingPadFanOwnershipJournalLoadResult loaded =
            await store.LoadAsync(default);

        Assert.True(loaded.Invalid);
        Assert.Null(loaded.Journal);
    }

    [Fact]
    public async Task CompositeCleaner_ClearsEveryJournal()
    {
        var first = new RecordingCleaner();
        var second = new RecordingCleaner();
        var cleaner = new CompositeServiceOwnershipJournalCleaner([first, second]);

        await cleaner.ClearAsync(default);

        Assert.Equal(1, first.ClearCalls);
        Assert.Equal(1, second.ClearCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingCleaner : IServiceOwnershipJournalCleaner
    {
        internal int ClearCalls { get; private set; }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCalls++;
            return Task.CompletedTask;
        }
    }
}
