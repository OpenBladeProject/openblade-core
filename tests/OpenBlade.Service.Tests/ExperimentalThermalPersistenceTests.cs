using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ExperimentalThermalPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.ThermalPersistence", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactDeviceProfiles_RoundTripBoostAndMaximumFans()
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High) { MaximumFans = true };
        DeviceCapabilities caps = DeviceCapabilities.SafeTargetDefaults() with
        {
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High),
            })
            {
                SupportsMaximumFans = true,
            },
        };
        var defaults = ProfileDefaults.CreateSafeDefaults();
        var profiles = defaults with { PluggedIn = defaults.PluggedIn with { Performance = PerformancePreset.Custom, CustomPerformance = custom } };
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"), ProfileSetValidator.CreateForDeviceCapabilities(caps));
        await store.SaveAsync(profiles, CancellationToken.None);
        Assert.Equal(custom, (await store.LoadAsync(CancellationToken.None)).Profiles.PluggedIn.CustomPerformance);
        Assert.Contains("\"schemaVersion\": 22", await File.ReadAllTextAsync(Path.Combine(directory, "profiles.json")));
    }

    [Fact]
    public async Task Schema19Profiles_MigrateWithMaximumFansOff()
    {
        Directory.CreateDirectory(directory);
        var defaults = ProfileDefaults.CreateSafeDefaults();
        var profiles = defaults with { PluggedIn = defaults.PluggedIn with { Performance = PerformancePreset.Custom, CustomPerformance = CustomPerformanceSettings.Default } };
        JsonNode document = JsonSerializer.SerializeToNode(new ProfileDocument(19, profiles), ProfileStore.SerializerOptions)!;
        document["profiles"]!["pluggedIn"]!["customPerformance"]!.AsObject().Remove("maximumFans");
        string path = Path.Combine(directory, "profiles.json");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        ProfileLoadResult result = await new ProfileStore(path).LoadAsync(CancellationToken.None);
        Assert.False(result.RecoveredFromInvalidFile);
        Assert.False(result.Profiles.PluggedIn.CustomPerformance!.MaximumFans);
        Assert.Contains("\"schemaVersion\": 22", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(CustomPerformanceLevel.Boost, true)]
    [InlineData(CustomPerformanceLevel.High, false)]
    [InlineData(CustomPerformanceLevel.Medium, true)]
    public async Task StoredProfiles_PreserveAdvancedIntentWhenModelIsUnavailable(
        CustomPerformanceLevel cpu, bool maximumFans)
    {
        var custom = new CustomPerformanceSettings(cpu, CustomPerformanceLevel.High) { MaximumFans = maximumFans };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Performance = PerformancePreset.Custom, CustomPerformance = custom },
        };
        string path = Path.Combine(directory, "stored.json");
        await new ProfileStore(path).SaveAsync(profiles, CancellationToken.None);
        ProfileLoadResult loaded = await new ProfileStore(path,
            ProfileSetValidator.CreateForDeviceCapabilities(DeviceCapabilities.Unavailable())).LoadAsync(CancellationToken.None);
        Assert.False(loaded.RecoveredFromInvalidFile);
        Assert.Equal(custom, loaded.Profiles.PluggedIn.CustomPerformance);
        Assert.Equal(ConfigurationLoadDisposition.Admitted, loaded.Disposition);
        Assert.Throws<InvalidDataException>(() => ProfileSetValidator.CreateForDeviceCapabilities(
            DeviceCapabilities.SafeTargetDefaults()).Validate(profiles));
    }

    [Fact]
    public void Profiles_RejectMaximumFansTogetherWithManualTargets()
    {
        DeviceCapabilities caps = DeviceCapabilities.SafeTargetDefaults() with
        {
            CustomPerformance = DeviceCapabilities.SafeTargetDefaults().CustomPerformance! with
            {
                SupportsMaximumFans = true,
            },
        };
        var defaults = ProfileDefaults.CreateSafeDefaults();
        var conflicting = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = CustomPerformanceSettings.Default with { MaximumFans = true },
                Fans = FanMode.Fixed,
                FanControl = new FanControlSettings(FanMode.Fixed, 3000, ManualFanCurve.Default),
            },
        };
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            ProfileSetValidator.CreateForDeviceCapabilities(caps).Validate(conflicting));
        Assert.Contains("maximum-fan flag requires Automatic", failure.Message);
    }

    [Fact]
    public void DeviceValidation_MissingCustomPolicyDoesNotAdmitChangedIntent()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = CustomPerformanceSettings.Default,
            },
        };
        Assert.Throws<InvalidDataException>(() => ProfileSetValidator.CreateForDeviceCapabilities(
            DeviceCapabilities.Unavailable()).Validate(profiles));
    }

    [Fact]
    public async Task Overrides_RoundTripExpansionAndMigrateSchemaSixWithoutInventingMaximumFans()
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High) { MaximumFans = true };
        string path = Path.Combine(directory, "overrides.json");
        var store = new PersistentOverrideStore(path);
        await store.SaveAsync(new(PerformancePreset.Custom, null, null, custom), CancellationToken.None);
        Assert.Equal(custom, (await store.LoadAsync(CancellationToken.None)).Overrides.CustomPerformance);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(7, document["schemaVersion"]!.GetValue<int>());
        document["schemaVersion"] = 6;
        document["overrides"]!["customPerformance"]!["cpu"] = 1;
        document["overrides"]!["customPerformance"]!.AsObject().Remove("maximumFans");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        PersistentOverrideLoadResult loaded = await new PersistentOverrideStore(path).LoadAsync(CancellationToken.None);
        Assert.False(loaded.Recovered);
        Assert.False(loaded.Overrides.CustomPerformance!.MaximumFans);
        Assert.Contains("\"schemaVersion\": 7", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
