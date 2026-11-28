using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service leaves HyperBoost for a battery preset with the managed
// port's restoration recipe: the Balanced pair, a read that must confirm
// Balanced with Automatic fans, then the battery pair and a final read. These
// rows pin what managed replies and leaves pending when that port write
// succeeds or fails at each stage; the Rust tests replay each row on the
// native rig.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeHyperBoostBatteryExitVectors = "hyperboost-battery-exit-vectors.json";
    private const string RegenerateNativeHyperBoostBatteryExitVectors =
        "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_BATTERY_EXIT_VECTORS";

    private const string NativeHyperBoostBatteryExitComment =
        "Recorded by ServiceControlRuntimeTests.NativeHyperBoostBatteryExitVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_BATTERY_EXIT_VECTORS set to this directory. Each row creates "
        + "the battery transition context on the row's source with the laptop confirmed in HyperBoost and "
        + "Automatic fans, observes once, then sets a persistent Performance override to the row's battery "
        + "preset. The port's SetPerformanceAsync either succeeds (success: the laptop then reads the preset "
        + "with HyperBoost inactive) or throws the indeterminate failure ProductionBatteryPort raises at that "
        + "stage of the restoration recipe: restoreSetter (either restoration setter), intermediateMismatch "
        + "(the Balanced and Automatic read failed to confirm), batterySetter (either battery setter) and "
        + "finalMismatch (the final read). A failure row reads back either the prior HyperBoost state (readback "
        + "prior) or Balanced with Automatic fans and HyperBoost inactive (readback balanced, the state the restoration "
        + "pair leaves). Each row records the reply, whether writes are then blocked, the "
        + "unconfirmed subsystems, whether a HyperBoost restoration is pending and how many performance "
        + "writes the runtime made.";

    private static readonly (string Name, string? Message)[] NativeHyperBoostBatteryExitCases =
    [
        ("success", null),
        ("restoreSetter", "The HyperBoost restoration outcome is indeterminate."),
        ("intermediateMismatch", "HyperBoost restoration did not confirm Balanced performance and Automatic fans."),
        ("batterySetter", "The multi-command thermal write outcome is indeterminate."),
        ("finalMismatch", "The acknowledged thermal commands did not match the final state readback."),
    ];

    [Fact]
    public async Task NativeHyperBoostBatteryExitVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeHyperBoostBatteryExitVectors);
        var rows = new JsonArray();
        foreach (PowerSource source in new[] { PowerSource.Battery, PowerSource.PluggedIn, PowerSource.UsbC })
        {
            foreach (PerformancePreset preset in new[] { PerformancePreset.BatterySaver, PerformancePreset.BatteryBalanced })
            {
                foreach ((string name, string? message) in NativeHyperBoostBatteryExitCases)
                {
                    foreach (string? readback in message is null ? new string?[] { null } : new string?[] { "prior", "balanced" })
                    {
                        rows.Add(await NativeHyperBoostBatteryExitAsync(source, preset, name, message, readback));
                    }
                }
            }
        }

        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeHyperBoostBatteryExitVectors)
            : Path.Combine(output, NativeHyperBoostBatteryExitVectors);
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeHyperBoostBatteryExitComment).ToJsonString())
                .Append(",\n\"replies\":[\n")
                .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "replies"], recorded.Select(member => member.Key).ToArray());
        Assert.Equal(NativeHyperBoostBatteryExitComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["replies"], rows), rows.ToJsonString());
    }

    private async Task<JsonObject> NativeHyperBoostBatteryExitAsync(
        PowerSource source,
        PerformancePreset preset,
        string name,
        string? message,
        string? readback)
    {
        // Each row starts from empty stores so no row inherits another's state.
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        TestContext context = await CreateBatteryBalancedTransitionContextAsync(
            source,
            PerformancePreset.Balanced,
            batteryPerformance: preset);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = null,
            Fans = FanMode.Automatic,
            HyperBoostActive = true,
        };
        await runtime.ObserveAsync(CancellationToken.None);
        if (message is null)
        {
            context.Device.AfterSetter = subsystem =>
            {
                if (subsystem == DeviceSubsystem.Performance)
                {
                    context.Device.ConfirmedState = context.Device.ConfirmedState with
                    {
                        HyperBoostActive = false,
                    };
                }
            };
        }
        else
        {
            context.Device.PerformanceFailures.Enqueue(
                new BladeWriteException(WriteSubmissionState.Indeterminate, message));
            if (readback == "balanced")
            {
                context.Device.BeforeQueuedPerformanceFailure = () =>
                    context.Device.ConfirmedState = context.Device.ConfirmedState with
                    {
                        Performance = PerformancePreset.Balanced,
                        HyperBoostActive = false,
                    };
            }
        }

        int writesBefore = context.Device.PerformanceSetCalls;
        ControlOperationResult result = await runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(preset, OverrideLifetime.Persistent),
            CancellationToken.None);
        ServiceStatusSnapshot status = runtime.GetStatus();
        return new JsonObject
        {
            ["source"] = source.ToString(),
            ["preset"] = (int)preset,
            ["case"] = name,
            ["readback"] = readback,
            ["status"] = (int)result.Status,
            ["message"] = result.Message,
            ["writesBlocked"] = status.WritesBlocked,
            ["unconfirmed"] = new JsonArray([.. status.UnconfirmedWrites.Order().Select(subsystem => (JsonNode)subsystem.ToString())]),
            ["hyperBoostRestorePending"] = status.HyperBoostRestorePending,
            ["performanceWrites"] = context.Device.PerformanceSetCalls - writesBefore,
        };
    }
}
