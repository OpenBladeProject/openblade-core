using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Session.Tests;

// The native Session Agent's Windows power-mode automation is pinned against
// managed. Each case drives the real WindowsPowerModeAutomationCoordinator, as
// ServicePowerModeAutomationLoop does, over a scripted Windows power mode: its
// statuses and reconnects in, and the Windows calls, retry delays and the last
// published result out. The Rust replay reads the same file.
public sealed class WindowsPowerModeAutomationNativeVectorTests
{
    private const string VectorFile = "power-mode-automation-vectors.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_POWER_MODE_VECTORS";

    private const string Comment =
        "Recorded by WindowsPowerModeAutomationNativeVectorTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_POWER_MODE_VECTORS set to this directory. A step is observe "
        + "(a status on source, with performance confirmed unless confirmed is false, and only that "
        + "preset's association set to mode; access, verified, retained and unconfirmed change the "
        + "status's confirmation members) or unavailable (the subscription reconnecting). Each Windows "
        + "call takes the next applies entry (default Succeeded); a failed call's retry delay runs its "
        + "during steps. After each step the coordinator is idle; expect records each step's result, "
        + "each during step's result, every Windows call as its supply and mode, each retry delay in milliseconds, "
        + "and the last published result.";

    private const string Cases = """
        [
        {"name":"first/plugged-balanced","steps":[{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"first/battery-saver","steps":[{"observe":{"source":1,"performance":3,"mode":0}}]},
        {"name":"first/usbc-custom","steps":[{"observe":{"source":2,"performance":5,"mode":2}}]},
        {"name":"first/creator","steps":[{"observe":{"source":0,"performance":9,"mode":2}}]},
        {"name":"leave-unchanged","steps":[{"observe":{"source":0,"performance":1,"mode":null}}]},
        {"name":"identical-status","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"same-supply/usbc-after-plugged","steps":[{"observe":{"source":0,"performance":1,"mode":2}},{"observe":{"source":2,"performance":1,"mode":2}},{"observe":{"source":1,"performance":1,"mode":2}}]},
        {"name":"performance-change","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":2,"mode":2}}]},
        {"name":"association-edit","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":1,"mode":2}}]},
        {"name":"association-cleared","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":1,"mode":null}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"unavailable/reapplies","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"unavailable":true},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"unconfirmed/inactive-session","steps":[{"observe":{"source":0,"performance":1,"mode":1,"access":1}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"unconfirmed/not-interactive","steps":[{"observe":{"source":0,"performance":1,"mode":1,"access":2}}]},
        {"name":"unconfirmed/unverified","steps":[{"observe":{"source":0,"performance":1,"mode":1,"verified":false}}]},
        {"name":"unconfirmed/retained","steps":[{"observe":{"source":0,"performance":1,"mode":1,"retained":true}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"unconfirmed/performance-write","steps":[{"observe":{"source":1,"performance":3,"mode":0,"unconfirmed":[0]}}]},
        {"name":"unconfirmed/other-writes","steps":[{"observe":{"source":1,"performance":3,"mode":0,"unconfirmed":[1,3]}}]},
        {"name":"unconfirmed/no-preset","steps":[{"observe":{"source":0,"performance":1,"mode":1,"confirmed":false}}]},
        {"name":"unconfirmed/external-unknown","steps":[{"observe":{"source":3,"performance":1,"mode":1}}]},
        {"name":"unconfirmed/clears-observed","steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":1,"mode":1,"verified":false}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/failed-then-succeeded","applies":[{"status":"Failed"},{"status":"Succeeded"}],"steps":[{"observe":{"source":0,"performance":2,"mode":2}}]},
        {"name":"retry/failed-twice","applies":[{"status":"Failed"},{"status":"Failed"}],"steps":[{"observe":{"source":1,"performance":4,"mode":1}}]},
        {"name":"retry/failed-twice-then-new-target","applies":[{"status":"Failed"},{"status":"Failed"}],"steps":[{"observe":{"source":1,"performance":4,"mode":1}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/superseded-in-delay","applies":[{"status":"Failed","during":[{"observe":{"source":1,"performance":3,"mode":0}}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/unavailable-in-delay","applies":[{"status":"Failed","during":[{"unavailable":true}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}},{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/unconfirmed-in-delay","applies":[{"status":"Failed","during":[{"observe":{"source":0,"performance":1,"mode":1,"retained":true}}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/identical-in-delay","applies":[{"status":"Failed","during":[{"observe":{"source":0,"performance":1,"mode":1}}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/same-supply-in-delay","applies":[{"status":"Failed","during":[{"observe":{"source":2,"performance":1,"mode":1}}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}}]},
        {"name":"retry/cleared-in-delay","applies":[{"status":"Failed","during":[{"observe":{"source":0,"performance":1,"mode":null}}]}],"steps":[{"observe":{"source":0,"performance":1,"mode":1}}]}
        ]
        """;

