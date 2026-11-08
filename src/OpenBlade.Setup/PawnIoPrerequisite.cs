using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Platform;

namespace OpenBlade.Setup;

public interface IPawnIoPrerequisite
{
    Task<bool> EnsureAsync(string stagedPayloadDirectory, CancellationToken cancellationToken);
}

internal interface IPawnIoSetupOperations
{
    PawnIoSetupInspection Inspect(CancellationToken cancellationToken);
    Task<PawnIoInstallResult> InstallAsync(string installer, CancellationToken cancellationToken);
}

internal sealed record PawnIoSetupInspection(PawnIoInstallation? Installation, bool RebootPending = false);
internal sealed record PawnIoInstallResult(int ExitCode, PawnIoInstallation? VerifiedInstallation);

/// <summary>The shared full-install route, including broker installs. Never removes shared PawnIO.</summary>
internal sealed class PawnIoPrerequisite(IPawnIoSetupOperations operations) : IPawnIoPrerequisite
{
    public async Task<bool> EnsureAsync(string stagedPayloadDirectory, CancellationToken cancellationToken)
    {
        PawnIoSetupInspection inspection = operations.Inspect(cancellationToken);
        if (inspection.RebootPending) { return true; }
        if (inspection.Installation is { } installed)
        {
            Trace.TraceInformation("PawnIO prerequisite: reused trusted installed driver; running={0}.", installed.Started);
            // Never reinstall a stopped shared driver or declare it sensor-ready.
            // PnP may finish starting the existing signed package after restart.
            return !installed.Started;
        }
        PawnIoInstallResult result = await operations.InstallAsync(Path.Combine(stagedPayloadDirectory, "PawnIO_setup.exe"), cancellationToken);
        int exitCode = result.ExitCode;
        Trace.TraceInformation("PawnIO prerequisite: official installer exit code {0}.", exitCode);
        if (exitCode == 3010)
        {
            // Installer success is not sensor readiness. Runtime independently admits the
            // actual signed, started PnP binding before opening LibreHardwareMonitor.
            return true;
        }
        if (exitCode != 0)
        { throw new InvalidOperationException($"PawnIO installation failed (exit code {exitCode}); no alternate installation was attempted."); }
        if (result.VerifiedInstallation is not { } readback)
        { throw new InvalidOperationException("PawnIO installer exited successfully but independent driver binding readback is missing."); }
        return !readback.Started;
    }
}

internal sealed class WindowsPawnIoSetupOperations(SetupPaths paths) : IPawnIoSetupOperations
{
    internal const string InstallerSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    internal const string InstallerPublisher = "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU";
    // This shared-driver submission latch survives OpenBlade uninstall/purge.
    // Removing per-product settings must never authorize replay of an uncertain
    // vendor installation still running independently of the product transaction.
    private string JournalDirectory => Path.Combine(paths.MachineDataRoot, "OpenBlade.PawnIOSetup");
    private string JournalPath => Path.Combine(JournalDirectory, "install-intent.json");

    public PawnIoSetupInspection Inspect(CancellationToken cancellationToken)
    {
        if (File.Exists(JournalPath))
        {
            EnsureNoReparsePoints(JournalPath);
            using FileStream file = File.Open(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > 16384) { throw new InvalidDataException("PawnIO journal length is invalid."); }
            PawnIoInstallJournal journal = JsonSerializer.Deserialize<PawnIoInstallJournal>(file)
                ?? throw new InvalidDataException("PawnIO journal is invalid.");
            journal.Validate();
            long uptime = Environment.TickCount64;
            PawnIoRecordedProcessState process = PawnIoJournalRecovery.InspectProcess(journal);
            PawnIoSetupInspection result = PawnIoJournalRecovery.Reconcile(journal, uptime, process,
                () => PawnIoRuntimeAdmission.Inspect(cancellationToken), out bool clear);
            file.Dispose();
            if (clear) { File.Delete(JournalPath); }
            return result;
        }
        return new(PawnIoRuntimeAdmission.Inspect(cancellationToken));
    }

