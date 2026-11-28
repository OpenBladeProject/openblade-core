using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native Service reads and writes managed's thermal-convergence.json and
// derives managed's revisions. These rows pin both through the real managed
// code: each revision row runs RefreshIntent from an empty ledger and records
// the three revisions and the normalized intent; each document row loads its
// bytes through ThermalConvergenceStateStore; each transition row applies
// RefreshIntent, Reserve and Complete in order and records every state and
// whether TrySaveAsync persists it; each native row is a document the Rust
// encoder wrote, which managed must load. The Rust tests replay all four.
public sealed class NativeThermalConvergenceLedgerVectorsTests : IDisposable
{
    private const string Vectors = "thermal-convergence-ledger-vectors.json";
    private const string Regenerate =
        "OPENBLADE_REGENERATE_NATIVE_THERMAL_CONVERGENCE_LEDGER_VECTORS";
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private const string Comment =
        "Recorded by NativeThermalConvergenceLedgerVectorsTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_THERMAL_CONVERGENCE_LEDGER_VECTORS set to this directory. "
        + "A revision row runs RefreshIntent(Empty) for its intent, policy and disclosure at Battery and "
        + "records the three revisions and the normalized effective intent. A document row takes the compact "
        + "text of its document (or of its base row's), applies each patch [path, value] or removal [path] "
        + "('-' appends) and its text replacement, or uses its text, and records the text and the "
        + "ThermalConvergenceStateStore load for SID S-1-5-21-1-2-3-1001. A transition row starts from Empty and "
        + "applies refresh (the row's epoch replaces a new device epoch), reserve (candidate revisions from the "
        + "state, desired = its effective intent) and complete, recording each state and whether TrySaveAsync "
        + "persists it. A native row is a document the Rust encoder wrote; managed records its load.";

    private static readonly string[] Sections = ["revisions", "documents", "transitions", "native"];

    // ThermalConvergenceStateStore's options.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.NativeThermalConvergenceLedgerVectorsTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeThermalConvergenceLedgerVectors_MatchManagedLedger()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, Vectors)
            : Path.Combine(output, Vectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", .. Sections], root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = Comment;
        }

        Assert.Equal(Comment, (string)root["comment"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        var documents = new Dictionary<string, JsonObject>();
        foreach (JsonNode? entry in root["revisions"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Assert.True(names.Add((string)row["name"]!));
            ThermalConvergenceLedgerState state = ThermalConvergenceLedgerTransitions.RefreshIntent(
                ThermalConvergenceLedgerState.Empty(Sid),
                Read<DesiredDeviceState>(row["intent"]),
                (ThermalControlPolicy)(int)row["policy"]!,
                (ThermalPolicyUpgradeDisclosureState)(int)row["disclosure"]!,
                PowerSource.Battery,
                "OpenBlade:RZ09-0581",
                deviceAuthorityInvalidated: false);
            Record(row, "revisions", new JsonObject
            {
                ["performance"] = state.PerformanceDesiredRevision,
                ["fans"] = state.FanDesiredRevision,
                ["policy"] = state.PolicyRevision,
            });
            Record(row, "effectiveIntent", Write(state.EffectiveIntent));
        }

        foreach (JsonNode? entry in root["documents"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name));
            if (row["document"] is JsonObject document)
            {
                documents[name] = document;
            }
            string text = DocumentText(row, documents);
            Record(row, "text", text);
            Record(row, "load", await LoadAsync(name, text));
        }

        foreach (JsonNode? entry in root["transitions"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name));
            Record(row, "states", await TransitionsAsync(name, row["steps"]!.AsArray()));
        }

        foreach (JsonNode? entry in root["native"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name));
            Record(row, "load", await LoadAsync(name, (string)row["text"]!));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in Sections)
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
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

    private static string DocumentText(JsonObject row, Dictionary<string, JsonObject> documents)
    {
        if (row["text"] is JsonValue literal && row["base"] is null && row["document"] is null)
        {
            return (string)literal!;
        }

        JsonNode document = (row["document"] ?? documents[(string)row["base"]!]).DeepClone();
        foreach (JsonNode? patch in row["patch"]?.AsArray() ?? [])
        {
            JsonArray step = patch!.AsArray();
            string[] segments = ((string)step[0]!).Split('/');
            JsonNode parent = document;
            foreach (string segment in segments[..^1])
            {
                parent = parent is JsonArray array ? array[int.Parse(segment)]! : parent[segment]!;
            }

            string last = segments[^1];
            if (parent is JsonArray items)
            {
                JsonNode? value = step[1]?.DeepClone();
                if (last == "-")
                {
                    items.Add(value);
                }
                else
                {
                    items[int.Parse(last)] = value;
                }
            }
            else if (step.Count == 1)
            {
                Assert.True(parent.AsObject().Remove(last), (string)step[0]!);
            }
            else
            {
                parent[last] = step[1]?.DeepClone();
            }
        }

        string text = document.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        if (row["replace"] is JsonArray replace)
        {
            string from = (string)replace[0]!;
            Assert.Contains(from, text);
            text = text.Replace(from, (string)replace[1]!, StringComparison.Ordinal);
        }

        return text;
    }

