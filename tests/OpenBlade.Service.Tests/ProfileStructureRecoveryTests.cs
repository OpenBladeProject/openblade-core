using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ProfileStructureRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ProfileStore.CurrentSchemaVersion, "pluggedIn")]
    [InlineData(ProfileStore.CurrentSchemaVersion, "battery")]
    [InlineData(ProfileStore.CurrentSchemaVersion, "usbC")]
    [InlineData(ProfileStore.LegacySchemaVersion, "pluggedIn")]
    [InlineData(ProfileStore.LegacySchemaVersion, "battery")]
    [InlineData(ProfileStore.LegacySchemaVersion, "profiles")]
    public async Task Load_NullRequiredProfileQuarantinesAndPreservesBytes(int schema, string member)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        JsonObject document = schema == ProfileStore.LegacySchemaVersion
            ? JsonSerializer.SerializeToNode(new LegacyProfileDocument(schema,
                new LegacyProfileSet(defaults.PluggedIn, defaults.Battery, defaults.BatteryPolicy)),
                ProfileStore.SerializerOptions)!.AsObject()
            : JsonSerializer.SerializeToNode(new ProfileDocument(schema, defaults),
                ProfileStore.SerializerOptions)!.AsObject();
        if (member == "profiles")
        {
            document[member] = null;
        }
        else
        {
            document["profiles"]![member] = null;
        }
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(document);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profiles.json");
        await File.WriteAllBytesAsync(path, original);

        var store = new ProfileStore(path);
        ProfileLoadResult loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ConfigurationLoadDisposition.Quarantined, loaded.Disposition);
        Assert.Equal(defaults, loaded.Profiles);
        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            store.SaveAsync(defaults, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
