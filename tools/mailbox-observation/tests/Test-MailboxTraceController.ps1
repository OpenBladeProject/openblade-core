$ErrorActionPreference = 'Stop'
if ('OpenBlade.MailboxObservation.Recorder' -as [type]) {
    throw 'Run this test in a fresh PowerShell process.'
}
$production = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../MailboxTraceController.cs')
$tests = @'
namespace OpenBlade.MailboxObservation
{
    public static class ControllerTests
    {
        private static readonly ActionKind[] Calibration = { ActionKind.Calibration };
        private static readonly ActionKind[] ActionBatch = { ActionKind.Calibration, ActionKind.TypeC };

        public static string[] Run(string root)
        {
            var results = new List<string>();
            var backend = new FakeBackend { StartFailureKind = "acpi", StartFailure = 5 };
            var runtime = new FakeRuntime();
            Manifest result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Status == "incomplete" && runtime.ReadyCalls == 0, "partial start blocks actions");
            Require(backend.Stopped.SequenceEqual(new ulong[] { 1 }) && backend.Owned.Count == 0, "partial start cleanup");
            Require(runtime.Markers.Count == 1 && runtime.Markers.All(marker => marker.Closed), "held marker closes on partial start");
            results.Add("partial_start_cleanup");

            backend = new FakeBackend { EnableFailureKind = "acpi" };
            runtime = new FakeRuntime();
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Single().StopReason == "enable_failed", "enable failure is recorded");
            Require(backend.Stopped.SequenceEqual(new ulong[] { 2, 1 }) && backend.Owned.Count == 0, "enable cleanup");
            Require(result.Windows.Single().Sessions.All(session => session.AfterStop.Status == 4201), "post-stop absence recorded");
            results.Add("enable_failure_cleanup");

            backend = new FakeBackend { StartFailureKind = "object", StartFailure = 183 };
            result = Observe(root, Calibration, backend, new FakeRuntime());
            Require(result.Windows.Single().StopReason == "start_failed" && backend.Stopped.Count == 0,
                "a failed start with a nonzero output is never owned");
            backend = new FakeBackend { NameCollision = true };
            result = Observe(root, Calibration, backend, new FakeRuntime());
            Require(result.Windows.Single().StopReason == "preflight_failed" && backend.NextHandle == 0 && backend.Stopped.Count == 0,
                "an existing instance is never started or stopped");
            results.Add("collision_not_owned");

