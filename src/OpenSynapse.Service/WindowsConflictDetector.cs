using System.Diagnostics;

namespace OpenSynapse.Service;

public interface IRunningProcessSource
{
    IReadOnlyList<string> GetProcessNames();
}

public sealed class RunningProcessSource : IRunningProcessSource
{
    public IReadOnlyList<string> GetProcessNames()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            return processes.Select(process => process.ProcessName).ToArray();
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}

public sealed class WindowsConflictDetector(IRunningProcessSource processSource) : IConflictDetector
{
    private static readonly HashSet<string> KnownControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "RazerAppEngine",
        "Razer Synapse Service",
        "Razer Chroma SDK Service",
        "Razer Chroma SDK Server",
        "razerwdl",
    };

    public IReadOnlyList<string> GetRunningConflicts() => processSource
        .GetProcessNames()
        .Where(KnownControllers.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
