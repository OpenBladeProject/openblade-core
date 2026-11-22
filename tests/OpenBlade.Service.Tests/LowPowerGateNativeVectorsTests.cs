using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBlade.Service.Tests;

// The native low-power gate (rust/crates/openblade-domain/src/low_power_gate.rs)
// is pinned against managed. Each case replays one LowPowerGate over a QPC
// clock of the case's frequency, calling TryApply as the managed suppliers
// call it, and records each step's result, whether TransitionChanged was
// raised and CurrentTransition after it. The Rust domain tests read the same
// file.
public sealed class LowPowerGateNativeVectorsTests
{
    private const string NativeLowPowerGateVectors = "low-power-gate-vectors.json";
    private const string RegenerateNativeLowPowerGateVectors =
        "OPENBLADE_REGENERATE_NATIVE_LOW_POWER_GATE_VECTORS";

    private const string NativeLowPowerGateComment =
        "Recorded by LowPowerGateNativeVectorsTests; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_LOW_POWER_GATE_VECTORS set to this directory. "
        + "A signal step calls TryApply as managed suppliers do: suspend is (true, at), "
        + "resume is (false, at, newResume: true) and displayOn is (false, at). "
        + "retain and release call RetainResumeGeneration and ReleaseResumeGeneration; "
        + "activeFor calls IsActiveForGeneration. Each expect row holds the step's result "
        + "(TryApply's or IsActiveForGeneration's return, else null), whether TransitionChanged "
        + "was raised, and the generation and boundary timestamp of CurrentTransition after it. "
        + "Every malformed row is a case both readers refuse.";

    private const long Second = 10_000_000;

    private static readonly string[] Signals = ["suspend", "resume", "displayOn"];
    private static readonly string[] Probes = ["retain", "release", "activeFor"];