            backend = new FakeBackend();
            runtime = new FakeRuntime { ObjectBytes = 192 * 1024 * 1024 };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Single().StopReason == "disk_watermark" && runtime.ReadyCalls == 0,
                "a full watermark stops before action");
            Require(backend.Stopped.Count == 2 && backend.Owned.Count == 0, "watermark cleanup");
            backend = new FakeBackend { ActualObjectBuffers = 768 };
            runtime = new FakeRuntime { ObjectBytes = 177 * 1024 * 1024 };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Single().Sessions.First().DiskWatermarkBytes == 176 * 1024 * 1024 &&
                result.Windows.Single().StopReason == "disk_watermark", "actual buffer allocation lowers the watermark");
            backend = new FakeBackend();
            runtime = new FakeRuntime { ObjectFinalBytes = 256 * 1024 * 1024 };
            result = Observe(root, Calibration, backend, runtime);
            Require(result.Windows.Single().FailureReasons.Contains("object_file_missing_empty_or_at_cap"),
                "hard-cap equality cannot pass");
            results.Add("watermark_actual_buffers_hard_cap");

            runtime = new FakeRuntime { ObjectBytes = 15_335_424, AcpiBytes = 1_507_328, ArmAction = false };
            result = Observe(root, ActionBatch, new FakeBackend(), runtime);
            Window measured = result.Windows.Single();
            Require(result.SchemaVersion == 2 && measured.Status == "captured" && runtime.ReadyCalls == 1 &&
                measured.Sessions.Select(session => session.FileCapBytes).SequenceEqual(new long[] {
                    256 * 1024 * 1024, 32 * 1024 * 1024
                }) && measured.Sessions.Select(session => session.DiskWatermarkBytes).SequenceEqual(new long?[] {
                    192 * 1024 * 1024, 24 * 1024 * 1024
                }), "v2 caps and watermarks accept the measured calibration projection");
            runtime = new FakeRuntime { AcpiBytes = 2 * 1024 * 1024 };
            result = Observe(root, ActionBatch, new FakeBackend(), runtime);
            Require(runtime.ReadyCalls == 0 && result.FailureReasons.Contains("calibration_volume_exceeds_action_budget"),
                "ACPI projection at the v2 watermark still blocks arming");
            results.Add("v2_measured_calibration_budget");

            backend = new FakeBackend { LoseAfterQueries = 3 };
            runtime = new FakeRuntime();
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Single().StopReason == "trace_loss" && runtime.ReadyCalls == 0, "loss blocks action");
            Require(backend.Stopped.Count == 2 && runtime.Markers.All(marker => marker.Closed), "loss cleanup");
            backend = new FakeBackend { StopFailureKind = "acpi" };
            result = Observe(root, ActionBatch, backend, new FakeRuntime());
            Require(result.Status == "incomplete" && backend.Stopped.SequenceEqual(new ulong[] { 2, 1 }),
                "one stop failure does not prevent attempting the other stop");
            results.Add("loss_and_stop_failure");

            backend = new FakeBackend();
            runtime = new FakeRuntime { CancelAt = 1000 };
            result = Observe(root, Calibration, backend, runtime);
            Require(result.Windows.Single().StopReason == "cancelled" && runtime.Qpc == 1000, "cancellation bounded");
            Require(backend.Stopped.Count == 2 && runtime.Markers.Count == 2 && runtime.Markers.All(marker => marker.Closed),
                "cancellation stops both sessions and closes both markers");
            results.Add("cancellation_and_marker_cleanup");

            backend = new FakeBackend();
            runtime = new FakeRuntime { FailTransientClose = true };
            result = Observe(root, Calibration, backend, runtime);
            Require(result.Windows.Single().Markers.Last().CloseQpc == null &&
                result.Windows.Single().StopReason == "recorder_error" && result.Status == "incomplete",
                "failed transient disposal is not a confirmed close");
            Require(backend.Owned.Count == 0 && runtime.Markers.First().Closed,
                "transient disposal failure still cleans up traces and held marker");
            results.Add("transient_close_failure");

            backend = new FakeBackend();
            runtime = new FakeRuntime { FailTransientValidation = true };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Single().StopReason == "recorder_error" && runtime.ReadyCalls == 0 &&
                result.Windows.Single().Markers.Count == 1 && runtime.Prepared.Count == 1,
                "transient handle validation failure cannot publish a marker or arm an action");
            Require(backend.Owned.Count == 0 && runtime.Markers.Count == 2 && runtime.Markers.All(marker => marker.Closed),
                "transient validation failure closes its marker, both traces and the held marker");
            backend = new FakeBackend();
            runtime = new FakeRuntime { FailSeed = true };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(backend.NextHandle == 0 && runtime.ReadyCalls == 0 && result.Status == "incomplete",
                "seed failure prevents any trace from starting");
            results.Add("seed_and_transient_validation_failure");

            backend = new FakeBackend();
            runtime = new FakeRuntime();
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Count == 3 && result.Windows[0].Status == "captured" &&
                result.Windows[0].EndQpc - result.Windows[0].StartQpc == 5000 &&
                result.Windows[0].PlannedDeadlineQpc == 5000, "calibration records its five-second deadline");
            Require(result.Windows[1].Kind == "baseline" && result.Windows[1].Sessions.Count == 1 &&
                result.Windows[1].StartQpc == result.Windows[1].EndQpc &&
                result.Windows[1].PlannedDeadlineQpc == null, "baseline stops immediately without a planned deadline");
            Require(result.Windows[2].EndQpc - result.Windows[2].StartQpc == 60000 &&
                result.Windows[2].StopReason == "deadline" && result.Windows[2].ReturnEdgeReminder &&
                result.Windows[2].Status == "incomplete", "no console input cannot extend action deadline");
            Require(ReferenceEquals(result.Windows[0].Markers[0], result.Windows[2].Markers[0]) &&
                result.Windows[0].Markers[0].CloseQpc >= result.Windows[2].EndQpc, "held marker spans batch");
            Require(backend.Owned.Count == 0 && runtime.Markers.Count == 3 &&
                runtime.Markers.All(marker => marker.Closed), "timeout cleanup");
            Require(runtime.Prepared.Count == 2 && runtime.Reopened.SetEquals(runtime.Prepared) &&
                result.Windows[1].Markers.Count == 1, "calibration and action reopen the seeded file and baseline does not");
            results.Add("timeout_baseline_and_held_marker");

            backend = new FakeBackend();
            runtime = new FakeRuntime {
                Inputs = new Queue<Input>(new[] {
                    new Input(ConsoleKey.D, 15500, "operator-departure"),
                    new Input(ConsoleKey.R, 31000, "operator-return")
                })
            };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Status == "captured" && result.Windows.Last().ManualMarks.Count == 2 &&
                result.Windows.Last().ManualMarks.All(mark => mark.Source == "operator" && mark.InWindow),
                "reported actions stay labeled as operator input");
            Require(result.AcpiCoverageStatus == "unresolved" &&
                result.Windows.Last().MarkerValidationStatus == "pending_offline", "no recorder coverage claim");
            results.Add("operator_marks_and_coverage_boundaries");

            backend = new FakeBackend();
            runtime = new FakeRuntime { DelayJumpAt = 14980, DelayJumpBy = 22000 };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Last().StopReason == "insufficient_departure_time" &&
                runtime.Messages.All(message => !message.StartsWith("Departure:")),
                "departure cue suppressed without time for both settle periods");
            backend = new FakeBackend();
            runtime = new FakeRuntime { Inputs = new Queue<Input>(new[] {
                new Input(ConsoleKey.D, 35000, "late-departure")
            }) };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Last().StopReason == "insufficient_return_time" &&
                result.Windows.Last().ReturnEdgeReminder &&
                runtime.Messages.All(message => !message.StartsWith("Return:")),
                "return cue suppressed without a measured tail, with a restore reminder");
            results.Add("insufficient_cue_time");

            backend = new FakeBackend();
            runtime = new FakeRuntime {
                DelayJumpAt = 64980, DelayJumpBy = 5000,
                Inputs = new Queue<Input>(new[] {
                    new Input(ConsoleKey.D, 15500, "operator-departure"),
                    new Input(ConsoleKey.R, 67000, "return-after-planned-deadline")
                })
            };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Last().PlannedDeadlineQpc == 65000 && result.Windows.Last().EndQpc == 70000 &&
                result.Windows.Last().ManualMarks.Last().Kind == "return" &&
                !result.Windows.Last().ManualMarks.Last().InWindow &&
                result.Windows.Last().ReturnEdgeReminder && result.Status == "incomplete",
                "final drain cannot admit marks after the planned deadline");
            backend = new FakeBackend();
            runtime = new FakeRuntime { Inputs = new Queue<Input>(new[] {
                new Input(ConsoleKey.D, 15500, "operator-departure"),
                new Input(ConsoleKey.R, 31000, "operator-return")
            }.Concat(Enumerable.Repeat(new Input(ConsoleKey.F1, 65000, "unrelated-key"), 32))
              .Append(new Input(ConsoleKey.Q, 65000, "cancel-at-deadline"))) };
            result = Observe(root, ActionBatch, backend, runtime);
            Require(result.Windows.Last().StopReason == "cancelled" &&
                result.Windows.Last().Status == "incomplete" && backend.Owned.Count == 0,
                "final drain honors cancellation even with otherwise valid marks");
            results.Add("deadline_overshoot_and_final_cancellation");

            Guid runId = Guid.NewGuid();
            string directory = Recorder.CreateRunDirectory(root, runId);
            string preserved = Path.Combine(directory, "held-marker.txt");
            File.WriteAllText(preserved, "preserve");
            bool rejected = false;
            try { Recorder.CreateRunDirectory(root, runId); }
            catch (IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(preserved) == "preserve", "a prior run is never overwritten");
            result = Recorder.Run(directory, runId.ToString("N"), Calibration, 60,
                new FakeBackend(), new FakeRuntime { CreateMarkerFiles = true });
            Require(result.Status == "incomplete" && result.Windows.Count == 0 &&
                File.ReadAllText(preserved) == "preserve", "preexisting marker is not overwritten");
            results.Add("no_artifact_overwrite");
            return results.ToArray();
        }

        private static Manifest Observe(string root, ActionKind[] actions, FakeBackend backend, FakeRuntime runtime)
        {
            Guid runId = Guid.NewGuid();
            runtime.Backend = backend;
            return Recorder.Run(Recorder.CreateRunDirectory(root, runId), runId.ToString("N"),
                actions, 60, backend, runtime);
        }

        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + description);
        }

        private sealed class FakeBackend : ITraceBackend
        {
            public string StartFailureKind;
            public uint StartFailure;
            public string EnableFailureKind;
            public string StopFailureKind;
            public bool NameCollision;
            public int LoseAfterQueries = int.MaxValue;
            public uint ActualObjectBuffers = 128;
            public ulong NextHandle;
            public readonly Dictionary<ulong, Session> Owned = new();
            public readonly List<ulong> Stopped = new();
            private int queries;

            public TraceStage QueryName(Session session) => new TraceStage {
                Status = NameCollision || Owned.Values.Contains(session) ? 0U : 4201U
            };
            public TraceStage Start(Session session, out ulong handle)
            {
                handle = ++NextHandle;
                if (session.Kind == StartFailureKind) return new TraceStage { Status = StartFailure };
                Owned.Add(handle, session);
                return WithCounters(session);
            }
            public TraceStage Enable(Session session, ulong handle)
            {
                Require(Owned[handle] == session, "enable uses the acquired handle");
                return new TraceStage { Status = session.Kind == EnableFailureKind ? 5U : 0U };
            }
            public TraceStage QueryHandle(Session session, ulong handle)
            {
                Require(Owned[handle] == session, "query uses the acquired handle");
                TraceStage result = WithCounters(session);
                if (++queries > LoseAfterQueries) result.EventsLost = 1;
                return result;
            }
            public TraceStage Stop(Session session, ulong handle)
            {
                Require(Owned[handle] == session, "stop uses the acquired handle");
                Stopped.Add(handle);
                if (session.Kind == StopFailureKind) return new TraceStage { Status = 5 };
                Owned.Remove(handle);
                return WithCounters(session);
            }
            private TraceStage WithCounters(Session session) => new TraceStage {
                BufferSizeKib = 64, BufferCount = session.Kind == "object" ? ActualObjectBuffers : 16,
                MaximumBuffers = session.MaximumBuffers, EventsLost = 0,
                LogBuffersLost = 0, RealtimeBuffersLost = 0, BuffersWritten = 1
            };
        }

        private sealed class FakeRuntime : IObservationRuntime
        {
            public long Frequency => 1000;
            public long Qpc { get; private set; }
            public string Utc => DateTimeOffset.UnixEpoch.AddMilliseconds(Qpc).ToString("o");
            public int Pid => 42;
            public long CancelAt = long.MaxValue;
            public bool Cancelled => Qpc >= CancelAt;
            public long ObjectBytes = 64 * 1024;
            public long AcpiBytes = 64 * 1024;
            public long ObjectFinalBytes;
            public FakeBackend Backend;
            public bool CreateMarkerFiles;
            public bool FailTransientClose;
            public bool FailTransientValidation;
            public bool FailSeed;
            public bool ArmAction = true;
            public long DelayJumpAt = long.MaxValue;
            public long DelayJumpBy;
            public int ReadyCalls;
            public readonly List<FakeMarker> Markers = new();
            public readonly HashSet<string> Prepared = new();
            public readonly HashSet<string> Reopened = new();
            public readonly List<string> Messages = new();
            public Queue<Input> Inputs = new();

            public void Delay(int milliseconds)
            {
                long previous = Qpc;
                Qpc += milliseconds;
                if (previous >= DelayJumpAt)
                {
                    Qpc += DelayJumpBy;
                    DelayJumpAt = long.MaxValue;
                }
            }
            public void Tell(string message) => Messages.Add(message);
            public bool Ready(ActionKind action) { ReadyCalls++; return ArmAction; }
            public Input? NextInput() => Inputs.TryPeek(out Input input) && input.Qpc <= Qpc ? Inputs.Dequeue() : null;
            public IDisposable OpenMarker(string path, out ulong handle)
            {
                if (CreateMarkerFiles)
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
                }
                return OpenFakeMarker(out handle);
            }
            public void PrepareTransientMarker(string path)
            {
                Require(Backend.Owned.Count == 0 && Prepared.Add(path), "seed happens once before starting either trace");
                if (FailSeed) throw new IOException("Test marker seed failed");
            }
            public IDisposable OpenTransientMarker(string path, out ulong handle)
            {
                Require(Backend.Owned.Count == 2 && Prepared.Contains(path) && Reopened.Add(path),
                    "one open of the seeded path happens while both traces are active");
                var marker = OpenFakeMarker(out handle);
                if (!FailTransientValidation)
                    return marker;
                marker.Dispose();
                throw new IOException("Test transient path validation failed");
            }
            private FakeMarker OpenFakeMarker(out ulong handle)
            {
                var marker = new FakeMarker { FailClose = FailTransientClose && Markers.Count > 0 };
                Markers.Add(marker);
                handle = (ulong)Markers.Count;
                return marker;
            }
            public long? FileLength(string path)
            {
                if (!path.Contains("-object-")) return AcpiBytes;
                if (ObjectFinalBytes != 0 && Backend.Stopped.Count > 0) return ObjectFinalBytes;
                return ObjectBytes;
            }
            public string FileHash(string path) => new string('1', 64);
        }

        private sealed class FakeMarker : IDisposable
        {
            public bool Closed;
            public bool FailClose;
            public void Dispose()
            {
                Require(!Closed, "marker closes exactly once");
                if (FailClose) throw new IOException("Test marker disposal failed");
                Closed = true;
            }
        }
    }
}
'@
Add-Type -TypeDefinition ($production + [Environment]::NewLine + $tests)

