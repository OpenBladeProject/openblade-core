using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Windows Service's ProfileSet contract is pinned against managed by
// shared vectors. Every expectation comes from the managed IPC codec, the
// production RZ09-0581 validator and the real UpdateProfilesAsync path; the Rust
// tests in rust/crates/openblade-domain read the same files.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeProfileVectors = "profile-set-vectors-v27.json";
    private const string NativeProfileCapabilities = "profile-capabilities-rz09-0581.json";

    // Set to the repository's tools/openblade-windows-host/tests/data directory
    // to rewrite the recorded expectations from managed.
    private const string RegenerateNativeProfileVectors = "OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS";

    [Fact]
    public void NativeProfileCapabilities_MatchTheProductionRz090581Factories()
    {
        DeviceCapabilities capabilities = NativeProfileDeviceCapabilities(out _);
        JsonObject observed = ProjectNativeProfileCapabilities(capabilities);

        if (Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors) is { Length: > 0 } output)
        {
            WriteNativeProfileVector(Path.Combine(output, NativeProfileCapabilities), observed);
            return;
        }

        JsonNode recorded = JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, NativeProfileCapabilities)))!;
        Assert.True(JsonNode.DeepEquals(recorded, observed), observed.ToJsonString());
    }

    [Fact]
    public async Task NativeProfileVectors_MatchManagedCodecValidatorAndStoredBytes()
    {
        DeviceCapabilities capabilities = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeProfileVectors)
            : Path.Combine(output, NativeProfileVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        JsonNode defaults = await SerializeNativeProfileRequestAsync(ProfileDefaults.CreateSafeDefaults());
        if (!string.IsNullOrEmpty(output))
        {
            root["base"] = defaults.DeepClone();
        }

        // The base is what the Tray sends for the managed safe defaults.
        Assert.True(JsonNode.DeepEquals(root["base"], defaults));
        var failures = new List<string>();
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonObject observed = await ObserveNativeProfileVectorAsync(
                defaults, vector, capabilities, validator);
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
                .Append(",\n\"base\":")
                .Append(root["base"]!.ToJsonString())
                .Append(",\n\"cases\":[\n")
                .AppendJoin(",\n", root["cases"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    // The managed composition admits these for exact RZ09-0581: thermal members
    // from the battery factory and lighting members from the lighting factory,
    // which is also how the injected production validator reads them.
    private static DeviceCapabilities NativeProfileDeviceCapabilities(out ProfileSetValidator validator)
    {
        IProductionBatteryPortFactory battery = ProductionBatteryFactory.CreateWindows();
        IProductionLightingPortFactory lighting = ProductionLightingFactory.CreateWindows();
        validator = ServiceComposition.CreateProfileSetValidator(battery, lighting);
        DeviceCapabilities light = lighting.Capabilities;
        return battery.Capabilities with
        {
            LightingEffects = light.LightingEffects,
            KeyboardLightingLayoutId = light.KeyboardLightingLayoutId,
            SupportsBreathingColorSelection = light.SupportsBreathingColorSelection,
            SupportedBreathingColorModes = light.SupportedBreathingColorModes,
            SupportsWaveDirectionSelection = light.SupportsWaveDirectionSelection,
            SupportsIndependentLightingControls = light.SupportsIndependentLightingControls,
            SupportsVaporChamberLighting = light.SupportsVaporChamberLighting,
            SupportsTouchpadStatusIndicator = light.SupportsTouchpadStatusIndicator,
            SupportsKeyboardBrightness = light.SupportsKeyboardBrightness,
            LogoLightingModes = light.LogoLightingModes,
            SupportedLightingRefreshRates = light.SupportedLightingRefreshRates,
        };
    }

    private static JsonObject ProjectNativeProfileCapabilities(DeviceCapabilities capabilities)
    {
        static JsonArray Sorted(IEnumerable<int> values) =>
            new([.. values.Order().Select(value => (JsonNode)value)]);
        static JsonNode Range(FanSpeedRange range) => new JsonObject
        {
            ["minimumRpm"] = range.MinimumRpm,
            ["maximumRpm"] = range.MaximumRpm,
            ["stepRpm"] = range.StepRpm,
        };

        return new JsonObject
        {
            ["performancePresets"] = Sorted(capabilities.PerformancePresets.Select(value => (int)value)),
            ["fanModes"] = Sorted(capabilities.FanModes.Select(value => (int)value)),
            ["lightingEffects"] = Sorted(capabilities.LightingEffects.Select(value => (int)value)),
            ["fanSpeedRange"] = capabilities.FanSpeedRange is { } shared ? Range(shared) : null,
            ["fanSpeedRangesByPerformancePreset"] = new JsonArray([.. capabilities.FanSpeedRangesByPerformancePreset
                .OrderBy(pair => pair.Key)
                .Select(pair => (JsonNode)new JsonObject
                {
                    ["performance"] = (int)pair.Key,
                    ["range"] = Range(pair.Value),
                })]),
            ["customPerformance"] = capabilities.CustomPerformance is { } custom
                ? new JsonObject
                {
                    ["allowedCombinations"] = new JsonArray([.. custom.AllowedCombinations
                        .OrderBy(pair => pair.Cpu)
                        .ThenBy(pair => pair.Gpu)
                        .Select(pair => (JsonNode)new JsonArray((int)pair.Cpu, (int)pair.Gpu))]),
                    ["supportsMaximumFans"] = custom.SupportsMaximumFans,
                }
                : null,
            ["keyboardLightingLayoutId"] = capabilities.KeyboardLightingLayoutId,
            ["supportsBreathingColorSelection"] = capabilities.SupportsBreathingColorSelection,
            ["supportedBreathingColorModes"] = capabilities.SupportedBreathingColorModes is { } modes
                ? Sorted(modes.Select(value => (int)value))
                : null,
            ["supportsWaveDirectionSelection"] = capabilities.SupportsWaveDirectionSelection,
            ["supportsIndependentLightingControls"] = capabilities.SupportsIndependentLightingControls,
            ["supportsVaporChamberLighting"] = capabilities.SupportsVaporChamberLighting,
            ["supportsTouchpadStatusIndicator"] = capabilities.SupportsTouchpadStatusIndicator,
            ["reportsPerformanceWithManualFans"] = capabilities.ReportsPerformanceWithManualFans,
            ["supportsManualFansOnLimitedPower"] = capabilities.SupportsManualFansOnLimitedPower,
            ["supportsBatterySaverFanStop"] = capabilities.SupportsBatterySaverFanStop,
        };
    }

    private async Task<JsonObject> ObserveNativeProfileVectorAsync(
        JsonNode defaults,
        JsonObject vector,
        DeviceCapabilities capabilities,
        ProfileSetValidator validator)
    {
        JsonNode requested = ApplyNativeProfileEdits(defaults, vector["edit"]);
        var frame = new JsonObject
        {
            ["operation"] = "updateProfiles",
            ["version"] = OpenBladeIpc.ProtocolVersion,
            ["requestId"] = "aaaaaaaa-bbbb-cccc-dddd-000000000001",
            ["profiles"] = requested,
        };
        byte[] payload = Encoding.UTF8.GetBytes(frame.ToJsonString());
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Position = 0;
        ProfileSet profiles;
        try
        {
            profiles = ((UpdateProfilesRequest)await IpcMessageCodec.ReadRequestAsync(
                stream, CancellationToken.None)).Profiles;
        }
        catch (InvalidDataException)
        {
            // NamedPipeControlServer answers "IPC request is malformed." and closes.
            return new JsonObject { ["decode"] = false };
        }

        var observed = new JsonObject
        {
            ["decode"] = true,
            ["stored"] = NativeProfileIntentFailure(() => validator.ValidateStoredCustomIntent(
                ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _, batteryProtectionDefault: null))),
            ["full"] = NativeProfileIntentFailure(() => validator.Validate(
                ProfileStore.NormalizeLegacyBatteryProfile(profiles, out _, batteryProtectionDefault: null))),
        };

        ProfileSet saved = JsonSerializer.Deserialize<ProfileSet>(
            ApplyNativeProfileEdits(defaults, vector["saved"]).ToJsonString(),
            ProfileStore.SerializerOptions)!;
        string profilePath = Path.Combine(directory, "profiles.json");
        string preferencesPath = Path.Combine(directory, "battery-preferences.json");
        string markerPath = Path.Combine(directory, "battery-configuration.commit.json");
        Directory.CreateDirectory(directory);
        SeedConfiguration(
            profilePath,
            preferencesPath,
            markerPath,
            new BatteryConfiguration(saved, new BatteryAutomationPreferences(), Recovered: false));
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: validator);
        context.Device.Capabilities = capabilities;
        BatteryConfiguration loaded = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsAuthoritative, vector["name"]!.ToString());
        context.Runtime.Initialize(loaded, PowerSource.PluggedIn, PersistentOverrideState.Empty);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.NotNull(context.Runtime.GetStatus().Capabilities);
        byte[] before = File.ReadAllBytes(profilePath);
        byte[] preferences = File.ReadAllBytes(preferencesPath);
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult result;
        try
        {
            result = await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not an intent failure: the handler has no reply to send.
            Assert.Equal(before, File.ReadAllBytes(profilePath));
            observed["update"] = new JsonObject { ["throws"] = exception.GetType().Name };
            observed["documentSha256"] = null;
            return observed;
        }

        byte[] after = File.ReadAllBytes(profilePath);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Equal(preferences, File.ReadAllBytes(preferencesPath));
        observed["update"] = new JsonObject
        {
            ["status"] = (int)result.Status,
            ["message"] = result.Message,
        };
        if (result.Status == ControlOperationStatus.Stored)
        {
            // The digest pins every stored document; a few cases also show the text.
            observed["documentSha256"] = Convert.ToHexString(SHA256.HashData(after));
            if (vector["showDocument"]?.GetValue<bool>() == true)
            {
                observed["document"] = Encoding.UTF8.GetString(after);
            }
        }
        else
        {
            Assert.Equal(before, after);
            observed["documentSha256"] = null;
        }

        return observed;
    }

    // ServiceControlRuntime.IsIntentFailure: these become a Failed reply.
    private static string? NativeProfileIntentFailure(Action validate)
    {
        try
        {
            validate();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            return $"throws:{exception.GetType().Name}";
        }
    }

    // Each edit is [path, value] to set a member or [path] to remove it.
    private static JsonNode ApplyNativeProfileEdits(JsonNode source, JsonNode? edits)
    {
        JsonNode result = source.DeepClone();
        foreach (JsonNode? edit in edits?.AsArray() ?? [])
        {
            JsonArray pair = edit!.AsArray();
            JsonArray path = pair[0]!.AsArray();
            JsonNode parent = result;
            foreach (JsonNode? step in path.Take(path.Count - 1))
            {
                parent = step!.GetValueKind() == JsonValueKind.Number
                    ? parent[step.GetValue<int>()]!
                    : parent[step.GetValue<string>()]!;
            }

            string name = path[^1]!.GetValue<string>();
            if (pair.Count == 1)
            {
                parent.AsObject().Remove(name);
            }
            else
            {
                parent[name] = pair[1]?.DeepClone();
            }
        }

        return result;
    }

    private static async Task<JsonNode> SerializeNativeProfileRequestAsync(ProfileSet profiles)
    {
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(
            stream,
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles),
            CancellationToken.None);
        return JsonNode.Parse(stream.ToArray().AsSpan(sizeof(int)))!["profiles"]!.DeepClone();
    }

    private static void WriteNativeProfileVector(string path, JsonNode content) =>
        File.WriteAllText(
            path,
            content.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                .Replace("\r\n", "\n") + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
