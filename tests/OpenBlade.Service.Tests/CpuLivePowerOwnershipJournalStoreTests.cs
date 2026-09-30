using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLivePowerOwnershipJournalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBladeLivePowerJournalTests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(directory, "live-ppt.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PublicDtoFieldsRoundTripExactFractionalOriginalAndFullPendingPair()
    {
        var journal = Create().Progress(CpuLivePowerOwnershipState.PossibleSubmission, 1);
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath);
        string? hash = await store.TryWriteAsync(null, journal, default);
        Assert.NotNull(hash);
        var loaded = await new CpuLivePowerOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.False(loaded.Invalid); Assert.Equal(hash, loaded.Sha256);
        Assert.Equal(journal.Context, loaded.Journal!.Context); Assert.Equal(journal.OriginalBaseline, loaded.Journal.OriginalBaseline);
        Assert.Equal(journal.OperationBaseline, loaded.Journal.OperationBaseline); Assert.Equal(journal.Expected, loaded.Journal.Expected);
        Assert.Equal(journal.Steps, loaded.Journal.Steps); Assert.Equal(1, loaded.Journal.CompletedSteps);
        Assert.Equal(CpuLivePowerOwnershipState.PossibleSubmission, loaded.Journal.State);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        Assert.Equal(100123u, json["originalBaseline"]!["fast"]!["milliwatts"]!.GetValue<uint>());
        Assert.Equal(CpuLivePowerTuningTransactionTests.Bits(100123), json["originalBaseline"]!["fast"]!["rawBits"]!.GetValue<uint>());
        Assert.Equal(CpuLivePowerTuningTransactionTests.Bits(80765), json["originalBaseline"]!["slow"]!["rawBits"]!.GetValue<uint>());
    }

    [Fact]
    public async Task RestoringFractionalDestinationsProgressForwardAndKeepCompleteOriginal()
    {
        var original = Create(); var owned = original.Progress(CpuLivePowerOwnershipState.OwnedVerified, 2);
        var restoring = owned.ArmRestoration().Progress(CpuLivePowerOwnershipState.OwnedVerified, 1);
        Assert.True(restoring.IsWellFormed); Assert.True(restoring.Restoring);
        Assert.Equal(owned.Expected, restoring.OperationBaseline);
        Assert.Equal(new[] { CpuLivePowerLimit.Fast, CpuLivePowerLimit.Slow }, restoring.Steps.Select(step => step.Limit));
        Assert.Equal(new[] { 100123u, 80765u }, restoring.Steps.Select(step => step.After.Milliwatts));
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath);
        Assert.NotNull(await store.TryWriteAsync(null, restoring, default));
        var loaded = (await new CpuLivePowerOwnershipJournalStore(JournalPath).LoadAsync(default)).Journal!;
        Assert.Equal(original.OriginalBaseline, loaded.OriginalBaseline); Assert.Equal(100123u, loaded.Expected.Fast.Milliwatts);
        Assert.Equal(79000u, loaded.Expected.Slow.Milliwatts);
        var finished = loaded.Progress(CpuLivePowerOwnershipState.Restored, 2);
        Assert.True(finished.IsWellFormed); Assert.Equal(original.OriginalBaseline, finished.Expected);
    }

    [Fact]
    public async Task CompareAndSwapAcrossInstancesAllowsOnlyOneWinner()
    {
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath); var journal = Create();
        string? hash = await store.TryWriteAsync(null, journal, default);
        var results = await Task.WhenAll(store.TryWriteAsync(hash, journal.Progress(CpuLivePowerOwnershipState.PossibleSubmission, 0), default),
            new CpuLivePowerOwnershipJournalStore(JournalPath).TryWriteAsync(hash, journal.Progress(CpuLivePowerOwnershipState.Blocked, 0), default));
        Assert.Single(results, result => result is not null);
        Assert.Null(await store.TryWriteAsync(hash, journal, default)); Assert.Null(await store.TryWriteAsync(null, journal, default));
        Assert.False((await store.LoadAsync(default)).Invalid);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("case")]
    [InlineData("schema")]
    [InlineData("missing-original")]
    [InlineData("missing-bits")]
    [InlineData("milliwatt-mismatch")]
    [InlineData("canonical-float")]
    [InlineData("nonfinite")]
    [InlineData("step-order")]
    [InlineData("duplicate-limit")]
    [InlineData("unknown-limit")]
    [InlineData("before")]
    [InlineData("edited-fraction")]
    [InlineData("edited-range")]
    [InlineData("progress")]
    [InlineData("expected")]
    [InlineData("stapm")]
    [InlineData("false-restored")]
    [InlineData("false-restoring")]
    [InlineData("null-steps")]
    [InlineData("null-value")]
    [InlineData("null-step")]
    [InlineData("boot")]
    [InlineData("backend")]
    [InlineData("missing-state")]
    public async Task StrictSyntaxAndDerivedPairSemanticsRejectTampering(string fault)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Create(), Options))!;
        switch (fault)
        {
            case "unknown": node["extra"] = 1; break;
            case "case": node["SchemaVersion"] = 1; break;
            case "schema": node["schemaVersion"] = 0; break;
            case "missing-original": node.AsObject().Remove("originalBaseline"); break;
            case "missing-bits": node["originalBaseline"]!["fast"]!.AsObject().Remove("rawBits"); break;
            case "milliwatt-mismatch": node["originalBaseline"]!["fast"]!["milliwatts"] = 100122; break;
            case "canonical-float": node["originalBaseline"]!["fast"]!["rawBits"] = 0x42AA0000u; node["originalBaseline"]!["fast"]!["milliwatts"] = 85000; break;
            case "nonfinite": node["originalBaseline"]!["stapmRawBits"] = 0x7FC00001u; break;
            case "step-order": node["steps"]![0]!["limit"] = 1; node["steps"]![1]!["limit"] = 0; break;
            case "duplicate-limit": node["steps"]![1]!["limit"] = 0; break;
            case "unknown-limit": node["steps"]![0]!["limit"] = 9; break;
            case "before": node["steps"]![0]!["before"] = Value(100122); break;
            case "edited-fraction": node["steps"]![0]!["after"] = Value(90001); break;
            case "edited-range": node["steps"]![0]!["after"] = Value(96000); break;
            case "progress": node["completedSteps"] = 3; break;
            case "expected": node["expected"]!["slow"] = Value(80000); break;
            case "stapm": node["operationBaseline"]!["stapmRawBits"] = CpuLivePowerTuningTransactionTests.Bits(59000); break;
            case "false-restored": node["state"] = (int)CpuLivePowerOwnershipState.Restored; break;
            case "false-restoring": node["restoring"] = true; break;
            case "null-steps": node["steps"] = null; break;
            case "null-value": node["originalBaseline"]!["fast"] = null; break;
            case "null-step": node["steps"]![0] = null; break;
            case "boot": node["context"]!["bootSequence"]!["sequence"] = null; break;
            case "backend": node["context"]!["backend"]!["driverSha256"] = "other"; break;
            case "missing-state": node.AsObject().Remove("state"); break;
        }
        string json = node.ToJsonString();
        if (fault == "duplicate") { json = json.Insert(1, "\"schemaVersion\":1,"); }
        if (fault == "nested-duplicate") { json = json.Replace("\"milliwatts\":100123", "\"milliwatts\":100123,\"milliwatts\":100123", StringComparison.Ordinal); }
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(JournalPath, json);
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath);
        Assert.True((await store.LoadAsync(default)).Invalid);
        Assert.Null(await store.TryWriteAsync(null, Create(), default)); Assert.Equal(json, await File.ReadAllTextAsync(JournalPath));
    }

    [Fact]
    public async Task MissingMalformedOversizeAndLockedFilesFailClosedWithoutOverwrite()
    {
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath);
        var missing = await store.LoadAsync(default); Assert.False(missing.Invalid); Assert.Null(missing.Journal);
        Directory.CreateDirectory(directory);
        foreach (var content in new[] { "", "{}", "null", "{bad", new string('x', 32769) })
        {
            await File.WriteAllTextAsync(JournalPath, content);
            Assert.True((await store.LoadAsync(default)).Invalid); Assert.Null(await store.TryWriteAsync(null, Create(), default));
            Assert.Equal(content, await File.ReadAllTextAsync(JournalPath));
        }
        await File.WriteAllTextAsync(JournalPath, JsonSerializer.Serialize(Create(), Options));
        using var held = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True((await store.LoadAsync(default)).Invalid); Assert.Null(await store.TryWriteAsync(null, Create(), default));
    }

    [Fact]
    public async Task DefensiveStepArrayAndLockContentionCannotReplaceJournal()
    {
        var original = Create(); var steps = original.Steps.ToArray();
        var journal = new CpuLivePowerOwnershipJournal(1, original.TransactionId, original.Context, original.OriginalBaseline,
            original.OperationBaseline, steps, original.Expected, original.State, 0, false, original.UpdatedAtUtc);
        steps[0] = steps[1]; Assert.True(journal.IsWellFormed);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuLivePowerStoredStep>)journal.Steps)[0] = steps[1]);
        Directory.CreateDirectory(directory);
        using var held = new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(await new CpuLivePowerOwnershipJournalStore(JournalPath).TryWriteAsync(null, journal, default));
        Assert.False(File.Exists(JournalPath));
    }

    private static JsonNode Value(uint value) => JsonSerializer.SerializeToNode(new CpuLivePowerStoredValue(value, CpuLivePowerTuningTransactionTests.Bits(value)), Options)!;
    internal static CpuLivePowerOwnershipJournal Create()
    {
        var plan = Rz090528CpuLivePowerPlan.CreateSelective(CpuLivePowerTuningTransactionTests.Raw(CpuLivePowerTuningTransactionTests.PairFixture()),
            [new(CpuLivePowerLimit.Fast, 90), new(CpuLivePowerLimit.Slow, 79)], CpuLivePowerFusedCap.TryDecode(4, 7u << 28),
            CpuLivePowerCapability.TryDecode(2), new(0x50001, 0, 125000, 1), (_, _) => true).Plan!;
        return CpuLivePowerOwnershipJournal.Prepare(CpuLivePowerTuningTransactionTests.ContextFixture(), plan);
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
