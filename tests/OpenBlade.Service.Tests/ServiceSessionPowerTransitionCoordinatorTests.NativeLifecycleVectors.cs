using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// The native Service's lifecycle operations
// (tools/openblade-windows-host/src/windows/scm/installed/session_lifecycle.rs)
// are pinned against managed. Each frame row is the reply managed's handler
// gives one framed request from one caller; each relay case replays one
// coordinator over a 1 kHz QPC clock and records every report's result. The
// Rust installed-Service tests read the same file and assert each documented
// deviation literally.
public sealed partial class ServiceSessionPowerTransitionCoordinatorTests
{
    private const string NativeLifecycleVectors = "session-lifecycle-vectors.json";
    private const string RegenerateNativeLifecycleVectors =
        "OPENBLADE_REGENERATE_NATIVE_SESSION_LIFECYCLE_VECTORS";

    private const string NativeLifecycleComment =
        "Recorded by ServiceSessionPowerTransitionCoordinatorTests.NativeLifecycleVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_SESSION_LIFECYCLE_VECTORS set to this directory. "
        + "A frame row holds the framed request, the caller (logon, session and administrator flag; "
        + "the control session is 1), whether a relay coordinator was composed, the reply frame "
        + "and how many shutdowns were requested. A relay case starts at QPC 10000 at 1000 Hz. "
        + "advance moves the clock in milliseconds; scm applies a native notice to the gate at the "
        + "current time as the managed suppliers do (suspend is TryApply(true), resume is "
        + "TryApply(false, newResume: true), displayOn is TryApply(false)); report is one relayed "
        + "report from the given user and session and records the result.";

    private const string VectorSid = "S-1-5-21-111-222-333-1001";
    private const long Started = 1000;

