using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's read-only keyboard Driver-mode observation is pinned
// against managed. Request and reply rows replay DeviceModeProtocol through the
// managed RazerReport codec with the two-byte payload length
// ProductionLightingPort gives each device-mode command. Status rows drive the
// real ServiceControlRuntime.MaintainDriverModeAsync once per periodic
// observation over a scripted device result and record the status it publishes.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeKeyboardDriverModeVectors = "keyboard-driver-mode-vectors.json";
    private const string RegenerateNativeKeyboardDriverModeVectors =
        "OPENBLADE_REGENERATE_NATIVE_DRIVER_MODE_VECTORS";

    private const string NativeKeyboardDriverModeComment =
        "Recorded by ServiceControlRuntimeTests.NativeKeyboardDriverModeVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_DRIVER_MODE_VECTORS set to this directory. "
        + "A reply is the request's report with status 2 and the row's payload, then its edits "
        + "(offset to byte), a recomputed checksum (inverted when corruptChecksum) and its envelope length. "
        + "Its expect is the decoded mode, Acknowledged or the managed validation failure. "
        + "A status row starts with the startup ObserveAsync. Each step is one MaintainDriverModeAsync "
        + "with the device result Driver, Normal, Failed (the read and its one recovery fail) or "
        + "Unavailable (no verified device), then an ObserveAsync status rebuild; expect is the status after the last.";

    [Fact]
    public async Task NativeKeyboardDriverModeVectors_MatchManagedCodecAndStatus()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeKeyboardDriverModeVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeKeyboardDriverModeVectors)
            : Path.Combine(output, NativeKeyboardDriverModeVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", "requests", "replies", "status"],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeKeyboardDriverModeComment;
        }

        Assert.Equal(NativeKeyboardDriverModeComment, (string)root["comment"]!);
        var failures = new List<string>();
        var requests = new Dictionary<string, (RazerReport Report, Func<ReadOnlySpan<byte>, string> Decode)>();
        foreach (JsonNode? entry in root["requests"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            byte transaction = (byte)row["transactionId"]!;
            (RazerReport Report, Func<ReadOnlySpan<byte>, string> Decode) request = name switch
            {
                "query" => (
                    DeviceModeProtocol.CreateQuery(transaction),
                    payload => DeviceModeProtocol.TryDecodeQuery(payload, out RazerDeviceMode mode)
                        ? mode.ToString()
                        : nameof(HidProtocolValidationFailure.PayloadValidator)),
                "setNormal" => Setter(transaction, RazerDeviceMode.Normal),
                "setDriver" => Setter(transaction, RazerDeviceMode.Driver),
                _ => throw new InvalidDataException($"Unknown request {name}."),
            };
            requests.Add(name, request);
            Record(row, "report", Convert.ToHexString(request.Report.Encode()));
        }

        foreach (JsonNode? entry in root["replies"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            (RazerReport report, Func<ReadOnlySpan<byte>, string> decode) = requests[(string)row["request"]!];
            byte[] response = NativeDeviceModeReply(report, row);
            Record(row, "response", Convert.ToHexString(response));
            string decoded;
            try
            {
                decoded = decode(report.ValidateResponse(response, expectedPayloadLength: 2).Span);
            }
            catch (RazerProtocolException exception)
            {
                decoded = exception.ValidationFailure.ToString();
            }

            Record(row, "expect", decoded);
        }

        foreach (JsonNode? entry in root["status"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "expect", await ObserveNativeDriverModeStatusAsync(row["steps"]!.AsArray()));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in (string[])["requests", "replies", "status"])
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        static (RazerReport, Func<ReadOnlySpan<byte>, string>) Setter(byte transaction, RazerDeviceMode mode) => (
            RazerReport.Create(
                transaction,
                DeviceModeProtocol.OperationClass,
                DeviceModeProtocol.SetOperationId,
                DeviceModeProtocol.CreateSetArguments(mode)),
            payload => DeviceModeProtocol.TryDecodeSet(payload, mode)
                ? "Acknowledged"
                : nameof(HidProtocolValidationFailure.PayloadValidator));

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

    private static byte[] NativeDeviceModeReply(RazerReport request, JsonObject row)
    {
        byte[] payload = Convert.FromHexString((string)row["payload"]!);
        byte[] response = RazerReport.Create(
            request.TransactionId,
            request.CommandClass,
            request.CommandId,
            payload).Encode();
        response[0] = 0x02;
        if (row["edits"] is JsonObject edits)
        {
            foreach ((string offset, JsonNode? value) in edits)
            {
                int index = int.Parse(offset, System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotEqual(88, index);
                response[index] = (byte)value!;
            }
        }

        response[88] = 0;
        for (int index = 2; index <= 87; index++)
        {
            response[88] ^= response[index];
        }

        if (row["corruptChecksum"] is not null)
        {
            response[88] ^= 0xFF;
        }

        int envelope = row["envelope"] is JsonNode length ? (int)length : response.Length;
        Array.Resize(ref response, envelope);
        return response;
    }

    // Each step is one periodic MaintainDriverModeAsync and the next status
    // rebuild, which keeps the mode only for a verified device. The managed recovery
    // retries once per failure streak, so a failed step fails both reads and
    // leaves no queued failure for the next step.
    private async Task<JsonObject> ObserveNativeDriverModeStatusAsync(JsonArray steps)
    {
        TestContext context = CreateContext(providerAvailable: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        foreach (JsonNode? step in steps)
        {
            context.Provider.Available = true;
            context.Device.DriverModeReadFailures.Clear();
            switch ((string)step!)
            {
                case "Driver":
                    context.Device.DriverModeActive = true;
                    await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
                    break;
                case "Normal":
                    context.Device.DriverModeActive = false;
                    await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
                    break;
                case "Failed":
                    context.Device.DriverModeReadFailures.Enqueue(new IOException("read failed"));
                    context.Device.DriverModeReadFailures.Enqueue(new IOException("recovery failed"));
                    await Assert.ThrowsAsync<IOException>(() =>
                        context.Runtime.MaintainDriverModeAsync(CancellationToken.None));
                    break;
                case "Unavailable":
                    context.Provider.Available = false;
                    await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
                    break;
                default:
                    throw new InvalidDataException($"Unknown step {step}.");
            }

            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        return new JsonObject
        {
            ["deviceVerified"] = status.DeviceVerified,
            ["keyboardDriverModeActive"] = status.KeyboardDriverModeActive,
        };
    }
}
