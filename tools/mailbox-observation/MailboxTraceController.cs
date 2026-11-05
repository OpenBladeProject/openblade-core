using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace OpenBlade.MailboxObservation
{
    public enum ActionKind { Calibration, TypeC, AcPower, ThermalProfile }

    public sealed class TraceStage
    {
        public uint Status { get; set; }
        public string Utc { get; set; }
        public long Qpc { get; set; }
        public uint? BufferSizeKib { get; set; }
        public uint? BufferCount { get; set; }
        public uint? MaximumBuffers { get; set; }
        public uint? EventsLost { get; set; }
        public uint? LogBuffersLost { get; set; }
        public uint? RealtimeBuffersLost { get; set; }
        public uint? BuffersWritten { get; set; }
    }

    public sealed class Session
    {
        public string Kind { get; init; }
        public Guid ProviderGuid { get; init; }
        public ulong Keyword { get; init; }
        public byte Level { get; init; }
        public string Name { get; init; }
        public Guid InstanceGuid { get; init; }
        public string File { get; init; }
        public long FileCapBytes { get; init; }
        public uint MinimumBuffers { get; init; }
        public uint MaximumBuffers { get; init; }
        public long? DiskWatermarkBytes { get; set; }
        public long? LiveBytes { get; set; }
        public long? Bytes { get; set; }
        public string Sha256 { get; set; }
        public TraceStage BeforeStart { get; set; }
        public TraceStage Start { get; set; }
        public TraceStage Enable { get; set; }
        public TraceStage LastQuery { get; set; }
        public TraceStage Stop { get; set; }
        public TraceStage AfterStop { get; set; }
    }

    public sealed class Marker
    {
        public string Kind { get; init; }
        public int Pid { get; init; }
        public string Handle { get; init; }
        public string Path { get; init; }
        public long OpenQpc { get; init; }
        public long? CloseQpc { get; set; }
    }

    public sealed class ManualMark
    {
        public string Kind { get; init; }
        public string Source => "operator";
        public string Utc { get; init; }
        public long Qpc { get; init; }
        public bool InWindow { get; set; }
    }

    public sealed class Window
    {
        public string Action { get; init; }
        public string Kind { get; init; }
        public string Status { get; set; } = "incomplete";
        public string MarkerValidationStatus => "pending_offline";
        public string StartedUtc { get; set; }
        public string EndedUtc { get; set; }
        public long? StartQpc { get; set; }
        public long? PlannedDeadlineQpc { get; set; }
        public long? EndQpc { get; set; }
        public string StopReason { get; set; }
        public bool ReturnEdgeReminder { get; set; }
        public List<string> FailureReasons { get; } = new();
        public List<Marker> Markers { get; } = new();
        public List<ManualMark> ManualMarks { get; } = new();
        public List<Session> Sessions { get; } = new();
    }

    public sealed class Manifest
    {
        public string Schema => "openblade.mailbox-observation";
        public int SchemaVersion => 1;
        public string Architecture => "x64";
        public string RunId { get; init; }
        public string CreatedUtc { get; init; }
        public long QpcFrequency { get; init; }
        public string[] Actions { get; init; }
        public int WindowSeconds { get; init; }
        public string AcpiCoverageStatus => "unresolved";
        public string Status { get; set; } = "incomplete";
        public List<string> FailureReasons { get; } = new();
        public List<Window> Windows { get; } = new();
    }

    public interface ITraceBackend
    {
        TraceStage QueryName(Session session);
        TraceStage Start(Session session, out ulong handle);
        TraceStage Enable(Session session, ulong handle);
        TraceStage QueryHandle(Session session, ulong handle);
        TraceStage Stop(Session session, ulong handle);
    }

    public readonly record struct Input(ConsoleKey Key, long Qpc, string Utc);

    public interface IObservationRuntime
    {
        long Frequency { get; }
        long Qpc { get; }
        string Utc { get; }
        int Pid { get; }
        bool Cancelled { get; }
        void Delay(int milliseconds);
        void Tell(string message);
        bool Ready(ActionKind action);
        Input? NextInput();
        IDisposable OpenMarker(string path, out ulong handle);
        long? FileLength(string path);
        string FileHash(string path);
    }

    public static class Recorder
    {
        private const long MiB = 1024 * 1024;
        private const uint NotFound = 4201;
        private const int CalibrationSeconds = 5;
        private const int BeforeDepartureSeconds = 10;
        private const int AfterEachEdgeSeconds = 15;
        private const int OperatorResponseSeconds = 3;
        private static readonly Guid AcpiProvider = new("c514638f-7723-485b-bcfc-96565d735d4a");
        private static readonly Guid ObjectProvider = new("febd7460-3d1d-47eb-af49-c9eeb1e146f2");

        public static void Validate(ActionKind[] actions, int windowSeconds)
        {
            if (actions == null || actions.Length == 0 || actions[0] != ActionKind.Calibration ||
                actions.Any(action => !Enum.IsDefined(action)) || actions.Distinct().Count() != actions.Length)
                throw new ArgumentException("Choose distinct reviewed actions with Calibration first.");
            if (windowSeconds < 50 || windowSeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(windowSeconds), "Action windows must be 50–60 seconds.");
        }

        public static string CreateRunDirectory(string outputRoot, Guid runId)
        {
            var parent = new DirectoryInfo(Path.GetFullPath(outputRoot));
            for (DirectoryInfo current = parent; current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Trace output cannot use a reparse point.");
            string directory = Path.Combine(parent.FullName, runId.ToString("N"));
            if (Path.Exists(directory))
                throw new IOException("Trace run directory already exists.");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Trace run directory cannot be a reparse point.");
            // Reserve this name once, including in a race with another recorder.
            using var reservation = new FileStream(Path.Combine(directory, ".run"), FileMode.CreateNew, FileAccess.Write);
            if (Directory.EnumerateFileSystemEntries(directory).Count() != 1)
                throw new IOException("Trace run directory is not empty.");
            return directory;
        }

        public static Manifest Run(string directory, string runId, ActionKind[] actions, int windowSeconds,
            ITraceBackend backend, IObservationRuntime runtime)
        {
            Validate(actions, windowSeconds);
            var manifest = new Manifest {
                RunId = runId, CreatedUtc = runtime.Utc, QpcFrequency = runtime.Frequency,
                Actions = actions.Select(action => action.ToString()).ToArray(), WindowSeconds = windowSeconds
            };
            IDisposable held = null;
            Marker heldMarker = null;
            try
            {
                heldMarker = OpenMarker(directory, "held-marker.txt", "held", runtime, out held);
                Window calibration = Record(directory, ActionKind.Calibration, "calibration", CalibrationSeconds,
                    heldMarker, backend, runtime);
                manifest.Windows.Add(calibration);
                if (calibration.Status != "captured")
                {
                    manifest.FailureReasons.Add("calibration_incomplete");
                    return manifest;
                }
                if (actions.Length == 1)
                {
                    manifest.Status = "captured";
                    return manifest;
                }
                foreach (Session session in calibration.Sessions)
                {
                    if (session.LiveBytes.GetValueOrDefault() * windowSeconds / CalibrationSeconds >=
                        session.DiskWatermarkBytes.GetValueOrDefault())
                    {
                        manifest.FailureReasons.Add("calibration_volume_exceeds_action_budget");
                        return manifest;
                    }
                }

                bool baselineCaptured = false;
                foreach (ActionKind action in actions.Skip(1))
                {
                    if (runtime.Cancelled || !runtime.Ready(action))
                    {
                        manifest.FailureReasons.Add("action_not_armed");
                        return manifest;
                    }
                    if (!baselineCaptured)
                    {
                        Window baseline = Record(directory, action, "baseline", 0, heldMarker, backend, runtime);
                        manifest.Windows.Add(baseline);
                        if (baseline.Status != "captured")
                        {
                            manifest.FailureReasons.Add("baseline_incomplete");
                            return manifest;
                        }
                        baselineCaptured = true;
                    }
                    Window window = Record(directory, action, "action", windowSeconds, heldMarker, backend, runtime);
                    manifest.Windows.Add(window);
                    if (window.Status != "captured")
                    {
                        manifest.FailureReasons.Add("action_incomplete");
                        return manifest;
                    }
                }
                manifest.Status = "captured";
            }
            catch (Exception ex)
            {
                manifest.FailureReasons.Add("recorder_error: " + ex.GetType().Name);
            }
            finally
            {
                if (held != null)
                {
                    try { held.Dispose(); heldMarker.CloseQpc = runtime.Qpc; }
                    catch (Exception ex)
                    {
                        manifest.Status = "incomplete";
                        manifest.FailureReasons.Add("held_marker_close_error: " + ex.GetType().Name);
                    }
                }
            }
            return manifest;
        }

        private static Window Record(string directory, ActionKind action, string kind, int seconds, Marker held,
            ITraceBackend backend, IObservationRuntime runtime)
        {
            var window = new Window { Action = action.ToString(), Kind = kind };
            window.Markers.Add(held);
            window.Sessions.Add(NewSession(kind, action, true));
            if (kind != "baseline")
                window.Sessions.Add(NewSession(kind, action, false));
            var owned = new List<(Session Session, ulong Handle)>();
            bool departurePrompted = false;
            long setupStart = runtime.Qpc;
            long? plannedDeadline = null;
            try
            {
                foreach (Session session in window.Sessions)
                {
                    if (runtime.Cancelled)
                    {
                        window.StopReason = "cancelled";
                        return window;
                    }
                    if (Path.Exists(Path.Combine(directory, session.File)))
                        throw new IOException("Trace output already exists.");
                    session.BeforeStart = Stamp(backend.QueryName(session), runtime);
                    if (session.BeforeStart.Status != NotFound)
                    {
                        window.StopReason = "preflight_failed";
                        window.FailureReasons.Add(session.Kind + "_preflight");
                        return window;
                    }
                    session.Start = backend.Start(session, out ulong handle);
                    if (session.Start.Status == 0 && handle != 0)
                        owned.Add((session, handle));
                    Stamp(session.Start, runtime);
                    if (session.Start.Status != 0 || handle == 0)
                    {
                        window.StopReason = "start_failed";
                        window.FailureReasons.Add(session.Kind + "_start");
                        return window;
                    }
                    session.Enable = Stamp(backend.Enable(session, handle), runtime);
                    if (session.Enable.Status != 0)
                    {
                        window.StopReason = "enable_failed";
                        window.FailureReasons.Add(session.Kind + "_enable");
                        return window;
                    }
                }

                if (!CheckSessions(directory, owned, backend, runtime, window))
                    return window;
                if (runtime.Cancelled)
                {
                    window.StopReason = "cancelled";
                    return window;
                }
                window.StartQpc = runtime.Qpc;
                window.StartedUtc = runtime.Utc;
                if (kind == "baseline")
                {
                    window.StopReason = "snapshot";
                    return window;
                }
                long start = window.StartQpc.Value;
                long deadline = Math.Min(start + seconds * runtime.Frequency, setupStart + 60 * runtime.Frequency);
                plannedDeadline = deadline;
                window.PlannedDeadlineQpc = deadline;

                Marker pulse = OpenMarker(directory, kind + "-" + action + "-transient-marker.txt",
                    "transient", runtime, out IDisposable transient);
                window.Markers.Add(pulse);
                transient.Dispose();
                pulse.CloseQpc = runtime.Qpc;

                runtime.Tell(kind == "calibration" ? "Calibration recording active. Leave the hardware state as it is."
                    : "Recording active. Wait for the departure cue. D and R mark completed edges; Q cancels recording.");
                long nextQuery = start;
                bool returnPrompted = false;
                while (runtime.Qpc < deadline)
                {
                    if (runtime.Cancelled || !ReadMarks(window, runtime, deadline))
                    {
                        window.StopReason = "cancelled";
                        return window;
                    }
                    long now = runtime.Qpc;
                    if (now >= nextQuery)
                    {
                        if (!CheckSessions(directory, owned, backend, runtime, window))
                            return window;
                        nextQuery = now + Math.Max(1, runtime.Frequency / 4);
                    }
                    now = runtime.Qpc;
                    if (kind == "action" && now < deadline)
                    {
                        if (!departurePrompted && now >= start + BeforeDepartureSeconds * runtime.Frequency)
                        {
                            if (deadline - now < 2 * (AfterEachEdgeSeconds + OperatorResponseSeconds) * runtime.Frequency)
                            {
                                window.StopReason = "insufficient_departure_time";
                                window.FailureReasons.Add("action_departure_window_too_short");
                                return window;
                            }
                            departurePrompted = true;
                            runtime.Tell("Departure: perform only the agreed " + action + " action now, then press D.");
                        }
                        ManualMark departure = window.ManualMarks.FirstOrDefault(mark => mark.Kind == "departure");
                        if (!returnPrompted && departure != null &&
                            now >= departure.Qpc + AfterEachEdgeSeconds * runtime.Frequency &&
                            !window.ManualMarks.Any(mark => mark.Kind == "return"))
                        {
                            if (deadline - now < (AfterEachEdgeSeconds + OperatorResponseSeconds) * runtime.Frequency)
                            {
                                window.StopReason = "insufficient_return_time";
                                window.FailureReasons.Add("action_return_window_too_short");
                                return window;
                            }
                            returnPrompted = true;
                            runtime.Tell("Return: restore the agreed baseline using the supported action, then press R.");
                        }
                    }
                    runtime.Delay(20);
                }
                window.StopReason = "deadline";
            }
            catch (Exception ex)
            {
                window.StopReason = "recorder_error";
                window.FailureReasons.Add(ex.GetType().Name);
            }
            finally
            {
                // DCEnd may be emitted during Stop. These timestamps bound the
                // shared active interval, not the separately decoded rundown.
                window.EndQpc = runtime.Qpc;
                window.EndedUtc = runtime.Utc;
                for (int i = owned.Count - 1; i >= 0; --i)
                    StopOwned(owned[i], backend, runtime, window);
                long marksEnd = Math.Min(window.EndQpc.Value, plannedDeadline ?? window.EndQpc.Value);
                try
                {
                    // Preserve already queued late marks, without waiting for further input.
                    if (!ReadMarks(window, runtime, marksEnd, limit: 64) || runtime.Cancelled)
                    {
                        if (window.StopReason == "deadline" || window.StopReason == "snapshot" || window.StopReason == null)
                            window.StopReason = "cancelled";
                        window.FailureReasons.Add("cancelled");
                    }
                    else if (window.StopReason == "cancelled")
                        window.FailureReasons.Add("cancelled");
                    if (kind == "action")
                        CheckActionMarks(window, runtime, marksEnd, departurePrompted);
                }
                catch (Exception ex)
                {
                    window.FailureReasons.Add("operator_input_error: " + ex.GetType().Name);
                }
                CheckFiles(directory, window, runtime);
                if (window.FailureReasons.Count == 0 &&
                    (window.StopReason == "snapshot" || window.StopReason == "deadline"))
                    window.Status = "captured";
            }
            return window;
        }

        private static bool ReadMarks(Window window, IObservationRuntime runtime, long deadline, int limit = 32)
        {
            for (int i = 0; i < limit && runtime.NextInput() is Input input; i++)
            {
                if (input.Key == ConsoleKey.Q || input.Key == ConsoleKey.Escape)
                    return false;
                if (window.Kind == "action" && (input.Key == ConsoleKey.D || input.Key == ConsoleKey.R))
                    window.ManualMarks.Add(new ManualMark {
                        Kind = input.Key == ConsoleKey.D ? "departure" : "return", Qpc = input.Qpc, Utc = input.Utc,
                        InWindow = input.Qpc >= window.StartQpc && input.Qpc <= deadline
                    });
            }
            return true;
        }

        private static void CheckActionMarks(Window window, IObservationRuntime runtime, long marksEnd, bool departurePrompted)
        {
            foreach (ManualMark mark in window.ManualMarks)
                mark.InWindow &= mark.Qpc <= marksEnd;
            var departure = window.ManualMarks.Where(mark => mark.InWindow && mark.Kind == "departure").ToArray();
            var returned = window.ManualMarks.Where(mark => mark.InWindow && mark.Kind == "return").ToArray();
            if (window.StartQpc == null || departure.Length != 1 || returned.Length != 1 ||
                departure[0].Qpc - window.StartQpc.Value < BeforeDepartureSeconds * runtime.Frequency ||
                returned[0].Qpc - departure[0].Qpc < AfterEachEdgeSeconds * runtime.Frequency ||
                marksEnd - returned[0].Qpc < AfterEachEdgeSeconds * runtime.Frequency)
                window.FailureReasons.Add("action_marks_missing_ambiguous_or_outside_timing");
            bool clearReturn = departure.Length == 1 && returned.Length == 1 && returned[0].Qpc > departure[0].Qpc;
            bool endedEarly = window.StartQpc != null && window.StopReason != "deadline";
            if ((endedEarly || departurePrompted || departure.Length != 0) && !clearReturn)
            {
                window.ReturnEdgeReminder = true;
                runtime.Tell("Recording ended before a clear return mark. If the state changed, restore the agreed baseline using the supported control. This trace cannot verify restoration.");
            }
        }

        private static bool CheckSessions(string directory, List<(Session Session, ulong Handle)> owned,
            ITraceBackend backend, IObservationRuntime runtime, Window window)
        {
            foreach (var entry in owned)
            {
                Session session = entry.Session;
                session.LastQuery = Stamp(backend.QueryHandle(session, entry.Handle), runtime);
                if (session.LastQuery.Status != 0)
                {
                    window.StopReason = "session_unavailable";
                    window.FailureReasons.Add(session.Kind + "_query");
                    return false;
                }
                if (LostOrUnknown(session.LastQuery))
                {
                    window.StopReason = "trace_loss";
                    window.FailureReasons.Add(session.Kind + "_loss_or_unknown_counters");
                    return false;
                }
                long bufferCount = Math.Max(session.MaximumBuffers,
                    Math.Max(session.LastQuery.BufferCount.GetValueOrDefault(), session.LastQuery.MaximumBuffers.GetValueOrDefault()));
                long bufferSize = Math.Max(64, session.LastQuery.BufferSizeKib.GetValueOrDefault()) * 1024L;
                long reserve = session.Kind == "object" ? 32 * MiB : 4 * MiB;
                session.DiskWatermarkBytes = bufferCount >= session.FileCapBytes / bufferSize
                    ? 0 : Math.Max(0, session.FileCapBytes - reserve - bufferCount * bufferSize);
                session.LiveBytes = runtime.FileLength(Path.Combine(directory, session.File));
                if (session.LiveBytes == null || session.LiveBytes >= session.DiskWatermarkBytes)
                {
                    window.StopReason = "disk_watermark";
                    window.FailureReasons.Add(session.Kind + "_disk_watermark_or_unreadable_size");
                    return false;
                }
            }
            return true;
        }

        private static void StopOwned((Session Session, ulong Handle) owned, ITraceBackend backend,
            IObservationRuntime runtime, Window window)
        {
            Session session = owned.Session;
            try
            {
                session.Stop = Stamp(backend.Stop(session, owned.Handle), runtime);
                if (session.Stop.Status != 0 || LostOrUnknown(session.Stop))
                    window.FailureReasons.Add(session.Kind + "_stop_failed_or_loss");
            }
            catch (Exception ex) { window.FailureReasons.Add(session.Kind + "_stop_error: " + ex.GetType().Name); }
            try
            {
                session.AfterStop = Stamp(backend.QueryName(session), runtime);
                if (session.AfterStop.Status != NotFound)
                    window.FailureReasons.Add(session.Kind + "_session_absence_unconfirmed");
            }
            catch (Exception ex) { window.FailureReasons.Add(session.Kind + "_post_query_error: " + ex.GetType().Name); }
        }

        private static void CheckFiles(string directory, Window window, IObservationRuntime runtime)
        {
            foreach (Session session in window.Sessions.Where(session => session.Start?.Status == 0))
            {
                try
                {
                    string path = Path.Combine(directory, session.File);
                    session.Bytes = runtime.FileLength(path);
                    if (session.Bytes == null || session.Bytes <= 0 || session.Bytes >= session.FileCapBytes)
                        window.FailureReasons.Add(session.Kind + "_file_missing_empty_or_at_cap");
                    if (session.Bytes > 0 && session.Bytes <= session.FileCapBytes && session.AfterStop?.Status == NotFound)
                        session.Sha256 = runtime.FileHash(path);
                }
                catch (Exception ex) { window.FailureReasons.Add(session.Kind + "_file_error: " + ex.GetType().Name); }
            }
        }

        private static bool LostOrUnknown(TraceStage stage) =>
            stage.EventsLost != 0 || stage.LogBuffersLost != 0 || stage.RealtimeBuffersLost != 0;

        private static Marker OpenMarker(string directory, string relativePath, string kind, IObservationRuntime runtime,
            out IDisposable file)
        {
            long open = runtime.Qpc;
            file = runtime.OpenMarker(Path.Combine(directory, relativePath), out ulong handle);
            return new Marker { Kind = kind, Path = relativePath, Pid = runtime.Pid,
                Handle = $"0x{handle:X}", OpenQpc = open };
        }

        private static TraceStage Stamp(TraceStage stage, IObservationRuntime runtime)
        {
            stage.Qpc = runtime.Qpc;
            stage.Utc = runtime.Utc;
            return stage;
        }

        private static Session NewSession(string kind, ActionKind action, bool system)
        {
            Guid guid = Guid.NewGuid();
            string provider = system ? "object" : "acpi";
            return new Session {
                Kind = provider, ProviderGuid = system ? ObjectProvider : AcpiProvider,
                Keyword = system ? 2UL : 1UL, Level = system ? (byte)5 : (byte)4,
                InstanceGuid = guid, Name = "OpenBladeMailbox-" + guid.ToString("N"),
                File = $"{kind}-{action}-{provider}-{guid:N}.etl",
                FileCapBytes = (system ? 128 : 16) * MiB,
                MinimumBuffers = system ? 128U : 16U, MaximumBuffers = system ? 512U : 64U
            };
        }
    }

    // Console input runs on a background thread. The sampler never waits on input.
    public sealed class ConsoleRuntime : IObservationRuntime, IDisposable
    {
        private readonly ConcurrentQueue<Input> inputs = new();
        private readonly Thread reader;
        private volatile bool stopped;
        private volatile bool cancelled;

        public ConsoleRuntime()
        {
            Console.CancelKeyPress += OnCancel;
            reader = new Thread(ReadConsole) { IsBackground = true, Name = "MailboxObservationInput" };
            reader.Start();
        }

        public long Frequency => Stopwatch.Frequency;
        public long Qpc => Stopwatch.GetTimestamp();
        public string Utc => DateTimeOffset.UtcNow.ToString("o");
        public int Pid => Environment.ProcessId;
        public bool Cancelled => cancelled;
        public void Delay(int milliseconds) => Thread.Sleep(milliseconds);
        public void Tell(string message) => Console.Error.WriteLine(message);
        public Input? NextInput() => inputs.TryDequeue(out Input input) ? input : null;

        public bool Ready(ActionKind action)
        {
            inputs.Clear();
            Tell("No trace is active. Prepare for " + action + ". Press Enter to arm, or Q to end the batch.");
            while (!Cancelled)
            {
                Input? input = NextInput();
                if (input?.Key == ConsoleKey.Enter) return true;
                if (input?.Key == ConsoleKey.Q || input?.Key == ConsoleKey.Escape) return false;
                Delay(20);
            }
            return false;
        }

        public IDisposable OpenMarker(string path, out ulong handle)
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            handle = unchecked((ulong)file.SafeFileHandle.DangerousGetHandle().ToInt64());
            return file;
        }

        public long? FileLength(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }

        public string FileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private void OnCancel(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            cancelled = true;
        }

        private void ReadConsole()
        {
            try
            {
                while (!stopped)
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKey key = Console.ReadKey(intercept: true).Key;
                        if (inputs.Count < 64)
                            inputs.Enqueue(new Input(key, Qpc, Utc));
                        else
                            cancelled = true;
                    }
                    Thread.Sleep(20);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                cancelled = true;
            }
        }

        public void Dispose()
        {
            stopped = true;
            Console.CancelKeyPress -= OnCancel;
            reader.Join(100);
        }
    }

    public sealed class NativeTraceBackend : ITraceBackend
    {
        private const uint Query = 0;
        private const uint StopSession = 1;
        private const uint EnableProvider = 1;
        private const uint Sequential = 1;
        private const uint SystemLogger = 0x02000000;
        private const uint TracedGuid = 0x00020000;
        private const int StringBytes = 2048;
        private const int HeaderSize = 120;
        private readonly string directory;

        public NativeTraceBackend(string directory)
        {
            this.directory = Path.GetFullPath(directory);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                Marshal.SizeOf<Properties>() != HeaderSize ||
                Marshal.OffsetOf<Properties>(nameof(Properties.BufferSize)).ToInt32() != 48 ||
                Marshal.OffsetOf<Properties>(nameof(Properties.LogFileNameOffset)).ToInt32() != 112 ||
                Marshal.OffsetOf<Properties>(nameof(Properties.LoggerNameOffset)).ToInt32() != 116)
                throw new PlatformNotSupportedException("Requires the reviewed x64 ETW properties layout.");
        }

        public TraceStage Start(Session session, out ulong handle)
        {
            IntPtr buffer = Allocate(session);
            try { return GetStage(StartTraceW(out handle, session.Name, buffer), buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public TraceStage Enable(Session session, ulong handle)
        {
            Guid provider = session.ProviderGuid;
            return new TraceStage { Status = EnableTraceEx2(handle, ref provider, EnableProvider,
                session.Level, session.Keyword, 0, 2000, IntPtr.Zero) };
        }

        public TraceStage QueryName(Session session) => Control(session, 0, session.Name, Query);
        public TraceStage QueryHandle(Session session, ulong handle) => Control(session, handle, null, Query);
        public TraceStage Stop(Session session, ulong handle) => Control(session, handle, null, StopSession);

        private TraceStage Control(Session session, ulong handle, string name, uint code)
        {
            IntPtr buffer = Allocate(session);
            try { return GetStage(ControlTraceW(handle, name, buffer, code), buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private IntPtr Allocate(Session session)
        {
            string path = Path.Combine(directory, session.File);
            byte[] name = Encoding.Unicode.GetBytes(session.Name + "\0");
            byte[] file = Encoding.Unicode.GetBytes(path + "\0");
            if (name.Length > StringBytes || file.Length > StringBytes)
                throw new IOException("Trace name or path is too long.");
            int capacity = HeaderSize + 2 * StringBytes;
            var fields = new Properties {
                WnodeBufferSize = (uint)capacity, WnodeGuid = session.InstanceGuid,
                WnodeClientContext = 1, WnodeFlags = TracedGuid, BufferSize = 64,
                MinimumBuffers = session.MinimumBuffers, MaximumBuffers = session.MaximumBuffers,
                MaximumFileSize = (uint)(session.FileCapBytes / (1024 * 1024)),
                LogFileMode = Sequential | (session.Kind == "object" ? SystemLogger : 0), FlushTimer = 1,
                LoggerNameOffset = HeaderSize, LogFileNameOffset = HeaderSize + StringBytes
            };
            IntPtr buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                Marshal.Copy(new byte[capacity], 0, buffer, capacity);
                Marshal.StructureToPtr(fields, buffer, false);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, HeaderSize), name.Length);
                Marshal.Copy(file, 0, IntPtr.Add(buffer, HeaderSize + StringBytes), file.Length);
                return buffer;
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }

        private static TraceStage GetStage(uint status, IntPtr buffer)
        {
            if (status != 0) return new TraceStage { Status = status };
            Properties fields = Marshal.PtrToStructure<Properties>(buffer);
            return new TraceStage {
                Status = status, BufferSizeKib = fields.BufferSize, BufferCount = fields.NumberOfBuffers,
                MaximumBuffers = fields.MaximumBuffers, BuffersWritten = fields.BuffersWritten,
                EventsLost = fields.EventsLost, LogBuffersLost = fields.LogBuffersLost,
                RealtimeBuffersLost = fields.RealTimeBuffersLost
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Properties
        {
            public uint WnodeBufferSize, WnodeProviderId;
            public ulong WnodeHistoricalContext;
            public long WnodeTimeStamp;
            public Guid WnodeGuid;
            public uint WnodeClientContext, WnodeFlags;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint StartTraceW(out ulong handle, string instance, IntPtr properties);
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint ControlTraceW(ulong handle, string instance, IntPtr properties, uint code);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level,
            ulong matchAny, ulong matchAll, uint timeoutMs, IntPtr enableParameters);
    }
}
