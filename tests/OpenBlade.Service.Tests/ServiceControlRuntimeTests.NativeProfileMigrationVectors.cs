using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native store-open migration is pinned against the managed store: each
// case is the set of stored files BatteryConfigurationStore.LoadAsync opens,
// and its expectation is what that load leaves on disk. A migration rewrites
// all three documents through the recorded transaction; a refusal leaves
// every file byte for byte. The Rust domain and host tests read the same file.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeProfileMigrationVectors = "profile-migration-vectors.json";

    private static readonly string[] NativeMigrationFiles =
        ["profiles.json", "battery-preferences.json", "battery-configuration.commit.json"];

    [Fact]
    public async Task NativeProfileMigrationVectors_MatchManagedStoreOpen()
    {
        _ = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeProfileMigrationVectors)
            : Path.Combine(output, NativeProfileMigrationVectors);
        JsonObject root = ReadNativeMigrationVectors(File.ReadAllText(source));
        var failures = new List<string>();
        int index = 0;
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonObject observed = await ObserveNativeProfileMigrationAsync(vector, validator, index++);
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
            // One case per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(root["comment"]!.ToJsonString())
                .Append(",\n\"cases\":[\n")
                .AppendJoin(",\n", root["cases"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
        // Every schema managed migrates, and the legacy profile, is covered.
        HashSet<int> schemas = root["cases"]!.AsArray()
            .Select(entry => entry!["schema"]?.GetValue<int>())
            .Where(schema => schema is >= 1 and < ProfileStore.CurrentSchemaVersion)
            .Select(schema => schema!.Value)
            .ToHashSet();
        Assert.Equal(Enumerable.Range(1, ProfileStore.CurrentSchemaVersion - 1), schemas.Order());
        Assert.Contains(root["cases"]!.AsArray(), entry => entry!["name"]!.GetValue<string>().StartsWith("legacy ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"comment":"","cases":[],"extra":1}""")]
    [InlineData("""{"comment":"","cases":{}}""")]
    [InlineData("""{"cases":[]}""")]
    [InlineData("""{"comment":"","cases":[{"profile":{}}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"","profile":{}}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x"}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"profileText":"{}"}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","preferences":{},"preferencesText":"{}"}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"schema":1,"markerText":"{}"}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"schema":"1"}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"schema":1.5}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profileText":{}}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"showDocument":1}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"expected":{}}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"nativeDifference":true}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{},"nativeDifference":""}]}""")]
    [InlineData("""{"comment":"","cases":[{"name":"x","profile":{}},{"name":"x","profile":{}}]}""")]
    public void NativeProfileMigrationVectors_RejectMalformedOrUnknownRows(string text) =>
        Assert.Throws<InvalidDataException>(() => ReadNativeMigrationVectors(text));

    // The file shape, checked before any row runs: an unknown or misspelt
    // member would otherwise be ignored and its case silently weakened.
    private static JsonObject ReadNativeMigrationVectors(string text)
    {
        JsonObject root = Shape(JsonNode.Parse(text), ["comment", "cases"], ["comment", "cases"]);
        if (root["cases"] is not JsonArray cases)
        {
            throw new InvalidDataException("The cases are an array.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? entry in cases)
        {
            JsonObject vector = Shape(
                entry,
                ["name", "profile", "profileText", "preferences", "preferencesText", "schema", "markerText", "showDocument", "nativeDifference", "expect"],
                ["name"]);
            if (vector["name"]?.GetValueKind() != JsonValueKind.String
                || vector["name"]!.GetValue<string>().Length == 0
                || !names.Add(vector["name"]!.GetValue<string>()))
            {
                throw new InvalidDataException("A case has a unique, non-empty name.");
            }
            foreach ((string document, string raw) in new[] { ("profile", "profileText"), ("preferences", "preferencesText"), ("schema", "markerText") })
            {
                if (vector.ContainsKey(document) && vector.ContainsKey(raw))
                {
                    throw new InvalidDataException($"A case gives {document} or {raw}, not both.");
                }
                if (vector.ContainsKey(raw) && vector[raw]?.GetValueKind() != JsonValueKind.String)
                {
                    throw new InvalidDataException($"{raw} is a string.");
                }
            }
            if (vector.ContainsKey("schema")
                && (vector["schema"]?.GetValueKind() != JsonValueKind.Number
                    || !vector["schema"]!.AsValue().TryGetValue(out int _)))
            {
                throw new InvalidDataException("A schema is an integer.");
            }
            if (vector.ContainsKey("showDocument")
                && vector["showDocument"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("showDocument is true or false.");
            }
            // A declared parity difference: managed's outcome is still the
            // expectation, and the native readers pin their own refusal.
            if (vector.ContainsKey("nativeDifference")
                && (vector["nativeDifference"]?.GetValueKind() != JsonValueKind.String
                    || vector["nativeDifference"]!.GetValue<string>().Length == 0))
            {
                throw new InvalidDataException("nativeDifference is a non-empty string.");
            }
            if (!new[] { "profile", "profileText", "preferences", "preferencesText", "schema", "markerText" }.Any(vector.ContainsKey))
            {
                throw new InvalidDataException("A case stores at least one file.");
            }
        }

        return root;

        static JsonObject Shape(JsonNode? node, string[] allowed, string[] required)
        {
            if (node is not JsonObject value
                || value.Any(member => !allowed.Contains(member.Key, StringComparer.Ordinal))
                || required.Any(name => !value.ContainsKey(name)))
            {
                throw new InvalidDataException($"Unexpected vector row: {node?.ToJsonString()}");
            }

            return value;
        }
    }

    // The bytes a case stores under one name, or null for an absent file.
    private static byte[]? NativeMigrationDocument(JsonObject vector, string document) =>
        vector.TryGetPropertyValue(document + "Text", out JsonNode? raw)
            ? Encoding.UTF8.GetBytes(raw!.GetValue<string>())
            : vector.TryGetPropertyValue(document, out JsonNode? value)
                ? Encoding.UTF8.GetBytes(value!.ToJsonString())
                : null;

    private async Task<JsonObject> ObserveNativeProfileMigrationAsync(
        JsonObject vector,
        ProfileSetValidator validator,
        int index)
    {
        byte[]? profile = NativeMigrationDocument(vector, "profile");
        byte[]? preferences = NativeMigrationDocument(vector, "preferences");
        byte[]? marker = vector.TryGetPropertyValue("markerText", out JsonNode? markerText)
            ? Encoding.UTF8.GetBytes(markerText!.GetValue<string>())
            : vector.TryGetPropertyValue("schema", out JsonNode? schema)
                ? JsonSerializer.SerializeToUtf8Bytes(
                    new BatteryConfigurationCommit(
                        schema!.GetValue<int>(),
                        Convert.ToHexString(SHA256.HashData(profile ?? [])),
                        Convert.ToHexString(SHA256.HashData(preferences ?? []))),
                    ProfileStore.SerializerOptions)
                : null;
        string root = Path.Combine(directory, $"migration-{index}");
        Directory.CreateDirectory(root);
        byte[]?[] stored = [profile, preferences, marker];
        for (int file = 0; file < NativeMigrationFiles.Length; file++)
        {
            if (stored[file] is { } bytes)
            {
                File.WriteAllBytes(Path.Combine(root, NativeMigrationFiles[file]), bytes);
            }
        }

        var store = new BatteryConfigurationStore(
            Path.Combine(root, NativeMigrationFiles[0]),
            Path.Combine(root, NativeMigrationFiles[1]),
            Path.Combine(root, NativeMigrationFiles[2]),
            validator);
        BatteryConfiguration? configuration = null;
        string? thrown = null;
        try
        {
            configuration = await store.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not a recoverable document error: the load itself fails.
            thrown = exception.GetType().Name;
        }

        // Managed's on-disk outcome: every file the load left, and the bytes
        // of the three documents.
        JsonArray files = new(Directory.EnumerateFiles(root)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .Select(name => (JsonNode?)JsonValue.Create(name))
            .ToArray());
        byte[]?[] left = NativeMigrationFiles
            .Select(name => Path.Combine(root, name))
            .Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null)
            .ToArray();
        bool unchanged = left.Zip(stored).All(pair =>
            pair.First is null ? pair.Second is null : pair.Second is not null && pair.First.AsSpan().SequenceEqual(pair.Second));
        if (thrown is not null)
        {
            return new JsonObject { ["throws"] = thrown, ["unchanged"] = unchanged, ["files"] = files };
        }

        if (!configuration!.IsAuthoritative)
        {
            Assert.True(unchanged, $"{vector["name"]}: a quarantined aggregate is preserved");
            return new JsonObject
            {
                ["admitted"] = false,
                ["reason"] = configuration.QuarantineReason.ToString(),
                ["files"] = files,
            };
        }

        var observed = new JsonObject { ["admitted"] = true, ["migrated"] = !unchanged, ["files"] = files };
        if (!unchanged)
        {
            observed["profileSha256"] = Convert.ToHexString(SHA256.HashData(left[0]!));
            observed["preferencesSha256"] = Convert.ToHexString(SHA256.HashData(left[1]!));
            observed["markerSha256"] = Convert.ToHexString(SHA256.HashData(left[2]!));
            if (vector["showDocument"]?.GetValue<bool>() == true)
            {
                observed["profileDocument"] = Encoding.UTF8.GetString(left[0]!);
                observed["preferencesDocument"] = Encoding.UTF8.GetString(left[1]!);
                observed["markerDocument"] = Encoding.UTF8.GetString(left[2]!);
            }
        }

        return observed;
    }
}
