using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Capture;

// Fixed developer experiment only. The default is an offline plan. There is no
// register/value input, unattended mode, persisted approval or replay command.
internal static class So6120SilentPwmCommand
{
    internal const string Name = "validate-so6120-silent-pwm-increase";
    internal const string BalancedName = "validate-so6120-balanced-pwm-increase";
    internal const string HalfCycleName = "validate-so6120-balanced-pwm-half-cycle";
    internal const string StopName = "validate-so6120-balanced-pwm-stop";
    internal const string FirmwareHash = "8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707";
    internal const string HandleControlHash = "AF1A1648AA2FD19DB79352AA31DFAB50B93CD49DF84CE959A7C9AC3FBD01EFE9";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static string FormatPlan(PerformancePreset profile, string name, string powerInstruction,
        So6120TrialTargetPolicy targetPolicy)
    {
        bool halfCycle = targetPolicy != So6120TrialTargetPolicy.PlusFour;
        bool stop = targetPolicy == So6120TrialTargetPolicy.HalfCycleThenStop;
        string targetDescription = halfCycle ? """
            Only then open InpOut and read the A0 baseline, including the current PWM cycle count.
            This trial fixes both targets at half the captured cycle count, rounded down.
            Cycle 183 gives raw 91/183 per fan, about 49.7% of the cycle count.
            The cycle fraction has no calibrated electrical duty or RPM interpretation.
            This permits a larger increase than the separate +4-count trial.
            A second explicit approval binds the baseline and exact raw targets.
            Each fixed target must exceed its fan's fresh actual PWM before entry and each entry write.
            Reads and writes are separate operations. Either fan at or above its target stops entry.
            """ : """
            Only then open InpOut and read the A0 baseline to derive fixed targets at +4 raw counts per fan.
            Both fans need >=4 counts of headroom. No zero/decrease target or preparatory RPM command.
            A +4 request from a natural zero baseline may or may not start a fan.
            A second explicit approval binds the baseline, exact targets, and live +1 to +8 range per fan.
            Before entry and before each entry write, compare fresh actual PWM to those fixed targets.
            Either target outside the live increase range stops entry. The read and write are separate operations.
            """;
        string afterEntry = stop ? """
            Fresh FE readback must show both half-cycle targets and both raw tach bytes above zero.
            Then write B8=0 and B9=0 in order while FE is active; verify each write separately.
            Observe actual PWM 0/0 and raw tach 0/0 in two consecutive complete samples.
            Start no new coast sample after five seconds and take at most four additional samples.
            Tach is a raw EC rotation indication. Zero tach cannot establish absolute impeller standstill.
            Finish each started confirmation and hand back promptly from the last confirmed state.
            """ : "After a one-second dwell, hand back from the last confirmed state.";
        return $"""
        SO6120 supervised {profile} PWM {(stop ? "stop trial" : "increase")}: PREPARATION ONLY
        Exact RZ09-0581 / 1532:02E0 / SKU RZ09-05819EN4 / BIOS 4.01 / reported EC 1.04.
        {powerInstruction}
        Keep the laptop awake, idle/light desktop, and peripherals unchanged.
        Select {profile} + Automatic, then use OpenBlade's normal Shut down command.
        Service, Tray and Session Agent must all be stopped before acquisition.
        Live run requires an elevated interactive console and a new absolute run directory.
        A separate-console guardian must escape the caller's job before acquisition; otherwise launch is refused.
        Both BASELINE and APPLY approvals occur in that guardian console. The client holds no hardware handle.
        Metadata inspection opens HID descriptor handles without sending feature reports.
        It first obtains explicit acceptance of the scoped contention/abort limits.
        {targetDescription}
        Stage B8/B9; set owned F0.6 permission; publish FE last; verify each write.
        {afterEntry}
        Restore the captured normal selector to Auto, confirm Manual00, then restore the original owned F0/staging bytes.
        Raw tach bytes are recorded; their RPM scale is not established here.
        CPU thermal-zone 10..55 C and confirmed inactive GPU admit entry only.
        High/unknown thermal readings stop entry; they do not block sound Auto cleanup.
        Ctrl+C requests stop at a resolved boundary and the approved Auto handback.
        Caller loss or a missed five-second heartbeat also requests that handback; it cannot recover guardian/OS failure.
        Ambiguous commands/readback/storage fence ALL mailbox commands, including A0.
        No retry, alternate writer, automatic kill, forced reboot or GUI Auto fallback.
        Stop new entry or experiment work after 45s or 30s since possible F0; finish confirmation first.
        Auto has its own 60s phase. Each logical transaction retains its full 2s budget.
        These are publication/work cutoffs, not a hard physical exposure guarantee.
        Synchronous native calls can stall beyond those bounds; no hard watchdog is claimed.
        If stalled/unresolved, keep the console/process alive, leave OpenBlade stopped,
        stop workload and seek supervised reconciliation. Physical recovery is unverified.
        Usage: OpenBlade.Capture {name} --run-root <new absolute directory>
        """;
    }