    [Fact]
    public void NativeLowPowerGateVectors_MatchManagedGate()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeLowPowerGateVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeLowPowerGateVectors)
            : Path.Combine(output, NativeLowPowerGateVectors);
        JsonObject expected = NativeLowPowerGateRows();
        JsonObject root = string.IsNullOrEmpty(output)
            ? ReadNativeLowPowerGateVectors(File.ReadAllText(path))
            : expected;

        // The file holds exactly the reviewed rows: a dropped or edited input
        // row would otherwise weaken the Rust side silently.
        Assert.True(JsonNode.DeepEquals(InputsOnly(root), InputsOnly(expected)));
        foreach (JsonNode? row in root["malformed"]!.AsArray())
        {
            Assert.Throws<InvalidDataException>(() => NativeLowPowerGateCase(row));
        }

        var failures = new List<string>();
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            JsonArray observed = ReplayNativeLowPowerGateCase(vector);
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
                .AppendJoin(",\n", root["cases"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n],\n\"malformed\":[\n")
                .AppendJoin(",\n", root["malformed"]!.AsArray().Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("""{"comment":"","cases":[]}""")]
    [InlineData("""{"comment":"","cases":[],"malformed":[],"extra":1}""")]
    [InlineData("""{"comment":"","cases":{},"malformed":[]}""")]
    public void NativeLowPowerGateVectors_RejectMalformedFiles(string text) =>
        Assert.Throws<InvalidDataException>(() => ReadNativeLowPowerGateVectors(text));

    private static JsonObject NativeLowPowerGateRows()
    {
        JsonObject[] cases =
        [
            Case("boundary/suspend-resume-suspend",
                Signal("suspend", 100), ActiveFor(1), Signal("resume", 150), ActiveFor(1), ActiveFor(2),
                Signal("suspend", 160), ActiveFor(1), ActiveFor(3)),
            Case("boundary/repeated-suspend-keeps-its-boundary",
                Signal("suspend", 100), Signal("suspend", Second), Signal("resume", Second + 1)),
            Case("order/older-resume-cannot-clear-a-suspend",
                Signal("suspend", 200), Signal("resume", 100), Signal("displayOn", 199), ActiveFor(1),
                Signal("displayOn", 200)),
            Case("order/stale-suspend-after-resume",
                Signal("suspend", 100), Signal("resume", 300), Signal("suspend", 200), Signal("suspend", 300),
                ActiveFor(1), ActiveFor(3), Signal("suspend", 301), ActiveFor(3)),
            Case("order/same-timestamp",
                Signal("suspend", 100), Signal("resume", 100), Signal("suspend", 100), Signal("suspend", 101),
                Signal("suspend", 101), Signal("resume", 101), Signal("displayOn", 101)),
            Case("order/latest-not-boundary-orders-a-clear",
                Signal("suspend", 100), Signal("suspend", 300), Signal("resume", 200), Signal("displayOn", 299),
                Signal("resume", 300)),
            Case("order/latest-not-boundary-orders-a-suspend",
                Signal("suspend", 100), Signal("resume", 200), Signal("displayOn", 400), Signal("suspend", 300),
                Signal("suspend", 400), Signal("suspend", 401)),
            Case("order/initial-timestamp-is-zero",
                Signal("suspend", 0), Signal("resume", -1), Signal("displayOn", 0), Signal("suspend", 1)),
            Case("resume/duplicate-within-two-seconds",
                Signal("suspend", Second), Signal("resume", 2 * Second), Signal("resume", 3 * Second),
                Signal("resume", 4 * Second), Signal("displayOn", (4 * Second) + 1),
                Signal("resume", (4 * Second) + 1), Signal("resume", (6 * Second) + 1), ActiveFor(4)),
            Case("resume/retained-generation",
                Signal("suspend", Second), Signal("resume", 2 * Second), Retain(1), Retain(4), Retain(2),
                Signal("resume", 5 * Second), Release(4), Signal("resume", 6 * Second), Release(2),
                Signal("resume", 7 * Second)),
            Case("resume/stale-retention-does-not-absorb",
                Signal("suspend", Second), Signal("resume", 2 * Second), Retain(2), Signal("suspend", 3 * Second),
                Signal("resume", 4 * Second), Signal("resume", 7 * Second), Release(2), Release(6)),
            Case("resume/first-late-resume-only-sets-the-boundary",
                Signal("resume", 5 * Second), Signal("resume", (7 * Second) + 1)),
            Case("resume/retention-follows-the-latest-clear-generation",
                Signal("suspend", Second), Signal("resume", 2 * Second), Retain(2), Signal("suspend", 3 * Second),
                Signal("resume", 4 * Second), Retain(4), Signal("resume", 7 * Second)),
            Case("resume/a-boundary-of-one-is-set",
                Signal("displayOn", 1), Signal("resume", (2 * Second) + 2)),
            Case("resume/missed-suspend",
                Signal("resume", Second), Signal("resume", 2 * Second), Signal("resume", (3 * Second) + 1),
                ActiveFor(1), ActiveFor(2)),
            CaseAt("resume/three-megahertz-clock", 3_000_000,
                Signal("suspend", 1), Signal("resume", 2), Signal("resume", 6_000_002), Signal("resume", 6_000_003)),
            Case("displayOn/recovers-and-never-reenters",
                Signal("suspend", Second), Signal("displayOn", 2 * Second), Signal("displayOn", 5 * Second),
                Signal("resume", 8 * Second)),
            Case("displayOn/while-clear-sets-the-boundary",
                Signal("displayOn", Second), Signal("resume", Second + 1), Signal("resume", (3 * Second) + 1)),
        ];
        JsonNode[] malformed =
        [
            new JsonObject { ["name"] = "x", ["frequency"] = 0, ["steps"] = new JsonArray() },
            new JsonObject { ["name"] = "x", ["frequency"] = Second },
            new JsonObject { ["name"] = "x", ["frequency"] = Second, ["steps"] = new JsonArray(), ["extra"] = 1 },
            new JsonObject
            {
                ["name"] = "x", ["frequency"] = Second, ["steps"] = new JsonArray(Signal("sleep", 1)),
            },
            new JsonObject
            {
                ["name"] = "x", ["frequency"] = Second,
                ["steps"] = new JsonArray(new JsonObject { ["signal"] = "suspend" }),
            },
            new JsonObject
            {
                ["name"] = "x", ["frequency"] = Second,
                ["steps"] = new JsonArray(new JsonObject { ["signal"] = "suspend", ["at"] = 1.5 }),
            },
            new JsonObject
            {
                ["name"] = "x", ["frequency"] = Second,
                ["steps"] = new JsonArray(new JsonObject { ["retain"] = 1, ["release"] = 1 }),
            },
            new JsonObject { ["name"] = "x", ["frequency"] = Second, ["steps"] = new JsonArray(ActiveFor(-1)) },
            new JsonObject
            {
                ["name"] = "x", ["frequency"] = Second,
                ["steps"] = new JsonArray(new JsonObject { ["retain"] = "2" }),
            },
        ];
        return new JsonObject
        {
            ["comment"] = NativeLowPowerGateComment,
            ["cases"] = new JsonArray(cases),
            ["malformed"] = new JsonArray(malformed),
        };

        static JsonObject Case(string name, params JsonObject[] steps) => CaseAt(name, Second, steps);

        static JsonObject CaseAt(string name, long frequency, params JsonObject[] steps) => new()
        {
            ["name"] = name,
            ["frequency"] = frequency,
            ["steps"] = new JsonArray(steps),
        };

        static JsonObject Signal(string signal, long at) => new() { ["signal"] = signal, ["at"] = at };
        static JsonObject Retain(long generation) => new() { ["retain"] = generation };
        static JsonObject Release(long generation) => new() { ["release"] = generation };
        static JsonObject ActiveFor(long generation) => new() { ["activeFor"] = generation };
    }

    private static JsonArray ReplayNativeLowPowerGateCase(JsonObject vector)
    {
        var gate = new LowPowerGate(new QpcClock(vector["frequency"]!.GetValue<long>()));
        int notified = 0;
        gate.TransitionChanged += () => notified++;
        var observed = new JsonArray();
        foreach (JsonNode? entry in vector["steps"]!.AsArray())
        {
            JsonObject step = entry!.AsObject();
            notified = 0;
            bool? result = null;
            if (step["signal"]?.GetValue<string>() is { } signal)
            {
                bool applied = gate.TryApply(signal == "suspend", step["at"]!.GetValue<long>(),
                    out LowPowerTransition transition, newResume: signal == "resume");
                Assert.Equal(gate.CurrentTransition, transition);
                result = applied;
            }
            else if (step["retain"] is { } retain)
            {
                gate.RetainResumeGeneration(retain.GetValue<long>());
            }
            else if (step["release"] is { } release)
            {
                gate.ReleaseResumeGeneration(release.GetValue<long>());
            }
            else
            {
                result = gate.IsActiveForGeneration(step["activeFor"]!.GetValue<long>());
            }

            LowPowerTransition current = gate.CurrentTransition;
            Assert.Equal(current.Generation, gate.Generation);
            Assert.Equal(current.Suspending, gate.IsActive);
            observed.Add(new JsonObject
            {
                ["result"] = result,
                ["changed"] = notified > 0,
                ["generation"] = current.Generation,
                ["boundary"] = current.TimestampQpc,
            });
        }

        return observed;
    }

    private static JsonObject InputsOnly(JsonObject root)
    {
        var copy = root.DeepClone().AsObject();
        foreach (JsonNode? entry in copy["cases"]!.AsArray())
        {
            entry!.AsObject().Remove("expect");
        }

        return copy;
    }

    // The file shape, checked before any row runs: an unknown or misspelt
    // member would otherwise be ignored and its case silently weakened.
    private static JsonObject ReadNativeLowPowerGateVectors(string text)
    {
        string[] sections = ["comment", "cases", "malformed"];
        JsonObject root = Shape(JsonNode.Parse(text), sections, sections);
        if (root["comment"]?.GetValueKind() != JsonValueKind.String)
        {
            throw new InvalidDataException("The comment is a string.");
        }

        foreach (JsonNode? entry in Rows(root["cases"]))
        {
            NativeLowPowerGateCase(entry);
        }

        Rows(root["malformed"]);
        return root;
    }

    private static JsonObject NativeLowPowerGateCase(JsonNode? node)
    {
        JsonObject vector = Shape(node, ["name", "frequency", "steps", "expect"], ["name", "frequency", "steps"]);
        if (vector["name"]?.GetValueKind() != JsonValueKind.String || Integer(vector["frequency"]) <= 0)
        {
            throw new InvalidDataException("A case has a name and a positive frequency.");
        }

        foreach (JsonNode? entry in Rows(vector["steps"]))
        {
            JsonObject step = Shape(entry, ["signal", "at", .. Probes], []);
            switch (step.Select(member => member.Key).Order(StringComparer.Ordinal).ToArray())
            {
                case ["at", "signal"]:
                    if (step["signal"]?.GetValueKind() != JsonValueKind.String
                        || !Signals.Contains(step["signal"]!.GetValue<string>()))
                    {
                        throw new InvalidDataException("A signal is suspend, resume or displayOn.");
                    }
                    Integer(step["at"]);
                    break;
                case [var probe] when Probes.Contains(probe):
                    if (Integer(step[probe]) < 0)
                    {
                        throw new InvalidDataException("A generation is not negative.");
                    }
                    break;
                default:
                    throw new InvalidDataException("A step is one signal, retain, release or activeFor.");
            }
        }

        if (vector.ContainsKey("expect"))
        {
            Rows(vector["expect"]);
        }

        return vector;

        static long Integer(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                && value.TryGetValue(out long number)
                ? number
                : throw new InvalidDataException($"Expected an integer: {node?.ToJsonString()}");
    }

    private static JsonObject Shape(JsonNode? node, string[] allowed, string[] required)
    {
        if (node is not JsonObject value
            || value.Any(member => !allowed.Contains(member.Key))
            || required.Any(name => !value.ContainsKey(name)))
        {
            throw new InvalidDataException($"Unexpected vector row: {node?.ToJsonString()}");
        }

        return value;
    }

    private static JsonArray Rows(JsonNode? node) =>
        node as JsonArray ?? throw new InvalidDataException("Expected an array.");

    // TryApply takes the timestamp; the clock supplies only its frequency,
    // which GetElapsedTime scales the new-resume window by.
    private sealed class QpcClock(long frequency) : TimeProvider
    {
        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() => throw new InvalidOperationException("Timestamps are scripted.");
    }
}
