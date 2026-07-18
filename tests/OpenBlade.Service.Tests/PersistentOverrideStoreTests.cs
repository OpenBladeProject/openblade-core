using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PersistentOverrideStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.PersistentOverrideStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsTypedOverrides()
    {
        PersistentOverrideStore store = CreateStore();
        var expected = new PersistentOverrideState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            new LightingSettings(
                LightingEffect.Static,
                new RgbColor(12, 34, 56),
                Brightness: 70));

        await store.SaveAsync(expected, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.Recovered);
        Assert.Equal(expected, loaded.Overrides);
    }

    [Fact]
    public async Task Load_CorruptDocumentRecoversOnceToEmptyState()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{invalid-json");
        var store = new PersistentOverrideStore(path);

        PersistentOverrideLoadResult recovered = await store.LoadAsync(CancellationToken.None);
        PersistentOverrideLoadResult verified = await store.LoadAsync(CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(PersistentOverrideState.Empty, recovered.Overrides);
        Assert.False(verified.Recovered);
        Assert.Equal(PersistentOverrideState.Empty, verified.Overrides);
    }

    [Fact]
    public async Task Save_WriteFailurePreservesPreviouslyCommittedState()
    {
        string path = Path.Combine(directory, "manual-overrides.json");
        var baseline = new PersistentOverrideState(
            PerformancePreset.Silent,
            null,
            null);
        await new PersistentOverrideStore(path).SaveAsync(baseline, CancellationToken.None);
        var failing = new PersistentOverrideStore(
            path,
            _ => Task.FromException(new IOException("disk unavailable")));

        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(
            baseline with { Performance = PerformancePreset.Performance },
            CancellationToken.None));

        PersistentOverrideLoadResult loaded =
            await new PersistentOverrideStore(path).LoadAsync(CancellationToken.None);
        Assert.Equal(baseline, loaded.Overrides);
    }

    [Fact]
    public async Task Save_LegacyOffNormalizesToTypedOff()
    {
        PersistentOverrideStore store = CreateStore();
        var legacy = new PersistentOverrideState(
            null,
            null,
            new LightingSettings(LightingEffect.Off, default, Brightness: 0));

        await store.SaveAsync(legacy, CancellationToken.None);
        PersistentOverrideLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.IsType<OffLightingParameters>(loaded.Overrides.Lighting?.Parameters);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private PersistentOverrideStore CreateStore() => new(
        Path.Combine(directory, "manual-overrides.json"));
}
