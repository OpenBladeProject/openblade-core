using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native Service serves ReapplyChangedSettings for the thermal subsystems it
// tracks for drift. These rows pin what managed replies and leaves in the shared
// convergence ledger for the same inputs; the Rust tests replay each row on the
// native rig.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeThermalReapplyVectors = "thermal-reapply-vectors.json";
    private const string RegenerateNativeThermalReapplyVectors =
        "OPENBLADE_REGENERATE_NATIVE_THERMAL_REAPPLY_VECTORS";

    private const string NativeThermalReapplyComment =
        "Recorded by ServiceControlRuntimeTests.NativeThermalReapplyVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_THERMAL_REAPPLY_VECTORS set to this directory. Each row creates the "
        + "thermal convergence context on AC, runs ReconcileStartupProfileAsync and one baseline ObserveAsync, "
        + "optionally confirms Performance and observes again (drift), seeds the ledger (tombstoned: one "
        + "Performance tombstone, its terminal result and a spent budget; tombstone: that tombstone and terminal "
        + "result without the budget; budget: the spent budget alone; "
        + "inFlight: a Performance reservation with that budget), optionally appends a space to the ledger file "
        + "(changedOnDisk), latches a pending Performance write confirmation (pending), runs one "
        + "ReapplyChangedSettingsAsync (sessionChanged makes stillControlsSession false) and records the result, "
        + "the ledger the store then loads and whether the reapply called any setter.";

    [Fact]
    public async Task NativeThermalReapplyVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeThermalReapplyVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeThermalReapplyVectors)
            : Path.Combine(output, NativeThermalReapplyVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "replies"], root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeThermalReapplyComment;
        }

        Assert.Equal(NativeThermalReapplyComment, (string)root["comment"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        foreach (JsonNode? entry in root["replies"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            (JsonNode result, JsonNode ledger, bool wrote) = await NativeReapplyAsync(name, row);
            Record(row, "result", result);
            Record(row, "ledger", ledger);
            Record(row, "wrote", wrote);
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString())
                .Append(",\n\"replies\":[\n")
                .AppendJoin(",\n", root["replies"]!.AsArray().Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
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

    private async Task<(JsonNode Result, JsonNode Ledger, bool Wrote)> NativeReapplyAsync(
        string name,
        JsonObject row)
    {
        ThermalConvergenceTestContext thermal =
            await CreateThermalConvergenceContextAsync("native-reapply-" + name);
        TestContext context = thermal.Context;
        ServiceControlRuntime runtime = context.Runtime;
        await runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        await runtime.ObserveAsync(CancellationToken.None);
        if ((bool?)row["drift"] == true)
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.Performance,
            };
            await runtime.ObserveAsync(CancellationToken.None);
            Assert.Contains(DeviceSubsystem.Performance, runtime.GetStatus().ExternalChanges);
        }

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        if ((string?)row["seed"] is { } seed)
        {
            FieldInfo ledgerField = typeof(ServiceControlRuntime).GetField(
                "thermalConvergenceLedger",
                Private)!;
            ThermalConvergenceLedgerState current = Assert.IsType<ThermalConvergenceLedgerState>(
                ledgerField.GetValue(runtime));
            string performanceRevision = current.PerformanceDesiredRevision!;
            string policyRevision = current.PolicyRevision!;
            ThermalConvergenceBudget[] budgets =
            [
                new(PowerSource.PluggedIn, ThermalComponent.Performance, performanceRevision, policyRevision, 1, 1),
            ];
            DesiredDeviceState desired = runtime.GetStatus().DesiredState;
            ThermalConvergenceLedgerState tombstoned = current with
            {
                SettledDestination = PowerSource.PluggedIn,
                Contradiction = ThermalConvergenceContradiction.Performance,
                Tombstones =
                [
                    new(
                        PowerSource.PluggedIn,
                        ThermalConvergenceContradiction.Performance,
                        [ThermalComponent.Performance],
                        performanceRevision,
                        null,
                        policyRevision,
                        ThermalTransitionDisposition.Confirmed),
                ],
                TerminalDisposition = ThermalTransitionDisposition.Confirmed,
                TerminalAffectedComponents = [ThermalComponent.Performance],
                Budgets = budgets,
            };
            ThermalConvergenceLedgerState seeded = seed switch
            {
                "tombstoned" => tombstoned,
                "tombstone" => tombstoned with { Budgets = [] },
                "budget" => current with { Budgets = budgets },
                "inFlight" => current with
                {
                    SettledDestination = PowerSource.PluggedIn,
                    Contradiction = ThermalConvergenceContradiction.Performance,
                    InFlight = new ThermalConvergenceInFlight(
                        Guid.NewGuid(),
                        [ThermalComponent.Performance],
                        context.Device.ConfirmedState,
                        desired,
                        ReconciliationRequired: true),
                    Budgets = budgets,
                },
                _ => throw new InvalidOperationException(seed),
            };
            ThermalConvergenceLoadResult stored = await thermal.Store.LoadAsync(CancellationToken.None);
            ThermalConvergenceMutationResult saved = await thermal.Store.TrySaveAsync(
                stored.Sha256,
                seeded,
                CancellationToken.None);
            Assert.True(saved.Applied, name);
            ledgerField.SetValue(runtime, seeded);
            typeof(ServiceControlRuntime)
                .GetField("thermalConvergenceLedgerSha256", Private)!
                .SetValue(runtime, saved.Sha256);
        }

        if ((bool?)row["changedOnDisk"] == true)
        {
            await File.AppendAllTextAsync(
                Path.Combine(directory, "native-reapply-" + name, "users", thermal.UserSid.Value, "thermal-convergence.json"),
                " ");
        }

        if ((bool?)row["pending"] == true)
        {
            ServiceStatusSnapshot status = runtime.GetStatus();
            typeof(ServiceControlRuntime)
                .GetMethod("SetPendingWriteConfirmation", Private)!
                .Invoke(
                    runtime,
                    [
                        DeviceSubsystem.Performance,
                        new PendingWriteConfirmation(
                            status.ActivePowerSource,
                            status.DesiredState,
                            context.Device.ConfirmedState),
                    ]);
        }

        int writesBefore = context.Device.TotalSetterCalls;
        bool sessionChanged = (bool?)row["sessionChanged"] == true;
        ControlOperationResult result = await runtime.ReapplyChangedSettingsAsync(
            () => !sessionChanged,
            CancellationToken.None);
        ThermalConvergenceLoadResult after = await thermal.Store.LoadAsync(CancellationToken.None);
        ThermalConvergenceLedgerState ledger = after.State;
        return (
            new JsonObject
            {
                ["status"] = (int)result.Status,
                ["message"] = result.Message,
            },
            new JsonObject
            {
                ["tombstones"] = ledger.Tombstones.Count,
                ["budgets"] = ledger.Budgets.Count,
                ["terminalDisposition"] = (int?)ledger.TerminalDisposition,
                ["inFlight"] = ledger.InFlight is not null,
            },
            context.Device.TotalSetterCalls > writesBefore);
    }
}