$root = Join-Path ([IO.Path]::GetTempPath()) ('openblade-mailbox-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $passed = [OpenBlade.MailboxObservation.ControllerTests]::Run($root)
    $passed | ForEach-Object { "PASS $_" }

    # Extract only the publisher. The recorder's live entry point stays uninvoked.
    $parseErrors = $null
    $recorderScript = Join-Path $PSScriptRoot '../Start-MailboxObservation.ps1'
    $syntax = [System.Management.Automation.Language.Parser]::ParseFile($recorderScript, [ref]$null, [ref]$parseErrors)
    $publisher = $syntax.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-NewJson'
    }, $false)
    if ($parseErrors.Count -ne 0 -or $null -eq $publisher) { throw 'Could not isolate the manifest publisher.' }
    . ([scriptblock]::Create($publisher.Extent.Text))
    $target = Join-Path $root 'manifest.json'
    $expectedJson = '{"status":"captured"}'
    Write-NewJson $target $expectedJson
    $rejected = $false
    try {
        Write-NewJson $target '{"status":"incomplete"}'
    } catch {
        if ($_.Exception.InnerException -isnot [IO.IOException]) { throw }
        $rejected = $true
    }
    if (-not $rejected -or [IO.File]::ReadAllText($target) -cne $expectedJson -or
        [IO.Directory]::GetFiles($root, '.manifest.json.*.tmp').Length -ne 0) {
        throw 'Manifest publication did not preserve the existing result or clean up its temporary file.'
    }
    'PASS manifest_publication_no_overwrite'
    "$($passed.Count + 1) offline tests passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $expectedParent -and
        [IO.Path]::GetFileName($resolved) -match '^openblade-mailbox-test-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
