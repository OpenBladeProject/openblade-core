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
    internal const string WorkerMode = OpenBlade.ProcessArguments.InputHelperCommandLine.WorkerMode;
    internal static string HeartbeatName(int parent) => $"Local\\OpenBlade.SnapTap.Heartbeat.{parent}";
    internal static string StartGateName(int parent) => $"Local\\OpenBlade.SnapTap.Start.{parent}";
    internal static string AdmissionName(int parent) => $"Local\\OpenBlade.SnapTap.Admission.{parent}";

    internal const int StartupHeartbeatBudgetMilliseconds = 10000;
    internal const int InputHeartbeatBudgetMilliseconds = 1500;
    // Every driver mutation repeats full exact-device/controller admission on the
    // input thread. That user-mode check measured 1.7 s under boot load, above the
    // input budget, so its own window gets the startup bound. Driver requests stay
    // under the input budget: they never run inside an admission window.
    internal const int AdmissionHeartbeatBudgetMilliseconds = 10000;

    internal static bool HeartbeatExpired(long elapsedMilliseconds, bool started, bool admitting) =>
        elapsedMilliseconds > (!started ? StartupHeartbeatBudgetMilliseconds
            : admitting ? AdmissionHeartbeatBudgetMilliseconds : InputHeartbeatBudgetMilliseconds);

    // An explicit retry waits this long for the user to release the keys and mouse
    // button used to change the setting before it refuses a same-boot journal.
    internal const int RetryReleaseBudgetMilliseconds = 5000;

    internal static int Run(Func<bool> shouldStop, KeyboardDriverFeatures features = KeyboardDriverFeatures.SnapTap,
        bool retryRequested = false)
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
            // matching global records. Only its own durable journal and worker evidence
            // can release that case automatically; otherwise it waits for explicit recovery.
            foreach (string previousPath in new[] { "snaptap-driver-ownership.json", "function-row-driver-ownership.json" }
                .Select(name => Path.Combine(Path.GetDirectoryName(journal)!, name)))
            {
                OwnershipJournal? previous = DriverOwnershipJournalReader.Read(previousPath);
                if (previous is null) { continue; }
                if (!CanDiscardJournal(previous.BootIdentity, boot, baseline))
                {
                    // A same-boot journal stays closed unless one of two read-only-checked
                    // paths releases it: an explicit preference change, or durable evidence
                    // that its owner was terminated before its own reconciliation (Task
                    // Manager, a forced process-tree stop). The exclusive helper mutex
                    // excludes a live owner; fresh enumeration must then show no OpenBlade
                    // hook and Windows must report no key or button down.
                    bool sameBoot = string.Equals(previous.BootIdentity, boot, StringComparison.Ordinal);
                    bool terminatedOwner = false;
                    if (sameBoot && TryReadTerminatedOwner(previous, out string? evidencePath,
                            out IReadOnlyList<byte[]>? evidencedHooks, out int evidenceWorkerId))
                    {
                        // The dead worker wrote this after each independent hook readback.
                        // Clear exactly those records with the normal post-exit transaction;
                        // any other OpenBlade or foreign change refuses before mutation.
                        stage = "TerminatedOwnerReconciliation";
                        RazerKeyboardHookPurpose previousPurpose = RazerKeyboardHookScope.Purpose(previous.Features);
                        using var recovery = previousPurpose == purpose ? null
                            : new RazerSnapTapDriverTransport(endpoint.Path, () => admission.IsCurrent(endpoint),
                                purpose: previousPurpose);
                        // The evidence shrinks before each clear (see TryReconcileHooks), so a
                        // kill here stays recoverable and never replays an in-flight clear.
                        bool reconciled;
                        string? orphanReason;
                        try
                        {
                            reconciled = TryReconcileHooks(recovery ?? guardian, previous.BaselineHooks,
                                pending => reconciliationMayHaveSubmitted = pending, out orphanReason,
                                previousPurpose, evidencedHooks,
                                RazerKeyboardOwnedHookEvidence.Recorder(evidencePath!, evidenceWorkerId));
                        }
                        catch when (reconciliationMayHaveSubmitted)
                        {
                            MarkJournalLatched(previousPath, evidencePath!);
                            throw;
                        }
                        if (reconciled)
                        {
                            terminatedOwner = true;
                            File.Delete(evidencePath!);
                            baseline = guardian.ReadHooks();
                        }
                        else if (orphanReason != "HookOwnershipChanged")
                        {
                            // Latch, as the owner itself would have after a failed cleanup.
                            MarkJournalLatched(previousPath, evidencePath!);
                            return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                                SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                                    "ReconcileTerminatedOwner", stage, orphanReason!, true), 1);
                        }
                        stage = "OwnershipJournal";
                    }
                    bool ownedHooks = baseline.Any(record => IsPurposeHook(record, RazerKeyboardHookPurpose.Combined));
                    bool released = (retryRequested || terminatedOwner) && sameBoot && !ownedHooks
                        && WaitForReleasedInput(SnapTapDriverLoop.AllReleased, shouldStop, RetryReleaseBudgetMilliseconds);
                    if (!CanRetrySameBootJournal(retryRequested || terminatedOwner, previous.BootIdentity, boot, baseline, released))
                    {
                        return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.RecoveryRequired,
                            SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                                "OwnershipJournal", stage, "RecoveryRequired") with
                            {
                                Context = $"RetryRequested={retryRequested}; SameBoot={sameBoot}; OwnedHooks={ownedHooks}; "
                                    + $"InputReleased={released}; TerminatedOwner={terminatedOwner}",
                            }, 4);
                    }
                    Trace.TraceInformation(terminatedOwner
                            ? "Released same-boot driver ownership journal {0} after clearing a terminated owner's evidenced hooks."
                            : "Released same-boot driver ownership journal {0} after an explicit retry.",
                        Path.GetFileName(previousPath));
                }
                File.Delete(previousPath);
                // A released or discarded journal's owner evidence can never be used again.
                if (previous.SupervisorProcessId is int previousOwner && previousOwner > 0
                    && previousOwner != Environment.ProcessId)
                {
                    File.Delete(RazerKeyboardOwnedHookEvidence.PathFor(previousOwner));
                }
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
            // Evidence left by an earlier process with this reused id must not
            // outlive the journal this process is about to own.
            File.Delete(RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId));
            using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new OwnershipJournal(boot, baseline.ToArray(), features,
                    Environment.ProcessId, DateTimeOffset.UtcNow));
                stream.Flush(true);
            }
            stage = "WorkerInfrastructure";
            using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, HeartbeatName(Environment.ProcessId));
            using var admitting = new EventWaitHandle(false, EventResetMode.ManualReset, AdmissionName(Environment.ProcessId));
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
            var stallRearms = new Queue<long>();
            while (true)
            {
                Process? worker = null;
                bool clean = false;
                bool rearming = false;
                try
                {
                    startGate.Reset();
                    stop.Reset();
                    heartbeat.Reset();
                    admitting.Reset();
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
                    bool killedAfterStall = false;
                    while (!worker.HasExited)
                    {
                        // Read the window flag before consuming the heartbeat. The worker
                        // signals before clearing it, so a just-finished admission is
                        // always followed by a heartbeat consumed in this same pass.
                        bool workerAdmitting = admitting.WaitOne(0);
                        if (heartbeat.WaitOne(50))
                        {
                            lastHeartbeat = Environment.TickCount64;
                            started = true;
                        }
                        bool requestedStop = shouldStop();
                        bool timedOut = HeartbeatExpired(Environment.TickCount64 - lastHeartbeat, started, workerAdmitting);
                        if (requestedStop || timedOut)
                        {
                            interrupted = true;
                            if (timedOut)
                            {
                                observedWorkerFailure = HeartbeatTimeoutFailure(started);
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
                                killedAfterStall = timedOut && !requestedStop;
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
                    bool cleanPause = IsCleanPause(worker.ExitCode, finalStatus, worker.Id, workerStartedAt)
                        && observedWorkerFailure is null;
                    if (worker.ExitCode != 0 && !cleanPause && observedWorkerFailure is null)
                    {
                        observedWorkerFailure = SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
                            "WorkerExit", worker.ExitCode == 3 ? "WorkerAdmissionUnavailable" : "UnexpectedExit",
                            processExitCode: worker.ExitCode);
                    }
                    stage = "Reconciliation";
                    string ownEvidence = RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId);
                    IReadOnlyList<byte[]> workerOwned =
                        RazerKeyboardOwnedHookEvidence.Read(Environment.ProcessId, worker.Id, workerStartedAt, purpose);
                    if (!(worker.ExitCode is 0 or 3 || cleanPause))
                    {
                        // An uncertain exit always latches. Mark it before cleanup so a kill
                        // during cleanup cannot look like a terminated owner.
                        MarkJournalLatched(journal, ownEvidence);
                    }
                    // The evidence shrinks before each clear: if this supervisor dies during
                    // its own cleanup, a later start can finish it without replaying a clear.
                    if (!TryReconcileHooks(guardian, baseline,
                        pending => reconciliationMayHaveSubmitted = pending, out string? cleanupReason, purpose,
                        workerOwned, RazerKeyboardOwnedHookEvidence.Recorder(ownEvidence, worker.Id)))
                    {
                        return PublishTerminal(endpoint.Profile, SnapTapRuntimeState.Failed,
                            SnapTapDiagnosticFailure.Create(SnapTapDiagnosticFailure.Supervisor,
                                "ReconcileHooks", stage, cleanupReason!, true, observedWorkerFailure), 1);
                    }
                    clean = worker.ExitCode is 0 or 3 || cleanPause;
                    if (clean)
                    {
                        // Verified cleanup after a clean exit leaves nothing to recover.
                        File.Delete(ownEvidence);
                    }
                    if (IsCleanStall(observedWorkerFailure, worker.ExitCode, finalStatus, worker.Id, workerStartedAt))
                    {
                        // The observed timeout latched a clean-pause exit before cleanup.
                        // Verified cleanup resolved it: close the journal on stop, and
                        // reopen it before rearming so a later force-close stays recoverable.
                        if (shouldStop())
                        {
                            clean = true;
                            File.Delete(ownEvidence);
                            return 0;
                        }
                        if (TryAdmitStallRearm(stallRearms, Environment.TickCount64) && ClearJournalLatch(journal))
                        {
                            Trace.TraceInformation("SnapTap worker stall reconciled; rearming: {0}", observedWorkerFailure);
                            clean = false;
                            rearming = true;
                            continue;
                        }
                    }
                    if (IsKilledStall(observedWorkerFailure, killedAfterStall, worker.ExitCode,
                        MatchingTerminalFailure(finalStatus, worker.Id, workerStartedAt)))
                    {
                        // A worker frozen past the stop wait, as under boot load, was killed.
                        // Reconciliation has cleared and read back its hooks, so this is the
                        // terminated-owner case with a live supervisor: resume under the same
                        // released-input check, within the shared stall-rearm budget.
                        if (shouldStop())
                        {
                            if (SnapTapDriverLoop.AllReleased())
                            {
                                clean = true;
                                File.Delete(ownEvidence);
                                return 0;
                            }
                        }
                        else if (WaitForReleasedInput(SnapTapDriverLoop.AllReleased, shouldStop, RetryReleaseBudgetMilliseconds)
                            && TryAdmitStallRearm(stallRearms, Environment.TickCount64)
                            && ClearJournalLatch(journal))
                        {
                            Trace.TraceInformation("SnapTap killed worker reconciled; rearming: {0}", observedWorkerFailure);
                            File.Delete(ownEvidence);
                            clean = false;
                            rearming = true;
                            continue;
                        }
                    }
                    if (cleanPause)
                    {
                        Trace.TraceInformation("SnapTap clean pause reconciled; waiting to rearm: {0}", finalStatus?.Failure);
                        if (interrupted || shouldStop())
                        {
                            return 0;
                        }
                        // Keep the original baseline and durable journal across workers.
                        // A new process must pass fresh admission, focus and all-release gates.
                        clean = false;
                        rearming = true;
                        continue;
                    }
                    int exitCode = clean ? worker.ExitCode : 4;
                    if (!clean)
                    {
                        DiagnosticFailure cleanupFailure = RecoveryRequiredFailure(stage, observedWorkerFailure,
                            worker.ExitCode, MatchingTerminalFailure(finalStatus, worker.Id, workerStartedAt));
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
                    try
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
                    }
                    finally
                    {
                        if (clean)
                        {
                            File.Delete(journal);
                            File.Delete(RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId));
                        }
                        else if (!rearming)
                        {
                            // This supervisor is leaving its journal for explicit recovery.
                            // Mark that decision so a later start cannot mistake it for an
                            // owner that was terminated before reaching this point.
                            MarkJournalLatched(journal, RazerKeyboardOwnedHookEvidence.PathFor(Environment.ProcessId));
                        }
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

    // Two worker pauses are clean: a focus loss in the input loop, and a subscription
    // enable refused before submission during hook setup. Neither submitted output.
    internal static bool IsCleanPause(
        int exitCode, SnapTapRuntimeStatus? status, int processId, DateTimeOffset startedAt) =>
        exitCode == SnapTapDriverLoop.CleanPauseExitCode
        && status is { State: SnapTapRuntimeState.Paused, Failure: { } failure }
        && status.ProcessId == processId && status.UpdatedAt >= startedAt
        && failure is
        {
            Component: SnapTapDiagnosticFailure.Worker,
            Operation: "Run",
            MayHaveSubmitted: false,
            Cause: null,
            Related: null,
        }
        && failure is { Stage: "InputLoop", Reason: "ForegroundUnavailable" }
            or { Stage: "HookSetup", Reason: "SubscriptionDeferred" };

    internal static DiagnosticFailure HeartbeatTimeoutFailure(bool started) => SnapTapDiagnosticFailure.Create(
        SnapTapDiagnosticFailure.Supervisor, "SuperviseWorker",
        started ? "InputLoop" : "WorkerStartup", "HeartbeatTimeout", started);

    // A late heartbeat alone proves only a stall: a worker under CPU or paging load
    // can miss the input budget while it is still healthy. Every path that leaves
    // output uncertain (a timed-out send, an uncancelled request, a faulted or
    // poisoned processor, an unconfirmed hook close) exits non-zero or publishes
    // Failed. The one faulted case that exits 0 is output refused before submission
    // because of the stop, after the worker released every key it delivered
    // (StopRefusalExit). So a worker that honours the stop with a fresh Off of its
    // own, or with the dedicated clean-pause exit (a stop during hook setup defers the enable
    // before submission), submitted nothing ambiguous. Startup stalls keep their
    // own budget and are not admitted. The caller must also confirm reconciliation.
    internal static bool IsCleanStall(
        DiagnosticFailure? observed, int exitCode, SnapTapRuntimeStatus? status, int processId,
        DateTimeOffset startedAt) =>
        observed is not null && observed == HeartbeatTimeoutFailure(started: true)
        && ((exitCode == 0
                && status is { State: SnapTapRuntimeState.Off, Failure: null }
                && status.ProcessId == processId && status.UpdatedAt >= startedAt)
            || IsCleanPause(exitCode, status, processId, startedAt));

    // A worker that missed its input heartbeat and then ignored the stop for the whole
    // wait was killed by this supervisor. Its output is as uncertain as after any
    // force-close, so the caller resumes only after verified hook reconciliation and
    // the released-input check that a terminated owner's recovery uses. Kill() does
    // not fail on a worker that exited on its own just before it, so only the
    // termination exit code counts, and a worker that published its own terminal
    // failure keeps that failure's latch.
    internal const int KilledExitCode = -1;

    internal static bool IsKilledStall(DiagnosticFailure? observed, bool killedAfterStall, int exitCode,
        DiagnosticFailure? workerTerminalFailure) =>
        killedAfterStall && exitCode == KilledExitCode && workerTerminalFailure is null
        && observed is not null && observed == HeartbeatTimeoutFailure(started: true);

    // Bounds automatic stall recovery so a worker that keeps stalling still latches
    // a visible failure instead of restarting indefinitely.
    internal const int StallRearmLimit = 3;
    internal const long StallRearmWindowMilliseconds = 10 * 60 * 1000;

    internal static bool TryAdmitStallRearm(Queue<long> recent, long now)
    {
        while (recent.Count != 0 && now - recent.Peek() >= StallRearmWindowMilliseconds)
        {
            recent.Dequeue();
        }
        if (recent.Count >= StallRearmLimit)
        {
            return false;
        }
        recent.Enqueue(now);
        return true;
    }

    // A supervisor-observed cause, such as a heartbeat timeout, hides why the
    // worker itself exited; keep both for diagnostics.
    internal static DiagnosticFailure RecoveryRequiredFailure(
        string stage, DiagnosticFailure? observedWorkerFailure, int workerExitCode, DiagnosticFailure? workerReported) =>
        (SnapTapDiagnosticFailure.Create(
            SnapTapDiagnosticFailure.Supervisor, "ReconcileHooks", stage, "RecoveryRequired",
            observedWorkerFailure?.MayHaveSubmitted == true, observedWorkerFailure,
            processExitCode: workerExitCode) with
        {
            Context = $"WorkerReason={workerReported?.Reason ?? "None"}; "
                + $"WorkerStage={workerReported?.Stage ?? "None"}; WorkerContext={workerReported?.Context ?? "None"}",
        }).LimitDetails();

    internal static bool TryReconcileHooks(
        IRazerSnapTapHookTransport guardian,
        IReadOnlyList<byte[]> baseline,
        Action<bool> setSubmissionPending,
        out string? failureReason,
        RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap,
        IReadOnlyList<byte[]>? confirmedOwned = null,
        Action<IReadOnlyList<byte[]>>? recordRemaining = null)
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
        var remaining = hooks.Where(record => IsPurposeHook(record, purpose)).ToList();
        foreach (ushort scan in RazerSnapTapDriverProtocol.ScanCodes(purpose))
        {
            byte[] owned = RazerSnapTapDriverProtocol.EncodeHook(scan, purpose);
            if (hooks.Any(record => record.AsSpan().SequenceEqual(owned)))
            {
                // Before each submission, durable evidence shrinks to the hooks still
                // to clear, without this one: a process ended mid-cleanup stays
                // recoverable, and an in-flight clear is never replayed.
                remaining.RemoveAll(record => record.AsSpan().SequenceEqual(owned));
                recordRemaining?.Invoke(remaining.ToArray());
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
        DriverOwnershipJournalReader.IsLaterBoot(previousBoot, currentBoot)
        && !hooks.Any(record => IsPurposeHook(record, RazerKeyboardHookPurpose.Combined));

    // The residual risk is a key an earlier worker sent as pressed that Windows does
    // not report as down; the user accepts it by explicitly retrying.
    internal static bool CanRetrySameBootJournal(bool retryRequested, string? previousBoot, string currentBoot,
        IReadOnlyList<byte[]> hooks, bool inputReleased) =>
        retryRequested && inputReleased
        && string.Equals(previousBoot, currentBoot, StringComparison.Ordinal)
        && !hooks.Any(record => IsPurposeHook(record, RazerKeyboardHookPurpose.Combined));

    internal static bool WaitForReleasedInput(Func<bool> released, Func<bool> shouldStop, int budgetMilliseconds)
    {
        long started = Environment.TickCount64;
        while (!shouldStop())
        {
            if (released()) { return true; }
            if (Environment.TickCount64 - started >= budgetMilliseconds) { return false; }
            Thread.Sleep(50);
        }
        return false;
    }

    internal static bool CanStartWithHooks(IReadOnlyList<byte[]> hooks) => hooks.Count == 0;

    internal static KeyboardDriverFeatures AdmitFeatures(KeyboardDriverFeatures features, RazerSnapTapDriverProfile profile)
    {
        if (features.HasFlag(KeyboardDriverFeatures.FunctionRow) && profile.ProductId is not ("02E0" or "02C6" or "02A0"))
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

    // SupervisorProcessId and CreatedAt identify the writer; journals from earlier
    // builds lack them and keep the explicit-retry-only behavior. Latched records a
    // supervisor that deliberately left the journal for explicit recovery.
    internal sealed record OwnershipJournal(string BootIdentity, byte[][] BaselineHooks,
        KeyboardDriverFeatures Features = KeyboardDriverFeatures.SnapTap,
        int? SupervisorProcessId = null, DateTimeOffset? CreatedAt = null, bool Latched = false);

    // A same-boot, unlatched journal whose supervisor no longer runs was left by an
    // owner terminated before its own reconciliation. Its hooks come only from that
    // owner's worker evidence; none is needed when no OpenBlade hook remains, and the
    // caller's reconciliation refuses any OpenBlade hook the evidence does not name.
    internal static bool TryReadTerminatedOwner(OwnershipJournal previous, out string? evidencePath,
        out IReadOnlyList<byte[]>? hooks, out int workerId) => TryReadTerminatedOwner(previous, Environment.ProcessId,
            IsLiveSince, RazerKeyboardOwnedHookEvidence.PathFor, out evidencePath, out hooks, out workerId);

    internal static bool TryReadTerminatedOwner(OwnershipJournal previous, int currentProcessId,
        Func<int, DateTimeOffset, bool> isLiveSince, Func<int, string> evidencePathFor,
        out string? evidencePath, out IReadOnlyList<byte[]>? hooks, out int workerId)
    {
        evidencePath = null;
        hooks = null;
        workerId = 0;
        if (previous.Latched || previous.SupervisorProcessId is not int owner || owner <= 0
            || owner == currentProcessId || previous.CreatedAt is not DateTimeOffset createdAt
            || isLiveSince(owner, createdAt))
        {
            return false;
        }
        string path = evidencePathFor(owner);
        RazerKeyboardOwnedHookEvidence.Evidence? evidence;
        try
        {
            evidence = RazerKeyboardOwnedHookEvidence.ReadTerminatedOwner(path, createdAt,
                RazerKeyboardHookScope.Purpose(previous.Features));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticTrace.TraceException("OpenBlade.SessionAgent.SnapTapDriverSupervisor",
                "ReadTerminatedOwnerEvidence", exception);
            return false;
        }
        if (evidence is not null && isLiveSince(evidence.ProcessId, evidence.UpdatedAt))
        {
            return false;
        }
        evidencePath = path;
        hooks = evidence?.Hooks ?? [];
        workerId = evidence?.ProcessId ?? 0;
        return true;
    }

    // True when a running process with this id started no later than notAfter, so it
    // may be the recorded owner. A later start is a reused id. An unreadable process
    // cannot be ruled out.
    internal static bool IsLiveSince(int processId, DateTimeOffset notAfter)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; }
        using (process)
        {
            try { return !process.HasExited && process.StartTime.ToUniversalTime() <= notAfter.UtcDateTime; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }

    internal const int LatchMarkAttempts = 3;

    // Marks the journal latched. A concurrent reader or I/O error can block the
    // replace, so it retries briefly and then fails closed: a refusal marker in the
    // owner's evidence path makes a later start treat the journal as not terminated.
    internal static bool MarkJournalLatched(string path, string evidencePath)
    {
        if (TryWriteJournalLatch(path, latched: true, "MarkJournalLatched")) { return true; }
        try { RazerKeyboardOwnedHookEvidence.WriteRefusal(evidencePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticTrace.TraceException("OpenBlade.SessionAgent.SnapTapDriverSupervisor",
                "WriteLatchRefusal", exception);
            // Last resort: without evidence, any remaining OpenBlade hook still refuses.
            try { File.Delete(evidencePath + ".tmp"); File.Delete(evidencePath); }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                DiagnosticTrace.TraceException("OpenBlade.SessionAgent.SnapTapDriverSupervisor",
                    "DeleteLatchEvidence", deleteException);
            }
        }
        return false;
    }

    // Reopens a journal this supervisor latched before an uncertain cleanup that then
    // passed every resume check. Failure leaves it latched, so the caller stays latched.
    internal static bool ClearJournalLatch(string path) =>
        TryWriteJournalLatch(path, latched: false, "ClearJournalLatch");

    private static bool TryWriteJournalLatch(string path, bool latched, string operation)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < LatchMarkAttempts; attempt++)
        {
            try
            {
                OwnershipJournal? journal = DriverOwnershipJournalReader.Read(path);
                if (journal is null) { return latched; }
                if (journal.Latched == latched) { return true; }
                string temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, journal with { Latched = latched });
                    stream.Flush(true);
                }
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
                or UnauthorizedAccessException)
            {
                last = exception;
                if (attempt + 1 < LatchMarkAttempts) { Thread.Sleep(50); }
            }
        }
        DiagnosticTrace.TraceException("OpenBlade.SessionAgent.SnapTapDriverSupervisor", operation, last!);
        try { File.Delete(path + ".tmp"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return false;
    }

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
