using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Owns worker lifetime without sharing its subscribed driver file object.</summary>
internal static class SnapTapDriverSupervisor
{
    internal const string WorkerMode = "--snap-tap-driver-worker";
    internal static string HeartbeatName(int parent) => $"Local\\OpenBlade.SnapTap.Heartbeat.{parent}";
    internal static string StartGateName(int parent) => $"Local\\OpenBlade.SnapTap.Start.{parent}";

    internal static int Run(Func<bool> shouldStop)
    {
        // Helpers bypass the main agent's logging setup. Use a separate file
        // that the diagnostic exporter already includes, without logging from
        // the input loop or contending with the agent's active log.
        using IDisposable logging = PersistentTraceLog.RegisterDefault(
            "OpenBlade.SnapTap", OpenBladeProductVersion.Read(typeof(SnapTapDriverSupervisor).Assembly));
        string stage = "Admission";
        RazerSnapTapDriverEndpoint? endpoint = null;
        bool reconciliationMayHaveSubmitted = false;
        DiagnosticFailure? observedWorkerFailure = null;
        try
        {
            var admission = new RazerSnapTapDriverAdmission();
            if (!admission.TryGetEndpointWithFailure(
                    out endpoint, out var admissionFailure, out DiagnosticFailure? admissionDetail))
            {
                int exitCode = AdmissionExitCode(admissionFailure);
                SnapTapRuntimeState state = AdmissionState(exitCode);
                PublishTerminal(endpoint?.Profile, state, admissionDetail
                    ?? SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Supervisor, "Admission", stage,
                        "AdmissionRejected"), exitCode);
                return exitCode;
            }
            Trace.TraceInformation("SnapTap driver admitted: {0}; PID={1}; provenance={2}.",
                endpoint.Profile.ProductName, endpoint.Profile.ProductId,
                endpoint.Profile.PhysicallyValidated ? "PhysicallyValidated" : "SourceReviewedExperimental");
            // This independent CreateFile remains unsubscribed. Duplicating the
            // worker's handle would retain its subscription after worker death.
            stage = "GuardianOpen";
            using var guardian = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint));
            stage = "BaselineEnumeration";
            IReadOnlyList<byte[]> baseline = guardian.ReadHooks();
            if (baseline.Any(IsAdHook))
            {
                return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                    SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                        "GuardHooks", stage, "PreexistingHook"), 1);
            }
            stage = "OwnershipJournal";
            string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade", "snaptap-driver-ownership.json");
            string boot = ReadBootIdentity();
            // An interrupted supervisor cannot establish ownership from coincidentally
            // matching global records. Keep that case closed for explicit recovery.
            if (File.Exists(journal))
            {
                using var previousFile = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (previousFile.Length > 131072)
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "OwnershipJournal", stage, "JournalTooLarge"), 4);
                }
                OwnershipJournal? previous = JsonSerializer.Deserialize<OwnershipJournal>(previousFile);
                if (!CanDiscardJournal(previous?.BootIdentity, boot, baseline))
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "OwnershipJournal", stage, "RecoveryRequired"), 4);
                }
                previousFile.Dispose();
                File.Delete(journal);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new OwnershipJournal(boot, baseline.ToArray()));
                stream.Flush(true);
            }
            stage = "WorkerInfrastructure";
            using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, HeartbeatName(Environment.ProcessId));
            using var startGate = new EventWaitHandle(false, EventResetMode.ManualReset, StartGateName(Environment.ProcessId));
            startGate.Reset();
            using var job = new SnapTapWorkerJob();
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                SnapTapInputProcess.StopEventName(Environment.ProcessId));
            stop.Reset();
            string executable = Environment.ProcessPath!;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            start.ArgumentList.Add(WorkerMode);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process? worker = null;
            bool clean = false;
            try
            {
                stage = "WorkerLaunch";
                DateTimeOffset workerStartedAt = DateTimeOffset.UtcNow;
                worker = Process.Start(start) ?? throw new InvalidOperationException("The keyboard worker could not start.");
                job.Assign(worker);
                startGate.Set();
                long lastHeartbeat = Environment.TickCount64;
                bool started = false;
                while (!worker.HasExited)
                {
                    if (heartbeat.WaitOne(50))
                    {
                        lastHeartbeat = Environment.TickCount64;
                        started = true;
                    }
                    bool requestedStop = shouldStop();
                    bool timedOut = Environment.TickCount64 - lastHeartbeat > (started ? 1500 : 10000);
                    if (requestedStop || timedOut)
                    {
                        if (timedOut)
                        {
                            observedWorkerFailure = SnapTapDiagnosticFailure.Create(
                                SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                                started ? "InputLoop" : "WorkerStartup", "HeartbeatTimeout", started);
                        }
                        stop.Set();
                        if (!worker.WaitForExit(1500))
                        {
                            worker.Kill();
                            if (!worker.WaitForExit(1500))
                            {
                                return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                                    observedWorkerFailure ?? SnapTapDiagnosticFailure.Create(
                                        SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                                        "WorkerExit", "WorkerDidNotExit", started), 1);
                            }
                        }
                        break;
                    }
                }
                // Prove the exact child exited before touching global hook records.
                if (!worker.HasExited)
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "SuperviseWorker", "WorkerExit", "WorkerDidNotExit"), 1);
                }
                observedWorkerFailure ??= MatchingTerminalFailure(
                    SnapTapStatusStore.Read(), worker.Id, workerStartedAt);
                if (worker.ExitCode != 0 && observedWorkerFailure is null)
                {
                    observedWorkerFailure = SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                        "WorkerExit", worker.ExitCode == 3 ? "WorkerAdmissionUnavailable" : "UnexpectedExit",
                        processExitCode: worker.ExitCode);
                }
                stage = "Reconciliation";
                IReadOnlyList<byte[]> hooks = guardian.ReadHooks();
                if (!SameUnownedHooks(baseline, hooks))
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "ReconcileHooks", stage, "HookOwnershipChanged", true, observedWorkerFailure), 1);
                }
                foreach (ushort scan in new ushort[] { 0x1E, 0x20 })
                {
                    byte[] owned = RazerSnapTapDriverProtocol.EncodeHook(scan);
                    if (hooks.Any(record => record.AsSpan().SequenceEqual(owned)))
                    {
                        // A fresh read reconciles hook ownership after child closure.
                        // Never synthesize a guessed key release or reset the driver.
                        reconciliationMayHaveSubmitted = true;
                        guardian.ClearHook(RazerSnapTapDriverProtocol.EncodeClearHook(scan));
                        hooks = guardian.ReadHooks();
                        reconciliationMayHaveSubmitted = false;
                        if (hooks.Any(record => record.AsSpan().SequenceEqual(owned))
                            || !SameUnownedHooks(baseline, hooks))
                        {
                            return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                                SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                                    "ReconcileHooks", stage, "HookCleanupUnconfirmed", true, observedWorkerFailure), 1);
                        }
                    }
                }
                clean = hooks.Count == baseline.Count && SameUnownedHooks(baseline, hooks)
                    && worker.ExitCode is 0 or 3;
                int exitCode = clean ? worker.ExitCode : 4;
                if (!clean)
                {
                    DiagnosticFailure cleanupFailure = SnapTapDiagnosticFailure.Create(
                        SnapTapDiagnosticFailure.Supervisor, "ReconcileHooks", stage,
                        "RecoveryRequired", observedWorkerFailure?.MayHaveSubmitted == true, observedWorkerFailure);
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                        cleanupFailure, exitCode);
                }
                if (observedWorkerFailure is not null)
                {
                    return PublishTerminal(endpoint.Profile,
                        exitCode == 3 ? SnapTapRuntimeState.Unavailable : SnapTapRuntimeState.Failed,
                        observedWorkerFailure, exitCode);
                }
                return exitCode;
            }
            finally
            {
                // Exceptions in admission, enumeration or recovery must also close
                // the mutator. Do not leave it alive behind a failed supervisor.
                if (worker is not null)
                {
                    if (!worker.HasExited)
                    {
                        stop.Set();
                        if (!worker.WaitForExit(1500))
                        {
                            worker.Kill();
                            worker.WaitForExit(1500);
                        }
                    }
                    worker.Dispose();
                }
                if (clean)
                {
                    File.Delete(journal);
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticFailure failure = SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Supervisor, "Run", stage, "UnexpectedFailure",
                exception, reconciliationMayHaveSubmitted
                    || observedWorkerFailure?.MayHaveSubmitted == true,
                observedWorkerFailure);
            return PublishTerminal(endpoint?.Profile, SnapTapRuntimeState.Failed, failure, 1);
        }
    }

    internal static DiagnosticFailure? MatchingTerminalFailure(
        SnapTapRuntimeStatus? status,
        int processId,
        DateTimeOffset startedAt) => status is not null
            && status.ProcessId == processId
            && status.UpdatedAt >= startedAt
            && SnapTapStatusStore.IsTerminalState(status.State)
                ? status.Failure : null;

    private static int PublishTerminal(
        RazerSnapTapDriverProfile? profile,
        SnapTapRuntimeState state,
        DiagnosticFailure failure,
        int exitCode)
    {
        TraceTerminalFailure(failure, exitCode);
        try
        {
            SnapTapStatusStore.Write(new(
                state,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                profile?.ProductName,
                profile is { PhysicallyValidated: false },
                failure));
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("SnapTap terminal status publication failed: {0}.", exception.GetType().Name);
        }
        return exitCode;
    }

    private static void TraceTerminalFailure(DiagnosticFailure failure, int exitCode)
    {
        int depth = 0;
        for (DiagnosticFailure? current = failure; current is not null && depth < 4; current = current.Cause)
        {
            Trace.TraceWarning(
                "SnapTap terminal failure: depth={0}; component={1}; operation={2}; stage={3}; reason={4}; mayHaveSubmitted={5}; exception={6}; hresult={7}; native={8}; exitCode={9}.",
                depth, current.Component, current.Operation, current.Stage, current.Reason,
                current.MayHaveSubmitted, current.ExceptionType ?? "None",
                current.HResult?.ToString(CultureInfo.InvariantCulture) ?? "None",
                current.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "None",
                current.ProcessExitCode ?? (depth == 0 ? exitCode : null));
            depth++;
        }
    }

    private static SnapTapRuntimeState AdmissionState(int exitCode) => exitCode switch
    {
        4 => SnapTapRuntimeState.RecoveryRequired,
        5 => SnapTapRuntimeState.DriverMissing,
        6 => SnapTapRuntimeState.UnsupportedModel,
        7 => SnapTapRuntimeState.ManualDriverSetupRequired,
        _ => SnapTapRuntimeState.Unavailable,
    };

    internal static bool CanDiscardJournal(string? previousBoot, string currentBoot, IReadOnlyList<byte[]> hooks) =>
        !string.IsNullOrWhiteSpace(previousBoot) && !string.IsNullOrWhiteSpace(currentBoot)
        && !string.Equals(previousBoot, currentBoot, StringComparison.Ordinal) && !hooks.Any(IsAdHook);

    internal static int AdmissionExitCode(RazerSnapTapAdmissionFailure failure) => failure switch
    {
        RazerSnapTapAdmissionFailure.None => 0,
        RazerSnapTapAdmissionFailure.DriverMissing => 5,
        RazerSnapTapAdmissionFailure.UnsupportedModel => 6,
        RazerSnapTapAdmissionFailure.ManualDriverSetupRequired => 7,
        _ => 3,
    };

    private static string ReadBootIdentity()
    {
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        query.Options.Timeout = TimeSpan.FromSeconds(2);
        using ManagementObjectCollection results = query.Get();
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                if (result["LastBootUpTime"] is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
            }
        }
        throw new InvalidOperationException("Windows boot identity is unavailable.");
    }

    private sealed record OwnershipJournal(string BootIdentity, byte[][] BaselineHooks);

    private static bool IsAdHook(byte[] record) =>
        RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
        && hook is not null && RazerSnapTapDriverProtocol.IsAdHook(hook);

    internal static bool SameUnownedHooks(IReadOnlyList<byte[]> baseline, IReadOnlyList<byte[]> current)
    {
        var remaining = current.Where(record => !IsCanonical(record)).ToList();
        foreach (byte[] record in baseline)
        {
            int index = remaining.FindIndex(candidate => candidate.AsSpan().SequenceEqual(record));
            if (index < 0)
            {
                return false;
            }
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static bool IsCanonical(byte[] record) => record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(0x1E))
        || record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(0x20));
}
