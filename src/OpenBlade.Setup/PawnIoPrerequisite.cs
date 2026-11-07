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
    PawnIoInstallation? Inspect(CancellationToken cancellationToken);
    Task<int> InstallAsync(string installer, CancellationToken cancellationToken);
}

/// <summary>The shared full-install route, including broker installs. Never removes shared PawnIO.</summary>
internal sealed class PawnIoPrerequisite(IPawnIoSetupOperations operations) : IPawnIoPrerequisite
{
    public async Task<bool> EnsureAsync(string stagedPayloadDirectory, CancellationToken cancellationToken)
    {
        if (operations.Inspect(cancellationToken) is { } installed)
        {
            Trace.TraceInformation("PawnIO prerequisite: reused trusted installed driver; running={0}.", installed.Started);
            // Never reinstall a stopped shared driver or declare it sensor-ready.
            // PnP may finish starting the existing signed package after restart.
            return !installed.Started;
        }
        int exitCode = await operations.InstallAsync(Path.Combine(stagedPayloadDirectory, "PawnIO_setup.exe"), cancellationToken);
        Trace.TraceInformation("PawnIO prerequisite: official installer exit code {0}.", exitCode);
        if (exitCode == 3010)
        {
            // Installer success is not sensor readiness. Runtime independently admits the
            // actual signed, started PnP binding before opening LibreHardwareMonitor.
            return true;
        }
        if (exitCode != 0)
        { throw new InvalidOperationException($"PawnIO installation failed (exit code {exitCode}); no alternate installation was attempted."); }
        if (operations.Inspect(cancellationToken) is not { } readback)
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

    public PawnIoInstallation? Inspect(CancellationToken cancellationToken)
    {
        if (File.Exists(JournalPath))
        { throw new InvalidOperationException("A previous PawnIO installation has an unresolved durable intent. Preserve its setup diagnostics and reconcile the installed state before retrying."); }
        return PawnIoRuntimeAdmission.Inspect(cancellationToken);
    }

    public async Task<int> InstallAsync(string installer, CancellationToken cancellationToken)
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
        using (FileStream journal = new(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(journal, new
            {
                SchemaVersion = 1,
                Component = "PawnIO",
                Version = "2.2.0",
                Sha256 = InstallerSha256,
                Stage = "SubmissionIntent",
                StartedAt = DateTimeOffset.UtcNow
            });
            journal.Flush(flushToDisk: true);
        }
        ProcessStartInfo plan = CreateProcessPlan(installer);
        // Once submission may have happened, cancellation/timeout never retries or
        // kills the vendor installer. The durable marker blocks a second submission.
        using Process process = Process.Start(plan)
            ?? throw new InvalidOperationException("PawnIO installer process creation returned no process; submission is unresolved.");
        RecordObservedProcess(process.Id, null);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException error)
        { throw new InvalidOperationException("PawnIO installer exit was not observed within five minutes. Submission remains unresolved; no retry was attempted.", error); }
        int exitCode = process.ExitCode;
        RecordObservedProcess(process.Id, exitCode);
        if (exitCode is 0 or 3010)
        {
            // Independent readback is mandatory except when the official installer
            // explicitly reports reboot required. Keep the marker on any ambiguity.
            if (exitCode == 0 && PawnIoRuntimeAdmission.Inspect(CancellationToken.None) is null)
            { throw new InvalidOperationException("PawnIO successful exit had no independently bound driver."); }
            File.Delete(JournalPath);
        }
        return exitCode;
    }

    private void RecordObservedProcess(int processId, int? exitCode)
    {
        string temporary = Path.Combine(JournalDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream journal = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(journal, new
                {
                    SchemaVersion = 1,
                    Component = "PawnIO",
                    Version = "2.2.0",
                    Sha256 = InstallerSha256,
                    Stage = exitCode.HasValue ? "ExitObserved" : "ProcessStarted",
                    ProcessId = processId,
                    ExitCode = exitCode,
                    ObservedAt = DateTimeOffset.UtcNow
                });
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