    [Fact]
    public async Task NativeLifecycleVectors_MatchManagedHandlerAndCoordinator()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeLifecycleVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeLifecycleVectors)
            : Path.Combine(output, NativeLifecycleVectors);
        JsonArray frames = await NativeLifecycleFramesAsync();
        JsonArray relay = await NativeRelayCasesAsync();
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(JsonValue.Create(NativeLifecycleComment).ToJsonString())
                .Append(",\n\"frames\":[\n")
                .AppendJoin(",\n", frames.Select(entry => entry!.ToJsonString()))
                .Append("\n],\n\"relay\":[\n")
                .AppendJoin(",\n", relay.Select(entry => entry!.ToJsonString()))
                .Append("\n]\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        JsonObject recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(new[] { "comment", "frames", "relay" }, recorded.Select(member => member.Key));
        Assert.Equal(NativeLifecycleComment, recorded["comment"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(recorded["frames"], frames), recorded["frames"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(recorded["relay"], relay), recorded["relay"]!.ToJsonString());
    }

    private static async Task<JsonArray> NativeLifecycleFramesAsync()
    {
        var time = new Clock();
        ShutdownOpenBladeRequest Shutdown(int n) =>
            new(OpenBladeIpc.ProtocolVersion, VectorId(n));
        ReportSessionPowerTransitionRequest Report(int n, long timestamp) =>
            new(OpenBladeIpc.ProtocolVersion, VectorId(n), VectorId(100 + n), Started, 1,
                timestamp, Suspending: true);
        long now = time.GetTimestamp();
        return
        [
            await FrameAsync("shutdown/system", Shutdown(1), "system", 0, false),
            await FrameAsync("shutdown/administrator", Shutdown(2), "interactive", 1, true),
            await FrameAsync("shutdown/inactive-administrator", Shutdown(3), "interactive", 2, true),
            await FrameAsync("shutdown/remote-administrator", Shutdown(4), "remoteInteractive", 1, true),
            await FrameAsync("shutdown/service-administrator", Shutdown(5), "other", 0, true),
            await FrameAsync("shutdown/user", Shutdown(6), "interactive", 1, false),
            await FrameAsync("shutdown/remote-user", Shutdown(7), "remoteInteractive", 1, false),
            await FrameAsync("shutdown/service-user", Shutdown(8), "other", 0, false),
            await FrameAsync("relay/inactive-session", Report(9, now), "interactive", 2, false),
            await FrameAsync("relay/remote", Report(10, now), "remoteInteractive", 1, false),
            await FrameAsync("relay/system", Report(11, now), "system", 0, true),
            await FrameAsync("relay/unavailable", Report(12, now), "interactive", 1, false, relay: false),
            await FrameAsync("relay/expired", Report(13, now - SessionPowerTransition.MaximumAgeMilliseconds),
                "interactive", 1, false),
            await FrameAsync("relay/accepted-unconfirmed", Report(14, now), "interactive", 1, false,
                blockPrepare: true),
        ];
    }

    private static async Task<JsonObject> FrameAsync(
        string name,
        ServiceRequest request,
        string logon,
        int session,
        bool administrator,
        bool relay = true,
        bool blockPrepare = false)
    {
        var time = new Clock();
        var runtime = new Runtime { BlockPrepare = blockPrepare };
        var shutdown = new RecordingShutdownSignal();
        ServiceSessionPowerTransitionCoordinator? coordinator = relay
            ? new ServiceSessionPowerTransitionCoordinator(runtime, new LowPowerGate(time))
            : null;
        var handler = new ControlRequestHandler(runtime, new RejectArchive(), shutdown,
            new Authority(), coordinator);
        ServiceResponse response = await handler.HandleAsync(request, Client(logon, session, administrator), default);
        runtime.PrepareRelease.TrySetResult();
        using var requestFrame = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(requestFrame, request, default);
        using var responseFrame = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(responseFrame, response, default);
        return new JsonObject
        {
            ["name"] = name,
            ["request"] = Encoding.UTF8.GetString(requestFrame.ToArray().AsSpan(4)),
            ["caller"] = new JsonObject
            {
                ["logon"] = logon,
                ["session"] = session,
                ["administrator"] = administrator,
            },
            ["relay"] = relay,
            ["response"] = Encoding.UTF8.GetString(responseFrame.ToArray().AsSpan(4)),
            ["shutdowns"] = shutdown.Requests,
        };
    }

    private static ServiceClientContext Client(string logon, int session, bool administrator) => new(
        logon == "system"
            ? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
            : new SecurityIdentifier(VectorSid),
        session,
        administrator,
        logon switch
        {
            "system" => ServiceClientLogonKind.LocalSystem,
            "interactive" => ServiceClientLogonKind.Interactive,
            "remoteInteractive" => ServiceClientLogonKind.RemoteInteractive,
            "other" => ServiceClientLogonKind.Other,
            _ => throw new InvalidDataException($"Unknown vector logon {logon}."),
        });

    private static async Task<JsonArray> NativeRelayCasesAsync()
    {
        static RelayStep At(long milliseconds) => new(Advance: milliseconds);
        static RelayStep Scm(string signal) => new(Scm: signal);
        static RelayStep Report(int instance, long sequence, bool suspending, long age = 0,
            long started = Started, int session = 1, string sid = VectorSid) =>
            new(Report: new(instance, started, sequence, age, suspending, session, sid));
        return
        [
            await RelayCaseAsync("accept/suspend-then-resume",
                Report(1, 1, true), At(1), Report(1, 2, false)),
            await RelayCaseAsync("repeat/same-sequence",
                Report(1, 1, true), At(1), Report(1, 1, true), Report(1, 1, false)),
            await RelayCaseAsync("repeat/lower-sequence",
                Report(1, 5, true), At(1), Report(1, 4, false), Report(1, 6, false)),
            await RelayCaseAsync("late/older-than-the-accepted-report",
                Report(1, 1, true), At(5), Report(1, 2, false, age: 10)),
            await RelayCaseAsync("late/same-timestamp-suspend-after-resume",
                Report(1, 1, false), Report(1, 2, true)),
            await RelayCaseAsync("late/same-timestamp-resume-after-suspend",
                Report(1, 1, true), Report(1, 2, false)),
            await RelayCaseAsync("age/at-the-limit",
                Report(1, 1, true, age: SessionPowerTransition.MaximumAgeMilliseconds)),
            await RelayCaseAsync("age/inside-the-limit",
                Report(1, 1, true, age: SessionPowerTransition.MaximumAgeMilliseconds - 1)),
            await RelayCaseAsync("age/future",
                Report(1, 1, true, age: -1)),
            await RelayCaseAsync("age/rejection-keeps-the-sequence",
                Report(1, 1, true, age: SessionPowerTransition.MaximumAgeMilliseconds), Report(1, 1, true)),
            await RelayCaseAsync("instance/older-start",
                Report(1, 1, true, started: Started + 1), At(1), Report(2, 9, false)),
            await RelayCaseAsync("instance/same-start-other-instance",
                Report(1, 1, true), At(1), Report(2, 9, false)),
            await RelayCaseAsync("instance/same-instance-other-start",
                Report(1, 1, true), At(1), Report(1, 9, false, started: Started + 1)),
            await RelayCaseAsync("instance/newer-start-replaces",
                Report(1, 7, true), At(1), Report(2, 1, false, started: Started + 1),
                At(1), Report(1, 8, true)),
            await RelayCaseAsync("instance/other-session-or-user",
                Report(1, 7, true, started: Started + 5), At(1), Report(2, 1, false, session: 2),
                At(1), Report(3, 1, true, sid: "S-1-5-21-111-222-333-1002")),
            await RelayCaseAsync("scm/older-relay-suspend",
                At(1), Scm("displayOn"), Report(1, 1, true, age: 1), Report(1, 1, true)),
            await RelayCaseAsync("scm/same-timestamp-suspend-while-clear",
                Scm("resume"), Report(1, 1, true), Report(1, 2, false)),
            await RelayCaseAsync("scm/same-timestamp-suspend-while-set",
                Scm("suspend"), Report(1, 1, true)),
            await RelayCaseAsync("scm/newer-than-the-relay",
                Report(1, 1, true), At(2), Scm("resume"), Report(1, 2, true, age: 1),
                Report(1, 3, true)),
        ];
    }

    private sealed record RelayStep(long? Advance = null, string? Scm = null, RelayReport? Report = null);

    private sealed record RelayReport(int Instance, long Started, long Sequence, long Age, bool Suspending,
        int Session, string Sid);

    private static async Task<JsonObject> RelayCaseAsync(string name, params RelayStep[] steps)
    {
        var time = new Clock();
        var gate = new LowPowerGate(time);
        var runtime = new Runtime();
        var coordinator = new ServiceSessionPowerTransitionCoordinator(runtime, gate);
        var rows = new JsonArray();
        foreach (RelayStep step in steps)
        {
            if (step.Advance is { } advance)
            {
                time.Advance(advance);
                rows.Add(new JsonObject { ["advance"] = advance });
            }
            else if (step.Scm is { } signal)
            {
                long now = time.GetTimestamp();
                _ = signal switch
                {
                    "suspend" => gate.TryApply(true, now, out _),
                    "resume" => gate.TryApply(false, now, out _, newResume: true),
                    "displayOn" => gate.TryApply(false, now, out _),
                    _ => throw new InvalidDataException($"Unknown vector signal {signal}."),
                };
                rows.Add(new JsonObject { ["scm"] = signal });
            }
            else if (step.Report is { } report)
            {
                long timestamp = time.GetTimestamp() - report.Age;
                var request = new ReportSessionPowerTransitionRequest(OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(), VectorId(500 + report.Instance), report.Started, report.Sequence,
                    timestamp, report.Suspending);
                ControlOperationResult result = await coordinator.ReportAsync(request,
                    new ServiceClientContext(new SecurityIdentifier(report.Sid), report.Session,
                        IsAdministrator: false, ServiceClientLogonKind.Interactive),
                    static () => true, default);
                rows.Add(new JsonObject
                {
                    ["report"] = new JsonObject
                    {
                        ["sid"] = report.Sid,
                        ["session"] = report.Session,
                        ["instance"] = VectorId(500 + report.Instance).ToString(),
                        ["started"] = report.Started,
                        ["sequence"] = report.Sequence,
                        ["timestamp"] = timestamp,
                        ["suspending"] = report.Suspending,
                    },
                    ["status"] = (int)result.Status,
                    ["message"] = result.Message,
                });
            }
        }
        return new JsonObject { ["name"] = name, ["steps"] = rows };
    }

    private static Guid VectorId(int n) => Guid.Parse($"aaaaaaaa-bbbb-4ccc-8ddd-{n:D12}");

    private sealed class RecordingShutdownSignal : IServiceShutdownSignal
    {
        internal int Requests { get; private set; }

        public void RequestShutdownAfterResponse() => Requests++;
    }
}