    public async Task<PawnIoInstallResult> InstallAsync(string installer, CancellationToken cancellationToken)
    {
        // The transaction has already copied this payload into Program Files. Lock
        // the exact verified bytes through child exit; never execute an uninstall
        // registry path or a per-user downloaded copy.
        string parent = Path.GetDirectoryName(installer)!;
        if (!SetupPaths.IsExactTransactionSiblingDirectory(parent, paths.ProgramFilesRoot))
        { throw new InvalidDataException("PawnIO installer must run from the protected staged product payload."); }
        EnsureNoReparsePoints(installer);
        using FileStream executable = File.Open(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(executable)), InstallerSha256, StringComparison.Ordinal))
        { throw new InvalidDataException("The bundled PawnIO installer is not the pinned unmodified 2.2.0 release."); }
        _ = WindowsDriverApiTrust.VerifyAuthenticodeFile(installer, [InstallerPublisher], cancellationToken);
        EnsureNoReparsePoints(paths.MachineDataRoot);
        await new WindowsMachineDataDirectorySecurity().ProtectAsync(JournalDirectory, cancellationToken);
        EnsureNoReparsePoints(JournalDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        PawnIoInstallJournal intent = new(2, InstallerSha256, "SubmissionIntent",
            Environment.TickCount64, installer, null, null, null);
        using (FileStream journal = new(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(journal, intent);
            journal.Flush(flushToDisk: true);
        }
        ProcessStartInfo plan = CreateProcessPlan(installer);
        // Once submission may have happened, cancellation/timeout never retries or
        // kills the vendor installer. The durable marker blocks a second submission.
        Process process;
        try
        {
            process = Process.Start(plan)
                ?? throw new InvalidOperationException("PawnIO installer process creation returned no process; submission is unresolved.");
        }
        catch (Win32Exception error) when (PawnIoJournalRecovery.IsKnownNoProcessCreated(error.NativeErrorCode))
        {
            RecordJournal(intent with { Stage = "NoProcessCreated", ExitCode = error.NativeErrorCode });
            throw;
        }
        using (process)
        {
            PawnIoInstallJournal running = intent with
            {
                Stage = "ProcessStarted",
                ProcessId = process.Id,
                ProcessCreatedAt = process.StartTime.ToUniversalTime()
            };
            RecordJournal(running);
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException error)
            { throw new InvalidOperationException("PawnIO installer exit was not observed within five minutes. Submission remains unresolved; no retry was attempted.", error); }
            int exitCode = process.ExitCode;
            RecordJournal(running with { Stage = "ExitObserved", ExitCode = exitCode });
            if (exitCode == 0)
            {
                // Independent readback is mandatory except when the official installer
                // explicitly reports reboot required. Keep the marker on any ambiguity.
                PawnIoInstallation readback = PawnIoRuntimeAdmission.Inspect(CancellationToken.None)
                    ?? throw new InvalidOperationException("PawnIO successful exit had no independently bound driver.");
                File.Delete(JournalPath);
                return new(exitCode, readback);
            }
            // Keep observed failure/reboot evidence. A later user-initiated setup can
            // reconcile it read-only, and must never resubmit an uncertain install.
            return new(exitCode, null);
        }
    }

    private void RecordJournal(PawnIoInstallJournal state)
    {
        string temporary = Path.Combine(JournalDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream journal = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(journal, state);
                journal.Flush(flushToDisk: true);
            }
            File.Replace(temporary, JournalPath, null);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    internal static ProcessStartInfo CreateProcessPlan(string installer)
    {
        ProcessStartInfo plan = new(installer)
        {
            WorkingDirectory = Path.GetDirectoryName(installer)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        plan.ArgumentList.Add("-install");
        plan.ArgumentList.Add("-silent");
        foreach (string key in plan.Environment.Keys.Where(key =>
            key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)).ToArray())
        { plan.Environment.Remove(key); }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        plan.Environment["PATH"] = Path.Combine(windows, "System32") + ";" + windows;
        return plan;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidDataException("PawnIO setup paths must not contain reparse points."); }
        }
    }
}
