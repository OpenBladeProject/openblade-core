using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service's exportDiagnostics is pinned against managed. The contract file
// records the managed enum names, model catalog and history bounds the native port
// reads. The vector file holds native-authored inputs with the managed outputs: the
// DiagnosticText redaction, DiagnosticHistory retention and truncation, the exporter's
// entry text for typed bundles, and the ControlRequestHandler refusal replies. The
// native archive file is written by the Rust tests: each sample's response is decoded
// through the managed codec and ZIP reader and compared entry for entry with the
// archive the managed provider builds from the same status and history.
public sealed class NativeDiagnosticExportVectorTests
{
    private const string ContractFile = "diagnostic-export-contract.json";
    private const string VectorFile = "diagnostic-export-vectors.json";
    private const string NativeFile = "diagnostic-export-native.json";
    private const string NativePrivateFile = "diagnostic-export-native-private.json";
    private const string Regenerate = "OPENBLADE_REGENERATE_NATIVE_DIAGNOSTIC_EXPORT_VECTORS";
    private const string RequestId = "8f3c2a71-5d4e-4b6a-9c1f-2e7d0a9b4c31";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    // Every managed enum whose name the native configuration and capability ports print.
    private static readonly Type[] Enums =
    [
        typeof(BladeSupportBasis),
        typeof(BreathingColorMode),
        typeof(CustomPerformanceLevel),
        typeof(DeviceFeatureAccess),
        typeof(DeviceSubsystem),
        typeof(FanMode),
        typeof(FirmwareCompatibilityStatus),
        typeof(FunctionKeyPrimaryMode),
        typeof(HyperBoostEntryRestriction),
        typeof(LightingEffect),
        typeof(LightingRefreshRate),
        typeof(LightingRendererOwnershipState),
        typeof(OverrideLifetime),
        typeof(PadlessHyperBoostEligibilityState),
        typeof(PerformancePreset),
        typeof(PowerSource),
        typeof(SettingValueSource),
        typeof(SpecialKeySlotSet),
        typeof(WindowsDynamicLightingAdvisory),
    ];

