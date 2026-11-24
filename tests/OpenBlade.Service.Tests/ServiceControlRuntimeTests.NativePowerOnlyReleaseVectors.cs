using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Managed's stale power-only write block (1a03e358, 3f7b3838) is pinned for
// the native Service: a classified power change after conflicting adapter
// classification applies the destination thermal step and publishes the
// classified source, a block another owner holds is kept, and an unresolved
// destination submission is never replayed. The native readiness tests in
// `power_only_release.rs` replay the same file.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativePowerOnlyReleaseVectors = "power-only-release-vectors.json";

    private const string NativePowerOnlyReleaseComment =
        "Recorded from managed by ServiceControlRuntimeTests.NativePowerOnlyReleaseVectors; regenerate with OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS. "
        + "Each case starts on PluggedIn with Balanced confirmed, then reads conflicting adapter classes on ExternalUnknown power, which publishes a power-only block (blocked). "
        + "after is the status once Windows reports Battery: performanceSetters lists the presets submitted by that change, and authorityClosed whether its message says the thermal write authority closed. "
        + "owner names a block that is not power-only: writePause is an experimental write pause. "
        + "recovered is the status after a successful observation and another power-source change.";

    [Fact]
    public async Task NativePowerOnlyReleaseVectors_MatchManagedTransitions()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativePowerOnlyReleaseVectors)
            : Path.Combine(output, NativePowerOnlyReleaseVectors);
        JsonArray observed = await ObserveNativePowerOnlyReleaseAsync();
        if (!string.IsNullOrEmpty(output))
        {
            // One case per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativePowerOnlyReleaseComment).ToJsonString())
                .Append(",\n\"cases\":[\n")
                .Append(string.Join(",\n", observed.Select(row => row!.ToJsonString())))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        Assert.Equal(NativePowerOnlyReleaseComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["cases"], observed), observed.ToJsonString());
    }

    private async Task<JsonArray> ObserveNativePowerOnlyReleaseAsync()
    {
        static JsonObject Status(ServiceStatusSnapshot status) => new()
        {
            ["writesBlocked"] = status.WritesBlocked,
            ["activePowerSource"] = (int)status.ActivePowerSource,
            ["confirmedPerformance"] = status.ConfirmedState?.Performance is { } confirmed ? (int)confirmed : null,
            ["desiredPerformance"] = (int)status.DesiredState.Performance.Value,
            ["unconfirmedWrites"] = new JsonArray([.. status.UnconfirmedWrites.Select(subsystem => JsonValue.Create((int)subsystem))]),
            ["authorityClosed"] = status.Message.Contains("Thermal write authority closed", StringComparison.Ordinal),
        };

        static JsonObject Blocked(ServiceStatusSnapshot status) => new()
        {
            ["writesBlocked"] = status.WritesBlocked,
            ["activePowerSource"] = (int)status.ActivePowerSource,
            ["observationReason"] = (int)status.ObservationReason,
        };

        async Task<JsonObject> PowerChange(TestContext context)
        {
            int before = context.Device.PerformanceWrites.Count;
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
            JsonObject after = Status(context.Runtime.GetStatus());
            after["performanceSetters"] = new JsonArray(
                [.. context.Device.PerformanceWrites.Skip(before).Select(preset => JsonValue.Create((int)preset))]);
            return after;
        }

        var cases = new JsonArray();
        foreach (bool transientRead in new[] { false, true })
        {
            TestContext context = await CreateStalePowerOnlyBlockContextAsync(transientRead);
            JsonObject blocked = Blocked(context.Runtime.GetStatus());
            cases.Add(new JsonObject
            {
                ["case"] = transientRead
                    ? "a classified power change applies the destination over a power-only block under a transient read label"
                    : "a classified power change applies the destination over a power-only block",
                ["transientRead"] = transientRead,
                ["blocked"] = blocked,
                ["after"] = await PowerChange(context),
            });
        }

        TestContext paused = await CreateStalePowerOnlyBlockContextAsync(transientRead: false);
        JsonObject pausedBlocked = Blocked(paused.Runtime.GetStatus());
        paused.Provider.IsExperimentalWritesPaused = true;
        cases.Add(new JsonObject
        {
            ["case"] = "a block another owner holds is kept",
            ["owner"] = "writePause",
            ["blocked"] = pausedBlocked,
            ["after"] = await PowerChange(paused),
        });

        TestContext lost = await CreateStalePowerOnlyBlockContextAsync(transientRead: false);
        JsonObject lostBlocked = Blocked(lost.Runtime.GetStatus());
        lost.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        lost.Device.MutatePerformanceBeforeQueuedFailure = true;
        lost.Device.BeforeQueuedPerformanceFailure = () =>
            lost.Device.ReadStateFailure = new IOException("Injected lost readback.");
        JsonObject lostAfter = await PowerChange(lost);
        lost.Device.ReadStateFailure = null;
        lost.Device.BeforeQueuedPerformanceFailure = null;
        int recoveredFrom = lost.Device.PerformanceWrites.Count;
        await lost.Runtime.ObserveAsync(CancellationToken.None);
        await lost.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        JsonObject recovered = Status(lost.Runtime.GetStatus());
        recovered["performanceSetters"] = new JsonArray(
            [.. lost.Device.PerformanceWrites.Skip(recoveredFrom).Select(preset => JsonValue.Create((int)preset))]);
        cases.Add(new JsonObject
        {
            ["case"] = "an unresolved destination submission is not replayed",
            ["blocked"] = lostBlocked,
            ["after"] = lostAfter,
            ["recovered"] = recovered,
        });
        return cases;
    }
}
