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
        var journal = Create();
        var store = new CpuLivePowerOwnershipJournalStore(JournalPath);
        Directory.CreateDirectory(directory);
        string originalJson = JsonSerializer.Serialize(journal, Options);
        await File.WriteAllTextAsync(JournalPath, originalJson);
        var loaded = await new CpuLivePowerOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.False(loaded.Invalid); Assert.NotNull(loaded.Sha256); Assert.Equal(originalJson, await File.ReadAllTextAsync(JournalPath));
        Assert.Equal(journal.Context, loaded.Journal!.Context); Assert.Equal(journal.OriginalBaseline, loaded.Journal.OriginalBaseline);
        Assert.Equal(journal.OperationBaseline, loaded.Journal.OperationBaseline); Assert.Equal(journal.Expected, loaded.Journal.Expected);
        Assert.Equal(journal.Steps, loaded.Journal.Steps); Assert.Equal(0, loaded.Journal.CompletedSteps);
        Assert.Equal(CpuLivePowerOwnershipState.Prepared, loaded.Journal.State);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        Assert.Equal(100123u, json["originalBaseline"]!["fast"]!["milliwatts"]!.GetValue<uint>());
        Assert.Equal(CpuPowerTestData.Bits(100123), json["originalBaseline"]!["fast"]!["rawBits"]!.GetValue<uint>());
        Assert.Equal(CpuPowerTestData.Bits(80765), json["originalBaseline"]!["slow"]!["rawBits"]!.GetValue<uint>());
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
            case "stapm": node["operationBaseline"]!["stapmRawBits"] = CpuPowerTestData.Bits(59000); break;
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
        Assert.Equal(json, await File.ReadAllTextAsync(JournalPath));
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
            Assert.True((await store.LoadAsync(default)).Invalid);
            Assert.Equal(content, await File.ReadAllTextAsync(JournalPath));
        }
        await File.WriteAllTextAsync(JournalPath, JsonSerializer.Serialize(Create(), Options));
        using var held = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True((await store.LoadAsync(default)).Invalid);
    }

    private static JsonNode Value(uint value) => JsonSerializer.SerializeToNode(new CpuLivePowerStoredValue(value, CpuPowerTestData.Bits(value)), Options)!;
    internal static CpuLivePowerOwnershipJournal Create()
    {
        var original = CpuPowerTestData.PairFixture();
        var after = CpuPowerTestData.PairFixture(90000, 79000);
        return new(1, Guid.NewGuid(), CpuPowerTestData.ContextFixture(), CpuLivePowerStoredPair.From(original),
            CpuLivePowerStoredPair.From(original),
            [new(CpuLivePowerLimit.Fast, CpuLivePowerStoredValue.From(original.Fast), CpuLivePowerStoredValue.From(after.Fast)),
                new(CpuLivePowerLimit.Slow, CpuLivePowerStoredValue.From(original.Slow), CpuLivePowerStoredValue.From(after.Slow))],
            CpuLivePowerStoredPair.From(original), CpuLivePowerOwnershipState.Prepared, 0, false, DateTimeOffset.UnixEpoch);
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
