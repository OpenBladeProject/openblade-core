using System.Diagnostics;

namespace OpenBlade.Tray;

internal static class SessionAgentLauncher
{
    internal static bool TryStartSibling()
    {
        string executable = Path.Combine(
            AppContext.BaseDirectory,
            "OpenBlade.SessionAgent.exe");
        if (!File.Exists(executable))
        {
            return false;
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return process is not null;
    }
}
