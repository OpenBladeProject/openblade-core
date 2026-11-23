using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Display;

namespace OpenBlade.Session.Tests;

// The native Session Agent's display automation is pinned against managed.
// Each case drives the real DisplayAutomationCoordinator, as the managed
// display automation loop does, over a scripted display: its status triggers,
// manual operations and explicit reads in, and the profile writes, retry
// delays, baseline queries and durable checkpoints out. The Rust replay reads
// the same file and names every case where native deliberately differs.
public sealed partial class DisplayAutomationCoordinatorTests
{
    private const string NativeAutomationVectors = "display-automation-vectors.json";
    private const string RegenerateNativeAutomationVectors =
        "OPENBLADE_REGENERATE_NATIVE_DISPLAY_AUTOMATION_VECTORS";
    private static readonly Guid NativeAutomationGame = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private const string NativeAutomationComment =
        "Recorded by DisplayAutomationCoordinatorTests.NativeAutomationVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_DISPLAY_AUTOMATION_VECTORS set to this directory. "
        + "A step is one trigger: observe (a confirmed status for source, its power display base and, "
        + "when active, the game override), unavailable (an unconfirmed status; true with manual "
        + "external power), begin and complete (a manual operation), read (an explicit query) or panel "
        + "(the scripted display changes). Each profile write takes the next applies entry (default "
        + "Succeeded) and runs its during steps while it holds the display. After each step the "
        + "coordinator is idle; expect records each step's result and latest checkpoint, then every "
        + "write, baseline query and retry delay, and the last transition result.";

    private const string NativeAutomationCases = """
        [
        {"name":"first-status/power-profile-only","steps":[{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"first-status/application","steps":[{"observe":{"source":0,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60"}}}]},
        {"name":"same-source/profile-edit","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"refresh":"fixed-60","hdr":true,"brightness":80}}}]},
        {"name":"same-source/identical-status","steps":[{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}}]},
        {"name":"source-change/power-profile","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":0,"base":{"refresh":"fixed-120","brightness":80}}}]},
        {"name":"source-change/unconfigured","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":2}}]},
        {"name":"source-change/clears-application-baseline","steps":[{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60"}}},{"observe":{"source":0,"base":{"refresh":"fixed-120"}}}]},
        {"name":"application/entry-and-exit","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60","hdr":true}}},{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"application/switch-keeps-baseline","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60"}}},{"panel":{"refresh":"fixed-60"}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"color":"native"}}},{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"application/without-override","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true}}]},
        {"name":"application/unknown-refresh-baseline","steps":[{"panel":{"refresh":null}},{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60","hdr":true}}}]},
        {"name":"application/unavailable-baseline","steps":[{"panel":{"available":false}},{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}},{"panel":{"available":true}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}}]},
        {"name":"application/hdr-unsupported-baseline","steps":[{"panel":{"hdrSupported":false}},{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}},{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"retry/failed-twice","applies":[{"status":"Failed"},{"status":"Failed"}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"retry/blocked-then-succeeded","applies":[{"status":"Blocked"},{"status":"Succeeded"}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"stop/indeterminate-pauses-until-read","applies":[{"status":"Indeterminate"}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60","brightness":80}}},{"observe":{"source":2,"base":{"refresh":"fixed-240"}}},{"read":true}]},
        {"name":"stop/read-without-pause","steps":[{"observe":{"source":1}},{"read":true}]},
        {"name":"stop/incomplete-read-keeps-pause","applies":[{"status":"Indeterminate"}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"brightness":80}}},{"panel":{"brightness":null}},{"read":true},{"panel":{"brightness":50}},{"read":true}]},
        {"name":"mid-apply/source-change-resumes-latest","applies":[{"status":"Succeeded","during":[{"observe":{"source":2,"base":{"refresh":"fixed-240"}}}]}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"mid-apply/application-change-resumes-latest","applies":[{"status":"Succeeded","during":[{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"color":"native"}}}]}],"steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60"}}}]},
        {"name":"mid-apply/indeterminate-keeps-hold","applies":[{"status":"Indeterminate","during":[{"observe":{"source":2,"base":{"refresh":"fixed-240"}}}]}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"mid-apply/canceled-retry-delay","applies":[{"status":"Failed","during":[{"observe":{"source":2,"base":{"refresh":"fixed-240"}}}]}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}},{"read":true}]},
        {"name":"mid-apply/lock-resumes-after-unlock","applies":[{"status":"Succeeded","during":[{"unavailable":false}]}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"lock/idle-unlock-same-source","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"unavailable":false},{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"lock/source-changed-while-locked","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"unavailable":false},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"lock/manual-external-power","steps":[{"unavailable":true},{"begin":{"brightness":70}},{"complete":"Succeeded"},{"observe":{"source":1,"base":{"brightness":40}}}]},
        {"name":"lock/manual-external-power-application","steps":[{"unavailable":true},{"begin":{"brightness":70}},{"panel":{"brightness":70}},{"complete":"Succeeded"},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}}]},
        {"name":"manual/confirmed-keeps-hold-until-source-change","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"begin":{"brightness":70}},{"panel":{"brightness":70}},{"complete":"Succeeded"},{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"hdr":true}}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"manual/indeterminate-pauses-until-read","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"begin":{"refresh":"fixed-60"}},{"complete":"Indeterminate"},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}},{"read":true}]},
        {"name":"manual/failed-restores-unpaused","steps":[{"observe":{"source":1,"base":{"brightness":40}}},{"begin":{"hdr":true}},{"complete":"Failed"},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}}]},
        {"name":"manual/failed-keeps-inherited-pause","applies":[{"status":"Indeterminate"}],"steps":[{"observe":{"source":1}},{"observe":{"source":0,"base":{"refresh":"fixed-60"}}},{"begin":{"brightness":70}},{"complete":"Failed"},{"read":true}]},
        {"name":"manual/clears-application-baseline","steps":[{"observe":{"source":1,"base":{"brightness":40},"active":true,"override":{"refresh":"fixed-60"}}},{"begin":{"brightness":70}},{"complete":"Succeeded"},{"observe":{"source":1,"base":{"brightness":40}}}]}
        ]
        """;

