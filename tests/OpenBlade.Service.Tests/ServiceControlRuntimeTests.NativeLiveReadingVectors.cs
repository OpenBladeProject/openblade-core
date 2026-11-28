using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's live readings are pinned against managed. Fan rows replay
// FanRpmProtocol through the managed RazerReport codec with the three-byte
// payload length FanRpmQueryPort gives the 0D/88 getter. Battery rows replay the
// WindowsPowerStateProvider mappings. Native frames are recorded by the Rust tests
// and decoded here through the real v91 status codec, each with the managed
// capability record of its composition (capability-record-vectors.json).
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeLiveReadingVectors = "live-reading-vectors.json";
    private const string NativeLiveReadingFrames = "live-reading-native-frames.json";
    private const string RegenerateNativeLiveReadingVectors =
        "OPENBLADE_REGENERATE_NATIVE_LIVE_READING_VECTORS";

    private const string NativeLiveReadingComment =
        "Recorded by ServiceControlRuntimeTests.NativeLiveReadingVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_LIVE_READING_VECTORS set to this directory. "
        + "A reply is the request's report with status 2 and the row's payload, then its edits "
        + "(offset to byte), a recomputed checksum (inverted when corruptChecksum) and its envelope length. "
        + "Its expect is the decoded RPM or the managed validation failure. A battery-health row is "
        + "WindowsPowerStateProvider.MapBatteryHealth over its (design, full charge) capacities; a power-flow "
        + "row is MapBatteryPowerFlow.";

    private static readonly string[] NativeLiveReadingSections =
        ["requests", "replies", "batteryHealth", "batteryPowerFlow"];

    [Fact]
    public async Task NativeLiveReadingVectors_MatchManagedCodecAndMappings()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeLiveReadingVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeLiveReadingVectors)
            : Path.Combine(output, NativeLiveReadingVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeLiveReadingSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeLiveReadingComment;
        }

        Assert.Equal(NativeLiveReadingComment, (string)root["comment"]!);
        var failures = new List<string>();
        var requests = new Dictionary<string, (RazerReport Report, FanRpmSelector Selector)>();
        foreach (JsonNode? entry in root["requests"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            var selector = (FanRpmSelector)(byte)row["selector"]!;
            RazerReport report = FanRpmProtocol.CreateQuery((byte)row["transactionId"]!, selector);
            requests.Add((string)row["name"]!, (report, selector));
            Record(row, "report", Convert.ToHexString(report.Encode()));
        }

        foreach (JsonNode? entry in root["replies"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            (RazerReport report, FanRpmSelector selector) = requests[(string)row["request"]!];
            byte[] response = NativeDeviceModeReply(report, row);
            Record(row, "response", Convert.ToHexString(response));
            JsonNode decoded;
            try
            {
                decoded = FanRpmProtocol.TryDecodeQuery(
                    report.ValidateResponse(response, expectedPayloadLength: 3).Span,
                    selector,
                    out int rpm)
                    ? rpm
                    : nameof(HidProtocolValidationFailure.PayloadValidator);
            }
            catch (RazerProtocolException exception)
            {
                decoded = exception.ValidationFailure.ToString();
            }

            Record(row, "expect", decoded);
        }

        foreach (JsonNode? entry in root["batteryHealth"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            BatteryHealthStatus? health = WindowsPowerStateProvider.MapBatteryHealth(
                row["capacities"]!.AsArray()
                    .Select(pair => ((uint)pair![0]!, (uint)pair[1]!))
                    .ToArray());
            Record(row, "expect", health is null
                ? null
                : new JsonObject
                {
                    ["designCapacityMilliwattHours"] = health.DesignCapacityMilliwattHours,
                    ["fullChargeCapacityMilliwattHours"] = health.FullChargeCapacityMilliwattHours,
                });
        }

        foreach (JsonNode? entry in root["batteryPowerFlow"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "expect", WindowsPowerStateProvider.MapBatteryPowerFlow(
                (bool)row["present"]!,
                (uint)row["rate"]!));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeLiveReadingSections)
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

    // Every native frame the Rust tests recorded decodes through the strict
    // v91 codec, and carries the managed record of its composition: the
    // identified read-only record beside a confirmed state, and managed's
    // unavailable record without one.
    [Fact]
    public async Task NativeLiveReadingFrames_DecodeThroughTheManagedCodec()
    {
        string path = Path.Combine(AppContext.BaseDirectory, NativeLiveReadingFrames);
        JsonArray rows = JsonNode.Parse(File.ReadAllText(path))!["frames"]!.AsArray();
        Assert.NotEmpty(rows);
        Dictionary<string, JsonNode> records = JsonNode.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, NativeCapabilityRecordVectors)))!["rows"]!.AsArray()
            .ToDictionary(row => (string)row!["name"]!, row => row!["record"]!);
        var names = new HashSet<string>();
        foreach (JsonNode? entry in rows)
        {
            string name = (string)entry!["name"]!;
            Assert.True(names.Add(name), name);
            byte[] json = Encoding.UTF8.GetBytes((string)entry["frame"]!);
            byte[] framed = new byte[json.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, json.Length);
            json.CopyTo(framed, 4);
            using var stream = new MemoryStream(framed, writable: false);
            ServiceStatusEvent decoded = Assert.IsType<ServiceStatusEvent>(
                await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
            Assert.Equal(stream.Length, stream.Position);
            ServiceStatusSnapshot status = decoded.Status;
            JsonNode expected = entry["expect"]!;
            Assert.Equal((bool?)expected["hyperBoostActive"], status.ConfirmedState?.HyperBoostActive);
            string row = status.ConfirmedState is null ? "unidentified" : "read only";
            Assert.Equal(row, (string)expected["capabilityRow"]!);
            Assert.True(JsonNode.DeepEquals(records[row], JsonNode.Parse(json)!["status"]!["capabilities"]), name);
            Assert.Equal(status.ConfirmedState is not null, status.Capabilities!.HyperBoostAccess is not null);
            Assert.Equal((int?)expected["fan1Rpm"], status.LiveReadings?.Fan1Rpm);
            Assert.Equal((int?)expected["fan2Rpm"], status.LiveReadings?.Fan2Rpm);
            Assert.Equal(
                (int?)expected["batteryPowerFlowMilliwatts"],
                status.LiveReadings?.BatteryPowerFlowMilliwatts);
            Assert.Equal(
                (int?)expected["gpuTemperatureCelsius"],
                status.LiveReadings?.GpuTemperatureCelsius);
            Assert.Equal(
                (int?)expected["designCapacityMilliwattHours"],
                status.BatteryHealth?.DesignCapacityMilliwattHours);
            Assert.Equal(
                (int?)expected["fullChargeCapacityMilliwattHours"],
                status.BatteryHealth?.FullChargeCapacityMilliwattHours);
        }
    }
}
