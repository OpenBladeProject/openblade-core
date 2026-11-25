using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service owns HyperBoost on exact RZ09-0581 through setHyperBoost.
// These rows pin what managed SetHyperBoostAsync replies and writes from each
// confirmed state the exact getter decodes, on each power source, with the
// cooling pad absent, present or unobservable; the Rust tests replay each row
// on the native rig and name every deviation.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeHyperBoostOwnedVectors = "hyperboost-owned-vectors.json";
    private const string RegenerateNativeHyperBoostOwnedVectors =
        "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_OWNED_VECTORS";

    private const string NativeHyperBoostOwnedComment =
        "Recorded by ServiceControlRuntimeTests.NativeHyperBoostOwnedVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_OWNED_VECTORS set to this directory. Each row creates the "
        + "pad-less HyperBoost context on the row's source with RZ09-0581's HyperBoost bases (Silent, Balanced, "
        + "Performance and Custom, with direct entry from manual fans) and the default saved profiles, confirms "
        + "the row's state, observes once, then calls SetHyperBoostAsync(enabled) with the cooling pad in the "
        + "row's state. Where ProductionBatteryPort's entry precondition refuses the confirmed state (a battery "
        + "preset with Automatic fans), the device throws that precondition's known-not-applied refusal. Each "
        + "row records the reply and the HyperBoost and performance writes the runtime made.";

    // (name, performance, fans, HyperBoost active), as the exact getter decodes them.
    private static readonly (string Name, PerformancePreset? Performance, FanMode Fans, bool HyperBoost)[] NativeHyperBoostOwnedStates =
    [
        ("SilentAutomatic", PerformancePreset.Silent, FanMode.Automatic, false),
        ("BalancedAutomatic", PerformancePreset.Balanced, FanMode.Automatic, false),
        ("PerformanceAutomatic", PerformancePreset.Performance, FanMode.Automatic, false),
        ("CustomAutomatic", PerformancePreset.Custom, FanMode.Automatic, false),
        ("BatterySaverAutomatic", PerformancePreset.BatterySaver, FanMode.Automatic, false),
        ("BatteryBalancedAutomatic", PerformancePreset.BatteryBalanced, FanMode.Automatic, false),
        ("SilentManual", PerformancePreset.Silent, FanMode.Maximum, false),
        ("FixedMaximum", null, FanMode.Maximum, false),
        ("HyperBoostObserved", null, FanMode.Automatic, true),
    ];

    [Fact]
    public async Task NativeHyperBoostOwnedVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeHyperBoostOwnedVectors);
        var rows = new JsonArray();
        foreach (PowerSource source in new[] { PowerSource.PluggedIn, PowerSource.UsbC, PowerSource.Battery })
        {
            foreach (var state in NativeHyperBoostOwnedStates)
            {
                foreach (bool enabled in new[] { true, false })
                {
                    rows.Add(await NativeHyperBoostOwnedAsync(source, state, enabled, CoolingPadConnectionState.Absent));
                }
            }
        }
        foreach (CoolingPadConnectionState pad in new[] { CoolingPadConnectionState.PresentExact, CoolingPadConnectionState.ObservationFailed })
        {
            rows.Add(await NativeHyperBoostOwnedAsync(PowerSource.PluggedIn, NativeHyperBoostOwnedStates[1], true, pad));
        }

        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeHyperBoostOwnedVectors)
            : Path.Combine(output, NativeHyperBoostOwnedVectors);
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeHyperBoostOwnedComment).ToJsonString())
                .Append(",\n\"replies\":[\n")
                .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "replies"], recorded.Select(member => member.Key).ToArray());
        Assert.Equal(NativeHyperBoostOwnedComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["replies"], rows), rows.ToJsonString());
    }

    private async Task<JsonObject> NativeHyperBoostOwnedAsync(
        PowerSource source,
        (string Name, PerformancePreset? Performance, FanMode Fans, bool HyperBoost) state,
        bool enabled,
        CoolingPadConnectionState pad)
    {
        // Each row starts from empty stores so no row inherits another's state.
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        var padSource = new FixedCoolingPadDiscoverySource(CoolingPadConnectionState.Absent);
        TestContext context = CreateContext(true,
            initialPowerSource: source,
            hyperBoostPadDiscoverySource: padSource,
            hyperBoostOwnershipJournalStore: new RecordingHyperBoostOwnershipJournalStore());
        using ServiceControlRuntime runtime = context.Runtime;
        ConfigurePadlessHyperBoost(context);
        // RZ09-0581: every ordinary AC mode is a direct HyperBoost base, and
        // manual fans enter directly (ProductionBatteryFactory).
        context.Device.Capabilities = context.Device.Capabilities with
        {
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite)
            {
                Custom = DeviceFeatureAccess.ReadWrite,
            },
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = state.Performance,
            CustomPerformance = state.Performance == PerformancePreset.Custom ? CustomPerformanceSettings.Default : null,
            Fans = state.Fans,
            HyperBoostActive = state.HyperBoost,
        };
        await runtime.ObserveAsync(CancellationToken.None);
        padSource.State = pad;
        // ProductionBatteryPort.SetHyperBoostCoreAsync's entry precondition: the
        // confirmed base with Automatic fans, or manual (Maximum) fans.
        if (enabled && !state.HyperBoost && state.Fans == FanMode.Automatic
            && state.Performance is PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced)
        {
            context.Device.HyperBoostFailures.Enqueue(new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "HyperBoost requires confirmed Balanced performance with Automatic fans, or admitted direct entry from manual fan control."));
        }

        int hyperBoostBefore = context.Device.HyperBoostSetCalls;
        int performanceBefore = context.Device.PerformanceSetCalls;
        ControlOperationResult result = await runtime.SetHyperBoostAsync(enabled, CancellationToken.None);
        return new JsonObject
        {
            ["source"] = source.ToString(),
            ["state"] = state.Name,
            ["enabled"] = enabled,
            ["pad"] = pad.ToString(),
            ["status"] = (int)result.Status,
            ["message"] = result.Message,
            ["hyperBoostWrites"] = context.Device.HyperBoostSetCalls - hyperBoostBefore,
            ["performanceWrites"] = context.Device.PerformanceSetCalls - performanceBefore,
        };
    }
}
