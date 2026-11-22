using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native request triggers' low-power replies are pinned against managed:
// a report sequence, a save, the startup transition and explicit requests
// while the LowPowerGate is set, and the per-write low-power refusal. Managed's
// gate is a latch with no unknown state. The Rust readiness tests read the
// same file.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeLowPowerTriggerVectors = "low-power-trigger-vectors.json";

    private const string NativeLowPowerTriggerComment =
        "Recorded from managed by ServiceControlRuntimeTests.NativeLowPowerTriggerVectors; regenerate with OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS. "
        + "A lowPower row runs with the LowPowerGate entered. A report row whose expect has transition true ran ApplyFullTransitionAsync with the gate clear. "
        + "setters counts the device setters the request submitted. startup is ApplyFullTransitionAsync itself, as the user's profile load runs it; "
        + "writeBlock is ScopedThermalWriteBlockReason, which every thermal write of a transition checks. "
        + "An explicit row is a persistent override at power, the pluggedIn rows first, then the battery rows after a source change with the gate clear.";

    [Fact]
    public async Task NativeLowPowerTriggerVectors_MatchManagedReplies()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeLowPowerTriggerVectors)
            : Path.Combine(output, NativeLowPowerTriggerVectors);
        JsonObject observed = await ObserveNativeLowPowerTriggersAsync();
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            static string Rows(JsonNode? rows) =>
                string.Join(",\n", rows!.AsArray().Select(row => row!.ToJsonString()));
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeLowPowerTriggerComment).ToJsonString())
                .Append(",\n\"report\":[\n").Append(Rows(observed["report"]))
                .Append("\n],\n\"save\":").Append(observed["save"]!.ToJsonString())
                .Append(",\n\"startup\":").Append(observed["startup"]!.ToJsonString())
                .Append(",\n\"writeBlock\":").Append(observed["writeBlock"]!.ToJsonString())
                .Append(",\n\"explicit\":[\n").Append(Rows(observed["explicit"]))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        Assert.Equal(NativeLowPowerTriggerComment, recorded["comment"]!.GetValue<string>());
        foreach (string member in new[] { "report", "save", "startup", "writeBlock", "explicit" })
        {
            Assert.True(JsonNode.DeepEquals(recorded[member], observed[member]), observed[member]!.ToJsonString());
        }
    }

    // One runtime through low-power reports, the startup transition, the
    // write refusal, a report and a renewal after the gate clears, a save in
    // low power, and explicit requests in low power, in that order.
    private async Task<JsonObject> ObserveNativeLowPowerTriggersAsync()
    {
        DeviceCapabilities capabilities = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string profilePath = Path.Combine(directory, "profiles.json");
        string preferencesPath = Path.Combine(directory, "battery-preferences.json");
        string markerPath = Path.Combine(directory, "battery-configuration.commit.json");
        Directory.CreateDirectory(directory);
        SeedConfiguration(
            profilePath,
            preferencesPath,
            markerPath,
            new BatteryConfiguration(NativeResolutionBase(), new BatteryAutomationPreferences(), Recovered: false));
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: validator);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = capabilities;
        BatteryConfiguration loaded = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsAuthoritative);
        runtime.Initialize(loaded, PowerSource.PluggedIn, PersistentOverrideState.Empty);
        await runtime.ObserveAsync(CancellationToken.None);
        Guid instance = Guid.Parse("cccccccc-bbbb-cccc-dddd-000000000004");

        JsonObject Row(ControlOperationResult result, int setters, bool transition = false)
        {
            var expect = new JsonObject { ["status"] = (int)result.Status };
            if (transition)
            {
                expect["transition"] = true;
            }
            else
            {
                expect["message"] = result.Message;
            }

            expect["setters"] = setters;
            return expect;
        }

        async Task<JsonObject> Measure(Func<Task<ControlOperationResult>> request, bool transition = false)
        {
            int before = context.Device.TotalSetterCalls;
            ControlOperationResult result = await request();
            return Row(result, context.Device.TotalSetterCalls - before, transition);
        }

        var report = new JsonArray();
        (long Sequence, string? Path, bool LowPower)[] steps =
        [
            (1, @"C:\Games\Game.exe", true),
            (2, @"C:\Games\Game.exe", true),
            (3, @"C:\Tools\Other.exe", true),
            (4, null, true),
            (5, @"C:\Games\Game.exe", false),
            (6, @"C:\Games\Game.exe", false),
        ];
        foreach ((long sequence, string? path, bool lowPower) in steps)
        {
            if (lowPower)
            {
                context.LowPowerGate.Enter();
            }
            else
            {
                context.LowPowerGate.Exit();
            }

            JsonObject expect = await Measure(
                async () => await runtime.ReportApplicationContextAsync(
                    new ReportApplicationContextRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), instance, sequence, path),
                    () => true,
                    CancellationToken.None),
                transition: !lowPower && sequence == 5);
            report.Add(new JsonObject
            {
                ["sequence"] = sequence,
                ["path"] = path,
                ["lowPower"] = lowPower,
                ["expect"] = expect,
            });
        }

        // Game is active and reconciled; the save changes its intent.
        context.LowPowerGate.Enter();
        ProfileSet current = runtime.GetStatus().Profiles!;
        ApplicationProfileDefinition game = current.ApplicationProfiles[0];
        ProfileSet edited = current with
        {
            ApplicationProfiles =
            [
                game with
                {
                    Overrides = game.Overrides with
                    {
                        PluggedIn = game.Overrides.PluggedIn! with { Performance = PerformancePreset.Silent },
                    },
                },
                .. current.ApplicationProfiles.Skip(1),
            ],
        };
        JsonObject save = await Measure(() => runtime.UpdateProfilesAsync(edited, CancellationToken.None));

        MethodInfo apply = typeof(ServiceControlRuntime).GetMethod(
            "ApplyFullTransitionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] applyArguments = [.. apply.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null)];
        applyArguments[0] = PowerSource.PluggedIn;
        applyArguments[1] = CancellationToken.None;
        JsonObject startup = await Measure(async () =>
            (await (Task<ServiceTransitionApplicationOutcome>)apply.Invoke(runtime, applyArguments)!).ToOperationResult());

        MethodInfo block = typeof(ServiceControlRuntime).GetMethod(
            "ScopedThermalWriteBlockReason",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(lighting, SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(80), true, false));
        object?[] blockArguments = [.. block.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null)];
        blockArguments[0] = desired;
        blockArguments[3] = new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans };
        var writeBlock = new JsonObject { ["message"] = (string?)block.Invoke(runtime, blockArguments) };

        var explicitRows = new JsonArray();
        async Task Explicit(string power, PerformancePreset? performance, FanMode? fans)
        {
            explicitRows.Add(new JsonObject
            {
                ["power"] = power,
                ["operation"] = performance is null ? "setFanOverride" : "setPerformanceOverride",
                ["value"] = performance is { } preset ? (int)preset : (int)fans!.Value,
                ["expect"] = performance is { } requested
                    ? await Measure(() => runtime.SetPerformanceOverrideAsync(
                        new(requested, OverrideLifetime.Persistent),
                        CancellationToken.None))
                    : await Measure(() => runtime.SetFanOverrideAsync(
                        new(fans!.Value, OverrideLifetime.Persistent),
                        CancellationToken.None)),
            });
        }

        await Explicit("pluggedIn", PerformancePreset.Silent, null);
        await Explicit("pluggedIn", null, FanMode.Maximum);

        // The same requests on battery, which Windows alone classifies. The
        // source changes with the gate clear, so no transition is deferred.
        context.LowPowerGate.Exit();
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.LowPowerGate.Enter();
        await Explicit("battery", PerformancePreset.BatterySaver, null);
        await Explicit("battery", null, FanMode.Maximum);
        await Explicit("battery", null, FanMode.Automatic);
        context.LowPowerGate.Exit();

        return new JsonObject
        {
            ["report"] = report,
            ["save"] = save,
            ["startup"] = startup,
            ["writeBlock"] = writeBlock,
            ["explicit"] = explicitRows,
        };
    }
}