    internal static bool TryParse(IReadOnlyList<string> args, out string? runRoot)
    {
        runRoot = null;
        if (args.Count == 0 || args.Count == 1 && args[0] == "--plan") { return true; }
        if (args.Count != 2 || args[0] != "--run-root" || string.IsNullOrWhiteSpace(args[1])
            || !Path.IsPathFullyQualified(args[1])) { return false; }
        try
        {
            runRoot = So6120DirectDutyJournal.ValidateRunRoot(args[1]);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or InvalidDataException or UnauthorizedAccessException)
        { return false; }
    }

    internal static Task<int> RunAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, TextReader, TextWriter, Task<int>>? live = null) =>
        RunAsync(Name, args, input, output, live);

    internal static Task<int> RunBalancedAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, TextReader, TextWriter, Task<int>>? live = null) =>
        RunAsync(BalancedName, args, input, output, live);

    internal static Task<int> RunBalancedHalfCycleAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, TextReader, TextWriter, Task<int>>? live = null) =>
        RunAsync(HalfCycleName, args, input, output, live);

    internal static Task<int> RunBalancedStopAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, TextReader, TextWriter, Task<int>>? live = null) =>
        RunAsync(StopName, args, input, output, live);

    private static async Task<int> RunAsync(string name, IReadOnlyList<string> args,
        TextReader input, TextWriter output, Func<string, TextReader, TextWriter, Task<int>>? live)
    {
        string planText = GetTrialDefinition(name).Plan;
        if (!TryParse(args, out string? root))
        { await output.WriteLineAsync(planText); return 1; }
        if (root is null) { await output.WriteLineAsync(planText); return 0; }
        return live is not null ? await live(root, input, output)
            : await So6120GuardianHost.RunClientAsync(name, root, output);
    }

    internal static Task<int> RunOwnedWindowsAsync(string name, string root, TextReader input, TextWriter output,
        So6120GuardianSignals signals)
    {
        var definition = GetTrialDefinition(name);
        return RunWindowsAsync(definition.Profile, definition.Policy, definition.Plan, root, input, output, signals);
    }

    private static (PerformancePreset Profile, So6120TrialTargetPolicy Policy, string Plan) GetTrialDefinition(string name)
    {
        var (profile, policy) = name switch
        {
            Name => (PerformancePreset.Silent, So6120TrialTargetPolicy.PlusFour),
            BalancedName => (PerformancePreset.Balanced, So6120TrialTargetPolicy.PlusFour),
            HalfCycleName => (PerformancePreset.Balanced, So6120TrialTargetPolicy.HalfCycle),
            StopName => (PerformancePreset.Balanced, So6120TrialTargetPolicy.HalfCycleThenStop),
            _ => throw new ArgumentException("Unknown supervised trial.", nameof(name)),
        };
        string power = profile == PerformancePreset.Balanced
            ? "Keep the operator-reported USB-C power connected. Windows checks external power only." : "Keep AC connected.";
        return (profile, policy, FormatPlan(profile, name, power, policy));
    }

    internal static async Task<bool> ConfirmAsync(TextReader input, TextWriter output,
        string token, CancellationToken cancellationToken, TimeSpan? timeoutDuration = null)
    {
        await output.WriteLineAsync($"Type exactly {token} within 60 seconds to proceed:");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration ?? TimeSpan.FromSeconds(60));
        // Windows Console.In's SyncTextReader performs a blocking ReadLine even
        // through ReadLineAsync. Bound our wait independently. On expiry this
        // attempt ends; no second reader is started over the abandoned read.
        try
        {
            string? response = await Task.Run(input.ReadLine).WaitAsync(timeout.Token);
            return !timeout.IsCancellationRequested && response == token;
        }
        catch (OperationCanceledException) { return false; }
    }

    internal static async Task<bool> ConfirmLiveApprovalAsync(TextReader input, TextWriter output,
        string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool accepted = await ConfirmAsync(input, output, token, cancellationToken);
        // Caller loss and Auto are stop requests, never an operator decline.
        cancellationToken.ThrowIfCancellationRequested();
        return accepted;
    }

    internal static So6120SilentTrialResult RecordGuardianOutcome(Action<object> record, So6120GuardianReceipt receipt)
    {
        // A failed invocation can have no trial result. Preserve its independent
        // Auto, cleanup, journal and release facts before raising the failure.
        record(new { phase = "guardianReceipt", receipt });
        return receipt.Trial ?? throw new InvalidOperationException(receipt.Failure ?? "Guardian returned no trial result.");
    }

    private static async Task<int> RunWindowsAsync(PerformancePreset profile, So6120TrialTargetPolicy targetPolicy, string planText,
        string root, TextReader input, TextWriter output, So6120GuardianSignals signals)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected
            || !Environment.UserInteractive)
        { await output.WriteLineAsync("An attended Windows console with unredirected input/output is required."); return 2; }
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator))
        { await output.WriteLineAsync("Start the reviewed runner in an elevated console; it never requests UAC itself."); return 2; }
        signals.StopToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(planText);
        signals.StopToken.ThrowIfCancellationRequested();
        if (Directory.Exists(root) || File.Exists(root))
        { await output.WriteLineAsync("Run root already exists; never reuse an attempt directory."); return 2; }
        _ = So6120DirectDutyJournal.ValidateRunRoot(root);
        Directory.CreateDirectory(root);
        using var manifest = new FileStream(Path.Combine(root, "attended-run.jsonl"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(manifest, new UTF8Encoding(false), leaveOpen: true);
        void Record(object value)
        {
            string row = JsonSerializer.Serialize(value, Json);
            writer.WriteLine(row); writer.Flush(); manifest.Flush(flushToDisk: true);
        }
        CancellationToken cancellationToken = signals.StopToken;
        using var registration = ConsoleCancellation.Register(() => signals.Receive(2));
        var clock = TimeProvider.System;
        var run = Guid.NewGuid();
        string capturedProfile = profile.ToString();
        bool halfCycle = targetPolicy != So6120TrialTargetPolicy.PlusFour;
        bool stop = targetPolicy == So6120TrialTargetPolicy.HalfCycleThenStop;
        string? requestedPowerSource = profile == PerformancePreset.Balanced ? "USB-C" : null;
        So6120WindowsDutyAcquisition? acquisition = null;
        So6120DirectDutySession? session = null;
        So6120NativeAcquisitionException? acquisitionFailure = null;
        bool cleanupConfirmed = false;
        bool declinedAfterConfirmedBaseline = false;
        bool terminalRecorded = false;
        bool acquisitionStarted = false;
        bool knownNoSettingWrite = false;
        string? deferredResult = null;
        string? deferredFailure = null;
        So6120GuardianStopReason? guardianStopReason = null;
        var releaseFailures = new List<string>();
        int exitCode = 2;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(new
            {
                phase = "created",
                run,
                capturedProfile,
                requestedPowerSource,
                utc = DateTimeOffset.UtcNow,
                runnerSha256 = HashFile(Assembly.GetExecutingAssembly().Location),
                FirmwareHash,
                HandleControlHash,
                physicalStimulusWindowsPerformed = false,
                actualRpmScaleEstablished = false,
                guardianPid = Environment.ProcessId,
                callerHasHardwareAccess = false,
            });
            var read = So6120WindowsSupervisedMetadata.Capture(run, clock);
            if (read.Metadata is not { } metadata) { throw new InvalidOperationException($"Metadata unavailable: {read.FailedStage}/{read.Error}"); }
            var scope = So6120WindowsDirectDutyPlatform.ObserveCurrentScope(metadata);
            var residuals = So6120AcceptedResiduals.UnnamedFileHandles | So6120AcceptedResiduals.UncoveredAcpiPaths
                | So6120AcceptedResiduals.UnobservedWriters;
            var decision = new
            {
                phase = "scopeAndLimits",
                run,
                capturedProfile,
                requestedPowerSource,
                windowsAcLineStatus = metadata.Session.AcLineStatus,
                scope,
                proposedResidualAcceptance = residuals.ToString(),
                handleControlOnly = HandleControlHash,
                physicalStimulusWindowsPerformed = false,
                universalExclusionEstablished = false,
                physicalRecoveryEstablished = false,
                rule = targetPolicy switch
                {
                    So6120TrialTargetPolicy.HalfCycleThenStop => "One fixed half-cycle then zero trial with exact Balanced/Auto handback. Stop all commands after ambiguity.",
                    So6120TrialTargetPolicy.HalfCycle => "One fixed half-cycle trial with Balanced/Auto handback. Stop all commands after ambiguity.",
                    _ => "One fixed +4-count trial with captured-profile Auto handback. Stop all commands after ambiguity.",
                },
            };
            Record(decision);
            await output.WriteLineAsync(JsonSerializer.Serialize(decision, Json));
            await output.WriteLineAsync("The prior handle test qualified visibility only. No action-trigger challenge or hardware lock is claimed. Accept the unobserved-writer risk and the unresolved-stall procedure for this one attempt.");
            if (profile == PerformancePreset.Balanced)
            { await output.WriteLineAsync("Your BASELINE approval also confirms that USB-C power is connected and will remain unchanged during this attempt."); }
            string baselineToken = $"BASELINE {run:N}";
            if (!await ConfirmLiveApprovalAsync(input, output, baselineToken, cancellationToken))
            { Record(new { phase = "declinedBeforeAcquisition" }); throw new TrialDeclinedException(); }
            cancellationToken.ThrowIfCancellationRequested();
            string? operatorReportedPowerSource = requestedPowerSource;
            long stamp = clock.GetTimestamp();
            var challenge = new So6120CallerChallengeContext(new(run, metadata.Stamp.ConsoleSession, clock, stamp),
                Guid.NewGuid(), Hash(decision), scope, scope, residuals, residuals);
            var consent = new
            {
                run,
                baselineToken,
                stamp,
                scope,
                capturedProfile,
                operatorReportedPowerSource,
                acceptedResiduals = residuals.ToString(),
                procedure = planText,
            };
            Record(new { phase = "baselineConsent", consent });
            var identity = new So6120JournalIdentity(new(run, Hash(scope), FirmwareHash, Guid.NewGuid()), profile, null, null,
                Hash(scope), FirmwareHash, Guid.NewGuid(), Hash(consent),
                $"run-start={DateTimeOffset.UtcNow:O};uptime-ms={Environment.TickCount64}");
            using var temperatures = new SafeWindowsFanTemperatureSource();
            var thermal = new So6120TrialThermalGuard(temperatures, clock,
                observation => Record(new { phase = "thermal", observation }));
            // A warm/unavailable baseline must not consume the machine's
            // one-shot latch. Preparation repeats this check after acquisition.
            if (!thermal.Refresh()) { throw new InvalidOperationException("Thermal precheck refused before InpOut acquisition."); }
            cancellationToken.ThrowIfCancellationRequested();
            acquisitionStarted = true;
            acquisition = So6120WindowsDirectDutyPlatform.Acquire(new So6120AttendedBaselineConsent(identity,
                metadata.Stamp.ConsoleSession, clock, challenge, So6120WindowsDirectDutyPlatform.ObserveCurrentScope), cancellationToken);
            session = So6120DirectDutySession.CreateInjected(root, acquisition.Binding, acquisition.Port,
                acquisition.EvidenceSource, clock);
            // Console backpressure must not delay an approved Auto handback.
            // Mandatory transaction evidence is already durably journaled by
            // the session. Buffer this small optional progress summary and
            // publish it only after execution has restored or fenced.
            var progressBuffer = new List<So6120SilentTrialProgress>(16);
            Action<So6120SilentTrialProgress>? guardianProgress = null;
            var hooks = new So6120SilentTrialHooks(thermal.Refresh, thermal.CachedAdmission, progress =>
            {
                if (progressBuffer.Count == 16) { throw new InvalidOperationException("Trial progress bound exceeded."); }
                progressBuffer.Add(progress);
                guardianProgress?.Invoke(progress);
            });
            var plan = targetPolicy switch
            {
                So6120TrialTargetPolicy.HalfCycleThenStop => session.PrepareBalancedStop(hooks, cancellationToken),
                So6120TrialTargetPolicy.HalfCycle => session.PrepareBalancedHalfCycle(hooks, cancellationToken),
                _ when profile == PerformancePreset.Balanced => session.PrepareBalancedIncrease(hooks, cancellationToken),
                _ => session.PrepareSilentIncrease(hooks, cancellationToken),
            };
            var derivedPlan = new { phase = "derivedPlan", capturedProfile, operatorReportedPowerSource, plan };
            Record(derivedPlan);
            await output.WriteLineAsync(JsonSerializer.Serialize(derivedPlan, new JsonSerializerOptions { WriteIndented = true }));
            if (halfCycle)
            {
                byte cycle = plan.Baseline.Snapshot.Controls.Cycle;
                string fraction = FormattableString.Invariant($"{plan.RequestedDuty1}/{cycle} per fan, about {100.0 * plan.RequestedDuty1 / cycle:F1}% of the captured cycle count");
                await output.WriteLineAsync($"Approve the fixed raw targets of {fraction}, and exact Balanced/Auto restoration. This can be a larger increase than +4 counts. The cycle fraction has no calibrated electrical duty or RPM interpretation.");
                await output.WriteLineAsync("Either fan at or above its approved target stops entry before a write. The runner keeps the approved targets. Reads and writes are separate operations.");
                if (stop)
                {
                    await output.WriteLineAsync("This approval also covers B8=0 followed by B9=0 under FE after both fans read back half-cycle PWM and nonzero tach. Both fans may stop until the approved Auto handback. Coast and handback add time beyond the zero writes.");
                }
            }
            else
            {
                await output.WriteLineAsync($"Approve the fixed PWM targets and the exact {capturedProfile}/Auto restoration once. The targets are +4 from the captured baseline. Each must be +1 to +8 above its fresh actual PWM reading before entry and each entry write.");
                await output.WriteLineAsync("The runner keeps the approved targets. Changed controls or either increase outside the range stops entry. Reads and writes are separate operations.");
            }
            string writeToken = $"APPLY {plan.PlanId:N} {plan.PlanSha256}";
            if (!await ConfirmLiveApprovalAsync(input, output, writeToken, cancellationToken))
            { Record(new { phase = "declinedAfterBaseline", plan.PlanId }); declinedAfterConfirmedBaseline = true; throw new TrialDeclinedException(); }
            cancellationToken.ThrowIfCancellationRequested();
            var writeConsent = new { writeToken, run, capturedProfile, plan.PlanId, plan.PlanSha256, stamp = clock.GetTimestamp() };
            Record(new { phase = "writeConsent", writeConsent });
            var ownedSession = session;
            var ownedAcquisition = acquisition;
            var approval = new So6120SilentTrialApproval(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), Hash(writeConsent));
            // The existing two attended prompts finish before arming the
            // execution cutoff. Signals have covered liveness throughout
            // preparation, and attach atomically before the worker starts.
            var guardian = So6120TrialGuardian.Start(progress =>
            {
                guardianProgress = progress;
                return new(ownedSession, plan, approval, ownedAcquisition,
                    () => ownedAcquisition.CloseEvidence.State == So6120NativeCloseState.Closed);
            }, clock, So6120GuardianOptions.Default with { OperationLimit = TimeSpan.FromSeconds(45) }, signals);
            var receipt = await guardian.Completion;
            guardianStopReason = receipt.StopReason;
            if (!receipt.ReleaseCompleted) { releaseFailures.Add($"Guardian:{receipt.Failure ?? "Release unconfirmed"}"); }
            cleanupConfirmed = receipt.Trial?.Restoration is { Success: true, CleanupConfirmed: true, CommandsFenced: false };
            knownNoSettingWrite = receipt.Trial?.NoSettingWritePublished == true;
            var result = RecordGuardianOutcome(Record, receipt);
            foreach (var progress in progressBuffer) { Record(new { phase = "progress", progress }); }
            Record(new { phase = "trialResult", result });
            deferredResult = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            exitCode = receipt.Resolved && result.Success ? 0 : 2;
        }
        catch (TrialDeclinedException) { exitCode = 3; }
        catch (Exception error)
        {
            exitCode = 2;
            acquisitionFailure = error as So6120NativeAcquisitionException;
            // No guessed cleanup in exception handling. Only ExecuteApproved's
            // explicit resolved-state restoration may publish further commands.
            try
            {
                Record(new
                {
                    phase = "runnerFailure",
                    type = error.GetType().Name,
                    error.Message,
                    nativeError = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                    openedButAttachFailed = acquisitionFailure is not null,
                    acquisitionFailureClose = acquisitionFailure?.CloseEvidence,
                });
            }
            catch { }
            deferredFailure = $"Stopped: {error.GetType().Name}: {error.Message}. No retry or alternate writer.";
        }
        finally
        {
            try { session?.Dispose(); }
            catch (Exception error) { exitCode = 2; releaseFailures.Add($"Session:{error.GetType().Name}"); }
            try { acquisition?.Dispose(); }
            catch (Exception error) { exitCode = 2; releaseFailures.Add($"Acquisition:{error.GetType().Name}"); }
            try
            {
                Record(new
                {
                    phase = "closed",
                    cleanupConfirmed,
                    knownNoSettingWrite,
                    acquisitionStarted,
                    close = acquisition?.CloseEvidence,
                    acquisitionFailureClose = acquisitionFailure?.CloseEvidence,
                    callerStopReason = signals.StopReason,
                    guardianStopReason,
                    retainsNativeOwnership = NativeReleaseRequiresHold(acquisition?.CloseEvidence, acquisitionFailure?.CloseEvidence),
                    releaseFailures,
                });
                terminalRecorded = true;
            }
            catch { exitCode = 2; }
        }
        bool closed = releaseFailures.Count == 0 && acquisitionFailure is null
            && (acquisition is null || acquisition.CloseEvidence.State == So6120NativeCloseState.Closed);
        bool retainNativeOwnership = NativeReleaseRequiresHold(acquisition?.CloseEvidence, acquisitionFailure?.CloseEvidence);
        if (!closed) { exitCode = 2; }
        string completionMessage;
        if (terminalRecorded && closed && cleanupConfirmed)
        { completionMessage = "Auto and original owned fields verified; native handle closed. Wait for this process to exit before manually reopening OpenBlade. Check trialResult separately for PWM observation success."; }
        else if (terminalRecorded && closed && exitCode == 3 && (acquisition is null || declinedAfterConfirmedBaseline))
        { completionMessage = "Declined with no setting writes; native handle released. Wait for process exit before reopening OpenBlade."; }
        else if (terminalRecorded && !acquisitionStarted)
        { completionMessage = "Refused before InpOut acquisition; no mailbox commands were issued and no machine attempt latch was consumed."; }
        else if (terminalRecorded && closed && knownNoSettingWrite)
        { completionMessage = "Refused before any setting write was published; native handle closed. The trial did not change fan settings. Retain the journal and attempt latch for review."; }
        else
        { completionMessage = "Attempt incomplete. Leave OpenBlade stopped; preserve this console/report and reconcile before any new hardware command."; }
        try
        {
            try { Record(new { phase = "completion", exitCode, message = completionMessage }); }
            catch
            {
                exitCode = 2;
                completionMessage = "Final report could not be saved. Leave OpenBlade stopped; preserve the run folder and reconcile before any new hardware command.";
            }
            // Console backpressure and output failures must not postpone
            // native release or the durable terminal row on any path.
            if (deferredResult is not null) { await output.WriteLineAsync(deferredResult); }
            if (deferredFailure is not null) { await output.WriteLineAsync(deferredFailure); }
            await output.WriteLineAsync(completionMessage);
            if (retainNativeOwnership)
            { await output.WriteLineAsync("Native close is unconfirmed. This guardian will remain alive with its retained ownership. Leave OpenBlade stopped and obtain supervised reconciliation; it will not retry, read more console input or exit automatically."); }
        }
        finally
        {
            // Even an output exception must not drop an unconfirmed native
            // ownership lifetime. The operator's reconciliation is external;
            // caller EOF/Auto cannot authorize terminating this owner.
            if (retainNativeOwnership) { await Task.Delay(Timeout.InfiniteTimeSpan); }
            GC.KeepAlive(acquisition);
            GC.KeepAlive(acquisitionFailure);
        }
        return exitCode;
    }

    internal static bool NativeReleaseRequiresHold(So6120NativeCloseEvidence? acquired, So6120NativeCloseEvidence? attachFailure) =>
        acquired is not null && acquired.State != So6120NativeCloseState.Closed
        || attachFailure is not null && attachFailure.State != So6120NativeCloseState.Closed;

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static string HashFile(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private sealed class TrialDeclinedException : Exception;
}
