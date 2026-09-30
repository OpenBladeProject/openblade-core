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
        Assert.Equal(journal.OriginalBaseline, loaded.Journal.OriginalBaseline);
        Assert.Equal(journal.OperationBaseline, loaded.Journal.OperationBaseline);
        Assert.Equal(journal.Expected, loaded.Journal.Expected);
        Assert.Equal(journal.Steps, loaded.Journal.Steps);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, loaded.Journal.State);
        Assert.True(loaded.Journal.Restoring); Assert.Equal(1, loaded.Journal.CompletedSteps);
        Assert.Equal(short.MinValue, loaded.Journal.OriginalBaseline[0].Offset);
        Assert.Equal(short.MaxValue, loaded.Journal.OriginalBaseline[1].Offset);
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

    [Fact]
    public async Task RepeatedPreparedOperationRoundTripsBothBaselinesAndArmsEarlierChanges()
    {
        var first = Create();
        var owned = first.Progress(CpuLiveCurveOwnershipState.OwnedVerified, 2, false);
        var plan = Rz090528CpuLiveCurvePlan.CreateSelective(new(0xFF55, 0x005D000B, 0xD50, owned.Expected),
            [new(8, -3)], (_, _) => true).Plan!;
        var prepared = CpuLiveCurveOwnershipJournal.Prepare(owned.Context, plan, owned);
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        Assert.NotNull(await store.TryWriteAsync(null, prepared, default));
        var loaded = (await new CpuLiveCurveOwnershipJournalStore(JournalPath).LoadAsync(default)).Journal!;
        Assert.Equal(first.OriginalBaseline, loaded.OriginalBaseline);
        Assert.Equal(owned.Expected, loaded.OperationBaseline); Assert.Single(loaded.Steps);
        var restoration = loaded.ArmRestoration();
        Assert.True(restoration.IsWellFormed); Assert.True(restoration.Restoring);
        Assert.Equal(new[] { 0, 2 }, restoration.Steps.Select(step => step.PhysicalCoreId));
        Assert.Equal(new[] { short.MinValue, short.MaxValue }, restoration.Steps.Select(step => step.Before));
        Assert.Equal(loaded.OriginalBaseline, restoration.ExpectedAt(0).CoreOffsets);
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
    [InlineData("operation-missing")]
    [InlineData("operation-duplicate")]
    [InlineData("operation-order")]
    [InlineData("operation-unowned-value")]
    [InlineData("operation-before")]
    [InlineData("false-restored")]
    [InlineData("false-restoring")]
    public async Task StrictSyntaxAndDerivedSemanticsRejectTampering(string fault)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Create(), JsonOptions))!;
        switch (fault)
        {
            case "unknown-property": node["extra"] = 1; break;
            case "property-case": node["SchemaVersion"] = 1; break;
            case "schema": node["schemaVersion"] = 1; break;
            case "progress": node["completedSteps"] = 13; break;
            case "state": node["state"] = 999; break;
            case "expected": node["expected"]![0]!["offset"] = 1; break;
            case "reverse": node["steps"]![0]!["before"] = 1; break;
            case "step-order": node["steps"]![0]!["physicalCoreId"] = 15; break;
            case "target-positive": node["steps"]![0]!["after"] = 1; break;
            case "duplicate-core": node["originalBaseline"]![0]!["physicalCoreId"] = 2; break;
            case "unknown-boot": node["context"]!["bootId"] = Guid.Empty; break;
            case "backend": node["context"]!["backend"]!["driverSha256"] = "other"; break;
            case "bios": node["context"]!["identity"]!["biosVersion"] = "2.03"; break;
            case "missing-field": node.AsObject().Remove("restoring"); break;
            case "operation-missing": node.AsObject().Remove("operationBaseline"); break;
            case "operation-duplicate": node["operationBaseline"]![0]!["physicalCoreId"] = 2; break;
            case "operation-order":
                node["operationBaseline"]![0]!["physicalCoreId"] = 2;
                node["operationBaseline"]![1]!["physicalCoreId"] = 0; break;
            case "operation-unowned-value": node["operationBaseline"]![0]!["offset"] = 9; break;
            case "operation-before": node["operationBaseline"]![0]!["offset"] = -3; break;
            case "false-restored": node["state"] = (int)CpuLiveCurveOwnershipState.Restored; break;
            case "false-restoring": node["restoring"] = true; break;
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
        var baseline = original.OriginalBaseline.ToArray(); var operation = original.OperationBaseline.ToArray();
        var expected = original.Expected.ToArray(); var steps = original.Steps.ToArray();
        var journal = new CpuLiveCurveOwnershipJournal(2, original.TransactionId, original.Context, baseline, operation, steps, expected,
            original.State, original.CompletedSteps, original.Restoring, original.UpdatedAtUtc);
        baseline[0] = new(0, 9); operation[0] = new(0, 9); expected[0] = new(0, 9); steps[0] = new(0, 9, 0);
        Assert.True(journal.IsWellFormed); Assert.Equal(original.OriginalBaseline, journal.OriginalBaseline);
        Assert.Equal(original.OperationBaseline, journal.OperationBaseline);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLiveCoreOffset>)journal.OriginalBaseline)[0] = new(0, 2));
        Directory.CreateDirectory(directory);
        using var held = new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(await new CpuLiveCurveOwnershipJournalStore(JournalPath).TryWriteAsync(null, journal, default));
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task LegacyGuidJournalIsRetainedAndCannotBeAdoptedOrRestored()
    {
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(Create(), JsonOptions))!.AsObject();
        legacy["schemaVersion"] = 1;
        legacy["baseline"] = legacy["originalBaseline"]!.DeepClone();
        legacy.Remove("originalBaseline"); legacy.Remove("operationBaseline");
        Directory.CreateDirectory(directory);
        string json = legacy.ToJsonString();
        await File.WriteAllTextAsync(JournalPath, json);
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        var backend = new CpuLiveCurveTuningTransactionTests.FakeBackend(new());
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.Equal(CpuLiveCurveBlockReason.InvalidJournal, (await transaction.RestoreAsync(default)).Reason);
        Assert.Equal(CpuLiveCurveBlockReason.InvalidJournal, (await transaction.ApplySelectiveAsync([new(0, 0)], default)).Reason);
        Assert.Equal(0, backend.Reads); Assert.Empty(backend.Sent);
        Assert.Equal(json, await File.ReadAllTextAsync(JournalPath));
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