    [Fact]
    public void NativeDiagnosticExportContract_MatchesManagedDefinitions()
    {
        var enums = new JsonObject();
        var flags = new JsonArray();
        foreach (Type type in Enums)
        {
            var values = new JsonArray();
            foreach (object value in Enum.GetValues(type))
            {
                values.Add(new JsonArray(
                    Enum.GetName(type, value),
                    Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            enums[type.Name] = values;
            if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
            {
                flags.Add(type.Name);
            }
        }

        var models = new JsonArray();
        foreach (RazerBladeModelInfo model in RazerBladeModelCatalog.All)
        {
            HyperBoostWithoutCoolingPadEvidenceGates? gates =
                HyperBoostWithoutCoolingPadEvidenceGates.ForModel(model.ModelNumber);
            models.Add(new JsonObject
            {
                ["modelNumber"] = model.ModelNumber,
                ["usbVendorId"] = model.UsbVendorId,
                ["usbProductId"] = model.UsbProductId,
                ["supportBasis"] = model.SupportBasis.ToString(),
                ["hyperBoostWithoutCoolingPadGates"] = gates is not { } value ? null : new JsonObject
                {
                    ["immediateNoLoad"] = value.ImmediateNoLoadApplyReadbackRestoreValidated,
                    ["sustainedNoLoad"] = value.SustainedNoLoadExecutionValidated,
                    ["reviewedLoad"] = value.ReviewedLoadValidated,
                    ["adapterTransition"] = value.AdapterTransitionValidated,
                    ["lowPowerLifecycle"] = value.LowPowerLifecycleValidated,
                    ["crashRecovery"] = value.CrashRecoveryValidated,
                },
            });
        }

        var contract = new JsonObject
        {
            ["comment"] = "Recorded by NativeDiagnosticExportVectorTests from the managed definitions; "
                + "regenerate with " + Regenerate + " set to this directory.",
            ["enums"] = enums,
            ["flags"] = flags,
            ["models"] = models,
            ["history"] = new JsonObject
            {
                ["maximumEntries"] = DiagnosticHistory.DefaultMaximumEntries,
                ["maximumTotalCharacters"] = DiagnosticHistory.DefaultMaximumTotalCharacters,
                ["maximumLineCharacters"] = DiagnosticHistory.DefaultMaximumLineCharacters,
            },
            ["maximumArchiveBytes"] = OpenBladeIpc.MaximumDiagnosticArchiveBytes,
            ["maximumResponseBytes"] = OpenBladeIpc.MaximumDiagnosticResponseBytes,
        };
        CompareOrWrite(ContractFile, contract);
    }

    [Fact]
    public async Task NativeDiagnosticExportVectors_MatchManagedExporter()
    {
        JsonObject root = Load(VectorFile);
        var failures = new List<string>();
        bool regenerate = IsRegenerating;

        foreach (JsonNode? node in root["redact"]!.AsArray())
        {
            JsonObject row = node!.AsObject();
            string[] tokens = row["tokens"]!.AsArray().Select(token => (string)token!).ToArray();
            Record(row, DiagnosticText.Redact((string)row["input"]!, tokens));
        }

        foreach (JsonNode? node in root["history"]!.AsArray())
        {
            JsonObject row = node!.AsObject();
            JsonObject limits = row["limits"]!.AsObject();
            JsonArray records = row["records"]!.AsArray();
            var clock = new SequenceTimeProvider(records.Select(record =>
                DateTimeOffset.Parse((string)record!["at"]!, System.Globalization.CultureInfo.InvariantCulture)));
            var history = new DiagnosticHistory(
                (int)limits["entries"]!, (int)limits["characters"]!, (int)limits["line"]!, clock);
            foreach (JsonNode? record in records)
            {
                history.RecordSummary(
                    Enum.Parse<ControlOperationStatus>((string)record!["status"]!),
                    (string)record["summary"]!);
            }

            DiagnosticHistorySnapshot snapshot = history.Snapshot();
            var bundle = new DiagnosticBundle(
                new DiagnosticVersions("1.0.0", "Windows", "Unavailable"),
                new DiagnosticDeviceIdentity("Unavailable", "Unavailable", "Unavailable", null),
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                snapshot.CommandResults,
                snapshot.LogLines,
                [])
            {
                ServiceHistory = snapshot,
            };
            Dictionary<string, string> entries = ToDictionary(await ExportAsync(bundle));
            Record(row, new JsonObject
            {
                ["commandResults"] = entries["command-results.json"],
                ["serviceHistory"] = entries["service-history.json"],
            });
        }

        foreach (JsonNode? node in root["bundles"]!.AsArray())
        {
            JsonObject row = node!.AsObject();
            DiagnosticBundle bundle = ReadBundle(row["input"]!.AsObject());
            Record(row, Entries(await ExportAsync(bundle)));
        }

        foreach (JsonNode? node in root["refusals"]!.AsArray())
        {
            JsonObject row = node!.AsObject();
            Record(row, JsonNode.Parse(await RefusalAsync((string)row["caller"]!, (string?)row["failure"])));
        }

        if (regenerate)
        {
            Write(VectorFile, root);
        }

        Assert.Empty(failures);

        void Record(JsonObject row, JsonNode? observed)
        {
            if (regenerate)
            {
                row["expect"] = observed;
            }
            else if (!JsonNode.DeepEquals(row["expect"], observed))
            {
                failures.Add($"{row["name"]}: {observed?.ToJsonString() ?? "null"}");
            }
        }
    }

    [Fact]
    public async Task NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider()
    {
        // The second file holds the thermal composition's private projections.
        JsonNode?[] samples = [.. Load(NativeFile)["samples"]!.AsArray(), .. Load(NativePrivateFile)["samples"]!.AsArray()];
        Assert.NotEmpty(samples);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in samples)
        {
            JsonObject sample = node!.AsObject();
            string name = (string)sample["name"]!;
            Assert.True(names.Add(name), name);

            ServiceStatusSnapshot status = Assert.IsType<ServiceStatusEvent>(
                await IpcMessageCodec.ReadServerFrameAsync(Framed((string)sample["status"]!), default)).Status;
            using MemoryStream responseFrame = Framed((string)sample["response"]!);
            ServiceResponse response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadDiagnosticArchiveFrameAsync(responseFrame, default));
            Assert.Equal(responseFrame.Length, responseFrame.Position);
            Assert.True(response.Success, name);
            DiagnosticArchive native = Assert.IsType<DiagnosticArchive>(response.Archive);

            JsonArray records = sample["history"]!.AsArray();
            var history = new DiagnosticHistory(timeProvider: new SequenceTimeProvider(records.Select(record =>
                DateTimeOffset.Parse((string)record!["at"]!, System.Globalization.CultureInfo.InvariantCulture))));
            foreach (JsonNode? record in records)
            {
                history.RecordSummary(
                    Enum.Parse<ControlOperationStatus>((string)record!["status"]!),
                    (string)record["summary"]!);
            }

            var metadata = new NativeMetadataSource(
                status,
                (string)sample["windowsVersion"]!,
                sample["tokens"]!.AsArray().Select(token => (string)token!).ToArray(),
                DateTimeOffset.Parse((string)sample["capturedAt"]!, System.Globalization.CultureInfo.InvariantCulture));
            var provider = new DiagnosticArchiveProvider(
                new StatusOnlyRuntime(status), history, metadata, new FirmwareQueryDiagnostics());
            DiagnosticArchive managed = await provider.CreateAsync((bool)sample["liveReadingsEnabled"]!, default);

            Assert.Equal(managed.SuggestedFileName, native.SuggestedFileName);
            List<(string Name, string Text)> expected = await ReadEntriesAsync(managed.ZipBytes);
            List<(string Name, string Text)> observed = await ReadEntriesAsync(native.ZipBytes);
            Assert.Equal(expected.Select(entry => entry.Name), observed.Select(entry => entry.Name));
            for (int index = 0; index < expected.Count; index++)
            {
                Assert.True(
                    string.Equals(expected[index].Text, observed[index].Text, StringComparison.Ordinal),
                    $"{name} {expected[index].Name}:\n{observed[index].Text}\nexpected:\n{expected[index].Text}");
            }
        }
    }

    private static bool IsRegenerating =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Regenerate));

    private static JsonObject Load(string file)
    {
        string? output = Environment.GetEnvironmentVariable(Regenerate);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, file)
            : Path.Combine(output, file);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static void CompareOrWrite(string file, JsonObject value)
    {
        if (IsRegenerating)
        {
            Write(file, value);
            return;
        }

        JsonObject recorded = Load(file);
        Assert.True(JsonNode.DeepEquals(recorded, value), value.ToJsonString(FileOptions));
    }

    private static void Write(string file, JsonObject value) => File.WriteAllText(
        Path.Combine(Environment.GetEnvironmentVariable(Regenerate)!, file),
        value.ToJsonString(FileOptions) + "\n",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static MemoryStream Framed(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        return new MemoryStream(framed, writable: false);
    }

    private static DiagnosticBundle ReadBundle(JsonObject input)
    {
        JsonObject versions = input["versions"]!.AsObject();
        JsonObject device = input["device"]!.AsObject();
        var capabilities = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach ((string key, JsonNode? value) in input["capabilities"]!.AsObject())
        {
            capabilities[key] = (bool)value!;
        }

        var configuration = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, JsonNode? value) in input["configuration"]!.AsObject())
        {
            configuration[key] = (string)value!;
        }

        DiagnosticCommandResult[] results = input["commandResults"]!.AsArray().Select(node =>
            new DiagnosticCommandResult(
                DateTimeOffset.Parse((string)node!["timestamp"]!, System.Globalization.CultureInfo.InvariantCulture),
                (string)node["subsystem"]!,
                (string)node["action"]!,
                (string)node["status"]!,
                (string?)node["detail"])).ToArray();
        string[] lines = input["logLines"]!.AsArray().Select(line => (string)line!).ToArray();
        var snapshot = new DiagnosticHistorySnapshot(results, lines)
        {
            EvictedEntries = (long)input["evicted"]!,
            DroppedEntries = (long)input["dropped"]!,
            TruncatedValues = (long)input["truncated"]!,
            RetainedEntries = results.Length + lines.Length,
            RetainedCharacters = (int)input["characters"]!,
        };
        return new DiagnosticBundle(
            new DiagnosticVersions(
                (string)versions["openBladeVersion"]!,
                (string)versions["windowsVersion"]!,
                (string)versions["biosVersion"]!,
                (string)versions["ecVersion"]!,
                (string)versions["mcuVersion"]!,
                (string)versions["vbiosVersion"]!,
                (string)versions["vbiosGpuName"]!,
                (string)versions["thunderboltFirmwareVersion"]!,
                (string)versions["touchpadFirmwareVersion"]!,
                (string?)versions["thunderboltFirmwareAccess"],
                (string?)versions["touchpadFirmwareAccess"]),
            new DiagnosticDeviceIdentity(
                (string)device["model"]!,
                (string)device["vendorId"]!,
                (string)device["productId"]!,
                null),
            capabilities,
            configuration,
            results,
            lines,
            input["tokens"]!.AsArray().Select(token => (string)token!).ToArray(),
            DiagnosticHyperBoostOwnershipJournal.Missing,
            DiagnosticDeviceVerification.NotObserved)
        {
            FirmwareQueries = [],
            ServiceHistory = snapshot,
        };
    }

    private static async Task<List<(string Name, string Text)>> ExportAsync(DiagnosticBundle bundle)
    {
        using var stream = new MemoryStream();
        await DiagnosticBundleExporter.ExportAsync(stream, bundle);
        return await ReadEntriesAsync(stream.ToArray());
    }

    private static async Task<List<(string Name, string Text)>> ReadEntriesAsync(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = new List<(string, string)>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, throwOnInvalidBytes: true));
            entries.Add((entry.FullName, await reader.ReadToEndAsync()));
        }

        return entries;
    }

    private static JsonArray Entries(List<(string Name, string Text)> entries) =>
        new(entries.Select(entry => (JsonNode)new JsonArray(entry.Name, entry.Text)).ToArray());

    private static Dictionary<string, string> ToDictionary(List<(string Name, string Text)> entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Text, StringComparer.Ordinal);

    // The reply managed gives each refused or failed export, as the current codec writes it.
    private static async Task<string> RefusalAsync(string caller, string? failure)
    {
        var user = new SecurityIdentifier("S-1-5-21-1000000000-2000000000-3000000000-1001");
        ServiceClientContext client = caller switch
        {
            "inactiveInteractive" => new(user, SessionId: 2, IsAdministrator: false, ServiceClientLogonKind.Interactive),
            "remoteInteractive" => new(user, SessionId: 1, IsAdministrator: false, ServiceClientLogonKind.RemoteInteractive),
            "notInteractive" => new(user, SessionId: 0, IsAdministrator: false, ServiceClientLogonKind.Other),
            "system" => new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), SessionId: 0,
                IsAdministrator: true, ServiceClientLogonKind.LocalSystem),
            _ => throw new InvalidDataException($"Unknown vector caller {caller}."),
        };
        var handler = new ControlRequestHandler(
            new StatusOnlyRuntime(NativeShapedStatus()),
            new FailingArchiveProvider(failure),
            NullServiceShutdownSignal.Instance,
            new ConsoleSessionAuthority(1));
        ServiceResponse response = await handler.HandleAsync(
            new ExportDiagnosticsRequest(OpenBladeIpc.ProtocolVersion, Guid.Parse(RequestId), LiveReadingsEnabled: false),
            client,
            default);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(stream, response, default);
        return Encoding.UTF8.GetString(stream.ToArray().AsSpan(4));
    }

    private static ServiceStatusSnapshot NativeShapedStatus() => new(
        Revision: 1,
        DeviceVerified: false,
        WritesBlocked: true,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        Message: "Device unavailable.",
        Capabilities: null,
        ConfirmedState: null,
        Profiles: ProfileDefaults.CreateSafeDefaults(),
        DesiredState: new ProfileAutomationState(ProfileDefaults.CreateSafeDefaults(), new BatteryAutomationPreferences(),
            PowerSource.Battery).Resolve(new ConfirmedDeviceState(null, null, null, null)),
        ActivePowerSource: PowerSource.Battery,
        LiveReadings: null,
        PerformanceOverride: null,
        FanOverride: null,
        LightingOverride: null,
        FullChargeOverride: false);

    private sealed class SequenceTimeProvider(IEnumerable<DateTimeOffset> times) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> queue = new(times);

        public override DateTimeOffset GetUtcNow() =>
            queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException("No recorded time remains.");
    }

    // Native publishes its versions from the status firmware record: no fresh Windows
    // firmware read runs on the export path. Its OpenBlade version is the status
    // serviceVersion and its Windows version the host's RtlGetVersion text.
    private sealed class NativeMetadataSource(
        ServiceStatusSnapshot status,
        string windowsVersion,
        IReadOnlyCollection<string> tokens,
        DateTimeOffset capturedAt) : IDiagnosticMetadataSource
    {
        public DiagnosticHostMetadata Capture()
        {
            FirmwareVersions firmware = status.Firmware ?? FirmwareVersions.Unavailable();
            return new(
                new DiagnosticVersions(
                    status.ServiceVersion ?? "Unavailable",
                    windowsVersion,
                    firmware.BiosVersion,
                    firmware.EcVersion,
                    firmware.McuVersion,
                    firmware.VbiosVersion,
                    firmware.VbiosGpuName,
                    firmware.ThunderboltFirmwareVersion,
                    firmware.TouchpadFirmwareVersion,
                    firmware.ThunderboltFirmwareAccess?.ToString(),
                    firmware.TouchpadFirmwareAccess?.ToString()),
                tokens,
                capturedAt);
        }
    }

    private sealed class ConsoleSessionAuthority(int session) : IInteractiveSessionAuthority
    {
        public int? GetControlSessionId() => session;
    }

    private sealed class FailingArchiveProvider(string? failure) : IDiagnosticArchiveProvider
    {
        public Task<DiagnosticArchive> CreateAsync(bool liveReadingsEnabled, CancellationToken cancellationToken) =>
            failure is null
                ? throw new InvalidOperationException("A refused export must not build an archive.")
                : throw new InvalidDataException(failure);
    }

    // The runtime of a status-only exporter: every diagnostic cache is the interface
    // default a source without that evidence reports, and anything else is refused.
    private sealed class StatusOnlyRuntime(ServiceStatusSnapshot status) : IServiceControlRuntime
    {
        public ServiceStatusSnapshot GetStatus() => status;

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => Unexpected();
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Export must not refresh hardware.");
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Export must not refresh conflicts.");
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            ObserveAsync(includeLiveReadings: false, cancellationToken);

        private static Task<ControlOperationResult> Unexpected() =>
            throw new InvalidOperationException("Export must not mutate hardware.");
    }
}
