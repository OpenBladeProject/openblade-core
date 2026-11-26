using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

// The native Service's firmware versions are pinned against managed. A record
// row feeds one set of Windows reads through WindowsFirmwareVersionSource and
// the two WindowsDeviceFirmwareVersionReader selectors, and writes the result
// through the real v91 status codec. The native capture over the same reads must
// produce the same record. The registry loop and the USB4 enumeration filter
// are private to managed, so this test repeats their rules where a row feeds
// them. A version row is Version.TryParse and an integer row is
// Convert.ToInt32 with the invariant culture, the two framework parsers the
// native code mirrors. Native frames are recorded by the Rust tests and decoded
// here through the same codec.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeFirmwareVectors = "firmware-version-vectors.json";
    private const string NativeFirmwareFrames = "firmware-version-native-frames.json";
    private const string RegenerateNativeFirmwareVectors =
        "OPENBLADE_REGENERATE_NATIVE_FIRMWARE_VERSION_VECTORS";

    private const string NativeFirmwareComment =
        "Recorded by ServiceControlRuntimeTests.NativeFirmwareVersionVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_FIRMWARE_VERSION_VECTORS set to this directory. "
        + "A record row is WindowsFirmwareVersionSource.Capture() over its registry values, its "
        + "video adapter instances (one list per adapter key), its USB4 devices and its touchpad HID "
        + "collections, as the v91 status codec writes the firmware member. A version row is "
        + "Version.TryParse and an integer row is Convert.ToInt32 with the invariant culture.";

    private static readonly string[] NativeFirmwareSections = ["records", "versions", "integers"];

    [Fact]
    public async Task NativeFirmwareVersionVectors_MatchManagedSourceAndCodec()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeFirmwareVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeFirmwareVectors)
            : Path.Combine(output, NativeFirmwareVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeFirmwareSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeFirmwareComment;
        }

        Assert.Equal(NativeFirmwareComment, (string)root["comment"]!);
        var failures = new List<string>();
        TestContext context = CreateContext(providerAvailable: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot baseline = context.Runtime.GetStatus();

        foreach (JsonNode? entry in root["records"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            FirmwareVersions firmware = CaptureManagedFirmware(row);
            Record(row, "expect", await FirmwareMemberAsync(baseline, firmware));
        }

        foreach (JsonNode? entry in root["versions"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "expect", Version.TryParse((string)row["text"]!, out _));
        }

        foreach (JsonNode? entry in root["integers"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            JsonNode? parsed;
            try
            {
                parsed = Convert.ToInt32((string)row["text"]!, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                parsed = null;
            }

            Record(row, "expect", parsed);
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeFirmwareSections)
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
                failures.Add(
                    $"{row["name"] ?? row["text"]} {member}: {observed?.ToJsonString() ?? "null"}");
            }
        }
    }

    private static FirmwareVersions CaptureManagedFirmware(JsonObject row)
    {
        JsonObject registry = row["registry"]!.AsObject();
        var source = new WindowsFirmwareVersionSource(
            (_, name) => registry.TryGetPropertyValue(name, out JsonNode? value)
                ? RegistryObject(value!.AsObject())
                : null,
            () => ManagedVideoCandidates(row["video"]!.AsArray()),
            () => WindowsDeviceFirmwareVersionReader.SelectThunderboltFirmwareVersion(
                ManagedPnpCandidates(row["routers"]!.AsArray())),
            () => WindowsDeviceFirmwareVersionReader.SelectTouchpadFirmwareVersion(
                row["interfaces"]!.AsArray().Select(candidate =>
                {
                    JsonObject interfaceRow = candidate!.AsObject();
                    return new WindowsHidFirmwareCandidate(
                        (ushort)interfaceRow["vendorId"]!,
                        (ushort)interfaceRow["productId"]!,
                        (ushort)interfaceRow["usagePage"]!,
                        (ushort)interfaceRow["usage"]!,
                        (ushort)interfaceRow["version"]!);
                })));
        return source.Capture();
    }

    // RegistryKey.GetValue returns int for REG_DWORD, long for REG_QWORD,
    // string[] for REG_MULTI_SZ and byte[] for REG_BINARY.
    private static object RegistryObject(JsonObject value) => (string)value["type"]! switch
    {
        "text" => (string)value["value"]!,
        "multi" => value["value"]!.AsArray().Select(item => (string)item!).ToArray(),
        "int" => (int)value["value"]!,
        "long" => (long)value["value"]!,
        "binary" => new byte[] { 1, 2, 3 },
        _ => throw new InvalidOperationException("Unknown registry value type."),
    };

    // WindowsFirmwareVersionSource.ReadVideoFirmwareCandidates: an instance
    // needs both values to be strings, and each adapter key's instances are
    // collapsed before they are added.
    private static IReadOnlyList<VideoFirmwareCandidate> ManagedVideoCandidates(JsonArray adapters)
    {
        var candidates = new List<VideoFirmwareCandidate>();
        foreach (JsonNode? adapter in adapters)
        {
            var instances = new List<VideoFirmwareCandidate>();
            foreach (JsonNode? instance in adapter!.AsArray())
            {
                if (RegistryObject(instance!["adapter"]!.AsObject()) is string adapterName
                    && RegistryObject(instance["bios"]!.AsObject()) is string bios)
                {
                    instances.Add(new VideoFirmwareCandidate(adapterName, bios));
                }
            }

            candidates.AddRange(
                WindowsFirmwareVersionSource.CollapseRegistryAdapterInstances(instances));
        }

        return candidates;
    }

    // WindowsDeviceFirmwareVersionReader.EnumerateUsb4FirmwareCandidates lists
    // only the root router's instances that have all four properties.
    private static IReadOnlyList<WindowsPnpFirmwareCandidate> ManagedPnpCandidates(JsonArray routers)
    {
        var candidates = new List<WindowsPnpFirmwareCandidate>();
        foreach (JsonNode? entry in routers)
        {
            JsonObject router = entry!.AsObject();
            string instanceId = (string)router["instanceId"]!;
            string? firmware = (string?)router["firmware"];
            string[] hardwareIds = router["hardwareIds"]!.AsArray()
                .Select(id => (string)id!)
                .ToArray();
            string? service = (string?)router["service"];
            string? parent = (string?)router["parent"];
            if (instanceId.StartsWith(
                    $"{WindowsDeviceFirmwareVersionReader.ThunderboltRootRouterId}\\",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(firmware)
                && hardwareIds.Length > 0
                && !string.IsNullOrWhiteSpace(service)
                && !string.IsNullOrWhiteSpace(parent))
            {
                candidates.Add(new(instanceId, firmware, hardwareIds, service, parent));
            }
        }

        return candidates;
    }

    // The v91 status codec's firmware member for a status carrying `firmware`,
    // after the strict decoder accepted the frame.
    private static async Task<JsonNode> FirmwareMemberAsync(
        ServiceStatusSnapshot baseline,
        FirmwareVersions firmware)
    {
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, baseline with { Firmware = firmware }),
            CancellationToken.None);
        byte[] framed = stream.ToArray();
        stream.Position = 0;
        var decoded = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(firmware, decoded.Status.Firmware);
        return JsonNode.Parse(framed.AsSpan(4))!["status"]!["firmware"]!.DeepClone();
    }

    // Every native frame the Rust tests recorded decodes through the strict v91
    // codec to the expected record, including those whose MCU version the native
    // read job read.
    [Fact]
    public async Task NativeFirmwareVersionFrames_DecodeThroughTheManagedCodec()
    {
        string path = Path.Combine(AppContext.BaseDirectory, NativeFirmwareFrames);
        JsonArray rows = JsonNode.Parse(File.ReadAllText(path))!["frames"]!.AsArray();
        Assert.NotEmpty(rows);
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
            JsonObject expect = entry["expect"]!.AsObject();
            string Text(string member) => (string)expect[member]!;
            DeviceFeatureAccess? Access(string member) =>
                expect[member] is null ? null : (DeviceFeatureAccess)(int)expect[member]!;
            var expected = new FirmwareVersions(
                Text("biosVersion"),
                Text("ecVersion"),
                Text("mcuVersion"),
                Text("vbiosVersion"),
                Text("modelNumber"),
                Text("vbiosGpuName"),
                Text("thunderboltFirmwareVersion"),
                Text("touchpadFirmwareVersion"),
                Access("thunderboltFirmwareAccess"),
                Access("touchpadFirmwareAccess"));
            Assert.Equal(expected, decoded.Status.Firmware);
        }

        Assert.Contains(rows, row => (string)row!["expect"]!["mcuVersion"]! == "1.5.0.0");
        Assert.Contains(rows, row => (string)row!["expect"]!["mcuVersion"]! == "Unavailable");
    }
}
