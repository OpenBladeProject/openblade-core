using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service publishes one DeviceCapabilities record, a function of its
// compiled feature groups and whether an exact RZ09-0581 state is confirmed. These
// rows pin what managed's own producers publish for the same composition: the
// production battery port's CreateCapabilities over the production fixture
// restricted to the actions of the row's groups, with the factory's exact-identity
// members (HyperBoost, GPU overclock, THX, local dimming, Fan Stop evidence and
// direct manual fans), merged with the lighting port's record as
// ReadCapabilitiesAsync merges it, then the native restrictions named in the comment,
// written through the real v91 status codec. The Rust tests replay every row.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeCapabilityRecordVectors = "capability-record-vectors.json";
    private const string RegenerateNativeCapabilityRecordVectors =
        "OPENBLADE_REGENERATE_NATIVE_CAPABILITY_RECORD_VECTORS";

    private const string NativeCapabilityRecordComment =
        "Recorded by ServiceControlRuntimeTests.NativeCapabilityRecordVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_CAPABILITY_RECORD_VECTORS set to this directory. An unidentified "
        + "row is DeviceCapabilities.Unavailable(), managed's record before it reads the device. An "
        + "identified row is ProductionBatteryPort.CreateCapabilities for an exact model-scoped "
        + "Blade 16 - RZ09-0581 host over the production fixture restricted to its groups' actions "
        + "(thermal: performance and custom-performance; fans: fan-maximum; battery: battery-limit and "
        + "battery-protection-off; temporaryCharge: charge-to-100, the exact model's temporary full "
        + "charge evidence; no group: the one-shot controls), with the factory's identity members: "
        + "HyperBoost from GetRz090581HyperBoostCapabilities with reversible writes when hyperBoost is "
        + "a group and its pad-less gates' WriteAdmitted, the GPU overclock with gpuClockTuning, THX, "
        + "ObservedOnly local dimming, direct manual fans, ordinary thermal control on unclassified "
        + "external power with thermal (managed's writable composition captures the power source), and "
        + "the Fan Stop evidence with fanStop (the Fan Stop requests are native's Fan Stop backend; "
        + "SupportsFanStop is false while its guardian is unavailable). With lighting the record is "
        + "merged with ProductionLightingPort.CreateCapabilities(true) as ReadCapabilitiesAsync merges "
        + "it. Native restrictions: fan modes are intersected with Automatic, Fixed and Maximum "
        + "(managed's admittedFanModes), manual fans are ranged only from Balanced and Performance "
        + "(native's fan recipes plan from those baselines alone), lighting effects with Off, Static, "
        + "Spectrum, Breathing, Tidal, "
        + "Starlight, Wave, Wheel and Fire, special keys and the playback mute indicator are absent, "
        + "and keyboard brightness and refresh rates need profiles (stepKeyboardBrightness and "
        + "setLightingRefreshRate).";

    private static readonly string[] NativeCapabilityRecordGroups =
    [
        "thermal", "fans", "battery", "temporaryCharge", "profiles", "lighting", "hyperBoost",
        "gpuClockTuning", "fanStop",
    ];

    private static readonly JsonSerializerOptions NativeCapabilityFixtureOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

    [Fact]
    public async Task NativeCapabilityRecordVectors_MatchManagedProducers()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeCapabilityRecordVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeCapabilityRecordVectors)
            : Path.Combine(output, NativeCapabilityRecordVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "rows"], root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeCapabilityRecordComment;
        }

        Assert.Equal(NativeCapabilityRecordComment, (string)root["comment"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        foreach (JsonNode? entry in root["rows"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            string[] groups = [.. row["groups"]!.AsArray().Select(group => (string)group!)];
            Assert.All(groups, group => Assert.Contains(group, NativeCapabilityRecordGroups));
            DeviceCapabilities record = (bool)row["identified"]!
                ? IdentifiedNativeCapabilities(groups.ToHashSet())
                : DeviceCapabilities.Unavailable();
            Assert.True((bool)row["identified"]! || groups.Length == 0, name);
            JsonNode observed = (await EncodedStatusAsync(
                CreateContext(providerAvailable: true).Runtime.GetStatus() with
                {
                    Capabilities = record,
                    ConfirmedState = null,
                }))["capabilities"]!.DeepClone();
            if (!string.IsNullOrEmpty(output))
            {
                row["record"] = observed;
            }
            else if (!JsonNode.DeepEquals(row["record"], observed))
            {
                failures.Add($"{name}: {observed.ToJsonString()}");
            }
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString())
                .Append(",\n\"rows\":[\n")
                .AppendJoin(",\n", root["rows"]!.AsArray().Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    // The record managed's production composition would publish for an exact
    // RZ09-0581 host whose backends are this row's groups, with the native
    // restrictions the comment names.
    private static DeviceCapabilities IdentifiedNativeCapabilities(IReadOnlySet<string> groups)
    {
        bool Has(string group) => groups.Contains(group);
        var identity = new HostIdentityReadResult(
            new HostIdentityEvidence(
                "Razer",
                WindowsHostIdentitySource.ProductionIdentity.ProductName,
                Sku: null,
                BiosVersion: null),
            Failure: null,
            HostFirmwareCompatibility.ModelScoped);
        Assert.True(ProductionBatteryFactory.IsExactModelScopedRz090581Identity(identity));

        CapturedCommandFixture[] fixtures = JsonSerializer.Deserialize<CapturedCommandFixture[]>(
            ProductionFixtureSource.Read()!.Value.Span,
            NativeCapabilityFixtureOptions)!;
        string[] prefixes =
        [
            .. Has("temporaryCharge") ? new[] { "charge-to-100-" } : Array.Empty<string>(),
            .. Has("thermal") ? new[] { "performance-", "custom-performance-" } : Array.Empty<string>(),
            .. Has("fans") ? new[] { "fan-maximum-" } : Array.Empty<string>(),
            .. Has("battery") ? new[] { "battery-limit-", "battery-protection-off" } : Array.Empty<string>(),
        ];
        DeviceCommandCatalog catalog = DeviceCommandCatalog.Create(
            CapturedFixtureFile.TargetDevice,
            fixtures.Where(fixture => prefixes.Any(prefix => fixture.Action.StartsWith(prefix, StringComparison.Ordinal))));

        (DeviceFeatureAccess? hyperBoost, DeviceFeatureAccess? padless, HyperBoostBaseModeCapabilities? baseModes) =
            ProductionBatteryFactory.GetRz090581HyperBoostCapabilities(
                identity,
                reversibleWritesAdmitted: Has("hyperBoost"),
                padlessWritesAdmitted: Has("hyperBoost")
                    && HyperBoostWithoutCoolingPadEvidenceGates.Current.WriteAdmitted);
        (bool direct, bool batterySaverStop) = ProductionBatteryFactory.GetRz090581FanCapabilityAdmission(
            identity,
            catalog,
            trustedExactFixture: true);
        // The factory computes both flags inline (CreateWindows), so the Fan Stop
        // generator's predicates for the exact host stand in for them; a build
        // without native's Fan Stop backend publishes neither.
        (bool trial, bool source) = Has("fanStop")
            ? FanStopEvidence(WindowsHostIdentitySource.ProductionIdentity.ProductName)
            : (false, false);
        DeviceCapabilities port = ProductionBatteryPort.CreateCapabilities(
            catalog,
            ProductionBatteryFactory.GetRz090581ProductionBatteryReadEvidence(identity),
            admittedFanModes: new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.Maximum },
            gpuOverclockCapabilities: Has("gpuClockTuning")
                ? ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(identity)
                : null,
            hyperBoostAccess: hyperBoost,
            hyperBoostWithoutCoolingPadAccess: padless,
            hyperBoostBaseModes: baseModes,
            supportsDirectManualFanTransitions: direct,
            thxAudioCapabilities: ProductionBatteryFactory.GetRz090581ThxAudioCapabilities(identity),
            supportsBatterySaverFanStop: batterySaverStop,
            supportsOrdinaryThermalControlOnUnclassifiedExternalPower: Has("thermal")
                && ProductionBatteryFactory.IsExactModelScopedRz090581Identity(identity),
            // CreateWindows passes this record inline, with no accessor to read it from.
            localDimmingCapabilities: new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, SupportsClear: false));
        DeviceCapabilities battery = port with
        {
            HasFanStopTrialEvidence = trial,
            HasFanStopSourceEvidence = source,
            SupportsFanStop = false,
            // Native's fan recipes (openblade-domain fan_recipe::Plan::select) enter
            // manual fans only from Balanced or Performance, so the reviewed range
            // is published for those presets alone.
            FanSpeedRangesByPerformancePreset = port.FanSpeedRange is { } range
                ? new Dictionary<PerformancePreset, FanSpeedRange>
                {
                    [PerformancePreset.Balanced] = range,
                    [PerformancePreset.Performance] = range,
                }
                : new Dictionary<PerformancePreset, FanSpeedRange>(),
        };
        if (!Has("lighting"))
        {
            return battery;
        }

        DeviceCapabilities lighting = ProductionLightingPort.CreateCapabilities(admitted: true);
        bool controls = Has("profiles");
        // Native admission is Rust (lighting_mutation's effect mapping); this list
        // restates it, and capability_tests checks the refused effects stay out.
        LightingEffect[] nativeEffects =
        [
            LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum, LightingEffect.Breathing,
            LightingEffect.Tidal, LightingEffect.Starlight, LightingEffect.Wave, LightingEffect.Wheel,
            LightingEffect.Fire,
        ];
        return battery with
        {
            LightingEffects = lighting.LightingEffects.Intersect(nativeEffects).ToHashSet(),
            ObservedLightingEffects = lighting.ObservedLightingEffects,
            ObservedLightingZones = lighting.ObservedLightingZones,
            SpecialKeyBindingsAccess = null,
            WritableSpecialKeySlots = SpecialKeySlotSet.None,
            LampArrayLightingAccess = lighting.LampArrayLightingAccess,
            LogoLightingModes = lighting.LogoLightingModes,
            SupportsIndependentLightingControls = lighting.SupportsIndependentLightingControls,
            SupportsVaporChamberLighting = lighting.SupportsVaporChamberLighting,
            SupportsTouchpadStatusIndicator = lighting.SupportsTouchpadStatusIndicator,
            SupportsKeyboardBrightness = lighting.SupportsKeyboardBrightness && controls,
            SupportsBreathingColorSelection = lighting.SupportsBreathingColorSelection,
            SupportedBreathingColorModes = lighting.SupportedBreathingColorModes,
            SupportsUniformLightingFrames = lighting.SupportsUniformLightingFrames,
            SupportsWaveDirectionSelection = lighting.SupportsWaveDirectionSelection,
            SupportsPlaybackMuteIndicator = false,
            KeyboardLightingLayoutId = lighting.KeyboardLightingLayoutId,
            Blade18KeyboardLayoutId = lighting.Blade18KeyboardLayoutId,
            Blade14KeyboardEdition = lighting.Blade14KeyboardEdition,
            SupportedLightingRefreshRates = controls
                ? lighting.SupportedLightingRefreshRates
                : new HashSet<LightingRefreshRate>(),
        };
    }
}
