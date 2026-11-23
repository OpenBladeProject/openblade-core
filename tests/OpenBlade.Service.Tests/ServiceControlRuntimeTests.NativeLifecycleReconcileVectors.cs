using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native lifecycle tick's reconciliations are pinned against managed: what
// ApplyFullTransitionAsync applies after the application lease expires and
// after a power-source change, and what a resume on an unchanged source
// applies after a report the LowPowerGate deferred. The Rust readiness tests
// read the same file and assert where native deliberately differs.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeLifecycleReconcileVectors = "lifecycle-reconcile-vectors.json";

    private const string NativeLifecycleReconcileComment =
        "Recorded from managed by ServiceControlRuntimeTests.NativeLifecycleReconcileVectors; regenerate with OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS. "
        + "profiles is the saved performance the runtime starts from: the plugged-in and battery power profiles and the Game rule's plugged-in override, with Game reported and applied on AC. "
        + "Each transition row runs in order on that runtime: submitted is whether any device setter was submitted, performance the confirmed preset after it, desired the desired preset and source its source. "
        + "leaseExpired is ExpireApplicationContextAsync after the 15 s lease; gameReported reports Game again. "
        + "powerUnchanged, powerToBattery and powerToPluggedIn are HandlePowerSourceChangedAsync with the gate clear. "
        + "resumeUnchangedAfterDeferral reports Other.exe with the LowPowerGate entered, exits it, and runs HandlePowerSourceChangedAsync on the unchanged source, as resume processing does.";

    [Fact]
    public async Task NativeLifecycleReconcileVectors_MatchManagedTransitions()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeLifecycleReconcileVectors)
            : Path.Combine(output, NativeLifecycleReconcileVectors);
        JsonObject observed = await ObserveNativeLifecycleReconcileAsync();
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            string rows = string.Join(
                ",\n",
                observed["transitions"]!.AsArray().Select(row => row!.ToJsonString()));
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeLifecycleReconcileComment).ToJsonString())
                .Append(",\n\"profiles\":").Append(observed["profiles"]!.ToJsonString())
                .Append(",\n\"transitions\":[\n").Append(rows)
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        Assert.Equal(NativeLifecycleReconcileComment, recorded["comment"]!.GetValue<string>());
        foreach (string member in new[] { "profiles", "transitions" })
        {
            Assert.True(JsonNode.DeepEquals(recorded[member], observed[member]), observed[member]!.ToJsonString());
        }
    }

    // The native readiness fixture's saved performance: Balanced plugged in,
    // Battery Saver on battery, and a Game rule that overrides only plugged-in
    // performance with Performance.
    private static ProfileSet NativeLifecycleProfiles()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        return defaults with
        {
            Battery = defaults.Battery with { Performance = PerformancePreset.BatterySaver },
            ApplicationProfiles =
            [
                new ApplicationProfileDefinition(
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011"),
                    "Game",
                    @"C:\Games\Game.exe",
                    ApplicationActivationMode.Foreground,
                    new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride { Performance = PerformancePreset.Performance },
                    }),
            ],
        };
    }

    private async Task<JsonObject> ObserveNativeLifecycleReconcileAsync()
    {
        DeviceCapabilities capabilities = NativeProfileDeviceCapabilities(out ProfileSetValidator validator);
        string profilePath = Path.Combine(directory, "profiles.json");
        string preferencesPath = Path.Combine(directory, "battery-preferences.json");
        string markerPath = Path.Combine(directory, "battery-configuration.commit.json");
        Directory.CreateDirectory(directory);
        ProfileSet profiles = NativeLifecycleProfiles();
        SeedConfiguration(
            profilePath,
            preferencesPath,
            markerPath,
            new BatteryConfiguration(profiles, new BatteryAutomationPreferences(), Recovered: false));
        var clock = new HeldLeaseTimeProvider();
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: validator,
            timeProvider: clock);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = capabilities;
        BatteryConfiguration loaded = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(loaded.IsAuthoritative);
        runtime.Initialize(loaded, PowerSource.PluggedIn, PersistentOverrideState.Empty);
        await runtime.ObserveAsync(CancellationToken.None);
        Guid instance = Guid.Parse("cccccccc-bbbb-cccc-dddd-000000000011");
        long sequence = 0;

        Task<ControlOperationResult> Report(string? path) =>
            runtime.ReportApplicationContextAsync(
                new ReportApplicationContextRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), instance, ++sequence, path),
                () => true,
                CancellationToken.None);

        var transitions = new JsonArray();
        async Task Record(string transition, Func<Task> run)
        {
            int before = context.Device.TotalSetterCalls;
            await run();
            ServiceStatusSnapshot status = runtime.GetStatus();
            transitions.Add(new JsonObject
            {
                ["transition"] = transition,
                ["submitted"] = context.Device.TotalSetterCalls > before,
                ["performance"] = status.ConfirmedState?.Performance is { } performance ? (int)performance : null,
                ["source"] = status.DesiredState is { } desired ? (int)desired.Performance.Source : null,
                ["desired"] = status.DesiredState is { } wanted ? (int)wanted.Performance.Value : null,
            });
        }

        async Task PowerChanged(PowerSource power)
        {
            context.Telemetry.Current = CreateTelemetry(power);
            await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        }

        ControlOperationResult applied = await Report(@"C:\Games\Game.exe");
        // The scripted device leaves battery and lighting unapplied, so the
        // transition replies Failed; only its thermal result is recorded below.
        Assert.StartsWith(NativeRecordedPrefix, applied.Message, StringComparison.Ordinal);
        await Record("leaseExpired", async () =>
        {
            clock.ExpireLeases();
            // The expiry publishes the power profile's desired source under the
            // operation gate before it applies; the next gated call waits for
            // that transition to finish.
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (runtime.GetStatus().DesiredState?.Performance.Source != SettingValueSource.PowerProfile)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), bounded.Token);
            }

            await runtime.ObserveAsync(CancellationToken.None);
        });
        await Record("gameReported", () => Report(@"C:\Games\Game.exe"));
        await Record("powerUnchanged", () => PowerChanged(PowerSource.PluggedIn));
        await Record("powerToBattery", () => PowerChanged(PowerSource.Battery));
        await Record("powerToPluggedIn", () => PowerChanged(PowerSource.PluggedIn));
        await Record("resumeUnchangedAfterDeferral", async () =>
        {
            context.LowPowerGate.Enter();
            ControlOperationResult deferred = await Report(@"C:\Tools\Other.exe");
            Assert.Equal(ControlOperationStatus.Blocked, deferred.Status);
            context.LowPowerGate.Exit();
            await PowerChanged(PowerSource.PluggedIn);
        });

        return new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                ["pluggedIn"] = (int)profiles.PluggedIn.Performance,
                ["battery"] = (int)profiles.Battery.Performance,
                ["game"] = (int)profiles.ApplicationProfiles[0].Overrides.PluggedIn!.Performance!.Value,
            },
            ["transitions"] = transitions,
        };
    }

    // The system clock, except that the 15 s application-context lease
    // timer fires only when the test expires it.
    private sealed class HeldLeaseTimeProvider : TimeProvider
    {
        private static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);
        private readonly List<HeldTimer> held = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Lease)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }

            var timer = new HeldTimer(callback, state);
            lock (held)
            {
                held.Add(timer);
            }

            return timer;
        }

        public void ExpireLeases()
        {
            HeldTimer[] due;
            lock (held)
            {
                due = [.. held];
                held.Clear();
            }

            foreach (HeldTimer timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
        {
            private int disposed;

            public void Fire()
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
