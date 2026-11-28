using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Native request-driven reconciliation is pinned against managed: the
// ServiceTransitionApplicationOutcome.ToOperationResult table, the
// DeviceSubsystemOrder.ForTransition order choice and the
// ReportApplicationContextAsync reply sequence. The Rust readiness tests read
// the same file.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeRequestTriggerVectors = "request-trigger-vectors.json";

    private const string NativeRequestTriggerComment =
        "Recorded from managed by ServiceControlRuntimeTests.NativeRequestTriggerVectors; regenerate with OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS. "
        + "A report row whose expect has transition true ran ApplyFullTransitionAsync: its reply starts with 'The active application profile was recorded. ' and carries the transition message.";

    private const string NativeRecordedPrefix = "The active application profile was recorded. ";

    [Fact]
    public async Task NativeRequestTriggerVectors_MatchManagedReplies()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeRequestTriggerVectors)
            : Path.Combine(output, NativeRequestTriggerVectors);
        JsonArray operation = ObserveNativeToOperationResult();
        JsonArray order = ObserveNativeForTransition();
        JsonArray report = await ObserveNativeReportSequenceAsync();
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            static string Rows(JsonArray rows) =>
                string.Join(",\n", rows.Select(row => row!.ToJsonString()));
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeRequestTriggerComment).ToJsonString())
                .Append(",\n\"toOperationResult\":[\n").Append(Rows(operation))
                .Append("\n],\n\"forTransition\":[\n").Append(Rows(order))
                .Append("\n],\n\"report\":[\n").Append(Rows(report))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        Assert.Equal(NativeRequestTriggerComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["toOperationResult"], operation));
        Assert.True(JsonNode.DeepEquals(recorded["forTransition"], order));
        Assert.True(JsonNode.DeepEquals(recorded["report"], report), report.ToJsonString());
    }

    // Every combination of the facts ToOperationResult reads.
    private static JsonArray ObserveNativeToOperationResult()
    {
        var rows = new JsonArray();
        for (int bits = 0; bits < 32; bits++)
        {
            bool fullyApplied = (bits & 1) != 0;
            bool intentStored = (bits & 2) != 0;
            bool submissionPossible = (bits & 4) != 0;
            bool anyConfirmedWrites = (bits & 8) != 0;
            bool anyWritesAttempted = (bits & 16) != 0;
            var outcome = new ServiceTransitionApplicationOutcome(
                fullyApplied,
                StopFurtherWrites: false,
                submissionPossible,
                anyConfirmedWrites,
                "message",
                AnyWritesAttempted: anyWritesAttempted);
            rows.Add(new JsonObject
            {
                ["fullyApplied"] = fullyApplied,
                ["intentStored"] = intentStored,
                ["submissionPossible"] = submissionPossible,
                ["anyConfirmedWrites"] = anyConfirmedWrites,
                ["anyWritesAttempted"] = anyWritesAttempted,
                ["status"] = (int)outcome.ToOperationResult(intentStored).Status,
            });
        }

        return rows;
    }

    // Each confirmed fan mode, unknown included, against each desired one.
    private static JsonArray ObserveNativeForTransition()
    {
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        var rows = new JsonArray();
        FanMode?[] initial = [null, .. Enum.GetValues<FanMode>().Cast<FanMode?>()];
        foreach (FanMode? confirmed in initial)
        {
            foreach (FanMode desiredFans in Enum.GetValues<FanMode>())
            {
                var desired = new DesiredDeviceState(
                    new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                    new ResolvedSetting<FanMode>(desiredFans, SettingValueSource.PowerProfile),
                    new ResolvedSetting<LightingSettings>(lighting, SettingValueSource.PowerProfile),
                    new DesiredBatteryState(BatteryPolicy.Create(80), true, false));
                IReadOnlyList<DeviceSubsystem> order = DeviceSubsystemOrder.ForTransition(
                    new ConfirmedDeviceState(null, confirmed, null, null), desired);
                rows.Add(new JsonObject
                {
                    ["initial"] = confirmed is null ? null : (int)confirmed.Value,
                    ["desired"] = (int)desiredFans,
                    ["initialManual"] = DeviceSubsystemOrder.IsManualFanMode(confirmed),
                    ["desiredAutomatic"] = desiredFans == FanMode.Automatic,
                    ["fansFirst"] = ReferenceEquals(order, DeviceSubsystemOrder.FansFirst),
                });
            }
        }

        return rows;
    }

    // One reporter through a change, a stale and a renewed report, leaving the
    // rule, a report with no application, and a caller that lost the session.
    private async Task<JsonArray> ObserveNativeReportSequenceAsync()
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
        Guid instance = Guid.Parse("cccccccc-bbbb-cccc-dddd-000000000003");
        (long Sequence, string? Path, bool ControlsSession)[] steps =
        [
            (1, @"C:\Games\Game.exe", true),
            (1, @"C:\Games\Game.exe", true),
            (2, @"C:\Games\Game.exe", true),
            (3, @"C:\Tools\Other.exe", true),
            (4, null, true),
            (99, null, false),
        ];
        var rows = new JsonArray();
        foreach ((long sequence, string? path, bool controlsSession) in steps)
        {
            ControlOperationResult result = await runtime.ReportApplicationContextAsync(
                new ReportApplicationContextRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), instance, sequence, path),
                () => controlsSession,
                CancellationToken.None);
            bool transition = result.Message.StartsWith(NativeRecordedPrefix, StringComparison.Ordinal);
            var expect = new JsonObject { ["status"] = (int)result.Status };
            if (transition)
            {
                expect["transition"] = true;
            }
            else
            {
                expect["message"] = result.Message;
            }

            rows.Add(new JsonObject
            {
                ["sequence"] = sequence,
                ["path"] = path,
                ["controlsSession"] = controlsSession,
                ["expect"] = expect,
            });
        }

        return rows;
    }
}
