using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.WriterFence.Scratch;

namespace OpenBlade.Service.Tests;

public sealed class MachineConfigurationWriterTests : IDisposable
{
    private readonly ScratchConfigurationWriter writer = new(Path.Combine(
        Path.GetTempPath(), "OpenBlade.MachineConfigurationWriterTests", Guid.NewGuid().ToString("N")));

    internal const string LegacyOverride = """
        {
          "schemaVersion": 1,
          "overrides": { "performance": 2, "fans": null, "lighting": null }
        }
        """;

    [Fact]
    public async Task OverrideLoad_ContentionAndRecoveryPreserveLegacyBytes()
    {
        MachineConfigurationWriterBinding binding = Bind();
        await File.WriteAllTextAsync(binding.OverridePath, LegacyOverride);
        var store = new PersistentOverrideStore(binding);
        var before = Snapshot();

        using (writer.Acquire())
        {
            await UserConfigurationWriterLeaseTests.AssertContendedAsync(
                () => store.LoadAsync(CancellationToken.None));
            Assert.Equal(PerformancePreset.Performance,
                (await store.InspectAsync(CancellationToken.None)).Result.Overrides.Performance);
            AssertSnapshot(before);
        }

        await File.WriteAllBytesAsync(binding.MarkerPath + ".recovery-required", [1, 3, 5]);
        before = Snapshot();
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(
            () => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(
            () => store.SaveAsync(PersistentOverrideState.Empty, CancellationToken.None));
        using IDisposable successor = writer.Acquire();
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideLoad_HoldsLeaseForSchemaAndCurrentSchemaNormalization(bool currentSchema)
    {
        MachineConfigurationWriterBinding binding = Bind();
        if (currentSchema)
        {
            var visible = new PersistentOverrideState(null, null,
                LightingSettingsFactory.BreathingSingle(new RgbColor(40, 50, 60), 73));
            await new PersistentOverrideStore(binding.OverridePath).SaveAsync(visible, CancellationToken.None);
            string json = await File.ReadAllTextAsync(binding.OverridePath);
            await File.WriteAllTextAsync(binding.OverridePath,
                json.Replace("\"brightness\": 73", "\"brightness\": 0", StringComparison.Ordinal));
        }
        else
        {
            await File.WriteAllTextAsync(binding.OverridePath, LegacyOverride);
        }
        string legacyStage = binding.OverridePath + ".tmp";
        await File.WriteAllTextAsync(legacyStage, "preserve preexisting stage");
        byte[] before = await File.ReadAllBytesAsync(binding.OverridePath);

        int writes = 0;
        var store = new PersistentOverrideStore(binding, async _ =>
        {
            writes++;
            await UserConfigurationWriterLeaseTests.AssertContendedAsync(
                () => new PersistentOverrideStore(binding).LoadAsync(CancellationToken.None));
        });
        PersistentOverrideLoadResult migrated = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, writes);
        Assert.Equal(ConfigurationLoadDisposition.Admitted, migrated.Disposition);
        using IDisposable successor = writer.Acquire();
        Assert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(binding.OverridePath)));
        Assert.Equal("preserve preexisting stage", await File.ReadAllTextAsync(legacyStage));
        Assert.Equal(migrated,
            (await new PersistentOverrideStore(binding).InspectAsync(CancellationToken.None)).Result);
    }

    [Fact]
    public async Task AggregateSaves_ContentionAndRecoveryPreserveCommittedBytes()
    {
        MachineConfigurationWriterBinding binding = Bind();
        var store = new BatteryConfigurationStore(binding);
        BatteryConfiguration initial = await new MachineProfileBootstrapper(store)
            .InitializeAsync(CancellationToken.None);
        ProfileSet updated = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
        var before = Snapshot();

        using (writer.Acquire())
        {
            await UserConfigurationWriterLeaseTests.AssertContendedAsync(
                () => store.SaveProfilesAsync(updated, CancellationToken.None));
            await UserConfigurationWriterLeaseTests.AssertContendedAsync(
                () => store.SaveConfigurationAsync(updated, initial.Preferences, CancellationToken.None));
            AssertSnapshot(before);
        }

        await File.WriteAllTextAsync(binding.MarkerPath + ".recovery-required", "unresolved");
        before = Snapshot();
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(
            () => store.SaveProfilesAsync(updated, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(
            () => store.SaveConfigurationAsync(updated, initial.Preferences, CancellationToken.None));
        using IDisposable successor = writer.Acquire();
        AssertSnapshot(before);
    }

    [Fact]
    public async Task AggregateSave_RollsBackWhileExclusionIsHeldAndCanRetry()
    {
        MachineConfigurationWriterBinding binding = Bind();
        bool failAfterReplacement = false;
        var store = new BatteryConfigurationStore(binding, transactionBoundary: async (boundary, _) =>
        {
            if (failAfterReplacement && boundary == BatteryConfigurationCommitBoundary.AfterPreferencesReplacement)
            {
                await UserConfigurationWriterLeaseTests.AssertContendedAsync(
                    () => new PersistentOverrideStore(binding).LoadAsync(CancellationToken.None));
                throw new IOException("Injected save failure.");
            }
        });
        BatteryConfiguration initial = await store.LoadAsync(CancellationToken.None);
        BatteryAutomationPreferences updated = initial.Preferences with
        {
            LastKnownLighting = LightingSettingsFactory.Off(),
        };
        var before = Snapshot();
        failAfterReplacement = true;

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => store.SaveConfigurationAsync(initial.Profiles, updated, CancellationToken.None));
        Assert.Equal("Injected save failure.", failure.Message);
        AssertSnapshot(before);

        failAfterReplacement = false;
        await store.SaveConfigurationAsync(initial.Profiles, updated, CancellationToken.None);
        using IDisposable successor = writer.Acquire();
        Assert.Equal(updated, (await store.InspectAsync(CancellationToken.None)).Configuration.Preferences);
    }

    [Fact]
    public async Task SequentialWriters_RejectStaleAggregateAndOverrideEvenAfterReadOnlyInspection()
    {
        MachineConfigurationWriterBinding binding = Bind();
        var first = new BatteryConfigurationStore(binding);
        var second = new BatteryConfigurationStore(binding);
        var firstOverrides = new PersistentOverrideStore(binding);
        var secondOverrides = new PersistentOverrideStore(binding);
        BatteryConfiguration prior = await first.LoadAsync(CancellationToken.None);
        await second.LoadAsync(CancellationToken.None);
        await firstOverrides.LoadAsync(CancellationToken.None);
        await secondOverrides.LoadAsync(CancellationToken.None);
        ProfileSet newer = prior.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
        var newerOverrides = PersistentOverrideState.Empty with { Performance = PerformancePreset.Performance };

        await second.SaveProfilesAsync(newer, CancellationToken.None);
        await secondOverrides.SaveAsync(newerOverrides, CancellationToken.None);
        Assert.Equal(newer, (await first.InspectAsync(CancellationToken.None)).Configuration.Profiles);
        Assert.Equal(newerOverrides, (await firstOverrides.InspectAsync(CancellationToken.None)).Result.Overrides);
        var before = Snapshot();

        Assert.Contains("changed since", (await Assert.ThrowsAsync<IOException>(
            () => first.SaveProfilesAsync(prior.Profiles, CancellationToken.None))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(
            () => first.SaveConfigurationAsync(prior.Profiles, prior.Preferences, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(
            () => firstOverrides.SaveAsync(PersistentOverrideState.Empty, CancellationToken.None));
        AssertSnapshot(before);

        BatteryConfiguration reloaded = await first.LoadAsync(CancellationToken.None);
        await firstOverrides.LoadAsync(CancellationToken.None);
        await first.SaveConfigurationAsync(reloaded.Profiles, reloaded.Preferences, CancellationToken.None);
        await firstOverrides.SaveAsync(PersistentOverrideState.Empty, CancellationToken.None);
        using IDisposable successor = writer.Acquire();
        Assert.Equal(newer, (await first.InspectAsync(CancellationToken.None)).Configuration.Profiles);
        Assert.Equal(PersistentOverrideState.Empty,
            (await firstOverrides.InspectAsync(CancellationToken.None)).Result.Overrides);
    }

    private MachineConfigurationWriterBinding Bind() => new(writer.Root, writer.Acquire);

    private Dictionary<string, byte[]> Snapshot() =>
        ConfigurationFiles().ToDictionary(file => file, File.ReadAllBytes);

    private void AssertSnapshot(Dictionary<string, byte[]> expected)
    {
        Assert.Equal(expected.Keys.Order(), ConfigurationFiles().Order());
        foreach ((string path, byte[] bytes) in expected)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    private IEnumerable<string> ConfigurationFiles() =>
        Directory.GetFiles(writer.Root).Where(file => Path.GetFileName(file) != WriterLease.Name);

    public void Dispose() => Directory.Delete(writer.Root, recursive: true);
}
