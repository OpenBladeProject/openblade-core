using System.ComponentModel;
using System.Text.Json;

namespace OpenBlade.Capture;

// This sidecar describes startup admission only. It is never a trial receipt,
// Auto confirmation, ownership token or permission to reuse a run directory.
internal static class So6120GuardianStartup
{
    internal const string RecoveryScope = "CallerProcessAndOwnedJob";
    internal const string ScopeNotice = "Supervised recovery covers this caller process and its dedicated job only. "
        + "Keep the guardian console open. Guardian termination, unknown outer host-job termination, sleep and OS failure are not covered.";

    internal static string GetPath(string root) => root + ".guardian-startup.json";

    internal static async Task<bool> CompleteAdmissionAsync(string trial, string root,
        So6120GuardianIsolationEvidence isolation, TextWriter output)
    {
        try
        {
            await output.WriteLineAsync(ScopeNotice);
            await output.WriteLineAsync($"Caller isolation verified. Ambient Windows job membership: {isolation.InAmbientJob}. "
                + $"Startup diagnostic target: {GetPath(root)}");
            // Keep the create-once sidecar available for a console refusal.
            // Only publish admission after all required startup reporting works.
            Write(trial, root, "admitted", admitted: true, isolation);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A partial storage write is preserved and still blocks acquisition.
            await RefuseAsync(trial, root, "startup-report", So6120GuardianHost.OwnerFailure, error, output, isolation);
            return false;
        }
    }

    internal static void Write(string trial, string root, string stage, bool admitted,
        So6120GuardianIsolationEvidence? isolation = null, Exception? error = null)
    {
        if (!Path.IsPathFullyQualified(root)) { throw new ArgumentException("The startup run root must be fully qualified.", nameof(root)); }
        var record = new
        {
            schemaVersion = 1,
            recordKind = "guardian-startup-admission",
            trial,
            runRoot = root,
            processId = Environment.ProcessId,
            processRole = stage == "launch" ? "caller" : "owner",
            observedAtUtc = DateTimeOffset.UtcNow,
            stage,
            admitted,
            acquisitionStartedAtRecord = false,
            recoveryScope = RecoveryScope,
            callerProcessId = isolation?.CallerProcessId,
            ambientJobMembership = isolation?.InAmbientJob,
            errorType = error?.GetType().FullName,
            errorMessage = error is null ? null : error.Message[..Math.Min(error.Message.Length, 4096)],
            nativeErrorCode = (error as Win32Exception)?.NativeErrorCode,
        };
        // The reviewed parent directory already exists. Never create the trial
        // root or overwrite a prior attempt, including a partial failed receipt.
        using var stream = new FileStream(GetPath(root), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, record);
        stream.Flush(flushToDisk: true);
    }

    internal static async Task<int> RefuseAsync(string trial, string root, string stage, int exitCode,
        Exception error, TextWriter output, So6120GuardianIsolationEvidence? isolation = null)
    {
        string report = PersistRefusal(trial, root, stage, error, isolation);
        await ReportRefusalAsync(report, output);
        return exitCode;
    }

    internal static string PersistRefusal(string trial, string root, string stage,
        Exception error, So6120GuardianIsolationEvidence? isolation = null)
    {
        string persistence;
        try
        {
            Write(trial, root, stage, admitted: false, isolation, error);
            persistence = $"Startup diagnostic: {GetPath(root)}";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            persistence = $"Startup diagnostic could not be saved: {failure.GetType().Name}: {failure.Message}. "
                + "Preserve any existing sidecar; no retry was attempted.";
        }

        return $"Guardian {stage} refused before acquisition: {error.Message}. {persistence}";
    }

    internal static async Task ReportRefusalAsync(string report, TextWriter output)
    {
        // The owner closes its verification handles before attempting console
        // output. A blocked console must not prolong the caller job's lifetime.
        try { await output.WriteLineAsync(report); }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException) { }
    }
}
