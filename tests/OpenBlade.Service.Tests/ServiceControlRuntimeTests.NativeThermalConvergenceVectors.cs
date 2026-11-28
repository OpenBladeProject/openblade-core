using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service publishes ThermalConvergenceStatus and serves the thermal
// policy requests from its own derivation. These rows pin what managed publishes
// and replies for the same inputs: each status row drives the managed runtime from
// the row's saved policy, saved intent and confirmed state through one observation,
// and records the thermalConvergence member as the v91 status codec writes it. Each
// reply row runs one policy request from the row's saved preferences and records
// the result and the stored preferences. The Rust tests replay both.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeThermalConvergenceVectors = "thermal-convergence-vectors.json";
    private const string RegenerateNativeThermalConvergenceVectors =
        "OPENBLADE_REGENERATE_NATIVE_THERMAL_CONVERGENCE_VECTORS";

    private const string NativeThermalConvergenceComment =
        "Recorded by ServiceControlRuntimeTests.NativeThermalConvergenceVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_THERMAL_CONVERGENCE_VECTORS set to this directory. "
        + "A status row saves policy, disclosure and the profile at its power class (performance, Custom "
        + "levels as [cpu, gpu], fans as FanMode with a 3700 RPM default curve when manual), starts the device "
        + "at that intent (with Automatic fans when owned, so the startup fan write establishes ownership), "
        + "runs ReconcileStartupProfileAsync and one baseline ObserveAsync, then confirms the row's state, "
        + "optionally reports an unclassified adapter (unsettled) or names a competing controller (RazerAppEngine), observes again, prepares low power after "
        + "that observation, or appends a space to profiles.json before activation (quarantined), and records "
        + "the thermalConvergence member. A reply row saves its preferences, runs one "
        + "SetThermalControlPolicyAsync or AcknowledgeThermalPolicyUpgradeDisclosureAsync (sessionChanged makes "
        + "stillControlsSession false) and records the result and the stored policy, disclosure and whether "
        + "any configuration document changed.";

    private static readonly string[] NativeThermalConvergenceSections = ["statuses", "replies"];

    [Fact]
    public async Task NativeThermalConvergenceVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(
            RegenerateNativeThermalConvergenceVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeThermalConvergenceVectors)
            : Path.Combine(output, NativeThermalConvergenceVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeThermalConvergenceSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeThermalConvergenceComment;
        }

        Assert.Equal(NativeThermalConvergenceComment, (string)root["comment"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        foreach (JsonNode? entry in root["statuses"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            Record(row, "status", await NativeConvergenceStatusAsync(name, row));
        }

        foreach (JsonNode? entry in root["replies"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            (JsonNode result, JsonNode stored) = await NativeConvergenceReplyAsync(name, row);
            Record(row, "result", result);
            Record(row, "stored", stored);
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeThermalConvergenceSections)
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Record(JsonObject row, string member, JsonNode? observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                row[member] = observed;
            }
            else if (!JsonNode.DeepEquals(row[member], observed))
            {
                failures.Add($"{row["name"]} {member}: {observed?.ToJsonString() ?? "null"}");
            }
        }
    }

    private async Task<JsonNode?> NativeConvergenceStatusAsync(string name, JsonObject row)
    {
        var power = (PowerSource)(int)row["power"]!;
        JsonObject desired = row["desired"]!.AsObject();
        JsonObject confirmed = row["confirmed"]!.AsObject();
        bool owned = (bool?)row["owned"] == true;
        var desiredFans = (FanMode)(int)desired["fans"]!;
        ThermalConvergenceTestContext thermal = await CreateNativeConvergenceContextAsync(
            "native-convergence-" + name,
            row,
            power,
            configuration => new ConfirmedDeviceState(
                (PerformancePreset)(int)desired["performance"]!,
                owned ? FanMode.Automatic : desiredFans,
                ConfirmedBattery(80),
                ActiveProfile(configuration, power).Lighting)
            {
                HyperBoostActive = false,
                CustomPerformance = CustomLevels(desired["custom"]),
            },
            desired);
        TestContext context = thermal.Context;

        // Startup applies saved intent and the next sample is the baseline, so the
        // row's confirmed state is a change observed after both. An owned row starts
        // from Automatic fans so that startup's fan write establishes ownership.
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (owned)
        {
            Assert.True(context.Device.FanSetCalls > 0, name);
            Assert.Equal(desiredFans, context.Device.ConfirmedState.Fans);
        }

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = (PerformancePreset?)(int?)confirmed["performance"],
            Fans = (FanMode?)(int?)confirmed["fans"],
            HyperBoostActive = (bool?)confirmed["hyperBoost"],
            CustomPerformance = CustomLevels(confirmed["custom"]),
        };
        if ((bool?)row["unsettled"] == true)
        {
            context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
            context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        }

        if ((bool?)row["controller"] == true)
        {
            context.Monitor.Current = new ControllerPresenceObservation(
                ["RazerAppEngine"],
                null);
        }

        await context.Runtime.ObserveAsync(CancellationToken.None);
        if ((bool?)row["lowPower"] == true)
        {
            await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        return await EncodedThermalConvergenceAsync(status);
    }

    private async Task<(JsonNode Result, JsonNode Stored)> NativeConvergenceReplyAsync(
        string name,
        JsonObject row)
    {
        string root = "native-convergence-" + name;
        ThermalConvergenceTestContext thermal = await CreateNativeConvergenceContextAsync(
            root,
            row,
            PowerSource.PluggedIn,
            confirmedStateFactory: null,
            desired: null);
        string userRoot = Path.Combine(directory, root, "users", thermal.UserSid.Value);
        string[] documents =
        [
            Path.Combine(userRoot, "profiles.json"),
            Path.Combine(userRoot, "battery-preferences.json"),
            Path.Combine(userRoot, "battery-configuration.commit.json"),
        ];
        byte[][] before = [.. documents.Select(File.ReadAllBytes)];
        bool sessionChanged = (bool?)row["sessionChanged"] == true;
        Func<bool> stillControlsSession = () => !sessionChanged;
        JsonObject request = row["request"]!.AsObject();
        ControlOperationResult result = request["setPolicy"] is { } policy
            ? await thermal.Context.Runtime.SetThermalControlPolicyAsync(
                (ThermalControlPolicy)(int)policy,
                stillControlsSession,
                CancellationToken.None)
            : await thermal.Context.Runtime.AcknowledgeThermalPolicyUpgradeDisclosureAsync(
                stillControlsSession,
                CancellationToken.None);
        bool written = !documents.Select(File.ReadAllBytes)
            .Zip(before, (after, prior) => after.AsSpan().SequenceEqual(prior))
            .All(same => same);
        JsonNode preferences = JsonNode.Parse(File.ReadAllText(documents[1]))!["preferences"]!;
        return (
            new JsonObject
            {
                ["status"] = (int)result.Status,
                ["message"] = result.Message,
            },
            new JsonObject
            {
                ["policy"] = (int)preferences["thermalControlPolicy"]!,
                ["disclosure"] = (int)preferences["thermalPolicyUpgradeDisclosure"]!,
                ["written"] = written,
            });
    }

    private async Task<ThermalConvergenceTestContext> CreateNativeConvergenceContextAsync(
        string root,
        JsonObject row,
        PowerSource power,
        Func<BatteryConfiguration, ConfirmedDeviceState>? confirmedStateFactory,
        JsonObject? desired)
    {
        bool quarantined = (bool?)row["quarantined"] == true;
        return await CreateThermalConvergenceContextAsync(
            root,
            configuration =>
            {
                BatteryConfiguration configured = configuration with
                {
                    Preferences = configuration.Preferences with
                    {
                        ThermalControlPolicy = (ThermalControlPolicy)(int)row["policy"]!,
                        ThermalPolicyUpgradeDisclosure =
                            (ThermalPolicyUpgradeDisclosureState)(int)row["disclosure"]!,
                    },
                };
                if (desired is null)
                {
                    return configured;
                }

                PowerProfile profile = ActiveProfile(configured, power);
                var fans = (FanMode)(int)desired["fans"]!;
                profile = profile with
                {
                    Performance = (PerformancePreset)(int)desired["performance"]!,
                    CustomPerformance = CustomLevels(desired["custom"]) ?? profile.CustomPerformance,
                    Fans = fans,
                    FanControl = fans == FanMode.Automatic
                        ? profile.FanControl
                        : new FanControlSettings(fans, 3700, ManualFanCurve.Default),
                };
                return configured with
                {
                    Profiles = power switch
                    {
                        PowerSource.Battery => configured.Profiles with { Battery = profile },
                        PowerSource.UsbC => configured.Profiles with { UsbC = profile },
                        _ => configured.Profiles with { PluggedIn = profile },
                    },
                };
            },
            confirmedStateFactory,
            initialPowerSource: power,
            beforeActivation: quarantined
                ? async (_, sid) => await File.AppendAllTextAsync(
                    Path.Combine(directory, root, "users", sid.Value, "profiles.json"),
                    " ")
                : null,
            requireAuthoritativeSeed: false);
    }

    private static PowerProfile ActiveProfile(BatteryConfiguration configuration, PowerSource power) =>
        power switch
        {
            PowerSource.Battery => configuration.Profiles.Battery,
            PowerSource.UsbC => configuration.Profiles.UsbC,
            _ => configuration.Profiles.PluggedIn,
        };

    private static CustomPerformanceSettings? CustomLevels(JsonNode? levels) =>
        levels is JsonArray pair
            ? new CustomPerformanceSettings(
                (CustomPerformanceLevel)(int)pair[0]!,
                (CustomPerformanceLevel)(int)pair[1]!)
            : null;

    // Only the thermalConvergence member is recorded. The test device's confirmed
    // state carries no capability record, which the codec requires beside an active
    // HyperBoost, so the frame drops the confirmed state the row already names.
    private static async Task<JsonNode?> EncodedThermalConvergenceAsync(ServiceStatusSnapshot status)
    {
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            stream,
            new ServiceStatusEvent(
                OpenBladeIpc.ProtocolVersion,
                status with { ConfirmedState = null }),
            CancellationToken.None);
        byte[] framed = stream.ToArray();
        stream.Position = 0;
        Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        return JsonNode.Parse(framed.AsSpan(4))!["status"]!["thermalConvergence"]?.DeepClone();
    }
}