    [Fact]
    public async Task NativeVectors_MatchManagedCoordinator()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, VectorFile)
            : Path.Combine(output, VectorFile);
        var expected = new JsonObject
        {
            ["comment"] = Comment,
            ["cases"] = JsonNode.Parse(Cases),
        };
        JsonObject root = string.IsNullOrEmpty(output)
            ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
            : expected;

        // The file holds exactly the reviewed cases: a dropped or edited input
        // would otherwise weaken the Rust replay silently.
        Assert.True(JsonNode.DeepEquals(InputsOnly(root), InputsOnly(expected)));

        var failures = new List<string>();
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonObject observed = await ReplayAsync(vector);
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
                .AppendJoin(",\n", root["cases"]!.AsArray().Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        static JsonObject InputsOnly(JsonObject root)
        {
            var inputs = (JsonObject)root.DeepClone();
            inputs.Remove("comment");
            foreach (JsonNode? row in inputs["cases"]!.AsArray())
            {
                row!.AsObject().Remove("expect");
            }

            return inputs;
        }
    }

    private static async Task<JsonObject> ReplayAsync(JsonObject vector)
    {
        var applies = new Queue<JsonObject>(
            vector["applies"]?.AsArray().Select(row => row!.AsObject()) ?? []);
        var results = new JsonArray();
        var during = new JsonArray();
        var calls = new JsonArray();
        var delays = new JsonArray();
        JsonArray? pending = null;
        WindowsPowerModeAutomationCoordinator? coordinator = null;
        var controller = new VectorController((source, mode) =>
        {
            calls.Add(new JsonArray(source == PowerSource.Battery ? "DC" : "AC", (int)mode));
            JsonObject? script = applies.TryDequeue(out JsonObject? next) ? next : null;
            pending = script?["during"]?.AsArray();
            return (string?)script?["status"] is null or "Succeeded"
                ? new WindowsPowerModeApplyResult(true, true, mode, null)
                : new WindowsPowerModeApplyResult(false, false, null, "scripted");
        });
        await using (coordinator = new WindowsPowerModeAutomationCoordinator(
            controller,
            delay: (wait, token) =>
            {
                delays.Add((int)wait.TotalMilliseconds);
                JsonArray? steps = pending;
                pending = null;
                foreach (JsonNode? step in steps ?? [])
                {
                    during.Add(Step(step!.AsObject()));
                }

                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            }))
        {
            foreach (JsonNode? step in vector["steps"]!.AsArray())
            {
                results.Add(Step(step!.AsObject()));
                await coordinator.WhenIdleAsync();
            }

            WindowsPowerModeAutomationResult? last = coordinator.LastResult;
            return new JsonObject
            {
                ["results"] = results,
                ["during"] = during,
                ["calls"] = calls,
                ["delays"] = delays,
                ["last"] = last is null
                    ? null
                    : new JsonObject
                    {
                        ["generation"] = last.Generation,
                        ["source"] = (int)last.PowerSource,
                        ["mode"] = (int)last.RequestedMode,
                        ["succeeded"] = last.Result.Succeeded,
                    },
            };
        }

        JsonNode? Step(JsonObject step)
        {
            (string kind, JsonNode? value) = Assert.Single(step);
            switch (kind)
            {
                case "observe":
                    return coordinator!.Observe(VectorStatus(value!.AsObject())).ToString();
                case "unavailable":
                    coordinator!.MarkUnavailable();
                    return null;
                default:
                    throw new InvalidDataException($"Unknown vector step {kind}.");
            }
        }
    }

    private static ServiceStatusSnapshot VectorStatus(JsonObject observe)
    {
        var source = (PowerSource)(int)observe["source"]!;
        var performance = (PerformancePreset)(int)observe["performance"]!;
        WindowsPowerMode? mode = observe["mode"] is { } value ? (WindowsPowerMode)(int)value : null;
        PerformancePreset? confirmed = (bool?)observe["confirmed"] ?? true ? performance : (PerformancePreset?)null;
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            WindowsPowerModes = WindowsPowerModeAssociations.LeaveUnchanged.WithPerformance(performance, mode),
        };
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: (bool?)observe["verified"] ?? true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "test",
            Capabilities: null,
            ConfirmedState: new ConfirmedDeviceState(confirmed, FanMode.Automatic, Battery: null, Lighting: null),
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(performance, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: source,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: null,
            ServiceVersion: "test",
            ControlAccess: (SessionControlAccess)((int?)observe["access"] ?? 0))
        {
            ObservationReason = (bool?)observe["retained"] ?? false
                ? DeviceObservationReason.RetainedPowerSource
                : DeviceObservationReason.None,
            UnconfirmedWrites = observe["unconfirmed"]?.AsArray()
                .Select(subsystem => (DeviceSubsystem)(int)subsystem!)
                .ToArray() ?? [],
        };
    }

    private sealed class VectorController(
        Func<PowerSource, WindowsPowerMode, WindowsPowerModeApplyResult> apply)
        : IWindowsPowerModeController
    {
        public Task<WindowsPowerModeApplyResult> ApplyAsync(
            PowerSource powerSource,
            WindowsPowerMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(apply(powerSource, mode));
        }
    }
}
