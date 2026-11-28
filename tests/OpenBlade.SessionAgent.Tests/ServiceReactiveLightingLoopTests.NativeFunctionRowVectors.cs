using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

// The native Session Agent's Windows-hook function row is pinned against
// managed. Each case replays FunctionRowKeyboardHookProcessor, as the managed
// low-level hook calls it, and FunctionRowHookRecovery, as the Raw Input path
// calls it, over one RazerBladeFunctionRowInputState. Each status row drives
// the real ServiceReactiveLightingLoop and records the device, Driver-mode
// knowledge and primary mode its hook reads, beside the status members as the
// IPC codec writes them. The Rust domain and Agent tests read the same file and
// name every row where native deliberately differs.
public sealed partial class ServiceReactiveLightingLoopTests
{
    private const string NativeFunctionRowVectors = "function-row-hook-vectors.json";
    private const string RegenerateNativeFunctionRowVectors =
        "OPENBLADE_REGENERATE_NATIVE_FUNCTION_ROW_VECTORS";

    private const string NativeFunctionRowComment =
        "Recorded by ServiceReactiveLightingLoopTests.NativeFunctionRowVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_FUNCTION_ROW_VECTORS set to this directory. "
        + "A case step is a hook edge (key) or a Raw Input edge the hook missed (missed); "
        + "its result is the hook's suppression or the recovery's reinstall. "
        + "Step members after the edge change the case state from that step on. "
        + "A preference row is a functionRowBackend value and the backend UiPreferenceStore reads from it.";

    private static readonly string[] NativeStatusMembers =
        ["deviceVerified", "firmware", "keyboardDriverModeActive", "functionKeyPrimary"];

