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
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero))
        {
            RestoreIntent = new HyperBoostRestoreIntent(
                PerformancePreset.Custom,
                CustomPerformanceSettings.Default,
                GpuClockTuningSettings.Disabled,
                new FanControlSettings(
                    FanMode.Fixed,
                    3200,
                    ManualFanCurve.Default)),
            RecoveryBaseline = state ==
                    HyperBoostOwnershipJournalState.RestorePending
                ? new HyperBoostRecoveryBaseline(
                    Performance: null,
                    FanMode.Maximum,
                    HyperBoostActive: false,
                    CustomPerformanceSettings.Default,
                    new GpuClockOffsetSettings(75, 150))
                : null,
        };

        await store.WriteAsync(expected, CancellationToken.None);
        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.Equal(expected, loaded.Journal);
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public async Task WriteAndLoad_RoundTripsLowPowerRestoreIntent(
        PerformancePreset restorePerformance)
    {
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        var expected = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.RestorePending,
            PerformancePreset.Balanced,
            RequiresCoolingPad: false,
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero))
        {
            RestoreIntent = new HyperBoostRestoreIntent(
                restorePerformance,
                CustomPerformance: null,
                GpuClockTuning: null,
                FanControlSettings.FromLegacy(FanMode.Automatic)),
            RecoveryBaseline = new HyperBoostRecoveryBaseline(
                restorePerformance,
                FanMode.Automatic,
                HyperBoostActive: false),
        };

        await store.WriteAsync(expected, CancellationToken.None);
        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.Equal(expected, loaded.Journal);
    }

    [Fact]
    public async Task WriteAndLoad_CurrentRestorePendingWithoutBaselineAllowsExplicitCapture()
    {
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        var store = new HyperBoostOwnershipJournalStore(path);
        var expected = new HyperBoostOwnershipJournal(
            HyperBoostOwnershipJournalState.RestorePending,
            PerformancePreset.Balanced,
            RequiresCoolingPad: false,
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero))
        {
            RestoreIntent = new HyperBoostRestoreIntent(
                PerformancePreset.Performance,
                CustomPerformance: null,
                GpuClockTuning: null,
                new FanControlSettings(
                    FanMode.Fixed,
                    3200,
                    ManualFanCurve.Default)),
        };

        await store.WriteAsync(expected, CancellationToken.None);
        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Invalid);
        Assert.Equal(expected, loaded.Journal);
        Assert.True(loaded.AllowsExplicitRecoveryBaselineCapture);
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

    [Fact]
    public async Task Load_SchemaTwoDefaultsRestoreIntentToRecordedBaseMode()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 2,
              "state": 1,
              "baseMode": 2,
              "requiresCoolingPad": false,
              "updatedAtUtc": "2026-08-11T12:00:00Z"
            }
            """,
            CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        HyperBoostOwnershipJournal journal =
            Assert.IsType<HyperBoostOwnershipJournal>(loaded.Journal);
        Assert.False(loaded.Invalid);
        Assert.Equal(PerformancePreset.Performance, journal.RestoreIntent.Performance);
        Assert.Equal(FanMode.Automatic, journal.RestoreIntent.FanControl.Mode);
    }

    [Fact]
    public async Task Load_SchemaThreeDefaultsRecoveryBaselineToNull()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 3,
              "state": 2,
              "baseMode": 1,
              "requiresCoolingPad": false,
              "updatedAtUtc": "2026-08-11T12:00:00Z",
              "restorePerformance": 2,
              "restoreFanControl": {
                "mode": 1,
                "fixedSpeedRpm": 3200,
                "manualCurve": {
                  "cpu": [
                    { "temperatureCelsius": 40, "speedRpm": 2000 },
                    { "temperatureCelsius": 90, "speedRpm": 5400 }
                  ],
                  "gpu": [
                    { "temperatureCelsius": 35, "speedRpm": 2000 },
                    { "temperatureCelsius": 85, "speedRpm": 5400 }
                  ],
                  "useHigherTemperatureForBoth": true
                }
              }
            }
            """,
            CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        HyperBoostOwnershipJournal journal =
            Assert.IsType<HyperBoostOwnershipJournal>(loaded.Journal);
        Assert.False(loaded.Invalid);
        Assert.Null(journal.RecoveryBaseline);
        Assert.True(loaded.AllowsExplicitRecoveryBaselineCapture);
    }

    [Fact]
    public async Task Load_RecoveryBaselineOnOwnedJournalFailsClosed()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "hyperboost-ownership.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 4,
              "state": 1,
              "baseMode": 1,
              "requiresCoolingPad": false,
              "updatedAtUtc": "2026-08-11T12:00:00Z",
              "restorePerformance": 2,
              "restoreFanControl": {
                "mode": 0,
                "fixedSpeedRpm": 3700,
                "manualCurve": {
                  "cpu": [
                    { "temperatureCelsius": 40, "speedRpm": 2000 },
                    { "temperatureCelsius": 90, "speedRpm": 5400 }
                  ],
                  "gpu": [
                    { "temperatureCelsius": 35, "speedRpm": 2000 },
                    { "temperatureCelsius": 85, "speedRpm": 5400 }
                  ],
                  "useHigherTemperatureForBoth": true
                }
              },
              "recoveryBaseline": {
                "performance": null,
                "fans": 3,
                "hyperBoostActive": false
              }
            }
            """,
            CancellationToken.None);
        var store = new HyperBoostOwnershipJournalStore(path);

        HyperBoostOwnershipJournalLoadResult loaded =
            await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.Invalid);
        Assert.Null(loaded.Journal);
        Assert.True(File.Exists(path));
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
