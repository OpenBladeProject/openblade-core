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

    internal static int Run(Func<bool> shouldStop, KeyboardDriverFeatures features = KeyboardDriverFeatures.SnapTap)
    {
        KeyboardDriverStatusStore.ConfigureScope(features);
        KeyboardDriverFeatures requestedFeatures = features;
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
            features = AdmitFeatures(features, endpoint.Profile);
            if (features == KeyboardDriverFeatures.None)
            {
                return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.UnsupportedModel,
                    SnapTapDiagnosticFailure.Create("FunctionRowDriver", "Admission", "Model", "UnsupportedModel"), 6);
            }
            KeyboardDriverStatusStore.ConfigureScope(features);
            RazerKeyboardHookPurpose purpose = RazerKeyboardHookScope.Purpose(features);
            Trace.TraceInformation("SnapTap driver admitted: {0}; PID={1}; provenance={2}.",
                endpoint.Profile.ProductName, endpoint.Profile.ProductId,
                endpoint.Profile.PhysicallyValidated ? "PhysicallyValidated" : "SourceReviewedExperimental");
            // This independent CreateFile remains unsubscribed. Duplicating the
            // worker's handle would retain its subscription after worker death.
            stage = "GuardianOpen";
            using var guardian = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint), purpose: purpose);
            stage = "BaselineEnumeration";
            IReadOnlyList<byte[]> baseline = guardian.ReadHooks();
            stage = "OwnershipJournal";
            string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade", features.HasFlag(KeyboardDriverFeatures.SnapTap)
                    ? "snaptap-driver-ownership.json" : "function-row-driver-ownership.json");
            string boot = ReadBootIdentity();
            // An interrupted supervisor cannot establish ownership from coincidentally
            // matching global records. Keep that case closed for explicit recovery.
            foreach (string previousPath in new[] { "snaptap-driver-ownership.json", "function-row-driver-ownership.json" }
                .Select(name => Path.Combine(Path.GetDirectoryName(journal)!, name)))
            {
                if (!File.Exists(previousPath)) { continue; }
                using var previousFile = new FileStream(previousPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (previousFile.Length > 131072)
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "OwnershipJournal", stage, "JournalTooLarge"), 4);
                }
                OwnershipJournal? previous = JsonSerializer.Deserialize<OwnershipJournal>(previousFile);
                if (previous is null
                    || !IsValidOwnershipJournalScope(Path.GetFileName(previousPath), previous.Features)
                    || !CanDiscardJournal(previous.BootIdentity, boot, baseline))
                {
                    return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                        SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                            "OwnershipJournal", stage, "RecoveryRequired"), 4);
                }
                previousFile.Dispose();
                File.Delete(previousPath);
            }
            if (!CanStartWithHooks(baseline))
            {
                // RzCommon fans every matched global record to the subscribed
                // file. We cannot forward an unknown owner's keyboard/mouse
                // packets, and must not enable a subscriber that consumes them.
                // Preserve every foreign record; no mutation has occurred here.
                bool orphanFeature = baseline.Any(record => IsPurposeHook(record, RazerKeyboardHookPurpose.Combined));
                return PublishTerminal(endpoint.Profile,
                    orphanFeature ? SnapTapRuntimeState.RecoveryRequired : SnapTapRuntimeState.Unavailable,
                    SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                        "GuardHooks", stage, "ExistingHooks"), orphanFeature ? 4 : 3);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new OwnershipJournal(boot, baseline.ToArray(), features));
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
            start.ArgumentList.Add(SnapTapInputHost.FeaturesArgument(requestedFeatures));
            while (true)
            {
                Process? worker = null;
                bool clean = false;
                try
                {
                    startGate.Reset();
                    stop.Reset();
                    heartbeat.Reset();
                    observedWorkerFailure = null;
                    bool interrupted = false;
                    stage = "WorkerLaunch";
                    File.Delete(RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId));
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
                            interrupted = true;
                            if (timedOut)
                            {
                                observedWorkerFailure = SnapTapDiagnosticFailure.Create(
                                    SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                                    started ? "InputLoop" : "WorkerStartup", "HeartbeatTimeout", started);
                            }
                            stop.Set();
                            if (!worker.WaitForExit(6000))
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
                    SnapTapRuntimeStatus? finalStatus = KeyboardDriverStatusStore.Read();
                    observedWorkerFailure ??= MatchingTerminalFailure(finalStatus, worker.Id, workerStartedAt);
                    bool foregroundPause = IsCleanForegroundPause(worker.ExitCode, finalStatus, worker.Id, workerStartedAt)
                        && observedWorkerFailure is null;
                    if (worker.ExitCode != 0 && !foregroundPause && observedWorkerFailure is null)
                    {
                        observedWorkerFailure = SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                            "WorkerExit", worker.ExitCode == 3 ? "WorkerAdmissionUnavailable" : "UnexpectedExit",
                            processExitCode: worker.ExitCode);
                    }
                    stage = "Reconciliation";
                    if (!TryReconcileHooks(guardian, baseline,
                        pending => reconciliationMayHaveSubmitted = pending, out string? cleanupReason, purpose,
                        RazerKeyboardOwnedHookEvidence.Read(Environment.ProcessId, worker.Id, workerStartedAt, purpose)))
                    {
                        return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                            SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                                "ReconcileHooks", stage, cleanupReason!, true, observedWorkerFailure), 1);
                    }
                    clean = worker.ExitCode is 0 or 3 || foregroundPause;
                    if (foregroundPause)
                    {
                        Trace.TraceInformation("SnapTap foreground pause reconciled; waiting to rearm: {0}", finalStatus?.Failure);
                        if (interrupted || shouldStop())
                        {
                            return 0;
                        }
                        // Keep the original baseline and durable journal across workers.
                        // A new process must pass fresh admission, focus and all-release gates.
                        clean = false;
                        continue;
                    }
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
                            if (!worker.WaitForExit(6000))
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
                        File.Delete(RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId));
                    }
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

    internal static bool IsCleanForegroundPause(
        int exitCode, SnapTapRuntimeStatus? status, int processId, DateTimeOffset startedAt) =>
        exitCode == SnapTapDriverLoop.ForegroundPauseExitCode
        && status is { State: SnapTapRuntimeState.Paused, Failure: { } failure }
        && status.ProcessId == processId && status.UpdatedAt >= startedAt
        && failure is
        {
            Component: SnapTapDiagnosticFailure.Worker,
            Operation: "Run",
            Stage: "InputLoop",
            Reason: "ForegroundUnavailable",
            MayHaveSubmitted: false,
            Cause: null,
            Related: null,
        };

    internal static bool TryReconcileHooks(
        IRazerSnapTapHookTransport guardian,
        IReadOnlyList<byte[]> baseline,
        Action<bool> setSubmissionPending,
        out string? failureReason,
        RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap,
        IReadOnlyList<byte[]>? confirmedOwned = null)
    {
        using var mutation = RazerKeyboardHookMutationGate.Enter();
        failureReason = null;
        IReadOnlyList<byte[]> hooks = guardian.ReadHooks();
        if (baseline.Any(record => IsPurposeHook(record, purpose))
            || !SameUnownedHooks(baseline, hooks, purpose)
            || RazerSnapTapDriverProtocol.ScanCodes(purpose).Any(scan =>
                hooks.Count(record => record.AsSpan().SequenceEqual(
                    RazerSnapTapDriverProtocol.EncodeHook(scan, purpose))) > 1)
            || (confirmedOwned is not null && hooks.Where(record => IsPurposeHook(record, purpose))
                .Any(record => !confirmedOwned.Any(owned => owned.AsSpan().SequenceEqual(record)))))
        {
            failureReason = "HookOwnershipChanged";
            return false;
        }
        // Preserve the independently observed current disjoint feature records,
        // which may have changed since our own session started. During this
        // bounded transaction, every unrelated byte must remain unchanged.
        IReadOnlyList<byte[]> currentUnowned = hooks.Where(record => !IsPurposeHook(record, purpose)).ToArray();
        foreach (ushort scan in RazerSnapTapDriverProtocol.ScanCodes(purpose))
        {
            byte[] owned = RazerSnapTapDriverProtocol.EncodeHook(scan, purpose);
            if (hooks.Any(record => record.AsSpan().SequenceEqual(owned)))
            {
                // The caller must prove child exit before this independent read/clear.
                // Never synthesize a guessed key release or reset the driver.
                setSubmissionPending(true);
                guardian.ClearHook(RazerSnapTapDriverProtocol.EncodeClearHook(scan, purpose));
                hooks = guardian.ReadHooks();
                setSubmissionPending(false);
                if (hooks.Any(record => record.AsSpan().SequenceEqual(owned))
                    || !SameRecords(currentUnowned, hooks.Where(record => !IsCanonical(record, purpose)))
                    || hooks.Any(record => IsPurposeHook(record, purpose) && !IsCanonical(record, purpose)))
                {
                    failureReason = "HookCleanupUnconfirmed";
                    return false;
                }
            }
        }
        if (!SameRecords(currentUnowned, hooks))
        {
            failureReason = "RecoveryRequired";
            return false;
        }
        return true;
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
            KeyboardDriverStatusStore.Write(new(
                state,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                profile?.ProductName,
                profile is { PhysicallyValidated: false },
                failure));
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapDriverSupervisor",
                "PublishTerminalStatus",
                exception);
        }
        return exitCode;
    }

    private static void TraceTerminalFailure(DiagnosticFailure failure, int exitCode)
        => Trace.TraceWarning(
            "SnapTap terminal failure: {0}",
            failure with { ProcessExitCode = failure.ProcessExitCode ?? exitCode });

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
        && !string.Equals(previousBoot, currentBoot, StringComparison.Ordinal)
        && !hooks.Any(record => IsPurposeHook(record, RazerKeyboardHookPurpose.Combined));

    internal static bool CanStartWithHooks(IReadOnlyList<byte[]> hooks) => hooks.Count == 0;

    internal static KeyboardDriverFeatures AdmitFeatures(KeyboardDriverFeatures features, RazerSnapTapDriverProfile profile)
    {
        if (features.HasFlag(KeyboardDriverFeatures.FunctionRow) && profile.ProductId is not ("02E0" or "02C6"))
        {
            FunctionRowBackendStatusStore.Write(new(SnapTapRuntimeState.UnsupportedModel, DateTimeOffset.UtcNow,
                Environment.ProcessId, profile.ProductName, true,
                SnapTapDiagnosticFailure.Create("FunctionRowDriver", "Admission", "Model", "UnsupportedModel"))
            { Backend = KeyboardInputBackend.RazerDriver });
            return features & ~KeyboardDriverFeatures.FunctionRow;
        }
        return features;
    }

    internal static int AdmissionExitCode(RazerSnapTapAdmissionFailure failure) => failure switch
    {
        RazerSnapTapAdmissionFailure.None => 0,
        RazerSnapTapAdmissionFailure.DriverMissing => 5,
        RazerSnapTapAdmissionFailure.UnsupportedModel => 6,
        RazerSnapTapAdmissionFailure.ManualDriverSetupRequired => 7,
        _ => 3,
    };

    internal static string ReadBootIdentity()
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

    internal sealed record OwnershipJournal(string BootIdentity, byte[][] BaselineHooks,
        KeyboardDriverFeatures Features = KeyboardDriverFeatures.SnapTap);

    internal static bool IsValidOwnershipJournalScope(string fileName, KeyboardDriverFeatures features) => fileName switch
    {
        "snaptap-driver-ownership.json" => features is KeyboardDriverFeatures.SnapTap
            or (KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow),
        "function-row-driver-ownership.json" => features == KeyboardDriverFeatures.FunctionRow,
        _ => false,
    };

    private static bool IsAdHook(byte[] record) =>
        RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
        && hook is not null && RazerSnapTapDriverProtocol.IsAdHook(hook);

    internal static bool SameUnownedHooks(IReadOnlyList<byte[]> baseline, IReadOnlyList<byte[]> current,
        RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap)
    {
        RazerKeyboardHookPurpose other = purpose == RazerKeyboardHookPurpose.SnapTap
            ? RazerKeyboardHookPurpose.FunctionRow : RazerKeyboardHookPurpose.SnapTap;
        return SameRecords(baseline.Where(record => !IsCanonical(record, other)),
            current.Where(record => !IsCanonical(record, purpose) && !IsCanonical(record, other)));
    }

    private static bool SameRecords(IEnumerable<byte[]> baseline, IEnumerable<byte[]> current)
    {
        var remaining = current.ToList();
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

    private static bool IsCanonical(byte[] record, RazerKeyboardHookPurpose purpose) =>
        RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
        && hook is not null && RazerSnapTapDriverProtocol.IsOwnedHook(hook, purpose);

    private static bool IsPurposeHook(byte[] record, RazerKeyboardHookPurpose purpose) =>
        RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
        && hook is not null && RazerSnapTapDriverProtocol.IsPurposeHook(hook, purpose);
}
