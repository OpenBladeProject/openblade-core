using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using static OpenBlade.Capture.So6120WindowsDirectDutyPlatform;

namespace OpenBlade.Capture;

internal static class So6120StagingRecoveryCommand
{
    internal const string Name = "restore-so6120-staging";
    private static readonly Guid PriorRun = new("a8380b7c-4ac5-4651-a740-20b527d9f193");
    private static readonly Guid ObservationRun = new("3c6d7dc5-dc5b-4229-9fba-3b070919a918");
    private const string PriorJournalSha256 = "B81FDF15F0606D0690593450D6F080D802ACE6A4871C4340C06F899D25B9F73F";
    private const string PriorLogSha256 = "7AC21E7108D4B257684370AD60B9734BDDF03D2B7F9F80E812959342A7500B01";
    private const string PriorReceiptSha256 = "49C3E400099EE5AFE1ED20BE4955CEB3569FD0AD3F7B7105CEB318E452CD3671";
    private const string ObservationLogSha256 = "06D87AC344530642B9A3E8E5569056937CF632CFE8D230F6DB252EB919D97D08";
    private const string ObservationReceiptSha256 = "F85E6879FA2429E870822C06D2B39DF2D49E632D247B2245218BF2B696C25486";

    private const string Plan = """
        SO6120 staging-only recovery
        Keep OpenBlade stopped, external power connected, and peripherals unchanged.
        This command is pinned to the reviewed interrupted Balanced trial and subsequent observation.
        Live operation requires an elevated attended console and explicit RESTORE approval.
        A separate one-shot recovery marker is durably created before the first mailbox command.
        The original attempt marker and evidence remain read-only.
        Fresh typed reads must confirm active Balanced/Automatic, override and policy command both zero,
        and staged bytes matching the saved zero or the originally submitted count.
        Restore only changed B8 then B9 to zero with distinct readback and full checkpoints.
        Stop all mailbox commands after any failure or ambiguity. Leave OpenBlade stopped pending review.
        A stalled native call has no software time bound. Preserve the process if a call stalls.
        Usage: OpenBlade.Capture restore-so6120-staging --prior-run-root <original run directory> --observation-root <observation directory> --run-root <new absolute directory>
        """;

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, string, string, TextReader, TextWriter, Task<int>>? live = null)
    {
        if (args.Count == 0 || args.Count == 1 && args[0] == "--plan")
        { await output.WriteLineAsync(Plan); return 0; }
        if (args.Count != 6 || args[0] != "--prior-run-root" || args[2] != "--observation-root" || args[4] != "--run-root")
        { await output.WriteLineAsync(Plan); return 1; }
        string priorRoot, observationRoot, root;
        try
        {
            priorRoot = So6120DirectDutyJournal.ValidateRunRoot(args[1]);
            observationRoot = So6120DirectDutyJournal.ValidateRunRoot(args[3]);
            root = So6120DirectDutyJournal.ValidateRunRoot(args[5]);
            if (Directory.Exists(root) || File.Exists(root) || Under(root, priorRoot) || Under(root, observationRoot)
                || priorRoot.Equals(observationRoot, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException("Distinct evidence directories and a new recovery directory are required."); }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException
            or InvalidDataException or UnauthorizedAccessException)
        { await output.WriteLineAsync(Plan); return 1; }
        return await (live ?? RunWindowsAsync)(priorRoot, observationRoot, root, input, output);
    }

    private static bool Under(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RunWindowsAsync(string priorRoot, string observationRoot, string root,
        TextReader input, TextWriter output)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive)
        { await output.WriteLineAsync("An attended Windows console with unredirected input/output is required."); return 2; }
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator))
        { await output.WriteLineAsync("Start the reviewed recovery runner in an elevated console."); return 2; }

        using var cancellation = new CancellationTokenSource();
        using var registration = ConsoleCancellation.Register(() => ConsoleCancellation.Cancel(cancellation));
        var clock = TimeProvider.System;
        var run = Guid.NewGuid();
        So6120MailboxPortRunResult? result = null;
        string? failure = null;
        bool declined = false;
        try
        {
            Directory.CreateDirectory(root);
            _ = So6120DirectDutyJournal.ValidateRunRoot(root);
            using var file = new FileStream(Path.Combine(root, "staging-recovery.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
            void Record(object value)
            {
                writer.WriteLine(JsonSerializer.Serialize(value));
                writer.Flush();
                file.Flush(flushToDisk: true);
            }

            try
            {
                Record(new { phase = "created", run, utc = clock.GetUtcNow(), priorRoot, observationRoot });
                using var prior = So6120MailboxObservationEvidence.Open(priorRoot);
                RequirePinnedPrior(prior);
                prior.LockAttemptMarker(WindowsNative.AttemptDirectory);
                string observationParent = So6120DirectDutyJournal.ValidateRunRoot(Path.GetDirectoryName(observationRoot)!);
                string logSha256 = prior.LockPinnedEvidence(Path.Combine(observationRoot, "mailbox-observation.jsonl"),
                    1024 * 1024, ObservationLogSha256);
                string receiptSha256 = prior.LockPinnedEvidence(Path.Combine(observationParent, "parent-result.json"),
                    16 * 1024, ObservationReceiptSha256);
                var priorBinding = new { prior.Identity, prior.JournalSha256, prior.AttendedLogSha256, prior.ParentReceiptSha256 };
                var observationBinding = new { run = ObservationRun, logSha256, receiptSha256 };

                var native = new WindowsNative();
                var initial = ReadMetadata(run, null, clock, native, cancellation.Token);
                if (initial.OpenBladeStatus != ServiceControllerStatus.Stopped || initial.OpenBladeWriterPids.Count != 0)
                { throw new InvalidOperationException("OpenBlade must remain stopped."); }
                var scope = ObserveCurrentScope(initial);
                if (!string.Equals(Hash(scope), prior.Identity.DeviceEvidenceSha256, StringComparison.OrdinalIgnoreCase))
                { throw new InvalidOperationException("The current device and controller scope differs from the prior run."); }

                const So6120AcceptedResiduals residuals = So6120AcceptedResiduals.UnnamedFileHandles
                    | So6120AcceptedResiduals.UncoveredAcpiPaths | So6120AcceptedResiduals.UnobservedWriters;
                var decision = new
                {
                    phase = "scopeAndLimits",
                    run,
                    scope,
                    priorBinding,
                    observationBinding,
                    windowsAcLineStatus = initial.Session.AcLineStatus,
                    residuals = residuals.ToString(),
                    saved = new { B8 = 0, B9 = 0 },
                    submitted = new { B8 = So6120StagingRecovery.SubmittedB8, B9 = So6120StagingRecovery.SubmittedB9 },
                    authority = "One staging-only recovery with fresh checkpoints. No other setting writes.",
                };
                Record(decision);
                await output.WriteLineAsync(Plan);
                await output.WriteLineAsync(JsonSerializer.Serialize(decision));
                await output.WriteLineAsync("Approve one attempt despite possible unobserved writers. Keep OpenBlade stopped after this report.");
                string token = $"RESTORE {run:N}";
                declined = !await So6120SilentPwmCommand.ConfirmAsync(input, output, token, cancellation.Token);
                if (!declined)
                {
                    var stamp = initial.Stamp with { Timestamp = clock.GetTimestamp() };
                    var approval = new { phase = "approval", run, token, decision };
                    string approvalSha256 = Hash(approval);
                    Record(new { approval, approvalSha256 });
                    var challenge = new So6120CallerChallengeContext(stamp, Guid.NewGuid(), approvalSha256, scope, scope,
                        residuals, residuals);
                    var handoff = new So6120OpenBladeHandoff(stamp, So6120HandoffOrigin.AlreadyStopped, null);
                    So6120SupervisedMetadata Admit()
                    {
                        var metadata = ReadMetadata(run, stamp.ConsoleSession, clock, native, cancellation.Token);
                        var observed = challenge with { ObservedScope = ObserveCurrentScope(metadata) };
                        cancellation.Token.ThrowIfCancellationRequested();
                        var assessment = So6120SupervisedAdmission.Assess(clock, metadata, observed, handoff);
                        if (assessment != So6120AdmissionAssessment.InputsMatched)
                        { throw new InvalidOperationException($"SO6120 staging recovery admission refused ({assessment})."); }
                        return metadata;
                    }
                    result = So6120PinnedMailboxPort.Run(native, Admit, So6120InpOutBytePort.AttachWindowsHandle,
                        Record, cancellation.Token, port => So6120StagingRecovery.Restore(port, clock, () => Admit(),
                            () => So6120StagingRecovery.CreateClaim(WindowsNative.AttemptDirectory, PriorRun, run, approvalSha256),
                            Record, cancellation.Token));
                    failure = result.Failure;
                }
            }
            catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }
            Record(new
            {
                phase = "closed",
                run,
                declined,
                failure,
                openAttempted = result?.OpenAttempted ?? false,
                stagingVerified = result?.Completed == true,
                close = result?.CloseEvidence,
                releaseFailures = result?.ReleaseFailures,
            });
        }
        catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }

        bool success = failure is null && result?.Success == true;
        await output.WriteLineAsync(success
            ? $"Saved staging bytes verified and native handle closed. See {Path.Combine(root, "staging-recovery.jsonl")}. Leave OpenBlade stopped."
            : declined && failure is null
                ? "Recovery declined before opening InpOut."
                : $"Recovery incomplete ({failure ?? "native close or release was not confirmed"}). Leave OpenBlade stopped.");
        GC.KeepAlive(result);
        return success ? 0 : declined && failure is null ? 3 : 2;
    }

    internal static void RequirePinnedPrior(So6120MailboxObservationEvidence prior)
    {
        if (prior.Identity.Owner.OperationId != PriorRun || prior.Identity.CapturedProfile != OpenBlade.Contracts.PerformancePreset.Balanced
            || prior.JournalSha256 != PriorJournalSha256 || prior.AttendedLogSha256 != PriorLogSha256
            || prior.ParentReceiptSha256 != PriorReceiptSha256)
        { throw new InvalidDataException("The prior failed Balanced trial does not match the reviewed staging evidence."); }
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
