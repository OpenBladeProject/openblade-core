using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// The native Service serves the four Fan Stop requests as managed's production
// composition serves them while its guardian is unavailable, and publishes the
// same fanStopStatus. These rows pin what managed replies and publishes for the
// same inputs. Each sequence row saves its settings and Battery profile flag, runs
// its requests in order through one runtime and records each result with the
// fanStopStatus member the v91 status codec then writes, and the stored settings,
// flag and whether any configuration document changed. The capability rows record
// the exact RZ09-0581 evidence and the observation record native publishes. The
// Rust tests replay all three sections.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeFanStopVectors = "fan-stop-vectors.json";
    private const string RegenerateNativeFanStopVectors = "OPENBLADE_REGENERATE_NATIVE_FAN_STOP_VECTORS";

    private const string NativeFanStopComment =
        "Recorded by ServiceControlRuntimeTests.NativeFanStopVectors; regenerate with "
        + "OPENBLADE_REGENERATE_NATIVE_FAN_STOP_VECTORS set to this directory. The runtime uses "
        + "GuardianFanStopBackend over a guardian client that reports GuardianUnavailable with no "
        + "message and a Clear startup disposition, as WindowsFanStopGuardianClient does when it "
        + "cannot verify the guardian and its FanStopGuardian store inspects Clear (a pending record "
        + "or a failed inspection makes managed publish RecoveryRequired, which native does not "
        + "model). Device capabilities carry the Fan Stop evidence the production factory "
        + "admits for the row's host (exact RZ09-0581 when evidence is true, none otherwise). A "
        + "sequence row saves its settings (merged over the defaults) and Battery stopFansInBatterySaver, "
        + "optionally appends a space to profiles.json before activation (quarantined), activates the "
        + "user, observes once, then records the fanStopStatus member (initial), runs each request (mode, settings, resume or batterySaver; "
        + "sessionChanged makes stillControlsSession false) and records its result and the "
        + "fanStopStatus member, then the stored settings, flag and whether any configuration "
        + "document changed. A capability row records the evidence flags and the observation "
        + "record the native Service publishes beside a confirmed HyperBoost.";

    private static readonly string[] NativeFanStopSections = ["capabilities", "sequences"];

    [Fact]
    public async Task NativeFanStopVectors_MatchManagedRuntime()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeFanStopVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeFanStopVectors)
            : Path.Combine(output, NativeFanStopVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeFanStopSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeFanStopComment;
        }

        Assert.Equal(NativeFanStopComment, (string)root["comment"]!);
        var failures = new List<string>();
        var names = new HashSet<string>();
        foreach (JsonNode? entry in root["capabilities"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            (bool trial, bool source) = FanStopEvidence((string)row["productName"]!);
            Record(row, "evidence", new JsonObject { ["trial"] = trial, ["source"] = source });
            Record(row, "record", await ObservedFanStopCapabilitiesAsync(trial, source));
        }

        foreach (JsonNode? entry in root["sequences"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            string name = (string)row["name"]!;
            Assert.True(names.Add(name), name);
            (JsonNode? initial, JsonArray steps, JsonObject stored) = await NativeFanStopSequenceAsync(name, row);
            Record(row, "initial", initial);
            Record(row, "steps", steps);
            Record(row, "stored", stored);
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeFanStopSections)
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

    // The RZ09-0581 production factory's two evidence predicates
    // (ProductionBatteryFactory) for a host with this exact product name. Any
    // other host never composes that factory, so it has neither.
    private static (bool Trial, bool Source) FanStopEvidence(string productName)
    {
        var identity = new HostIdentityReadResult(
            new HostIdentityEvidence("Razer", productName, Sku: null, BiosVersion: null),
            Failure: null,
            HostFirmwareCompatibility.ModelScoped);
        bool trial = ProductionBatteryFactory.IsExactModelScopedRz090581Identity(identity);
        return (
            trial,
            trial && FanStopModelContract.ForProfile(
                TargetBladeDevice.Model,
                TargetBladeDevice.ProductId,
                WindowsHostIdentitySource.ProductionIdentity.ProductName).Count != 0);
    }

    private async Task<(JsonNode? Initial, JsonArray Steps, JsonObject Stored)> NativeFanStopSequenceAsync(
        string name,
        JsonObject row)
    {
        string root = "native-fan-stop-" + name;
        bool evidence = (bool)row["evidence"]!;
        bool quarantined = (bool?)row["quarantined"] == true;
        JsonObject saved = row["saved"]?.AsObject() ?? [];
        var client = new GuardianFanStopBackendTests.Client
        {
            FirmwareAvailability = static () => new(FanStopUnavailableReason.GuardianUnavailable),
        };
        ThermalConvergenceTestContext thermal = await CreateThermalConvergenceContextAsync(
            root,
            configuration => configuration with
            {
                Preferences = configuration.Preferences with
                {
                    FanStopSettings = FanStopSettingsFrom(saved["settings"]),
                },
                Profiles = configuration.Profiles with
                {
                    Battery = configuration.Profiles.Battery with
                    {
                        StopFansInBatterySaver = (bool?)saved["stopFans"] == true,
                    },
                },
            },
            beforeActivation: quarantined
                ? async (_, sid) => await File.AppendAllTextAsync(
                    Path.Combine(directory, root, "users", sid.Value, "profiles.json"),
                    " ")
                : null,
            requireAuthoritativeSeed: false,
            fanStopBackend: new GuardianFanStopBackend(client));
        TestContext context = thermal.Context;
        (bool trial, bool source) = FanStopEvidence(evidence
            ? WindowsHostIdentitySource.ProductionIdentity.ProductName
            : WindowsHostIdentitySource.Rz090528Identity.ProductName);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            HasFanStopTrialEvidence = trial,
            HasFanStopSourceEvidence = source,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        string userRoot = Path.Combine(directory, root, "users", thermal.UserSid.Value);
        string[] documents =
        [
            Path.Combine(userRoot, "profiles.json"),
            Path.Combine(userRoot, "battery-preferences.json"),
            Path.Combine(userRoot, "battery-configuration.commit.json"),
        ];
        byte[][] before = [.. documents.Select(File.ReadAllBytes)];
        bool sessionChanged = (bool?)row["sessionChanged"] == true;
        Func<bool> stillControlsSession = () => !sessionChanged;
        JsonNode? initial = await EncodedStatusMemberAsync(context.Runtime.GetStatus(), "fanStopStatus");
        var steps = new JsonArray();
        foreach (JsonNode? step in row["requests"]!.AsArray())
        {
            JsonObject request = step!.AsObject();
            ControlOperationResult result = request switch
            {
                _ when request["mode"] is { } mode => await context.Runtime.SetFanStopModeAsync(
                    (FanStopMode)(int)mode, stillControlsSession, CancellationToken.None),
                _ when request["settings"] is { } settings => await context.Runtime.SetFanStopSettingsAsync(
                    FanStopSettingsFrom(settings), stillControlsSession, CancellationToken.None),
                _ when request["batterySaver"] is { } enabled => await context.Runtime.SetBatterySaverFanStopAsync(
                    (bool)enabled, stillControlsSession, CancellationToken.None),
                _ => await context.Runtime.ResumeAutomaticFanStopAsync(
                    stillControlsSession, CancellationToken.None),
            };
            steps.Add(new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["status"] = (int)result.Status,
                    ["message"] = result.Message,
                },
                ["fanStop"] = await EncodedStatusMemberAsync(context.Runtime.GetStatus(), "fanStopStatus"),
            });
        }

        Assert.Equal(0, client.StartCalls);
        Assert.Equal(0, client.AutoCalls);
        bool written = !documents.Select(File.ReadAllBytes)
            .Zip(before, (after, prior) => after.AsSpan().SequenceEqual(prior))
            .All(same => same);
        JsonNode stored = JsonNode.Parse(File.ReadAllText(documents[1]))!["preferences"]!["fanStopSettings"]!;
        JsonNode profiles = JsonNode.Parse(File.ReadAllText(documents[0]))!["profiles"]!;
        return (
            initial,
            steps,
            new JsonObject
            {
                ["settings"] = stored.DeepClone(),
                ["stopFans"] = (bool)profiles["battery"]!["stopFansInBatterySaver"]!,
                ["written"] = written,
            });
    }

    // Managed's FanStopSettings record from a row's members over its defaults,
    // as the codec reads them.
    private static FanStopSettings FanStopSettingsFrom(JsonNode? members) =>
        members is null
            ? FanStopSettings.Default
            : System.Text.Json.JsonSerializer.Deserialize<FanStopSettings>(
                members.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    // As ObservedHyperBoostCapabilitiesAsync, with the row's Fan Stop evidence.
    private async Task<JsonNode> ObservedFanStopCapabilitiesAsync(bool trial, bool source)
    {
        TestContext context = CreateContext(providerAvailable: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus() with
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ObservedOnly,
                SupportsTemporaryFullCharge = true,
                HasFanStopTrialEvidence = trial,
                HasFanStopSourceEvidence = source,
            },
            ConfirmedState = new ConfirmedDeviceState(null, FanMode.Automatic, null, null)
            {
                HyperBoostActive = true,
            },
        };
        return (await EncodedStatusAsync(status))["capabilities"]!.DeepClone();
    }

    // One member of the status as the v91 codec writes it. The test device's
    // confirmed state carries no capability record, which the codec requires
    // beside an active HyperBoost, so the frame drops the confirmed state.
    private static async Task<JsonNode?> EncodedStatusMemberAsync(ServiceStatusSnapshot status, string member) =>
        (await EncodedStatusAsync(status with { ConfirmedState = null }))[member]?.DeepClone();

    private static async Task<JsonNode> EncodedStatusAsync(ServiceStatusSnapshot status)
    {
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
            CancellationToken.None);
        byte[] framed = stream.ToArray();
        stream.Position = 0;
        Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        return JsonNode.Parse(framed.AsSpan(4))!["status"]!;
    }
}
