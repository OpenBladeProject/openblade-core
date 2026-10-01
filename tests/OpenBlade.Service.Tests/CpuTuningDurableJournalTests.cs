using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningDurableJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBladeCpuJournalTests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(directory, "ownership.json");

    [Fact]
    public async Task FullImagesTypedCommandsAndPossibleSubmissionSurviveRestart()
    {
        var store = new CpuTuningOwnershipJournalStore(JournalPath);
        CpuTuningOwnershipJournal journal = Create() with { State = CpuTuningOwnershipState.PossibleSubmission };

        string? hash = await store.TryWriteAsync(null, journal, CancellationToken.None);
        CpuTuningOwnershipJournalLoadResult loaded = await new CpuTuningOwnershipJournalStore(JournalPath).LoadAsync(CancellationToken.None);

        Assert.NotNull(hash);
        Assert.False(loaded.Invalid);
        Assert.Equal(hash, loaded.Sha256);
        Assert.Equal(journal.BaselineCurrent, loaded.Journal!.BaselineCurrent);
        Assert.Equal(journal.BaselineLast, loaded.Journal.BaselineLast);
        Assert.Equal(journal.Settings, loaded.Journal.Settings);
        Assert.Equal(journal.State, loaded.Journal.State);
        Assert.Equal(journal.Steps[0].Command, loaded.Journal.Steps[0].Command);
        Assert.Equal(journal.Steps[0].RestoreCommand, loaded.Journal.Steps[0].RestoreCommand);
        Assert.Equal(journal.Steps[0].After, loaded.Journal.Steps[0].After);
    }

    [Fact]
    public async Task MissingJournalIsDistinctFromMalformedAndOversizedJournal()
    {
        var store = new CpuTuningOwnershipJournalStore(JournalPath);
        Assert.False((await store.LoadAsync(CancellationToken.None)).Invalid);
        Directory.CreateDirectory(directory);
        foreach (string contents in new[] { "", "{}", "{invalid", new string('x', 32769) })
        {
            await File.WriteAllTextAsync(JournalPath, contents);
            Assert.True((await store.LoadAsync(CancellationToken.None)).Invalid);
            Assert.Null(await store.TryWriteAsync(null, Create(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task BlockedOwnershipRetainsOriginalImagesAcrossDiskStoreRestart()
    {
        var journal = Create() with { State = CpuTuningOwnershipState.OwnedVerified, CompletedSteps = 1 };
        var store = new CpuTuningOwnershipJournalStore(JournalPath);
        string? ownedHash = await store.TryWriteAsync(null, journal, default);
        string? blockedHash = await store.TryWriteAsync(ownedHash, journal with { State = CpuTuningOwnershipState.Blocked }, default);
        var loaded = await new CpuTuningOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.False(loaded.Invalid);
        Assert.NotNull(blockedHash);
        Assert.Equal(blockedHash, loaded.Sha256);
        Assert.Equal(CpuTuningOwnershipState.Blocked, loaded.Journal!.State);
        Assert.Equal(journal.BaselineCurrent, loaded.Journal.BaselineCurrent);
        Assert.Equal(journal.BaselineLast, loaded.Journal.BaselineLast);
        Assert.Equal(journal.ExpectedCurrent, loaded.Journal.ExpectedCurrent);
        Assert.Equal(journal.TransactionId, loaded.Journal.TransactionId);
    }

    [Fact]
    public async Task CompareAndSwapRejectsForeignReplacementAndStaleRevision()
    {
        var store = new CpuTuningOwnershipJournalStore(JournalPath);
        CpuTuningOwnershipJournal journal = Create();
        string? first = await store.TryWriteAsync(null, journal, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await store.TryWriteAsync(null, journal, CancellationToken.None));
        string? second = await store.TryWriteAsync(first,
            journal with { State = CpuTuningOwnershipState.PossibleSubmission }, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Null(await store.TryWriteAsync(first, journal, CancellationToken.None));
        await File.WriteAllTextAsync(JournalPath, "foreign state");
        Assert.Null(await store.TryWriteAsync(second, journal, CancellationToken.None));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("expected")]
    [InlineData("command")]
    [InlineData("schema")]
    [InlineData("progress")]
    public async Task JournalSemanticsAreRecomputedAndTamperingFailsClosed(string kind)
    {
        CpuTuningOwnershipJournal journal = Create();
        journal = kind switch
        {
            "baseline" => journal with { BaselineLast = new byte[800] },
            "expected" => journal with { Steps = [journal.Steps[0] with { After = new byte[800] }] },
            "command" => journal with { Steps = [journal.Steps[0] with { RestoreCommand = new(CpuSavedTuningCommandKind.CurveOptimizer, -20) }] },
            "schema" => journal with { SchemaVersion = 2 },
            "progress" => journal with { CompletedSteps = 2 },
            _ => throw new InvalidOperationException(),
        };
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(JournalPath, JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.True((await new CpuTuningOwnershipJournalStore(JournalPath).LoadAsync(CancellationToken.None)).Invalid);
    }

    private static CpuTuningOwnershipJournal Create()
    {
        byte[] baseline = CpuSavedTuningTransactionTests.Baseline();
        var settings = new CpuTuningSettings(null, new(true, -12));
        return new(1, Guid.NewGuid(), new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"), settings,
            baseline, (byte[])baseline.Clone(), CpuSavedTuningPlan.Create(baseline, settings)!,
            CpuTuningOwnershipState.Prepared, 0, false, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
