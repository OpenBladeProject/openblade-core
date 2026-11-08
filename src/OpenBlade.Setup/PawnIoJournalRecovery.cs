using System.ComponentModel;
using System.Diagnostics;
using OpenBlade.Platform;

namespace OpenBlade.Setup;

internal sealed record PawnIoInstallJournal(int SchemaVersion, string Sha256, string Stage,
    long? SubmittedUptimeMilliseconds, string? InstallerPath, int? ProcessId, DateTime? ProcessCreatedAt, int? ExitCode)
{
    internal void Validate()
    {
        if (SchemaVersion is not (1 or 2) || Sha256 != WindowsPawnIoSetupOperations.InstallerSha256
            || Stage is not ("SubmissionIntent" or "ProcessStarted" or "ExitObserved" or "NoProcessCreated")
            || ProcessId is <= 0 || ProcessCreatedAt is { Kind: not DateTimeKind.Utc }
            || Stage == "ExitObserved" && !ExitCode.HasValue
            || SubmittedUptimeMilliseconds is < 0
            || SchemaVersion == 2 && (!SubmittedUptimeMilliseconds.HasValue || string.IsNullOrWhiteSpace(InstallerPath))
            || Stage == "NoProcessCreated" && (ProcessId.HasValue || ProcessCreatedAt.HasValue
                || !ExitCode.HasValue || !PawnIoJournalRecovery.IsKnownNoProcessCreated(ExitCode.Value)))
        { throw new InvalidDataException("PawnIO journal identity or stage is invalid."); }
    }
}

internal enum PawnIoRecordedProcessState { NotRunning, Running, Indeterminate }

internal static class PawnIoJournalRecovery
{
    internal static PawnIoSetupInspection Reconcile(PawnIoInstallJournal journal, long currentUptimeMilliseconds,
        PawnIoRecordedProcessState process, Func<PawnIoInstallation?> inspect, out bool clear)
    {
        journal.Validate();
        clear = false;
        if (currentUptimeMilliseconds < 0) { throw new InvalidDataException("Windows uptime is invalid."); }
        // TickCount64 cannot decrease within one boot and is independent of wall
        // clock/timezone adjustment. A rollback proves the old boot ended. If the
        // new boot has already aged beyond the saved uptime, do not guess: an
        // unbound intent stays paused until independent process evidence exists.
        bool rebootProven = journal.SubmittedUptimeMilliseconds is { } submittedUptime
            && currentUptimeMilliseconds < submittedUptime;
        bool noSubmission = journal.Stage == "NoProcessCreated";
        bool observedExit = journal.Stage == "ExitObserved";
        if (process == PawnIoRecordedProcessState.Running
            || !noSubmission && !observedExit && !rebootProven
                && (journal.ProcessCreatedAt is null || journal.ProcessId is null
                    || process != PawnIoRecordedProcessState.NotRunning))
        { throw new InvalidOperationException("PawnIO installation submission is unresolved; the previous installer may still be running. No replay was attempted."); }

        PawnIoInstallation? installed = inspect();
        if (installed is not null)
        {
            clear = true;
            return new(installed);
        }
        if (noSubmission || observedExit && journal.ExitCode is not (0 or 3010))
        {
            // A known creation failure or observed failed exit permits the next
            // explicit setup attempt only after fresh admission found no package.
            clear = true;
            return new(null);
        }
        if (observedExit && journal.ExitCode == 3010)
        {
            // Preserve the marker and report the official reboot outcome, rather
            // than install a second copy before the PnP binding becomes visible.
            return new(null, RebootPending: true);
        }
        throw new InvalidOperationException("The previous PawnIO installer has ended but its submitted changes remain unconfirmed. No automatic replay was attempted.");
    }

    // Native CreateProcess reports these errors before a child exists. Unknown
    // exceptions retain SubmissionIntent because they do not prove non-submission.
    internal static bool IsKnownNoProcessCreated(int error) => error is 2 or 3 or 5 or 193 or 206 or 740;

    internal static PawnIoRecordedProcessState InspectProcess(PawnIoInstallJournal journal)
    {
        if (journal.Stage == "NoProcessCreated" || journal.Stage == "ExitObserved")
        { return PawnIoRecordedProcessState.NotRunning; }
        if (journal.ProcessId is not { } id || journal.ProcessCreatedAt is not { } created)
        { return PawnIoRecordedProcessState.Indeterminate; }
        try
        {
            using Process process = Process.GetProcessById(id);
            if (process.HasExited) { return PawnIoRecordedProcessState.NotRunning; }
            DateTime observedCreated = process.StartTime.ToUniversalTime();
            if (observedCreated != created) { return PawnIoRecordedProcessState.NotRunning; }
            return ClassifyProcessIdentity(created, journal.InstallerPath, observedCreated, process.MainModule?.FileName);
        }
        catch (ArgumentException) { return PawnIoRecordedProcessState.NotRunning; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { return PawnIoRecordedProcessState.Indeterminate; }
    }

    internal static PawnIoRecordedProcessState ClassifyProcessIdentity(DateTime expectedCreated,
        string? expectedPath, DateTime observedCreated, string? observedPath) =>
        expectedCreated != observedCreated ? PawnIoRecordedProcessState.NotRunning
            : string.Equals(expectedPath, observedPath, StringComparison.OrdinalIgnoreCase)
                ? PawnIoRecordedProcessState.Running : PawnIoRecordedProcessState.Indeterminate;
}
