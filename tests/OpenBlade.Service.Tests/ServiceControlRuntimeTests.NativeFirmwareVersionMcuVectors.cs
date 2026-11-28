using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's MCU firmware version read is pinned against managed.
// Request and reply rows replay McuVersionProtocol through the managed RazerReport
// codec with the four-byte payload length McuVersionCommandClient gives the 00/87
// getter. Budget rows drive the real ServiceControlRuntime over a scripted device
// result and record the McuVersion it publishes and how many reads it made.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeMcuVersionVectors = "mcu-version-vectors.json";
    private const string RegenerateNativeMcuVersionVectors =
        "OPENBLADE_REGENERATE_NATIVE_MCU_VERSION_VECTORS";

    private const string NativeMcuVersionComment =
        "Recorded by ServiceControlRuntimeTests.NativeFirmwareVersionMcuVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_MCU_VERSION_VECTORS set to this directory. "
        + "A reply is the request's report with status 2 and the row's payload, then its edits "
        + "(offset to byte), a recomputed checksum (inverted when corruptChecksum) and its envelope length. "
        + "Its expect is the decoded version or the managed validation failure. A budget row starts "
        + "with a record whose McuVersion is Unavailable. Each step is one ObserveAsync whose device "
        + "read returns 1.5.0.0 (Version) or throws (Failed), or BeginResumeValidationAsync (Resume); "
        + "expect is the published McuVersion and the number of device reads after the last.";

    private static readonly string[] NativeMcuVersionSections = ["requests", "replies", "budget"];

    [Fact]
    public async Task NativeFirmwareVersionMcuVectors_MatchManagedCodecAndReadBudget()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeMcuVersionVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeMcuVersionVectors)
            : Path.Combine(output, NativeMcuVersionVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeMcuVersionSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeMcuVersionComment;
        }

        Assert.Equal(NativeMcuVersionComment, (string)root["comment"]!);
        var failures = new List<string>();
        var requests = new Dictionary<string, RazerReport>();
        foreach (JsonNode? entry in root["requests"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            RazerReport report = McuVersionProtocol.CreateQuery((byte)row["transactionId"]!);
            requests.Add((string)row["name"]!, report);
            Record(row, "report", Convert.ToHexString(report.Encode()));
        }

        foreach (JsonNode? entry in root["replies"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            RazerReport report = requests[(string)row["request"]!];
            byte[] response = NativeDeviceModeReply(report, row);
            Record(row, "response", Convert.ToHexString(response));
            string decoded;
            try
            {
                decoded = McuVersionProtocol.TryDecodeQuery(
                    report.ValidateResponse(
                        response,
                        expectedPayloadLength: McuVersionProtocol.PayloadLength).Span,
                    out string version)
                    ? version
                    : nameof(HidProtocolValidationFailure.PayloadValidator);
            }
            catch (RazerProtocolException exception)
            {
                decoded = exception.ValidationFailure.ToString();
            }

            Record(row, "expect", decoded);
        }

        foreach (JsonNode? entry in root["budget"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "expect", await ObserveNativeMcuVersionBudgetAsync(row["steps"]!.AsArray()));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeMcuVersionSections)
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Record(JsonObject row, string member, JsonNode observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                row[member] = observed;
            }
            else if (!JsonNode.DeepEquals(row[member], observed))
            {
                failures.Add($"{row["name"]} {member}: {observed.ToJsonString()}");
            }
        }
    }

    // Each Version or Failed step is one verified observation, whose
    // ReadFirmwareVersionsAsync reads the MCU while its bounded budget lasts and
    // the record is still Unavailable. A Resume step restores that budget.
    private async Task<JsonObject> ObserveNativeMcuVersionBudgetAsync(JsonArray steps)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            firmwareVersions: FirmwareVersions.Unavailable());
        foreach (JsonNode? step in steps)
        {
            switch ((string)step!)
            {
                case "Version":
                    context.Device.McuVersion = "1.5.0.0";
                    await context.Runtime.ObserveAsync(CancellationToken.None);
                    break;
                case "Failed":
                    context.Device.McuVersion = null;
                    context.Device.McuVersionFailures.Enqueue(
                        new TimeoutException("firmware query timed out"));
                    await context.Runtime.ObserveAsync(CancellationToken.None);
                    context.Device.McuVersionFailures.Clear();
                    break;
                case "Resume":
                    await context.Runtime.BeginResumeValidationAsync(CancellationToken.None);
                    break;
                default:
                    throw new InvalidDataException($"Unknown step {step}.");
            }
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.True(status.DeviceVerified);
        return new JsonObject
        {
            ["mcuVersion"] = status.Firmware!.McuVersion,
            ["reads"] = context.Device.McuVersionReadCalls,
        };
    }
}
