using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ThermalConvergenceStateStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.ThermalConvergenceStateStoreTests",
        Guid.NewGuid().ToString("N"));
    private readonly SecurityIdentifier userSid =
        new("S-1-5-21-1-2-3-1001");

    [Fact]
    public async Task Load_NoFileReturnsAuthoritativeEmptyStateWithoutMaterializingFile()
    {
        string path = Path.Combine(directory, "thermal-convergence.json");
        var store = new ThermalConvergenceStateStore(path, userSid);

        ThermalConvergenceLoadResult loaded = await store.LoadAsync(
            CancellationToken.None);

        Assert.True(loaded.IsAuthoritative);
        Assert.True(loaded.State.IsEmpty);
        Assert.Equal(userSid.Value, loaded.State.ActiveUserSid);
        Assert.Null(loaded.Sha256);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TrySaveAndLoad_RoundTripsReservationBeforeWriteEvidence()
    {
        string path = Path.Combine(directory, "thermal-convergence.json");
        var store = new ThermalConvergenceStateStore(path, userSid);
        ThermalConvergenceLedgerState state = CreateState(withInFlight: true);

        ThermalConvergenceMutationResult saved = await store.TrySaveAsync(
            expectedSha256: null,
            state,
            CancellationToken.None);
        ThermalConvergenceLoadResult loaded = await store.LoadAsync(
            CancellationToken.None);

        Assert.True(saved.Applied);
        Assert.Matches("^[0-9A-F]{64}$", saved.Sha256);
        Assert.Equivalent(state, loaded.State, strict: true);
        Assert.Equal(saved.Sha256, loaded.Sha256);
        Assert.True(loaded.State.RequiresReadOnlyReconciliation);
    }

    [Fact]
    public async Task TrySave_StaleHashPreservesNewerRecord()
    {
        string path = Path.Combine(directory, "thermal-convergence.json");
        var store = new ThermalConvergenceStateStore(path, userSid);
        ThermalConvergenceLedgerState original = CreateState(withInFlight: true);
        ThermalConvergenceMutationResult first = await store.TrySaveAsync(
            null,
            original,
            CancellationToken.None);
        ThermalConvergenceLedgerState newer = original with
        {
            IntentGeneration = original.IntentGeneration + 1,
        };
        ThermalConvergenceMutationResult second = await store.TrySaveAsync(
            first.Sha256,
            newer,
            CancellationToken.None);

        ThermalConvergenceMutationResult stale = await store.TrySaveAsync(
            first.Sha256,
            original with { IntentGeneration = original.IntentGeneration + 2 },
            CancellationToken.None);

        Assert.True(second.Applied);
        Assert.False(stale.Applied);
        Assert.Equivalent(
            newer,
            (await store.LoadAsync(CancellationToken.None)).State,
            strict: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_CorruptOrFutureDocumentPreservesExactBytesAndBlocksMutation(
        bool future)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "thermal-convergence.json");
        byte[] evidence = future
            ? Encoding.UTF8.GetBytes(
                "{\r\n  \"schemaVersion\": 2,\r\n  \"state\": {}\r\n}\r\n")
            : Encoding.UTF8.GetBytes("{ not-json\r\n");
        await File.WriteAllBytesAsync(path, evidence);
        var store = new ThermalConvergenceStateStore(path, userSid);

        ThermalConvergenceLoadResult loaded = await store.LoadAsync(
            CancellationToken.None);
        ConfigurationQuarantinedException exception =
            await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
                store.TrySaveAsync(
                    expectedSha256: null,
                    CreateState(withInFlight: false),
                    CancellationToken.None));

        Assert.False(loaded.IsAuthoritative);
        Assert.True(loaded.State.IsEmpty);
        Assert.Equal(
            future
                ? ConfigurationQuarantineReason.FutureSchema
                : ConfigurationQuarantineReason.InvalidDocument,
            loaded.QuarantineReason);
        Assert.Equal(loaded.QuarantineReason, exception.Reason);
        Assert.Equal(evidence, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Load_RecordBoundToAnotherUserIsQuarantinedInPlace()
    {
        string path = Path.Combine(directory, "thermal-convergence.json");
        var first = new ThermalConvergenceStateStore(path, userSid);
        ThermalConvergenceMutationResult saved = await first.TrySaveAsync(
            null,
            CreateState(withInFlight: false),
            CancellationToken.None);
        byte[] evidence = await File.ReadAllBytesAsync(path);
        var other = new ThermalConvergenceStateStore(
            path,
            new SecurityIdentifier("S-1-5-21-1-2-3-1002"));

        ThermalConvergenceLoadResult loaded = await other.LoadAsync(
            CancellationToken.None);

        Assert.NotNull(saved.Sha256);
        Assert.False(loaded.IsAuthoritative);
        Assert.Equal(ConfigurationQuarantineReason.InvalidDocument, loaded.QuarantineReason);
        Assert.Equal(evidence, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("tombstones", false)]
    [InlineData("tombstones", true)]
    [InlineData("terminalAffectedComponents", false)]
    [InlineData("terminalAffectedComponents", true)]
    [InlineData("budgets", false)]
    [InlineData("budgets", true)]
    public async Task Load_MissingOrNullEvidenceCollectionIsQuarantinedInPlace(
        string member,
        bool writeNull)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "thermal-convergence.json");
        JsonObject document = Assert.IsType<JsonObject>(
            JsonSerializer.SerializeToNode(
                new ThermalConvergenceStateDocument(
                    SchemaVersion: 1,
                    CreateState(withInFlight: false)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        JsonObject state = Assert.IsType<JsonObject>(document["state"]);
        if (writeNull)
        {
            state[member] = null;
        }
        else
        {
            Assert.True(state.Remove(member));
        }
        byte[] evidence = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(path, evidence);
        var store = new ThermalConvergenceStateStore(path, userSid);

        ThermalConvergenceLoadResult loaded = await store.LoadAsync(
            CancellationToken.None);

        Assert.False(loaded.IsAuthoritative);
        Assert.True(loaded.State.IsEmpty);
        Assert.Equal(
            ConfigurationQuarantineReason.InvalidDocument,
            loaded.QuarantineReason);
        Assert.Equal(evidence, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TrySave_RejectsUnsortedOrDuplicateComponentsWithoutCreatingFile()
    {
        string path = Path.Combine(directory, "thermal-convergence.json");
        var store = new ThermalConvergenceStateStore(path, userSid);
        ThermalConvergenceLedgerState valid = CreateState(withInFlight: true);
        ThermalConvergenceLedgerState invalid = valid with
        {
            InFlight = valid.InFlight! with
            {
                DeclaredComponents =
                [ThermalComponent.Fans, ThermalComponent.Performance],
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.TrySaveAsync(
            null,
            invalid,
            CancellationToken.None));

        Assert.False(File.Exists(path));
    }

    private ThermalConvergenceLedgerState CreateState(bool withInFlight)
    {
        DesiredDeviceState desired = CreateDesired();
        string performanceRevision = Guid.NewGuid().ToString("D");
        string fanRevision = Guid.NewGuid().ToString("D");
        string policyRevision = Guid.NewGuid().ToString("D");
        var preWrite = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.ManualCurve,
            Battery: null,
            Lighting: null);
        return new ThermalConvergenceLedgerState(
            userSid.Value,
            Guid.NewGuid(),
            "RZ09-0581|1532:02E0",
            IntentGeneration: 7,
            desired,
            performanceRevision,
            fanRevision,
            policyRevision,
            PowerSource.PluggedIn,
            ThermalConvergenceContradiction.Performance
                | ThermalConvergenceContradiction.FanTopology,
            withInFlight
                ? new ThermalConvergenceInFlight(
                    Guid.NewGuid(),
                    [ThermalComponent.Performance, ThermalComponent.Fans],
                    preWrite,
                    desired,
                    ReconciliationRequired: true)
                : null,
            Tombstones:
            [
                new ThermalConvergenceTombstone(
                    PowerSource.PluggedIn,
                    ThermalConvergenceContradiction.Performance,
                    [ThermalComponent.Performance],
                    performanceRevision,
                    FanDesiredRevision: null,
                    policyRevision,
                    ThermalTransitionDisposition.External),
            ],
            TerminalDisposition: ThermalTransitionDisposition.External,
            TerminalAffectedComponents:
                [ThermalComponent.Performance, ThermalComponent.Fans],
            Budgets:
            [
                new ThermalConvergenceBudget(
                    PowerSource.PluggedIn,
                    ThermalComponent.Performance,
                    performanceRevision,
                    policyRevision,
                    ConsumedClaims: 1,
                    MaximumClaims: 1),
                new ThermalConvergenceBudget(
                    PowerSource.PluggedIn,
                    ThermalComponent.Fans,
                    fanRevision,
                    policyRevision,
                    ConsumedClaims: 1,
                    MaximumClaims: 1),
            ]);
    }

    private static DesiredDeviceState CreateDesired()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(
                PerformancePreset.Balanced,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(
                FanMode.Automatic,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                defaults.PluggedIn.Lighting,
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                defaults.BatteryPolicy,
                protectionEnabled: true,
                temporaryFullCharge: false))
        {
            FanControl = FanControlSettings.FromLegacy(FanMode.Automatic),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
