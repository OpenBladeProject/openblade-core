using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native aggregate reader is pinned against the managed store: every
// expectation is what BatteryConfigurationStore.LoadAsync does with a committed
// aggregate at the case's schema, including the documents it rewrites.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeProfileAggregateVectors = "profile-aggregate-vectors.json";

    [Fact]
    public async Task NativeProfileAggregateVectors_MatchManagedLoadAndMigration()
    {
        _ = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeProfileAggregateVectors)
            : Path.Combine(output, NativeProfileAggregateVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var failures = new List<string>();
        int index = 0;
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonObject observed = await ObserveNativeProfileAggregateAsync(vector, validator, index++);
            if (!string.IsNullOrEmpty(output))
            {
                vector["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(vector["expect"], observed))
            {
                failures.Add($"{vector["name"]}: {observed.ToJsonString()}");
            }
        }

        if (!string.IsNullOrEmpty(output))
        {
            var text = new StringBuilder("{\n\"comment\":")
                .Append(root["comment"]!.ToJsonString())
                .Append(",\n\"cases\":[\n")
                .AppendJoin(",\n", root["cases"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    private async Task<JsonObject> ObserveNativeProfileAggregateAsync(
        JsonObject vector,
        ProfileSetValidator validator,
        int index)
    {
        byte[] profile = Encoding.UTF8.GetBytes(vector["profile"]!.ToJsonString());
        byte[] preferences = Encoding.UTF8.GetBytes(vector["preferences"]!.ToJsonString());
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                vector["schema"]!.GetValue<int>(),
                Convert.ToHexString(SHA256.HashData(profile)),
                Convert.ToHexString(SHA256.HashData(preferences))),
            ProfileStore.SerializerOptions);
        string root = Path.Combine(directory, $"aggregate-{index}");
        Directory.CreateDirectory(root);
        string profilePath = Path.Combine(root, "profiles.json");
        string preferencesPath = Path.Combine(root, "battery-preferences.json");
        string markerPath = Path.Combine(root, "battery-configuration.commit.json");
        File.WriteAllBytes(profilePath, profile);
        File.WriteAllBytes(preferencesPath, preferences);
        File.WriteAllBytes(markerPath, marker);
        var store = new BatteryConfigurationStore(profilePath, preferencesPath, markerPath, validator);

        BatteryConfiguration configuration;
        try
        {
            configuration = await store.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not a recoverable document error: the load itself fails.
            return new JsonObject { ["throws"] = exception.GetType().Name };
        }

        byte[] storedProfile = File.ReadAllBytes(profilePath);
        byte[] storedPreferences = File.ReadAllBytes(preferencesPath);
        if (!configuration.IsAuthoritative)
        {
            Assert.Equal(profile, storedProfile);
            Assert.Equal(preferences, storedPreferences);
            return new JsonObject
            {
                ["admitted"] = false,
                ["reason"] = configuration.QuarantineReason.ToString(),
            };
        }

        bool migrated = !profile.AsSpan().SequenceEqual(storedProfile)
            || !preferences.AsSpan().SequenceEqual(storedPreferences);
        var observed = new JsonObject { ["admitted"] = true, ["migrated"] = migrated };
        if (migrated)
        {
            // Migration rewrites all three documents at the current schema.
            observed["profileSha256"] = Convert.ToHexString(SHA256.HashData(storedProfile));
            observed["preferencesSha256"] = Convert.ToHexString(SHA256.HashData(storedPreferences));
            observed["markerSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(markerPath)));
            if (vector["showDocument"]?.GetValue<bool>() == true)
            {
                observed["profileDocument"] = Encoding.UTF8.GetString(storedProfile);
                observed["preferencesDocument"] = Encoding.UTF8.GetString(storedPreferences);
            }
        }
        else
        {
            Assert.Equal(marker, File.ReadAllBytes(markerPath));
        }

        return observed;
    }
}
