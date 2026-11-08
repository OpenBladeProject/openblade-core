using System.ComponentModel;

namespace OpenBlade.Setup;

public interface ISetupGuardianQuiescenceGuard
{
    void EnsureClear();
}

// Observes only. A retained owner or unresolved reservation must be reconciled
// by its owner before Setup may replace its payload or remove its evidence.
internal sealed class SetupGuardianQuiescenceGuard(SetupPaths paths, ISetupProcessOperations processes,
    Func<string, FileAttributes> attributes) : ISetupGuardianQuiescenceGuard
{
    internal const string ProcessName = "OpenBlade.FanStop.Guardian";
    private const string ExecutableName = ProcessName + ".exe";

    public void EnsureClear()
    {
        try
        {
            string directory = Path.Combine(paths.MachineDataDirectory, "FanStopGuardian");
            foreach (string parent in new[] { paths.MachineDataRoot, paths.MachineDataDirectory, directory })
            {
                FileAttributes? state = ReadIfPresent(parent);
                if (state is null) { break; }
                if ((state.Value & FileAttributes.ReparsePoint) != 0 || (state.Value & FileAttributes.Directory) == 0)
                {
                    throw new InvalidOperationException("Fan-stop guardian evidence has an unexpected directory identity.");
                }
            }
            if (ReadIfPresent(Path.Combine(directory, "pending.json")) is not null)
            {
                throw new InvalidOperationException("Fan-stop guardian ownership is pending. Preserve its process and evidence before continuing Setup.");
            }

            IReadOnlyList<ISetupProcess> candidates = processes.GetProcessesByName(ProcessName);
            try
            {
                foreach (ISetupProcess candidate in candidates)
                {
                    string? executable = candidate.ExecutablePath;
                    if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
                    {
                        throw new InvalidOperationException("A fan-stop guardian process could not be identified.");
                    }
                    string full = Path.GetFullPath(executable);
                    if (!string.Equals(full, executable, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(@"\\", StringComparison.Ordinal) ||
                        !string.Equals(Path.GetFileName(full), ExecutableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("A fan-stop guardian process has an unexpected executable identity.");
                    }
                    string parent = Path.GetDirectoryName(full)!;
                    if (string.Equals(parent, paths.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(paths.InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        SetupPaths.IsExactTransactionSiblingDirectory(parent, paths.ProgramFilesRoot))
                    {
                        throw new InvalidOperationException("The installed fan-stop guardian is still running. Setup will preserve its payload and evidence.");
                    }
                }
            }
            finally
            {
                foreach (ISetupProcess candidate in candidates) { candidate.Dispose(); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            throw new InvalidOperationException("Setup could not establish that the fan-stop guardian is quiescent. Preserve its process and evidence.", error);
        }
    }

    private FileAttributes? ReadIfPresent(string path)
    {
        try { return attributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
