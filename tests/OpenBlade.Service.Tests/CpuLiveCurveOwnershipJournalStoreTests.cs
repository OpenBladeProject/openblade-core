using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveCurveOwnershipJournalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBladeLiveCurveJournalTests", Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(directory, "live-co.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExplicitPostBootObservationArchivesExactPendingBytesAndClosesOnlyVerifiedOriginal()
    {
        var fixture = await RecoveryFixture.CreateAsync(JournalPath);
        byte[] previous = await File.ReadAllBytesAsync(JournalPath);
        string previousHash = (await fixture.Store.LoadAsync(default)).Sha256!;
        Assert.Equal(ControlOperationStatus.Blocked, (await fixture.Controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await fixture.Controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.Equal(0, fixture.BootReads); Assert.Equal(0, fixture.Curve.Reads); Assert.Equal(0, fixture.Power.Reads);
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var result = await fixture.Controller.ObserveLiveAsync(_ => fixture.Admitted, default, diagnostic: diagnostic);
        Assert.False(result.RestorePending); Assert.Equal(CpuTuningRecoveryState.None, result.LiveRecovery!.State);
        Assert.Equal(CpuTuningFeatures.None, result.LiveRecovery.Features); Assert.False(result.Live.CanWrite);
        Assert.Equal(fixture.Prior.OriginalBaseline.Select(core => (int)core.Offset), result.LiveState!.CoreValues.Select(core => core.Value));
        Assert.Contains("original live CO observed after boot", diagnostic.LiveObservationReason);
        Assert.Equal(2, fixture.Curve.Reads); Assert.Equal(2, fixture.Power.Reads);
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Curve.Admissions);
        var settled = await new CpuLiveCurveOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, settled.Journal!.State);
        Assert.Equal(fixture.Prior.TransactionId, settled.Journal.TransactionId);
        Assert.Equal(fixture.Prior.Context, settled.Journal.Context);
        Assert.Equal(fixture.Prior.OriginalBaseline, settled.Journal.OriginalBaseline);
        string archive = Assert.Single(Directory.GetDirectories(directory, "live-co.json.post-boot-*"));
        Assert.Equal(previous, await File.ReadAllBytesAsync(Path.Combine(archive, "original.json")));
        var receipt = JsonSerializer.Deserialize<CpuLiveCurvePostBootReceipt>(await File.ReadAllTextAsync(Path.Combine(archive, "receipt.json")), JsonOptions)!;
        Assert.Equal("PreSettlement", receipt.State); Assert.Equal("OriginalObservedAfterBoot", receipt.Basis);
        Assert.Equal(previousHash, receipt.PreviousSha256); Assert.Equal(settled.Sha256, receipt.ProposedSha256);
        Assert.True(receipt.Observation.Matches(fixture.Prior));
        Assert.Equal(fixture.Boot, receipt.Observation.Context.BootSequence);
        Assert.NotEqual(fixture.Prior.Context.RuntimeLifetime, receipt.Observation.Context.RuntimeLifetime);
    }

    [Theory]
    [InlineData("same-boot")]
    [InlineData("unknown-boot")]
    [InlineData("disabled")]
    [InlineData("worker")]
    [InlineData("admission")]
    [InlineData("ppt-restored")]
    [InlineData("ppt-invalid")]
    [InlineData("malformed")]
    public async Task PostBootReconciliationRejectsUnsafeEntryBeforeNativeReads(string fault)
    {
        var fixture = await RecoveryFixture.CreateAsync(JournalPath, enabled: fault != "disabled");
        if (fault == "same-boot") { fixture.Boot = fixture.Prior.Context.BootSequence; }
        if (fault == "unknown-boot") { fixture.Boot = null; }
        if (fault == "worker") { fixture.Idle = false; }
        if (fault == "admission") { fixture.Admitted = false; }
        if (fault == "ppt-restored") { fixture.PowerStore.Journal = CpuPowerTestData.Legacy(CpuLivePowerOwnershipState.Restored); }
        if (fault == "ppt-invalid") { fixture.PowerStore.Invalid = true; }
        if (fault == "malformed") { await File.WriteAllTextAsync(JournalPath, "{invalid"); }
        byte[] previous = await File.ReadAllBytesAsync(JournalPath);
        var result = await fixture.Controller.ObserveLiveAsync(_ => fixture.Admitted, default);
        Assert.True(result.RestorePending); Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
        Assert.Equal(previous, await File.ReadAllBytesAsync(JournalPath));
        Assert.Equal(0, fixture.Curve.Reads); Assert.Equal(0, fixture.Power.Reads); Assert.Empty(fixture.Curve.Sent);
        Assert.Empty(Directory.GetDirectories(directory, "live-co.json.post-boot-*"));
    }

    [Theory]
    [InlineData("curve-drift")]
    [InlineData("curve-incomplete")]
    [InlineData("curve-context")]
    [InlineData("power-unavailable")]
    [InlineData("peer-drift")]
    [InlineData("boot-drift")]
    [InlineData("runtime-drift")]
    [InlineData("cancel")]
    public async Task PostBootReconciliationNeverSettlesUnprovenReadings(string fault)
    {
        var fixture = await RecoveryFixture.CreateAsync(JournalPath);
        using var cancellation = new CancellationTokenSource();
        if (fault == "curve-drift") { fixture.Curve.Values[0] = new(0, 0); }
        if (fault == "curve-incomplete") { fixture.Curve.Values = fixture.Curve.Values[..^1]; }
        if (fault == "curve-context") { fixture.Curve.Context = fixture.Curve.Context with { BootSequence = fixture.Prior.Context.BootSequence }; }
        if (fault == "power-unavailable") { fixture.Power.Unavailable = true; }
        var beforePower = fixture.Power.OnObserve!;
        fixture.Power.OnObserve = () =>
        {
            beforePower();
            if (fixture.Power.Reads != 2) { return; }
            if (fault == "peer-drift") { fixture.Power.Pair = CpuPowerTestData.PairFixture(45000); }
            if (fault == "boot-drift") { fixture.Boot = new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 44); }
            if (fault == "runtime-drift") { fixture.Admitted = false; }
            if (fault == "cancel") { cancellation.Cancel(); }
        };
        byte[] previous = await File.ReadAllBytesAsync(JournalPath);
        var result = await fixture.Controller.ObserveLiveAsync(_ => fixture.Admitted, cancellation.Token);
        Assert.True(result.RestorePending); Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
        Assert.Equal(previous, await File.ReadAllBytesAsync(JournalPath)); Assert.Empty(fixture.Curve.Sent);
        Assert.Empty(fixture.Curve.Admissions); Assert.Empty(Directory.GetDirectories(directory, "live-co.json.post-boot-*"));
    }

    [Fact]
    public async Task CurveDriftDuringPeerValidationCannotSettleEarlierOriginalObservation()
    {
        var fixture = await RecoveryFixture.CreateAsync(JournalPath);
        var beforePower = fixture.Power.OnObserve!;
        fixture.Power.OnObserve = () =>
        {
            beforePower();
            if (fixture.Power.Reads == 2)
            {
                Assert.Equal(1, fixture.Curve.Reads);
                Assert.Equal(fixture.Prior.OriginalBaseline, fixture.Curve.Values);
                fixture.Curve.Values[0] = new(0, 0);
            }
        };
        byte[] previous = await File.ReadAllBytesAsync(JournalPath);
        var result = await fixture.Controller.ObserveLiveAsync(_ => true, default);
        Assert.True(result.RestorePending); Assert.Equal(CpuTuningRecoveryState.Blocked, result.LiveRecovery!.State);
        Assert.Equal(2, fixture.Curve.Reads); Assert.Equal(2, fixture.Power.Reads);
        Assert.Equal(previous, await File.ReadAllBytesAsync(JournalPath));
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, (await fixture.Store.LoadAsync(default)).Journal!.State);
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Curve.Admissions);
        Assert.Empty(Directory.GetDirectories(directory, "live-co.json.post-boot-*"));
    }

    [Theory]
    [InlineData("cas")]
    [InlineData("admission")]
    [InlineData("persistence")]
    public async Task PostBootAuditAttemptCannotClaimSettlementAfterFinalFailure(string fault)
    {
        var fixture = await RecoveryFixture.CreateAsync(JournalPath);
        var loaded = await fixture.Store.LoadAsync(default);
        byte[] previous = await File.ReadAllBytesAsync(JournalPath);
        var evidence = fixture.Evidence();
        int checks = 0;
        FileStream? held = null;
        try
        {
            string? result = await fixture.Store.TryReconcileAfterBootAsync(loaded.Sha256!, evidence, async () =>
            {
                if (++checks == 2)
                {
                    if (fault == "cas") { await File.WriteAllTextAsync(JournalPath, "{} "); }
                    if (fault == "admission") { return false; }
                    if (fault == "persistence") { held = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                }
                return true;
            }, default);
            Assert.Null(result);
        }
        finally { held?.Dispose(); }
        Assert.Equal(fault == "cas" ? "{} " : System.Text.Encoding.UTF8.GetString(previous), await File.ReadAllTextAsync(JournalPath));
        string archive = Assert.Single(Directory.GetDirectories(directory, "live-co.json.post-boot-*"));
        Assert.Equal(previous, await File.ReadAllBytesAsync(Path.Combine(archive, "original.json")));
        var receipt = JsonSerializer.Deserialize<CpuLiveCurvePostBootReceipt>(await File.ReadAllTextAsync(Path.Combine(archive, "receipt.json")), JsonOptions)!;
        Assert.Equal("PreSettlement", receipt.State);
        Assert.NotEqual(receipt.ProposedSha256, (await fixture.Store.LoadAsync(default)).Sha256);
    }

    private sealed class RecoveryFixture
    {
        internal readonly CpuLiveCurveOwnershipJournalStore Store;
        internal readonly CpuPowerTestData.MemoryStore PowerStore = new();
        internal readonly CpuLiveCurveTuningTransactionTests.FakeBackend Curve = new(new());
        internal readonly CpuPowerTestData.FakeBackend Power = new();
        internal readonly CpuTuningController Controller;
        internal readonly CpuLiveCurveOwnershipJournal Prior;
        internal CpuLiveCurveBootSequence? Boot = new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 43);
        internal bool Idle = true, Admitted = true;
        internal int BootReads;
        private RecoveryFixture(string path, bool enabled)
        {
            Store = new(path);
            Prior = Create(CpuLiveCurveTuningTransactionTests.ContextFixture() with
            { RuntimeLifetime = new(Guid.NewGuid(), 0, 1) }).Progress(CpuLiveCurveOwnershipState.PossibleSubmission, 0, false);
            Controller = new(new UnusedSource(), curveBackend: Curve, curveStore: Store, powerBackend: Power, powerStore: PowerStore,
                readRecoveryBoot: enabled ? ReadBoot : null,
                recoveryWorkerIdle: enabled ? () => Idle : null);
            Curve.Context = Prior.Context with { BootSequence = Boot! };
            Curve.Values = Prior.OriginalBaseline.ToArray();
            Curve.OnObserve = _ =>
            {
                Curve.Context = Curve.Context with { RuntimeLifetime = Controller.CaptureLiveRuntimeLifetime() };
                return Task.FromResult(new CpuLiveCurveBackendObservation(Curve.Context, new(0xFF55, 0x005D000B, 0xD50, Curve.Values.ToArray())));
            };
            Power.Context = Power.Context with { BootSequence = Boot! };
            Power.OnObserve = () => Power.Context = Power.Context with { RuntimeLifetime = Controller.CaptureLiveRuntimeLifetime() };
        }
        private CpuLiveCurveBootSequence? ReadBoot(CancellationToken token) { BootReads++; return Boot; }
        internal static async Task<RecoveryFixture> CreateAsync(string path, bool enabled = true)
        {
            var result = new RecoveryFixture(path, enabled);
            Assert.NotNull(await result.Store.TryWriteAsync(null, result.Prior, default)); return result;
        }
        internal CpuLiveCurvePostBootObservation Evidence()
        {
            var current = Curve.Context with { RuntimeLifetime = new(Guid.NewGuid(), 0, 1) };
            return new(current, Prior.OriginalBaseline, Power.Context with { RuntimeLifetime = current.RuntimeLifetime },
                CpuLivePowerStoredPair.From(Power.Pair), CpuLivePowerStoredPair.From(Power.Pair), DateTimeOffset.UtcNow);
        }
    }

    private sealed class UnusedSource : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => throw new InvalidOperationException("Recovery cannot use saved identity.");
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public async Task KnownLoaderSequenceRoundTripsWithoutBecomingAGuid(uint sequence)
    {
        var context = CpuLiveCurveTuningTransactionTests.ContextFixture() with
        { BootSequence = new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, sequence) };
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        Assert.True(context.IsWellFormed);
        Assert.NotNull(await store.TryWriteAsync(null, Create(context), default));
        var loaded = await new CpuLiveCurveOwnershipJournalStore(JournalPath).LoadAsync(default);
        Assert.False(loaded.Invalid); Assert.Equal(3, loaded.Journal!.SchemaVersion);
        Assert.Equal(context, loaded.Journal.Context);
        Assert.Equal(sequence, loaded.Journal.Context.BootSequence.Sequence);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        Assert.False(json["context"]!.AsObject().ContainsKey("bootId"));
    }

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
    [InlineData("missing-boot-sequence")]
    [InlineData("missing-boot-source")]
    [InlineData("unknown-boot-source")]
    [InlineData("undefined-boot-source")]
    [InlineData("null-boot")]
    [InlineData("missing-boot")]
    [InlineData("backend")]
    [InlineData("model")]
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
            case "unknown-boot": node["context"]!["bootSequence"]!["sequence"] = null; break;
            case "missing-boot-sequence": node["context"]!["bootSequence"]!.AsObject().Remove("sequence"); break;
            case "missing-boot-source": node["context"]!["bootSequence"]!.AsObject().Remove("source"); break;
            case "unknown-boot-source": node["context"]!["bootSequence"]!["source"] = (int)CpuLiveCurveBootSequenceSource.Unknown; break;
            case "undefined-boot-source": node["context"]!["bootSequence"]!["source"] = 999; break;
            case "null-boot": node["context"]!["bootSequence"] = null; break;
            case "missing-boot": node["context"]!.AsObject().Remove("bootSequence"); break;
            case "backend": node["context"]!["backend"]!["driverSha256"] = "other"; break;
            case "model": node["context"]!["identity"]!["model"] = "RZ09-9999"; break;
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
        var journal = new CpuLiveCurveOwnershipJournal(3, original.TransactionId, original.Context, baseline, operation, steps, expected,
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyGuidJournalIsRetainedAndCannotBeAdoptedOrRestored(int schema)
    {
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(Create(), JsonOptions))!.AsObject();
        legacy["schemaVersion"] = schema;
        legacy["context"]!.AsObject().Remove("bootSequence");
        legacy["context"]!["bootId"] = Guid.Parse("22222222-2222-2222-2222-222222222222");
        if (schema == 1)
        {
            legacy["baseline"] = legacy["originalBaseline"]!.DeepClone();
            legacy.Remove("originalBaseline"); legacy.Remove("operationBaseline");
        }
        Directory.CreateDirectory(directory);
        string json = legacy.ToJsonString();
        await File.WriteAllTextAsync(JournalPath, json);
        var store = new CpuLiveCurveOwnershipJournalStore(JournalPath);
        var backend = new CpuLiveCurveTuningTransactionTests.FakeBackend(new());
        var transaction = new CpuLiveCurveTuningTransaction(backend, store);
        Assert.True((await store.LoadAsync(default)).Invalid);
        Assert.Equal(CpuLiveCurveBlockReason.InvalidJournal, (await transaction.RestoreAsync(default)).Reason);
        Assert.Equal(CpuLiveCurveBlockReason.InvalidJournal, (await transaction.ApplySelectiveAsync([new(0, 0)], default)).Reason);
        Assert.Equal(0, backend.Reads); Assert.Empty(backend.Sent);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(JournalPath)));
        Assert.Null(await store.TryWriteAsync(hash, Create(), default));
        Assert.Equal(json, await File.ReadAllTextAsync(JournalPath));
    }

    private static CpuLiveCurveOwnershipJournal Create(CpuLiveCurveContext? context = null)
    {
        var cores = CpuLiveCurveTuningTransactionTests.ValuesFixture();
        cores[0] = new(0, short.MinValue); cores[1] = new(2, short.MaxValue);
        var plan = Rz090528CpuLiveCurvePlan.CreateAllCore(new(0xFF55, 0x005D000B, 0xD50, cores), new(true, -2), (_, _) => true).Plan!;
        return CpuLiveCurveOwnershipJournal.Prepare(context ?? CpuLiveCurveTuningTransactionTests.ContextFixture(), plan);
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