    private async Task<JsonNode> LoadAsync(string name, string text)
    {
        string path = Path.Combine(directory, name, "thermal-convergence.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text));
        var store = new ThermalConvergenceStateStore(path, new SecurityIdentifier(Sid));
        ThermalConvergenceLoadResult loaded = await store.LoadAsync(CancellationToken.None);
        return new JsonObject
        {
            ["admitted"] = loaded.IsAuthoritative,
            ["reason"] = (int)loaded.QuarantineReason,
            ["sha256"] = loaded.Sha256,
            ["state"] = loaded.IsAuthoritative ? Write(loaded.State) : null,
        };
    }

    private async Task<JsonArray> TransitionsAsync(string name, JsonArray steps)
    {
        ThermalConvergenceLedgerState state = ThermalConvergenceLedgerState.Empty(Sid);
        var states = new JsonArray();
        int index = 0;
        foreach (JsonNode? entry in steps)
        {
            JsonObject step = entry!.AsObject();
            switch ((string)step["op"]!)
            {
                case "refresh":
                    ThermalConvergenceLedgerState refreshed = ThermalConvergenceLedgerTransitions.RefreshIntent(
                        state,
                        Read<DesiredDeviceState>(step["intent"]),
                        (ThermalControlPolicy)(int)step["policy"]!,
                        (ThermalPolicyUpgradeDisclosureState)(int)step["disclosure"]!,
                        (PowerSource?)(int?)step["destination"],
                        (string)step["fingerprint"]!,
                        (bool)step["invalidated"]!,
                        (bool)step["policyChanged"]!);
                    state = refreshed.DeviceAuthorityEpoch == state.DeviceAuthorityEpoch
                        ? refreshed
                        : refreshed with { DeviceAuthorityEpoch = Guid.Parse((string)step["epoch"]!) };
                    break;
                case "reserve":
                    state = ThermalConvergenceLedgerTransitions.Reserve(
                        state,
                        new ThermalConvergenceCandidate(
                            Sid,
                            AdmissionGeneration: 1,
                            (PowerSource)(int)step["destination"]!,
                            state.DeviceAuthorityEpoch.ToString("D"),
                            state.PerformanceDesiredRevision!,
                            state.FanDesiredRevision!,
                            (ThermalConvergenceContradiction)(int)step["contradiction"]!),
                        Guid.Parse((string)step["attempt"]!),
                        Read<ConfirmedDeviceState>(step["fresh"]),
                        state.EffectiveIntent!,
                        Components(step["affected"]!));
                    break;
                case "complete":
                    state = ThermalConvergenceLedgerTransitions.Complete(
                        state,
                        (PowerSource)(int)step["destination"]!,
                        (ThermalConvergenceContradiction)(int)step["contradiction"]!,
                        Components(step["affected"]!),
                        (ThermalTransitionDisposition)(int)step["disposition"]!);
                    break;
                default:
                    throw new InvalidDataException((string)step["op"]!);
            }

            states.Add(new JsonObject
            {
                ["state"] = Write(state),
                ["persistable"] = await PersistableAsync($"{name}-{index++}", state),
                ["selfExpected"] = ThermalConvergenceLedgerTransitions.IsExpectedReservation(state, state),
            });
        }

        return states;
    }

    private async Task<bool> PersistableAsync(string name, ThermalConvergenceLedgerState state)
    {
        string path = Path.Combine(directory, name, "thermal-convergence.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new ThermalConvergenceStateStore(path, new SecurityIdentifier(Sid));
        try
        {
            return (await store.TrySaveAsync(null, state, CancellationToken.None)).Applied;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static ThermalComponent[] Components(JsonNode node) =>
        [.. node.AsArray().Select(component => (ThermalComponent)(int)component!)];

    private static T Read<T>(JsonNode? node) =>
        node.Deserialize<T>(Options) ?? throw new InvalidDataException(typeof(T).Name);

    private static JsonNode? Write<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
}