    [Fact]
    public async Task NativeFunctionRowVectors_MatchManagedHookAndStatus()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeFunctionRowVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeFunctionRowVectors)
            : Path.Combine(output, NativeFunctionRowVectors);
        JsonObject expected = NativeFunctionRowRows();
        JsonObject root = string.IsNullOrEmpty(output)
            ? ReadNativeFunctionRowVectors(File.ReadAllText(path))
            : expected;

        // The file holds exactly the reviewed rows: a dropped or edited input
        // row would otherwise weaken the Rust side silently.
        Assert.True(JsonNode.DeepEquals(InputsOnly(root), InputsOnly(expected)));
        foreach (JsonNode? entry in root["malformed"]!.AsArray())
        {
            JsonObject malformed = entry!.AsObject();
            JsonNode row = malformed["row"]!;
            Assert.Throws<InvalidDataException>(() => ((string)malformed["section"]!) switch
            {
                "cases" => NativeFunctionRowCase(row),
                "status" => NativeFunctionRowStatusRow(row),
                "preference" => NativeFunctionRowPreferenceRow(row),
                _ => throw new InvalidOperationException("Unknown malformed section."),
            });
        }

        var failures = new List<string>();
        foreach (JsonNode? entry in root["cases"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            Record(vector, "expect", ReplayNativeFunctionRowCase(vector));
        }

        foreach (JsonNode? entry in root["status"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            ServiceStatusSnapshot status = NativeFunctionRowStatus(vector["set"]!.AsObject());
            Record(vector, "wire", await NativeFunctionRowWireAsync(status));
            Record(vector, "expect", await ObserveNativeFunctionRowStatusAsync(status));
        }

        foreach (JsonNode? entry in root["preference"]!.AsArray())
        {
            JsonObject vector = entry!.AsObject();
            Record(vector, "expect", await ReadNativeFunctionRowPreferenceAsync(vector["value"]));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(root["comment"]!.ToJsonString());
            foreach (string section in (string[])["cases", "status", "preference", "malformed"])
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Record(JsonObject vector, string member, JsonNode observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                vector[member] = observed;
            }
            else if (!JsonNode.DeepEquals(vector[member], observed))
            {
                failures.Add($"{vector["name"]} {member}: {observed.ToJsonString()}");
            }
        }

        static JsonObject InputsOnly(JsonObject root)
        {
            var inputs = (JsonObject)root.DeepClone();
            inputs.Remove("comment");
            foreach (string section in (string[])["cases", "status", "preference"])
            {
                foreach (JsonNode? row in inputs[section]!.AsArray())
                {
                    row!.AsObject().Remove("expect");
                    row.AsObject().Remove("wire");
                }
            }

            return inputs;
        }
    }

    [Theory]
    [InlineData("""{"comment":"","cases":[],"status":[],"malformed":[]}""")]
    [InlineData("""{"comment":"","cases":[],"status":[],"preference":[],"malformed":[],"extra":1}""")]
    [InlineData("""{"comment":"","cases":{},"status":[],"preference":[],"malformed":[]}""")]
    [InlineData("""{"comment":"","cases":[],"status":[],"preference":{},"malformed":[]}""")]
    [InlineData("""{"comment":"","cases":[],"status":[],"preference":[],"malformed":[{"section":"cases"}]}""")]
    [InlineData("""{"comment":"","cases":[],"status":[],"preference":[],"malformed":[{"section":"other","row":{}}]}""")]
    public void NativeFunctionRowVectors_RejectMalformedFiles(string text) =>
        Assert.Throws<InvalidDataException>(() => ReadNativeFunctionRowVectors(text));

    // The file shape, checked before any row runs. Each malformed row in the
    // file is one this reader and the Rust readers must both refuse.
    private static JsonObject ReadNativeFunctionRowVectors(string text)
    {
        string[] sections = ["comment", "cases", "status", "preference", "malformed"];
        JsonObject root = NativeShape(JsonNode.Parse(text), sections, sections);
        if (root["comment"]?.GetValueKind() != JsonValueKind.String)
        {
            throw new InvalidDataException("The comment is a string.");
        }

        foreach (JsonNode? row in NativeRows(root["cases"]))
        {
            NativeFunctionRowCase(row);
        }

        foreach (JsonNode? row in NativeRows(root["status"]))
        {
            NativeFunctionRowStatusRow(row);
        }

        foreach (JsonNode? row in NativeRows(root["preference"]))
        {
            NativeFunctionRowPreferenceRow(row);
        }

        foreach (JsonNode? row in NativeRows(root["malformed"]))
        {
            JsonObject malformed = NativeShape(row, ["section", "row"], ["section", "row"]);
            if (malformed["section"]?.GetValueKind() != JsonValueKind.String
                || (string)malformed["section"]! is not ("cases" or "status" or "preference"))
            {
                throw new InvalidDataException("A malformed row names its section.");
            }
        }

        return root;
    }

    private static JsonObject NativeFunctionRowCase(JsonNode? row)
    {
        JsonObject vector = NativeShape(
            row,
            ["name", "device", "backend", "driverMode", "fn", "primary", "steps", "expect"],
            ["name", "device", "backend", "driverMode", "fn", "primary", "steps"]);
        NativeString(vector["name"]);
        NativeName(vector["device"], ["Rz09_0581", "Rz09_0528", "Rz09_0484", "Unsupported"]);
        NativeState(vector);
        JsonArray steps = NativeRows(vector["steps"]);
        foreach (JsonNode? entry in steps)
        {
            JsonObject step = NativeShape(
                entry,
                ["key", "missed", "down", "wasDown", "backend", "driverMode", "fn", "primary"],
                ["down"]);
            bool hook = step.ContainsKey("key");
            if (hook == step.ContainsKey("missed") || hook != step.ContainsKey("wasDown"))
            {
                throw new InvalidDataException("A step is one hook edge with wasDown or one missed edge.");
            }

            NativeScanCode(step[hook ? "key" : "missed"]);
            NativeBoolean(step["down"]);
            if (hook)
            {
                NativeBoolean(step["wasDown"]);
            }

            NativeState(step);
        }

        if (vector.ContainsKey("expect"))
        {
            JsonArray expect = NativeRows(vector["expect"]);
            if (expect.Count != steps.Count)
            {
                throw new InvalidDataException("One outcome per step.");
            }

            foreach (JsonNode? entry in expect)
            {
                JsonObject outcome = NativeShape(entry, ["result", "events"], ["result", "events"]);
                NativeBoolean(outcome["result"]);
                foreach (JsonNode? inputEvent in NativeRows(outcome["events"]))
                {
                    string[] parts = NativeString(inputEvent).Split(':');
                    if (parts.Length != 2
                        || !Enum.TryParse(parts[0], ignoreCase: false, out FunctionRowAction _)
                        || !Enum.TryParse(parts[1], ignoreCase: false, out FunctionRowInputPhase _)
                        || int.TryParse(parts[0], out _)
                        || int.TryParse(parts[1], out _))
                    {
                        throw new InvalidDataException("An event is Action:Phase.");
                    }
                }
            }
        }

        return vector;
    }

    private static JsonObject NativeFunctionRowStatusRow(JsonNode? row)
    {
        JsonObject vector = NativeShape(row, ["name", "set", "wire", "expect"], ["name", "set"]);
        NativeString(vector["name"]);
        JsonObject set = NativeShape(
            vector["set"],
            ["deviceVerified", "model", "keyboardDriverModeActive", "functionKeyPrimary"],
            ["deviceVerified", "model", "keyboardDriverModeActive", "functionKeyPrimary"]);
        NativeBoolean(set["deviceVerified"]);
        if (set["model"] is not null)
        {
            NativeString(set["model"]);
        }

        if (set["keyboardDriverModeActive"] is not null)
        {
            NativeBoolean(set["keyboardDriverModeActive"]);
        }

        NativeOptionalName(set["functionKeyPrimary"], ["FunctionKeys", "MultimediaKeys"]);
        if (vector.ContainsKey("wire"))
        {
            NativeShape(vector["wire"], NativeStatusMembers, ["deviceVerified"]);
        }

        if (vector.ContainsKey("expect"))
        {
            JsonObject expect = NativeShape(
                vector["expect"],
                ["device", "driverMode", "primary"],
                ["device", "driverMode", "primary"]);
            NativeName(expect["device"], Enum.GetNames<RazerBladeInputDeviceKind>());
            NativeName(expect["driverMode"], ["Unknown", "Inactive", "Active"]);
            NativeOptionalName(expect["primary"], ["FunctionKeys", "MultimediaKeys"]);
        }

        return vector;
    }

    private static JsonObject NativeFunctionRowPreferenceRow(JsonNode? row)
    {
        JsonObject vector = NativeShape(row, ["name", "value", "expect"], ["name", "value"]);
        NativeString(vector["name"]);
        if (vector.ContainsKey("expect"))
        {
            NativeName(vector["expect"], ["WindowsHook", "RazerDriver", "Unknown", "Unreadable"]);
        }

        return vector;
    }

    // Every state member is present on a case and optional on a step.
    private static void NativeState(JsonObject row)
    {
        if (row.ContainsKey("backend"))
        {
            NativeName(row["backend"], ["WindowsHook", "RazerDriver", "Unknown"]);
        }

        if (row.ContainsKey("driverMode"))
        {
            NativeName(row["driverMode"], ["Unknown", "Inactive", "Active"]);
        }

        if (row.ContainsKey("fn") && row["fn"] is not null)
        {
            NativeBoolean(row["fn"]);
        }

        if (row.ContainsKey("primary"))
        {
            NativeOptionalName(row["primary"], ["FunctionKeys", "MultimediaKeys"]);
        }
    }

    private static JsonObject NativeShape(JsonNode? node, string[] allowed, string[] required)
    {
        if (node is not JsonObject value
            || value.Any(member => !allowed.Contains(member.Key))
            || required.Any(name => !value.ContainsKey(name)))
        {
            throw new InvalidDataException($"Unexpected vector row: {node?.ToJsonString()}");
        }

        return value;
    }

    private static JsonArray NativeRows(JsonNode? node) =>
        node as JsonArray ?? throw new InvalidDataException("Expected an array.");

    private static string NativeString(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String
            ? (string)node!
            : throw new InvalidDataException("Expected a string.");

    private static void NativeBoolean(JsonNode? node)
    {
        if (node?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("Expected true or false.");
        }
    }

    private static void NativeName(JsonNode? node, string[] names)
    {
        if (!names.Contains(NativeString(node)))
        {
            throw new InvalidDataException($"Unexpected name: {node!.ToJsonString()}");
        }
    }

    private static void NativeOptionalName(JsonNode? node, string[] names)
    {
        if (node is not null)
        {
            NativeName(node, names);
        }
    }

    private static void NativeScanCode(JsonNode? node)
    {
        if (node?.GetValueKind() != JsonValueKind.Number
            || !node.AsValue().TryGetValue(out int scanCode)
            || scanCode is < 0 or > 0xFFFF)
        {
            throw new InvalidDataException("A scan code is 0 to 65535.");
        }
    }

    // The reviewed rows, without their recorded outcomes.
    private static JsonObject NativeFunctionRowRows()
    {
        var cases = new JsonArray();
        JsonArray Press(int key) => [Hook(key, true, false), Hook(key, true, true), Hook(key, false, false)];

        // Every Driver-mode knowledge, backend, Fn state and primary mode for a
        // fresh press, an auto-repeat and the release of a repeating key.
        foreach (string driverMode in (string[])["Unknown", "Inactive", "Active"])
        {
            foreach (string backend in (string[])["WindowsHook", "RazerDriver", "Unknown"])
            {
                foreach (bool fn in (bool[])[false, true])
                {
                    foreach (string? primary in (string?[])["MultimediaKeys", "FunctionKeys", null])
                    {
                        cases.Add(Case(
                            $"decision/{driverMode}/{backend}/fn-{(fn ? "held" : "released")}/{primary ?? "unknown"}",
                            "Rz09_0581", backend, driverMode, fn, primary, Press(0x3C)));
                    }
                }
            }
        }

        foreach (string device in (string[])["Rz09_0528", "Rz09_0484", "Unsupported"])
        {
            cases.Add(Case($"device/{device}", device, "WindowsHook", "Active", false, "MultimediaKeys", Press(0x3C)));
        }

        // Every function-row key and one outside it: one-shot actions ignore
        // auto-repeat; volume and brightness repeat.
        var keys = new JsonArray();
        foreach (int key in (int[])[0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58, 0x01])
        {
            foreach (JsonNode? step in Press(key))
            {
                keys.Add(step!.DeepClone());
            }
        }

        cases.Add(Case("keys/all", "Rz09_0581", "WindowsHook", "Active", false, "MultimediaKeys", keys));
        // Managed reads Fn as released until a Raw Input report says otherwise.
        cases.Add(Case("fn/unknown", "Rz09_0581", "WindowsHook", "Active", null, "MultimediaKeys", Press(0x3C)));
        cases.Add(Case("fn/unknown-function-keys", "Rz09_0581", "WindowsHook", "Active", null, "FunctionKeys", Press(0x3C)));

        cases.Add(Admitted("sequence/stale-release",
            Hook(0x3C, true, false), Hook(0x3C, true, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/started-mid-press",
            Hook(0x3C, true, true), Hook(0x3C, true, true), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/latched-through-fn-change",
            Hook(0x3C, true, false), Hook(0x3C, true, true, ("fn", true)), Hook(0x3C, false, false),
            Hook(0x3C, true, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/latched-through-driver-mode-loss",
            Hook(0x3C, true, false), Hook(0x3C, true, true, ("driverMode", "Unknown")), Hook(0x3C, false, false),
            Hook(0x3C, true, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/latched-through-backend-change",
            Hook(0x3C, true, false), Hook(0x3C, true, true, ("backend", "RazerDriver")), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/passed-press-stays-passed",
            Hook(0x3C, true, false, ("driverMode", "Inactive")), Hook(0x3C, true, true, ("driverMode", "Active")),
            Hook(0x3C, false, false), Hook(0x3C, true, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("sequence/two-keys-interleaved",
            Hook(0x3C, true, false), Hook(0x3D, true, false), Hook(0x3C, false, false), Hook(0x3D, false, false)));

        cases.Add(Admitted("missed/release-of-suppressed-press",
            Hook(0x3C, true, false), Missed(0x3C, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("missed/repeat-of-suppressed-press",
            Hook(0x3C, true, false), Missed(0x3C, true), Hook(0x3C, false, false)));
        cases.Add(Admitted("missed/press-then-hook-repeat",
            Missed(0x3C, true), Hook(0x3C, true, true), Hook(0x3C, false, false), Hook(0x3C, true, false)));
        cases.Add(Admitted("missed/release-of-passed-press",
            Hook(0x3C, true, false, ("driverMode", "Inactive")), Missed(0x3C, false), Hook(0x3C, false, false)));
        cases.Add(Admitted("missed/outside-the-row", Missed(0x01, true), Missed(0x01, false)));
        cases.Add(Case("missed/driver-backend-idle", "Rz09_0581", "RazerDriver", "Active", false, "MultimediaKeys",
            [Missed(0x3C, true), Missed(0x3C, false)]));
        cases.Add(Admitted("missed/driver-backend-held",
            Hook(0x3C, true, false), Missed(0x3C, false, ("backend", "RazerDriver")), Missed(0x3D, true)));
        cases.Add(Case("missed/unsupported-device", "Rz09_0484", "WindowsHook", "Active", false, "MultimediaKeys",
            [Missed(0x3C, true), Missed(0x3C, false)]));

        var status = new JsonArray();
        foreach (bool? active in (bool?[])[true, false, null])
        {
            status.Add(Status($"driver-mode/{Name(active)}", true, "RZ09-0581", active, "MultimediaKeys"));
        }

        status.Add(Status("primary/function-keys", true, "RZ09-0581", true, "FunctionKeys"));
        // Managed falls back to the active profile's laptop behavior.
        status.Add(Status("primary/absent", true, "RZ09-0581", true, null));
        status.Add(Status("unverified", false, "RZ09-0581", true, "MultimediaKeys"));
        foreach (string model in (string[])["RZ09-0528", "RZ09-0484", "RZ09-0530", "rz09-0581"])
        {
            status.Add(Status($"model/{model}", true, model, true, "MultimediaKeys"));
        }

        status.Add(Status("model/absent", true, null, true, "MultimediaKeys"));

        JsonObject valid = Case("x", "Rz09_0581", "WindowsHook", "Active", false, "MultimediaKeys", [Hook(0x3C, true, false)]);
        JsonObject validStatus = Status("x", true, "RZ09-0581", true, "MultimediaKeys");
        var validPreference = new JsonObject { ["name"] = "x", ["value"] = "WindowsHook" };
        var malformed = new JsonArray
        {
            Malformed("cases", valid, row => row["extra"] = 1),
            Malformed("cases", valid, row => row.Remove("fn")),
            Malformed("cases", valid, row => row["driverMode"] = "On"),
            Malformed("cases", valid, row => row["driverMode"] = 2),
            Malformed("cases", valid, row => row["fn"] = "false"),
            Malformed("cases", valid, row => row["primary"] = 1),
            Malformed("cases", valid, row => row["backend"] = "windowshook"),
            Malformed("cases", valid, row => row["device"] = "RZ09-0581"),
            Malformed("cases", valid, row => row["steps"] = new JsonObject()),
            Malformed("cases", valid, row => row["steps"]![0]!.AsObject().Remove("wasDown")),
            Malformed("cases", valid, row => row["steps"]![0]!["missed"] = 0x3C),
            Malformed("cases", valid, row => row["steps"]![0]!["key"] = -1),
            Malformed("cases", valid, row => row["steps"]![0]!["key"] = 65536),
            Malformed("cases", valid, row => row["steps"]![0]!["key"] = "60"),
            Malformed("cases", valid, row => row["steps"]![0]!["down"] = 1),
            Malformed("cases", valid, row => row["steps"]![0]!["held"] = true),
            Malformed("cases", valid, row => row["expect"] = new JsonArray()),
            Malformed("cases", valid, row => row["expect"] = new JsonArray(Outcome(true, "VolumeDown:Down", "x"))),
            Malformed("cases", valid, row => row["expect"] = new JsonArray(Outcome(true, "VolumeDown:Pressed"))),
            Malformed("cases", valid, row => row["expect"] = new JsonArray(Outcome(true, "1:0"))),
            Malformed("status", validStatus, row => row["extra"] = 1),
            Malformed("status", validStatus, row => row["set"]!.AsObject().Remove("model")),
            Malformed("status", validStatus, row => row["set"]!["keyboardDriverModeActive"] = "true"),
            Malformed("status", validStatus, row => row["set"]!["functionKeyPrimary"] = 1),
            Malformed("status", validStatus, row => row["set"]!["deviceVerified"] = null),
            Malformed("status", validStatus, row => row["wire"] = new JsonObject { ["other"] = 1, ["deviceVerified"] = true }),
            Malformed("status", validStatus, row => row["expect"] = new JsonObject { ["device"] = "Rz09_0581", ["driverMode"] = "On", ["primary"] = null }),
            Malformed("preference", validPreference, row => row["extra"] = 1),
            Malformed("preference", validPreference, row => row.Remove("value")),
            Malformed("preference", validPreference, row => row["name"] = 1),
            Malformed("preference", validPreference, row => row["expect"] = "windowshook"),
            Malformed("preference", validPreference, row => row["expect"] = 1),
        };

        // The functionRowBackend values Enum.TryParse and the converter decide:
        // names in any case, numbers with a sign and white space, comma-joined
        // names, and the comma, white-space and number forms that fail.
        JsonNode?[] values =
        [
            "WindowsHook", "windowshook", "WINDOWSHOOK", "RazerDriver", "Unknown",
            "1", "2", "0", "3", "-1", "+1", "01", "-0", "1.0", "0x1", "1e0", "2147483648", "99999999999",
            " 1", "1 ", "\t1", "1\t", "\u00a01", "1\u00a0", "\u20001", "+ 1", "1 1",
            "1\u0000", "1 \u0000", "1\u0000\u0000", "1\u0000 ", "1 \u0000 \u0000", "WindowsHook\u0000",
            " WindowsHook ", "\u00a0WindowsHook", "WindowsHook\u00a0", "Windows Hook",
            "1,1", "WindowsHook,1", "1,WindowsHook", "0,1", "RazerDriver,0", "1,2",
            "WindowsHook,Unknown", "WindowsHook,WindowsHook", "WindowsHook , Unknown",
            "WindowsHook,RazerDriver", "WindowsHook,", ",WindowsHook", "WindowsHook,,Unknown",
            "", " ", ",", "W\u0131ndowsHook", "Window\u017fHook", "WindowsHoo\u212a",
            1, 2, 0, 3, -1, JsonNode.Parse("1.0"), JsonNode.Parse("1e0"), 2147483648, null, true, new JsonArray(), new JsonObject(),
        ];
        var preference = new JsonArray(
        [
            .. values.Select(value => (JsonNode?)new JsonObject
            {
                ["name"] = value?.ToJsonString() ?? "null",
                ["value"] = value,
            }),
        ]);

        return new JsonObject
        {
            ["comment"] = NativeFunctionRowComment,
            ["cases"] = cases,
            ["status"] = status,
            ["preference"] = preference,
            ["malformed"] = malformed,
        };

        static JsonObject Case(
            string name, string device, string backend, string driverMode, bool? fn, string? primary, JsonArray steps)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["device"] = device,
                ["backend"] = backend,
                ["driverMode"] = driverMode,
                ["fn"] = fn,
                ["primary"] = primary,
                ["steps"] = steps,
            };
        }

        static JsonObject Admitted(string name, params JsonObject[] steps) =>
            Case(name, "Rz09_0581", "WindowsHook", "Active", false, "MultimediaKeys", [.. steps]);

        static JsonObject Hook(int key, bool down, bool wasDown, params (string Name, JsonNode? Value)[] set) =>
            With(new JsonObject { ["key"] = key, ["down"] = down, ["wasDown"] = wasDown }, set);

        static JsonObject Missed(int key, bool down, params (string Name, JsonNode? Value)[] set) =>
            With(new JsonObject { ["missed"] = key, ["down"] = down }, set);

        static JsonObject With(JsonObject step, (string Name, JsonNode? Value)[] set)
        {
            foreach ((string name, JsonNode? value) in set)
            {
                step[name] = value;
            }

            return step;
        }

        static JsonObject Status(string name, bool verified, string? model, bool? active, string? primary) => new()
        {
            ["name"] = name,
            ["set"] = new JsonObject
            {
                ["deviceVerified"] = verified,
                ["model"] = model,
                ["keyboardDriverModeActive"] = active,
                ["functionKeyPrimary"] = primary,
            },
        };

        static JsonObject Outcome(bool result, params string[] events) => new()
        {
            ["result"] = result,
            ["events"] = new JsonArray([.. events.Select(inputEvent => (JsonNode?)inputEvent)]),
        };

        static JsonObject Malformed(string section, JsonObject valid, Action<JsonObject> edit)
        {
            var row = (JsonObject)valid.DeepClone();
            edit(row);
            return new JsonObject { ["section"] = section, ["row"] = row };
        }

        static string Name(bool? value) => value switch
        {
            true => "active",
            false => "inactive",
            null => "unknown",
        };
    }

    // UiPreferenceStore.LoadInputPreferencesAsync over a file holding only this
    // member; a JsonException is a failed read.
    private static async Task<JsonNode> ReadNativeFunctionRowPreferenceAsync(JsonNode? value)
    {
        string path = Path.Combine(Path.GetTempPath(), $"openblade-native-preference-{Guid.NewGuid():N}.json");
        try
        {
            var content = new JsonObject { ["functionRowBackend"] = value?.DeepClone() };
            await File.WriteAllTextAsync(path, content.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                UiInputPreferences preferences = await new UiPreferenceStore(path).LoadInputPreferencesAsync();
                return preferences.FunctionRowBackend.ToString();
            }
            catch (JsonException)
            {
                return "Unreadable";
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonArray ReplayNativeFunctionRowCase(JsonObject vector)
    {
        var state = new RazerBladeFunctionRowInputState();
        RazerBladeInputDeviceKind device = Enum.Parse<RazerBladeInputDeviceKind>((string)vector["device"]!);
        var current = new JsonObject();
        foreach (string member in (string[])["backend", "driverMode", "fn", "primary"])
        {
            current[member] = vector[member]?.DeepClone();
        }

        var observed = new JsonArray();
        foreach (JsonNode? entry in vector["steps"]!.AsArray())
        {
            JsonObject step = entry!.AsObject();
            foreach (string member in (string[])["backend", "driverMode", "fn", "primary"])
            {
                if (step.ContainsKey(member))
                {
                    current[member] = step[member]?.DeepClone();
                }
            }

            KeyboardInputBackend backend = Enum.Parse<KeyboardInputBackend>((string)current["backend"]!);
            var driverMode = new KeyboardDriverModeObservation(
                Enum.Parse<KeyboardDriverModeKnowledge>((string)current["driverMode"]!), 0);
            FunctionKeyPrimaryMode? primary = current["primary"] is null
                ? null
                : Enum.Parse<FunctionKeyPrimaryMode>((string)current["primary"]!);
            bool down = (bool)step["down"]!;
            var events = new List<FunctionRowInputEvent>();
            bool result;
            if (step.ContainsKey("key"))
            {
                // WindowsReactiveKeyboardSource's hook: Fn is the last Raw Input
                // observation, which starts released.
                result = FunctionRowKeyboardHookProcessor.Process(
                    state,
                    device,
                    (uint)(int)step["key"]!,
                    down,
                    (bool)step["wasDown"]!,
                    (bool?)current["fn"] ?? false,
                    primary,
                    FunctionRowKeyboardHookProcessor.ShouldAdmitNewPress(backend, driverMode),
                    events.Add);
            }
            else
            {
                // WindowsReactiveKeyboardSource's Raw Input path recovers while
                // the hook backend is selected or a hook press is still held.
                FunctionRowHookRecoveryResult recovery =
                    backend != KeyboardInputBackend.WindowsHook && !state.HasActiveKeys
                        ? default
                        : FunctionRowHookRecovery.EvaluateTargetPacket(
                            state, device, observedByHook: false, (uint)(int)step["missed"]!, down);
                result = recovery.ReinstallHook;
                if (recovery.ReinstallHook && recovery.ReleaseEvent is { } release)
                {
                    events.Add(release);
                }
            }

            observed.Add(new JsonObject
            {
                ["result"] = result,
                ["events"] = new JsonArray(
                    [.. events.Select(inputEvent => (JsonNode?)$"{inputEvent.Action}:{inputEvent.Phase}")]),
            });
        }

        return observed;
    }

    private static ServiceStatusSnapshot NativeFunctionRowStatus(JsonObject set) =>
        Status(LightingRendererOwnershipState.Static) with
        {
            ServiceVersion = "test",
            DeviceVerified = (bool)set["deviceVerified"]!,
            Firmware = set["model"] is null
                ? null
                : new FirmwareVersions("4.00", "1", "1", "1", (string)set["model"]!),
            KeyboardDriverModeActive = (bool?)set["keyboardDriverModeActive"],
            FunctionKeyPrimary = set["functionKeyPrimary"] is null
                ? null
                : Enum.Parse<FunctionKeyPrimaryMode>((string)set["functionKeyPrimary"]!),
        };

    // The status members the native Agent reads, as the Service writes them.
    private static async Task<JsonObject> NativeFunctionRowWireAsync(ServiceStatusSnapshot status)
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(
            stream, new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        JsonObject written = JsonNode.Parse(stream.ToArray().AsSpan(4))!["status"]!.AsObject();
        var wire = new JsonObject();
        foreach (string member in NativeStatusMembers)
        {
            if (written.TryGetPropertyValue(member, out JsonNode? value))
            {
                wire[member] = value?.DeepClone();
            }
        }

        return wire;
    }

    private static async Task<JsonObject> ObserveNativeFunctionRowStatusAsync(ServiceStatusSnapshot status)
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        client.Publish(status);
        await WaitUntilAsync(() => client.DeliveryCount == 1);
        RecordingSource source = factory.Last!;
        var observed = new JsonObject
        {
            ["device"] = source.ReadVerifiedInputDeviceKind().ToString(),
            ["driverMode"] = source.ReadDriverModeObservation().Knowledge.ToString(),
            ["primary"] = source.ReadFunctionKeyPrimaryMode()?.ToString(),
        };
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        return observed;
    }
}
