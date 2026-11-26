using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native Service serves setDisplayBrightnessHidUsage on exact RZ09-0581 as
// managed's runtime serves it through DisplayBrightnessHidController. These
// rows pin what managed replies, and which usages reach the transport, for the
// same transport results. The Rust tests replay both sections.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeDisplayBrightnessHidVectors = "display-brightness-hid-vectors.json";
    private const string RegenerateNativeDisplayBrightnessHidVectors =
        "OPENBLADE_REGENERATE_NATIVE_DISPLAY_BRIGHTNESS_HID_VECTORS";

    private const string NativeDisplayBrightnessHidComment =
        "Recorded by ServiceControlRuntimeTests.NativeDisplayBrightnessHidVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_DISPLAY_BRIGHTNESS_HID_VECTORS set to this directory. ioctl "
        + "and buffers are WindowsRazerDisplayBrightnessHidTransport.SubmitInputIoctl and "
        + "CreateControlBuffer (lowercase hex) for each defined usage. A sequence row composes "
        + "DisplayBrightnessHidController over a transport that returns the row's writes in order "
        + "(Applied once they run out), creates the runtime with the device provider available "
        + "when verified is true and observes once, or leaves it unobserved and unavailable "
        + "otherwise, then runs each request and records its status (ControlOperationStatus as a "
        + "number) and message, and every usage the transport was asked for.";

    private static readonly string[] NativeDisplayBrightnessHidSections = ["buffers", "sequences"];

    [Fact]
    public async Task NativeDisplayBrightnessHidVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeDisplayBrightnessHidVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeDisplayBrightnessHidVectors)
            : Path.Combine(output, NativeDisplayBrightnessHidVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", "ioctl", .. NativeDisplayBrightnessHidSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeDisplayBrightnessHidComment;
            root["ioctl"] = WindowsRazerDisplayBrightnessHidTransport.SubmitInputIoctl;
        }

        Assert.Equal(NativeDisplayBrightnessHidComment, (string)root["comment"]!);
        Assert.Equal(WindowsRazerDisplayBrightnessHidTransport.SubmitInputIoctl, (uint)root["ioctl"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        JsonArray buffers = root["buffers"]!.AsArray();
        Assert.Equal(
            Enum.GetValues<DisplayBrightnessHidUsage>().Select(usage => (int)usage),
            buffers.Select(row => (int)row!["usage"]!));
        foreach (JsonNode? entry in buffers)
        {
            JsonObject row = entry!.AsObject();
            var usage = (DisplayBrightnessHidUsage)(int)row["usage"]!;
            Record(row, "buffer", Convert.ToHexStringLower(
                WindowsRazerDisplayBrightnessHidTransport.CreateControlBuffer(usage)));
        }

        foreach (JsonNode? entry in root["sequences"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            (JsonArray steps, JsonArray usages) = await NativeDisplayBrightnessHidSequenceAsync(row);
            Record(row, "steps", steps);
            Record(row, "usages", usages);
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString())
                .Append(",\n\"ioctl\":").Append(root["ioctl"]!.ToJsonString());
            foreach (string section in NativeDisplayBrightnessHidSections)
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
                failures.Add($"{row["name"] ?? row["usage"]} {member}: {observed?.ToJsonString() ?? "null"}");
            }
        }
    }

    private async Task<(JsonArray Steps, JsonArray Usages)> NativeDisplayBrightnessHidSequenceAsync(
        JsonObject row)
    {
        bool verified = (bool)row["verified"]!;
        var transport = new ScriptedDisplayBrightnessHidTransport(
            row["writes"]!.AsArray().Select(write => new DisplayBrightnessHidWriteResult(
                Enum.Parse<DisplayBrightnessHidWriteStatus>((string)write!["status"]!),
                (string?)write["error"])));
        TestContext context = CreateContext(
            providerAvailable: verified,
            displayBrightnessHidController: new DisplayBrightnessHidController(transport));
        if (verified)
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        var steps = new JsonArray();
        foreach (JsonNode? request in row["requests"]!.AsArray())
        {
            ControlOperationResult result = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
                (DisplayBrightnessHidUsage)(int)request!,
                CancellationToken.None);
            steps.Add(new JsonObject
            {
                ["status"] = (int)result.Status,
                ["message"] = result.Message,
            });
        }

        return (steps, new JsonArray([.. transport.Usages.Select(usage => JsonValue.Create((int)usage))]));
    }

    private sealed class ScriptedDisplayBrightnessHidTransport(
        IEnumerable<DisplayBrightnessHidWriteResult> writes)
        : IDisplayBrightnessHidTransport
    {
        private readonly Queue<DisplayBrightnessHidWriteResult> writes = new(writes);

        public List<DisplayBrightnessHidUsage> Usages { get; } = [];

        public DisplayBrightnessHidAvailability GetAvailability() =>
            DisplayBrightnessHidAvailability.Available;

        public DisplayBrightnessHidWriteResult SetUsage(DisplayBrightnessHidUsage usage)
        {
            Usages.Add(usage);
            return writes.Count > 0
                ? writes.Dequeue()
                : new DisplayBrightnessHidWriteResult(DisplayBrightnessHidWriteStatus.Applied);
        }
    }
}