    [Fact]
    public async Task NativeAutomationVectors_MatchManagedCoordinator()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeAutomationVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeAutomationVectors)
            : Path.Combine(output, NativeAutomationVectors);
        var expected = new JsonObject
        {
            ["comment"] = NativeAutomationComment,
            ["cases"] = JsonNode.Parse(NativeAutomationCases),
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
            JsonObject observed = await ReplayNativeAutomationCaseAsync(vector);
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

    private static async Task<JsonObject> ReplayNativeAutomationCaseAsync(JsonObject vector)
    {
        var applier = new VectorApplier(vector["applies"]?.AsArray());
        DisplayAutomationRecoveryState? checkpoint = null;
        int delays = 0;
        await using var coordinator = new DisplayAutomationCoordinator(
            new DisplayPowerContextState(),
            applier,
            retryDelay: (_, token) =>
            {
                Interlocked.Increment(ref delays);
                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            },
            recordRecoveryState: state =>
            {
                Volatile.Write(ref checkpoint, state);
                return true;
            });
        applier.Trigger = step => Step(step);

        var results = new JsonArray();
        var checkpoints = new JsonArray();
        foreach (JsonNode? step in vector["steps"]!.AsArray())
        {
            results.Add(Step(step!.AsObject()));
            await coordinator.WhenIdleAsync();
            checkpoints.Add(NativeCheckpoint(Volatile.Read(ref checkpoint)));
        }

        DisplayTransitionResult? last = coordinator.LastTransitionResult;
        return new JsonObject
        {
            ["results"] = results,
            ["checkpoints"] = checkpoints,
            ["applied"] = new JsonArray(applier.Applied.ToArray()),
            ["queries"] = applier.Queries,
            ["delays"] = delays,
            ["last"] = last is null
                ? null
                : new JsonObject
                {
                    ["source"] = (int)last.PowerSource,
                    ["status"] = last.Result.Status.ToString(),
                },
        };

        JsonNode? Step(JsonObject step)
        {
            (string kind, JsonNode? value) = Assert.Single(step);
            switch (kind)
            {
                case "observe":
                    return coordinator.Observe(NativeAutomationStatus(value!.AsObject())).ToString();
                case "unavailable":
                    bool manual = (bool)value!;
                    return coordinator.Observe(Status(PowerSource.ExternalUnknown, Profiles("unavailable")) with
                    {
                        ExternalPowerPresent = manual,
                    }).ToString();
                case "begin":
                    return coordinator.BeginManualOperation(NativeSettings(value));
                case "complete":
                    coordinator.CompleteManualOperation(
                        Enum.Parse<SessionDisplayOperationStatus>((string)value!),
                        applier.Snapshot());
                    return null;
                case "read":
                    return coordinator.ResumeAfterSuccessfulExplicitRead(applier.Snapshot());
                case "panel":
                    applier.Change(value!.AsObject());
                    return null;
                default:
                    throw new InvalidDataException($"Unknown step {kind}.");
            }
        }
    }

    private static ServiceStatusSnapshot NativeAutomationStatus(JsonObject observe)
    {
        var source = (PowerSource)(int)observe["source"]!;
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        DisplayProfileSettings? display = observe["base"] is null ? null : NativeSettings(observe["base"]);
        bool active = (bool?)observe["active"] == true;
        var overrides = new ApplicationProfileOverrideSet();
        if (observe["override"] is { } application)
        {
            var entry = new ApplicationPowerProfileOverride { Display = NativeSettings(application) };
            overrides = source switch
            {
                PowerSource.PluggedIn => overrides with { PluggedIn = entry },
                PowerSource.Battery => overrides with { Battery = entry },
                _ => overrides with { UsbC = entry },
            };
        }

        ProfileSet profiles = source switch
        {
            PowerSource.PluggedIn => defaults with { PluggedIn = defaults.PluggedIn with { Display = display } },
            PowerSource.Battery => defaults with { Battery = defaults.Battery with { Display = display } },
            _ => defaults with { UsbC = defaults.UsbC with { Display = display } },
        };
        profiles = profiles with
        {
            ApplicationProfiles =
            [
                new ApplicationProfileDefinition(
                    NativeAutomationGame,
                    "Game",
                    @"C:\Games\game.exe",
                    ApplicationActivationMode.Foreground,
                    overrides),
            ],
        };
        return Status(source, profiles) with
        {
            ActiveApplicationProfileId = active ? NativeAutomationGame : null,
        };
    }

    private static DisplayProfileSettings NativeSettings(JsonNode? settings) =>
        new((string?)settings?["refresh"], (bool?)settings?["hdr"], (string?)settings?["color"])
        {
            BrightnessPercent = NativeByte(settings?["brightness"]),
        };

    private static byte? NativeByte(JsonNode? value) => value is null ? null : checked((byte)(int)value);

    private static JsonObject NativeSettingsJson(DisplayProfileSettings settings)
    {
        var json = new JsonObject();
        if (settings.RefreshRateKey is { } refresh)
        {
            json["refresh"] = refresh;
        }
        if (settings.HdrEnabled is { } hdr)
        {
            json["hdr"] = hdr;
        }
        if (settings.ColorProfileKey is { } color)
        {
            json["color"] = color;
        }
        if (settings.BrightnessPercent is { } brightness)
        {
            json["brightness"] = brightness;
        }
        return json;
    }

    // The durable members as the checkpoint store writes them.
    private static JsonObject? NativeCheckpoint(DisplayAutomationRecoveryState? state) => state is null
        ? null
        : new JsonObject
        {
            ["LastReconciledPowerSource"] = (int)state.LastReconciledPowerSource,
            ["ApplicationProfileId"] = state.ApplicationProfileId?.ToString(),
            ["Baseline"] = state.Baseline is not { } baseline
                ? null
                : new JsonObject
                {
                    ["RefreshRateKey"] = baseline.RefreshRateKey,
                    ["HdrEnabled"] = baseline.HdrEnabled,
                    ["ColorProfileKey"] = baseline.ColorProfileKey,
                    ["BrightnessPercent"] = baseline.BrightnessPercent,
                },
            ["OwnedRefreshRate"] = state.OwnedRefreshRate,
            ["OwnedHdr"] = state.OwnedHdr,
            ["OwnedColorProfile"] = state.OwnedColorProfile,
            ["OwnedBrightness"] = state.OwnedBrightness,
            ["ManualHold"] = state.ManualHold,
            ["AutomaticWritesPaused"] = state.AutomaticWritesPaused,
            ["UncertainRefreshRate"] = state.UncertainRefreshRate,
            ["UncertainHdr"] = state.UncertainHdr,
            ["UncertainColorProfile"] = state.UncertainColorProfile,
            ["UncertainBrightness"] = state.UncertainBrightness,
        };

    // A scripted display. Every call yields first, so a transition runs off
    // the coordinator's lock as it does over the real applier.
    private sealed class VectorApplier : IDisplayProfileApplier
    {
        private readonly Queue<JsonObject> applies;
        private readonly JsonObject panel = new()
        {
            ["available"] = true,
            ["refresh"] = "fixed-240",
            ["hdrSupported"] = true,
            ["hdr"] = false,
            ["color"] = "srgb",
            ["brightness"] = 50,
        };

        internal VectorApplier(JsonArray? applies)
        {
            this.applies = new((applies ?? []).Select(entry => entry!.AsObject()));
        }

        internal Func<JsonObject, JsonNode?> Trigger { get; set; } = _ => null;
        internal List<JsonNode> Applied { get; } = [];
        internal int Queries { get; private set; }

        internal void Change(JsonObject change)
        {
            foreach ((string member, JsonNode? value) in change)
            {
                Assert.True(panel.ContainsKey(member), member);
                panel[member] = value?.DeepClone();
            }
        }

        internal DisplaySettingsSnapshot Snapshot() => (bool)panel["available"]!
            ? new DisplaySettingsSnapshot(
                IsAvailable: true,
                DisplayName: "Synthetic panel",
                Width: 2560,
                Height: 1600,
                CurrentRefreshKey: (string?)panel["refresh"],
                RefreshOptions: [],
                HdrSupported: (bool)panel["hdrSupported"]!,
                HdrEnabled: (bool)panel["hdr"]!,
                ColorProfilesEnabled: true,
                ActiveColorProfileId: (string?)panel["color"],
                ColorProfiles: [],
                Message: null)
            {
                BrightnessPercent = NativeByte(panel["brightness"]),
                SupportedBrightnessLevels = [0, 20, 40, 50, 60, 80, 100],
            }
            : new DisplaySettingsSnapshot(false, null, 0, 0, null, [], false, false, false, null, [],
                "The scripted panel is unavailable.");

        public async Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Queries++;
            return new(SessionDisplayOperationStatus.Succeeded, null, Snapshot());
        }

        public async Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            Applied.Add(new JsonObject
            {
                ["source"] = (int)expectedPowerSource,
                ["settings"] = NativeSettingsJson(settings),
            });
            JsonObject script = applies.TryDequeue(out JsonObject? next)
                ? next
                : new JsonObject { ["status"] = "Succeeded" };
            foreach (JsonNode? step in script["during"]?.AsArray() ?? [])
            {
                Trigger(step!.AsObject());
            }
            var status = Enum.Parse<SessionDisplayOperationStatus>((string)script["status"]!);
            return new(status, status == SessionDisplayOperationStatus.Succeeded ? null : "The scripted profile did not apply.",
                Snapshot());
        }

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetBrightnessAsync(
            PowerSource expectedPowerSource,
            byte percent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
