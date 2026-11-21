using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Native active application-profile resolution is pinned against managed.
// Each case drives ProfileAutomationState itself through foreground reports,
// profile updates and power-source changes; each intent row drives the real
// UpdateProfilesAsync and records the inputs and the outcome of its
// activeApplicationIntentChanged predicate. The Rust domain tests read the
// same file.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeProfileResolutionVectors = "profile-resolution-vectors.json";

    // UpdateProfilesAsync applies the full transition, and says so, only when
    // the predicate holds on a verified, writable device.
    private const string NativeIntentAppliedMessage = "Profiles were stored.";

    // A case without "confirmed" resolves with nothing confirmed.
    private static readonly ConfirmedDeviceState NativeUnknownConfirmedState = new(null, null, null, null);

    [Fact]
    public async Task NativeProfileResolutionVectors_MatchManagedAutomationState()
    {
        DeviceCapabilities capabilities = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeProfileResolutionVectors)
            : Path.Combine(output, NativeProfileResolutionVectors);
        JsonObject root = ReadNativeResolutionVectors(File.ReadAllText(source));
        JsonNode defaults = JsonSerializer.SerializeToNode(NativeResolutionBase(), ProfileStore.SerializerOptions)!;
        if (!string.IsNullOrEmpty(output))
        {
            root["base"] = defaults.DeepClone();
        }

        Assert.True(JsonNode.DeepEquals(root["base"], defaults));
        var failures = new List<string>();
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonArray observed = ObserveNativeResolutionCase(defaults, vector, validator);
            Record(vector, observed);
        }

        int index = 0;
        foreach (JsonNode? entry in root["intent"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonObject observed = await ObserveNativeIntentAsync(
                defaults, vector, capabilities, validator, index++);
            Record(vector, observed);
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
                .Append("\n],\n\"intent\":[\n")
                .AppendJoin(",\n", root["intent"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Record(JsonObject vector, JsonNode observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                vector["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(vector["expect"], observed))
            {
                failures.Add($"{vector["name"]}: {observed.ToJsonString()}");
            }
        }
    }

    [Theory]
    [InlineData("""{"comment":"","base":{},"cases":[],"intent":[],"extra":1}""")]
    [InlineData("""{"comment":"","base":{},"cases":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":4,"steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[],"expected":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"report":null,"power":1}]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"sleep":1}]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"report":7}]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"update":{}}]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":"0","steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[],"intent":[{"name":"x","report":null}]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[],"intent":[{"name":"x","report":null,"update":[],"power":0}]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"steps":[{"power":4}]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"projectUnclassified":1,"steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":0,"supportsManualFansOnLimitedPower":null,"steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":null,"fans":null,"lighting":null},"steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":null,"fans":null,"lighting":null,"customPerformance":null,"battery":null},"steps":[]}],"intent":[]}""")]
    [InlineData("""{"comment":"","base":{},"cases":[{"name":"x","power":3,"confirmed":{"performance":"1","fans":null,"lighting":null,"customPerformance":null},"steps":[]}],"intent":[]}""")]
    public void NativeProfileResolutionVectors_RejectMalformedOrUnknownRows(string text) =>
        Assert.Throws<InvalidDataException>(() => ReadNativeResolutionVectors(text));

    // The file shape, checked before any row runs: an unknown or misspelt
    // member would otherwise be ignored and its case silently weakened.
    private static JsonObject ReadNativeResolutionVectors(string text)
    {
        JsonObject root = Shape(JsonNode.Parse(text), ["comment", "base", "cases", "intent"], ["comment", "base", "cases", "intent"]);
        foreach (JsonNode? entry in Rows(root["cases"]))
        {
            JsonObject vector = Shape(
                entry,
                ["name", "power", "projectUnclassified", "supportsManualFansOnLimitedPower", "confirmed", "profiles", "steps", "expect"],
                ["name", "power", "steps"]);
            Power(vector["power"]);
            foreach (string flag in (string[])["projectUnclassified", "supportsManualFansOnLimitedPower"])
            {
                if (vector.ContainsKey(flag)
                    && vector[flag]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException($"{flag} is true or false.");
                }
            }

            if (vector.ContainsKey("confirmed"))
            {
                string[] members = ["performance", "fans", "lighting", "customPerformance"];
                JsonObject confirmed = Shape(vector["confirmed"], members, members);
                foreach ((string name, int maximum) in (ReadOnlySpan<(string, int)>)[("performance", 9), ("fans", 4)])
                {
                    if (confirmed[name] is { } value
                        && (value.GetValueKind() != JsonValueKind.Number || value.GetValue<int>() is var number && (number < 0 || number > maximum)))
                    {
                        throw new InvalidDataException($"A confirmed {name} is null or 0 to {maximum}.");
                    }
                }
            }

            Edits(vector["profiles"]);
            foreach (JsonNode? step in Rows(vector["steps"]))
            {
                JsonObject operation = Shape(step, ["report", "update", "power", "nativeDifference"], []);
                string[] kinds = [.. operation.Select(member => member.Key).Where(name => name != "nativeDifference")];
                switch (kinds)
                {
                    case ["report"]:
                        ReportedPath(operation["report"]);
                        break;
                    case ["update"]:
                        Edits(operation["update"]);
                        break;
                    case ["power"]:
                        Power(operation["power"]);
                        break;
                    default:
                        throw new InvalidDataException("A step is exactly one report, update or power change.");
                }
            }
        }

        foreach (JsonNode? entry in Rows(root["intent"]))
        {
            JsonObject vector = Shape(entry, ["name", "profiles", "report", "update", "expect"], ["name", "report", "update"]);
            Edits(vector["profiles"]);
            ReportedPath(vector["report"]);
            Edits(vector["update"]);
        }

        return root;

        static JsonObject Shape(JsonNode? node, string[] allowed, string[] required)
        {
            if (node is not JsonObject value
                || value.Any(member => !allowed.Contains(member.Key))
                || required.Any(name => !value.ContainsKey(name)))
            {
                throw new InvalidDataException($"Unexpected vector row: {node?.ToJsonString()}");
            }

            return value;
        }

        static JsonArray Rows(JsonNode? node) =>
            node as JsonArray ?? throw new InvalidDataException("Expected an array.");

        static void Edits(JsonNode? node)
        {
            if (node is not null and not JsonArray)
            {
                throw new InvalidDataException("Profile edits are an array.");
            }
        }

        static void ReportedPath(JsonNode? node)
        {
            if (node is not null && node.GetValueKind() != JsonValueKind.String)
            {
                throw new InvalidDataException("A reported path is a string or null.");
            }
        }

        static void Power(JsonNode? node)
        {
            if (node?.GetValueKind() != JsonValueKind.Number
                || node.GetValue<int>() is < (int)PowerSource.PluggedIn or > (int)PowerSource.ExternalUnknown)
            {
                throw new InvalidDataException("A power source is 0 to 3.");
            }
        }
    }

    // Safe defaults with one configured laptop behaviour and three rules: an
    // enabled game, a disabled rule and an enabled rule with a non-ASCII path.
    private static ProfileSet NativeResolutionBase()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                LaptopBehavior = new LaptopBehaviorProfileSettings(true, FunctionKeyPrimaryMode.FunctionKeys, false),
            },
            ApplicationProfiles =
            [
                new ApplicationProfileDefinition(
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"),
                    "Game",
                    @"C:\Games\Game.exe",
                    ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride
                        {
                            Performance = PerformancePreset.Performance,
                            GamingModeEnabled = true,
                        },
                        Battery = new ApplicationPowerProfileOverride
                        {
                            Performance = PerformancePreset.BatteryBalanced,
                        },
                    }),
                new ApplicationProfileDefinition(
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002"),
                    "Disabled",
                    @"C:\Games\Disabled.exe",
                    ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride { Performance = PerformancePreset.Silent },
                    },
                    Enabled: false),
                new ApplicationProfileDefinition(
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003"),
                    "Editor",
                    @"C:\Tools\Éditeur.exe",
                    ApplicationActivationMode.WhileRunningAfterActivation,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride { Fans = FanMode.Maximum },
                        UsbC = new ApplicationPowerProfileOverride
                        {
                            GamingModeKeyPolicy = new GamingModeKeyPolicy(true, false),
                        },
                    }),
            ],
        };
    }

    // A saved set: the document form the store reads, already normalised and
    // accepted by the production validator, as the store requires.
    private static ProfileSet ReadNativeResolutionProfiles(
        JsonNode defaults, JsonNode? edits, ProfileSetValidator validator, string name)
    {
        ProfileSet profiles = JsonSerializer.Deserialize<ProfileSet>(
            ApplyNativeProfileEdits(defaults, edits).ToJsonString(),
            ProfileStore.SerializerOptions)!;
        ProfileSet normalized = ProfileStore.NormalizeLegacyBatteryProfile(
            profiles, out bool migrated, batteryProtectionDefault: null);
        Assert.False(migrated, name);
        validator.ValidateStoredCustomIntent(normalized);
        return normalized;
    }

    private static JsonArray ObserveNativeResolutionCase(
        JsonNode defaults, JsonObject vector, ProfileSetValidator validator)
    {
        string name = vector["name"]!.GetValue<string>();
        // ProjectUnclassifiedIntent is the runtime's; ResolveAutomationDesired
        // hands it to Resolve as the observed source, as this does, with the
        // device's SupportsManualFansOnLimitedPower.
        bool projectUnclassified = vector["projectUnclassified"]?.GetValue<bool>() == true;
        bool supports = vector["supportsManualFansOnLimitedPower"]?.GetValue<bool>() != false;
        ConfirmedDeviceState confirmed = vector["confirmed"] is JsonObject reported
            ? new ConfirmedDeviceState(
                (PerformancePreset?)reported["performance"]?.GetValue<int>(),
                (FanMode?)reported["fans"]?.GetValue<int>(),
                Battery: null,
                reported["lighting"].Deserialize<LightingSettings>(ProfileStore.SerializerOptions))
            {
                CustomPerformance = reported["customPerformance"].Deserialize<CustomPerformanceSettings>(
                    ProfileStore.SerializerOptions),
            }
            : NativeUnknownConfirmedState;
        var state = new ProfileAutomationState(
            ReadNativeResolutionProfiles(defaults, vector["profiles"], validator, name),
            new BatteryAutomationPreferences(),
            (PowerSource)vector["power"]!.GetValue<int>());
        DesiredDeviceState Desired() => state.Resolve(
            confirmed, supports, projectUnclassified ? PowerSource.ExternalUnknown : null);
        var observed = new JsonArray();
        foreach (JsonNode? entry in vector["steps"]!.AsArray())
        {
            JsonObject step = entry!.AsObject();
            var result = new JsonObject();
            if (step.TryGetPropertyValue("report", out JsonNode? report))
            {
                result["changed"] = state.SetApplicationContext(report?.GetValue<string>());
            }
            else if (step.TryGetPropertyValue("update", out JsonNode? update))
            {
                // activeApplicationIntentChanged
                // (ServiceControlRuntime.cs:2116-2119) over the Resolve call
                // ResolveAutomationDesired makes, with no override active.
                Guid? priorId = state.ActiveApplicationProfileId;
                DesiredDeviceState priorDesired = Desired();
                LaptopBehaviorProfileSettings priorBehavior = state.EffectiveLaptopBehavior;
                state.UpdateProfiles(ReadNativeResolutionProfiles(defaults, update, validator, name));
                result["intentChanged"] = priorId.HasValue
                    && (state.ActiveApplicationProfileId != priorId
                        || Desired() != priorDesired
                        || state.EffectiveLaptopBehavior != priorBehavior);
            }
            else
            {
                result["changed"] = state.ChangePowerSource((PowerSource)step["power"]!.GetValue<int>());
            }

            ProjectNativeResolution(state, projectUnclassified, supports, confirmed, result);
            observed.Add(result);
        }

        return observed;
    }

    // "profile" is the profile Resolve starts from: on a classified source,
    // ApplicationProfileResolution.Apply over its base and the override it
    // selects; on ExternalUnknown, CreateObservedFallbackProfile with no
    // override. "intent" is Resolve itself with no manual override, less the
    // temporary full charge no case sets. The private inputs are read as the
    // getters and Resolve read them.
    private static void ProjectNativeResolution(
        ProfileAutomationState state,
        bool projectUnclassified,
        bool supportsManualFansOnLimitedPower,
        ConfirmedDeviceState confirmed,
        JsonObject result)
    {
        static object? Private(ProfileAutomationState state, string method, params object[] arguments) =>
            typeof(ProfileAutomationState)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(state, arguments);

        var applicationOverride = (ApplicationPowerProfileOverride?)Private(state, "GetApplicationOverride");
        var baseline = (PowerProfile?)Private(state, "GetActiveProfile");
        Assert.Equal(
            baseline is null
                ? LaptopBehaviorProfileSettings.Unconfigured()
                : ApplicationProfileResolution.Apply(baseline, applicationOverride).EffectiveLaptopBehavior,
            state.EffectiveLaptopBehavior);
        PowerSource source = projectUnclassified ? PowerSource.ExternalUnknown : state.PowerSource;
        result["activeId"] = state.ActiveApplicationProfileId?.ToString();
        PowerProfile profile = source == PowerSource.ExternalUnknown
            ? ApplicationProfileResolution.Apply(
                (PowerProfile)Private(state, "CreateObservedFallbackProfile", confirmed)!, null)
            : ApplicationProfileResolution.Apply(state.Profiles.ForPowerSource(source), applicationOverride);
        result["profile"] = JsonSerializer.SerializeToNode(profile, ProfileStore.SerializerOptions);
        DesiredDeviceState desired = state.Resolve(
            confirmed,
            supportsManualFansOnLimitedPower,
            projectUnclassified ? PowerSource.ExternalUnknown : null);
        JsonObject intent = JsonSerializer.SerializeToNode(desired, ProfileStore.SerializerOptions)!.AsObject();
        JsonObject battery = intent["battery"]!.AsObject();
        Assert.False(battery["temporaryFullCharge"]!.GetValue<bool>());
        battery.Remove("temporaryFullCharge");
        result["intent"] = intent;
        result["laptopBehavior"] = JsonSerializer.SerializeToNode(
            state.EffectiveLaptopBehavior, ProfileStore.SerializerOptions);
    }

    private async Task<JsonObject> ObserveNativeIntentAsync(
        JsonNode defaults,
        JsonObject vector,
        DeviceCapabilities capabilities,
        ProfileSetValidator validator,
        int index)
    {
        string name = vector["name"]!.GetValue<string>();
        ProfileSet saved = ReadNativeResolutionProfiles(defaults, vector["profiles"], validator, name);
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
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = capabilities;
        BatteryConfiguration loaded = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsAuthoritative, name);
        runtime.Initialize(loaded, PowerSource.PluggedIn, PersistentOverrideState.Empty);
        await runtime.ObserveAsync(CancellationToken.None);
        if (vector["report"]?.GetValue<string>() is { } path)
        {
            ControlOperationResult reported = await runtime.ReportApplicationContextAsync(
                new ReportApplicationContextRequest(
                    OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), index + 1, path),
                static () => true,
                CancellationToken.None);
            // Only the recorded selection matters here. A transition the
            // scripted device cannot complete in full refuses the unsupported
            // value without blocking later writes, which the next check pins.
            Assert.DoesNotContain("rejected", reported.Message, StringComparison.Ordinal);
        }

        ServiceStatusSnapshot before = runtime.GetStatus();
        Assert.True(before.DeviceVerified && !before.WritesBlocked, name);
        ControlOperationResult result = await runtime.UpdateProfilesAsync(
            ReadNativeResolutionProfiles(defaults, vector["update"], validator, name),
            CancellationToken.None);
        bool intentChanged = result.Message.StartsWith(NativeIntentAppliedMessage, StringComparison.Ordinal);
        // Stored either way: the set was not refused.
        Assert.True(
            intentChanged || result.Message == "Profiles stored without automatic hardware application.",
            $"{name}: {result.Message}");
        ServiceStatusSnapshot after = runtime.GetStatus();
        return new JsonObject
        {
            ["priorId"] = before.ActiveApplicationProfileId?.ToString(),
            ["id"] = after.ActiveApplicationProfileId?.ToString(),
            ["desiredChanged"] = after.DesiredState != before.DesiredState,
            ["laptopBehaviorChanged"] = after.EffectiveLaptopBehavior != before.EffectiveLaptopBehavior,
            ["intentChanged"] = intentChanged,
        };
    }
}
