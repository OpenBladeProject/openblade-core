using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using static OpenBlade.Capture.So6120WindowsDirectDutyPlatform;

namespace OpenBlade.Capture;

internal static class So6120MailboxObservationCommand
{
    internal const string Name = "observe-so6120-mailbox";
    private const string Plan = """
        SO6120 mailbox observation
        Leave OpenBlade stopped. Keep external power connected and peripherals unchanged.
        This records two command-bracketed scans of indices 90, 91, 92, 93, 94 and A0.
        Index selection writes port 300; the observer only reads port 301.
        Matching scans cannot identify a writer, verify a prior request, or authorize recovery.
        The original run files and machine attempt marker are opened read-only.
        Live observation requires an elevated attended console and explicit approval.
        A stalled native call has no software time bound. Preserve the process and leave OpenBlade stopped.
        An unconfirmed native close is reported without an automatic retry.
        The driver pin and run reservation are retained through the report. Process exit ends that retention.
        Usage: OpenBlade.Capture observe-so6120-mailbox --prior-run-root <original run directory> --run-root <new absolute directory>
        """;

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input, TextWriter output,
        Func<string, string, TextReader, TextWriter, Task<int>>? live = null)
    {
        if (args.Count == 0 || args.Count == 1 && args[0] == "--plan")
        { await output.WriteLineAsync(Plan); return 0; }
        if (args.Count != 4 || args[0] != "--prior-run-root" || args[2] != "--run-root")
        { await output.WriteLineAsync(Plan); return 1; }
        string priorRoot, root;
        try
        {
            priorRoot = So6120DirectDutyJournal.ValidateRunRoot(args[1]);
            root = So6120DirectDutyJournal.ValidateRunRoot(args[3]);
            if (root.Equals(priorRoot, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(priorRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(root) || File.Exists(root))
            { throw new InvalidDataException("A new observation directory outside the prior run is required."); }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException
            or InvalidDataException or UnauthorizedAccessException)
        { await output.WriteLineAsync(Plan); return 1; }
        return await (live ?? RunWindowsAsync)(priorRoot, root, input, output);
    }

    private static async Task<int> RunWindowsAsync(string priorRoot, string root, TextReader input, TextWriter output)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive)
        { await output.WriteLineAsync("An attended Windows console with unredirected input/output is required."); return 2; }
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(windowsIdentity).IsInRole(WindowsBuiltInRole.Administrator))
        { await output.WriteLineAsync("Start the reviewed observer in an elevated console."); return 2; }

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
            using var file = new FileStream(Path.Combine(root, "mailbox-observation.jsonl"), FileMode.CreateNew,
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
                Record(new { phase = "created", run, utc = clock.GetUtcNow(), priorRoot });
                using var prior = So6120MailboxObservationEvidence.Open(priorRoot);
                prior.LockAttemptMarker(WindowsNative.AttemptDirectory);
                var priorBinding = new
                {
                    prior.Identity,
                    prior.JournalSha256,
                    prior.AttendedLogSha256,
                    prior.ParentReceiptSha256,
                };
                Record(new { phase = "priorEvidence", priorBinding });
                var native = new WindowsNative();
                var initial = ReadMetadata(run, null, clock, native, cancellation.Token);
                if (initial.OpenBladeStatus != System.ServiceProcess.ServiceControllerStatus.Stopped
                    || initial.OpenBladeWriterPids.Count != 0)
                { throw new InvalidOperationException("OpenBlade must remain stopped."); }
                var scope = ObserveCurrentScope(initial);
                if (!string.Equals(Hash(scope), prior.Identity.DeviceEvidenceSha256, StringComparison.OrdinalIgnoreCase))
                { throw new InvalidOperationException("Current device and controller scope differs from the prior run."); }

                const So6120AcceptedResiduals residuals = So6120AcceptedResiduals.UnnamedFileHandles
                    | So6120AcceptedResiduals.UncoveredAcpiPaths | So6120AcceptedResiduals.UnobservedWriters;
                var decision = new
                {
                    phase = "scopeAndLimits",
                    run,
                    scope,
                    priorBinding,
                    windowsAcLineStatus = initial.Session.AcLineStatus,
                    residuals = residuals.ToString(),
                    action = "Two fixed scans with index selection on port 300 and reads on port 301.",
                };
                Record(decision);
                await output.WriteLineAsync(Plan);
                await output.WriteLineAsync(JsonSerializer.Serialize(decision));
                await output.WriteLineAsync("Approve one observation despite possible unobserved writers. Leave OpenBlade stopped after the report.");
                string token = $"OBSERVE {run:N}";
                declined = !await So6120SilentPwmCommand.ConfirmAsync(input, output, token, cancellation.Token);
                if (!declined)
                {
                    var stamp = initial.Stamp with { Timestamp = clock.GetTimestamp() };
                    var approval = new { phase = "approval", run, token, scope, priorBinding, residuals = residuals.ToString() };
                    Record(approval);
                    var challenge = new So6120CallerChallengeContext(stamp, Guid.NewGuid(), Hash(approval), scope, scope,
                        residuals, residuals);
                    var handoff = new So6120OpenBladeHandoff(stamp, So6120HandoffOrigin.AlreadyStopped, null);
                    So6120SupervisedMetadata Admit()
                    {
                        var metadata = ReadMetadata(run, stamp.ConsoleSession, clock, native, cancellation.Token);
                        var observed = challenge with { ObservedScope = ObserveCurrentScope(metadata) };
                        cancellation.Token.ThrowIfCancellationRequested();
                        var assessment = So6120SupervisedAdmission.Assess(clock, metadata, observed, handoff);
                        if (assessment != So6120AdmissionAssessment.InputsMatched)
                        { throw new InvalidOperationException($"SO6120 observation admission refused ({assessment})."); }
                        return metadata;
                    }
                    result = So6120MailboxObservation.Observe(native, clock, Admit, So6120InpOutBytePort.AttachWindowsHandle,
                        Record, cancellation.Token);
                    failure = result.Failure;
                }
            }
            catch (Exception error) { failure = Describe(error); }
            Record(new
            {
                phase = "closed",
                run,
                declined,
                failure,
                openAttempted = result?.OpenAttempted ?? false,
                close = result?.CloseEvidence,
                releaseFailures = result?.ReleaseFailures,
                scansMatched = result?.Completed,
            });
        }
        catch (Exception error) { failure = Describe(error); }

        bool success = failure is null && result?.Success == true;
        await output.WriteLineAsync(success
            ? $"Two mailbox scans matched and the native handle closed. See {Path.Combine(root, "mailbox-observation.jsonl")}. Leave OpenBlade stopped."
            : declined && failure is null
                ? "Observation declined before opening InpOut."
                : $"Observation incomplete ({failure ?? "native close or release was not confirmed"}). Leave OpenBlade stopped.");
        GC.KeepAlive(result);
        return success ? 0 : declined && failure is null ? 3 : 2;
    }

    private static string Describe(Exception error) => error is Win32Exception native
        ? $"{error.GetType().Name}: {error.Message} (Win32 {native.NativeErrorCode})"
        : $"{error.GetType().Name}: {error.Message}";

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
