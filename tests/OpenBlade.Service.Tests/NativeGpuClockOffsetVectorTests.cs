using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's NVML clock-offset controller is pinned against managed.
// Each row drives NvidiaGpuClockOffsetController.Query or Set over a scripted NVML
// and records its result, the NVML call trace and the driver's offsets afterwards.
public sealed class NativeGpuClockOffsetVectorTests
{
    private const string Vectors = "gpu-clock-offset-vectors.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_GPU_CLOCK_OFFSET_VECTORS";

    private const string Comment =
        "Recorded by NativeGpuClockOffsetVectorTests; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_GPU_CLOCK_OFFSET_VECTORS set to this directory. "
        + "A row drives NvidiaGpuClockOffsetController.Query or Set over a scripted NVML. "
        + "The driver has a device count, a name and [minimum, maximum, current] core and "
        + "memory offsets in NVML units (memory doubled). A fault names a call (init, count, "
        + "handle, name, get or set) and its 1-based occurrence: it returns status, a get "
        + "reports current instead of the stored value, and a set stores the requested value "
        + "(store true or absent), nothing (false) or the given number. Every unfaulted call "
        + "succeeds. Each expect is the result (the capabilities in editable units, null, or "
        + "the write's failure and message), the calls in order and the driver's core and "
        + "memory offsets afterwards.";

    [Fact]
    public void NativeGpuClockOffsetVectors_MatchManagedController()
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, Vectors)
            : Path.Combine(output, Vectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["comment", "controller"], root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = Comment;
        }

        Assert.Equal(Comment, (string)root["comment"]!);
        JsonArray rows = root["controller"]!.AsArray();
        Assert.NotEmpty(rows);
        var failures = new List<string>();
        foreach (JsonNode? entry in rows)
        {
            JsonObject row = entry!.AsObject();
            JsonObject observed = Replay(row);
            if (!string.IsNullOrEmpty(output))
            {
                row["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(row["expect"], observed))
            {
                failures.Add($"{row["name"]}: {observed.ToJsonString()}");
            }
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString())
                .Append(",\n\"controller\":[\n")
                .AppendJoin(",\n", rows.Select(row => row!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);
    }

    private static JsonObject Replay(JsonObject row)
    {
        var api = new ScriptedNvml(row);
        var controller = new NvidiaGpuClockOffsetController(api);
        JsonNode operation = row["operation"]!;
        JsonNode? result;
        if (operation.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            Assert.Equal("query", (string)operation!);
            result = Capabilities(controller.Query());
        }
        else
        {
            JsonArray set = operation["set"]!.AsArray();
            try
            {
                result = new JsonObject
                {
                    ["confirmed"] = Capabilities(controller.Set(
                        new GpuClockOffsetSettings((int)set[0]!, (int)set[1]!))),
                };
            }
            catch (BladeWriteException exception)
            {
                result = new JsonObject
                {
                    ["failure"] = exception.SubmissionState.ToString(),
                    ["message"] = exception.Message,
                };
            }
        }

        return new JsonObject
        {
            ["result"] = result,
            ["calls"] = new JsonArray(api.Calls.Select(call => (JsonNode?)call).ToArray()),
            ["driver"] = new JsonArray(api.Core, api.Memory),
        };
    }

    private static JsonObject? Capabilities(GpuClockOffsetCapabilities? capabilities) =>
        capabilities is null
            ? null
            : new JsonObject
            {
                ["name"] = capabilities.DeviceName,
                ["core"] = Range(capabilities.Core),
                ["memory"] = Range(capabilities.Memory),
            };

    private static JsonArray Range(GpuClockOffsetRange range) =>
        new(range.MinimumMegahertz, range.MaximumMegahertz, range.CurrentMegahertz);

    private sealed class ScriptedNvml : INvidiaNvmlClockOffsetApi
    {
        private readonly JsonObject driver;
        private readonly JsonObject[] faults;
        private readonly Dictionary<string, int> occurrences = [];

        internal ScriptedNvml(JsonObject row)
        {
            driver = row["driver"]!.AsObject();
            faults = row["faults"]!.AsArray().Select(fault => fault!.AsObject()).ToArray();
            Core = (int)driver["core"]![2]!;
            Memory = (int)driver["memory"]![2]!;
        }

        internal List<string> Calls { get; } = [];

        internal int Core { get; private set; }

        internal int Memory { get; private set; }

        public int Initialize()
        {
            Calls.Add("init");
            return Status(Fault("init"));
        }

        public int Shutdown()
        {
            Calls.Add("shutdown");
            return 0;
        }

        public int GetDeviceCount(out uint count)
        {
            Calls.Add("count");
            count = (uint)(int)driver["count"]!;
            return Status(Fault("count"));
        }

        public int GetDeviceHandleByIndex(uint index, out nint device)
        {
            Calls.Add($"handle {index}");
            device = 1;
            return Status(Fault("handle"));
        }

        public int GetDeviceName(nint device, StringBuilder name, uint length)
        {
            Calls.Add("name");
            int status = Status(Fault("name"));
            if (status == 0)
            {
                name.Append((string)driver["name"]!);
            }

            return status;
        }

        public int GetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            Calls.Add($"get {(int)info.Type}");
            JsonObject? fault = Fault("get");
            int status = Status(fault);
            if (status != 0)
            {
                return status;
            }

            JsonArray range = driver[info.Type == NvmlClockType.Graphics ? "core" : "memory"]!.AsArray();
            info.MinimumMegahertz = (int)range[0]!;
            info.MaximumMegahertz = (int)range[1]!;
            info.CurrentMegahertz = fault?["current"] is { } current
                ? (int)current
                : info.Type == NvmlClockType.Graphics ? Core : Memory;
            return 0;
        }

        public int SetClockOffsets(nint device, ref NvmlClockOffset info)
        {
            Calls.Add($"set {(int)info.Type} {info.CurrentMegahertz}");
            JsonObject? fault = Fault("set");
            int? stored = fault?["store"] switch
            {
                null => info.CurrentMegahertz,
                JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.True =>
                    info.CurrentMegahertz,
                JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.False => null,
                JsonNode value => (int)value,
            };
            if (stored is { } offset)
            {
                if (info.Type == NvmlClockType.Graphics)
                {
                    Core = offset;
                }
                else
                {
                    Memory = offset;
                }
            }

            return Status(fault);
        }

        private JsonObject? Fault(string call)
        {
            int occurrence = occurrences[call] = occurrences.GetValueOrDefault(call) + 1;
            return faults.FirstOrDefault(fault =>
                (string)fault["call"]! == call && (int)fault["index"]! == occurrence);
        }

        private static int Status(JsonObject? fault) =>
            fault?["status"] is { } status ? (int)status : 0;
    }
}
