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
    internal const string FirmwareHash = "8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707";
    internal const string HandleControlHash = "AF1A1648AA2FD19DB79352AA31DFAB50B93CD49DF84CE959A7C9AC3FBD01EFE9";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    internal const string PlanText = """
        SO6120 supervised Silent PWM increase: PREPARATION ONLY
        Exact RZ09-0581 / 1532:02E0 / SKU RZ09-05819EN4 / BIOS 4.01 / reported EC 1.04.
        Keep AC connected, awake, idle/light desktop, and peripherals unchanged.
        Select Silent + Automatic, then use OpenBlade's normal Shut down command.
        Service, Tray and Session Agent must all be stopped before acquisition.
        Live run requires an elevated interactive console and a new absolute run directory.
        Metadata inspection opens HID descriptor handles without sending feature reports.
        It first obtains explicit acceptance of the scoped contention/abort limits.
        Only then open InpOut and read the A0 baseline to derive PWM +4 raw counts per fan.
        Both fans need >=4 counts of headroom. No zero/decrease target or preparatory RPM command.
        A +4 request from a natural zero baseline may or may not start a fan.
        A second explicit approval binds those exact observed/derived values.
        Stage B8/B9; set owned F0.6 permission; publish FE last; verify each write.
        After a one-second dwell, restore the captured normal selector to Auto,
        confirm Manual00, then restore the original owned F0/staging bytes.
        Raw tach bytes are recorded; their RPM scale is not established here.
        CPU thermal-zone 10..55 C and confirmed inactive GPU admit entry only.
        High/unknown thermal readings stop entry; they do not block sound Auto cleanup.
        Ctrl+C requests stop at a resolved boundary and the approved Auto handback.
        Ambiguous commands/readback/storage fence ALL mailbox commands, including A0.
        No retry, alternate writer, automatic kill, forced reboot or GUI Auto fallback.
        Stop new entry A1/dwell after 45s or 30s since possible F0; finish confirmation first.
        Auto has its own 60s phase. Each logical transaction retains its full 2s budget.
        These are publication/work cutoffs, not a hard physical exposure guarantee.
        Synchronous native calls can stall beyond those bounds; no hard watchdog is claimed.
        If stalled/unresolved, keep the console/process alive, leave OpenBlade stopped,
        stop workload and seek supervised reconciliation. Physical recovery is unverified.
        This is a small-increase experiment, not fan stop or proof of universal ownership.
        Usage: OpenBlade.Capture validate-so6120-silent-pwm-increase --run-root <new absolute directory>
        """;

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
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, TextReader, TextWriter, Task<int>>? live = null)
    {
        if (!TryParse(args, out string? root))
        { await output.WriteLineAsync(PlanText); return 1; }
        if (root is null) { await output.WriteLineAsync(PlanText); return 0; }
        return await (live ?? RunWindowsAsync)(root, input, output);
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

    private static async Task<int> RunWindowsAsync(string root, TextReader input, TextWriter output)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected
            || !Environment.UserInteractive)
        { await output.WriteLineAsync("An attended Windows console with unredirected input/output is required."); return 2; }
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator))
        { await output.WriteLineAsync("Start the reviewed runner in an elevated console; it never requests UAC itself."); return 2; }
        await output.WriteLineAsync(PlanText);
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
        using var cancellation = new CancellationTokenSource();
        using var registration = ConsoleCancellation.Register(() => ConsoleCancellation.Cancel(cancellation));
        var clock = TimeProvider.System;
        var run = Guid.NewGuid();
        So6120WindowsDutyAcquisition? acquisition = null;
        So6120DirectDutySession? session = null;
        So6120NativeAcquisitionException? acquisitionFailure = null;
        bool cleanupConfirmed = false;
        bool declinedAfterConfirmedBaseline = false;
        bool terminalRecorded = false;
        bool acquisitionStarted = false;
        bool knownNoSettingWrite = false;
        var releaseFailures = new List<string>();
        int exitCode = 2;
        try
        {
            Record(new
            {
                phase = "created",
                run,
                utc = DateTimeOffset.UtcNow,
                runnerSha256 = HashFile(Assembly.GetExecutingAssembly().Location),
                FirmwareHash,
                HandleControlHash,
                physicalStimulusWindowsPerformed = false,
                actualRpmScaleEstablished = false,
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
                scope,
                proposedResidualAcceptance = residuals.ToString(),
                handleControlOnly = HandleControlHash,
                physicalStimulusWindowsPerformed = false,
                universalExclusionEstablished = false,
                physicalRecoveryEstablished = false,
                rule = "One fixed +4-count trial; generic captured-profile Auto handback; stop all commands after ambiguity.",
            };
            Record(decision);
            await output.WriteLineAsync(JsonSerializer.Serialize(decision, Json));
            await output.WriteLineAsync("The prior handle test qualified visibility only. No action-trigger challenge or hardware lock is claimed. Accept the unobserved-writer risk and the unresolved-stall procedure for this one attempt.");
            string baselineToken = $"BASELINE {run:N}";
            if (!await ConfirmAsync(input, output, baselineToken, cancellation.Token))
            { Record(new { phase = "declinedBeforeAcquisition" }); throw new TrialDeclinedException(); }
            cancellation.Token.ThrowIfCancellationRequested();
            long stamp = clock.GetTimestamp();
            var challenge = new So6120CallerChallengeContext(new(run, metadata.Stamp.ConsoleSession, clock, stamp),
                Guid.NewGuid(), Hash(decision), scope, scope, residuals, residuals);
            var consent = new { run, baselineToken, stamp, scope, acceptedResiduals = residuals.ToString(), procedure = PlanText };
            Record(new { phase = "baselineConsent", consent });
            var identity = new So6120JournalIdentity(new(run, Hash(scope), FirmwareHash, Guid.NewGuid()), PerformancePreset.Silent, null, null,
                Hash(scope), FirmwareHash, Guid.NewGuid(), Hash(consent),
                $"run-start={DateTimeOffset.UtcNow:O};uptime-ms={Environment.TickCount64}");
            using var temperatures = new SafeWindowsFanTemperatureSource();
            var thermal = new So6120TrialThermalGuard(temperatures, clock,
                observation => Record(new { phase = "thermal", observation }));
            // A warm/unavailable baseline must not consume the machine's
            // one-shot latch. Preparation repeats this check after acquisition.
            if (!thermal.Refresh()) { throw new InvalidOperationException("Thermal precheck refused before InpOut acquisition."); }
            cancellation.Token.ThrowIfCancellationRequested();
            acquisitionStarted = true;
            acquisition = So6120WindowsDirectDutyPlatform.Acquire(new So6120AttendedBaselineConsent(identity,
                metadata.Stamp.ConsoleSession, clock, challenge, So6120WindowsDirectDutyPlatform.ObserveCurrentScope), cancellation.Token);
            session = So6120DirectDutySession.CreateInjected(root, acquisition.Binding, acquisition.Port,
                acquisition.EvidenceSource, clock);
            // Console backpressure must not delay an approved Auto handback.
            // Mandatory transaction evidence is already durably journaled by
            // the session. Buffer this small optional progress summary and
            // publish it only after execution has restored or fenced.
            var progressBuffer = new List<So6120SilentTrialProgress>(16);
            var hooks = new So6120SilentTrialHooks(thermal.Refresh, thermal.CachedAdmission, progress =>
            {
                if (progressBuffer.Count == 16) { throw new InvalidOperationException("Trial progress bound exceeded."); }
                progressBuffer.Add(progress);
            });
            var plan = session.PrepareSilentIncrease(hooks, cancellation.Token);
            Record(new { phase = "derivedPlan", plan });
            await output.WriteLineAsync(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            await output.WriteLineAsync("Approve these captured raw PWM targets and the exact Auto restoration once. Any changed baseline refuses entry; the runner will not derive new targets.");
            string writeToken = $"APPLY {plan.PlanId:N} {plan.PlanSha256}";
            if (!await ConfirmAsync(input, output, writeToken, cancellation.Token))
            { Record(new { phase = "declinedAfterBaseline", plan.PlanId }); declinedAfterConfirmedBaseline = true; throw new TrialDeclinedException(); }
            cancellation.Token.ThrowIfCancellationRequested();
            var writeConsent = new { writeToken, run, plan.PlanId, plan.PlanSha256, stamp = clock.GetTimestamp() };
            Record(new { phase = "writeConsent", writeConsent });
            var result = session.ExecuteApproved(plan, new So6120SilentTrialApproval(plan.PlanId,
                plan.PlanSha256, Guid.NewGuid(), Hash(writeConsent)), cancellation.Token);
            cleanupConfirmed = result.Restoration is { Success: true, CleanupConfirmed: true, CommandsFenced: false };
            knownNoSettingWrite = result.NoSettingWritePublished;
            foreach (var progress in progressBuffer) { Record(new { phase = "progress", progress }); }
            Record(new { phase = "trialResult", result });
            await output.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            exitCode = cleanupConfirmed && result.Success ? 0 : 2;
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
            await output.WriteLineAsync($"Stopped: {error.GetType().Name}: {error.Message}. No retry or alternate writer.");
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
                    releaseFailures,
                });
                terminalRecorded = true;
            }
            catch { exitCode = 2; }
        }
        bool closed = releaseFailures.Count == 0 && acquisitionFailure is null
            && (acquisition is null || acquisition.CloseEvidence.State == So6120NativeCloseState.Closed);
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
        await output.WriteLineAsync(completionMessage);
        GC.KeepAlive(acquisitionFailure);
        return exitCode;
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static string HashFile(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private sealed class TrialDeclinedException : Exception;
}
