using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveCurveOwnershipJournalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBladeLiveCurveJournalTests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(directory, "live-co.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PendingSignedBaselineAndExactContextSurviveRestart()
    {
        var original = Create();
        var journal = original.Progress(CpuLiveCurveOwnershipState.PossibleSubmission, 1, restoring: true);
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        string? hash = await store.TryWriteAsync(null, journal, default);
        var loaded = await new CpuLiveCurveOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.NotNull(hash); Assert.False(loaded.Invalid); Assert.Equal(hash, loaded.Sha256);
        Assert.Equal(journal.Context, loaded.Journal!.Context);
        Assert.Equal(journal.Baseline, loaded.Journal.Baseline);
        Assert.Equal(journal.Expected, loaded.Journal.Expected);
        Assert.Equal(journal.Steps, loaded.Journal.Steps);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, loaded.Journal.State);
        Assert.True(loaded.Journal.Restoring); Assert.Equal(1, loaded.Journal.CompletedSteps);
        Assert.Equal(short.MinValue, loaded.Journal.Baseline[0].Offset);
        Assert.Equal(short.MaxValue, loaded.Journal.Baseline[1].Offset);
    }

    [Fact]
    public async Task CompareAndSwapAcrossStoreInstancesHasOnlyOneWinner()
    {
        var firstStore = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        var journal = Create();
        string? original = await firstStore.TryWriteAsync(null, journal, default);
        var pending = journal.Progress(CpuLiveCurveOwnershipState.PossibleSubmission, 0, false);
        var blocked = journal.Progress(CpuLiveCurveOwnershipState.Blocked, 0, false);
        var results = await Task.WhenAll(firstStore.TryWriteAsync(original, pending, default),
            new CpuLiveCurveOwnershipJournalStore(JournalPath).TryWriteAsync(original, blocked, default));
        Assert.Single(results, hash => hash is not null);
        Assert.Null(await firstStore.TryWriteAsync(original, journal, default));
        Assert.Null(await firstStore.TryWriteAsync(null, journal, default));
        Assert.False((await firstStore.LoadAsync(default)).Invalid);
    }

    [Theory]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("nested-duplicate")]
    [InlineData("property-case")]
    [InlineData("schema")]
    [InlineData("progress")]
    [InlineData("state")]
    [InlineData("expected")]
    [InlineData("reverse")]
    [InlineData("step-order")]
    [InlineData("target-positive")]
    [InlineData("duplicate-core")]
    [InlineData("unknown-boot")]
    [InlineData("backend")]
    [InlineData("bios")]
    [InlineData("missing-field")]
    public async Task StrictSyntaxAndDerivedSemanticsRejectTampering(string fault)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Create(), JsonOptions))!;
        switch (fault)
        {
            case "unknown-property": node["extra"] = 1; break;
            case "property-case": node["SchemaVersion"] = 1; break;
            case "schema": node["schemaVersion"] = 2; break;
            case "progress": node["completedSteps"] = 13; break;
            case "state": node["state"] = 999; break;
            case "expected": node["expected"]![0]!["offset"] = 1; break;
            case "reverse": node["steps"]![0]!["before"] = 1; break;
            case "step-order": node["steps"]![0]!["physicalCoreId"] = 15; break;
            case "target-positive": node["steps"]![0]!["after"] = 1; break;
            case "duplicate-core": node["baseline"]![0]!["physicalCoreId"] = 2; break;
            case "unknown-boot": node["context"]!["bootId"] = Guid.Empty; break;
            case "backend": node["context"]!["backend"]!["driverSha256"] = "other"; break;
            case "bios": node["context"]!["identity"]!["biosVersion"] = "2.03"; break;
            case "missing-field": node.AsObject().Remove("restoring"); break;
        }
        string json = node.ToJsonString();
        if (fault == "duplicate-property") { json = json.Insert(1, "\"schemaVersion\":1,"); }
        if (fault == "nested-duplicate") { json = json.Replace("\"physicalCoreId\":0", "\"physicalCoreId\":0,\"physicalCoreId\":0", StringComparison.Ordinal); }
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(JournalPath, json);
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        Assert.True((await store.LoadAsync(default)).Invalid);
        Assert.Null(await store.TryWriteAsync(null, Create(), default));
        Assert.Equal(json, await File.ReadAllTextAsync(JournalPath));
    }

    [Fact]
    public async Task MissingUnreadableMalformedOversizeAndCorruptStatesAreDistinct()
    {
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        var absent = await store.LoadAsync(default);
        Assert.False(absent.Invalid); Assert.Null(absent.Journal);
        Directory.CreateDirectory(directory);
        foreach (var content in new[] { "", "{}", "null", "{bad", new string('x', 32769) })
        {
            await File.WriteAllTextAsync(JournalPath, content);
            Assert.True((await store.LoadAsync(default)).Invalid);
            Assert.Null(await store.TryWriteAsync(null, Create(), default));
        }
        await File.WriteAllTextAsync(JournalPath, JsonSerializer.Serialize(Create(), JsonOptions));
        using var held = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True((await store.LoadAsync(default)).Invalid);
        Assert.Null(await store.TryWriteAsync(null, Create(), default));
    }

    [Fact]
    public async Task JournalCannotAliasCallerOwnedArraysOrBeOverwrittenWhileLocked()
    {
        var original = Create();
        var baseline = original.Baseline.ToArray(); var expected = original.Expected.ToArray(); var steps = original.Steps.ToArray();
        var journal = new CpuLiveCurveOwnershipJournal(1, original.TransactionId, original.Context, baseline, steps, expected,
            original.State, original.CompletedSteps, original.Restoring, original.UpdatedAtUtc);
        baseline[0] = new(0, 9); expected[0] = new(0, 9); steps[0] = new(0, 9, 0);
        Assert.True(journal.IsWellFormed); Assert.Equal(original.Baseline, journal.Baseline);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLiveCoreOffset>)journal.Baseline)[0] = new(0, 2));
        Directory.CreateDirectory(directory);
        using var held = new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(await new CpuLiveCurveOwnershipJournalStore(JournalPath).TryWriteAsync(null, journal, default));
        Assert.False(File.Exists(JournalPath));
    }

    private static CpuLiveCurveOwnershipJournal Create()
    {
        var cores = CpuLiveCurveTuningTransactionTests.ValuesFixture();
        cores[0] = new(0, short.MinValue); cores[1] = new(2, short.MaxValue);
        var plan = Rz090528CpuLiveCurvePlan.CreateAllCore(new(0xFF55, 0x005D000B, 0xD50, cores), new(true, -2), (_, _) => true).Plan!;
        return CpuLiveCurveOwnershipJournal.Prepare(CpuLiveCurveTuningTransactionTests.ContextFixture(), plan);
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
